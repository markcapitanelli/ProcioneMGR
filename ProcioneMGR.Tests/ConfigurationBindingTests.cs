using Microsoft.Extensions.Configuration;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Tests;

/// <summary>
/// Un refuso nel nome di una sezione di configurazione non rompe niente: lascia semplicemente la
/// funzione <b>spenta in silenzio</b>. È il guasto peggiore per una manopola di sicurezza, perché
/// l'operatore la vede scritta nel file e crede che sia attiva.
///
/// Questi test legano le sezioni <i>dal file d'esempio versionato</i>, non da JSON inventato qui:
/// così coprono anche il caso in cui il codice sia giusto e sia il file a essere sbagliato.
/// </summary>
public sealed class ConfigurationBindingTests
{
    /// <summary>Risale dalla cartella dei binari fino alla radice del repository (dove sta la .sln).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IConfigurationRoot ExampleConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "ProcioneMGR", "appsettings.json.example"), optional: false)
            .Build();

    [Fact]
    public void CorrelatedExposureSection_BindsFromTheShippedExample()
    {
        var options = new CorrelatedExposureOptions();
        ExampleConfiguration().GetSection("Trading:CorrelatedExposure").Bind(options);

        // Se il nome della sezione fosse sbagliato, Bind non fallirebbe: lascerebbe i default.
        // Ecco perché si verifica un valore CHE DIFFERISCE dal default.
        Assert.True(options.Enabled);
        Assert.Equal(30m, options.MaxCorrelatedExposurePercent);
        Assert.NotEqual(new CorrelatedExposureOptions().MaxCorrelatedExposurePercent, options.MaxCorrelatedExposurePercent);
        Assert.Equal("1h", options.Timeframe);
        Assert.Equal(720, options.LookbackBars);
    }

    [Fact]
    public void RegimeRoutingSection_BindsFromTheShippedExample_AndStaysInObservation()
    {
        var options = new RegimeRoutingOptions();
        ExampleConfiguration().GetSection("Trading:RegimeRouting").Bind(options);

        Assert.True(options.Enabled);

        // L'invariante che conta: acceso NON significa decidente. Se questo diventasse true per
        // distrazione, il router comincerebbe a spegnere strategie sulla base di regole scritte su
        // una manciata di trade — esattamente ciò che la modalità osservazione esiste per evitare.
        Assert.False(options.DriveDecisions);

        Assert.True(options.AllowUnmappedRegimes);
        Assert.Equal(60, options.MinCandles);
    }

    [Fact]
    public void RegimeRoutingRules_NameOnlyStrategiesThatExist()
    {
        // Una regola che nomina una strategia inesistente non viene mai soddisfatta: nel regime
        // corrispondente la corsia resterebbe ferma per sempre, e il motivo sarebbe invisibile.
        var options = new RegimeRoutingOptions();
        ExampleConfiguration().GetSection("Trading:RegimeRouting").Bind(options);

        var known = new ProcioneMGR.Services.Backtesting.StrategyFactory()
            .Prototypes.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in options.Rules)
        {
            foreach (var strategy in rule.Strategies)
            {
                Assert.True(known.Contains(strategy),
                    $"La regola sul regime {rule.RegimeId} nomina '{strategy}', che non esiste nel catalogo delle strategie.");
            }
        }
    }

    [Fact]
    public void HeritageGuardSection_BindsFromTheShippedExample()
    {
        var options = new ProcioneMGR.Services.Sentiment.SentimentOptions();
        ExampleConfiguration().GetSection("Sentiment").Bind(options);

        // FundingSymbols è VUOTA nel POCO (trappola del binder che appende ai default): trovarla
        // popolata è la prova che la sezione annidata si lega davvero — un refuso nel percorso
        // lascerebbe il guardiano sui default incorporati SENZA dirlo.
        Assert.True(options.HeritageGuard.Enabled);
        Assert.Equal(["BTC", "ETH", "SOL", "BNB", "XRP", "DOGE"], options.HeritageGuard.FundingSymbols);
        // 2020-10-01, non 2020-01-01: l'àncora deve stare DOPO il listing più tardo (SOL,
        // 2020-09-13) — trovato dal collaudo a browser con quattro serie complete marcate violate.
        Assert.Equal(new DateTime(2020, 10, 1), options.HeritageGuard.FundingMinStartUtc.Date);
        Assert.Equal(5000, options.HeritageGuard.FundingMinEventsPerSymbol);
        Assert.Equal(2019, options.HeritageGuard.FearGreedMinStartUtc.Year);
        Assert.True(options.HeritageGuard.LiquidationsEnforced);
        Assert.Equal(new DateTime(2026, 8, 1), options.HeritageGuard.LiquidationsMinStartUtc.Date);
    }
}
