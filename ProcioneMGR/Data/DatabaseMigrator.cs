using System.Reflection;
using System.Runtime.Loader;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ProcioneMGR.Data;

/// <summary>Opzioni della migrazione automatica, sezione <c>Database</c>.</summary>
public sealed class DatabaseMigrationOptions
{
    /// <summary>
    /// Applica le migrazioni pendenti all'avvio. Default TRUE: fino al 2026-08-05 lo schema si
    /// applicava solo a mano (<c>dotnet ef database update</c>) e una migrazione dimenticata si
    /// manifestava come un errore runtime a metà giornata — «relation … does not exist» — invece
    /// che come una riga di log all'avvio.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Secondi di attesa per il lock: oltre, si rinuncia e si dichiara (un altro host sta migrando).</summary>
    public int LockTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Esito della migrazione, per chi vuole raccontarlo (log, pannello, test).
/// <paramref name="Applied"/> elenca le migrazioni applicate DA QUESTA chiamata.
/// </summary>
public sealed record MigrationOutcome(
    bool Ran,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> StillPending,
    string? Skipped);

/// <summary>
/// [2026-08-05] Applica le migrazioni pendenti all'avvio.
///
/// <para><b>Il problema che risolve</b>: l'app NON referenzia <c>ProcioneMGR.Migrations.Postgres</c>
/// (sarebbe un ciclo di progetti — quello referenzia l'app per il tipo del DbContext), quindi non
/// c'era migrate-on-startup e lo schema si applicava a mano. Funziona finché qualcuno si ricorda.
/// La sera del 2026-08-05 non me ne sono ricordato io: la tabella nuova non c'era e me ne sono
/// accorto solo interrogando il database.</para>
///
/// <para><b>Come lo risolve senza creare il ciclo</b>: EF risolve l'assembly delle migrazioni per
/// NOME (<c>MigrationsAssembly("ProcioneMGR.Migrations.Postgres")</c>), quindi basta che la DLL sia
/// accanto all'eseguibile — ci pensa un target di copia nel progetto delle migrazioni. Se la DLL
/// NON c'è (host satelliti, che non la ricevono), non si finge nulla: si dichiara a log che le
/// migrazioni non sono applicabili da qui e si prosegue, esattamente come prima.</para>
///
/// <para><b>Perché il lock</b>: monolite e servizi in cluster condividono lo stesso database e
/// possono partire insieme. Due <c>Migrate()</c> concorrenti sulla stessa migrazione producono
/// errori di DDL duplicata. Un advisory lock di PostgreSQL serializza i partenti: il primo migra,
/// gli altri aspettano e poi non trovano nulla da fare.</para>
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>
    /// Chiave dell'advisory lock. Costante arbitraria ma STABILE: cambiarla significa perdere la
    /// serializzazione fra host che eseguono versioni diverse.
    /// </summary>
    private const long AdvisoryLockKey = 8_050_2026;

    /// <summary>Nome dell'assembly che ospita le migrazioni (deve combaciare con <c>MigrationsAssembly(...)</c>).</summary>
    internal const string MigrationsAssemblyName = "ProcioneMGR.Migrations.Postgres";

    /// <summary>La DLL delle migrazioni è fisicamente accanto all'eseguibile di QUESTO host?</summary>
    internal static bool MigrationsDllPresent()
        => File.Exists(Path.Combine(AppContext.BaseDirectory, MigrationsAssemblyName + ".dll"));

    private static int _resolverInstalled;

    /// <summary>
    /// Insegna al runtime a trovare l'assembly delle migrazioni accanto all'eseguibile.
    ///
    /// <para><b>Perché non basta copiarci la DLL</b>: un'app .NET framework-dependent risolve gli
    /// assembly dal proprio <c>deps.json</c>, non dai file che trova nella cartella. La DLL c'era,
    /// pesava mezzo mega, e <c>Assembly.Load</c> continuava a dire «file non trovato» — perché
    /// nessuno gliel'aveva dichiarata. Non essendoci un ProjectReference (sarebbe un ciclo), non
    /// finisce in deps.json: gliela si presenta qui, esplicitamente, per NOME e solo per quel nome.</para>
    ///
    /// <para>Idempotente e innocuo: se il file non c'è restituisce null e il chiamante degrada come
    /// prima. Non è un resolver generico — risponde a un solo assembly, per non trasformare un
    /// dettaglio di deployment in una porta aperta.</para>
    /// </summary>
    internal static void EnsureMigrationsAssemblyResolvable(ILogger? logger = null)
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (!string.Equals(name.Name, MigrationsAssemblyName, StringComparison.Ordinal)) return null;

            var path = Path.Combine(AppContext.BaseDirectory, MigrationsAssemblyName + ".dll");
            if (!File.Exists(path)) return null;

