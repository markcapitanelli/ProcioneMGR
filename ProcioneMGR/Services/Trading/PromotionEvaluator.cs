using Microsoft.Extensions.DependencyInjection;

namespace ProcioneMGR.Services.Trading;

/// <summary>Soglie della promozione/retrocessione automatica delle corsie (sezione di config <c>PromotionEvaluator</c>).</summary>
public sealed class PromotionEvaluatorOptions
{
    public decimal MinSharpeRealized { get; set; } = 0.8m;
    public int MinTradeCount { get; set; } = 30;
    public decimal MaxDrawdownPercent { get; set; } = 15m;
    public int MinObservationWeeks { get; set; } = 3;
    public decimal MinWinRate { get; set; } = 0.45m; // frazione (0-1)

    /// <summary>Se true il <c>PromotionWorker</c> promuove davvero (Paper→Testnet); se false valuta soltanto (la UI mostra "pronto").</summary>
    public bool AutoPromoteToTestnet { get; set; } = true;

    /// <summary>Scrive una voce di audit visibile all'utente a ogni promozione/retrocessione.</summary>
    public bool NotifyOnPromotion { get; set; } = true;

    /// <summary>Blocco assoluto: una corsia con drawdown oltre questa soglia non viene MAI promossa, anche se il resto è ottimo.</summary>
    public decimal HardMaxDrawdownPercent { get; set; } = 20m;

    // --- Retrocessione (reversibilità): Testnet→Paper se l'edge svanisce ---
    public bool AutoDemoteToPaper { get; set; } = true;
    public decimal DemoteSharpeThreshold { get; set; } = 0.5m;
    public int DemoteMinWeeks { get; set; } = 2;

    /// <summary>Ogni quante ore il <c>PromotionWorker</c> rivaluta le corsie.</summary>
    public int EvaluationIntervalHours { get; set; } = 6;
}

/// <summary>Metriche realizzate di una corsia, con i flag "criterio soddisfatto?" per la trasparenza in UI.</summary>
public sealed class LaneMetrics
{
    public decimal RealizedSharpe { get; set; }
    public decimal RealizedProfitFactor { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int TradeCount { get; set; }
    public decimal WinRate { get; set; } // frazione (0-1)
    public TimeSpan ObservationPeriod { get; set; }

    public bool MeetsMinSharpe { get; set; }
    public bool MeetsMinTrades { get; set; }
    public bool MeetsMaxDrawdown { get; set; }
    public bool MeetsMinWeeks { get; set; }
    public bool MeetsMinWinRate { get; set; }
}

/// <summary>Decisione di promozione/retrocessione per una corsia. La modalità suggerita non è MAI Live (safety).</summary>
public sealed class PromotionDecision
{
    public int LaneId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public TradingMode CurrentMode { get; set; }
    public TradingMode SuggestedMode { get; set; }

    /// <summary>True se va promossa (Paper→Testnet).</summary>
    public bool ShouldPromote { get; set; }

    /// <summary>True se va retrocessa (Testnet→Paper) perché l'edge è svanito.</summary>
    public bool ShouldDemote { get; set; }

    /// <summary>True se la corsia è pronta per Testnet ma l'auto-promozione è disattivata (mostra "pronto" in UI).</summary>
    public bool ReadyForTestnet { get; set; }

