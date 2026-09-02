using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [K54, PRD autonomia-piena — Fase 4, 2026-09-02] <b>Che cosa ha detto la ricerca DOPO che
/// l'aspettativa è stata scritta.</b>
///
/// <param name="Portato">Il valore che la gamba porta, scritto una volta allo schieramento.</param>
/// <param name="Ancora">Quando è stato scritto (<c>ExpectedSharpeAtUtc</c>).</param>
/// <param name="MisureDopo">Quante volte la STESSA identica ipotesi è stata rivalutata da allora.</param>
/// <param name="MedianaDopo">La mediana di quelle rivalutazioni: la stima corrente, non quella d'origine.</param>
/// </summary>
public sealed record ExpectationEvidence(
    decimal Portato,
    DateTime Ancora,
    int MisureDopo,
    decimal? MedianaDopo)
{
    /// <summary>
    /// Sotto questa soglia di misure successive non si dice nulla: due rivalutazioni non
    /// smentiscono niente, e un allarme costruito su di esse sarebbe rumore travestito da prova.
    /// </summary>
    public const int MinMisurePerGiudicare = 5;

    /// <summary>
    /// Quanto l'evidenza successiva deve discostarsi perché valga la pena dirlo. 1,5× in un verso o
    /// nell'altro: sotto, la differenza è ordinaria variabilità di finestra e allarmerebbe sempre.
    /// </summary>
    public const decimal SogliaDivergenza = 1.5m;

    /// <summary>Vero = ci sono abbastanza rivalutazioni per poterle contrapporre al numero portato.</summary>
    public bool Giudicabile => MisureDopo >= MinMisurePerGiudicare && MedianaDopo is not null;

    /// <summary>
    /// Vero = l'evidenza successiva <b>contraddice</b> il numero portato, in un verso o nell'altro.
    /// Il rapporto si calcola sempre col maggiore al numeratore: la divergenza non ha un verso
    /// privilegiato — un'aspettativa troppo bassa rende il giudizio indulgente esattamente come una
    /// troppo alta lo rende impossibile.
    /// </summary>
    public bool Contraddetta
    {
        get
        {
            if (!Giudicabile) return false;
            var a = Math.Abs(Portato);
            var b = Math.Abs(MedianaDopo!.Value);
            if (a == 0m || b == 0m) return a != b;
            return (a > b ? a / b : b / a) >= SogliaDivergenza;
        }
    }

    /// <summary>
    /// La stima da usare per giudicare: la mediana delle rivalutazioni quando c'è, il numero
    /// portato quando non c'è. <b>Non sovrascrive nulla</b>: il valore d'origine resta dov'è, e
    /// questa è la lettura corrente accanto a quella storica.
    /// </summary>
    public decimal Corrente => Giudicabile ? MedianaDopo!.Value : Portato;

    public string Racconto => !Giudicabile
        ? $"nessuna rivalutazione sufficiente da quando è stata scritta ({MisureDopo} su {MinMisurePerGiudicare} necessarie): resta l'unica misura che c'è"
        : Contraddetta
            ? $"da allora la stessa ipotesi è stata rivalutata {MisureDopo} volte e la mediana è {MedianaDopo:F3}, non {Portato:F3}"
            : $"confermata da {MisureDopo} rivalutazioni successive (mediana {MedianaDopo:F3})";
}

public interface IExpectationEvidenceReader
{
    /// <summary>L'evidenza successiva per una gamba, o <c>null</c> se non ha un'aspettativa timbrata.</summary>
    Task<ExpectationEvidence?> ReadAsync(
        EnsembleConfiguration cfg, EnsembleStrategy leg, CancellationToken ct = default);
}

