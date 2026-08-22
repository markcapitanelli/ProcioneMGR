using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Difetto B, 2026-08-22] <b>Il benchmark banale: «tieni la stessa direzione e non fare niente».</b>
///
/// <para>Nessuno dei sette stadi della pipeline lo calcolava, e senza di esso <i>uno Sharpe positivo
/// su un mercato che scende non è un edge se la strategia era short</i>. Misurato il 2026-08-22: sei
/// gambe su nove fra quelle proposte non battevano il passivo nella loro stessa finestra, e dal 27
/// luglio — con quattordici simboli su quattordici in salita — hanno cambiato segno.</para>
///
/// <para>È <b>misura, non gate</b>: nessun consumatore cambia comportamento, <c>GreyZone</c> non è
/// toccata, nessun candidato viene respinto per questo. Il gate arriva quando il proprietario avrà
/// deciso convenzione di costo e materiale di calibrazione, coi numeri veri davanti.</para>
/// </summary>
public class BenchmarkPassivoTests
{
    private static readonly DateTime Da = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime A = new(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc);   // 240 ore

    private static BacktestTrade Trade(string direzione, double oraIngresso, double oraUscita) => new()
    {
        Direction = direzione,
        EntryTime = Da.AddHours(oraIngresso),
        ExitTime = Da.AddHours(oraUscita),
        EntryPrice = 100m,
    };

    // --------------------------------------------- la direzione si pesa sul TEMPO, non sui trade

    /// <summary>
    /// Il caso che rende sbagliato contare i trade: <c>RsiOversold ETC/USDT 4h</c> ha una mediana di
    /// detenzione di <b>200 ore</b>. Un long da 200 ore più venti short da un'ora è un candidato
    /// <b>long</b>, che il conteggio chiamerebbe short venti a uno.
    /// </summary>
    [Fact]
    public void DirezionePrevalente_PesataSulTEMPO_NonSulNumeroDiTrade()
    {
        var trades = new List<BacktestTrade> { Trade("Long", 0, 200) };
        for (var k = 0; k < 20; k++) trades.Add(Trade("Short", 200 + k, 201 + k));

        var (direzione, netta, _) = PassiveBenchmark.ClassifyDirection(trades, Da, A);

        Assert.Equal(DominantDirection.Long, direzione);       // <- contando i trade: Short, 20 a 1
        Assert.True(netta > 0.80m, $"esposizione netta {netta}");
    }

    [Fact]
    public void SoloShort_DaDirezioneShort_EEsposizioneNegativa()
    {
        var (direzione, netta, frazione) = PassiveBenchmark.ClassifyDirection(
            [Trade("Short", 10, 130)], Da, A);

        Assert.Equal(DominantDirection.Short, direzione);
        Assert.Equal(-1m, netta);
        Assert.Equal(0.5m, frazione);   // 120 ore su 240
    }

    /// <summary>
    /// Sotto la soglia nessun lato domina: il passivo non ha un lato ovvio, e inventarne uno
    /// sarebbe peggio che dichiarare l'ambiguità.
    /// </summary>
    [Fact]
    public void DirezioneBilanciata_E_Mixed_NonUnaSceltaArbitraria()
    {
        var (direzione, _, _) = PassiveBenchmark.ClassifyDirection(
            [Trade("Long", 0, 60), Trade("Short", 60, 120)], Da, A);

        Assert.Equal(DominantDirection.Mixed, direzione);
    }

    /// <summary>
    /// Ramo degenere: entry == exit su tutti i trade. Deve dare <c>Unknown</c> e
    /// <c>TimeInMarketFraction = null</c> — <b>non</b> il numero di trade travestito da frazione
    /// di tempo.
    /// </summary>
    [Fact]
    public void TradeIstantanei_NonProduconoUnaFrazioneDiTempoInventata()
    {
        var (direzione, netta, frazione) = PassiveBenchmark.ClassifyDirection(
            [Trade("Long", 10, 10), Trade("Short", 20, 20)], Da, A);

        Assert.Equal(DominantDirection.Unknown, direzione);
        Assert.Equal(0m, netta);
        Assert.Null(frazione);
    }

    [Fact]
    public void NessunTrade_E_Unknown()
    {
        var (direzione, _, frazione) = PassiveBenchmark.ClassifyDirection([], Da, A);

        Assert.Equal(DominantDirection.Unknown, direzione);
        Assert.Null(frazione);
    }

