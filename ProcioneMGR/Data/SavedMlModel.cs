using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>
/// Modello di previsione dei rendimenti (<c>IReturnPredictor</c>) addestrato e salvato da un
/// utente in /ml, per riuso senza dover riaddestrare. A differenza di <c>RegimeModel</c> (che
/// salva solo i parametri numerici del K-means e reimplementa l'inferenza a mano), qui salviamo
/// il modello ML.NET GIÀ SERIALIZZATO (lo stesso blob prodotto da <c>IReturnPredictor.Save</c>):
/// per Random Forest/LightGBM (decine di alberi) reimplementare l'inferenza a mano sarebbe
/// complesso e rischioso, mentre il round-trip Save/Load è già testato per tutti i modelli.
/// </summary>
public class SavedMlModel
{
    public int Id { get; set; }

    /// <summary>FK verso AspNetUsers.</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    /// <summary>Nome scelto dall'utente, es. "RF momentum BTC 1h".</summary>
    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>"Linear" | "RandomForest" | "GradientBoosting" — usato per ricreare l'istanza giusta al Load.</summary>
    [Required]
    [MaxLength(32)]
    public string ModelType { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Symbol { get; set; } = string.Empty;

    [Required]
    [MaxLength(8)]
    public string Timeframe { get; set; } = string.Empty;

    public DateTime TrainingDataFrom { get; set; }
    public DateTime TrainingDataTo { get; set; }
    public int ForwardHorizon { get; set; }

    /// <summary>
    /// [1.V fase 2] Cosa predice il modello: "ForwardReturn" | "ForwardAbsReturn" |
    /// "ForwardRealizedVol". Persistito perché la semantica della predizione È il contratto:
    /// un modello di volatilità non può alimentare segnali long/short. Default retro-compatibile:
    /// tutti i modelli salvati prima del campo predicevano rendimenti.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string TargetKind { get; set; } = "ForwardReturn";

    /// <summary>
    /// True se la predizione è un rendimento atteso e può alimentare segnali long/short
    /// (MlStrategy, Champion). I modelli di rischio (vol) sono consumabili SOLO da sizing/
    /// vol-targeting. Non mappato da EF (sola lettura).
    /// </summary>
    public bool IsDirectional => TargetKind == "ForwardReturn";

    /// <summary>JSON: List&lt;SavedFactorSpecDto&gt; — nome fattore + parametri, per ricreare i FactorSpec al Load.</summary>
    [Required]
    public string FactorsJson { get; set; } = "[]";

    /// <summary>Il modello ML.NET serializzato (stesso formato prodotto da IReturnPredictor.Save).</summary>
    [Required]
    public byte[] ModelBytes { get; set; } = [];

    public int TrainRowCount { get; set; }
    public double TrainCorrelation { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // --- Registry / ciclo di vita (Fase 2) — campi additivi, default retro-compatibili ---

    /// <summary>Stadio nel registry. Default <see cref="ModelStage.Staging"/> (candidato appena salvato).</summary>
    public ModelStage Stage { get; set; } = ModelStage.Staging;

    /// <summary>Generazione del modello per (Symbol, Timeframe): informativa, assegnata dal registry.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Lineage: il run di experiment tracking che ha prodotto/valutato questo modello (se noto).</summary>
    public Guid? ExperimentRunId { get; set; }

    /// <summary>
    /// Deflated Sharpe (Fase 1) associato al modello: è il gate di promozione a Champion. null se non
    /// ancora misurato ⇒ non promuovibile a Champion (nessuna promozione "alla cieca").
    /// </summary>
    public double? DeflatedSharpe { get; set; }

    /// <summary>Quando è diventato Champion l'ultima volta (null se non lo è mai stato).</summary>
    public DateTime? PromotedAtUtc { get; set; }

    /// <summary>Quando è stato ritirato (null se non ritirato).</summary>
    public DateTime? RetiredAtUtc { get; set; }

    /// <summary>Motivo del ritiro (es. "superato da versione con DSR migliore", "drift: 3 feature in alert").</summary>
    [MaxLength(256)]
    public string? RetiredReason { get; set; }

    /// <summary>
    /// Marcatore "retrain accodato": valorizzato quando il ciclo drift chiede un riaddestramento. La
    /// piattaforma NON riaddestra da sola (scelta di sicurezza): è un segnale per l'operatore/UI.
    /// </summary>
    public DateTime? RetrainRequestedAtUtc { get; set; }
}
