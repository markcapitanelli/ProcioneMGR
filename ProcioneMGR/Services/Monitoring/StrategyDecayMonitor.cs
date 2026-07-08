using ProcioneMGR.Services.Ensemble;
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
    DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList<TradeRecord> allClosedTrades, DecayMonitorOptions? options = null);
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
    public decimal? RealizedSharpe { get; set; }

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
    public DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList<TradeRecord> allClosedTrades, DecayMonitorOptions? options = null)
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
        // la stima della cadenza annua dei trade ha bisogno dell'ordine temporale corretto).
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

        var (realizedSharpe, realizedPf) = ComputeRealizedMetrics(window);
        report.RealizedSharpe = realizedSharpe;
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
    /// Sharpe "a trade" e Profit Factor sugli ultimi N trade (in ordine cronologico).
    ///
    /// Annualizzazione: lo Sharpe holdout del backtest è calcolato su rendimenti PER CANDELA
    /// (vedi <see cref="Optimization.Statistics.SharpeRatio"/>, annualizzato con sqrt(candele/anno)
    /// del timeframe). Qui i "periodi" sono TRADE, non candele — usare sqrt(candele/anno) darebbe
    /// un numero senza senso se la strategia fa, es., un trade a settimana su un timeframe 1h
    /// (8760 candele/anno ma ~52 trade/anno: l'annualizzazione andrebbe sovrastimata di oltre
    /// 100x). Si stima invece la cadenza REALE dei trade dal campione stesso — trade/anno =
    /// N / giorni_di_ampiezza * 365 — e si annualizza con sqrt(trade/anno), la convenzione
    /// standard per uno Sharpe "per trade" quando la frequenza dei trade non è fissa.
    /// L'ampiezza è vincolata ad almeno 1 giorno per evitare stime assurde su campioni compressi
    /// in poche ore (es. un burst di trade in modalità Paper ad alta frequenza di replay).
    /// </summary>
    private static (decimal Sharpe, decimal ProfitFactor) ComputeRealizedMetrics(IReadOnlyList<TradeRecord> chronological)
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