/// <summary>
/// [K54] <b>L'aspettativa si scrive una volta e nessuno la ricontrolla mai più.</b>
///
/// <para><b>Il fatto misurato il 2026-09-02.</b> La corsia 6 porta
/// <c>expectedSharpe = 1,8754</c> per <c>GridMeanReversion DOGE/USDT 15m</c>. Quel numero è del
/// <b>21 agosto</b>. Da allora la caccia ha rivalutato <b>la stessa identica ipotesi</b> — stessi
/// parametri — altre undici volte:</para>
/// <code>
/// 08-21  1,8754  (14 trade)   ← il numero che la corsia porta ANCORA
/// 08-25  0,2329   08-26  0,4787   08-26  0,4720   08-27  0,3212
/// 08-28  0,3244   08-28  0,4531   08-29  0,6743   08-31  0,5902
/// 08-31  0,5522   08-31  0,5180   09-01  0,5901  (11 trade)
/// </code>
///
/// <para>Mediana delle undici: <b>0,479</b>. Il numero portato è <b>3,9 volte</b> tanto, ed è stato
/// prodotto <b>due giorni prima che il motore walk-forward venisse sostituito</b> (confine software
/// del 2026-08-23) — cioè da un motore che la piattaforma stessa ha smesso di considerare valido.
/// Il conteggio dei trade lo dice da solo: 14 → 13 → 11, la finestra è scorsa sotto l'ipotesi.</para>
///
/// <para><b>Dove finisce, e dove NON finisce.</b> Il ritiro di flotta usa una soglia
/// <i>assoluta</i> (<c>RetireSharpeThreshold</c>) e non è toccato. Chi usa l'aspettativa è
/// <see cref="Monitoring.StrategyDecayMonitor"/> — <c>ratio = realizzato / atteso</c> — i cui
/// verdetti compaiono su <c>/ensemble</c> e sulla scheda della Home. Quindi l'effetto è un
/// <b>falso allarme di decadimento</b>: la corsia 6 verrà confrontata con 1,875 quando ogni
/// evidenza recente dice 0,5, e «decadrà» comunque vada.</para>
///
/// <para><b>Perché non basta riscrivere il numero.</b> Sovrascriverlo cancellerebbe la storia — e su
/// quattro gambe su sette l'evidenza successiva <i>conferma</i> il numero portato (corsia 2
/// Supertrend: 28 rivalutazioni, mediana identica; corsia 5: 43 rivalutazioni, mediana identica).
/// Qui si <b>affianca</b>: il numero d'origine resta, la lettura corrente gli sta accanto, e chi
/// giudica usa quella dichiarando di farlo.</para>
/// </summary>
public sealed class ExpectationEvidenceReader(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ExpectationEvidenceReader> logger) : IExpectationEvidenceReader
{
    public async Task<ExpectationEvidence?> ReadAsync(
        EnsembleConfiguration cfg, EnsembleStrategy leg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(leg);

        // Senza ancora non si può dire «dopo»: è la stessa ragione per cui K39 non misura le gambe
        // senza timbro. Non sapere quando è stato scritto rende ogni confronto arbitrario.
        if (leg.ExpectedSharpe is not decimal portato || leg.ExpectedSharpeAtUtc is not DateTime ancora)
        {
            return null;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // L'identità dev'essere ESATTA — parametri compresi. Confrontare la sola terna
            // (strategia, simbolo, timeframe) mescolerebbe ipotesi diverse: verificato il
            // 2026-09-02, per Supertrend ADA/USDT 4h la terna dà 97 righe con mediana −0,108,
            // l'ipotesi esatta ne dà 45 con mediana 3,195. Il primo numero avrebbe fatto gridare
            // a un guasto che non c'era.
            var key = Pipeline.PipelineCandidateKey.Build(
                leg.StrategyName, cfg.Symbol, cfg.Timeframe, leg.Parameters);

            var dopo = await db.ResearchCandidates.AsNoTracking()
                .Where(c => c.CandidateKey == key && c.RunCompletedUtc > ancora)
                .Select(c => c.HoldoutSharpe)
                .ToListAsync(ct);

            return new ExpectationEvidence(portato, ancora, dopo.Count, Mediana(dopo));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fail-open sulla diagnostica (regola 4): non poter leggere l'evidenza successiva non
            // deve impedire di leggere il resto. Ma nemmeno fingere che l'evidenza dica «va bene»:
            // null significa «non lo so», e il chiamante lo mostra come tale.
            logger.LogDebug(ex, "Evidenza successiva non leggibile per la gamba {Leg}.", leg.StrategyId);
            return null;
        }
    }

    /// <summary>
    /// Mediana, non media: una singola rivalutazione anomala non deve spostare la stima corrente,
    /// che è esattamente il difetto che si sta correggendo — solo dall'altra parte.
    /// </summary>
    internal static decimal? Mediana(IReadOnlyList<decimal> valori)
    {
        if (valori.Count == 0) return null;
        var ordinati = valori.OrderBy(v => v).ToList();
        var m = ordinati.Count / 2;
        return ordinati.Count % 2 == 1 ? ordinati[m] : (ordinati[m - 1] + ordinati[m]) / 2m;
    }
}
