using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Regressione del bug adiacente a H1: <see cref="SafetyConfigWriter.SaveAsync"/> riscriveva
/// <c>Trading:Safety</c> con un elenco di 7 chiavi scritto a mano — ogni salvataggio dal pannello
/// riportava SILENZIOSAMENTE ai default le proprietà dimenticate (MaxLeverageAllowed,
/// MaintenanceMarginPercent, UseExchangeRestingStops). Il fix serializza l'INTERO oggetto:
/// per costruzione una proprietà nuova non può più essere persa. Il test enumera le proprietà
/// via reflection, così anche QUESTO test non va aggiornato a mano quando se ne aggiungono.
/// </summary>
public sealed class SafetyConfigWriterTests : IDisposable
{
    private readonly string _dir;

    public SafetyConfigWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "procione-safetywriter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private sealed class FakeEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ProcioneMGR.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private SafetyConfigWriter Writer() => new(new FakeEnv(_dir), NullLogger<SafetyConfigWriter>.Instance);

    private string SettingsPath => Path.Combine(_dir, "appsettings.json");

    [Fact]
    public async Task SaveAsync_WritesEveryPublicProperty_AndPreservesSiblingSections()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            {
              "ConnectionStrings": { "DefaultConnection": "Host=localhost" },
              "Trading": {
                "Safety": { "MaxPositionSizePercent": 10.0 },
                "LiveExecution": { "Enabled": false, "DefaultWindowMinutes": 5 }
              },
              "Llm": { "Enabled": false }
            }
            """);

        await Writer().SaveAsync(new SafetyConfiguration { MaxLeverageAllowed = 10 });

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        var safetyNode = root["Trading"]!["Safety"]!.AsObject();

        // OGNI proprietà pubblica di SafetyConfiguration deve essere presente nel JSON scritto:
        // la versione a elenco manuale ne perdeva 3 (il bug), e ne avrebbe perse di future.
        foreach (var prop in typeof(SafetyConfiguration).GetProperties())
        {
            Assert.True(safetyNode.ContainsKey(prop.Name), $"proprietà '{prop.Name}' assente dopo SaveAsync");
        }

        // Le sezioni sorelle e le altre radici NON vengono toccate.
        Assert.Equal("Host=localhost", (string?)root["ConnectionStrings"]!["DefaultConnection"]);
        Assert.Equal(5, (int?)root["Trading"]!["LiveExecution"]!["DefaultWindowMinutes"]);
        Assert.False((bool?)root["Llm"]!["Enabled"]);
    }

    [Fact]
    public async Task SaveAsync_NonDefaultValues_SurviveTheRoundtrip()
    {
        // Il sintomo del bug: MaxLeverageAllowed=10 salvato dal pannello, e al reload era di
        // nuovo 5 (default) perché la chiave non veniva mai scritta. Roundtrip completo:
        // SaveAsync → file → ConfigurationBinder → oggetto.
        await File.WriteAllTextAsync(SettingsPath, """{ "Trading": { "Safety": {} } }""");

        var saved = new SafetyConfiguration
        {
            MaxLeverageAllowed = 10,
            MaintenanceMarginPercent = 1.25m,
            UseExchangeRestingStops = true,
            PositionSizePercent = 6.5m,
            MaxPositionSizePercent = 33m,
        };
        await Writer().SaveAsync(saved);

        var config = new ConfigurationBuilder().AddJsonFile(SettingsPath).Build();
        var reloaded = config.GetSection("Trading:Safety").Get<SafetyConfiguration>()!;

        Assert.Equal(10, reloaded.MaxLeverageAllowed);
        Assert.Equal(1.25m, reloaded.MaintenanceMarginPercent);
        Assert.True(reloaded.UseExchangeRestingStops);
        Assert.Equal(6.5m, reloaded.PositionSizePercent);
        Assert.Equal(33m, reloaded.MaxPositionSizePercent);
    }

    [Fact]
    public async Task SaveAsync_MissingTradingSection_CreatesIt()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "Logging": { "LogLevel": { "Default": "Information" } } }""");

        await Writer().SaveAsync(new SafetyConfiguration());

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();
        Assert.NotNull(root["Trading"]?["Safety"]?["MaxPositionSizePercent"]);
        Assert.Equal("Information", (string?)root["Logging"]!["LogLevel"]!["Default"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
