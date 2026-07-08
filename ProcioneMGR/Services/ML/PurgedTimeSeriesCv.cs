namespace ProcioneMGR.Services.ML;

/// <summary>Implementazione di <see cref="IPurgedTimeSeriesCv"/>. Stateless -> registrabile Singleton.</summary>
public sealed class PurgedTimeSeriesCv : IPurgedTimeSeriesCv
{
    public IReadOnlyList<CvSplit> Split(int sampleCount, int folds, int purgeWindow, int embargoPeriods)
    {
        if (sampleCount < 2) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (folds < 2) throw new ArgumentOutOfRangeException(nameof(folds));
        if (purgeWindow < 0) throw new ArgumentOutOfRangeException(nameof(purgeWindow));
        if (embargoPeriods < 0) throw new ArgumentOutOfRangeException(nameof(embargoPeriods));

        var foldSize = sampleCount / folds;
        if (foldSize < 1) throw new ArgumentException("Troppi fold per il numero di campioni disponibili.", nameof(folds));

        var result = new List<CvSplit>(folds);
        for (var k = 0; k < folds; k++)
        {
            var testStart = k * foldSize;
            // L'ultimo fold assorbe l'eventuale resto della divisione intera.
            var testEnd = k == folds - 1 ? sampleCount : testStart + foldSize; // esclusivo

            var testIndices = new List<int>(testEnd - testStart);
            for (var i = testStart; i < testEnd; i++) testIndices.Add(i);

            // Banda esclusa dal training: [testStart - purgeWindow, testEnd + embargoPeriods).
            var excludeFrom = Math.Max(0, testStart - purgeWindow);
            var excludeTo = Math.Min(sampleCount, testEnd + embargoPeriods);

            var trainIndices = new List<int>(sampleCount - (excludeTo - excludeFrom));
            for (var i = 0; i < sampleCount; i++)
            {
                if (i >= excludeFrom && i < excludeTo) continue;
                trainIndices.Add(i);
            }

            result.Add(new CvSplit(k, trainIndices, testIndices));
        }
        return result;
    }
}
