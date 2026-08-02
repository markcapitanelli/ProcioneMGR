namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Opzioni dell'orchestratore di flotta (sezione <c>Fleet</c>). Default: SPENTO, e anche da
/// acceso parte in DryRun (solo journal, zero azioni) — l'ordine degli incrementi è parte del
/// contratto: prima si osserva il journal per giorni, poi si toglie il dry-run apposta.
/// </summary>
public sealed class FleetOptions
{
    public bool Enabled { get; set; }

    /// <summary>Finché è true (default), l'orchestratore DECIDE e SCRIVE il journal ma non esegue nulla.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Cadenza del tick in minuti.</summary>
    public int TickMinutes { get; set; } = 15;

    // --- Ritiro dei forward test perdenti (corsie di flotta, mai l'impronta storica) ---

    /// <summary>Sharpe realizzato sotto cui un forward test è un perdente da ritirare.</summary>
    public decimal RetireSharpeThreshold { get; set; }

    /// <summary>Settimane minime di osservazione prima che un ritiro sia un giudizio e non rumore.</summary>
    public int RetireMinWeeks { get; set; } = 3;

    /// <summary>Trade minimi prima che un ritiro sia un giudizio e non rumore.</summary>
    public int RetireMinTrades { get; set; } = 20;

    /// <summary>
    /// Tick CONSECUTIVI in cui il verdetto di ritiro deve ripetersi prima di agire (isteresi:
    /// uno Sharpe che oscilla attorno alla soglia non deve produrre stop a raffica).
    /// </summary>
    public int RetireConfirmTicks { get; set; } = 2;

    // --- Assegnazione dei candidati ---

    /// <summary>Assegnazioni massime per tick (prudenza: una alla volta, il tick dopo si rivaluta).</summary>
    public int MaxAssignmentsPerTick { get; set; } = 1;

    /// <summary>
    /// Trade/mese minimi dichiarati (derivati dall'holdout) perché un candidato entri in coda.
    /// Preferenza del proprietario: intraday/swing breve — un candidato che non dichiara la sua
    /// frequenza non entra affatto.
    /// </summary>
    public decimal MinTradesPerMonth { get; set; } = 1m;

    /// <summary>Età massima (giorni) di un run perché sia ancora un candidato fresco.</summary>
    public int CandidateMaxAgeDays { get; set; } = 14;

    // --- [AF4b] Guardia di flotta ---

    /// <summary>
    /// Oltre questo numero di corsie ATTIVE, l'orchestratore rifiuta nuove assegnazioni se il
    /// limite di esposizione correlata (<c>Trading:CorrelatedExposure</c>) è spento: una flotta
    /// larga senza guardia trasversale è concentrazione di rischio non misurata.
    /// </summary>
    public int MaxLanesWithoutExposureGuard { get; set; } = 3;

    /// <summary>Notifica se il worker del carry è abilitato ma non decide da più di queste ore.</summary>
    public int CarrySilenceAlertHours { get; set; } = 24;
}

/// <summary>Fotografia di una corsia come la vede l'orchestratore (sola lettura).</summary>
public sealed record FleetLaneState(
    int LaneId,
    bool IsRunning,
    string Mode,
    bool IsConfigured,
    bool Quarantined,
    bool CampaignOwned,
    bool EmergencyStopped,
    decimal RealizedSharpe,
    int TradeCount,
    TimeSpan Observation,
    string Symbol,
    string Timeframe);

/// <summary>
/// Un run candidato al forward test. <paramref name="Band"/>: "pass" = sopravvissuti alla
/// validazione piena (assegnabile in automatico); "grey" = bocciato SOLO per finestra corta
/// (ContoTrade/sotto-potenza) — proposto al click umano, mai assegnato da solo (F5).
/// La durata mediana delle posizioni NON è derivabile a livello di run (la trade list dei
/// candidati non è persistita): si dichiara la frequenza (trade/mese) e il timeframe, la durata
/// vera la misurerà il forward test stesso.
/// </summary>
public sealed record FleetCandidate(
    Guid RunId,
    DateTime CompletedAtUtc,
    string Band,
    decimal TradesPerMonth,
    string Timeframe,
    string Summary,
    bool AlreadyHandled);

/// <summary>Lo stato complessivo su cui <see cref="FleetOrchestrator.Decide"/> ragiona. Solo dati, nessun servizio.</summary>
public sealed class FleetState
{
    public required IReadOnlyList<FleetLaneState> Lanes { get; init; }

    public required IReadOnlyList<FleetCandidate> Candidates { get; init; }

    /// <summary>
    /// Le prime N corsie (l'impronta storica dell'auto-apply, oggi 3): territorio di
    /// auto-reapply e campagne, MAI dell'orchestratore. La flotta lavora da qui in su.
    /// </summary>
    public required int FootprintLanes { get; init; }

    /// <summary>Il limite di esposizione correlata fra corsie è acceso? (precondizione AF4b)</summary>
    public required bool ExposureGuardEnabled { get; init; }

    public required DateTime NowUtc { get; init; }
}

/// <summary>Le azioni che l'orchestratore può decidere. Chiuse: non esiste un'azione "avvia Live" per costruzione.</summary>
public abstract record FleetAction(string Reason);

/// <summary>Schiera il candidato sulla corsia libera indicata e la avvia in Paper (AF2b; in DryRun solo journal).</summary>
public sealed record AssignCandidateToLane(Guid RunId, int LaneId, string Reason) : FleetAction(Reason);

/// <summary>Ferma un forward test perdente e libera la corsia.</summary>
public sealed record StopAndFreeLane(int LaneId, string Reason) : FleetAction(Reason);

/// <summary>Fascia grigia (F5): si propone al click umano, MAI si assegna da soli.</summary>
public sealed record ProposeGreyCandidate(Guid RunId, string Reason) : FleetAction(Reason);

/// <summary>Nessuna azione, ma con un motivo che PORTA informazione (conflitto, guardia, coda bloccata).</summary>
public sealed record FleetNoOp(string Reason) : FleetAction(Reason);

/// <summary>Il piano di un tick.</summary>
public sealed record FleetPlan(IReadOnlyList<FleetAction> Actions)
{
    public static readonly FleetPlan Empty = new([]);
}
