using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [AF0] La sonda che confronta il numero di corsie del guscio con quello del motore remoto.
///
/// La distinzione che questi test difendono: <b>disallineamento</b> (entrambi i numeri noti e
/// diversi → allarme Critical) e <b>ignoranza</b> (motore muto o valore illeggibile → nessun
/// allarme) sono esiti diversi. Un allarme costruito sull'ignoranza sarebbe l'ennesimo controllo
/// che grida a prescindere — la classe di difetto opposta ma gemella di quello che rassicura a
/// prescindere.
///
/// Collezione TradingLanes: si configura lo static di processo, come in TradingLanesCountTests.
/// </summary>
[Collection("TradingLanes")]
public sealed class LaneCountCoherenceProbeTests : IDisposable
{
    public LaneCountCoherenceProbeTests() => TradingLanes.ResetForTests();

    public void Dispose() => TradingLanes.ResetForTests();

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationSeverity Severity, string Title, string Body)> Sent { get; } = new();

        public Task NotifyAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title, body));
            return Task.CompletedTask;
        }
    }

    private static LaneCountCoherenceProbe Probe(FakeEngineConfigStore store, RecordingNotifier? notifier = null)
        => new(store, NullLogger<LaneCountCoherenceProbe>.Instance, notifier);

    [Fact]
    public async Task CoherentFleet_NoAlarm()
    {
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:LaneCount", "8"); // la forma reale: scalare serializzato come stringa JSON
        var notifier = new RecordingNotifier();

        var result = await Probe(store, notifier).ProbeAsync();

        Assert.Equal(8, result.EngineLaneCount);
        Assert.False(result.Mismatch);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task MismatchedFleet_OneCriticalNotification()
    {
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:LaneCount", "3"); // il motore è rimasto alla flotta vecchia
        var notifier = new RecordingNotifier();

        var probe = Probe(store, notifier);
        var result = await probe.ProbeAsync();

        Assert.True(result.Mismatch);
        Assert.Equal(3, result.EngineLaneCount);
        Assert.Same(result, probe.Result); // stato esposto, stesso patto di MasterKeyProbe
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Critical, sent.Severity);
        Assert.Contains("disallineate", sent.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trading-config.env", sent.Body); // la notifica dice DOVE si sistema
    }

    [Fact]
    public async Task UnreachableEngine_IsIgnorance_NotMismatch()
    {
        // A freddo il port-forward chiuso è lo stato normale: la sonda non deve trasformarlo in un
        // secondo allarme (l'irraggiungibilità ha già la sua superficie nei pannelli).
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true, reachable: false);
        var notifier = new RecordingNotifier();

        var result = await Probe(store, notifier).ProbeAsync();

        Assert.Null(result.EngineLaneCount);
        Assert.False(result.Mismatch);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task KeyAbsentFromEngineConfig_MeansTheCodeDefault_AndThatIsARealMismatch()
    {
        // Chiave assente dal file del motore = il motore gira col default del codice (3). Con un
        // guscio a 8 non è ignoranza: è un disallineamento vero, e va detto.
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed<string?>("Trading:LaneCount", null); // il motore risponde "null" (chiave assente)
        var notifier = new RecordingNotifier();

        var result = await Probe(store, notifier).ProbeAsync();

        Assert.Equal(TradingLanes.DefaultCount, result.EngineLaneCount);
        Assert.True(result.Mismatch);
        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task UnparseableValue_NoFalseAlarm()
    {
        // Un valore illeggibile non diventa un default su cui costruire un allarme: si dichiara
        // l'ignoranza (warning nel log) e basta.
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:LaneCount", "otto");
        var notifier = new RecordingNotifier();

        var result = await Probe(store, notifier).ProbeAsync();

        Assert.Null(result.EngineLaneCount);
        Assert.False(result.Mismatch);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task NumericJsonForm_AlsoParses()
    {
        // Difesa in profondità: se un domani lo scalare arrivasse come numero JSON invece che come
        // stringa, il confronto deve restare giusto.
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:LaneCount", 8);

        var result = await Probe(store).ProbeAsync();

        Assert.Equal(8, result.EngineLaneCount);
        Assert.False(result.Mismatch);
    }

    [Fact]
    public async Task MissingNotifier_DoesNotThrowOnMismatch()
    {
        // Come per il watchdog: una corsia deve poter girare in un host senza canale di notifica.
        TradingLanes.Configure(8);
        var store = new FakeEngineConfigStore(remote: true);
        store.Seed("Trading:LaneCount", "3");

        var result = await Probe(store, notifier: null).ProbeAsync();

        Assert.True(result.Mismatch);
    }
}
