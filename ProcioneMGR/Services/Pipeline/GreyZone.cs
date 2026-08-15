namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// [F5 / PRD memoria-caccia 2026-08-14] IL giudice della fascia grigia — l'unica definizione,
/// promossa qui da <c>FleetStateReader.IsGrey</c> quando i consumatori sono diventati tre
/// (lettore di flotta, GreyDeployer, archivio candidati / assemblaggio ensemble). Due soglie in
/// due posti sarebbero due verdetti sulla stessa riga: il difetto già pagato con la doppia
/// regola dell'ampiezza finestra in D2.b.
///
/// Grigio = bocciato per SOLA finestra corta (ContoTrade "Solo N trade…" oppure DSR in
/// [0,80–0,95)) CON Sharpe holdout positivo e almeno un trade: un grigio che perde non è
/// grigio, è bocciato nel merito.
/// </summary>
public static class GreyZone
{
    /// <summary>Pavimento F5: DSR in [DsrFloor, DsrCeiling) = fascia grigia.</summary>
    public const double DsrFloor = 0.80;

    /// <summary>Il tetto coincide con la soglia di sopravvivenza del gate DSR.</summary>
    public const double DsrCeiling = 0.95;

    /// <summary>
    /// Prefisso del RejectReason del gate ContoTrade ("Solo N trade in holdout (&lt; M)"):
    /// bocciatura per finestra corta, non nel merito. Confronto Ordinal deliberato — il testo
    /// è prodotto dal nostro stesso codice, non da input libero.
    /// </summary>
    public const string ShortWindowRejectPrefix = "Solo ";

    public static bool IsGrey(ValidatedCandidate candidate) =>
        !candidate.Survived
        && candidate.HoldoutSharpe > 0m
        && candidate.HoldoutTrades > 0
        && ((candidate.RejectReason?.StartsWith(ShortWindowRejectPrefix, StringComparison.Ordinal) ?? false)
            || candidate.DeflatedSharpe is >= DsrFloor and < DsrCeiling);
}
