using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Monitoring;

/// <summary>
/// Confronta la performance REALIZZATA (trade chiusi dal vivo, Paper/Testnet/Live) di una gamba
/// dell'ensemble con quella ATTESA dal backtest/holdout che l'ha validata — "l'edge è morto?"
/// come segnale misurabile invece che intuizione. Puro/deterministico: nessuna dipendenza da DB
/// o orologio all'interno del calcolo (i trade e l'istante di analisi sono passati dal chiamante),
/// per restare testabile in isolamento con dati sintetici.
/// </summary>
public interface IStrategyDecayMonitor
{
    /// <summary>
    /// Analizza una gamba dato l'intero storico dei suoi trade chiusi (di qualunque strategia
    /// dell'ensemble contenga anche altre gambe — il filtro per <see cref="EnsembleStrategy.StrategyId"/>
    /// è fatto internamente, così il chiamante può passare l'intera tabella TradeRecords senza
    /// doverla già segmentare per gamba).
    /// </summary>
    /// <param name="timeframe">
    /// [M5] Timeframe della corsia (es. "1h"): serve a portare lo Sharpe realizzato sulla STESSA
    /// base per-candela dello Sharpe atteso, altrimenti il confronto non è interpretabile.
    /// </param>
    DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList<TradeRecord> allClosedTrades, string timeframe, DecayMonitorOptions? options = null);
}

/// <summary>Soglie del monitor di decadimento. Stessa finestra funge da minimo di trade richiesti e da ampiezza del rolling.</summary>
public sealed class DecayMonitorOptions
{
    /// <summary>Quante delle ultime operazioni chiuse considerare (e minimo richiesto prima di poter valutare).</summary>
    public int WindowTradeCount { get; set; } = 20;

    /// <summary>Sotto questa frazione di RealizedSharpe/ExpectedSharpe scatta l'alert (default 50%).</summary>
    public decimal AlertThresholdRatio { get; set; } = 0.5m;
}

public sealed class DecayReport
{
    public string StrategyId { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public decimal? ExpectedSharpe { get; set; }

    /// <summary>
    /// [M5] Sharpe realizzato su base PER-CANDELA (bucket del timeframe, bucket senza trade = 0),
    /// annualizzato come lo Sharpe holdout: è il numero CONFRONTABILE con <see cref="ExpectedSharpe"/>.
    /// </summary>
    public decimal? RealizedSharpe { get; set; }

    /// <summary>
    /// Sharpe realizzato "a trade" (annualizzato con sqrt(trade/anno) stimati dalla cadenza del
    /// campione) — il valore storico del monitor, conservato come INFORMATIVO: non è sulla stessa
    /// base dell'atteso e non partecipa più alla soglia di alert.
    /// </summary>
    public decimal? RealizedTradeSharpe { get; set; }

    /// <summary>RealizedSharpe - ExpectedSharpe.</summary>
    public decimal? SharpeDelta { get; set; }

    /// <summary>RealizedSharpe / ExpectedSharpe (1 = in linea, &lt;0.5 = alert di default). Null se ExpectedSharpe non è positivo (il rapporto non è interpretabile).</summary>
    public decimal? SharpeRatio { get; set; }

    public decimal? ExpectedProfitFactor { get; set; }
    public decimal? RealizedProfitFactor { get; set; }

    public int TradeCount { get; set; }
    public bool IsAlert { get; set; }

    /// <summary>Messaggio sempre valorizzato: spiega l'esito anche quando non scatta un alert (es. "trade insufficienti").</summary>
    public string StatusMessage { get; set; } = string.Empty;

    public DateTime AnalyzedAtUtc { get; set; }
}

public sealed class StrategyDecayMonitor : IStrategyDecayMonitor
{
    public DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList<TradeRecord> allClosedTrades, string timeframe, DecayMonitorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        allClosedTrades ??= [];
        options ??= new DecayMonitorOptions();

