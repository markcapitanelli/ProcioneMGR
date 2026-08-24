using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ProcioneMGR.Services.Admin;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// IL BACKUP CHE C'È, NON QUELLO CHE LA PAGINA CONOSCE.
///
/// <para>Il 2026-08-23 <c>/admin/backup</c> dichiarava «ultimo backup: 2026-07-09» mentre il dump
/// della notte prima esisteva, era integro e pesava centinaia di MB. Non era rotto niente: la
/// pagina guardava <c>backup/</c> sotto la content root, e il backup vero — l'operazione
/// pianificata che lancia <c>scripts/db-backup.ps1</c> — scriveva in
/// <c>%USERPROFILE%\ProcioneMGR-Backup</c>. Un controllo che dà una risposta <b>a prescindere dalla
/// realtà</b>: allarmava su un backup sano, e avrebbe taciuto allo stesso modo su uno morto.</para>
///
/// <para>Questi test difendono le tre proprietà che rendono la pagina un controllo vero: elenca
/// <b>entrambe</b> le cartelle dichiarando la provenienza; il verdetto sul notturno viene dai file
/// <b>e</b> dallo stato del task; e ogni divergenza fra ciò che la configurazione dice e ciò che il
/// sistema fa viene <b>detta</b>, invece di essere risolta in silenzio a favore di una delle
/// due.</para>
/// </summary>
public sealed class BackupVisibilityTests : IDisposable
{
    private readonly string _root;
    private readonly string _manualDir;
    private readonly string _nightlyDir;

