using ProcioneMGR.Services.AltData;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="NewsImpactClassifier"/>: classificazione per categoria e rilevamento
/// simboli con confronto a WORD BOUNDARY — il caso critico è evitare falsi positivi da semplice
/// substring ("ban" dentro "banana", "sol" dentro "absolute", "ada" dentro "canada").
/// </summary>
public class NewsImpactClassifierTests
{
    [Theory]
    [InlineData("SEC sues major exchange over unregistered securities", NewsCategory.Regulatory)]
    [InlineData("New ETF approval expected this week", NewsCategory.Regulatory)]
    [InlineData("Exchange hacked, $50M stolen in exploit", NewsCategory.Security)]
    [InlineData("BlackRock reports record ETF inflows", NewsCategory.Institutional)]
    [InlineData("Local cafe now accepts crypto payments", NewsCategory.Other)]
    public void Classify_ReturnsExpectedCategory(string headline, NewsCategory expected)
    {
        Assert.Equal(expected, NewsImpactClassifier.Classify(headline, null));
    }

    [Fact]
    public void Classify_PrioritizesRegulatoryOverOther_WhenMultipleKeywordsPresent()
    {
        var category = NewsImpactClassifier.Classify("SEC investigates exchange hack", null);
        Assert.Equal(NewsCategory.Regulatory, category);
    }

    [Fact]
    public void DetectSymbols_FindsBitcoinAndEthereum()
    {
        var symbols = NewsImpactClassifier.DetectSymbols("Bitcoin and Ethereum rally as ETF inflows surge", null);
        Assert.Contains("BTC", symbols);
        Assert.Contains("ETH", symbols);
    }

    [Fact]
    public void DetectSymbols_UsesWordBoundary_NoFalsePositiveFromSubstring()
    {
        // "banana" contiene "ban", "absolute"/"resolve" contengono "sol", "canada" contiene "ada":
        // nessuno di questi deve far scattare un simbolo o una categoria.
        var symbols = NewsImpactClassifier.DetectSymbols("A banana absolute resolve canada adapter", null);
        Assert.DoesNotContain("SOL", symbols);
        Assert.DoesNotContain("ADA", symbols);

        var category = NewsImpactClassifier.Classify("The banana market remains stable", null);
        Assert.Equal(NewsCategory.Other, category);
    }

    [Fact]
    public void DetectSymbols_NoMatch_ReturnsEmpty()
    {
        var symbols = NewsImpactClassifier.DetectSymbols("Generic tech news with no crypto mention", null);
        Assert.Empty(symbols);
    }

    [Fact]
    public void Classify_IsCaseInsensitive()
    {
        Assert.Equal(NewsCategory.Security, NewsImpactClassifier.Classify("EXCHANGE HACKED OVERNIGHT", null));
    }

    [Theory]
    [InlineData("Fed holds rates steady, Powell signals hawkish stance", NewsCategory.CentralBanks)]
    [InlineData("ECB rate decision: Lagarde hints at further cuts", NewsCategory.CentralBanks)]
    [InlineData("US Non-Farm Payrolls beat expectations, unemployment falls", NewsCategory.Macro)]
    [InlineData("Eurozone CPI inflation rises above forecast", NewsCategory.Macro)]
    public void Classify_RecognizesForexMacroCategories(string headline, NewsCategory expected)
    {
        Assert.Equal(expected, NewsImpactClassifier.Classify(headline, null));
    }

    [Fact]
    public void Classify_PrioritizesCentralBanksOverMacro_WhenBothPresentButCentralBanksStronger()
    {
        // Due segnali CentralBanks ("fed", "powell") contro uno solo Macro ("inflation"): vince CentralBanks.
        var category = NewsImpactClassifier.Classify("Fed's Powell comments on inflation outlook", null);
        Assert.Equal(NewsCategory.CentralBanks, category);
    }

    [Fact]
    public void DetectSymbols_FindsForexMajorPairs()
    {
        var symbols = NewsImpactClassifier.DetectSymbols("EUR/USD rallies as GBP/USD consolidates near cable support", null);
        Assert.Contains("EURUSD", symbols);
        Assert.Contains("GBPUSD", symbols);
    }

    [Fact]
    public void DetectSymbols_UsesWordBoundary_NoFalsePositiveOnEuroSubstring()
    {
        // "neurotic" contiene "euro" come substring (n-EURO-tic): non deve far scattare EURUSD.
        var symbols = NewsImpactClassifier.DetectSymbols("Analyst gives neurotic press conference", null);
        Assert.DoesNotContain("EURUSD", symbols);
    }
}