        var report = new DecayReport
        {
            StrategyId = strategy.StrategyId,
            StrategyName = strategy.StrategyName,
            DisplayName = strategy.DisplayName,
            ExpectedSharpe = strategy.ExpectedSharpe,
            ExpectedProfitFactor = strategy.ExpectedProfitFactor,
            AnalyzedAtUtc = DateTime.UtcNow,
        };

        // Ultime N chiuse (piu' recenti prima per il Take, poi rimesse in ordine cronologico:
        // il bucketing per periodo ha bisogno dell'ordine temporale corretto).
        var window = allClosedTrades
            .Where(t => t.StrategyId == strategy.StrategyId)
            .OrderByDescending(t => t.ClosedAtUtc)
            .Take(options.WindowTradeCount)
            .OrderBy(t => t.ClosedAtUtc)
            .ToList();
        report.TradeCount = window.Count;

        if (window.Count < options.WindowTradeCount)
        {
            report.StatusMessage = $"Trade insufficienti per una valutazione affidabile ({window.Count}/{options.WindowTradeCount}).";
            return report;
        }

        if (strategy.ExpectedSharpe is not decimal expected)
        {
            report.StatusMessage = "Metriche attese non disponibili (nessuna validazione collegata a questa gamba): nessun confronto possibile.";
            return report;
        }

        // [M5] Base OMOGENEA con l'atteso: l'ExpectedSharpe della gamba viene dal backtest/holdout,
        // calcolato su rendimenti PER CANDELA annualizzati con sqrt(candele/anno) (vedi
        // Statistics.SharpeRatio e PipelineApplier). Il vecchio realizzato era invece "a trade"
        // annualizzato con sqrt(trade/anno): due unità di misura diverse — es. una strategia con
        // 1 trade/settimana su 1h aveva un realizzato sgonfiato di ~sqrt(8760/52) ≈ 13x rispetto
        // all'atteso, e la soglia del 50% scattava (o taceva) senza significato. Qui i trade
        // vengono proiettati sui bucket del timeframe (bucket senza trade = rendimento 0, come le
        // candele piatte dell'holdout) e annualizzati con la STESSA convenzione.
        var (periodReturns, bucketsPerYear) = BuildPeriodReturns(window, timeframe);
        var realizedSharpe = AnnualizedSharpe(periodReturns, bucketsPerYear);
        var (tradeSharpe, realizedPf) = ComputeTradeMetrics(window);

        report.RealizedSharpe = realizedSharpe;
        report.RealizedTradeSharpe = tradeSharpe;
        report.RealizedProfitFactor = realizedPf;
        report.SharpeDelta = realizedSharpe - expected;

        if (expected <= 0m)
        {
            // Una soglia "% dell'atteso" non ha senso se l'atteso stesso non era un edge positivo:
            // il segno capovolgerebbe il significato del rapporto (es. -0.6/-1.2 = 0.5 sembrerebbe
            // "in linea" quando in realtà -0.6 è MEGLIO di -1.2, non uguale).
            report.StatusMessage = $"Sharpe atteso non positivo ({expected:F2}): la soglia percentuale non è applicabile, valutare il delta ({report.SharpeDelta:F2}) a occhio.";
            return report;
        }

        var ratio = realizedSharpe / expected;
        report.SharpeRatio = ratio;
        report.IsAlert = ratio < options.AlertThresholdRatio;
        report.StatusMessage = report.IsAlert
            ? $"ALERT: Sharpe realizzato {realizedSharpe:F2} vs atteso {expected:F2} ({ratio:P0}) — sotto la soglia {options.AlertThresholdRatio:P0}."
            : $"In linea: Sharpe realizzato {realizedSharpe:F2} vs atteso {expected:F2} ({ratio:P0}).";
        return report;
    }

