using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Carry;

/// <summary>
/// Modalità operativa del carry. Contiene DELIBERATAMENTE solo Paper e Testnet: Live è
/// IRRAPPRESENTABILE — non esiste il valore, quindi nessun percorso di codice, nessuna config,
/// nessun bug può portare il carry a operare con denaro reale. È il failsafe più forte possibile
/// (più forte di un controllo a runtime): ciò che non si può esprimere non può accadere.
/// </summary>
public enum CarryMode
{
    /// <summary>Simulazione locale: nessuna chiamata all'exchange, si registra ciò che si FAREBBE.</summary>
    Paper,

    /// <summary>Bitget Demo Trading (testnet): ordini reali sul wallet demo, mai denaro vero.</summary>
    Testnet,
}

/// <summary>Una gamba desiderata del carry (spot o perp), con lato e nozionale.</summary>
public sealed record CarryLegOrder(string Symbol, bool IsPerp, bool IsBuy, decimal NotionalQuote);

/// <summary>Esito dell'esecuzione di un'apertura/chiusura a due gambe.</summary>
public sealed record CarryExecutionResult(bool Success, string Message);

/// <summary>
/// Astrazione dell'esecuzione a due gambe: la implementa il Paper (registra e basta) e il Testnet
/// (ordini Bitget demo). Il <see cref="CarryEngine"/> decide COSA fare; l'executor decide COME, e
/// solo l'executor tocca l'exchange — così la logica di decisione è testabile senza rete.
/// </summary>
public interface ICarryExecutor
{
    CarryMode Mode { get; }

    /// <summary>Apre il carry: long spot + short perp allo stesso nozionale. Le due gambe insieme.</summary>
    Task<CarryExecutionResult> OpenAsync(string symbol, decimal notionalQuote, CancellationToken ct);

    /// <summary>Chiude entrambe le gambe (spot sell + perp buy reduce-only).</summary>
    Task<CarryExecutionResult> CloseAsync(string symbol, decimal notionalQuote, CancellationToken ct);
}

/// <summary>Stato per-simbolo del carry live.</summary>
public sealed class CarrySymbolState
{
    public bool InPosition { get; set; }
    public DateTime? OpenedUtc { get; set; }
    public decimal NotionalQuote { get; set; }
    public decimal FundingCollectedPercent { get; set; }
}

/// <summary>
/// [E3] Orchestrazione LIVE del carry delta-neutro (long spot + short perp) su Bitget, in Paper o
/// Testnet — MAI Live (vedi <see cref="CarryMode"/>). Usa la STESSA regola di decisione del backtest
/// (<see cref="CarryDecider"/>): a ogni valutazione calcola il funding annualizzato recente e apre/
/// chiude tramite l'<see cref="ICarryExecutor"/>. Isolato dal motore a corsia single-leg: non lo
/// tocca, per non destabilizzare il percorso di trading esistente.
///
/// <para>Stato in memoria per-simbolo (persistenza fra riavvii = follow-up dichiarato). Il funding
/// per la decisione arriva dal chiamante (serie recente da DB/exchange), così il motore resta puro
/// e testabile.</para>
/// </summary>
public sealed class CarryEngine(ICarryExecutor executor, CarryConfiguration config, ILogger<CarryEngine> logger)
{
    private readonly Dictionary<string, CarrySymbolState> _state = new();

    public IReadOnlyDictionary<string, CarrySymbolState> States => _state;

    /// <summary>
    /// Valuta un simbolo dato il suo funding recente (% per 8h, ordinato, ultimo = più recente) e
    /// agisce. Ritorna l'azione intrapresa. La size del nozionale per gamba è
    /// <see cref="CarryConfiguration.PositionSizePercent"/> del capitale.
    /// </summary>
    public async Task<CarryAction> EvaluateAsync(string symbol, IReadOnlyList<decimal> recentFundingPercent, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(recentFundingPercent);

        var annualized = CarryDecider.TrailingAnnualized(recentFundingPercent, config.TrailingFundingEvents, config.FundingEventsPerDay);
        if (annualized is null) return CarryAction.Hold;   // finestra non piena: non si decide

        var st = _state.TryGetValue(symbol, out var s) ? s : (_state[symbol] = new CarrySymbolState());
        var action = CarryDecider.Decide(annualized.Value, st.InPosition, config);

        var notional = config.InitialCapital * config.PositionSizePercent / 100m;
        switch (action)
        {
            case CarryAction.Open:
                var openRes = await executor.OpenAsync(symbol, notional, ct);
                if (openRes.Success)
                {
                    st.InPosition = true;
                    st.OpenedUtc = DateTime.UtcNow;
                    st.NotionalQuote = notional;
                    st.FundingCollectedPercent = 0m;
                    logger.LogInformation("Carry [{Mode}] APERTO {Sym}: funding annualizzato {Ann:F1}% > {Enter:F1}%, nozionale/gamba {N:N0}.",
                        executor.Mode, symbol, annualized.Value, config.EnterAnnualFundingPercent, notional);
                }
                else
                {
                    logger.LogWarning("Carry [{Mode}] apertura {Sym} FALLITA: {Msg}", executor.Mode, symbol, openRes.Message);
                    return CarryAction.Hold;
                }
                break;

            case CarryAction.Close:
                var closeRes = await executor.CloseAsync(symbol, st.NotionalQuote, ct);
                if (closeRes.Success)
                {
                    logger.LogInformation("Carry [{Mode}] CHIUSO {Sym}: funding annualizzato {Ann:F1}% < {Exit:F1}%.",
                        executor.Mode, symbol, annualized.Value, config.ExitAnnualFundingPercent);
                    st.InPosition = false;
                    st.OpenedUtc = null;
                }
                else
                {
                    logger.LogWarning("Carry [{Mode}] chiusura {Sym} FALLITA: {Msg}", executor.Mode, symbol, closeRes.Message);
                    return CarryAction.Hold;
                }
                break;
        }
        return action;
    }
}

/// <summary>
/// Executor Paper: NON tocca l'exchange, registra soltanto le due gambe che verrebbero aperte/chiuse.
/// È la simulazione sicura per il forward test locale del carry, senza alcun rischio.
/// </summary>
public sealed class PaperCarryExecutor(ILogger<PaperCarryExecutor> logger) : ICarryExecutor
{
    public CarryMode Mode => CarryMode.Paper;

    public Task<CarryExecutionResult> OpenAsync(string symbol, decimal notionalQuote, CancellationToken ct)
    {
        logger.LogInformation("Carry Paper: SIMULO apertura {Sym} — long spot {N:N0} + short perp {N:N0} (nessun ordine reale).", symbol, notionalQuote, notionalQuote);
        return Task.FromResult(new CarryExecutionResult(true, "simulato"));
    }

    public Task<CarryExecutionResult> CloseAsync(string symbol, decimal notionalQuote, CancellationToken ct)
    {
        logger.LogInformation("Carry Paper: SIMULO chiusura {Sym} — sell spot + buy perp {N:N0} (nessun ordine reale).", symbol, notionalQuote);
        return Task.FromResult(new CarryExecutionResult(true, "simulato"));
    }
}
