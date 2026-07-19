namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Soglie del watchdog di invarianti contabili per corsia (Fase 0-A3, PRD Autonomia Operativa),
/// sezione <c>Trading:LaneInvariants</c>. Le soglie sono LASCHE apposta: il watchdog non duplica
/// il <see cref="SafetyChecker"/> pre-ordine (che resta il freno fine), è un tripwire per stati
/// contabili ASSURDI che nessun percorso legittimo può produrre — come il caso reale della
/// corsia 2 (PnL -1,8M su capitale 10k con leva 2). Hot-reload via IOptionsMonitor.
/// </summary>
public sealed class LaneInvariantOptions
{
    /// <summary>Default ON: è un freno di sicurezza, spegnerlo è la scelta che va motivata.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadenza del check (letta all'avvio del worker; cambiarla richiede riavvio, come PromotionWorker).</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>ε in valuta: AvailableCapital sotto -ε è una violazione (mai negativo oltre l'arrotondamento).</summary>
    public decimal AvailableCapitalTolerance { get; set; } = 1m;

    /// <summary>k: |PnL totale (realizzato + non realizzato)| oltre k × TotalCapital × Leverage è una violazione.</summary>
    public decimal MaxAbsPnlCapitalMultiple { get; set; } = 2m;

    /// <summary>Nozionale aperto complessivo oltre questo multiplo di TotalCapital × Leverage è una violazione.</summary>
    public decimal MaxExposureCapitalMultiple { get; set; } = 2m;
}