    /// <summary>Un trade ancora aperto a fine finestra pesa fino alla fine della finestra, non zero.</summary>
    [Fact]
    public void TradeAncoraAperto_PesaFinoAllaFineDellaFinestra()
    {
        var aperto = new BacktestTrade { Direction = "Long", EntryTime = Da.AddHours(40), ExitTime = null, EntryPrice = 100m };

        var (direzione, _, frazione) = PassiveBenchmark.ClassifyDirection([aperto], Da, A);

        Assert.Equal(DominantDirection.Long, direzione);
        Assert.NotNull(frazione);
        Assert.Equal(200m / 240m, frazione!.Value, precision: 6);
    }

    // ---------------------------------------------------- il risk-free ZERO su entrambe le gambe

    /// <summary>
    /// <b>Il difetto fatale F2.</b> <c>Statistics.SharpeRatio</c> sottrae il 2%/anno al rendimento
    /// del <b>capitale intero</b> mentre ne è investito il 10%. Il drag vale <c>rf/σ</c>, e le σ non
    /// sono paragonabili: da 1,23% a 5,26% sui grigi in archivio contro ~4,8% del passivo. Con
    /// rf = 2% il candidato prende un handicap di 0,6-1,6 Sharpe contro lo 0,4 del passivo:
    /// <b>da 0,2 a 1,2 Sharpe di differenza fabbricata</b>, più grande del margine su cui un gate
    /// deciderebbe. Fallisce se torna il risk-free di default.
    /// </summary>
    [Fact]
    public void EccessoCalcolatoARiskFreeZERO_SuEntrambeLeGambe()
    {
        // Due curve con volatilità molto diverse: è dove il drag rf/σ morde in modo asimmetrico.
        var candidato = Curva(240, i => 1000m + i * 0.3m + (i % 2 == 0 ? 0.20m : -0.20m));
        var passivo = Curva(240, i => 1000m + i * 0.3m + (i % 2 == 0 ? 2.00m : -2.00m));

        var c = PassiveBenchmark.Compare(candidato, passivo, 8760, DominantDirection.Long, 1m, 0.9m);

        var attesoCandidato = Statistics.SharpeRatio(candidato, 8760, 0m);
        var attesoPassivo = Statistics.SharpeRatio(passivo, 8760, 0m);
        Assert.Equal(attesoCandidato, c.CandidateSharpe);
        Assert.Equal(attesoPassivo, c.PassiveSharpe);
        Assert.Equal(attesoCandidato - attesoPassivo, c.ExcessSharpe);

        // E soprattutto: NON è la differenza calcolata col risk-free di default.
        var conRfDefault = Statistics.SharpeRatio(candidato, 8760) - Statistics.SharpeRatio(passivo, 8760);
        Assert.NotEqual(conRfDefault, c.ExcessSharpe);
    }

    private static List<EquityPoint> Curva(int punti, Func<int, decimal> capitale) =>
        [.. Enumerable.Range(0, punti).Select(i => new EquityPoint { Timestamp = Da.AddHours(i), Capital = capitale(i) })];

    // -------------------------------------------------------------- il passivo NON paga il funding

    /// <summary>
    /// <b>Il difetto fatale F1.</b> <c>BacktestEngine</c> applica il funding a ogni candela con
    /// posizione aperta, e lo applica <b>firmato</b>: con tasso positivo il long paga e lo short
    /// <i>incassa</i>. Il tasso è la costante 0,01%/8h di <c>PipelineCosts</c> — non un dato,
    /// <c>FundingHistory</c> non è popolata da nessuno. Il passivo sta a mercato il 100% della
    /// finestra: lasciandogli quella costante, l'asticella short si alzerebbe di ~0,21 Sharpe di
    /// reddito inventato.
    ///
    /// <para>Fallisce se qualcuno «semplifica» passando la config del candidato da
    /// <c>costs.ApplyTo</c> senza azzerare il funding.</para>
    /// </summary>
    [Fact]
    public void LaConfigDelPassivo_HaFundingZERO()
    {
        var candidato = new BacktestConfiguration
        {
            ExchangeName = "Binance",
            Symbol = "BTC/USDT",
            Timeframe = "4h",
            InitialCapital = 10_000m,
            PositionSizePercent = 10m,
            SlippagePercent = 0.05m,
            FeePercent = 0.1m,
            FundingRatePercentPer8h = 0.01m,   // quello vero del candidato
        };

        var passivo = PassiveBenchmark.BuildConfig(candidato, Da, A);

        Assert.Equal(0m, passivo.FundingRatePercentPer8h);
        // Tutto il RESTO dei costi è identico: il passivo non è un benchmark scontato, è un
        // benchmark di solo prezzo.
        Assert.Equal(candidato.FeePercent, passivo.FeePercent);
        Assert.Equal(candidato.SlippagePercent, passivo.SlippagePercent);
        Assert.Equal(candidato.PositionSizePercent, passivo.PositionSizePercent);
        Assert.Equal(Da, passivo.From);
        Assert.Equal(A, passivo.To);
    }

