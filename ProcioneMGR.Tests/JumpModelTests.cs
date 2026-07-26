using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// [C1 roadmap integrazione] Lo statistical jump model su regimi sintetici PIANTATI: la verità è
/// nota per costruzione, quindi si misura il recupero, non lo si racconta. La proprietà che conta:
/// λ=0 degenera in K-means (flicker), λ moderato recupera la segmentazione persistente vera,
/// λ enorme collassa in un solo stato — la manopola fa quello che la letteratura promette.
/// </summary>
public class JumpModelTests
{
    /// <summary>
    /// Serie con 3 regimi veri in 2D, segmenti lunghi (150 barre), cluster VICINI (σ=1,1 su
    /// centri a distanza ~2): il nearest-centroid grezzo flickera per costruzione, come i
    /// nostri regimi K-means reali.
    /// </summary>
    private static (double[][] X, int[] TrueStates) Planted(int segments = 6, int segmentLength = 150, double sigma = 1.1, int seed = 42)
    {
        double[][] centers = [[0, 0], [2, 0], [1, 1.8]];
        var rng = new Random(seed);
        var x = new List<double[]>();
        var truth = new List<int>();
        for (var s = 0; s < segments; s++)
        {
            var state = s % centers.Length;
            for (var i = 0; i < segmentLength; i++)
            {
                x.Add([
                    centers[state][0] + Gaussian(rng) * sigma,
                    centers[state][1] + Gaussian(rng) * sigma,
                ]);
                truth.Add(state);
            }
        }
        return (x.ToArray(), truth.ToArray());
    }

    private static double Gaussian(Random rng)
    {
        // Box-Muller: due uniformi -> una normale standard.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Accuratezza invariante alla permutazione delle etichette (k piccolo: si provano tutte).</summary>
    private static double PermutationAccuracy(int[] predicted, int[] truth, int k)
    {
        var perms = Permutations([.. Enumerable.Range(0, k)]);
        var best = 0.0;
        foreach (var p in perms)
        {
            var hits = 0;
            for (var i = 0; i < truth.Length; i++) if (p[predicted[i]] == truth[i]) hits++;
            best = Math.Max(best, (double)hits / truth.Length);
        }
        return best;
    }

    private static List<int[]> Permutations(int[] items)
    {
        if (items.Length == 1) return [items];
        var result = new List<int[]>();
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, j) => j != i).ToArray();
            foreach (var p in Permutations(rest)) result.Add([items[i], .. p]);
        }
        return result;
    }

    [Fact]
    public void LambdaModerata_RecuperaLaSegmentazionePiantata()
    {
        var (x, truth) = Planted();
        var (z, _, _) = JumpModel.Standardize(x);

        var fit = JumpModel.Fit(z, k: 3, lambda: 20, seed: 1);

        Assert.True(PermutationAccuracy(fit.States, truth, 3) > 0.95,
            $"accuratezza {PermutationAccuracy(fit.States, truth, 3):P1}: il modello deve recuperare i regimi piantati");
        // 6 segmenti veri = 5 salti: il percorso stimato deve stare nello stesso ordine di grandezza.
        Assert.InRange(JumpModel.RunLengths(fit.States).Count, 3, 12);
    }

    [Fact]
    public void LambdaZero_EKMeans_FlickeraSuiClusterVicini()
    {
        var (x, _) = Planted();
        var (z, _, _) = JumpModel.Standardize(x);

        var kmeans = JumpModel.Fit(z, k: 3, lambda: 0, seed: 1);
        var jump = JumpModel.Fit(z, k: 3, lambda: 20, seed: 1);

        var kmeansTransitions = JumpModel.RunLengths(kmeans.States).Count - 1;
        var jumpTransitions = JumpModel.RunLengths(jump.States).Count - 1;
        // Su cluster sovrapposti il K-means salta di continuo; la penalità deve tagliare i salti
        // di almeno un ordine di grandezza — è l'intera ragione di esistere del modello.
        Assert.True(kmeansTransitions > 10 * jumpTransitions,
            $"K-means {kmeansTransitions} salti vs jump {jumpTransitions}: la penalità non sta lavorando");
    }

    [Fact]
    public void LambdaEnorme_CollassaInUnSoloStato()
    {
        var (x, _) = Planted();
        var (z, _, _) = JumpModel.Standardize(x);
        var fit = JumpModel.Fit(z, k: 3, lambda: 1e9, seed: 1);
        Assert.Single(fit.States.Distinct());
    }

    [Fact]
    public void StessoSeed_StessoRisultato()
    {
        var (x, _) = Planted();
        var (z, _, _) = JumpModel.Standardize(x);
        var a = JumpModel.Fit(z, k: 3, lambda: 10, seed: 7);
        var b = JumpModel.Fit(z, k: 3, lambda: 10, seed: 7);
        Assert.Equal(a.States, b.States);
        Assert.Equal(a.Objective, b.Objective);
    }

    [Fact]
    public void DecodificaCausale_ConcordaConLOfflineSuDatiPuliti()
    {
        var (x, _) = Planted();
        var (z, _, _) = JumpModel.Standardize(x);
        var fit = JumpModel.Fit(z, k: 3, lambda: 20, seed: 1);

        var causal = JumpModel.DecodeCausal(z, fit.Centroids, lambda: 20);
        var agreement = fit.States.Zip(causal, (a, b) => a == b ? 1.0 : 0.0).Average();
        // Il filtro non guarda avanti quindi ritarda ai bordi dei segmenti, ma sul corpo deve
        // concordare: sotto il 90% la versione live starebbe decidendo su un modello diverso.
        Assert.True(agreement > 0.90, $"accordo causale/offline {agreement:P1}");
    }

    [Fact]
    public void DecodificaCausale_HaIsteresi_NonFlickera()
    {
        var (x, _) = Planted();
        var (z, _, _) = JumpModel.Standardize(x);
        var fit = JumpModel.Fit(z, k: 3, lambda: 20, seed: 1);

        var raw = JumpModel.DecodeCausal(z, fit.Centroids, lambda: 0);
        var filtered = JumpModel.DecodeCausal(z, fit.Centroids, lambda: 20);
        Assert.True(JumpModel.RunLengths(filtered).Count < JumpModel.RunLengths(raw).Count / 5,
            "l'isteresi causale deve tagliare i cambi di stato rispetto al nearest-centroid nudo");
    }

    [Fact]
    public void Standardizzazione_MediaZeroVarianzaUno_ERiapplicabile()
    {
        var (x, _) = Planted(segments: 2, segmentLength: 50);
        var (z, means, stds) = JumpModel.Standardize(x);

        for (var j = 0; j < 2; j++)
        {
            var col = z.Select(r => r[j]).ToArray();
            Assert.True(Math.Abs(col.Average()) < 1e-9);
            Assert.True(Math.Abs(col.Select(v => v * v).Average() - 1.0) < 1e-9);
        }

        var reapplied = JumpModel.ApplyStandardization(x, means, stds);
        for (var i = 0; i < z.Length; i++) Assert.Equal(z[i], reapplied[i]);
    }

    [Fact]
    public void RunLengths_SuPercorsoNoto()
    {
        Assert.Equal([3, 1, 2], JumpModel.RunLengths([0, 0, 0, 1, 2, 2]));
        Assert.Equal([1], JumpModel.RunLengths([5]));
        Assert.Empty(JumpModel.RunLengths(Array.Empty<int>()));
    }
}
