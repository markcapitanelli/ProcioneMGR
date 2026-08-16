using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Worker schedulato: a intervalli regolari sincronizza tutte le serie abilitate
/// della watchlist. Gira nel processo dell'app come <see cref="BackgroundService"/>.
///
/// Configurazione (sezione "MarketData" in appsettings.json):
///  - Enabled              : true/false per accendere/spegnere il worker (default true)
///  - SyncIntervalMinutes  : intervallo tra i cicli (default 5)
///  - DefaultBackfillDays   : finestra di backfill alla prima sync di una serie (default 7)
///
/// Usa <see cref="IServiceScopeFactory"/> perche' i servizi di dominio sono scoped
/// mentre il worker e' singleton.
/// </summary>
public sealed class MarketDataSyncWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<MarketDataSyncWorker> logger,
    IngestionSyncHeartbeat? heartbeat = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Enabled è riletto a OGNI tick da IConfiguration (reloadOnChange): il toggle da
        // /admin/autonomy prende effetto a caldo, senza riavvio. L'intervallo invece è fisso
        // al primo avvio (PeriodicTimer): cambiarlo richiede riavvio.
        var interval = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("MarketData:SyncIntervalMinutes", 5)));
        _interval = interval;
        logger.LogInformation("MarketDataSyncWorker avviato, intervallo {Interval} (Enabled={Enabled}).",
            interval, configuration.GetValue("MarketData:Enabled", true));

        // Primo battito PRIMA di ogni attesa: l'health non deve leggere «mai battuto» durante
        // uno startup sano (i hosted service partono prima che Kestrel accetti le probe).
        heartbeat?.BeatLoop(DateTime.UtcNow);

        // Breve attesa iniziale per non competere con lo startup dell'app.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Budget di ciclo = 2× l'intervallo: largo per un recupero profondo legittimo (il cursore
        // è incrementale, un arretrato grosso converge attraverso più tick), stretto abbastanza da
        // non lasciare il worker parcheggiato. Vedi RunCycleAsync per l'incidente che lo motiva.
        var budget = interval * 2;

        using var timer = new PeriodicTimer(interval);
        do
        {
            // Il battito dice «il loop è vivo»: anche a Enabled=false (worker spento di proposito,
            // non un guasto) e anche se il ciclo poi viene interrotto dal budget.
            heartbeat?.BeatLoop(DateTime.UtcNow);
            try
            {
                if (configuration.GetValue("MarketData:Enabled", true))
                {
                    var completed = await RunCycleAsync(budget, stoppingToken);
                    await StampCycleAsync(completed ? "ciclo ok" : "ciclo interrotto", stoppingToken);
                }
                else
                {
                    // Anche da SPENTO il timbro si scrive: senza, la pagina e la guardia di
                    // freschezza leggerebbero il silenzio come «sync FERMO» — mentre è una scelta
                    // di configurazione (MarketData:Enabled=false), e va detta come tale.
                    await StampCycleAsync("spento", stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutdown — l'UNICA OCE che può legittimamente fermare il loop
            }
            catch (Exception ex)
            {
                // Include le OperationCanceledException che NON vengono dal token di shutdown:
                // il 2026-08-14 una TaskCanceledException di timeout di rete (Token=None,
                // sintetizzata dal rate-limit handler) è stata letta dal vecchio
                // «catch (OperationCanceledException) { break; }» come shutdown — worker morto
                // alle 22:44, pod «healthy», 122 serie ferme per 6 ore. Un errore di ciclo,
                // qualunque forma abbia, costa UN ciclo: mai il worker.
                logger.LogError(ex, "Ciclo di sincronizzazione fallito; ritento al prossimo tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("MarketDataSyncWorker fermato.");
    }

    /// <summary>
    /// Un ciclo di sync con TETTO DI TEMPO su due livelli. Il tetto esiste per misura, non per
    /// scrupolo: il 2026-08-13 una richiesta klines è rimasta appesa senza risposta (thread
    /// starvation nel pod, cpu limit 1) e il worker — che aspettava il completamento del ciclo per
    /// riarmare il timer — è rimasto muto per 30 minuti, con 89 serie oltre tolleranza.
    ///
    /// <para>Livello 1, cooperativo: <c>CancelAfter(budget)</c> cancella il token e la catena, che
    /// lo onora a ogni await, esce entro qualche secondo. Livello 2, il backstop
    /// <c>WaitAsync(2× budget)</c>: se un anello FUTURO della catena smettesse di onorare il token,
    /// il ciclo viene ABBANDONATO comunque — un'attesa che ignora la cancellazione costa al
    /// massimo due budget, mai un worker parcheggiato. (Il task zombie resta in volo coi suoi
    /// servizi scoped: il Dispose dello scope lo farà fallire; l'eccezione non osservata è il
    /// prezzo accettato per non morire con lui.)</para>
    ///
    /// <para>Pubblico per i test. Restituisce false se il ciclo è stato interrotto (budget,
    /// timeout di rete o backstop: il cursore incrementale riprende da dov'era al tick successivo);
    /// true se completato. La cancellazione del CHIAMANTE (shutdown) ripropaga: non è un timeout.</para>
    /// </summary>
    public async Task<bool> RunCycleAsync(TimeSpan budget, CancellationToken stoppingToken, TimeSpan? abandonAfter = null)
    {
        using var scope = scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<IMarketDataSyncService>();

        // Il backstop scatta sempre DOPO il budget cooperativo: se scattasse prima, il dispose di
        // cycleCts disarmerebbe il CancelAfter e il token dello zombie non verrebbe mai cancellato
        // (review 2026-08-15). Un abandonAfter <= budget viene quindi rialzato al default.
        var abandon = abandonAfter is TimeSpan a && a > budget ? a : budget * 2;

        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cycleCts.CancelAfter(budget);
        var cycle = sync.SyncAllEnabledAsync(cycleCts.Token);
        try
        {
            await cycle.WaitAsync(abandon, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw; // shutdown del chiamante: ripropaga, non è un timeout
        }
        catch (OperationCanceledException) when (cycleCts.IsCancellationRequested)
        {
            logger.LogWarning(
                "Ciclo di sincronizzazione oltre il budget di {Budget}: interrotto (una chiamata appesa non deve " +
                "parcheggiare il worker). Il cursore incrementale riprende dal punto raggiunto al prossimo tick.",
                budget);
            return false;
        }
        catch (OperationCanceledException ex)
        {
            // Terza sorgente di OCE: timeout di rete travestito da cancellazione (TaskCanceledException
            // con Token=None). Con il filtro per-serie in MarketDataSyncService non dovrebbe più
            // arrivare fin qui; se arriva, è un errore di ciclo — MAI uno shutdown.
            logger.LogError(ex,
                "Ciclo di sincronizzazione abortito da una cancellazione NON richiesta (timeout di rete?); " +
                "ritento al prossimo tick.");
            return false;
        }
        catch (TimeoutException) when (!cycle.IsCompleted)
        {
            // IL backstop: la catena non ha osservato la cancellazione e il task è ancora in volo.
            // Cancel() esplicito PRIMA del dispose: così il token dello zombie resta cancellato e,
            // se l'await appeso un giorno riprende, muore subito invece di scrivere a scope morto.
            cycleCts.Cancel();
            logger.LogError(
                "Ciclo di sincronizzazione ABBANDONATO dopo {Abandon}: la catena non ha osservato la cancellazione " +
                "del budget ({Budget}). C'è un await che ignora il CancellationToken — va trovato. Il worker resta vivo; "
                + "la serie in volo tiene il suo gate e verrà SALTATA dai prossimi cicli finché non si libera.",
                abandon, budget);
            return false;
        }
        catch (TimeoutException ex)
        {
            // TimeoutException DELLA CATENA (es. pool Npgsql esaurito), non del backstop: il task è
            // completato (faulted). Attribuirla al backstop manderebbe la forense nella direzione
            // sbagliata (review 2026-08-15).
            logger.LogError(ex, "Ciclo di sincronizzazione fallito per TimeoutException della catena; ritento al prossimo tick.");
            return false;
        }
    }

    /// <summary>
    /// Timbro del ciclo su <c>HostHeartbeats</c> (ruolo <see cref="HostHeartbeat.IngestionSyncRole"/>):
    /// «l'ultimo giro di sync è delle HH:mm» diventa un dato leggibile dal guscio — la pagina
    /// /market/watchlist e la guardia di freschezza lo usano per dire se il guasto è il SYNC o i
    /// simboli. Best-effort: un timbro mancato non deve costare il ciclo.
    /// </summary>
    /// <summary>Intervallo del PeriodicTimer, fissato all'avvio: finisce nel timbro perché i giudici (guscio) leggano la cadenza VERA del processo che timbra, non la propria config.</summary>
    private TimeSpan _interval = TimeSpan.FromMinutes(5);

    private async Task StampCycleAsync(string esito, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var now = DateTime.UtcNow;
            // L'intervallo viaggia COL timbro: guscio e pod hanno appsettings indipendenti, e una
            // soglia calcolata sull'intervallo del processo sbagliato giudicherebbe male (review
            // 2026-08-15). SyncPulse.TryParseStampedInterval lo rilegge dall'altra parte.
            var outcome = SyncPulse.ComposeOutcome(esito, _interval);
            var row = await db.HostHeartbeats.FirstOrDefaultAsync(h => h.Host == HostHeartbeat.IngestionSyncRole, ct);
            if (row is null)
            {
                db.HostHeartbeats.Add(new HostHeartbeat { Host = HostHeartbeat.IngestionSyncRole, LastUtc = now, Version = outcome });
            }
            else
            {
                row.LastUtc = now;
                row.Version = outcome;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Timbro del ciclo di sync non scritto (non fatale).");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
