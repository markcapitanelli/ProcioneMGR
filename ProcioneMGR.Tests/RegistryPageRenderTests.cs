using AngleSharp.Dom;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ProcioneMGR.Data;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Rendering e interazione di <c>/registry</c> dopo la revisione del 2026-08-19.
///
/// <para>Il difetto: un modello in stadio <c>Retired</c> non era ripromuovibile da nessuna
/// superficie, quindi un ritiro accidentale — e «Ritira» era l'unica azione distruttiva della
/// pagina a UN CLIC SOLO — si annullava soltanto scrivendo a mano sul database. Questi test
/// guardano ciò che l'operatore LEGGE e ciò che il suo clic FA, che è dove il difetto viveva:
/// il registry, da solo, era già corretto. Sono il livello 4 dello standard di verifica reso
/// permanente, sul modello di <see cref="WatchlistPageRenderTests"/>.</para>
/// </summary>
[Collection("Postgres")]
public sealed class RegistryPageRenderTests : BunitContext
{
    private readonly string _connString;

    public RegistryPageRenderTests(PostgresFixture pg)
    {
        _connString = pg.CreateDatabase();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<IDbContextFactory<ApplicationDbContext>> RegisterAsync()
    {
        Services.AddLogging();
        Services.AddSingleton<ProcioneMGR.Services.Security.IEncryptionService, PassthroughEncryption>();
        Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        Services.AddSingleton(new ModelRegistryOptions());
        Services.AddSingleton<IModelRegistry, ModelRegistry>(); // il registry VERO: è la catena che si vuole provare

        var auth = AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles(AppRoles.Admin);

        var dbFactory = Services.BuildServiceProvider().GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return dbFactory;
    }

    private static async Task<int> SeedModelAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, ModelStage stage,
        string? retiredReason = null, double? dsr = 0.90,
        int? dsrTrials = 500, string? dsrSource = SavedMlModel.DsrSourcePipeline)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var model = new SavedMlModel
        {
            UserId = user.Id, Name = "RF momentum BTC 1h", ModelType = "RandomForest",
            Symbol = "BTCUSDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [1],
            DeflatedSharpe = dsr, Stage = stage,
            DeflatedSharpeTrials = dsr is null ? null : dsrTrials,
            // [M2b] La provenienza è INDIPENDENTE dal numero: «validato e scartato» e «mai proposto»
            // sono fatti che esistono proprio quando un DSR non c'è. Legarla al DSR, come faceva la
            // prima versione di questo helper, rendeva impossibile provare i due casi nuovi.
            DeflatedSharpeSource = dsrSource,
            RetiredReason = retiredReason,
            RetiredAtUtc = retiredReason is null ? null : DateTime.UtcNow.AddHours(-3),
        };
        db.SavedMlModels.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private static async Task<ModelStage> StageOfAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.SavedMlModels.AsNoTracking().FirstAsync(m => m.Id == id)).Stage;
    }

    private static IElement Button(IRenderedComponent<ProcioneMGR.Components.Pages.Registry> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.OrdinalIgnoreCase));

    private static bool HasButton(IRenderedComponent<ProcioneMGR.Components.Pages.Registry> cut, string text) =>
        cut.FindAll("button").Any(b => b.TextContent.Contains(text, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task ModelloRitirato_OffreIlRientro_ConIlMotivoDelRitiroSottoGliOcchi()
    {
        // Il cuore del difetto: prima, la cella delle azioni di una riga Retired era VUOTA.
        var db = await RegisterAsync();
        var id = await SeedModelAsync(db, ModelStage.Retired, "drift: 3 feature in alert (Mom1, Rsi14, Atr)");

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("RF momentum BTC 1h", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.True(HasButton(cut, "Riporta in Staging"));
        Assert.Contains("drift: 3 feature in alert", cut.Markup);
        Assert.Equal(ModelStage.Retired, await StageOfAsync(db, id)); // il solo rendering non tocca nulla
    }

    [Fact]
    public async Task Rientro_ChiedeConfermaCitandoIlMotivo_EDichiaraIRischiPrimaDiAgire()
    {
        var db = await RegisterAsync();
        var id = await SeedModelAsync(db, ModelStage.Retired, "drift: 3 feature in alert (Mom1, Rsi14, Atr)");

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Riporta in Staging")), TimeSpan.FromSeconds(10));
        Button(cut, "Riporta in Staging").Click();

        // Primo clic: chiede, NON agisce.
        Assert.Equal(ModelStage.Retired, await StageOfAsync(db, id));
        Assert.Contains("drift: 3 feature in alert", cut.Markup);          // il motivo che si sta scavalcando
        Assert.Contains("Torna <strong>solo eleggibile</strong>", cut.Markup);
        Assert.Contains("ri-ritirarlo", cut.Markup);                       // il drift può rifarlo
        Assert.Contains("prima</strong> del ritiro", cut.Markup);          // il DSR è una misura vecchia

        // Si aspetta il BANNER (il DOM è sincrono), poi si rilegge il database: la prova che il
        // pulsante ha fatto la cosa, non solo che è tornato.
        Button(cut, "Sì, riporta in Staging").Click();
        cut.WaitForAssertion(() => Assert.Contains("Riportato in Staging", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Equal(ModelStage.Staging, await StageOfAsync(db, id));
        Assert.Contains("alert-success", cut.Markup); // esito positivo detto col colore giusto
    }

    [Fact]
    public async Task Ritira_NonAgisceAlPrimoClic_EDichiaraCosaCosta()
    {
        // La regressione da impedire: prima, questo singolo clic ritirava il modello senza domande.
        var db = await RegisterAsync();
        var id = await SeedModelAsync(db, ModelStage.Champion);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Ritira")), TimeSpan.FromSeconds(10));
        Button(cut, "Ritira").Click();

        Assert.Equal(ModelStage.Champion, await StageOfAsync(db, id));     // ancora vivo
        Assert.Contains("Ritirare «RF momentum BTC 1h»?", cut.Markup);
        Assert.Contains("senza modello", cut.Markup);                      // il costo per le corsie
        Assert.Contains("ri-superare il gate DSR", cut.Markup);            // il costo del rientro
    }

    [Fact]
    public async Task Ritira_AlSecondoClic_SalvaIlMotivoScrittoDallOperatore()
    {
        var db = await RegisterAsync();
        var id = await SeedModelAsync(db, ModelStage.Challenger);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Ritira")), TimeSpan.FromSeconds(10));
        Button(cut, "Ritira").Click();
        // `oninput` e non `onchange`: il pulsante di conferma è disabilitato finché il motivo è
        // vuoto, e con onchange l'abilitazione arriverebbe solo all'uscita dal campo.
        cut.Find("input.form-control").Input("sospetto look-ahead sulle feature");
        Button(cut, "Sì, ritira").Click();

        cut.WaitForAssertion(() => Assert.Contains("Modello ritirato", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Equal(ModelStage.Retired, await StageOfAsync(db, id));
        await using var check = await db.CreateDbContextAsync();
        var m = await check.SavedMlModels.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal("sospetto look-ahead sulle feature", m.RetiredReason);
    }

    /// <summary>Cambia lo stadio a database sotto la pagina: e' l'altra scheda, o il ciclo drift.</summary>
    private static async Task MutaStadioDiNascostoAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, int id, ModelStage stage, string? reason = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var m = await db.SavedMlModels.FirstAsync(x => x.Id == id);
        m.Stage = stage;
        if (reason is not null) { m.RetiredReason = reason; m.RetiredAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RigaStantia_IlBottoneChallengerDiceDiNo_InveceDiMentire()
    {
        // La pagina mostra ancora `Staging` (la sua lista e' una fotografia), ma il database e' andato
        // avanti. Prima il banner era verde a prescindere: «Promosso a Challenger» su un no-op.
        var db = await RegisterAsync();
        var id = await SeedModelAsync(db, ModelStage.Staging);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "→ Challenger")), TimeSpan.FromSeconds(10));
        await MutaStadioDiNascostoAsync(db, id, ModelStage.Retired, "ritirato da un'altra scheda");

        Button(cut, "→ Challenger").Click();

        cut.WaitForAssertion(() => Assert.Contains("alert-warning", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("riportalo prima in Staging", cut.Markup);   // e indica la via d'uscita vera
        Assert.DoesNotContain("alert-success", cut.Markup);
        Assert.Equal(ModelStage.Retired, await StageOfAsync(db, id));
    }

    [Fact]
    public async Task RigaStantia_IlSecondoClicDiRitiro_NonCancellaLaDiagnosiDelDrift()
    {
        // Conferma di ritiro aperta su un Champion; nel frattempo il ciclo drift lo ritira. Prima il
        // secondo clic sovrascriveva «drift: …» col motivo manuale e diceva verde.
        var db = await RegisterAsync();
        var id = await SeedModelAsync(db, ModelStage.Champion);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.True(HasButton(cut, "Ritira")), TimeSpan.FromSeconds(10));
        Button(cut, "Ritira").Click();                                   // conferma armata
        await MutaStadioDiNascostoAsync(db, id, ModelStage.Retired, "drift: 3 feature in alert (Mom1, Rsi14, Atr)");

        cut.Find("input.form-control").Input("Ritirato manualmente dalla UI.");
        Button(cut, "Sì, ritira").Click();

        cut.WaitForAssertion(() => Assert.Contains("alert-warning", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("già ritirato", cut.Markup);
        await using var check = await db.CreateDbContextAsync();
        var m = await check.SavedMlModels.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal("drift: 3 feature in alert (Mom1, Rsi14, Atr)", m.RetiredReason); // la diagnosi resta
    }

    [Fact]
    public async Task ModelloRientrato_MostraIlMotivoComeSTORIA_NonComeStatoAttuale()
    {
        // Regola 5: mai un valore vecchio esibito come attuale. Prima la colonna Note rendeva per
        // sola nullità del campo, quindi un modello riportato in Staging avrebbe mostrato il motivo
        // del ritiro accanto a un badge vivo, indistinguibile da un ritiro in corso.
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Staging, "drift: 3 feature in alert (Mom1, Rsi14, Atr)");

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("drift: 3 feature in alert", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("già ritirato", cut.Markup);        // qualificato come passato
        Assert.Contains("Storia, non stato attuale", cut.Markup);
        Assert.False(HasButton(cut, "Riporta in Staging")); // non è ritirato: niente rientro da offrire
    }

    // ---------------------------------------------- [M2, 2026-08-20] la provenienza del DSR in UI

    /// <summary>
    /// Il DSR della pipeline dichiara il proprio metro accanto al numero. Senza questa colonna due
    /// valori incomparabili — /ml a N=1 e pipeline a N=800 — si leggono come una classifica.
    /// </summary>
    [Fact]
    public async Task DsrDellaPipeline_DichiaraIlProprioMetro()
    {
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Champion, dsr: 0.60, dsrTrials: 800,
            dsrSource: SavedMlModel.DsrSourcePipeline);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("Misurato su", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("pipeline · N=800", cut.Markup);
        Assert.Contains("slippage", cut.Markup);   // il tooltip spiega COSA rende il metro severo
    }

    /// <summary>
    /// Il DSR di /ml nasce da un solo track: N = 1 significa nessuna deflazione. Il badge lo dice,
    /// e la guida della pagina spiega perché quel numero risulta sistematicamente più alto.
    /// </summary>
    [Fact]
    public async Task DsrDiMlLab_DichiaraCheNonEDeflazionato()
    {
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Staging, dsr: 0.97, dsrTrials: 1,
            dsrSource: SavedMlModel.DsrSourceMlLab);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("ml-lab · N=1", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("nessuna deflazione", cut.Markup);
        Assert.Contains("sistematicamente più alto", cut.Markup);

        // La guida nasce chiusa: si apre come farebbe l'operatore, e deve dichiarare il rifiuto del
        // gate. Senza questa parte, il badge direbbe la provenienza senza dire che cosa comporta.
        cut.Find(".guida-panel-header").Click();
        Assert.Contains("la promozione viene", cut.Markup);
        Assert.Contains("ordine di grandezza", cut.Markup);
    }

    /// <summary>
    /// Le righe misurate prima del 2026-08-20 non dichiarano N: mostrare il DSR come se fosse
    /// confrontabile sarebbe la solita rassicurazione. Il badge dice «metro ignoto», che è la
    /// verità e la ragione per cui il gate di promozione le rifiuta.
    /// </summary>
    [Fact]
    public async Task DsrStorico_SenzaProvenienza_EDichiaratoNonConfrontabile()
    {
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Staging, dsr: 0.95, dsrTrials: null, dsrSource: null);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("metro ignoto", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("N=", cut.Markup);
    }

    // ------------------- [M2b/M2c] senza DSR ci sono TRE fatti diversi, non un trattino

    /// <summary>
    /// Validato e bocciato prima del gate DSR: è il caso dei 50 modelli che la pipeline aveva
    /// giudicato e scartato, con Sharpe holdout fra −1 e −62. Un trattino li faceva sembrare
    /// «in attesa di valutazione».
    /// </summary>
    [Fact]
    public async Task ModelloValidatoEScartato_DichiaraCheIlGiudizioCE()
    {
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Staging, dsr: null,
            dsrSource: SavedMlModel.DsrSourceRejectedBeforeGate);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("scartato prima del gate", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("valutato e bocciato", cut.Markup);   // il tooltip toglie l'ambiguità
        Assert.DoesNotContain("mai proposto", cut.Markup);
    }

    /// <summary>
    /// Mai diventato candidato: il caso dei 114 modelli su 164, quelli che il gate di correlazione
    /// ha messo da parte prima ancora di provarli.
    /// </summary>
    [Fact]
    public async Task ModelloMaiProposto_EDistintoDaQuelloBocciato()
    {
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Staging, dsr: null,
            dsrSource: SavedMlModel.DsrSourceNeverValidated);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("mai proposto", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.Contains("minTestCorrelation", cut.Markup);
        Assert.DoesNotContain("scartato prima del gate", cut.Markup);
    }

    /// <summary>
    /// Il trattino sopravvive per un solo caso: nessun esito registrato. Senza questo test, i due
    /// badge nuovi potrebbero finire per coprire anche l'ignoranza — che è il difetto che chiudono.
    /// </summary>
    [Fact]
    public async Task SenzaDsrESenzaProvenienza_RestaIlTrattino()
    {
        var db = await RegisterAsync();
        await SeedModelAsync(db, ModelStage.Staging, dsr: null, dsrSource: null);

        var cut = Render<ProcioneMGR.Components.Pages.Registry>();
        cut.WaitForAssertion(() => Assert.Contains("Misurato su", cut.Markup), TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("mai proposto", cut.Markup);
        Assert.DoesNotContain("scartato prima del gate", cut.Markup);
        Assert.Contains("Nessun esito di validazione registrato", cut.Markup);   // il tooltip del trattino dice «non so»
    }
}
