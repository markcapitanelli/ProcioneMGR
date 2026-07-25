using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test di <see cref="EngleGrangerCointegrationTest"/> e <see cref="PairsSpreadAnalyzer"/>: una
/// coppia costruita per essere cointegrata deve superare il test con l'elasticità recuperata
/// vicino al vero beta; due random walk indipendenti (nessuna relazione di lungo periodo) non
/// devono risultare cointegrate.
///
/// Le serie sono costruite nella specificazione che il test usa davvero, cioè sui LOG: X è un
/// random walk GEOMETRICO (log X random walk, prezzi sempre positivi come un OHLCV vero) e
/// Y = e^α · X^β · e^ε con ε stazionario, cioè log Y = α + β·log X + ε. Costruirle in livello
/// (Y = α + βX) misurerebbe una relazione diversa da quella stimata: per X grande
/// log(α + βX) ≈ log β + log X, quindi l'elasticità tenderebbe a 1 qualunque sia il β di partenza.
/// </summary>
public class CointegrationTests
{
    private readonly ICointegrationTest _test = new EngleGrangerCointegrationTest();

    /// <summary>Random walk geometrico: log-prezzo integrato, livello sempre &gt; 0.</summary>
    private static List<decimal> RandomWalk(int n, double stepScale, int seed)
    {
        var rnd = new Random(seed);
        var logLevel = Math.Log(100.0);
        var series = new List<decimal>(n) { 100m };
        for (var i = 1; i < n; i++)
        {
            logLevel += (rnd.NextDouble() - 0.5) * 2 * stepScale * 0.01;
            series.Add((decimal)Math.Exp(logLevel));
        }
        return series;
    }

    /// <summary>Y cointegrata con X sui log: log Y = intercept + beta·log X + rumore stazionario.</summary>
    private static List<decimal> CointegratedWith(List<decimal> x, double beta, double intercept, int seed, double noise = 0.005)
    {
        var rnd = new Random(seed);
        return x.Select(xi =>
            (decimal)Math.Exp(intercept + beta * Math.Log((double)xi) + (rnd.NextDouble() - 0.5) * 2 * noise)).ToList();
    }

    [Fact]
    public void CointegratedPair_IsDetected_WithHedgeRatioClosToTrue()
    {
        var x = RandomWalk(1000, stepScale: 1.0, seed: 1);
        const double trueBeta = 1.2;
        var y = CointegratedWith(x, trueBeta, intercept: 0.5, seed: 2);

        var result = _test.Test(y, x);

        Assert.True(result.IsCointegrated, $"ADF={result.AdfStatistic:F3} (atteso < CV MacKinnon {result.CriticalValue:F3})");
        Assert.True(Math.Abs(result.HedgeRatio - trueBeta) < 0.1, $"elasticità={result.HedgeRatio:F3}, attesa ~{trueBeta}");
        Assert.True(result.IsTradeable);
    }

    [Fact]
    public void MacKinnonCriticalValue_IsStricterThanPlainAdf_AndReportsLags()
    {
        // P0-1: il valore critico di cointegrazione al 5% (~-3.34) è più severo del vecchio ADF -2.86.
        var x = RandomWalk(1000, stepScale: 1.0, seed: 1);
        var y = CointegratedWith(x, beta: 1.2, intercept: 0.5, seed: 2);

        var result = _test.Test(y, x);

        Assert.True(result.CriticalValue < -2.86, $"CV={result.CriticalValue:F3} atteso più severo di -2.86");
        Assert.InRange(result.CriticalValue, -3.5, -3.2); // ~-3.34 per T grande
        Assert.Equal(5.0, result.SignificanceLevelPercent);
        Assert.InRange(result.AdfLags, 0, 20);
        // Il giudizio usa la statistica contro il valore critico MacKinnon riportato.
        Assert.Equal(result.AdfStatistic < result.CriticalValue, result.IsCointegrated);
    }

    [Fact]
    public void IndependentRandomWalks_AreNotCointegrated()
    {
        var x = RandomWalk(1000, stepScale: 1.0, seed: 10);
        var y = RandomWalk(1000, stepScale: 1.0, seed: 20);

        var result = _test.Test(y, x);

        Assert.False(result.IsCointegrated, $"ADF={result.AdfStatistic:F3} (atteso >= CV MacKinnon {result.CriticalValue:F3}, spread non stazionario)");
    }