    public string Reason { get; set; } = string.Empty;
    public LaneMetrics Metrics { get; set; } = new();
    public bool IsRunning { get; set; }
}

public interface IPromotionEvaluator
{
    Task<PromotionDecision> EvaluateLaneAsync(int laneId, CancellationToken ct = default);
    Task<IReadOnlyList<PromotionDecision>> EvaluateAllLanesAsync(CancellationToken ct = default);
}

/// <summary>
/// Decide se una corsia di Paper trading ha performato abbastanza bene, abbastanza a lungo, da essere
/// promossa a Testnet (stesso protocollo di Live ma senza soldi veri), o se una corsia Testnet va
/// retrocessa a Paper perché l'edge è svanito.
///
/// CONFINE DI SICUREZZA NON NEGOZIABILE: la modalità suggerita non è MAI <see cref="TradingMode.Live"/>.
/// Nessuna metrica, per quanto eccellente, promuove automaticamente a Live: Testnet→Live resta sempre
/// una decisione manuale dietro <see cref="SafetyChecker"/> + conferma umana. Le corsie già in Live non
/// vengono nemmeno valutate.
///
/// La logica di decisione (<see cref="Decide"/>) è pura e deterministica: testabile in isolamento con
/// <see cref="LaneMetrics"/> sintetiche, senza DB né rete.
/// </summary>
public sealed class PromotionEvaluator(
    IServiceProvider serviceProvider,
    PromotionEvaluatorOptions options) : IPromotionEvaluator
{
    /// <summary>Numero di corsie isolate (allineato a Program.cs LaneCount).</summary>
    public const int LaneCount = 3;

    public async Task<IReadOnlyList<PromotionDecision>> EvaluateAllLanesAsync(CancellationToken ct = default)
    {
        var list = new List<PromotionDecision>();
        for (var lane = 0; lane < LaneCount; lane++)
        {
            list.Add(await EvaluateLaneAsync(lane, ct));
        }
        return list;
    }

    public async Task<PromotionDecision> EvaluateLaneAsync(int laneId, CancellationToken ct = default)
    {
        var engine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId);
        var status = await engine.GetStatusAsync(ct);
        var perf = await engine.GetPerformanceAsync(from: status.StartedAtUtc, ct);

        var observation = status.StartedAtUtc is DateTime start ? DateTime.UtcNow - start : TimeSpan.Zero;
        var metrics = new LaneMetrics
        {
            RealizedSharpe = perf.SharpeRatio,
            RealizedProfitFactor = perf.ProfitFactor,
            MaxDrawdown = perf.MaxDrawdown,
            TradeCount = perf.TotalTrades,
            WinRate = perf.WinRate / 100m, // GetPerformanceAsync espone la % (0-100); qui la frazione (0-1)
            ObservationPeriod = observation,
        };

        var decision = Decide(metrics, status.Mode, status.IsRunning, options);
        decision.LaneId = laneId;
        decision.Symbol = status.Symbol;
        decision.IsRunning = status.IsRunning;
        return decision;
    }

    /// <summary>
    /// Cuore deterministico della valutazione. Puro (nessun DB/orologio/rete): a parità di metriche
    /// la decisione è sempre identica. SICUREZZA: <see cref="PromotionDecision.SuggestedMode"/> non è
    /// mai <see cref="TradingMode.Live"/>; le corsie in Live non vengono toccate.
    /// </summary>
    public static PromotionDecision Decide(LaneMetrics metrics, TradingMode currentMode, bool isRunning, PromotionEvaluatorOptions opt)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(opt);

        var minWeeks = TimeSpan.FromDays(7 * Math.Max(1, opt.MinObservationWeeks));
        metrics.MeetsMinSharpe = metrics.RealizedSharpe >= opt.MinSharpeRealized;
        metrics.MeetsMinTrades = metrics.TradeCount >= opt.MinTradeCount;
        metrics.MeetsMaxDrawdown = metrics.MaxDrawdown <= opt.MaxDrawdownPercent;
        metrics.MeetsMinWeeks = metrics.ObservationPeriod >= minWeeks;
        metrics.MeetsMinWinRate = metrics.WinRate >= opt.MinWinRate;

        var decision = new PromotionDecision
        {
            CurrentMode = currentMode,
            SuggestedMode = currentMode, // default: nessun cambio; MAI Live
            Metrics = metrics,
        };

