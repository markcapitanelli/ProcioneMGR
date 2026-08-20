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
    ///
    /// <para><b>[M2, 2026-08-20] Questa colonna ha DUE scrittori che scrivono due statistiche
    /// diverse con lo stesso nome</b>, ed è la ragione per cui esistono i due campi qui sotto.
    /// Il salvataggio da /ml calcola un DSR con N = 1 tentativo — che per costruzione collassa sul
    /// Probabilistic Sharpe, cioè SR* = 0 e nessuna deflazione — su un'equity senza slippage né
    /// funding. La pipeline scrive invece un DSR deflazionato su N = max(candidati, combinazioni
    /// esplorate) × rapporto di collasso, nella pratica centinaia o migliaia, misurato sull'holdout
    /// coi costi pieni. Il primo produce sistematicamente il numero più alto. Confrontarli fra loro
    /// — che è ciò che fa il gate «batti l'incumbent» — è confrontare due grandezze diverse.</para>
    /// </summary>
    public double? DeflatedSharpe { get; set; }

    /// <summary>
    /// [M2] Il numero di tentativi con cui <see cref="DeflatedSharpe"/> è stato deflazionato.
    /// null = ignoto (righe scritte prima del 2026-08-20): in quel caso il valore non è
    /// confrontabile con nessun altro e il registry lo dichiara invece di fingere che lo sia.
    /// </summary>
    public int? DeflatedSharpeTrials { get; set; }

    /// <summary>
    /// [M2] Chi ha prodotto <see cref="DeflatedSharpe"/>: <c>"ml-lab"</c> (pagina /ml: un solo
    /// track, nessuna deflazione, backtest senza slippage né funding) oppure <c>"pipeline"</c>
    /// (gate anti-overfitting sull'holdout, costi pieni). null nelle righe storiche.
    /// </summary>
    [MaxLength(32)]
    public string? DeflatedSharpeSource { get; set; }

    /// <summary>[M2] Valore di <see cref="DeflatedSharpeSource"/> scritto dalla pagina /ml.</summary>
    public const string DsrSourceMlLab = "ml-lab";

    /// <summary>[M2] Valore di <see cref="DeflatedSharpeSource"/> scritto dal gate della pipeline.</summary>
    public const string DsrSourcePipeline = "pipeline";

    /// <summary>
    /// [M2b, 2026-08-20] Il modello è passato per la validazione della pipeline ed è stato
    /// <b>scartato prima</b> di arrivare al gate DSR (tipicamente Sharpe holdout sotto soglia o
    /// troppo pochi trade), quindi un Deflated Sharpe non è mai stato calcolato:
    /// <see cref="DeflatedSharpe"/> resta null <i>di proposito</i>.
    ///
    /// <para>Esiste perché «nessun numero» e «numero mai misurato» sono due fatti diversi che la
    /// colonna vuota confondeva. Al 2026-08-20 tutti i 164 modelli salvati erano nel primo caso e
    /// /registry li mostrava come il secondo.</para>
    /// </summary>
    public const string DsrSourceRejectedBeforeGate = "pipeline:scartato";

    /// <summary>
    /// [M2c, 2026-08-20] Il modello è stato addestrato e salvato, ma <b>non è mai diventato un
    /// candidato</b>: la correlazione di test sul set di verifica non ha superato
    /// <c>minTestCorrelation</c>, quindi non è mai entrato nella validazione holdout e nessun
    /// giudizio esiste su di lui.
    ///
    /// <para>È il caso di gran lunga più numeroso — al 2026-08-20, <b>114 modelli su 164</b> — e il
    /// più frainteso: la riga esiste nel registry con i suoi pulsanti di promozione, come se fosse
    /// materiale in attesa di una decisione, mentre il sistema lo ha già messo da parte prima ancora
    /// di provarlo. Tre fatti diversi («mai proposto», «proposto e bocciato», «giudicato con un
    /// numero») meritano tre parole diverse.</para>
    /// </summary>
    public const string DsrSourceNeverValidated = "pipeline:non-candidato";

    /// <summary>
    /// [M2] Due DSR sono confrontabili solo se entrambi dichiarano il proprio N e i due N non
    /// differiscono di oltre un ordine di grandezza. Un DSR su 1 tentativo e uno su 800 non
    /// misurano la stessa cosa: il primo non è deflazionato affatto, e metterli in una
    /// disuguaglianza produce un verdetto che sembra numerico ed è arbitrario.
    /// </summary>
    public static bool DsrComparable(int? trialsA, int? trialsB)
    {
        if (trialsA is not > 0 || trialsB is not > 0) return false;
        var hi = Math.Max(trialsA.Value, trialsB.Value);
        var lo = Math.Min(trialsA.Value, trialsB.Value);
        return hi <= lo * 10;
    }

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
