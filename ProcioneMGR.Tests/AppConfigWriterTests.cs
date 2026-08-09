using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Config;

namespace ProcioneMGR.Tests;

/// <summary>
/// <see cref="AppConfigWriter"/> è il writer generalizzato dietro i pannelli /trading e
/// /admin/autonomy: un bug qui corrompe appsettings.json per TUTTE le sezioni. I contratti chiave:
/// scrive l'intera sezione (nessuna chiave persa per costruzione), non tocca le sezioni sorelle,
/// crea i path mancanti, preserva le chiavi di documentazione "_comment*".
/// </summary>
public sealed class AppConfigWriterTests : IDisposable
{
    private readonly string _dir;

    public AppConfigWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "procione-appconfigwriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private sealed class FakeEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ProcioneMGR.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private AppConfigWriter Writer() => new(new FakeEnv(_dir), NullLogger<AppConfigWriter>.Instance);

    private string SettingsPath => Path.Combine(_dir, "appsettings.json");

    private sealed class SampleOptions
    {
        public bool Enabled { get; set; }
        public int IntervalHours { get; set; } = 6;
        public string Label { get; set; } = "default";
    }

    [Fact]
    public async Task Roundtrip_WritesAllProperties_AndSiblingSectionsSurvive()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            {
              "Drift": { "Enabled": false },
              "Llm": { "Enabled": true, "Model": "claude-opus-4-8" },
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """);

        await Writer().SaveSectionAsync("Drift", new SampleOptions { Enabled = true, IntervalHours = 12, Label = "x" });

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        var drift = root["Drift"]!.AsObject();
        Assert.True(drift["Enabled"]!.GetValue<bool>());
        Assert.Equal(12, drift["IntervalHours"]!.GetValue<int>());
        Assert.Equal("x", drift["Label"]!.GetValue<string>());

        // Le sezioni sorelle sono INTATTE (read-modify-write, non riscrittura da zero).
        Assert.Equal("claude-opus-4-8", root["Llm"]!["Model"]!.GetValue<string>());
        Assert.Equal("Information", root["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public async Task NestedPath_CreatesMissingNodes()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "AllowedHosts": "*" }""");

