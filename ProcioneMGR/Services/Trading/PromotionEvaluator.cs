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

    // --- [AF4a] Retrocessione di SICUREZZA Live→Testnet: l'unica estensione mai concessa al
    // perimetro Live, e solo nella direzione che riduce il rischio. Il flatten reduce-only prima
    // del cambio modalità è quello storico di LanePromoter. Default: SPENTA, e anche da accesa
    // parte in dry-run (annuncia senza agire) finché il dry-run non viene tolto apposta.
    // Verso Live non esiste e non esisterà alcun percorso automatico. ---

    /// <summary>Se true, una corsia LIVE degradata viene retrocessa a Testnet (mai a Paper diretto). Default false.</summary>
    public bool AutoDemoteLiveToTestnet { get; set; }

    /// <summary>Finché è true (default), la retrocessione Live si ANNUNCIA soltanto (WouldDemoteLive + reason DRY-RUN), senza agire.</summary>
    public bool DemoteLiveDryRun { get; set; } = true;

    /// <summary>Sharpe realizzato sotto cui la corsia Live è considerata degradata.</summary>
    public decimal DemoteLiveSharpeThreshold { get; set; }

    /// <summary>Drawdown oltre cui la corsia Live è considerata degradata, a prescindere dallo Sharpe.</summary>
    public decimal DemoteLiveMaxDrawdownPercent { get; set; } = 15m;

    /// <summary>Storia minima (settimane) prima che il degrado di una Live sia un giudizio e non rumore.</summary>
    public int DemoteLiveMinWeeks { get; set; } = 1;

    /// <summary>Trade minimi prima che il degrado di una Live sia un giudizio e non rumore.</summary>
    public int DemoteLiveMinTrades { get; set; } = 10;

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

    /// <summary>
    /// [2026-08-17] Falso quando la curva equity della sessione è troppo corta perché uno Sharpe
    /// significhi qualcosa (meno di 3 punti: <c>Statistics.SharpeRatio</c> restituisce 0).
    ///
    /// Serve perché i tre numeri della retrocessione non sopravvivono allo stesso modo a un riavvio
    /// del processo: <c>TradeCount</c> viene dai TradeRecord a database e <c>ObservationPeriod</c>
    /// da <c>StartedAtUtc</c> persistito, ma la curva equity vive in memoria e riparte vuota. Senza
    /// questo flag «Sharpe non misurabile» diventava «Sharpe = 0», cioè un giudizio pessimo, e una
    /// corsia Testnet sana veniva retrocessa da sola sessanta secondi dopo un riavvio del pod.
    /// Una finestra troppo corta non è un verdetto negativo: è l'assenza di un verdetto.
    /// </summary>
    public bool SharpeMeasurable { get; set; } = true;

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

    /// <summary>[AF4a] True se una corsia LIVE degradata VERREBBE retrocessa, ma il dry-run è acceso: solo visibilità, mai azione.</summary>
    public bool WouldDemoteLive { get; set; }

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
    Microsoft.Extensions.Options.IOptionsMonitor<PromotionEvaluatorOptions> options) : IPromotionEvaluator
{
    /// <summary>Numero di corsie isolate (allineato a Program.cs LaneCount).</summary>
    public static int LaneCount => TradingLanes.Count;

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
            // Sotto i 3 punti Statistics.SharpeRatio restituisce 0: è «non calcolabile», non «zero».
            // La curva è di SESSIONE e in memoria, quindi dopo un riavvio è corta anche su una
            // corsia che opera da settimane — vedi LaneMetrics.SharpeMeasurable.
            SharpeMeasurable = perf.EquityCurve.Count >= 3,
        };

        // CurrentValue a ogni valutazione: le soglie modificate da /admin/autonomy valgono dal tick dopo.
        var decision = Decide(metrics, status.Mode, status.IsRunning, options.CurrentValue);
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

        // Live: VERSO Live non esiste alcun percorso automatico, mai. [AF4a] DA Live esiste una
        // sola uscita automatica, opt-in e in sola direzione di sicurezza: la retrocessione a
        // Testnet di una corsia degradata — mai Paper diretto, mai un avvio, e col dry-run
        // (default) si annuncia soltanto. Il fuzz a 20k combinazioni difende tutti questi confini.
        if (currentMode == TradingMode.Live)
        {
            if (!opt.AutoDemoteLiveToTestnet)
            {
                decision.Reason = "Corsia in Live: nessuna gestione automatica (Testnet→Live e la gestione del Live restano manuali).";
                return decision;
            }

            var liveWeeks = TimeSpan.FromDays(7 * Math.Max(1, opt.DemoteLiveMinWeeks));
            var enoughLiveHistory = metrics.TradeCount >= Math.Max(1, opt.DemoteLiveMinTrades)
                                    && metrics.ObservationPeriod >= liveWeeks;
            // Il termine Sharpe vale solo se lo Sharpe è misurabile (curva di sessione ≥ 3 punti):
            // dopo un riavvio è vuota, e leggerne lo zero come «degradata» flatterebbe posizioni
            // REALI per un evento di infrastruttura. Il termine drawdown resta sempre valido:
            // MaxDrawdownPercent è persistito e sopravvive al riavvio.
            var degraded = (metrics.SharpeMeasurable && metrics.RealizedSharpe < opt.DemoteLiveSharpeThreshold)
                           || metrics.MaxDrawdown > Math.Max(0m, opt.DemoteLiveMaxDrawdownPercent);

            if (!enoughLiveHistory)
            {
                decision.Reason = $"Live: storia insufficiente per un giudizio ({metrics.TradeCount} trade, {metrics.ObservationPeriod.TotalDays:F0}gg): nessuna azione automatica.";
            }
            else if (!degraded)
            {
                decision.Reason = $"Live in linea: Sharpe {metrics.RealizedSharpe:F2}, DD {metrics.MaxDrawdown:F1}%. Testnet→Live resta manuale.";
            }
            else if (opt.DemoteLiveDryRun)
            {
                decision.WouldDemoteLive = true;
                decision.Reason = $"DRY-RUN: retrocederei Live→Testnet (Sharpe {metrics.RealizedSharpe:F2} < {opt.DemoteLiveSharpeThreshold:F2} o DD {metrics.MaxDrawdown:F1}% > {opt.DemoteLiveMaxDrawdownPercent:F0}%). Nessuna azione: DemoteLiveDryRun=true.";
            }
            else
            {
                decision.ShouldDemote = true;
                decision.SuggestedMode = TradingMode.Testnet;
                decision.Reason = $"Retrocessione di SICUREZZA Live→Testnet: Sharpe {metrics.RealizedSharpe:F2} (soglia {opt.DemoteLiveSharpeThreshold:F2}), DD {metrics.MaxDrawdown:F1}% (limite {opt.DemoteLiveMaxDrawdownPercent:F0}%) su {metrics.TradeCount} trade in {metrics.ObservationPeriod.TotalDays:F0}gg. Le posizioni reali vengono chiuse reduce-only prima del cambio.";
            }
            return decision;
        }

        // Testnet: l'unica azione automatica possibile è la RETROCESSIONE a Paper se l'edge svanisce.
        // Non esiste alcun percorso automatico verso Live.
        if (currentMode == TradingMode.Testnet)
        {
            var demoteWeeks = TimeSpan.FromDays(7 * Math.Max(1, opt.DemoteMinWeeks));
            var enoughHistory = metrics.TradeCount >= opt.MinTradeCount && metrics.ObservationPeriod >= demoteWeeks;
            if (opt.AutoDemoteToPaper && enoughHistory && metrics.SharpeMeasurable
                && metrics.RealizedSharpe < opt.DemoteSharpeThreshold)
            {
                decision.ShouldDemote = true;
                decision.SuggestedMode = TradingMode.Paper;
                decision.Reason = $"Retrocessione a Paper: Sharpe realizzato {metrics.RealizedSharpe:F2} < soglia {opt.DemoteSharpeThreshold:F2} da almeno {opt.DemoteMinWeeks} settimane.";
            }
            else if (!metrics.SharpeMeasurable)
            {
                decision.Reason = $"Testnet: Sharpe non misurabile in questo avvio (curva equity di sessione ancora corta, {metrics.TradeCount} trade chiusi). Nessuna retrocessione automatica finché non c'è un giudizio.";
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
