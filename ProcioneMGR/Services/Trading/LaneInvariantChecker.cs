using System.Globalization;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Valutazione PURA degli invarianti contabili di una corsia (Fase 0-A3): stato + posizioni →
/// elenco di violazioni leggibili. Separata dal <see cref="LaneInvariantWatchdog"/> per essere
/// testabile senza database né motore (stesso criterio di FillSanityCheck per il bug B1).
/// </summary>
public static class LaneInvariantChecker
{
    /// <summary>
    /// Controlla gli invarianti su uno snapshot di corsia. Le posizioni devono essere quelle
    /// della modalità corrente della corsia (stesso filtro di EnsureLoadedAsync).
    /// </summary>
    public static IReadOnlyList<string> Check(
        TradingEngineState state,
        IReadOnlyList<OpenPosition> positions,
        LaneInvariantOptions options)
    {
        var violations = new List<string>();
        var ci = CultureInfo.InvariantCulture;

        // Capitale non positivo su una corsia che gira: nessun percorso legittimo lo produce
        // (StartAsync parte da capitale > 0; il SafetyChecker post-fix rifiuta capitale ≤ 0).
        if (state.TotalCapital <= 0m)
        {
            violations.Add(string.Format(ci,
                "TotalCapital non positivo ({0:0.##}) su corsia in esecuzione", state.TotalCapital));
            return violations; // gli invarianti sotto sono multipli del capitale: senza base sensata sarebbero rumore
        }

        if (state.AvailableCapital < -options.AvailableCapitalTolerance)
        {
            violations.Add(string.Format(ci,
                "AvailableCapital negativo: {0:0.##} (tolleranza -{1:0.##})",
                state.AvailableCapital, options.AvailableCapitalTolerance));
        }

        var leverage = Math.Max(1, state.Leverage);
        var totalPnl = state.RealizedPnl + positions.Sum(p => p.UnrealizedPnl);
        var pnlCap = options.MaxAbsPnlCapitalMultiple * state.TotalCapital * leverage;
        if (Math.Abs(totalPnl) > pnlCap)
        {
            violations.Add(string.Format(ci,
                "|PnL totale| fuori scala: {0:0.##} oltre {1:0.##} ({2}× capitale {3:0.##} × leva {4})",
                totalPnl, pnlCap, options.MaxAbsPnlCapitalMultiple, state.TotalCapital, leverage));
        }

        // Nozionale al prezzo corrente (o d'ingresso se il mark non è ancora arrivato): l'esposizione
        // REALE risultante dai fill adottati, non quella richiesta — è qui che il caso corsia 2
        // (Buy 1.039 ETH ≈ 1,8M su capitale 10k) sarebbe stato visibile alla prima passata.
        var notional = positions.Sum(p => Math.Abs(p.Quantity * (p.CurrentPrice > 0m ? p.CurrentPrice : p.EntryPrice)));
        var exposureCap = options.MaxExposureCapitalMultiple * state.TotalCapital * leverage;
        if (notional > exposureCap)
        {
            violations.Add(string.Format(ci,
                "Nozionale aperto fuori scala: {0:0.##} oltre {1:0.##} ({2}× capitale {3:0.##} × leva {4})",
                notional, exposureCap, options.MaxExposureCapitalMultiple, state.TotalCapital, leverage));
        }

        return violations;
    }
}
