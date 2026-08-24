using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Admin;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Rendering di <c>/admin/backup</c> — il livello che vede ciò che sta fra il servizio e l'occhio.
///
/// <para>Il difetto del 2026-08-23 viveva esattamente lì: <see cref="DatabaseBackupHelper"/>
/// funzionava, <c>db-backup.ps1</c> funzionava, i dump erano integri — e la pagina diceva che
/// l'ultimo backup era di un mese e mezzo prima, perché guardava la cartella sbagliata. Nessun test
/// di unità sul servizio avrebbe potuto vederlo: il servizio rispondeva correttamente alla domanda
/// che gli veniva posta. Era la domanda a essere incompleta.</para>
/// </summary>
public class BackupPageRenderTests : BunitContext, IDisposable
{
    private readonly string _root;
    private readonly string _manualDir;
    private readonly string _nightlyDir;

    public BackupPageRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _root = Path.Combine(Path.GetTempPath(), "procione-backup-page-" + Guid.NewGuid().ToString("N"));
        _manualDir = Path.Combine(_root, "backup");
        _nightlyDir = Path.Combine(_root, "notturni");
        Directory.CreateDirectory(_manualDir);
        Directory.CreateDirectory(_nightlyDir);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* best-effort */ }
    }

    private sealed class FakeEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ProcioneMGR.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeConfigWriter : IAppConfigWriter
    {
        public readonly List<(string Path, object Value)> Saved = [];

        public Task SaveSectionAsync<T>(string sectionPath, T options, CancellationToken ct = default)
        {
            Saved.Add((sectionPath, options!));
            return Task.CompletedTask;
        }

        public Task SaveValueAsync<T>(string keyPath, T value, CancellationToken ct = default)
        {
            Saved.Add((keyPath, value!));
            return Task.CompletedTask;
        }
    }

    private void Register(BackupOptions options)
    {
        // Connessione volutamente inerte: la pagina non tocca il database al rendering, e se un
        // giorno lo facesse è meglio che trovi una porta chiusa che il DB di sviluppo.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgresConnection"] =
                    "Host=127.0.0.1;Port=1;Database=procionemgr_test_inesistente;Username=nessuno;Password=x",
            })
            .Build();

        Services.AddLogging();
        Services.AddSingleton<IAppConfigWriter, FakeConfigWriter>();
        Services.AddSingleton(new DatabaseBackupService(configuration, new FakeEnv(_root), options.AsMonitor()));

        var auth = AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles(AppRoles.Admin);
    }

    private BackupOptions Options(int staleAfterHours = 48) => new()
    {
        NightlyDirectory = _nightlyDir,
        StaleAfterHours = staleAfterHours,
        // Nome deliberatamente inesistente: il verdetto della pagina non deve dipendere dal Task
        // Scheduler della macchina che esegue i test. Il probe vero si collauda dal vivo.
        ScheduledTaskName = "ProcioneMGR Backup DB (nome che non esiste)",
    };

    private static void Dump(string dir, string name, double ageHours)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "PGDMP-finto");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-ageHours));
    }

    private IRenderedComponent<ProcioneMGR.Components.Pages.Admin.Backup> RenderPage()
    {
        var cut = Render<ProcioneMGR.Components.Pages.Admin.Backup>();
        // Il caricamento è asincrono (elenco + interrogazione del Task Scheduler): si aspetta che
        // il pannello notturno abbia smesso di dire «Lettura dello stato…».
        cut.WaitForState(() => !cut.Markup.Contains("Lettura dello stato"), TimeSpan.FromSeconds(60));
        return cut;
    }

    // ----------------------------------------------------------------------------------------

    [Fact]
    public void LaRegressione_UnManualeVecchioNonNascondePiuUnNotturnoSano()
    {
        // ESATTAMENTE lo stato del 2026-08-23: manuale fermo al 2026-07-09, notturno di stanotte.
        // Prima la pagina mostrava una riga sola, quella vecchia, e la chiamava «il backup».
        Dump(_manualDir, "procionemgr-20260709-101500.dump", ageHours: 24 * 45);
        Dump(_nightlyDir, "procionemgr-20260823-033004.dump", ageHours: 6);
        Register(Options());

        var markup = RenderPage().Markup;

        Assert.Contains("procionemgr-20260823-033004.dump", markup, StringComparison.Ordinal);
        Assert.Contains("procionemgr-20260709-101500.dump", markup, StringComparison.Ordinal);
        Assert.Contains(">SANO<", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">FERMO<", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void OgniRigaDichiaraLaSuaProvenienza()
    {
        Dump(_manualDir, "procionemgr-20260709-101500.dump", ageHours: 24 * 45);
        Dump(_nightlyDir, "procionemgr-20260823-033004.dump", ageHours: 6);
        Register(Options());

        var cut = RenderPage();

        // Si cerca il TITLE del badge e non il testo: «notturno» e «manuale» compaiono anche nella
        // Guida, e un test che li trovasse lì passerebbe pure con la tabella priva della colonna.
        Assert.Contains("Sorgente", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Prodotto da scripts/db-backup.ps1", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Creato da questa pagina", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void UnNotturnoFermo_LoDiceInChiaro()
    {
        // Il rovescio della regressione: la pagina dev'essere capace di gridare, non solo di
        // rassicurare. Un controllo che dice sempre «sano» è inutile quanto uno che dice sempre
        // «fermo».
        Dump(_nightlyDir, "procionemgr-20260818-033000.dump", ageHours: 120);
        Register(Options(staleAfterHours: 48));

        var markup = RenderPage().Markup;

        Assert.Contains(">FERMO<", markup, StringComparison.Ordinal);
        Assert.Contains("non sta girando", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CartellaNotturnaVuota_ELoStatoMaiEseguito_NonUnElencoSilenzioso()
    {
        Register(Options());

        var markup = RenderPage().Markup;

        Assert.Contains(">MAI ESEGUITO<", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LaDestinazioneNotturnaEMostrataEModificabile()
    {
        // Il mandato «tutto amministrabile da UI»: la cartella dove finisce il backup non può
        // essere una costante nascosta dentro uno script.
        Register(Options());

        var cut = RenderPage();

        Assert.Contains(_nightlyDir, cut.Markup, StringComparison.Ordinal);
        foreach (var id in new[] { "bk_dir", "bk_task", "bk_stale", "bk_keep" })
        {
            Assert.Single(cut.FindAll($"#{id}"));
        }
    }

    [Fact]
    public void IlPannelloDichiaraLaSezioneCheStaScrivendo()
    {
        // Ciò che ConfigurationKeyUiCoverageTests pretende sul SORGENTE, qui verificato sul markup
        // RESO: un pannello nascosto dentro un blocco @if mai raggiunto soddisferebbe il guardiano
        // statico e resterebbe invisibile all'operatore. E il nome della sezione dev'esserci: senza,
        // chi legge appsettings.json non sa quale pannello governa quelle chiavi.
        Register(Options());

        var cut = RenderPage();

        Assert.Contains("<code>Backup</code>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Configurazione del backup notturno", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Salva configurazione", StringComparison.Ordinal));
    }

    [Fact]
    public void SenzaAlcunBackup_LaPaginaNominaEntrambeLeCartelle()
    {
        // Il messaggio «nessun backup» deve dire DOVE si è guardato: altrimenti è indistinguibile
        // da «ho guardato nel posto sbagliato», che è la storia di questa pagina.
        Register(Options());

        var markup = RenderPage().Markup;

        Assert.Contains(_manualDir, markup, StringComparison.Ordinal);
        Assert.Contains(_nightlyDir, markup, StringComparison.Ordinal);
    }
}
