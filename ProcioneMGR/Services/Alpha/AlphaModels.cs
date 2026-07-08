namespace ProcioneMGR.Services.Alpha;

/// <summary>Configurazione della valutazione di un fattore.</summary>
public sealed class FactorEvaluationConfig
{
    /// <summary>Orizzonte (in candele) del rendimento forward usato come target dell'IC.</summary>
    public int ForwardHorizon { get; set; } = 1;

    /// <summary>Numero di quantili in cui suddividere le osservazioni per l'analisi dei rendimenti.</summary>
    public int Quantiles { get; set; } = 5;

    /// <summary>Ampiezza (in osservazioni) della finestra per l'IC rolling, da cui si stima l'IR.</summary>
    public int RollingIcWindow { get; set; } = 60;

    /// <summary>Orizzonti su cui misurare il decadimento dell'IC.</summary>
    public int[] DecayHorizons { get; set; } = [1, 2, 3, 5, 10];
}

/// <summary>Rendimento medio forward per un quantile del fattore.</summary>
public sealed class QuantileReturn
{
    /// <summary>1 = quantile con i valori di fattore più bassi ... N = più alti.</summary>
    public int Quantile { get; set; }
    public int Count { get; set; }
    public decimal MeanForwardReturn { get; set; }
}

/// <summary>IC misurato a un dato orizzonte (per la curva di decadimento).</summary>
public sealed class IcByHorizon
{
    public int Horizon { get; set; }
    public double InformationCoefficient { get; set; }
}

/// <summary>Esito completo della valutazione di un fattore.</summary>
public sealed class FactorEvaluationResult
{
    public string FactorName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Numero di osservazioni valide (fattore e forward-return entrambi non null).</summary>
    public int Observations { get; set; }

    /// <summary>IC = correlazione di Spearman (rank) tra fattore e rendimento forward, full-sample.</summary>
    public double InformationCoefficient { get; set; }

    /// <summary>Correlazione di Pearson (lineare) tra fattore e rendimento forward, full-sample.</summary>
    public double PearsonCorrelation { get; set; }

    /// <summary>Media degli IC rolling (per finestra).</summary>
    public double RollingIcMean { get; set; }

    /// <summary>Deviazione standard degli IC rolling.</summary>
    public double RollingIcStd { get; set; }

    /// <summary>Information Ratio del fattore = RollingIcMean / RollingIcStd (stabilità del segnale).</summary>
    public double InformationRatio { get; set; }

    /// <summary>Frazione di finestre rolling con IC dello stesso segno dell'IC full-sample.</summary>
    public double IcConsistency { get; set; }

    public List<QuantileReturn> QuantileReturns { get; set; } = new();

    /// <summary>Spread top-bottom: rendimento medio del quantile più alto meno quello del più basso.</summary>
    public decimal TopMinusBottomSpread { get; set; }

    public List<IcByHorizon> IcDecay { get; set; } = new();
}

/// <summary>Statistica di correlazione di rango/lineare (helper condiviso).</summary>
public static class Correlation
{
    /// <summary>Correlazione di Pearson tra due serie della stessa lunghezza. 0 se degenerata.</summary>
    public static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 3) return 0d;

        double mx = 0d, my = 0d;
        for (var i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;

        double sxy = 0d, sxx = 0d, syy = 0d;
        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - mx;
            var dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        if (sxx <= 0d || syy <= 0d) return 0d;
        return sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Correlazione di Spearman = Pearson sui ranghi (gestione tie con rango medio).</summary>
    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 3) return 0d;
        var rx = Ranks(x, n);
        var ry = Ranks(y, n);
        return Pearson(rx, ry);
    }

    /// <summary>Ranghi 1..n con rango medio per i valori a pari merito (fractional ranking).</summary>
    public static double[] Ranks(IReadOnlyList<double> values, int n)
    {
        var idx = new int[n];
        for (var i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => values[a].CompareTo(values[b]));

        var ranks = new double[n];
        var i2 = 0;
        while (i2 < n)
        {
            var j = i2;
            while (j + 1 < n && values[idx[j + 1]] == values[idx[i2]]) j++;
            // Ranghi da (i2+1) a (j+1); assegna la media a tutti i pari merito.
            var avg = (i2 + 1 + j + 1) / 2.0;
            for (var k = i2; k <= j; k++) ranks[idx[k]] = avg;
            i2 = j + 1;
        }
        return ranks;
    }
}
