using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il canale con cui il guscio legge e riscrive la configurazione DEL MOTORE (2026-07-29).
///
/// <para>Perché esiste: il disegno originale faceva condividere ai due processi un solo
/// <c>appsettings.json</c> su PVC. Verificato dal vivo che non regge col guscio fuori dal cluster —
/// il file era rimasto a <c>{}</c> e ogni soglia mostrata in UI era quella del guscio, non quella
/// applicata dal motore.</para>
///
/// <para>Questi test difendono soprattutto il CONFINE: <c>SetEngineConfig</c> scrive su un processo
/// che firma ordini veri, quindi ciò che conta non è che funzioni ma che <b>non funzioni su tutto
/// il resto</b>.</para>
/// </summary>
public sealed class EngineConfigTests : IDisposable
{
    private readonly string _dir;

    public EngineConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "procione-engineconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ProcioneMGR.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private string SettingsPath => Path.Combine(_dir, "appsettings.json");

    private EngineConfigService Build(string? settingsJson = null, Dictionary<string, string?>? env = null)
    {
        File.WriteAllText(SettingsPath, settingsJson ?? "{}");
        var builder = new ConfigurationBuilder().AddJsonFile(SettingsPath, optional: false);
        if (env is not null) builder.AddInMemoryCollection(env); // simula le env: vincono sul file
        var configuration = builder.Build();

        var env2 = new FakeEnv(_dir);
        return new EngineConfigService(
            configuration,
            new AppConfigWriter(env2, NullLogger<AppConfigWriter>.Instance),
            env2,
            NullLogger<EngineConfigService>.Instance);
    }

    // --- IL CONFINE ------------------------------------------------------------------------------

    [Theory]
    [InlineData("ConnectionStrings")]
    [InlineData("ConnectionStrings:PostgresConnection")]
    [InlineData("Security")]
    [InlineData("Security:MasterKey")]
    [InlineData("Trading:GrpcSharedSecret")]
    [InlineData("Trading:RemoteUrl")]
    [InlineData("Llm")]
    public void Secrets_AreNeitherReadableNorWritable(string section)
    {
        // Non basta che non siano nell'elenco degli scrivibili: devono essere IRRAGGIUNGIBILI anche
        // in lettura, così un domani che qualcuno allarghi la lettura non le trascina dentro.
        Assert.False(EngineConfigSections.IsReadable(section), $"'{section}' non deve essere leggibile.");
        Assert.False(EngineConfigSections.IsWritable(section), $"'{section}' non deve essere scrivibile.");
    }

