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
    public async Task InvalidJson_ThrowsWithoutDestroyingFile()
    {
        await File.WriteAllTextAsync(SettingsPath, "{ NON-json ");

        await Assert.ThrowsAnyAsync<Exception>(() => Writer().SaveSectionAsync("Drift", new SampleOptions()));

        // Il file NON è stato toccato (la parse fallisce prima della scrittura).
        Assert.Equal("{ NON-json ", await File.ReadAllTextAsync(SettingsPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
