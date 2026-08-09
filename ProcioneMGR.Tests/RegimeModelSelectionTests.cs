using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2.7 PRD-RISANAMENTO] Il jump model dietro flag (<c>MarketRegime:Model</c>), nel rispetto del
/// contratto C1 scritto in <see cref="JumpModel"/>: il DEFAULT resta K-means finché la misura non
/// decide, e il flag rende la misura possibile dall'app. Qui si verificano le proprietà della
/// CUCITURA (il seam in RegimeDetector.TrainAsync), non l'algoritmo in sé — quello è già coperto
/// da JumpModelTests:
///   1. parsing tollerante del flag (un typo in config non deve rompere il training);
///   2. compatibilità del formato persistito: i centroidi del fit (double) convertiti in float
///      funzionano con l'inference nearest-centroid ESISTENTE (nessuna migrazione, stessa
///      pipeline di assegnazione);
///   3. la proprietà per cui il flag esiste: sul percorso usato dal seam, il jump produce meno
///      transizioni del K-means a parità di dati rumorosi.
/// </summary>
public sealed class RegimeModelSelectionTests
{
    // ------------------------------------------------------------------ 1. parsing del flag

    [Theory]
    [InlineData(null, "KMeans")]
    [InlineData("", "KMeans")]
    [InlineData("  ", "KMeans")]
    [InlineData("KMeans", "KMeans")]
    [InlineData("kmeans", "KMeans")]
    [InlineData("Jump", "Jump")]
    [InlineData("jump", "Jump")]
    [InlineData(" JUMP ", "Jump")]
    [InlineData("Jmup", "KMeans")]  // typo ⇒ default sicuro, mai un'eccezione
    public void Normalize_IsTolerant_AndDefaultsToKMeans(string? input, string expected)
    {
        Assert.Equal(expected, RegimeModelKinds.Normalize(input));
    }

    [Fact]
    public void TrainingConfiguration_DefaultsToKMeans()
    {
        // Il contratto C1: chi non tocca la config ottiene il comportamento storico, bit-identico.
        var cfg = new TrainingConfiguration();
        Assert.Equal(RegimeModelKinds.KMeans, cfg.Model);
        Assert.Equal(20.0, cfg.JumpLambda);
    }

    // ------------------------------------------------------------------ dati sintetici condivisi

    /// <summary>Serie temporale a 3 regimi VERI con rumore che fa sfarfallare il nearest-centroid:
    /// blocchi lunghi per regime, con osservazioni che sconfinano verso il centroide vicino.</summary>
    private static double[][] NoisyRegimeSeries(int perBlock, int dim, int seed)
    {
        var rnd = new Random(seed);
        var rows = new List<double[]>();
        int[] blocks = [0, 1, 2, 1, 0, 2];
        foreach (var b in blocks)
        {
            for (var i = 0; i < perBlock; i++)
            {
                var row = new double[dim];
                for (var d = 0; d < dim; d++)
                {
                    // Separazione modesta (3.0) + rumore comparabile (σ~1): il rumore basta a far
                    // saltare il nearest-centroid, che è il difetto che il jump model corregge.
                    row[d] = b * 3.0 + NextGaussian(rnd);
                }
                rows.Add(row);
            }
        }
        return rows.ToArray();
    }

    private static double NextGaussian(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    // ------------------------------------------------------------------ 2. formato persistito

    [Fact]
    public void JumpCentroids_ConvertedToFloat_WorkWithExistingNearestCentroidInference()
    {
        var x = NoisyRegimeSeries(perBlock: 80, dim: 4, seed: 11);
        var fit = JumpModel.Fit(x, k: 3, lambda: 20, seed: 1);

        // La conversione del seam: double[][] → float[][], lo stesso formato di CentroidsJson.
        var floatCentroids = fit.Centroids.Select(c => Array.ConvertAll(c, v => (float)v)).ToArray();
        var floatMatrix = x.Select(r => Array.ConvertAll(r, v => (float)v)).ToArray();

        // L'inference ESISTENTE (nearest-centroid) deve funzionare sui centroidi del jump e
        // concordare con il percorso offline del fit sulla stragrande maggioranza dei punti:
        // dove divergono è il rumore che la penalità ha assorbito — che è il punto del modello.
        var assigned = RegimeAssignment.AssignRaw(floatMatrix, floatCentroids);
        Assert.Equal(fit.States.Length, assigned.Length);
        var agreement = fit.States.Zip(assigned, (a, b) => a == b ? 1 : 0).Average();
        Assert.True(agreement > 0.85, $"accordo stati-offline vs nearest-centroid = {agreement:P0}");
    }

    // ------------------------------------------------------------------ 3. la proprietà del flag

    [Fact]
    public void OnSeamPath_Jump_ProducesFewerTransitions_ThanKMeans()
    {
        var x = NoisyRegimeSeries(perBlock: 80, dim: 4, seed: 23);

        // K-means puro = jump con λ=0 (degenerazione dichiarata nel doc del modello): stesso
        // codice, stessa pipeline del seam, differisce SOLO la penalità — il confronto è pulito.
        var kmeans = JumpModel.Fit(x, k: 3, lambda: 0, seed: 1);
        var jump = JumpModel.Fit(x, k: 3, lambda: 20, seed: 1);

        var kmeansTransitions = JumpModel.RunLengths(kmeans.States).Count - 1;
        var jumpTransitions = JumpModel.RunLengths(jump.States).Count - 1;

        Assert.True(jumpTransitions < kmeansTransitions,
            $"jump={jumpTransitions} transizioni, kmeans={kmeansTransitions}: la penalità deve ridurle");
        // E i 6 blocchi veri restano riconoscibili: la penalità non deve fondere i regimi reali.
        Assert.InRange(jumpTransitions, 3, kmeansTransitions - 1);
    }
}