    [Fact]
    public void EngineNotifications_AreConfigurable_ButTheTokenNeverTravels()
    {
        // Il motore emette gli allarmi di quarantena: lasciarli non configurabili avrebbe replicato
        // il punto cieco che ha tenuto Telegram muto per due giorni, un processo più in là.
        Assert.True(EngineConfigSections.IsWritable("Notifications"));

        // Ma il TOKEN non è in questa sezione e non passa da qui: vive solo in TELEGRAM_BOT_TOKEN.
        // Il POCO lo dimostra — se un domani qualcuno ce lo aggiungesse, questo test cade.
        var properties = typeof(ProcioneMGR.Services.Notifications.NotificationOptions)
            .GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(properties, p => p.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Trading:UseRemoteTrading")]
    [InlineData("Trading:LaneCount")]
    public void TopologySections_AreReadable_ButNeverWritable(string section)
    {
        // Sono fatti che l'operatore ha diritto di vedere, e che cambiano col deploy: col valore
        // sbagliato si ottengono due esecutori sulla stessa corsia, o nessuno.
        Assert.True(EngineConfigSections.IsReadable(section));
        Assert.False(EngineConfigSections.IsWritable(section));
    }

    [Fact]
    public void SimilarlyNamedSections_AreNotConfusedWithForbiddenOnes()
    {
        // Il controllo dei prefissi è per SEGMENTO: "Securities" non è "Security".
        Assert.False(EngineConfigSections.IsWritable("Securities"));   // comunque non in allow-list
        Assert.True(EngineConfigSections.IsWritable("Carry"));
        Assert.False(EngineConfigSections.IsWritable("CarryOther"));
    }

    [Fact]
    public void EveryWritableSection_IsAlsoReadable()
    {
        foreach (var section in EngineConfigSections.Writable)
        {
            Assert.True(EngineConfigSections.IsReadable(section), $"'{section}' scrivibile ma non leggibile.");
        }
    }

    /// <summary>
    /// [2026-08-17] Ogni sezione scrivibile deve avere un TIPO registrato, e il test lo verifica dal
    /// COMPORTAMENTO invece che dal dizionario privato.
    ///
    /// <para>Il difetto che chiude: <c>Trading:Bitget</c> era nell'allow-list di scrittura ma non
    /// nella mappa dei tipi. In lettura cadeva sul ramo pensato per le sezioni scalari, dove
    /// <c>configuration["Trading:Bitget"]</c> su una sezione-oggetto vale null: la sezione viaggiava
    /// come la stringa <c>"null"</c> e il pannello dell'attestazione mostrava SEMPRE «non
    /// verificata», anche col motore che stava lasciando passare i market-buy spot — un badge che
    /// sottostima il rischio in corso. In scrittura il Salva superava l'allow-list e moriva sul
    /// lookup del tipo, quindi l'attestazione non è mai stata impostabile dalla UI.</para>
    /// </summary>
    [Fact]
    public void EveryWritableSection_IsSerializedAsAnObject_NotAsTheScalarFallback()
    {
        var service = Build();

        var muti = new List<string>();
        foreach (var section in EngineConfigSections.Writable)
        {
            var json = service.Read([section]).SingleOrDefault()?.Json?.TrimStart();
            if (json is null || !json.StartsWith('{')) muti.Add($"{section} → {json ?? "(assente)"}");
        }

        Assert.True(muti.Count == 0,
            "Sezioni scrivibili lette come scalari invece che come oggetti (manca il tipo in "
            + "EngineConfigService.SectionTypes): " + string.Join("; ", muti));
    }

    [Fact]
    public async Task Write_OnASectionOutsideTheAllowList_IsRefused()
    {
        var service = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WriteAsync("ConnectionStrings", """{"PostgresConnection":"Host=cattivo"}"""));

        Assert.Contains("non scrivibile", ex.Message);
        // E il file non è stato toccato.
        Assert.Equal("{}", File.ReadAllText(SettingsPath));
    }

    // --- LETTURA -------------------------------------------------------------------------------

    [Fact]
    public void Read_ReturnsCodeDefaults_ForKeysAbsentFromTheFile()
    {
        // È il punto: il motore APPLICA i default del costruttore per le chiavi assenti. Una lettura
        // grezza mostrerebbe un buco, facendo credere "non configurato" ciò che è configuratissimo.
        var service = Build("{}");

        var sections = service.Read(["MarketData:Realtime"]);

        var realtime = Assert.Single(sections);
        var parsed = JsonSerializer.Deserialize<RealtimeFeedOptions>(realtime.Json)!;
        Assert.False(parsed.Enabled);                    // default del codice
        Assert.False(parsed.DriveProtectiveExits);       // default del codice [C-02: false per misura B3, vedi SecurityDefaultsTests]
        Assert.Equal(60, parsed.StaleAfterSeconds);
        Assert.Equal("default del codice", realtime.Source);
    }

    [Fact]
    public void Read_ReflectsTheFile_WhenTheSectionExists()
    {
        var service = Build("""{ "MarketData": { "Realtime": { "Enabled": true, "StaleAfterSeconds": 42 } } }""");

        var parsed = JsonSerializer.Deserialize<RealtimeFeedOptions>(
            service.Read(["MarketData:Realtime"]).Single().Json)!;

        Assert.True(parsed.Enabled);
        Assert.Equal(42, parsed.StaleAfterSeconds);
    }

    [Fact]
    public void Read_SkipsForbiddenSections_InsteadOfFailingTheWholeScreen()
    {
        var service = Build();

        var sections = service.Read(["Carry", "Security:MasterKey", "ConnectionStrings"]);

        Assert.Single(sections);
        Assert.Equal("Carry", sections[0].Path);
    }

    [Fact]
    public void Read_WithNoSectionsRequested_ReturnsEveryKnownOne()
    {
        var service = Build();

        var paths = service.Read().Select(s => s.Path).ToList();

        Assert.Equal(EngineConfigSections.AllReadable().Count(), paths.Count);
        Assert.Contains("Trading:Safety", paths);
        Assert.Contains("Carry", paths);
    }

    // --- SCRITTURA -----------------------------------------------------------------------------

    [Fact]
    public async Task Write_PersistsTheSection_AndReturnsItReread()
    {
        var service = Build();

        var result = await service.WriteAsync("Carry",
            JsonSerializer.Serialize(new CarryOptions { Enabled = true, EnterAnnualFundingPercent = 12m }));

        Assert.NotNull(result.AppliedJson);
        var onDisk = JsonSerializer.Deserialize<CarryOptions>(
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!["Carry"]!.ToJsonString())!;
        Assert.True(onDisk.Enabled);
        Assert.Equal(12m, onDisk.EnterAnnualFundingPercent);
    }

    [Fact]
    public async Task Write_AppliesTheSameValidationAsThePanels()
    {
        // Un valore rifiutato dalla UI non deve poter entrare da questa porta: le soglie del carry
        // invertite aprirebbero e chiuderebbero nella stessa valutazione.
        var service = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.WriteAsync("Carry",
            JsonSerializer.Serialize(new CarryOptions { EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 9m })));

        Assert.Contains("isteresi", ex.Message);
    }

