using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.MarketData;

namespace ProcioneMGR.Tests;

/// <summary>
/// L'interruttore del feed real-time deve essere una manopola vera (2026-07-29).
///
/// <para>Fino a questo giro <c>RealtimePriceWorker</c> leggeva <c>Enabled</c> UNA volta e usciva:
/// accendere o spegnere il feed richiedeva un riavvio del processo — cioè, col motore in cluster,
/// il riavvio del pod che sta operando su tre corsie. Una manopola che per funzionare pretende di
/// riavviare il motore non è una manopola, ed è esattamente la classe di difetto che questo audit
/// è nato per togliere.</para>
///
/// <para>La proprietà che conta, e che questi test fissano: <b>a feed spento non si apre alcuna
/// connessione</b>, e accendere/spegnere apre e chiude davvero, senza toccare il resto del
/// processo.</para>
/// </summary>
public class RealtimeFeedSwitchTests
{
    /// <summary>Monitor con valore MUTABILE: è il modo di simulare il salvataggio dal pannello.</summary>
    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>Transport che non consegna nulla: al test interessa SE ci si connette, non cosa arriva.</summary>
    private sealed class CountingTransportFactory : IWebSocketTransportFactory
    {
        public ConcurrentBag<Uri> Connections { get; } = [];

        public IWebSocketTransport Create() => new Silent(this);

        private sealed class Silent(CountingTransportFactory owner) : IWebSocketTransport
        {
            public Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                owner.Connections.Add(uri);
                return Task.CompletedTask;
            }

            public Task SendAsync(string message, CancellationToken ct) => Task.CompletedTask;

            public async Task<string?> ReceiveAsync(CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Il refresh delle sottoscrizioni interroga il DB: qui non c'è, e non deve importare —
    /// RefreshLoopAsync cattura, registra e ritenta. Se un DB assente uccidesse la sessione, il
    /// feed sarebbe fragile in un modo che il test deve scoprire, non nascondere.
    /// </summary>
    private sealed class ThrowingDbFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("niente DB nel test");
    }

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    private static (RealtimePriceWorker Worker, MutableOptionsMonitor<RealtimeFeedOptions> Options, CountingTransportFactory Transport)
        Build(bool enabled)
    {
        var options = new MutableOptionsMonitor<RealtimeFeedOptions>(new RealtimeFeedOptions
        {
            Enabled = enabled,
            SubscriptionRefreshSeconds = 1,
        });
        var transport = new CountingTransportFactory();
        var worker = new RealtimePriceWorker(
            new ServiceCollection().BuildServiceProvider(),
            new ThrowingDbFactory(),
            [new BinanceStreamMapper()],
            transport,
            options,
            NullLogger<RealtimePriceWorker>.Instance,
            switchPollInterval: Poll);
        return (worker, options, transport);
    }

    /// <summary>Attende che una condizione diventi vera, senza dormire a caso su un tempo fisso.</summary>
    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    // NB sull'osservabile scelto. Si guarda IsRunning (la SESSIONE di feed) e non il numero di
    // connessioni aperte: il feed apre un socket solo quando esistono corsie in esecuzione da cui
    // ricavare le sottoscrizioni, e quelle si leggono dal database. Legare questi test a una
    // connessione richiederebbe un Postgres, cioè trasformarli in test d'integrazione per misurare
    // una proprietà — "l'interruttore comanda il ciclo di vita" — che non ha nulla a che vedere col
    // database. La copertura del socket vero sta in WebSocketPriceFeedTests, dove le sottoscrizioni
    // vengono iniettate a mano.

    [Fact]
    public async Task Disabled_NoSessionIsEverOpened()
    {
        // "Spento" deve voler dire che non si avvia nulla, non che i dati vengono scartati dopo.
        var (worker, _, transport) = Build(enabled: false);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        Assert.False(worker.IsRunning);
        Assert.Empty(transport.Connections);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TurnedOnAtRuntime_TheFeedStarts_WithoutRestartingTheProcess()
    {
        // IL test di questo lavoro: prima il worker sarebbe rimasto morto fino al riavvio del pod.
        var (worker, options, _) = Build(enabled: false);
        await worker.StartAsync(CancellationToken.None);
        Assert.False(worker.IsRunning);

        options.CurrentValue = new RealtimeFeedOptions { Enabled = true, SubscriptionRefreshSeconds = 1 };

        Assert.True(await Eventually(() => worker.IsRunning),
            "acceso l'interruttore, il feed deve partire da solo");

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TurnedOffAtRuntime_TheSessionCloses()
    {
        var (worker, options, _) = Build(enabled: true);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(await Eventually(() => worker.IsRunning));

        options.CurrentValue = new RealtimeFeedOptions { Enabled = false };

        Assert.True(await Eventually(() => !worker.IsRunning),
            "abbassato l'interruttore, la sessione deve chiudersi");

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OffThenOnAgain_RestartsInsteadOfStayingDeadForever()
    {
        // Il ciclo completo: è il caso che il vecchio codice non poteva servire in alcun modo,
        // perché dopo il primo "spento" il worker era uscito da ExecuteAsync per sempre.
        var (worker, options, _) = Build(enabled: true);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(await Eventually(() => worker.IsRunning));

        options.CurrentValue = new RealtimeFeedOptions { Enabled = false };
        Assert.True(await Eventually(() => !worker.IsRunning));

        options.CurrentValue = new RealtimeFeedOptions { Enabled = true, SubscriptionRefreshSeconds = 1 };
        Assert.True(await Eventually(() => worker.IsRunning), "riacceso, il feed deve ripartire");

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppingTheHost_EndsTheWorker_EvenWhileTheFeedIsIdle()
    {
        // Il ciclo di sorveglianza non deve trattenere lo spegnimento dell'applicazione.
        var (worker, _, _) = Build(enabled: false);
        await worker.StartAsync(CancellationToken.None);

        var stop = worker.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stop, Task.Delay(3000)) == stop;

        Assert.True(completed, "StopAsync deve completare senza attendere il timeout dell'host");
    }
}