    /// <summary>
    /// [M5] Proietta i trade chiusi sui bucket temporali del timeframe: bucket i = periodo
    /// i-esimo dal primo trade, ogni trade contribuisce il proprio rendimento (PnlPercent/100)
    /// al bucket della sua CHIUSURA, bucket senza trade = 0 (come le candele piatte
    /// dell'holdout, dove l'equity non si muove). Se la finestra copre più di
    /// <paramref name="maxBuckets"/> periodi il bucket viene ingrossato di un fattore k intero
    /// (es. 2 candele per bucket) e l'annualizzazione usa i periodi-per-anno del bucket
    /// EFFETTIVO (PeriodsPerYear/k), così il vettore resta bounded senza distorcere la scala.
    /// </summary>
    internal static (IReadOnlyList<decimal> Returns, decimal BucketsPerYear) BuildPeriodReturns(
        IReadOnlyList<TradeRecord> chronological, string timeframe, int maxBuckets = 20_000)
    {
        var ppy = Statistics.PeriodsPerYear(timeframe);
        var period = TimeSpan.FromDays(365.0 / ppy);

        var start = chronological[0].ClosedAtUtc;
        var end = chronological[^1].ClosedAtUtc;
        var rawBuckets = (long)Math.Floor((end - start) / period) + 1;
        var k = (int)Math.Max(1L, (rawBuckets + maxBuckets - 1) / maxBuckets);
        var bucket = period * k;

        var count = (int)Math.Floor((end - start) / bucket) + 1;
        var returns = new decimal[count];
        foreach (var t in chronological)
        {
            var idx = (int)Math.Floor((t.ClosedAtUtc - start) / bucket);
            returns[idx] += t.PnlPercent / 100m;
        }
        return (returns, (decimal)ppy / k);
    }

    /// <summary>Sharpe annualizzato mean/std × sqrt(bucket/anno), varianza di popolazione (coerente con Statistics.SharpeRatio). 0 se degenere.</summary>
    private static decimal AnnualizedSharpe(IReadOnlyList<decimal> returns, decimal periodsPerYear)
    {
        if (returns.Count < 2) return 0m;
        var mean = returns.Average();
        decimal sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }
        var stdDev = (decimal)Math.Sqrt((double)(sumSq / returns.Count));
        return stdDev == 0m ? 0m : mean / stdDev * (decimal)Math.Sqrt((double)periodsPerYear);
    }

    /// <summary>
    /// Metriche "a trade": Profit Factor (invariato) e lo Sharpe per-trade storico, annualizzato
    /// con sqrt(trade/anno) stimati dalla cadenza reale del campione (trade/anno = N / giorni di
    /// ampiezza × 365, ampiezza ≥ 1 giorno per non esplodere su burst compressi). Dal fix M5
    /// questo numero è solo INFORMATIVO (<see cref="DecayReport.RealizedTradeSharpe"/>): non è
    /// sulla stessa base per-candela dell'atteso e non pilota più la soglia di alert.
    /// </summary>
    private static (decimal Sharpe, decimal ProfitFactor) ComputeTradeMetrics(IReadOnlyList<TradeRecord> chronological)
    {
        var returns = chronological.Select(t => t.PnlPercent / 100m).ToList();
        var n = returns.Count;
        var mean = returns.Average();

        decimal sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }
        var variance = sumSq / n; // popolazione, coerente con Statistics.SharpeRatio
        var stdDev = (decimal)Math.Sqrt((double)variance);

        decimal sharpe;
        if (stdDev == 0m)
        {
            sharpe = 0m;
        }
        else
        {
            var tradesPerYear = EstimateTradesPerYear(chronological);
            sharpe = mean / stdDev * (decimal)Math.Sqrt((double)tradesPerYear);
        }

        var grossProfit = chronological.Where(t => t.Pnl > 0m).Sum(t => t.Pnl);
        var grossLoss = chronological.Where(t => t.Pnl < 0m).Sum(t => t.Pnl);
        var pf = grossLoss == 0m ? 0m : grossProfit / Math.Abs(grossLoss);

        return (sharpe, pf);
    }

    private static decimal EstimateTradesPerYear(IReadOnlyList<TradeRecord> chronological)
    {
        var n = chronological.Count;
        var spanDays = (decimal)(chronological[^1].ClosedAtUtc - chronological[0].ClosedAtUtc).TotalDays;
        var effectiveDays = Math.Max(spanDays, 1m);
        return n / effectiveDays * 365m;
    }
}
