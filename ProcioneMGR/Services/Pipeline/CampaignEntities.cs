namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Campagna di vaglio (Fase 1, PRD Autonomia Operativa §4): un elenco ORDINATO di configurazioni
/// di caccia (<see cref="PipelineConfiguration"/>) che il <see cref="CampaignPlanner"/> ruota da
/// solo — "0 sopravvissuti" non è più un punto morto ma un input per la mossa successiva.
/// La campagna decide COSA fare dopo un run; il motore pipeline resta intoccato (si aggiunge
/// SOPRA, mai DENTRO).
///
/// SAFETY: doppio gate — la campagna agisce solo se <c>Campaign:Enabled</c> (globale, default
/// OFF) E <see cref="Enabled"/> (per campagna) sono veri. L'applica passa dalla STESSA catena
/// della ri-applica automatica (supervisore con veto + isteresi); le corsie si avviano al
/// massimo in Paper (Testnet nel planner è nel backlog §8 del PRD, Live MAI per costruzione).
/// </summary>
public class VettingCampaign
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Id dell'IdentityUser che ha creato la campagna (usato come userId dei run avviati).</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Gate per-campagna (oltre a quello globale <c>Campaign:Enabled</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>Vedi <see cref="CampaignStatus"/>: "Rotating" | "Observing" | "WaitingForTrigger".</summary>
    public string Status { get; set; } = CampaignStatus.Rotating;

    /// <summary>JSON: List&lt;<see cref="CampaignConfigState"/>&gt; — la rotazione ordinata con lo stato per config.</summary>
    public string ConfigStatesJson { get; set; } = "[]";

    /// <summary>Backoff: la stessa config non si ripete prima di N ore (un wake del trigger lo bypassa).</summary>
    public int BackoffHours { get; set; } = 12;

    /// <summary>
    /// Se true, dopo un'applica riuscita il planner AVVIA in Paper le corsie appena configurate
    /// (solo quelle ferme: una corsia già in esecuzione — o in quarantena — non viene mai toccata).
    /// </summary>
    public bool AutoStartPaperLanes { get; set; } = true;

    /// <summary>Run avviato dalla campagna e non ancora valutato (slot singolo per campagna).</summary>
    public Guid? PendingRunId { get; set; }

    /// <summary>
    /// Corsie configurate dall'ultima applica riuscita (lo "stato ATTESO di flotta" per il
    /// riallineamento post-riavvio, Fase 3-C3): in osservazione, le corsie 0..N-1 dovrebbero
    /// essere in esecuzione. 0 = nessuna applica ancora avvenuta.
    /// </summary>
    public int ObservedLanes { get; set; }

    /// <summary>
    /// Motivo del "wake" chiesto da un trigger contestuale (Fase 2) e non ancora consumato:
    /// il prossimo run parte subito (backoff bypassato) con trigger "Event".
    /// </summary>
    public string? PendingWakeReason { get; set; }

    /// <summary>Ultima decisione presa dal planner, leggibile (per UI e notifiche).</summary>
    public string? LastOutcome { get; set; }

    public DateTime? LastActionAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Stati di una campagna. Stringhe (non enum) per lo stesso motivo di PipelineRun.Status.</summary>
public static class CampaignStatus
{
    /// <summary>Sta ruotando le config di caccia in cerca di sopravvissuti.</summary>
    public const string Rotating = "Rotating";

    /// <summary>Ensemble schierato: rotazione ferma, osservazione (decay monitor / promozioni).</summary>
    public const string Observing = "Observing";

    /// <summary>Rotazione esaurita senza sopravvissuti: in attesa di un trigger contestuale (Fase 2).</summary>
    public const string WaitingForTrigger = "WaitingForTrigger";
}

/// <summary>Stato per-config dentro <see cref="VettingCampaign.ConfigStatesJson"/> (ordine = ordine di rotazione).</summary>
public sealed class CampaignConfigState
{
    public int ConfigurationId { get; set; }
    public Guid? LastRunId { get; set; }
    public DateTime? LastRunAtUtc { get; set; }

    /// <summary>"NoSurvivors" | "Applied" | "NotApplied" | "Failed" (null = mai eseguita in questo ciclo).</summary>
    public string? LastOutcome { get; set; }

    public int Attempts { get; set; }
}