    [Fact]
    public void Spread_HasSameLengthAsInput()
    {
        var x = RandomWalk(200, 1.0, 1);
        var y = RandomWalk(200, 1.0, 2);
        var result = _test.Test(y, x);
        Assert.Equal(200, result.Spread.Count);
    }

    [Fact]
    public void MismatchedLengths_Throws()
    {
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(90, 1.0, 2);
        Assert.Throws<ArgumentException>(() => _test.Test(y, x));
    }

    [Fact]
    public void TooFewObservations_Throws()
    {
        var x = RandomWalk(10, 1.0, 1);
        var y = RandomWalk(10, 1.0, 2);
        Assert.Throws<ArgumentException>(() => _test.Test(y, x));
    }

    // --- Banda di plausibilità dell'elasticità -------------------------------------------------

    [Fact]
    public void HugePriceScaleGap_DoesNotByItselfMakeThePairImplausible()
    {
        // Il nocciolo del passaggio ai log: due monete con prezzi di ordini di grandezza diversi
        // (qui X vale ~1/1000 di Y, come AAVE contro XLM) che però si muovono IN PROPORZIONE sono
        // una coppia sana. Sui prezzi grezzi β sarebbe ~1000 e un tetto su |β| la boccerebbe;
        // sui log l'elasticità resta ~1 ed è correttamente accettata.
        var x = RandomWalk(1000, stepScale: 1.0, seed: 7).Select(v => v / 1000m).ToList();
        var y = CointegratedWith(x, beta: 1.0, intercept: Math.Log(1000.0), seed: 8);

        var result = _test.Test(y, x);

        Assert.True(result.HedgeRatio is > 0.9 and < 1.1, $"elasticità={result.HedgeRatio:F3}, attesa ~1");
        Assert.True(result.IsHedgeRatioPlausible);
        Assert.True(result.IsTradeable);
    }

    [Fact]
    public void StationarySpreadButElasticityOutOfBand_IsNotTradeable()
    {
        // Il caso che il filtro esiste per fermare: lo spread È stazionario (l'ADF passa), ma le
        // due gambe non si muovono affatto in proporzione — X raddoppia e Y quadruplica. Il
        // portafoglio a controvalore uguale che il backtest apre non è quello testato qui, quindi
        // la coppia non deve arrivare in produzione nonostante la statistica sia a posto.
        var x = RandomWalk(1000, stepScale: 1.0, seed: 3);
        var y = CointegratedWith(x, beta: 3.0, intercept: 0.2, seed: 4);

        var result = _test.Test(y, x);

        Assert.True(result.IsCointegrated, "lo spread deve restare stazionario: il filtro non è un doppione dell'ADF");
        Assert.False(result.IsHedgeRatioPlausible, $"elasticità={result.HedgeRatio:F3}, attesa fuori banda");
        Assert.False(result.IsTradeable);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void NonPositivePrice_Throws(double bad)
    {
        // Un log su un prezzo non positivo darebbe -Infinity/NaN e avvelenerebbe la regressione in
        // silenzio, restituendo un verdetto formalmente valido e privo di senso.
        var x = RandomWalk(100, 1.0, 1);
        var y = RandomWalk(100, 1.0, 2);
        y[50] = (decimal)bad;

        Assert.Throws<ArgumentException>(() => _test.Test(y, x));
    }

    // --- PairsSpreadAnalyzer.RollingZScore (z-score causale) -----------------------------------

    [Fact]
    public void RollingZScore_NullDuringWarmup_ThenPopulated()
    {
        var spread = Enumerable.Range(0, 500).Select(i => Math.Sin(i * 0.1) * 10).ToList();
        var z = PairsSpreadAnalyzer.RollingZScore(spread, lookback: 20);

        for (var i = 0; i < 19; i++) Assert.Null(z[i]);
        for (var i = 19; i < 500; i++) Assert.NotNull(z[i]);
    }

    [Fact]
    public void RollingZScore_IsCausal_TruncationDoesNotChangePastValues()
    {
        // Lo z-score rolling e' causale: il valore a un indice i non deve cambiare troncando la
        // serie DOPO i (usa solo la finestra passata dello spread).
        var spread = Enumerable.Range(0, 300).Select(i => Math.Sin(i * 0.1) * 10).ToList();
        var full = PairsSpreadAnalyzer.RollingZScore(spread, 20);
        var truncated = PairsSpreadAnalyzer.RollingZScore(spread.Take(150).ToList(), 20);

        for (var i = 19; i < 150; i++)
        {
            Assert.Equal(full[i]!.Value, truncated[i]!.Value, 9);
        }
    }
}
