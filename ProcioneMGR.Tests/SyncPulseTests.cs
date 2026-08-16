using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>
/// Le due regole pure nate dall'incidente 2026-08-14 (worker di sync morto alle 22:44, pod
/// «healthy» per 6 ore): <see cref="SyncPulse"/> giudica il TIMBRO del ciclo («il sync sta
/// girando?») e <see cref="IngestionSyncHeartbeat"/> giudica il BATTITO del loop (l'health del
/// pod). Orologio passato come parametro: niente attese reali.
/// </summary>
public sealed class SyncPulseTests
{
    private static readonly DateTime Now = new(2026, 8, 15, 6, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // ------------------------------------------------------------------ SyncPulse

    [Fact]
    public void IsStalled_SenzaTimbro_EFermo()
    {
        // La trappola del null (stessa di SeriesFreshness): «mai timbrato» non può valere «sano».
        Assert.True(SyncPulse.IsStalled(null, Now, Interval));
    }

    [Fact]
    public void IsStalled_TimbroRecente_EVivo()
    {
        Assert.False(SyncPulse.IsStalled(Now.AddMinutes(-2), Now, Interval));
        // Il gap legittimo più lungo: ciclo partito un intervallo dopo il timbro precedente e
        // durato l'intero budget (2× intervallo) → 3× intervallo. Col margine, ancora vivo.
        Assert.False(SyncPulse.IsStalled(Now.AddMinutes(-16), Now, Interval));
    }

    [Fact]
    public void IsStalled_OltreLaSoglia_EFermo()
    {
        // 3×5 min + 2 min di margine = 17 min: a 18 è fermo.
        Assert.True(SyncPulse.IsStalled(Now.AddMinutes(-18), Now, Interval));
        // Il caso dell'incidente: 6 ore di silenzio.
        Assert.True(SyncPulse.IsStalled(Now.AddHours(-6), Now, Interval));
    }

    [Fact]
    public void DescribeCause_SenzaTimbro_PuntaAlWorker()
    {
        var causa = SyncPulse.DescribeCause(5, null, Now, Interval);
        Assert.Contains("mai completato", causa);
        Assert.Contains("worker", causa);
    }

    [Fact]
    public void DescribeCause_SyncFermo_IncolpaIlSyncNonISimboli()
    {
        // Il consiglio sbagliato dell'incidente: 122 serie ferme e la notifica diceva «verifica
        // BREAK sui simboli». Con il timbro stantio l'imputato va nominato: è il sync.
        var timbro = new DateTime(2026, 8, 14, 22, 44, 0, DateTimeKind.Utc); // l'ora vera dell'incidente
        var causa = SyncPulse.DescribeCause(122, timbro, Now, Interval);
        Assert.Contains("SYNC", causa);
        Assert.Contains("22:44", causa); // l'ora del timbro, per orientare la forense
        Assert.DoesNotContain("BREAK", causa);
    }

    [Fact]
    public void DescribeCause_SyncVivoPocheFerme_ConsigliaBreak()
    {
        var causa = SyncPulse.DescribeCause(1, Now.AddMinutes(-3), Now, Interval);
        Assert.Contains("BREAK", causa);
    }

    [Fact]
    public void DescribeCause_SyncVivoMolteFerme_SegnalaGuastoExchange()
    {
        var causa = SyncPulse.DescribeCause(10, Now.AddMinutes(-3), Now, Interval);
        Assert.Contains("più serie", causa);
        Assert.Contains("exchange", causa);
    }

    [Fact]
    public void DescribeCause_WorkerSpento_LoDiceInveceDiAccusareUnPodMorto()
    {
        // Spegnere il sync è una scelta di configurazione: accusare il worker manderebbe
        // l'operatore a cercare per ore un guasto che non esiste (review 2026-08-15).
        var causa = SyncPulse.DescribeCause(50, Now.AddHours(-6), Now, Interval,
            SyncPulse.ComposeOutcome("spento", Interval));
        Assert.Contains("SPENTO", causa);
        Assert.DoesNotContain("BREAK", causa);
    }

    [Fact]
    public void Timbro_IntervalloAndataERitorno()
    {
        // L'intervallo viaggia COL timbro: chi giudica (guscio) e chi scrive (pod) hanno
        // appsettings indipendenti, e una soglia sulla cadenza sbagliata giudica male.
        var outcome = SyncPulse.ComposeOutcome("ciclo ok", TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), SyncPulse.TryParseStampedInterval(outcome));
        Assert.False(SyncPulse.IsDisabledOutcome(outcome));

        // Timbro vecchio (formato senza intervallo) o assente: nessun valore inventato.
        Assert.Null(SyncPulse.TryParseStampedInterval("ciclo ok"));
        Assert.Null(SyncPulse.TryParseStampedInterval(null));

        Assert.True(SyncPulse.IsDisabledOutcome(SyncPulse.ComposeOutcome("spento", Interval)));
    }

    // ------------------------------------------------------------------ IngestionSyncHeartbeat

    [Fact]
    public void IsParked_SenzaBattito_EParcheggiato()
    {
        Assert.True(IngestionSyncHeartbeat.IsParked(null, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void IsParked_BattitoRecente_EVivo()
    {
        Assert.False(IngestionSyncHeartbeat.IsParked(Now.AddMinutes(-29), Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void IsParked_BattitoStantio_EParcheggiato()
    {
        Assert.True(IngestionSyncHeartbeat.IsParked(Now.AddMinutes(-31), Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void StaleAfter_MaiSottoI30Minuti_EScalaConLIntervallo()
    {
        // Il silenzio legittimo più lungo del loop è il backstop (4× intervallo): la soglia deve
        // stargli sopra. Col default (5 min) vince il pavimento dei 30; con intervalli larghi
        // scala a 6×.
        Assert.Equal(TimeSpan.FromMinutes(30), IngestionSyncHeartbeat.StaleAfter(TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(30), IngestionSyncHeartbeat.StaleAfter(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(90), IngestionSyncHeartbeat.StaleAfter(TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Heartbeat_BeatLoop_AggiornaLUltimoBattito()
    {
        var hb = new IngestionSyncHeartbeat();
        Assert.Null(hb.LastLoopTickUtc);

        hb.BeatLoop(Now);
        Assert.Equal(Now, hb.LastLoopTickUtc);

        hb.BeatLoop(Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(5), hb.LastLoopTickUtc);
    }
}
