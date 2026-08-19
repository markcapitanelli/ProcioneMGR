using ProcioneMGR.Services.Monitoring.Drift;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I6] La fotografia della deriva delle feature che alimenta la Home.
///
/// Il buco che chiude: il monitor persisteva tutto e mostrava tutto in <c>/admin/autonomy</c>, cioè
/// rispondeva <b>solo a chi andava a cercarlo</b> — mentre il senso di un monitor di deriva è
/// accorgersene senza doverci pensare. È la stessa lacuna che D2.a chiuse per la deriva dei fattori,
/// e questa classe ne è deliberatamente il gemello.
///
/// <para>La proprietà che questi test difendono è quella che rende onesto un «nessun allarme»: la
/// fotografia deve sapere <b>quanti modelli sono stati davvero guardati</b>. «0 allarmi su 53» si
/// legge come un via libera anche quando 50 di quei 53 sono stati saltati.</para>
/// </summary>
public class FeatureDriftSnapshotTests
{
    private static readonly DateTime At = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static FeatureDriftModelSnapshot Verdict(string name, DriftSeverity overall, int alerts = 0, int drifting = 0) =>
        new(1, name, "BTC/USDT", "1h", overall, drifting, 12, alerts, null, At);

    private static FeatureDriftModelSnapshot Skipped(string name, string reason) =>
        new(2, name, "ETH/USDT", "4h", DriftSeverity.None, 0, 0, 0, reason, At);

    [Fact]
    public void FotografiaVuota_NonDichiaraNulla()
    {
        var s = new FeatureDriftSnapshot();

        Assert.Null(s.LastRunUtc);
        Assert.Empty(s.All);
        Assert.Empty(s.Alerts);
        Assert.Equal(0, s.ModelsSkipped);
        Assert.Equal(0, s.ModelsWithVerdict);
        Assert.False(s.FromStoredHistory);
    }

    /// <summary>
    /// <b>La proprietà che rende onesto il «nessun allarme»</b>: i saltati si contano a parte e non
    /// gonfiano i giudicati. Se questo test fallisse, la Home direbbe «tutto a posto su 4 modelli»
    /// avendone guardato uno.
    /// </summary>
    [Fact]
    public void SaltatiEGiudicati_SiContanoSeparatamente()
    {
        var s = new FeatureDriftSnapshot();
        s.Replace(
        [
            Verdict("a", DriftSeverity.None),
            Skipped("b", "candele recenti insufficienti: 10 su 200 richieste"),
            Skipped("c", "il monitor non ha prodotto alcuna feature valutabile per questo modello"),
            Skipped("d", "la finestra corrente si sovrappone al periodo di training"),
        ], At);

        Assert.Equal(4, s.All.Count);
        Assert.Equal(1, s.ModelsWithVerdict);
        Assert.Equal(3, s.ModelsSkipped);
        Assert.Empty(s.Alerts);
    }

    /// <summary>
    /// <b>Il caso-trappola.</b> Un modello SALTATO ha <c>Overall = None</c> per costruzione: se gli
    /// allarmi si filtrassero solo sulla gravità, un salto potrebbe entrare (o mascherare) un
    /// verdetto. Qui si verifica il complemento: un saltato non compare MAI fra gli allarmi,
    /// nemmeno se la sua gravità fosse valorizzata per errore.
    /// </summary>
    [Fact]
    public void UnSaltatoNonCompareMaiFraGliAllarmi()
    {
        var s = new FeatureDriftSnapshot();
        var saltatoConGravita = new FeatureDriftModelSnapshot(
            9, "patologico", "SOL/USDT", "1h", DriftSeverity.Alert, 5, 10, 5,
            "candele recenti insufficienti", At);

        s.Replace([saltatoConGravita, Verdict("vero", DriftSeverity.Alert, alerts: 2, drifting: 3)], At);

        var alert = Assert.Single(s.Alerts);
        Assert.Equal("vero", alert.ModelName);
    }

    [Fact]
    public void GliAllarmi_SonoOrdinatiPerGravitaDecrescente()
    {
        var s = new FeatureDriftSnapshot();
        s.Replace(
        [
            Verdict("pochi", DriftSeverity.Alert, alerts: 1, drifting: 2),
            Verdict("molti", DriftSeverity.Alert, alerts: 7, drifting: 9),
            Verdict("medi", DriftSeverity.Alert, alerts: 3, drifting: 4),
        ], At);

        Assert.Equal(["molti", "medi", "pochi"], s.Alerts.Select(a => a.ModelName));
    }

    /// <summary>Warning non è Alert: la Home mostra il blocco rosso solo per gli Alert.</summary>
    [Fact]
    public void UnWarningNonEUnAllarme()
    {
        var s = new FeatureDriftSnapshot();
        s.Replace([Verdict("w", DriftSeverity.Warning, alerts: 0, drifting: 3)], At);

        Assert.Empty(s.Alerts);
        Assert.Equal(1, s.ModelsWithVerdict);
    }

    /// <summary>
    /// La provenienza della fotografia si dichiara: mostrare un valore ricostruito dalla storia come
    /// se fosse appena calcolato è la regola 5 della piattaforma («degradare dicendolo»).
    /// </summary>
    [Fact]
    public void LaProvenienzaDellaFotografiaSiDichiara()
    {
        var s = new FeatureDriftSnapshot();

        s.Replace([Verdict("a", DriftSeverity.None)], At, fromStoredHistory: true);
        Assert.True(s.FromStoredHistory);

        s.Replace([Verdict("a", DriftSeverity.None)], At.AddHours(6));
        Assert.False(s.FromStoredHistory); // un tick vero sostituisce la ricostruzione
        Assert.Equal(At.AddHours(6), s.LastRunUtc);
    }

    /// <summary>
    /// La proiezione riga → fotografia è UNA sola, condivisa da tick e idratazione: due proiezioni
    /// potrebbero divergere e dare due fotografie diverse sugli stessi dati — il difetto già pagato
    /// in D2 e con <c>SeriesFreshness</c>.
    /// </summary>
    [Fact]
    public void LaProiezioneConservaIlMotivoDelSalto()
    {
        var riga = new DriftCheckResult
        {
            ModelId = 7, ModelName = "m", Symbol = "ADA/USDT", Timeframe = "4h",
            Overall = DriftSeverity.None, TotalFeatures = 0, DriftingFeatures = 0, AlertFeatures = 0,
            CheckedAtUtc = At, SkipReason = "candele recenti insufficienti: 3 su 200 richieste",
        };

        var proiettata = FeatureDriftSnapshot.FromRow(riga);

        Assert.False(proiettata.IsVerdict);
        Assert.Equal(riga.SkipReason, proiettata.SkipReason);
        Assert.Equal(7, proiettata.ModelId);
        Assert.Equal("ADA/USDT", proiettata.Symbol);
    }
}