        // Live: mai gestito automaticamente, in nessuna direzione.
        if (currentMode == TradingMode.Live)
        {
            decision.Reason = "Corsia in Live: nessuna gestione automatica (Testnet→Live e la gestione del Live restano manuali).";
            return decision;
        }

        // Testnet: l'unica azione automatica possibile è la RETROCESSIONE a Paper se l'edge svanisce.
        // Non esiste alcun percorso automatico verso Live.
        if (currentMode == TradingMode.Testnet)
        {
            var demoteWeeks = TimeSpan.FromDays(7 * Math.Max(1, opt.DemoteMinWeeks));
            var enoughHistory = metrics.TradeCount >= opt.MinTradeCount && metrics.ObservationPeriod >= demoteWeeks;
            if (opt.AutoDemoteToPaper && enoughHistory && metrics.RealizedSharpe < opt.DemoteSharpeThreshold)
            {
                decision.ShouldDemote = true;
                decision.SuggestedMode = TradingMode.Paper;
                decision.Reason = $"Retrocessione a Paper: Sharpe realizzato {metrics.RealizedSharpe:F2} < soglia {opt.DemoteSharpeThreshold:F2} da almeno {opt.DemoteMinWeeks} settimane.";
            }
            else
            {
                decision.Reason = $"Testnet in linea: Sharpe {metrics.RealizedSharpe:F2}, {metrics.TradeCount} trade. Testnet→Live resta manuale.";
            }
            return decision;
        }

        // Paper: candidabile alla promozione a Testnet se TUTTI i criteri sono soddisfatti.
        // Blocco assoluto: drawdown oltre HardMaxDrawdownPercent → mai promossa.
        if (metrics.MaxDrawdown > opt.HardMaxDrawdownPercent)
        {
            decision.Reason = $"Non promossa: drawdown {metrics.MaxDrawdown:F1}% oltre il limite assoluto {opt.HardMaxDrawdownPercent:F0}%.";
            return decision;
        }

        var allMet = metrics.MeetsMinSharpe && metrics.MeetsMinTrades && metrics.MeetsMaxDrawdown
                     && metrics.MeetsMinWeeks && metrics.MeetsMinWinRate;
        if (allMet)
        {
            decision.ReadyForTestnet = true;
            decision.SuggestedMode = TradingMode.Testnet;
            decision.ShouldPromote = opt.AutoPromoteToTestnet;
            decision.Reason = decision.ShouldPromote
                ? $"Promozione a Testnet: Sharpe {metrics.RealizedSharpe:F2}, {metrics.TradeCount} trade, DD {metrics.MaxDrawdown:F1}%, win {metrics.WinRate:P0}, {metrics.ObservationPeriod.TotalDays:F0}gg — tutti i criteri soddisfatti."
                : $"Pronta per Testnet (auto-promozione disattivata): Sharpe {metrics.RealizedSharpe:F2}, {metrics.TradeCount} trade. Promuovi manualmente da /trading.";
        }
        else
        {
            var missing = new List<string>();
            if (!metrics.MeetsMinSharpe) missing.Add($"Sharpe {metrics.RealizedSharpe:F2}<{opt.MinSharpeRealized:F2}");
            if (!metrics.MeetsMinTrades) missing.Add($"trade {metrics.TradeCount}<{opt.MinTradeCount}");
            if (!metrics.MeetsMaxDrawdown) missing.Add($"DD {metrics.MaxDrawdown:F1}%>{opt.MaxDrawdownPercent:F0}%");
            if (!metrics.MeetsMinWeeks) missing.Add($"osservazione {metrics.ObservationPeriod.TotalDays:F0}gg<{opt.MinObservationWeeks * 7}gg");
            if (!metrics.MeetsMinWinRate) missing.Add($"win {metrics.WinRate:P0}<{opt.MinWinRate:P0}");
            decision.Reason = $"Non ancora pronta per Testnet: {string.Join(", ", missing)}.";
        }
        return decision;
    }
}
