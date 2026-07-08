using ProcioneMGR.Services.Monitoring.Drift;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dei detector di concept drift (rif. <c>docs/ROADMAP-QLIB.md §1.5</c>): su due campioni
/// dalla STESSA distribuzione non deve scattare drift; su una distribuzione chiaramente spostata
/// deve scattare (PSI alto / KS p→0 / Page-Hinkley oltre soglia). Dati insufficienti ⇒ None.
/// </summary>
public class DriftDetectorTests
{
    private readonly DriftThresholds _thr = new();

    /// <summary>Campione uniforme deterministico in [offset, offset+width].</summary>
    private static List<decimal> Uniform(int n, double offset, double width, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<decimal>(n);
        for (var i = 0; i < n; i++) list.Add((decimal)(offset + rnd.NextDouble() * width));
        return list;
    }

    // --- PSI ---------------------------------------------------------------------------------

    [Fact]
    public void Psi_SameDistribution_NoDrift()
    {
        var reference = Uniform(600, 0, 1, seed: 1);
        var current = Uniform(600, 0, 1, seed: 2);
        var r = new PsiDriftDetector().Detect(reference, current, _thr);
        Assert.Equal(DriftSeverity.None, r.Severity);
        Assert.True(r.Score < _thr.PsiWarning, $"PSI atteso basso, ottenuto {r.Score:F3}");
    }

    [Fact]
    public void Psi_ShiftedDistribution_Alerts()
    {
        var reference = Uniform(600, 0, 1, seed: 1);
        var current = Uniform(600, 1.0, 1, seed: 2); // spostato di una larghezza intera
        var r = new PsiDriftDetector().Detect(reference, current, _thr);
        Assert.Equal(DriftSeverity.Alert, r.Severity);
        Assert.True(r.Score >= _thr.PsiAlert, $"PSI atteso alto, ottenuto {r.Score:F3}");
    }

    // --- KS ----------------------------------------------------------------------------------

    [Fact]
    public void Ks_SameDistribution_HighPValue_NoDrift()
    {
        var reference = Uniform(500, 0, 1, seed: 10);
        var current = Uniform(500, 0, 1, seed: 20);
        var r = new KsDriftDetector().Detect(reference, current, _thr);
        Assert.Equal(DriftSeverity.None, r.Severity);
        Assert.NotNull(r.PValue);
        Assert.True(r.PValue > _thr.KsPValueWarning, $"p atteso alto, ottenuto {r.PValue:F4}");
    }

    [Fact]
    public void Ks_ShiftedDistribution_LowPValue_Alerts()
    {
        var reference = Uniform(500, 0, 1, seed: 10);
        var current = Uniform(500, 0.6, 1, seed: 20);
        var r = new KsDriftDetector().Detect(reference, current, _thr);
        Assert.Equal(DriftSeverity.Alert, r.Severity);
        Assert.True(r.PValue < _thr.KsPValueAlert, $"p atteso ~0, ottenuto {r.PValue:F4}");
    }

    // --- Page-Hinkley ------------------------------------------------------------------------

    [Fact]
    public void PageHinkley_StationaryStream_NoDrift()
    {
        var reference = Uniform(400, 0, 1, seed: 5);
        var current = Uniform(400, 0, 1, seed: 6);
        var r = new PageHinkleyDetector().Detect(reference, current, _thr);
        Assert.Equal(DriftSeverity.None, r.Severity);
    }

    [Fact]
    public void PageHinkley_MeanShiftedStream_Alerts()
    {
        var reference = Uniform(400, 0, 1, seed: 5);
        var current = Uniform(400, 3.0, 1, seed: 6); // media chiaramente spostata verso l'alto
        var r = new PageHinkleyDetector().Detect(reference, current, _thr);
        Assert.Equal(DriftSeverity.Alert, r.Severity);
        Assert.Contains("↑", r.Detail);
    }

    // --- Dati insufficienti ------------------------------------------------------------------

    [Theory]
    [InlineData("Psi")]
    [InlineData("Ks")]
    [InlineData("PageHinkley")]
    public void AllDetectors_InsufficientData_ReturnNoneWithoutThrowing(string which)
    {
        IFeatureDriftDetector d = which switch
        {
            "Psi" => new PsiDriftDetector(),
            "Ks" => new KsDriftDetector(),
            _ => new PageHinkleyDetector(),
        };
        var few = Uniform(5, 0, 1, seed: 1);
        var r = d.Detect(few, few, _thr);
        Assert.Equal(DriftSeverity.None, r.Severity);
        Assert.Contains("insufficient", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstantReference_IsHandledGracefully()
    {
        var constant = Enumerable.Repeat(1m, 100).ToList();
        var varied = Uniform(100, 0, 5, seed: 3);
        // Nessun detector deve lanciare su un riferimento degenere.
        Assert.Equal(DriftSeverity.None, new PsiDriftDetector().Detect(constant, varied, _thr).Severity);
        Assert.Equal(DriftSeverity.None, new PageHinkleyDetector().Detect(constant, varied, _thr).Severity);
        _ = new KsDriftDetector().Detect(constant, varied, _thr); // non deve lanciare
    }
}
