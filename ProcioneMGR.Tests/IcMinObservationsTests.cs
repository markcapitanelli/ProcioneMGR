using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.ML;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I9] Pavimento di numerosità nella selezione per Information Coefficient.
///
/// Il caso che copre: un fattore <b>null sulla stragrande maggioranza delle barre</b> — il sentiment
/// su un simbolo fuori dal vocabolario dei ticker, o una feature con warm-up lunghissimo — può avere
/// un |IC| altissimo calcolato su una manciata di punti e <b>vincere l'ordinamento</b> contro fattori
/// misurati su migliaia. L'IC non è confrontabile fra numerosità diverse: ordinare per |IC| senza
/// guardare <c>Observations</c> premia il rumore proprio dove è più facile scambiarlo per segnale.
///
/// <para><c>Observations</c> era già sul risultato della valutazione, popolato, e non lo guardava
/// nessuno.</para>
/// </summary>
public class IcMinObservationsTests
{
    /// <summary>Fattore che restituisce un valore SOLO ogni <paramref name="everyN"/> barre: altrove null.</summary>
    private sealed class SparseFactor(string name, int everyN, bool perfectlyCorrelated) : IAlphaFactor
    {
        public string Name => name;
        public string DisplayName => name;
        public FactorCategory Category => FactorCategory.Momentum;
        public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions => [];

        public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal>? parameters = null)
        {
            var result = new decimal?[candles.Count];
            for (var i = 0; i < candles.Count; i++)
            {
                if (i % everyN != 0) continue;
                // Correlato col rendimento successivo (segnale «perfetto» sui pochi punti che vede),
                // oppure costante (nessun segnale).
                result[i] = perfectlyCorrelated && i + 1 < candles.Count
                    ? candles[i + 1].Close - candles[i].Close
                    : 1m;
            }
            return result;
        }
    }

    private static List<OhlcvData> Candles(int n = 1000)
    {
        var rnd = new Random(20260819);
        var list = new List<OhlcvData>(n);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 100m;
        for (var i = 0; i < n; i++)
        {
            price += (decimal)((rnd.NextDouble() - 0.5) * 2);
            list.Add(new OhlcvData
            {
                Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                Open = price, High = price, Low = price, Close = price, Volume = 1,
            });
        }
        return list;
    }

    private static FactorSpec Spec(IAlphaFactor f) => new(f.Name, f, new Dictionary<string, decimal>());

    /// <summary>
    /// <b>La proprietà di livello 2</b>: con il pavimento a 0 la selezione è quella di sempre.
    /// «Configurabile» non doveva significare «cambiato».
    /// </summary>
    [Fact]
    public void PavimentoAZero_SelezioneInvariata()
    {
        var candles = Candles();
        var candidati = new[] { Spec(new SparseFactor("raro", 200, perfectlyCorrelated: true)), Spec(new SparseFactor("denso", 1, false)) };
        var selector = new IcFeatureSelector();

        var senza = selector.Select(candidati, candles, new IcFeatureSelectionConfig { TopN = 10 });
        var conZero = selector.Select(candidati, candles, new IcFeatureSelectionConfig { TopN = 10, MinObservations = 0 });

        Assert.Equal(senza.Select(s => s.FeatureName), conZero.Select(s => s.FeatureName));
    }

    /// <summary>
    /// <b>Il caso che il pavimento esiste per escludere</b>: un fattore visto su pochissime barre
    /// entra nella selezione col pavimento spento, e ne esce quando il pavimento supera le sue
    /// osservazioni. Se questo non cambiasse nulla, la manopola sarebbe inerte.
    /// </summary>
    [Fact]
    public void FattoreQuasiSempreNullo_EsceQuandoIlPavimentoSale()
    {
        var candles = Candles();
        var raro = Spec(new SparseFactor("raro", 200, perfectlyCorrelated: true)); // ~5 osservazioni su 1000
        var selector = new IcFeatureSelector();

        var osservazioni = selector.Rank([raro], candles, new IcFeatureSelectionConfig())
            .Single().Evaluation.Observations;
        Assert.True(osservazioni < 50, $"Il caso di prova non è sparso abbastanza: {osservazioni} osservazioni.");

        var senzaPavimento = selector.Select([raro], candles, new IcFeatureSelectionConfig { TopN = 10 });
        var conPavimento = selector.Select([raro], candles, new IcFeatureSelectionConfig { TopN = 10, MinObservations = 100 });

        Assert.Single(senzaPavimento);   // col pavimento spento entra
        Assert.Empty(conPavimento);      // col pavimento acceso no
    }

    /// <summary>
    /// <b>Il controllo nella direzione opposta</b>: il pavimento non deve buttare via un fattore
    /// misurato su tante barre. Un filtro che scarta tutto è inutile quanto uno che non scarta nulla.
    /// </summary>
    [Fact]
    public void FattoreDenso_SopravviveAlPavimento()
    {
        var candles = Candles();
        var denso = Spec(new SparseFactor("denso", 1, perfectlyCorrelated: true));

        var selezionati = new IcFeatureSelector().Select(
            [denso], candles, new IcFeatureSelectionConfig { TopN = 10, MinObservations = 100 });

        Assert.Single(selezionati);
    }

    /// <summary>
    /// Il pavimento non altera <c>Rank</c>, che serve alla UI per mostrare TUTTI i candidati: chi
    /// guarda la classifica deve poter vedere anche ciò che il filtro escluderebbe, altrimenti
    /// l'esclusione diventa invisibile e non si può capire perché una feature non c'è.
    /// </summary>
    [Fact]
    public void IlPavimentoNonNascondeICandidatiDallaClassifica()
    {
        var candles = Candles();
        var raro = Spec(new SparseFactor("raro", 200, perfectlyCorrelated: true));

        var classifica = new IcFeatureSelector().Rank([raro], candles, new IcFeatureSelectionConfig { MinObservations = 100 });

        Assert.Single(classifica);
    }
}
