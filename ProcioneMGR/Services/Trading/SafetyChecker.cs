namespace ProcioneMGR.Services.Trading;

public class SafetyCheckResult
{
    public bool IsAllowed { get; set; }
    public List<string> Violations { get; set; } = new();

    /// <summary>True se una violazione è CRITICA (max daily loss / max drawdown) e va attivato l'emergency stop.</summary>
    public bool RequiresEmergencyStop { get; set; }
}

/// <summary>
/// Valida ogni ordine contro i limiti di sicurezza PRIMA di piazzarlo.
/// Principio: meglio rifiutare un ordine valido che accettarne uno pericoloso.
/// Solo il metodo statico puro: l'interfaccia istanza (ISafetyChecker/ValidateOrderAsync)
/// era registrata in DI ma mai risolta da nessuno — rimossa come codice morto.
/// </summary>
public static class SafetyChecker
{
    /// <summary>
    /// Valutazione PURA (senza I/O) di tutti i safety check. Raccoglie TUTTE le violazioni
    /// (non si ferma alla prima) così l'operatore vede l'intero quadro. Testabile direttamente.
    /// </summary>
    public static SafetyCheckResult Evaluate(Order order, TradingEngineStatus status, SafetyConfiguration cfg, DateTime nowUtc)
    {
        var result = new SafetyCheckResult { IsAllowed = true };
        var capital = status.TotalCapital;
        var notional = order.Notional;

        // 0) Base di calcolo: i check 1-3 confrontano contro percentuali del capitale. Con
        //    capitale <= 0 (config errata, balance non caricato, conto vuoto) non esiste un
        //    denominatore valido: fail-CLOSED, nessun ordine è dimensionabile.
        if (capital <= 0m)
        {
            result.Violations.Add(
                $"Capitale totale non positivo ({capital:N2}): i limiti percentuali non sono verificabili, ordine rifiutato.");
        }

        // 1) Dimensione massima della singola posizione.
        if (capital > 0m && notional > capital * cfg.MaxPositionSizePercent / 100m)
        {
            result.Violations.Add(
                $"Posizione troppo grande: notional {notional:N2} > {cfg.MaxPositionSizePercent}% del capitale ({capital * cfg.MaxPositionSizePercent / 100m:N2}).");
        }

        // 2) Esposizione totale massima.
        if (capital > 0m && status.UsedCapital + notional > capital * cfg.MaxTotalExposurePercent / 100m)
        {
            result.Violations.Add(
                $"Esposizione totale eccessiva: {status.UsedCapital + notional:N2} > {cfg.MaxTotalExposurePercent}% del capitale ({capital * cfg.MaxTotalExposurePercent / 100m:N2}).");
        }

        // 3) Perdita giornaliera massima (CRITICA -> emergency stop). Confronto `>=` (fail-closed):
        //    AL limite si ferma, coerente col drawdown al punto 4 — prima la perdita giornaliera
        //    usava `>` (permessa esattamente al limite), un'asimmetria senza motivo. Safety-first:
        //    alla soglia esatta si blocca, non si aspetta il centesimo successivo.
        if (capital > 0m && status.DailyPnl < 0m && -status.DailyPnl >= capital * cfg.MaxDailyLossPercent / 100m)
        {
            result.Violations.Add(
                $"Perdita giornaliera {-status.DailyPnl:N2} al limite o oltre {cfg.MaxDailyLossPercent}% ({capital * cfg.MaxDailyLossPercent / 100m:N2}).");
            result.RequiresEmergencyStop = true;
        }

        // 4) Drawdown massimo (CRITICA -> emergency stop). status.MaxDrawdown è in %.
        if (status.MaxDrawdown >= cfg.MaxDrawdownPercent)
        {
            result.Violations.Add(
                $"Drawdown {status.MaxDrawdown:N2}% oltre il limite {cfg.MaxDrawdownPercent}%.");
            result.RequiresEmergencyStop = true;
        }

        // 5) Numero massimo di posizioni aperte.
        if (status.OpenPositionCount >= cfg.MaxOpenPositions)
        {
            result.Violations.Add(
                $"Troppe posizioni aperte: {status.OpenPositionCount} >= limite {cfg.MaxOpenPositions}.");
        }

        // 6) Intervallo minimo tra ordini (anti-spam).
        if (status.LastOrderUtc is DateTime last)
        {
            var elapsed = (nowUtc - last).TotalSeconds;
            if (elapsed < cfg.MinOrderIntervalSeconds)
            {
                result.Violations.Add(
                    $"Ordini troppo ravvicinati: {elapsed:F1}s dall'ultimo, minimo {cfg.MinOrderIntervalSeconds}s.");
            }
        }

        // 7) Conferma manuale obbligatoria in Live.
        if (order.Mode == TradingMode.Live && cfg.RequireManualConfirmationForLive && !order.ManuallyConfirmed)
        {
            result.Violations.Add("Ordine Live senza conferma manuale dell'operatore.");
        }

        // 8) Già in emergency stop: nessun ordine consentito.
        if (status.IsEmergencyStopped)
        {
            result.Violations.Add($"Emergency stop attivo: {status.EmergencyStopReason ?? "(motivo non specificato)"}.");
        }

        // 9) Sanità di base.
        if (order.Quantity <= 0m)
        {
            result.Violations.Add("Quantità non valida (<= 0).");
        }
        if ((order.Price ?? 0m) <= 0m)
        {
            result.Violations.Add("Prezzo di riferimento non valido (<= 0).");
        }

        // 10) Leva massima consentita (solo Futures; per lo Spot Leverage è sempre 1).
        if (order.MarketType == MarketType.Futures && order.Leverage > cfg.MaxLeverageAllowed)
        {
            result.Violations.Add(
                $"Leva {order.Leverage}x oltre il limite massimo consentito {cfg.MaxLeverageAllowed}x.");
        }

        result.IsAllowed = result.Violations.Count == 0;
        return result;
    }
}
