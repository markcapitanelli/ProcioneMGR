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
    // --- Grazia della watchdog di staleness (2026-07-29) ---------------------------------------
    // Trovata guardando i log del motore dopo aver reso il feed accendibile a caldo: a OGNI avvio
    // partiva "Feed STALE: nessun messaggio da oltre 60s (ultimo: mai)" come LogError PIÙ una
    // notifica Warning all'operatore. Con l'interruttore nel pannello quel falso allarme sarebbe
    // arrivato su Telegram a ogni singolo toggle — e un allarme che grida sempre insegna a
    // ignorare quelli veri.

    private static FeedHealth Health(DateTime? lastMessageUtc) =>
        new(ExchangeName.Binance, IsConnected: true, lastMessageUtc, Reconnects: 0, MessagesReceived: 0, LastError: null);

    [Fact]
    public void JustConnected_WithNothingReceivedYet_IsNotAnAlarm()
    {
        var start = DateTime.UtcNow;

        Assert.False(RealtimePriceWorker.ShouldAlertStale(
            Health(null), TimeSpan.FromSeconds(60), start.AddSeconds(30), start));
    }

    [Fact]
    public void ConnectedButMuteBeyondTheGrace_IsStillAnAlarm()
    {
        // Il caso che NON va perso: connesso e mai una consegna oltre la grazia è il guasto vero
        // (è il blocco EEA/MiCA visto sulle liquidazioni). Allerta, solo più tardi invece che subito.
        var start = DateTime.UtcNow;

        Assert.True(RealtimePriceWorker.ShouldAlertStale(
            Health(null), TimeSpan.FromSeconds(60), start.AddSeconds(61), start));
    }

    [Fact]
    public void ReceivedThenWentSilent_IsAnAlarmWithNoGrace()
    {
        // Qui non c'è nessuna grazia da concedere: il canale ha DIMOSTRATO di funzionare e ha
        // smesso. È l'allarme per cui la watchdog esiste.
        var start = DateTime.UtcNow;
        var now = start.AddMinutes(10);

        Assert.True(RealtimePriceWorker.ShouldAlertStale(
            Health(now.AddSeconds(-61)), TimeSpan.FromSeconds(60), now, start));
    }

    [Fact]
    public void ReceivingNormally_IsNeverAnAlarm()
    {
        var start = DateTime.UtcNow;
        var now = start.AddMinutes(10);

        Assert.False(RealtimePriceWorker.ShouldAlertStale(
            Health(now.AddSeconds(-5)), TimeSpan.FromSeconds(60), now, start));
    }

    [Fact]
    public void SeriesAddedMidSession_GetsItsOwnGrace_ThenAlertsIfNeverDelivering()
    {
        // [G2] La grazia è PER SERIE: una serie sottoscritta a sessione viva non può essere
        // giudicata sulla sveglia della sessione — non ha ancora avuto il tempo di consegnare.
        // Ma scaduta la SUA grazia, il silenzio totale è l'allarme che C1 ha reso possibile vedere.
        var sessionStart = DateTime.UtcNow;
        var subscribedAt = sessionStart.AddMinutes(30);   // corsia avviata mezz'ora dopo
        var threshold = TimeSpan.FromSeconds(60);

        // Dentro la grazia della serie (anche se la sessione è vecchia): silenzio legittimo.
        Assert.False(RealtimePriceWorker.ShouldAlertStale(
            null, threshold, subscribedAt.AddSeconds(30), graceStartUtc: subscribedAt));

        // Oltre la grazia della serie senza aver MAI consegnato: allarme.
        Assert.True(RealtimePriceWorker.ShouldAlertStale(
            null, threshold, subscribedAt.AddSeconds(61), graceStartUtc: subscribedAt));

        // Ha consegnato e poi ha smesso: nessuna grazia, allarme oltre soglia.
        Assert.True(RealtimePriceWorker.ShouldAlertStale(
            subscribedAt.AddMinutes(1), threshold, subscribedAt.AddMinutes(3), graceStartUtc: subscribedAt));
    }

    // --- Anti-raffica sulle NOTIFICHE di staleness (2026-08-13) ---------------------------------
    // L'incidente: su STX/USDT — illiquido, dove un silenzio di un paio di minuti è il ritmo
    // normale — la coppia «non risponde»/«ripristinato» partiva su Telegram ogni 1-2 minuti, fino
    // a «+2 notifiche soppresse dal rate-limit». Il danno vero non è il fastidio: quel budget
    // (20 messaggi/ora) è lo stesso degli allarmi che contano — corsia in quarantena, posizioni
    // orfane — che sarebbero stati soppressi dal rumore.

    private static readonly DateTime T0 = new(2026, 8, 13, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnSoloCampioneOltreSoglia_NonEUnGuasto()
    {
        // Il primo controllo stale non notifica: serve persistenza. È ciò che separa
        // «illiquido a strappi» da «stream morto».
        Assert.False(RealtimePriceWorker.ShouldNotifyStale(
            streak: 1, drivesProtectiveExits: true, neverDelivered: false, lastNotifiedUtc: null, nowUtc: T0));
        Assert.False(RealtimePriceWorker.ShouldNotifyStale(
            streak: 2, drivesProtectiveExits: true, neverDelivered: false, lastNotifiedUtc: null, nowUtc: T0));
        Assert.True(RealtimePriceWorker.ShouldNotifyStale(
            streak: 3, drivesProtectiveExits: true, neverDelivered: false, lastNotifiedUtc: null, nowUtc: T0));
    }

    [Fact]
    public void InSolaOsservazione_UnIntermittenzaNonSiNotifica()
    {
        // Con le uscite guidate dalle candele (DriveProtectiveExits=false, default PER MISURA —
        // B3, 24 configurazioni su 24) un feed che tace non cambia NULLA di operativo: gli stop
        // passano dal percorso REST per tutte le serie, sempre. È il caso di STX.
        Assert.False(RealtimePriceWorker.ShouldNotifyStale(
            streak: 99, drivesProtectiveExits: false, neverDelivered: false, lastNotifiedUtc: null, nowUtc: T0));
    }

    [Fact]
    public void InSolaOsservazione_UnoStreamCheNonHaMaiConsegnato_SiNotificaComunque()
    {
        // Il caso che NON va perso, e che vale anche in osservazione: connesso e mai una consegna
        // è un guasto STRUTTURALE (è il blocco EEA/MiCA visto sulle liquidazioni), non un ritmo.
        Assert.True(RealtimePriceWorker.ShouldNotifyStale(
            streak: 3, drivesProtectiveExits: false, neverDelivered: true, lastNotifiedUtc: null, nowUtc: T0));
    }

    [Fact]
    public void UnGuastoCheDura_SiRipeteUnaVoltaAllOra_NonAOgniGiro()
    {
        // Dentro il cooldown: silenzio (il guasto è già stato detto).
        Assert.False(RealtimePriceWorker.ShouldNotifyStale(
            streak: 10, drivesProtectiveExits: true, neverDelivered: false,
            lastNotifiedUtc: T0, nowUtc: T0.AddMinutes(30)));

        // Oltre il cooldown: il guasto dura ancora ed è giusto ricordarlo.
        Assert.True(RealtimePriceWorker.ShouldNotifyStale(
            streak: 10, drivesProtectiveExits: true, neverDelivered: false,
            lastNotifiedUtc: T0, nowUtc: T0.AddMinutes(61)));
    }

    [Fact]
    public void IlRitmoDiStxConIlFeedCheGuida_ProduceAlPiuUnMessaggioAllOra()
    {
        // Simulazione del caso reale con l'assetto PEGGIORE (feed che guida le uscite, quindi la
        // staleness è azionabile e va detta): STX alterna ~90s di silenzio e un evento, per un'ora.
        // Prima: una coppia stale/ripristino ogni volta. Ora: al massimo un messaggio.
        var notifiche = 0;
        DateTime? ultimaNotifica = null;

        for (var minuto = 0; minuto < 60; minuto++)
        {
            // tre controlli stale (90s) e poi un evento che azzera la serie
            for (var streak = 1; streak <= 3; streak++)
            {
                var now = T0.AddMinutes(minuto).AddSeconds(streak * 30);
                if (RealtimePriceWorker.ShouldNotifyStale(streak, true, false, ultimaNotifica, now))
                {
                    notifiche++;
                    ultimaNotifica = now;
                }
            }
        }

        Assert.Equal(1, notifiche);
    }

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
