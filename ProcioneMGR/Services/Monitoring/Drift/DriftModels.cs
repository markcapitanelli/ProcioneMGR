namespace ProcioneMGR.Services.Monitoring.Drift;

/// <summary>Gravità del drift rilevato su una feature. None &lt; Warning &lt; Alert.</summary>
public enum DriftSeverity
{
    None = 0,
    Warning = 1,
    Alert = 2,
}

/// <summary>
/// Esito di UN test di drift su una feature (una distribuzione di riferimento vs una corrente).
/// <see cref="Score"/> è la statistica del test (PSI, D di KS, statistica di Page-Hinkley);
/// <see cref="PValue"/> è valorizzato solo dove il test ne produce uno (KS).
/// </summary>
public sealed record DriftResult(
    string Detector,
    double Score,
    double? PValue,
    DriftSeverity Severity,
    string Detail);

/// <summary>
/// Soglie dei test di drift. Default coerenti con la prassi (PSI &gt;0.2 warning, &gt;0.25 alert;
/// KS p&lt;0.05 warning, p&lt;0.01 alert). Page-Hinkley lavora su z-score (standardizzati sulla
/// distribuzione di riferimento) così le sue soglie sono indipendenti dalla scala della feature.
/// </summary>
public sealed class DriftThresholds
{
    public int PsiBins { get; set; } = 10;
    public double PsiWarning { get; set; } = 0.2;
    public double PsiAlert { get; set; } = 0.25;

    public double KsPValueWarning { get; set; } = 0.05;
    public double KsPValueAlert { get; set; } = 0.01;

    /// <summary>
    /// Tolleranza (in deviazioni standard di riferimento) prima che Page-Hinkley accumuli: assorbe
    /// il rumore di uno stream stazionario così solo uno spostamento PERSISTENTE della media supera
    /// le soglie. Le soglie sono tarate per stream nell'ordine di 100-500 osservazioni: la
    /// statistica cresce ~(|z̄|−delta)·N su un vero shift, restando piccola sul rumore.
    /// </summary>
    public double PageHinkleyDelta { get; set; } = 1.0;
    public double PageHinkleyWarning { get; set; } = 25.0;
    public double PageHinkleyAlert { get; set; } = 50.0;

    /// <summary>Numero minimo di osservazioni valide (per lato) sotto cui il test non è affidabile.</summary>
    public int MinObservations { get; set; } = 20;
}

/// <summary>
/// Report di drift per UNA feature di un modello: distribuzione di training (reference) vs finestra
/// recente (current), con l'esito di ciascun detector. <see cref="Overall"/> è la gravità massima.
/// </summary>
public sealed class FactorDriftReport
{
    public string FeatureName { get; init; } = string.Empty;
    public int ReferenceCount { get; init; }
    public int CurrentCount { get; init; }
    public IReadOnlyList<DriftResult> Results { get; init; } = [];

    public DriftSeverity Overall => Results.Count == 0 ? DriftSeverity.None : Results.Max(r => r.Severity);
}

/// <summary>
/// ENTITÀ EF (tabella <c>DriftCheckResults</c>): esito PERSISTITO di un check di drift su un
/// modello, una riga per modello per tick del <see cref="FeatureDriftWorker"/> — anche quando è
/// tutto pulito, così l'assenza di righe si distingue da "il worker non sta girando". Prima di
/// questa tabella gli esiti vivevano solo nei log: la UI (/admin/autonomy) non poteva mostrare
/// né l'ultimo esito né lo storico. Prune automatico oltre i 90 giorni nel worker.
/// </summary>
public class DriftCheckResult
{
    public int Id { get; set; }

    /// <summary>Quando è stato eseguito il check (UTC).</summary>
    public DateTime CheckedAtUtc { get; set; }

    /// <summary>Id del <c>SavedMlModel</c> valutato. NON è FK: la riga sopravvive alla cancellazione del modello.</summary>
    public int ModelId { get; set; }

    /// <summary>Nome del modello, denormalizzato per leggibilità storica.</summary>
    public string ModelName { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Feature totali valutate; 0 = check saltato (es. candele recenti insufficienti).</summary>
    public int TotalFeatures { get; set; }

    /// <summary>Feature con drift (Warning o Alert).</summary>
    public int DriftingFeatures { get; set; }

    /// <summary>Feature in Alert (sottoinsieme di <see cref="DriftingFeatures"/>).</summary>
    public int AlertFeatures { get; set; }

    /// <summary>Gravità complessiva del check (max tra le feature).</summary>
    public DriftSeverity Overall { get; set; }

    /// <summary>
    /// Top-5 feature in drift, JSON <c>[{"name","severity","detector","score"}]</c> — abbastanza
    /// per la tabella in UI senza persistire l'intero report per-feature.
    /// </summary>
    public string? TopFeaturesJson { get; set; }

    /// <summary>True se QUESTO check ha fatto ritirare un Champion (ciclo chiuso del registry).</summary>
    public bool ChampionRetired { get; set; }

    /// <summary>
    /// [I6] <b>Perché il check non ha prodotto un verdetto</b>, quando non l'ha prodotto.
    /// <c>null</c> = il check è stato eseguito davvero e <see cref="Overall"/> è un giudizio.
    ///
    /// <para>È la colonna che trasforma questa tabella da «rassicurante a prescindere» a
    /// falsificabile. Prima, candele insufficienti, modello senza feature dichiarate o finestra
    /// corrente sovrapposta al periodo di training producevano tutti <c>Overall = None</c> — cioè
    /// il colore verde, indistinguibile da «ho guardato e va tutto bene». Il commento su
    /// <see cref="TotalFeatures"/> diceva «0 = check saltato», ma nessuna superficie lo leggeva
    /// così e nessun verdetto lo dichiarava: la distinzione esisteva nella testa di chi aveva
    /// scritto il campo, non nel prodotto.</para>
    ///
    /// <para>Il caso della <b>sovrapposizione col training</b> è il più insidioso dei tre, perché è
    /// l'unico che non sembra un guasto: confrontare la finestra recente con la distribuzione di
    /// training quando la prima è contenuta nella seconda significa confrontare un campione con la
    /// popolazione che lo contiene — non può quasi mai allarmare, e quel silenzio si legge come
    /// stabilità.</para>
    /// </summary>
    public string? SkipReason { get; set; }

    /// <summary>Il check ha prodotto un giudizio, non un rinvio.</summary>
    public bool IsVerdict => string.IsNullOrEmpty(SkipReason);
}