            try
            {
                var loaded = context.LoadFromAssemblyPath(path);
                logger?.LogDebug("Assembly delle migrazioni caricato da {Path}.", path);
                return loaded;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Assembly delle migrazioni presente ma non caricabile da {Path}.", path);
                return null;
            }
        };
    }

    public static async Task<MigrationOutcome> MigrateAsync(
        IServiceProvider services,
        DatabaseMigrationOptions options,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!options.AutoMigrate)
        {
            logger.LogInformation("Migrazione automatica disattivata (Database:AutoMigrate=false): lo schema si applica a mano.");
            return new MigrationOutcome(false, [], [], "disattivata da configurazione");
        }

        EnsureMigrationsAssemblyResolvable(logger);

        var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        // Prima domanda: le migrazioni sono raggiungibili da questo host? Gli host satelliti non
        // ricevono la DLL, e per loro il comportamento corretto è restare come prima — non un
        // errore, non un silenzio.
        IReadOnlyList<string> pending;
        try
        {
            pending = [.. await db.Database.GetPendingMigrationsAsync(ct)];
        }
        catch (Exception ex)
        {
            // NB: i segnaposto di ILogger sono POSIZIONALI anche se hanno un nome — ripeterne uno
            // senza ripetere l'argomento fa lanciare il formatter (successo scrivendo questa
            // riga). Un nome per argomento, sempre.
            const string configuration =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            logger.LogWarning(
                "Migrazioni non applicabili da questo host: l'assembly '{Assembly}' non è accanto all'eseguibile. " +
                "È NORMALE sugli host satelliti (ingestion/ml/trading), che non lo ricevono. Sul monolite invece " +
                "significa che la soluzione non è stata costruita in configurazione {Configuration}: " +
                "`dotnet build ProcioneMGR.sln -c {Configuration2}` mette la DLL al suo posto. Nel frattempo lo " +
                "schema resta com'è e si applica a mano con `dotnet ef database update`. Dettaglio: {Error}",
                "ProcioneMGR.Migrations.Postgres", configuration, configuration, ex.Message);
            return new MigrationOutcome(false, [], [], "assembly delle migrazioni non disponibile");
        }

        if (pending.Count == 0)
        {
            // [Fase 5, 2026-08-11] «Zero pendenti» ha DUE cause possibili, e confonderle è già
            // costato un primo avvio senza schema: (a) lo schema è davvero allineato; (b) l'assembly
            // si è CARICATO ma non espone alcuna migrazione — succede quando le versioni EF di app e
            // progetto migrazioni divergono: ogni classe Migration fallisce il load sulla versione
            // più alta, EF ingoia l'eccezione e la lista esce vuota. La discriminante è il conteggio
            // TOTALE delle migrazioni note: un assembly di migrazioni vero non è mai vuoto.
            var known = db.Database.GetMigrations().Count();
            if (known == 0 && MigrationsDllPresent())
            {
                logger.LogError(
                    "La DLL '{Assembly}' è accanto all'eseguibile ma NON espone alcuna migrazione: " +
                    "quasi certamente le versioni EF di app e progetto migrazioni sono disallineate " +
                    "(il load dei tipi fallisce in silenzio). Lo schema NON è verificato — non lo " +
                    "dichiaro allineato. Allineare Microsoft.EntityFrameworkCore.Design nel progetto " +
                    "migrazioni alla versione EF dell'app e ricostruire.",
                    MigrationsAssemblyName);
                return new MigrationOutcome(false, [], [], "assembly caricato ma senza migrazioni (versioni EF disallineate?)");
            }

            logger.LogInformation("Schema del database già allineato: nessuna migrazione pendente ({Known} note).", known);
            return new MigrationOutcome(true, [], [], null);
        }

        logger.LogInformation("Migrazioni pendenti ({Count}): {Migrations}. Le applico ora.",
            pending.Count, string.Join(", ", pending));

        // Advisory lock: seriale fra host. Connessione DEDICATA — il lock vive quanto la sessione
        // che lo tiene, e usare quella del DbContext significherebbe legarne la durata a un
        // dettaglio del pool.
        var connectionString = db.Database.GetConnectionString();
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(ct);

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.LockTimeoutSeconds)));
            try
            {
                await using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", lockConnection);
                acquire.Parameters.AddWithValue("key", AdvisoryLockKey);
                await acquire.ExecuteNonQueryAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Lock delle migrazioni non ottenuto entro {Seconds}s: un altro host sta migrando. Proseguo senza applicare nulla.",
                    options.LockTimeoutSeconds);
                return new MigrationOutcome(false, [], pending, "lock non ottenuto");
            }
        }

        try
        {
            // Rilettura DENTRO il lock: se un altro host ha migrato mentre aspettavamo, qui non
            // c'è più niente da fare — ed è il caso normale di due host che partono insieme.
            var stillPending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (stillPending.Count == 0)
            {
                logger.LogInformation("Un altro host ha già applicato le migrazioni mentre attendevo: nulla da fare.");
                return new MigrationOutcome(true, [], [], null);
            }

            await db.Database.MigrateAsync(ct);
            logger.LogInformation("Migrazioni applicate ({Count}): {Migrations}.",
                stillPending.Count, string.Join(", ", stillPending));
            return new MigrationOutcome(true, stillPending, [], null);
        }
        finally
        {
            try
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockConnection);
                release.Parameters.AddWithValue("key", AdvisoryLockKey);
                await release.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                // Non fatale: chiudendo la connessione il lock cade comunque.
                logger.LogDebug(ex, "Rilascio esplicito del lock delle migrazioni fallito (cade con la connessione).");
            }
        }
    }
}
