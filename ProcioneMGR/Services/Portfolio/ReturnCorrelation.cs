namespace ProcioneMGR.Services.Portfolio;

/// <summary>
/// [T2, PRD memoria-caccia 2026-08-14] Correlazione pairwise fra serie di rendimenti ALLINEATE —
/// il numero che l'HRP calcola da sempre per il clustering e poi butta via: due gambe correlate
/// a 0,9 sembrano diversificazione e sono la stessa scommessa raddoppiata, e l'operatore deve
/// vederlo PRIMA di applicare la proposta, non dedurlo dai pesi.
///
/// UNA sola implementazione per i due chiamanti (EnsembleAssemblyStage sulla finestra di
/// selezione; EnsemblePageService sulle gambe di una corsia): due formule darebbero due verdetti
/// sulla stessa coppia di gambe. La preparazione dei rendimenti resta di chi chiama — le fonti
/// sono legittimamente diverse — ma il calcolo e la soglia di avviso vivono qui.
/// </summary>
public static class ReturnCorrelation
{
    /// <summary>
    /// Sopra questo |ρ| due gambe si dichiarano ridondanti. 0,7 = metà della varianza in comune;
    /// mostrato accanto a ogni avviso, mai applicato in silenzio.
    /// </summary>
    public const double DefaultWarnThreshold = 0.7;

    /// <summary>Minimo di osservazioni comuni perché ρ non sia rumore puro (coerente col minimo dell'HRP nello stage).</summary>
    public const int MinObservations = 30;

    /// <summary>
    /// Pearson su due serie della stessa lunghezza. Varianza nulla su una delle due ⇒ 0 (nessuna
    /// relazione dichiarabile), mai NaN: un NaN serializzato romperebbe il report a valle.
    /// </summary>
    public static double Pearson(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b)
    {
        if (a.Count != b.Count)
        {
            throw new ArgumentException($"Serie disallineate: {a.Count} vs {b.Count} osservazioni.");
        }
        var n = a.Count;
        if (n < 2) return 0.0;

        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++) { meanA += (double)a[i]; meanB += (double)b[i]; }
        meanA /= n; meanB /= n;

        double cov = 0, varA = 0, varB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = (double)a[i] - meanA;
            var db = (double)b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }
        if (varA <= 0 || varB <= 0) return 0.0;
        return cov / Math.Sqrt(varA * varB);
    }

    /// <summary>
    /// Ultimo equity di ogni giornata → rendimenti % giornalieri per data. La STESSA formula per
    /// l'assemblaggio in pipeline e per la valutazione dal pannello Ensemble: due formule
    /// darebbero due ρ diverse sulle stesse gambe.
    /// </summary>
    public static Dictionary<DateTime, decimal> DailyReturns(IReadOnlyList<Backtesting.EquityPoint> equity)
    {
        var daily = equity
            .GroupBy(p => p.Timestamp.Date)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Timestamp).Last().Capital);
        var dates = daily.Keys.OrderBy(d => d).ToList();
        var returns = new Dictionary<DateTime, decimal>();
        for (var i = 1; i < dates.Count; i++)
        {
            var prev = daily[dates[i - 1]];
            if (prev > 0m) returns[dates[i]] = (daily[dates[i]] - prev) / prev;
        }
        return returns;
    }

    /// <summary>Tutte le coppie (i&lt;j) dalle serie allineate, ordinate per |ρ| decrescente.</summary>
    public static List<LegCorrelationPair> AllPairs(
        IReadOnlyList<(string Key, string DisplayName, IReadOnlyList<decimal> Returns)> aligned)
    {
        var pairs = new List<LegCorrelationPair>();
        for (var i = 0; i < aligned.Count; i++)
        {
            for (var j = i + 1; j < aligned.Count; j++)
            {
                pairs.Add(new LegCorrelationPair
                {
                    KeyA = aligned[i].Key,
                    DisplayA = aligned[i].DisplayName,
                    KeyB = aligned[j].Key,
                    DisplayB = aligned[j].DisplayName,
                    Rho = Math.Round(Pearson(aligned[i].Returns, aligned[j].Returns), 3),
                });
            }
        }
        return pairs.OrderByDescending(p => Math.Abs(p.Rho)).ToList();
    }
}

/// <summary>Una coppia di gambe con la loro correlazione sulla finestra dichiarata dal report.</summary>
public sealed class LegCorrelationPair
{
    public string KeyA { get; set; } = string.Empty;
    public string DisplayA { get; set; } = string.Empty;
    public string KeyB { get; set; } = string.Empty;
    public string DisplayB { get; set; } = string.Empty;
    public double Rho { get; set; }
}

/// <summary>
/// Il report di ridondanza di una proposta/config multi-gamba. <see cref="Note"/> non-null =
/// correlazione NON calcolabile e il motivo — degradare dicendolo, mai un pannello muto che
/// sembra "nessuna ridondanza".
/// </summary>
public sealed class LegCorrelationReport
{
    public List<LegCorrelationPair> Pairs { get; set; } = new();

    /// <summary>Soglia |ρ| usata per gli avvisi, dichiarata nel report (mai implicita).</summary>
    public double WarnThreshold { get; set; } = ReturnCorrelation.DefaultWarnThreshold;

    /// <summary>Descrizione della finestra su cui ρ è misurato (es. "selezione 2026-01→2026-06, 118 giorni comuni").</summary>
    public string Window { get; set; } = string.Empty;

    /// <summary>Valorizzata quando il report NON ha potuto calcolare: il motivo, per l'operatore.</summary>
    public string? Note { get; set; }

    public IEnumerable<LegCorrelationPair> AboveThreshold =>
        Pairs.Where(p => Math.Abs(p.Rho) >= WarnThreshold);
}