    public BackupVisibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "procione-backup-" + Guid.NewGuid().ToString("N"));
        _manualDir = Path.Combine(_root, "backup");
        _nightlyDir = Path.Combine(_root, "notturni");
        Directory.CreateDirectory(_manualDir);
        Directory.CreateDirectory(_nightlyDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best-effort */ }
    }

    // ----------------------------------------------------------------------------------------
    //  Impalcatura
    // ----------------------------------------------------------------------------------------

    private sealed class FakeEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ProcioneMGR.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private DatabaseBackupService Service(BackupOptions options)
    {
        // Porta 1 e database inesistente DI PROPOSITO: qualche test attraversa Restore(), che è
        // distruttivo. Se una guardia cedesse, deve trovare una connessione che non porta da
        // nessuna parte — non il database di sviluppo su localhost:5432.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgresConnection"] =
                    "Host=127.0.0.1;Port=1;Database=procionemgr_test_inesistente;Username=nessuno;Password=x",
            })
            .Build();

        return new DatabaseBackupService(configuration, new FakeEnv(_root), options.AsMonitor());
    }

    private BackupOptions Options(string? nightlyDir = null, int staleAfterHours = 48) => new()
    {
        NightlyDirectory = nightlyDir ?? _nightlyDir,
        StaleAfterHours = staleAfterHours,
        ScheduledTaskName = "ProcioneMGR Backup DB",
    };

    /// <summary>Crea un finto dump con l'età voluta. L'età si misura su LastWriteTime.</summary>
    private static string Dump(string dir, string name, double ageHours)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "PGDMP-finto");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
        return path;
    }

    private static ScheduledTaskStatus HealthyTask(string destination) => new(
        Queryable: true,
        Exists: true,
        State: "Ready",
        Arguments: $"-NoProfile -ExecutionPolicy Bypass -File \"C:\\repo\\scripts\\db-backup.ps1\" -Destination \"{destination}\"",
        ScriptPath: @"C:\repo\scripts\db-backup.ps1",
        Destination: destination,
        LastRunLocal: DateTime.Now.AddHours(-6),
        LastResult: 0,
        NextRunLocal: DateTime.Now.AddHours(18));

    // ----------------------------------------------------------------------------------------
    //  1. L'elenco vede entrambe le cartelle, e dice da quale viene ogni riga
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void ListBackups_MostraEntrambeLeCartelle_ConLaProvenienzaDichiarata()
    {
        Dump(_manualDir, "procionemgr-20260709-101500.dump", ageHours: 24 * 45);
        Dump(_nightlyDir, "procionemgr-20260823-033000.dump", ageHours: 6);

        var backups = Service(Options()).ListBackups();

        // Prima del 2026-08-23 qui ci sarebbe stato UN file, quello vecchio di 45 giorni, e la
        // pagina lo avrebbe presentato come «il backup».
        Assert.Equal(2, backups.Count);
        Assert.Equal(BackupSource.Nightly, backups[0].Source);   // più recente per primo
        Assert.Equal(BackupSource.Manual, backups[1].Source);
    }

    [Fact]
    public void ListBackups_QuandoLeDueCartelleCoincidono_NonDuplicaEAmmetteDiNonSaperDistinguere()
    {
        // I nomi hanno la stessa forma (procionemgr-<stamp>.dump): attribuire la provenienza
        // sarebbe indovinare, e un'etichetta indovinata è peggio di un'etichetta che si astiene.
        Dump(_manualDir, "procionemgr-20260823-033000.dump", ageHours: 6);

        var backups = Service(Options(nightlyDir: _manualDir)).ListBackups();

        Assert.Single(backups);
        Assert.Equal(BackupSource.Shared, backups[0].Source);
    }

    [Fact]
    public void ListBackups_ConDestinazioneNotturnaIrraggiungibile_NonEsplodeENonNasconde_iManuali()
    {
        Dump(_manualDir, "procionemgr-20260709-101500.dump", ageHours: 24 * 45);

        var backups = Service(Options(nightlyDir: Path.Combine(_root, "disco-che-non-ce"))).ListBackups();

        Assert.Single(backups);
        Assert.Equal(BackupSource.Manual, backups[0].Source);
    }

    // ----------------------------------------------------------------------------------------
    //  2. Il verdetto sul notturno
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Verdetto_ConUnDumpRecente_ESano()
    {
        Dump(_nightlyDir, "procionemgr-20260823-033000.dump", ageHours: 6);

        var status = Service(Options()).BuildStatus(Options(), HealthyTask(_nightlyDir));

        Assert.Equal(NightlyVerdict.Healthy, status.Verdict);
        Assert.NotNull(status.Latest);
        Assert.InRange(status.AgeHours!.Value, 5.5, 6.5);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public void Verdetto_OltreLaSogliaConfigurata_EFermo()
    {
        Dump(_nightlyDir, "procionemgr-20260820-033000.dump", ageHours: 72);

        var options = Options(staleAfterHours: 48);
        var status = Service(options).BuildStatus(options, HealthyTask(_nightlyDir));

        Assert.Equal(NightlyVerdict.Stale, status.Verdict);
    }

    [Fact]
    public void Verdetto_LaSogliaEQuellaConfigurata_NonUnaCostante()
    {
        // Stesso file, stessa età: cambia solo la soglia. Se il verdetto non seguisse la
        // configurazione, la manopola in pagina sarebbe finta — e /admin/backup e
        // `db-backup.ps1 -Verify` potrebbero dare due risposte opposte sullo stesso backup.
        Dump(_nightlyDir, "procionemgr-20260821-033000.dump", ageHours: 72);

        var permissive = Options(staleAfterHours: 96);
        var severe = Options(staleAfterHours: 24);

        Assert.Equal(NightlyVerdict.Healthy, Service(permissive).BuildStatus(permissive, HealthyTask(_nightlyDir)).Verdict);
        Assert.Equal(NightlyVerdict.Stale, Service(severe).BuildStatus(severe, HealthyTask(_nightlyDir)).Verdict);
    }

    [Fact]
    public void Verdetto_CartellaVuota_EMaiEseguito_NonSano()
    {
        var status = Service(Options()).BuildStatus(Options(), HealthyTask(_nightlyDir));

        Assert.Equal(NightlyVerdict.NeverRun, status.Verdict);
        Assert.Null(status.Latest);
        Assert.Equal(0, status.FileCount);
    }

    [Fact]
    public void Verdetto_CartellaAssente_ESuoProprioCaso()
    {
        // Diverso da «vuota»: una cartella che non esiste di solito significa che il backup scrive
        // altrove, non che non abbia mai girato. Confonderli manda a cercare nel posto sbagliato.
        var missing = Path.Combine(_root, "mai-creata");
        var options = Options(nightlyDir: missing);

        var status = Service(options).BuildStatus(options, HealthyTask(missing));

        Assert.Equal(NightlyVerdict.DirectoryMissing, status.Verdict);
        Assert.False(status.DirectoryExists);
    }

    // ----------------------------------------------------------------------------------------
    //  3. Le divergenze si dicono
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Avvisi_TaskCheScriveAltrove_EIlCasoChePrimaProduceva_ilFalsoAllarme()
    {
        var options = Options();
        var task = HealthyTask(@"D:\ProcioneMGR-Backup-vecchio");

        var warnings = DatabaseBackupService.BuildWarnings(_nightlyDir, task, options);

        var warning = Assert.Single(warnings);
        Assert.Contains(@"D:\ProcioneMGR-Backup-vecchio", warning, StringComparison.Ordinal);
        Assert.Contains(_nightlyDir, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Avvisi_StessaCartellaScrittaInModiDiversi_NonEUnaDivergenza()
    {
        // Un separatore finale o un percorso non normalizzato non sono un guasto: segnalarli
        // sarebbe rumore, e il rumore consuma la credibilità degli avvisi veri.
        var options = Options();
        var task = HealthyTask(_nightlyDir + Path.DirectorySeparatorChar);

        Assert.Empty(DatabaseBackupService.BuildWarnings(_nightlyDir, task, options));
    }

    [Fact]
    public void Avvisi_TaskInesistente_LoDiceEDiceComeRegistrarlo()
    {
        var warnings = DatabaseBackupService.BuildWarnings(
            _nightlyDir, new ScheduledTaskStatus(Queryable: true, Exists: false), Options());

        var warning = Assert.Single(warnings);
        Assert.Contains("ProcioneMGR Backup DB", warning, StringComparison.Ordinal);
        Assert.Contains("-Register", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Avvisi_TaskNonInterrogabile_NonEUnaConferma()
    {
        // Fuori da Windows non sappiamo niente del task. Tacere lascerebbe credere che il silenzio
        // sia un via libera: è la stessa forma del difetto che questa pagina chiude.
        var warnings = DatabaseBackupService.BuildWarnings(
            _nightlyDir,
            new ScheduledTaskStatus(Queryable: false, Exists: false, Message: "non siamo su Windows"),
            Options());

        var warning = Assert.Single(warnings);
        Assert.Contains("NON verificato", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Avvisi_ScriptDentroUnWorktree_RichiamaIlGuastoDel_2026_08_17()
    {
        var task = HealthyTask(_nightlyDir) with
        {
            ScriptPath = @"C:\repo\.claude\worktrees\qualcosa\scripts\db-backup.ps1",
        };

        var warnings = DatabaseBackupService.BuildWarnings(_nightlyDir, task, Options());

        Assert.Contains(warnings, w => w.Contains("WORKTREE", StringComparison.Ordinal));
    }

    [Fact]
    public void Avvisi_UltimaEsecuzioneFallita_LoDice()
    {
        var task = HealthyTask(_nightlyDir) with { LastResult = 1 };

        var warnings = DatabaseBackupService.BuildWarnings(_nightlyDir, task, Options());

        Assert.Contains(warnings, w => w.Contains("codice 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Avvisi_TaskInEsecuzioneOMaiPartito_NonSonoFallimenti()
    {
        // 0x41301 = sta girando adesso, 0x41303 = non è mai partito: due stati normali che, letti
        // come codici d'errore, produrrebbero un allarme ogni notte alle 03:30.
        foreach (var code in new[] { DatabaseBackupService.TaskRunningCode, DatabaseBackupService.TaskNeverRunCode })
        {
            var task = HealthyTask(_nightlyDir) with { LastResult = code };
            Assert.Empty(DatabaseBackupService.BuildWarnings(_nightlyDir, task, Options()));
        }
    }

    [Fact]
    public void Avvisi_TaskDisabilitato_LoDice()
    {
        var task = HealthyTask(_nightlyDir) with { State = "Disabled" };

        Assert.Contains(
            DatabaseBackupService.BuildWarnings(_nightlyDir, task, Options()),
            w => w.Contains("DISABILITATA", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------------------------------
    //  4. Lettura degli argomenti del task
    // ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("-File \"C:\\a b\\db-backup.ps1\" -Destination \"C:\\Users\\x\\Backup\"", "Destination", "C:\\Users\\x\\Backup")]
    [InlineData("-File C:\\a\\db-backup.ps1 -Destination C:\\Backup -KeepDays 14", "Destination", "C:\\Backup")]
    [InlineData("-File \"C:\\a\\db-backup.ps1\" -destination \"C:\\Backup\"", "Destination", "C:\\Backup")]
    [InlineData("-NoProfile -File \"C:\\a\\db-backup.ps1\"", "Destination", null)]
    [InlineData("-File \"C:\\a\\db-backup.ps1\" -Destination \"C:\\B\"", "File", "C:\\a\\db-backup.ps1")]
    public void ExtractSwitch_LeggeGliArgomentiRealiDelTask(string arguments, string name, string? expected)
    {
        Assert.Equal(expected, ScheduledTaskProbe.ExtractSwitch(arguments, name));
    }

    [Fact]
    public void ExtractSwitch_NonSiFaIngannareDaUnaSottostringa()
    {
        // "-NoDestination" non è "-Destination": un match sciatto qui inventerebbe una divergenza.
        Assert.Null(ScheduledTaskProbe.ExtractSwitch("-NoDestinationCheck C:\\x", "Destination"));
    }

    [Fact]
    public void Parse_TaskAssente_ELeggibileEDistintoDaNonInterrogabile()
    {
        var status = ScheduledTaskProbe.Parse("""{"Exists":false}""");

        Assert.True(status.Queryable);
        Assert.False(status.Exists);
    }

    [Fact]
    public void Parse_TaskPresente_PortaEsitoEDestinazione()
    {
        var status = ScheduledTaskProbe.Parse("""
            {"Exists":true,"State":"Ready","Arguments":"-File \"C:\\r\\scripts\\db-backup.ps1\" -Destination \"C:\\Users\\proci\\ProcioneMGR-Backup\"","LastRunTime":"2026-08-23T03:30:00.0000000+02:00","NextRunTime":"2026-08-24T03:30:00.0000000+02:00","LastTaskResult":0}
            """);

        Assert.True(status.Queryable);
        Assert.True(status.Exists);
        Assert.Equal("Ready", status.State);
        Assert.Equal(0L, status.LastResult);
        Assert.Equal(@"C:\Users\proci\ProcioneMGR-Backup", status.Destination);
        Assert.Equal(@"C:\r\scripts\db-backup.ps1", status.ScriptPath);
        Assert.NotNull(status.LastRunLocal);
    }

    [Fact]
    public void Parse_RispostaIlleggibile_DiventaNonInterrogabile_NonTaskAssente()
    {
        var status = ScheduledTaskProbe.Parse("questo non è JSON");

        Assert.False(status.Queryable);
        Assert.NotNull(status.Message);
    }

    // ----------------------------------------------------------------------------------------
    //  5. Il ripristino accetta solo le cartelle note
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Restore_RifiutaUnPercorsoFuoriDalleCartelleNote()
    {
        var estraneo = Path.Combine(_root, "altrove.dump");
        File.WriteAllText(estraneo, "x");

        var ex = Assert.Throws<InvalidOperationException>(() => Service(Options()).Restore(estraneo));
        Assert.Contains("non è in una cartella di backup nota", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_AccettaUnFileDellaCartellaNotturna()
    {
        // Il file notturno dev'essere ripristinabile come uno manuale: mostrarlo e poi rifiutarlo
        // sarebbe una promessa non mantenuta proprio nel momento in cui serve.
        var dump = Dump(_nightlyDir, "procionemgr-20260823-033000.dump", ageHours: 6);

        // pg_restore non c'è (o l'archivio è finto): l'errore atteso è di CONTENUTO, non di percorso.
        var ex = Record.Exception(() => Service(Options()).Restore(dump));
        Assert.DoesNotContain("cartella di backup nota", ex?.Message ?? "", StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    //  6. La configurazione: default, risoluzione, regole
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void DestinazioneVuota_RisolveAlDefaultStoricoDelloScript()
    {
        var atteso = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ProcioneMGR-Backup");

        Assert.Equal(atteso, DatabaseBackupService.ResolveNightlyDirectory(new BackupOptions()));
    }

    [Fact]
    public void SezioneBackup_SiLegaDalFileDEsempioVersionato()
    {
        // Come ConfigurationBindingTests: un refuso nel nome della sezione non fa fallire Bind, lascia
        // i default. Qui i default COINCIDONO con i valori d'esempio (sono quelli giusti), quindi la
        // prova che la sezione esiste davvero è la chiave grezza, non il valore legato.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "ProcioneMGR", "appsettings.json.example"), optional: false)
            .Build();

        Assert.NotNull(configuration["Backup:StaleAfterHours"]);
        Assert.NotNull(configuration["Backup:ScheduledTaskName"]);

        var options = new BackupOptions();
        configuration.GetSection(BackupOptions.SectionName).Bind(options);

        Assert.Equal(48, options.StaleAfterHours);
        Assert.Equal(14, options.RetentionDays);
        Assert.Equal("ProcioneMGR Backup DB", options.ScheduledTaskName);
        Assert.Equal("", options.NightlyDirectory);
    }

    [Fact]
    public void Regole_RifiutanoUnPercorsoRelativo()
    {
        // Un percorso relativo si risolve contro la directory di lavoro, che per l'app e per il Task
        // Scheduler non è la stessa: due cartelle diverse a partire dallo stesso testo.
        var error = AdminConfigRules.Validate(new BackupOptions { NightlyDirectory = "backup-notturni" });

        Assert.NotNull(error);
        Assert.Contains("ASSOLUTO", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Regole_RifiutanoUnaSogliaANullaEUnaConservazioneANulla()
    {
        Assert.NotNull(AdminConfigRules.Validate(new BackupOptions { StaleAfterHours = 0 }));
        Assert.NotNull(AdminConfigRules.Validate(new BackupOptions { RetentionDays = 0 }));
        Assert.NotNull(AdminConfigRules.Validate(new BackupOptions { ScheduledTaskName = "  " }));
    }

    /// <summary>
    /// Lo script notturno e la pagina devono leggere la STESSA sezione: se lo script cercasse un
    /// nome diverso tornerebbe silenziosamente ai suoi default, e le due verità che questa modifica
    /// ha unificato si separerebbero di nuovo — senza che nulla protesti.
    /// </summary>
    [Fact]
    public void LoScriptNotturno_LeggeLaStessaSezioneDellaPagina()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "db-backup.ps1"));

        Assert.Contains("ConvertFrom-Json).Backup", script, StringComparison.Ordinal);
        foreach (var key in new[] { "NightlyDirectory", "RetentionDays", "StaleAfterHours", "ScheduledTaskName" })
        {
            Assert.Contains($"$section.{key}", script, StringComparison.Ordinal);
        }

        // E -Register non deve congelare la destinazione dentro il task: sarebbe la stessa doppia
        // verità, spostata dal codice agli argomenti dell'operazione pianificata.
        Assert.Contains("if ($destinationExplicit) { $argument += \" -Destination", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il terzo lettore: il watchdog esterno guarda l'età del dump ogni 5 minuti e grida su
    /// Telegram. Se guardasse una cartella propria, il primo cambio di destinazione lo farebbe
    /// gridare ogni cinque minuti su un backup di stanotte — e dopo il terzo falso allarme
    /// nessuno leggerebbe più nemmeno quelli veri.
    /// </summary>
    [Fact]
    public void IlWatchdogEsterno_GuardaLaStessaCartellaEUsaLaStessaSoglia()
    {
        var watchdog = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "watchdog.ps1"));

        Assert.Contains("ConvertFrom-Json).Backup", watchdog, StringComparison.Ordinal);
        Assert.Contains("$backupDir = $backupWatch.Directory", watchdog, StringComparison.Ordinal);
        Assert.Contains("$backupWatch.StaleAfterHours", watchdog, StringComparison.Ordinal);

        // E la costante di prima non dev'essere rimasta accanto alla configurazione: due righe che
        // dicono la stessa cosa sono due righe che possono smettere di dirla.
        Assert.DoesNotContain(
            "$backupDir = Join-Path $env:USERPROFILE 'ProcioneMGR-Backup'", watchdog, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
