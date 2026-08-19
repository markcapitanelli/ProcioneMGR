using System.Diagnostics.Metrics;
using ProcioneMGR.Services.Observability;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I5] Scoperta dinamica degli istogrammi in <see cref="MetricsCollector"/>.
///
/// Il difetto che copre: la raccolta era filtrata da una lista di <b>tre nomi scritti a mano</b>.
/// Un istogramma nuovo veniva scartato in silenzio e la sua riga non compariva mai in
/// <c>/metrics</c> — chi aggiungeva una misura non aveva modo di accorgersene, e la pagina taceva
/// su ciò che non conosceva. Conta per questa ondata perché più item introducono misure nuove
/// (per esempio il costo di un tick del monitor di deriva): un gate nella forma «il numero compare
/// in <c>/metrics</c>» sarebbe stato insoddisfacibile per costruzione.
/// </summary>
public class MetricsCollectorDiscoveryTests
{
    /// <summary>
    /// Il collettore ascolta per NOME di meter, quindi il test deve emettere sullo stesso meter di
    /// piattaforma — altrimenti proverebbe qualcosa di diverso da ciò che succede in produzione.
    /// </summary>
    private static Meter PlatformMeter() => new(ProcioneMetrics.MeterName);

    /// <summary>Nome unico per test: due test che riusassero lo stesso strumento si vedrebbero a vicenda.</summary>
    private static string UniqueName(string suffix) => $"procione.test.{suffix}_{Guid.NewGuid():N}";

    /// <summary>
    /// <b>La proprietà che l'item chiede</b>: un istogramma mai censito da nessuna parte compare nel
    /// riassunto senza modificare alcuna lista. Prima della modifica questo test fallisce.
    /// </summary>
    [Fact]
    public async Task IstogrammaMaiCensito_CompareNelRiassuntoSenzaToccareAlcunaLista()
    {
        var name = UniqueName("hist");
        using var collector = new MetricsCollector();
        await collector.StartAsync(CancellationToken.None);

        using var meter = PlatformMeter();
        var hist = meter.CreateHistogram<double>(name, unit: "ms");
        hist.Record(10);
        hist.Record(30);

        var snap = collector.Snapshot();

        Assert.True(snap.Histograms.ContainsKey(name), $"l'istogramma '{name}' non è stato raccolto");
        var summary = snap.Histograms[name];
        Assert.Equal(2, summary.Count);
    }

    /// <summary>
    /// I tre istogrammi storici restano presenti <b>anche a zero misure</b>: è la differenza fra
    /// «non è ancora successo niente» e «questa misura non esiste», e collassarle farebbe leggere
    /// una riga assente come «nessun problema».
    /// </summary>
    [Fact]
    public async Task GliIstogrammiAttesi_CiSonoAncheSenzaAlcunaMisura()
    {
        using var collector = new MetricsCollector();
        await collector.StartAsync(CancellationToken.None);

        var snap = collector.Snapshot();

        Assert.Contains(MetricsCollector.ExecutionSlippageInstrument, snap.Histograms.Keys);
        Assert.Contains(MetricsCollector.TradingSlippageInstrument, snap.Histograms.Keys);
        Assert.Contains(MetricsCollector.OrderLatencyInstrument, snap.Histograms.Keys);
        Assert.All(
            new[] { MetricsCollector.ExecutionSlippageInstrument, MetricsCollector.TradingSlippageInstrument, MetricsCollector.OrderLatencyInstrument },
            n => Assert.Equal(0, snap.Histograms[n].Count));
    }

    /// <summary>
    /// <b>Il controllo sul rumore</b>: la scoperta dinamica non deve raccogliere strumenti di ALTRI
    /// meter. Il filtro per meter è l'unica cosa che impedisce a <c>/metrics</c> di riempirsi delle
    /// misure di qualunque libreria del processo.
    /// </summary>
    [Fact]
    public async Task IstogrammaDiUnAltroMeter_NonVieneRaccolto()
    {
        var name = UniqueName("estraneo");
        using var collector = new MetricsCollector();
        await collector.StartAsync(CancellationToken.None);

        using var foreign = new Meter("Qualcun.Altro");
        foreign.CreateHistogram<double>(name).Record(42);

        Assert.DoesNotContain(name, collector.Snapshot().Histograms.Keys);
    }

    /// <summary>
    /// Uno strumento double che NON è un istogramma finisce fra i contatori invece di essere
    /// scartato: oggi il caso è vuoto (tutti i contatori di piattaforma sono <c>long</c>), ma
    /// scartarlo in silenzio sarebbe la stessa perdita in un altro punto.
    /// </summary>
    [Fact]
    public async Task ContatoreDouble_FinisceFraIContatoriNonScartato()
    {
        var name = UniqueName("contatore");
        using var collector = new MetricsCollector();
        await collector.StartAsync(CancellationToken.None);

        using var meter = PlatformMeter();
        meter.CreateCounter<double>(name).Add(5);

        var snap = collector.Snapshot();
        Assert.Contains(snap.Counters.Keys, k => k.StartsWith(name, StringComparison.Ordinal));
        Assert.DoesNotContain(name, snap.Histograms.Keys);
    }
}