    // ------------------------------------------------------------------- la strategia passiva

    /// <summary>Apre una volta sola e poi tace: la chiusura la fa il motore sull'ultima candela.</summary>
    [Fact]
    public async Task IlPassivo_ApreUnaVoltaSola_EPoiTace()
    {
        var s = new PassiveHoldStrategy(isLong: true);
        await s.InitializeAsync([], [], new Dictionary<string, decimal>(), null!, default);

        Assert.Equal(Signal.Long, s.EvaluateSignal(0, 100m, Da));
        for (var i = 1; i < 10; i++)
        {
            Assert.Equal(Signal.Hold, s.EvaluateSignal(i, 100m + i, Da.AddHours(i)));
        }
    }

    [Fact]
    public async Task IlPassivoShort_ApreShort()
    {
        var s = new PassiveHoldStrategy(isLong: false);
        await s.InitializeAsync([], [], new Dictionary<string, decimal>(), null!, default);

        Assert.Equal(Signal.Short, s.EvaluateSignal(0, 100m, Da));
        Assert.Equal(Signal.Hold, s.EvaluateSignal(1, 101m, Da.AddHours(1)));
    }

    /// <summary>
    /// <b>Non deve entrare nello spazio di ricerca.</b> I <c>Prototypes</c> alimentano
    /// <c>StrategyDiscoveryEngine</c>, che senza una lista esplicita li usa tutti: registrarla
    /// gonfierebbe il conteggio tentativi e quindi il DSR di ogni candidato del run.
    /// </summary>
    [Fact]
    public void IlPassivo_NON_EntraNelloSpazioDiRicerca()
    {
        var factory = new StrategyFactory();

        Assert.DoesNotContain(factory.Prototypes, p => p.Name == "PassiveHold");
        Assert.Throws<NotSupportedException>(() => factory.Create("PassiveHold"));
    }

    // --------------------------------------------------------------------- la spiegazione in UI

    /// <summary>
    /// Un trattino muto sarebbe metà del difetto: ogni caso di «assenza» deve dire <b>perché</b>
    /// manca, e i tre motivi non sono la stessa cosa.
    /// </summary>
    [Fact]
    public void OgniAssenza_DichiaraLaPropriaRagione_ESonoDiverse()
    {
        var storica = ResearchPageService.SpiegaConfrontoPassivo(new ResearchCandidate());
        var sconosciuta = ResearchPageService.SpiegaConfrontoPassivo(new ResearchCandidate { DominantDirection = "Unknown" });
        var mista = ResearchPageService.SpiegaConfrontoPassivo(new ResearchCandidate { DominantDirection = "Mixed", NetExposure = 0.1m });

        Assert.Contains("2026-08-22", storica, StringComparison.Ordinal);
        Assert.Contains("istantanei", sconosciuta, StringComparison.Ordinal);
        Assert.Contains("mista", mista, StringComparison.Ordinal);
        Assert.Equal(3, new HashSet<string>([storica, sconosciuta, mista]).Count);
    }

    [Fact]
    public void QuandoCE_LaSpiegazione_DiceControCosaEConQualeConvenzione()
    {
        var c = new ResearchCandidate
        {
            DominantDirection = "Short",
            NetExposure = -0.9m,
            TimeInMarketFraction = 0.42m,
            PassiveHoldoutSharpe = 1.20m,
            ExcessHoldoutSharpe = -0.35m,
        };

        var testo = ResearchPageService.SpiegaConfrontoPassivo(c);

        Assert.Contains("NON batte", testo, StringComparison.Ordinal);
        Assert.Contains("risk-free ZERO", testo, StringComparison.Ordinal);
        Assert.Contains("funding", testo, StringComparison.Ordinal);
    }
}