    [Fact]
    public async Task Write_RejectsMalformedJson_WithAReadableMessage()
    {
        var service = Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.WriteAsync("Carry", "{ questo non è json"));

        Assert.Contains("JSON non valido", ex.Message);
    }

    [Fact]
    public async Task Write_WarnsWhenAnotherProviderWillKeepWinningOverTheFile()
    {
        // Il caso Kubernetes: la ConfigMap del deployment definisce Carry__Enabled, quindi scrivere
        // il file riesce e non cambia nulla. Tacerlo sarebbe la stessa bugia che questo lavoro
        // corregge — il salvataggio "riesce" e l'operatore crede di aver agito.
        //
        // Il rilevamento NON riconosce il provider dal nome ("Environment"): guarda se chi ha
        // l'ultima parola è il file su cui si è appena scritto. Per questo il test può simulare la
        // ConfigMap con un provider in memoria e restare fedele al meccanismo reale.
        var service = Build(env: new Dictionary<string, string?> { ["Carry:Enabled"] = "true" });

        var result = await service.WriteAsync("Carry", JsonSerializer.Serialize(new CarryOptions { Enabled = false }));

        Assert.NotNull(result.Warning);
        Assert.Contains("Enabled", result.Warning);
        Assert.Contains("precedenza", result.Warning);
    }

    [Fact]
    public async Task Write_DoesNotWarn_WhenTheFileIsTheOnlySource()
    {
        var service = Build();

        var result = await service.WriteAsync("Carry", JsonSerializer.Serialize(new CarryOptions()));

        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task Write_MakesTheNewValueVisibleImmediately_WithoutRelyingOnAFileWatcher()
    {
        // IL difetto trovato dal vivo il 2026-07-29 dentro il pod: il file conteneva
        // Realtime:Enabled=false e il motore continuava a rispondere true, perché reloadOnChange si
        // appoggia a inotify e inotify NON attraversa il mount di un PVC. Configurazione scritta e
        // non applicata: per chi guarda il pannello è identico al non aver salvato.
        //
        // Il test costruisce la configurazione SENZA reloadOnChange, così il file watcher non può
        // "salvare" il risultato per caso: se WriteAsync non ricarica esplicitamente, la rilettura
        // restituisce il valore vecchio e il test fallisce — che è esattamente ciò che accadeva.
        var service = Build("""{ "MarketData": { "Realtime": { "Enabled": true } } }""");

        var result = await service.WriteAsync("MarketData:Realtime",
            JsonSerializer.Serialize(new RealtimeFeedOptions { Enabled = false }));

        var applied = JsonSerializer.Deserialize<RealtimeFeedOptions>(result.AppliedJson)!;
        Assert.False(applied.Enabled);

        // E una lettura successiva, indipendente, vede lo stesso valore.
        var reread = JsonSerializer.Deserialize<RealtimeFeedOptions>(
            service.Read(["MarketData:Realtime"]).Single().Json)!;
        Assert.False(reread.Enabled);
    }

    [Fact]
    public async Task Write_PreservesSiblingSections()
    {
        var service = Build("""{ "Trading": { "Safety": { "MaxLeverageAllowed": 5 } }, "Carry": { "Enabled": false } }""");

        await service.WriteAsync("Carry", JsonSerializer.Serialize(new CarryOptions { Enabled = true }));

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(5, root["Trading"]!["Safety"]!["MaxLeverageAllowed"]!.GetValue<int>());
    }
}
