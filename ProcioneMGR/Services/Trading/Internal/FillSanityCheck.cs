namespace ProcioneMGR.Services.Trading.Internal;

/// <summary>
/// Sanity check sul fill riportato dall'exchange — bug B1 (docs/TEST-UI-2026-07-18.md): il testnet
/// ha risposto "Filled" con quantità cumulative (100x+) e prezzo 0, e il motore le ha adottate
/// così com'erano corrompendo capitale e PnL (-1,8M su 10k). Il SafetyChecker guarda l'ordine
/// PRIMA dell'invio: questo è il gemello sul RITORNO — il fill è verificato contro la quantità
/// RICHIESTA (tolleranza <see cref="SafetyConfiguration.MaxFillQuantityDeviationPercent"/>) e
/// contro il prezzo corrente di mercato (banda <see cref="SafetyConfiguration.MaxFillPriceDeviationPercent"/>).
///
/// Fuori banda ⇒ il fill è sospetto e NON va MAI adottato: l'apertura viene rifiutata come esito
/// incerto (verifica manuale), la chiusura si finalizza al prezzo di riferimento locale (rifiutarla
/// riaprirebbe il loop di oversell del bug H2). Solo i valori RIPORTATI vengono verificati: null
/// (exchange che non riporta il dettaglio del fill) mantiene il fallback locale di sempre.
/// Paper non passa mai di qui (nessuna chiamata exchange): comportamento invariato.
/// </summary>
internal static class FillSanityCheck
{
    /// <summary>True se il fill riportato è implausibile; <paramref name="reason"/> spiega perché.</summary>
    public static bool IsSuspect(
        decimal? reportedPrice, decimal? reportedQty, decimal requestedQty, decimal referencePrice,
        SafetyConfiguration safety, out string reason)
    {
        if (reportedPrice is decimal p)
        {
            if (p <= 0m)
            {
                reason = $"prezzo di fill non positivo ({p})";
                return true;
            }
            var band = safety.MaxFillPriceDeviationPercent / 100m;
            if (referencePrice > 0m && Math.Abs(p - referencePrice) > referencePrice * band)
            {
                reason = $"prezzo di fill {p} fuori banda ±{safety.MaxFillPriceDeviationPercent}% dal prezzo di riferimento {referencePrice}";
                return true;
            }
        }

        if (reportedQty is decimal q)
        {
            if (q <= 0m)
            {
                reason = $"quantità di fill non positiva ({q})";
                return true;
            }
            var tol = safety.MaxFillQuantityDeviationPercent / 100m;
            if (requestedQty > 0m && Math.Abs(q - requestedQty) > requestedQty * tol)
            {
                reason = $"quantità di fill {q} oltre la tolleranza ±{safety.MaxFillQuantityDeviationPercent}% dalla quantità richiesta {requestedQty}";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }
}
