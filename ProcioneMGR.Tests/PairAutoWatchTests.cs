using ProcioneMGR.Services.PairsTrading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-25, decisione del proprietario] L'alimentazione AUTOMATICA della sorveglianza spread.
/// Il criterio è la REPLICAZIONE: una coppia entra solo se operabile in almeno K screen DISTINTI
/// che coprono un arco minimo — non il top-N su un test singolo, che al 5% su ~190 coppie fabbrica
/// una decina di falsi per costruzione (la trappola che il commento su Pairs dichiara da sempre).
/// Questi test pinnano: la replica richiesta (screen E arco), la meraviglia-di-un-run esclusa, le
/// manuali mai sfrattate dal tetto, il taglio dal tetto DICHIARATO, l'ordinamento per forza.
/// </summary>
public class PairAutoWatchTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PairSpreadWatchWorker.AutoWatchRow Row(string pair, int day, double adf = -4.0) =>
        new(pair, Guid.NewGuid(), T0.AddDays(day), adf);

    [Fact]
    public void ReplicaSuScreenDistintiEArco_Qualifica()
    {
        var rows = new[] { Row("A|B 1h", 0), Row("A|B 1h", 10), Row("A|B 1h", 20) };
        var (selected, cut) = PairSpreadWatchWorker.SelectAutoPairs(rows, [], minScreens: 3, minSpanDays: 14, maxTotal: 5);
        Assert.Equal(["A|B 1h"], selected);
        Assert.Equal(0, cut);
    }

    [Fact]
    public void MeravigliaDiUnSoloRun_Esclusa()
    {
        // Un solo screen, anche con ADF fortissimo: è il test singolo — la fabbrica di candidati.
        var rows = new[] { Row("A|B 1h", 0, adf: -9.9) };
        var (selected, _) = PairSpreadWatchWorker.SelectAutoPairs(rows, [], 3, 14, 5);
        Assert.Empty(selected);
    }

    [Fact]
    public void ReplicheVicineNelTempo_NonQualificano()
    {
        // Tre screen in tre giorni: la replica a distanza di ore non è indipendenza.
        var rows = new[] { Row("A|B 1h", 0), Row("A|B 1h", 1), Row("A|B 1h", 3) };
        var (selected, _) = PairSpreadWatchWorker.SelectAutoPairs(rows, [], 3, 14, 5);
        Assert.Empty(selected);
    }

    [Fact]
    public void LeManuali_NonSiSfrattanoMai_EIlTettoValeSulTotale()
    {
        var rows = new[]
        {
            Row("A|B 1h", 0), Row("A|B 1h", 20),
            Row("C|D 4h", 0), Row("C|D 4h", 20),
        };
        // Tetto 2 con 2 manuali: zero slot per le auto, anche se qualificano.
        var (selected, cut) = PairSpreadWatchWorker.SelectAutoPairs(
            rows, manual: ["X|Y 1h", "Z|W 1h"], minScreens: 2, minSpanDays: 14, maxTotal: 2);
        Assert.Empty(selected);
        Assert.Equal(2, cut); // e il taglio si DICHIARA: non «non c'era altro», ma «non c'era posto»
    }

    [Fact]
    public void CoppiaGiaManuale_NonSiDuplica()
    {
        var rows = new[] { Row("A|B 1h", 0), Row("A|B 1h", 20) };
        var (selected, _) = PairSpreadWatchWorker.SelectAutoPairs(rows, ["A|B 1h"], 2, 14, 5);
        Assert.Empty(selected);
    }

    [Fact]
    public void Ordinamento_PerForzaDellUltimoScreen()
    {
        var rows = new[]
        {
            Row("DEBOLE|X 1h", 0, -3.6), Row("DEBOLE|X 1h", 20, -3.6),
            Row("FORTE|X 1h", 0, -5.5), Row("FORTE|X 1h", 20, -5.5),
        };
        var (selected, cut) = PairSpreadWatchWorker.SelectAutoPairs(rows, [], 2, 14, maxTotal: 1);
        Assert.Equal(["FORTE|X 1h"], selected);
        Assert.Equal(1, cut);
    }

    [Fact]
    public void ContanoIRunDistinti_NonLeRighe()
    {
        // Lo stesso run con più righe della stessa coppia (finestre diverse) = UN solo screen:
        // contare le righe sarebbe l'artefatto 19× già pagato due volte sull'archivio candidati.
        var runId = Guid.NewGuid();
        var rows = new[]
        {
            new PairSpreadWatchWorker.AutoWatchRow("A|B 1h", runId, T0, -4.0),
            new PairSpreadWatchWorker.AutoWatchRow("A|B 1h", runId, T0.AddDays(20), -4.0),
        };
        var (selected, _) = PairSpreadWatchWorker.SelectAutoPairs(rows, [], minScreens: 2, minSpanDays: 14, maxTotal: 5);
        Assert.Empty(selected);
    }
}
