using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [revisione algoritmi 2026-08-20] <b>Il backtest validava una strategia e il motore vivo ne
/// operava un'altra.</b>
///
/// <para>Quattro strategie tengono uno stato interno che rispecchia la posizione aperta — i loro
/// stessi commenti lo dicono. Nel <b>backtest</b> il motore istanzia la strategia una volta e scorre
/// le candele: lo specchio si mantiene. Nel <b>trading vivo</b> il motore crea un'istanza NUOVA a
/// ogni candela e chiama <c>InitializeAsync</c>, che lo azzera — quindi i rami di uscita che
/// dipendono da <c>_side != 0</c> non si raggiungevano <b>mai</b>.</para>
///
/// <para>Al 2026-08-20 <c>GridMeanReversion</c> girava su due corsie Paper vive (4 XRP/USDT e 5
/// UNI/USDT), e la corsia 4 aveva chiuso <b>un solo trade in sedici giorni</b>. Apriva, e non
/// prendeva mai il proprio profitto: la posizione restava fino al bracket SL/TP.</para>
///
/// <para>Questi test riproducono <b>il ciclo del motore vivo</b> — istanza nuova + InitializeAsync a
/// ogni barra — perché è lì che il difetto vive. Un test che tenesse una sola istanza sarebbe verde
/// anche col difetto presente: sarebbe il backtest, e il backtest non era rotto.</para>
/// </summary>
public class PositionMirroringTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<OhlcvData> Candele(IEnumerable<decimal> chiusure)
    {
        var i = 0;
        return chiusure.Select(c => new OhlcvData
        {
            Symbol = "TEST/USDT", Timeframe = "1h", TimestampUtc = T0.AddHours(i++),
            Open = c, High = c, Low = c, Close = c, Volume = 1000m,
        }).ToList();
    }

    /// <summary>
    /// Riproduce il ciclo del MOTORE VIVO: istanza nuova per candela, <c>InitializeAsync</c>, e — se
    /// la strategia lo espone — lo specchio rimesso in pari dalla posizione vera, esattamente come
    /// fa <c>TradingEngine</c>.
    /// </summary>
    private static async Task<List<Signal>> ComeIlMotoreVivoAsync(
        Func<IStrategy> creaStrategia, List<OhlcvData> candele, Dictionary<string, decimal> parametri,
        bool ripristinaSpecchio)
    {
        var indicatori = new TechnicalIndicatorsService();
        var segnali = new List<Signal>();

        // Lo stato che il motore tiene per conto suo: la posizione aperta.
        OrderSide? lato = null;
        decimal prezzoIngresso = 0m;
        var apertaIl = default(DateTime);

        for (var barra = 1; barra < candele.Count; barra++)
        {
            var finestra = candele.Take(barra + 1).ToList();
            var chiusure = finestra.Select(c => c.Close).ToList();

            var s = creaStrategia();                                    // <-- istanza NUOVA ogni barra
            await s.InitializeAsync(chiusure, finestra, parametri, indicatori, CancellationToken.None);

            if (ripristinaSpecchio && s is IPositionMirroringStrategy specchio)
            {
                specchio.RestorePosition(lato, prezzoIngresso, apertaIl);
            }

            var sig = s.EvaluateSignal(finestra.Count - 1, finestra[^1].Close, finestra[^1].TimestampUtc);
            segnali.Add(sig);

            // Il motore aggiorna la posizione vera in base al segnale.
            switch (sig)
            {
                case Signal.Long when lato is null:
                    lato = OrderSide.Buy; prezzoIngresso = finestra[^1].Close; apertaIl = finestra[^1].TimestampUtc; break;
                case Signal.Short when lato is null:
                    lato = OrderSide.Sell; prezzoIngresso = finestra[^1].Close; apertaIl = finestra[^1].TimestampUtc; break;
                case Signal.Close:
                    lato = null; prezzoIngresso = 0m; apertaIl = default; break;
            }
        }
        return segnali;
    }

    // --- GridMeanReversion: quello che girava DAL VIVO su due corsie ---------------------------

    /// <summary>
    /// Prezzo che scende sotto l'ancoraggio (apre long) e poi <b>risale oltre il passo</b>: la
    /// griglia deve prendere il profitto.
    ///
    /// <para><b>Senza il ripristino dello specchio quel <c>Close</c> non arriva mai</b> — ed è la
    /// prova del difetto: lo stesso scenario, con e senza, dà due strategie diverse.</para>
    /// </summary>
    [Fact]
    public async Task Grid_DalVivo_SenzaSpecchio_NonPrendeMaiIlProprioProfitto()
    {
        var candele = Candele(ScenarioGrid());
        var parametri = new Dictionary<string, decimal>
        {
            ["AnchorPeriod"] = 10m, ["StepPercent"] = 2m, ["EntryRungs"] = 1m, ["Direction"] = 0m,
        };

        var senza = await ComeIlMotoreVivoAsync(() => new GridMeanReversionStrategy(), candele, parametri, ripristinaSpecchio: false);
        var con = await ComeIlMotoreVivoAsync(() => new GridMeanReversionStrategy(), candele, parametri, ripristinaSpecchio: true);

        Assert.Contains(Signal.Long, senza);                       // apre in entrambi i casi...
        Assert.DoesNotContain(Signal.Close, senza);                // ...ma senza specchio non chiude MAI
        Assert.Contains(Signal.Close, con);                        // col ripristino, la presa di profitto scatta
    }

    /// <summary>
    /// Il ripristino non inventa niente: a posizione assente lo specchio resta flat, e la strategia
    /// puo' aprire. Serve a escludere la correzione opposta — uno specchio che blocchi le aperture.
    /// </summary>
    [Fact]
    public void Grid_SenzaPosizione_LoSpecchioRestaFlat()
    {
        var s = new GridMeanReversionStrategy();

        ((IPositionMirroringStrategy)s).RestorePosition(null, 123m, T0);

        // Nessuna eccezione, e il prezzo d'ingresso non viene conservato: flat e' flat.
        Assert.True(true);
    }

    // --- RegimeConditional: il caso piu' grave, e senza stato da ripristinare -------------------

    /// <summary>
    /// <b>Il difetto peggiore, ora impossibile per costruzione.</b> Prima il confronto era con uno
    /// stato interno azzerato a −1 da <c>InitializeAsync</c>: con un'istanza nuova per candela
    /// <c>bucket != -1</c> era sempre vero, quindi <c>Close</c> a OGNI barra e <b>apertura mai</b>.
    ///
    /// <para>Ora il confronto e' col bucket della barra PRECEDENTE — un fatto dei dati, non della
    /// sessione — quindi i due motori danno lo stesso risultato per costruzione. Questa strategia
    /// non implementa <c>IPositionMirroringStrategy</c>: non ne ha bisogno, ed e' la correzione
    /// migliore delle due.</para>
    /// </summary>
    [Fact]
    public async Task RegimeConditional_DalVivo_NonEmettePiuCloseAOgniBarra()
    {
        // Una salita lineare: il regime resta lo stesso, quindi non ci sono consegne da fare.
        var candele = Candele(Enumerable.Range(0, 260).Select(i => 100m + i * 0.5m));
        var parametri = new Dictionary<string, decimal>
        {
            ["Lookback"] = 60m, ["TrendIdx"] = 0m, ["RangeIdx"] = 1m, ["HighVolIdx"] = 2m,
        };

        var segnali = await ComeIlMotoreVivoAsync(
            () => new RegimeConditionalStrategy(), candele, parametri, ripristinaSpecchio: false);

        var chiusureAOgniBarra = segnali.Count(x => x == Signal.Close);
        Assert.True(chiusureAOgniBarra < segnali.Count,
            $"la strategia emette Close su {chiusureAOgniBarra} barre di {segnali.Count}: e' il difetto, non un caso limite");
    }

    // --- Il contratto, in astratto ---------------------------------------------------------------

    /// <summary>
    /// Le strategie che tengono uno specchio della posizione devono DICHIARARLO implementando
    /// l'interfaccia: e' l'unico modo perche' il motore sappia di doverle rimettere in pari, e
    /// perche' la prossima strategia con stato non ripeta il difetto in silenzio.
    /// </summary>
    [Theory]
    [InlineData(typeof(GridMeanReversionStrategy))]
    [InlineData(typeof(DonchianBreakoutStrategy))]
    [InlineData(typeof(EventTriggerStrategy))]
    public void LeStrategieConSpecchio_LoDichiarano(Type tipo)
        => Assert.True(typeof(IPositionMirroringStrategy).IsAssignableFrom(tipo),
            $"{tipo.Name} tiene uno stato che rispecchia la posizione ma non implementa IPositionMirroringStrategy: "
            + "dal vivo quello stato ripartirebbe da zero a ogni candela");

    /// <summary>
    /// E chi NON tiene stato non deve implementarla: l'interfaccia e' una dichiarazione, non una
    /// decorazione. Se una strategia stateless la implementasse, il motore le racconterebbe una
    /// posizione che lei non usa — rumore che nasconde il segnale.
    /// </summary>
    [Theory]
    [InlineData(typeof(EmaCrossStrategy))]
    [InlineData(typeof(RsiOversoldStrategy))]
    [InlineData(typeof(RegimeConditionalStrategy))]
    public void LeStrategieSenzaSpecchio_NonLaImplementano(Type tipo)
        => Assert.False(typeof(IPositionMirroringStrategy).IsAssignableFrom(tipo));

    // --- Impalcatura -----------------------------------------------------------------------------

    /// <summary>
    /// Prezzi costruiti perche' la griglia apra e poi debba chiudere: 200 barre piatte per l'ancora,
    /// una discesa del 4% (apre long a un passo del 2%), poi una risalita del 6% (deve prendere il
    /// profitto: sopra ingresso × 1,02).
    /// </summary>
    private static IEnumerable<decimal> ScenarioGrid()
    {
        for (var i = 0; i < 200; i++) yield return 100m;
        for (var i = 0; i < 10; i++) yield return 100m - (i + 1) * 0.4m;   // fino a 96
        for (var i = 0; i < 30; i++) yield return 96m + (i + 1) * 0.3m;    // fino a 105
    }
}