        await Writer().SaveSectionAsync("Trading:LiveExecution", new SampleOptions { Enabled = true });

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        Assert.True(root["Trading"]!["LiveExecution"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("*", root["AllowedHosts"]!.GetValue<string>()); // sorella intatta
    }

    [Fact]
    public async Task NestedPath_DoesNotClobberSiblingSubsections()
    {
        // Il caso reale: Trading contiene Safety E LiveExecution — salvare una NON deve toccare l'altra.
        await File.WriteAllTextAsync(SettingsPath, """
            {
              "Trading": {
                "Safety": { "MaxPositionSizePercent": 10.0, "MaxLeverageAllowed": 5 },
                "LiveExecution": { "Enabled": false }
              }
            }
            """);

        await Writer().SaveSectionAsync("Trading:LiveExecution", new SampleOptions { Enabled = true });

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        Assert.True(root["Trading"]!["LiveExecution"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(5, root["Trading"]!["Safety"]!["MaxLeverageAllowed"]!.GetValue<int>());
    }

    [Fact]
    public async Task CommentKeys_ArePreserved()
    {
        // Le chiavi "_comment*" sono documentazione per chi apre il file: la sovrascrittura
        // della sezione non deve mangiarsele (il template ne fa largo uso).
        await File.WriteAllTextAsync(SettingsPath, """
            {
              "Llm": {
                "_comment": "La API key NON va qui: solo env ANTHROPIC_API_KEY.",
                "Enabled": false,
                "Model": "claude-opus-4-8"
              }
            }
            """);

        await Writer().SaveSectionAsync("Llm", new SampleOptions { Enabled = true, Label = "nuovo" });

        var llm = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!["Llm"]!.AsObject();
        Assert.Equal("La API key NON va qui: solo env ANTHROPIC_API_KEY.", llm["_comment"]!.GetValue<string>());
        Assert.True(llm["Enabled"]!.GetValue<bool>());
        Assert.Equal("nuovo", llm["Label"]!.GetValue<string>());
    }

    [Fact]
    public async Task ParentSection_KeepsNestedSubsectionsThePocoDoesNotModel()
    {
        // IL CASO REALE (audit 2026-07-29). Il pannello "Sync dati" di /admin/autonomy salva la
        // sezione MarketData con un POCO di tre scalari; MarketData:Realtime — l'intera
        // configurazione del feed WebSocket, compreso se i tick possono CHIUDERE posizioni — è una
        // sottosezione che quel POCO non conosce. Prima del fix il salvataggio la cancellava, e il
        // feed tornava ai default senza che nessuno lo dicesse.
        await File.WriteAllTextAsync(SettingsPath, """
            {
              "MarketData": {
                "Enabled": true,
                "SyncIntervalMinutes": 5,
                "Realtime": { "Enabled": true, "DriveProtectiveExits": false, "MaxSpreadPercent": 2 }
              }
            }
            """);

        await Writer().SaveSectionAsync("MarketData", new SampleOptions { Enabled = false, IntervalHours = 9 });

        var marketData = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!["MarketData"]!.AsObject();
        Assert.False(marketData["Enabled"]!.GetValue<bool>());          // il POCO ha scritto
        Assert.Equal(9, marketData["IntervalHours"]!.GetValue<int>());  // il POCO ha scritto

        var realtime = marketData["Realtime"]!.AsObject();              // la sottosezione è SOPRAVVISSUTA
        Assert.True(realtime["Enabled"]!.GetValue<bool>());
        Assert.False(realtime["DriveProtectiveExits"]!.GetValue<bool>());
        Assert.Equal(2, realtime["MaxSpreadPercent"]!.GetValue<int>());
    }

    private sealed class OptionsWithNested
    {
        public bool Enabled { get; set; }
        public NestedChild? Child { get; set; }
    }

    private sealed class NestedChild
    {
        public int Value { get; set; }
    }

    [Fact]
    public async Task NestedObject_ThePocoDOESModel_IsOverwritten_NotPreserved()
    {
        // Il rovescio della medaglia: se la sottosezione è una PROPRIETÀ del POCO, il POCO è la sua
        // fonte di verità e deve poterla riscrivere per intero — anche azzerandola. Senza questa
        // distinzione, "preserva gli oggetti annidati" diventerebbe "certe proprietà non si possono
        // più cambiare dalla UI".
        await File.WriteAllTextAsync(SettingsPath, """
            { "Section": { "Enabled": false, "Child": { "Value": 42 } } }
            """);

        await Writer().SaveSectionAsync("Section", new OptionsWithNested { Enabled = true, Child = new NestedChild { Value = 7 } });

        var section = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!["Section"]!.AsObject();
        Assert.Equal(7, section["Child"]!["Value"]!.GetValue<int>());

        // E una proprietà annidata a null resta null: è comunque una chiave del payload.
        await Writer().SaveSectionAsync("Section", new OptionsWithNested { Enabled = true, Child = null });
        section = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!["Section"]!.AsObject();
        Assert.Null(section["Child"]?.GetValue<object?>());
    }

    private enum SampleMode { First, Second }

    private sealed class OptionsWithEnum
    {
        public SampleMode Mode { get; set; }
    }

    [Fact]
    public async Task Enums_AreWrittenByName_NotByOrdinal()
    {
        // Il binder accetterebbe anche l'ordinale, quindi non è correttezza: è che appsettings.json
        // lo legge anche un umano, e "Mode": 1 non dice nulla mentre "Second" sì.
        await File.WriteAllTextAsync(SettingsPath, """{ "AllowedHosts": "*" }""");

        await Writer().SaveSectionAsync("Execution", new OptionsWithEnum { Mode = SampleMode.Second });

        var mode = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!["Execution"]!["Mode"]!;
        Assert.Equal("Second", mode.GetValue<string>());
    }

    [Fact]
    public async Task InvalidJson_ThrowsWithoutDestroyingFile()
    {
        await File.WriteAllTextAsync(SettingsPath, "{ NON-json ");

        await Assert.ThrowsAnyAsync<Exception>(() => Writer().SaveSectionAsync("Drift", new SampleOptions()));

        // Il file NON è stato toccato (la parse fallisce prima della scrittura).
        Assert.Equal("{ NON-json ", await File.ReadAllTextAsync(SettingsPath));
    }

    // --- [Fase 3] SaveValueAsync: la scrittura chirurgica di un singolo scalare -----------------

    [Fact]
    public async Task SaveValue_TouchesOnlyTheTargetKey_SiblingScalarsAndObjectsSurvive()
    {
        // È la ragione per cui il metodo esiste: Trading contiene Safety (oggetto di un altro
        // pannello) e scalari come LaneCount — una scrittura di sezione qui richiederebbe un POCO
        // completo, e ogni proprietà dimenticata verrebbe cancellata.
        await File.WriteAllTextAsync(SettingsPath, """
            {
              "Trading": {
                "LaneCount": 8,
                "UseRemoteTrading": false,
                "Safety": { "MaxOpenPositions": 5 },
                "_comment": "doc per il lettore umano"
              }
            }
            """);

        await Writer().SaveValueAsync("Trading:UseRemoteTrading", true);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        var trading = root["Trading"]!.AsObject();
        Assert.True(trading["UseRemoteTrading"]!.GetValue<bool>());
        Assert.Equal(8, trading["LaneCount"]!.GetValue<int>());
        Assert.Equal(5, trading["Safety"]!["MaxOpenPositions"]!.GetValue<int>());
        Assert.Equal("doc per il lettore umano", trading["_comment"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveValue_CreatesMissingParents_AndWritesStrings()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "AllowedHosts": "*" }""");

        await Writer().SaveValueAsync("Ml:RemoteUrl", "http://procionemgr-ml:8080");

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        Assert.Equal("http://procionemgr-ml:8080", root["Ml"]!["RemoteUrl"]!.GetValue<string>());
        Assert.Equal("*", root["AllowedHosts"]!.GetValue<string>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
