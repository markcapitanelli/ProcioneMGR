namespace ProcioneMGR.Services.Trading;

/// <summary>
/// [D-02, Fase 1 PRD-RISANAMENTO 2026-08-08] L'esposizione che alimenta il check n.2 del
/// <see cref="SafetyChecker"/> (<c>MaxTotalExposurePercent</c>): il NOZIONALE delle posizioni
/// aperte, <c>Σ Quantity × EntryPrice</c>, per ogni tipo di mercato.
///
/// PERCHE' esiste come funzione a parte: prima il valore era calcolato inline in
/// <c>TradingEngine.BuildSafetyStatus</c> e sui Futures usava il MARGINE (Σ MarginBalance), con
/// un commento che dichiarava l'asimmetria «volutamente conservativa». Era vero sul singolo
/// ordine (order.Notional e' leveraged) ma FALSO sull'accumulo: ogni posizione gia' aperta
/// pesava 1/leva della propria esposizione reale, e con <c>MaxOpenPositions</c> alzato il
/// capitale esposto superava il DOPPIO di MaxTotalExposurePercent senza far scattare il check
/// (esempio numerico in docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §3). Coi default la
/// coincidenza 10% × 5 = 50% mascherava il buco. Le unita' ora sono omogenee: il limite
/// vincola cio' che il suo doc-comment dichiara, il capitale complessivamente ESPOSTO.
///
/// NB: la vista UI (<c>GetStatusAsync</c>) continua a mostrare il MARGINE come UsedCapital,
/// perche' li' deve tornare con AvailableCapital. Due domande diverse, due numeri diversi.
/// Statica e pura come il SafetyChecker che alimenta: stessa regola 1, non mockabile.
/// </summary>
internal static class SafetyExposure
{
    /// <summary>
    /// Nozionale complessivamente esposto dalle posizioni aperte (unita' di order.Notional).
    ///
    /// [T5, PRD memoria-caccia 2026-08-14] LORDO deliberato, in due sensi. (1) Il Side e'
    /// ignorato: long 1.000 + short 1.000 sullo stesso simbolo consumano 2.000 di budget, non 0 —
    /// con piu' gambe concorrenti sulla stessa corsia (ensemble multi-strategia, incluse le gambe
    /// grigie) e' la lettura fail-closed: un "hedge" fra strategie diverse non e' un hedge
    /// garantito, le due gambe escono in momenti diversi. (2) <c>Math.Abs</c> come cintura:
    /// Quantity e' non-segnata per convenzione (la direzione sta in Side), ma se un giorno una
    /// quantita' negativa arrivasse fin qui RIDURREBBE l'esposizione conteggiata in silenzio —
    /// l'abs la fa pesare, mai scontare. Fail-closed sulla sicurezza, regola 4.
    /// </summary>
    public static decimal ExposedNotional(IEnumerable<OpenPosition> positions)
        => positions.Sum(p => Math.Abs(p.Quantity * p.EntryPrice));
}
