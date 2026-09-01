using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [K33, PRD autonomia-piena — Fase 2, 2026-09-01] <b>La stessa ipotesi non occupa due corsie.</b>
///
/// <para><b>Il fatto che l'ha resa necessaria.</b> Il 2026-08-31, alle 20:22:19 e alle 21:12:53 UTC,
/// <c>GridMeanReversion DOGE/USDT 15m</c> con parametri identici
/// (<c>{Direction 1, EntryRungs 1, StepPercent 2, AnchorPeriod 20}</c>), <c>ExpectedSharpe</c>
/// identico a ventotto cifre e la stessa <c>ExpectedTradesSource</c> («run e0cec50f: 14 trade su
/// 3,68 mesi») è stata schierata su <b>due</b> corsie. Differiscono solo per lo
/// <c>StrategyId</c>, che <c>EnsembleModels</c> conia con <c>Guid.NewGuid()</c> a ogni costruzione:
/// due identità diverse per una ipotesi sola. Risultato misurato: <b>20.000 USDT nominali su una
/// stima da 14 trade di holdout</b>, due slot del tetto grigio consumati da una prova sola, e la
/// flotta che sembrava larga cinque ipotesi mentre ne portava quattro.</para>
///
/// <para><b>Perché il predicato è a DUE gradini.</b> Misurato sulla coda del 2026-09-01: delle 16
/// proposte grigie schierabili, <b>una sola</b> collide per identità esatta con una gamba in corsia,
/// ma <b>tre</b> collidono per terna (strategia, coppia, timeframe) — due sono
/// <c>MacdTrend AAVE/USDT 4h</c> con <c>FastPeriod</c> identico e <c>SlowPeriod</c> 26 e 31 contro
/// il 21 già in corsa sulla corsia 3. Una guardia sulla sola <see cref="Pipeline.PipelineCandidateKey"/>
/// fermerebbe uno dei tre casi e lascerebbe passare gli altri due, che sono <i>lo stesso segnale
/// sullo stesso strumento con una manopola spostata</i> — cioè esattamente il danno che la guardia
/// dichiara di voler evitare.</para>
///
/// <para><b>E perché i due gradini non sono lo stesso divieto.</b> Identità uguale = <b>replica</b>,
/// non c'è lettura alternativa e si rifiuta. Terna uguale = <b>ipotesi vicina</b>, che può essere una
/// scelta legittima (due tarature dello stesso segnale sono un esperimento, se dichiarato). Il
/// secondo gradino è quindi governato da una manopola, e il suo default è rifiutare — perché
/// l'unica volta che è successo davvero nessuno l'aveva deciso.</para>
///
/// <para><b>La lista vuota non è «nessun duplicato».</b> Una corsia con configurazione illeggibile
/// restituisce <c>null</c> da <see cref="LaneSummary.ActiveCandidateKeys"/>, e qui viene contata come
/// <b>ignota</b>: non blocca (bloccare su ignoto fermerebbe ogni schieramento appena una config si
/// corrompe) ma viene <b>dichiarata</b> nel messaggio. È il verso opposto al tetto grigio, dove
/// l'ignoto conta come grigio, e la differenza è deliberata: lì l'ignoto restringe un permesso, qui
/// lo negherebbe del tutto.</para>
///
/// Pura e statica: si prova senza database, senza motore e senza corsie vive.
/// </summary>
public static class HypothesisGuard
{
    /// <summary>L'esito della guardia. <paramref name="Reason"/> è scritto per un umano che deve decidere cosa fare.</summary>
    public sealed record Verdict(bool Blocked, string? Reason, int UnknownLanes = 0);

    /// <summary>La terna (strategia, coppia, timeframe) di una chiave canonica, cioè la chiave senza l'impronta dei parametri.</summary>
    internal static string Triple(string candidateKey)
    {
        var hash = candidateKey.LastIndexOf(" #", StringComparison.Ordinal);
        return hash < 0 ? candidateKey : candidateKey[..hash];
    }

    /// <summary>
    /// Si può schierare <paramref name="candidateKey"/> sulla corsia <paramref name="targetLane"/>?
    /// </summary>
    /// <param name="lanes">Le corsie come le vede la directory. La corsia bersaglio è esclusa da sé.</param>
    /// <param name="targetLane">La corsia su cui si sta per scrivere.</param>
    /// <param name="candidateKey">L'identità canonica del candidato (<see cref="Pipeline.PipelineCandidateKey"/>).</param>
    /// <param name="blockOnTriple">
    /// Rifiutare anche la sola terna uguale (default vero). Falso = si rifiuta solo la replica esatta
    /// e la terna produce un avviso nel motivo, non un blocco.
    /// </param>
    /// <param name="onlyRunning">
    /// Confrontare solo con le corsie IN CORSA (default vero). Una corsia ferma porta ancora la sua
    /// configurazione, ma non sta spendendo osservazione né capitale: rifiutare per lei bloccherebbe
    /// il caso normale «riprendo la stessa ipotesi su un'altra corsia dopo averla fermata».
    /// </param>
    public static Verdict Check(
        IReadOnlyList<LaneSummary> lanes,
        int targetLane,
        string candidateKey,
        bool blockOnTriple = true,
        bool onlyRunning = true)
    {
        ArgumentNullException.ThrowIfNull(lanes);
        if (string.IsNullOrWhiteSpace(candidateKey))
        {
            // Senza identità non si confronta nulla, e non si inventa un permesso: chi chiama
            // sceglie se questo è un caso legittimo. Vedi il chiamante in GreyDeployer, che a
            // questo punto ha già rifiutato per conto suo.
            return new(false, null);
        }

        var rilevanti = lanes.Where(l => l.Id != targetLane).Where(l => !onlyRunning || l.IsRunning).ToList();
        var ignote = rilevanti.Count(l => l.ActiveCandidateKeys is null or { Count: 0 });

        var replica = rilevanti.FirstOrDefault(l =>
            l.ActiveCandidateKeys?.Any(k => string.Equals(k, candidateKey, StringComparison.Ordinal)) == true);
        if (replica is not null)
        {
            return new(true,
                $"La corsia {replica.Id} sta gia' eseguendo ESATTAMENTE questa ipotesi ({candidateKey}): "
                + "schierarla di nuovo non e' diversificazione, e' la stessa scommessa con due dotazioni di "
                + "capitale e due slot del tetto grigio. Se la si vuole davvero replicare, va fermata l'altra "
                + "corsia o dichiarato il motivo a mano.",
                ignote);
        }

        var terna = Triple(candidateKey);
        var vicina = rilevanti.FirstOrDefault(l =>
            l.ActiveCandidateKeys?.Any(k => string.Equals(Triple(k), terna, StringComparison.Ordinal)) == true);
        if (vicina is not null)
        {
            var motivo =
                $"La corsia {vicina.Id} sta gia' eseguendo la stessa terna ({terna}) con parametri diversi: "
                + "e' lo stesso segnale sullo stesso strumento con una manopola spostata, non un'ipotesi "
                + "indipendente. Le due corsie sbaglieranno insieme, e il tetto grigio le conta come due prove.";
            return blockOnTriple
                ? new(true, motivo + " (Fleet:BlockDuplicateTriple e' acceso.)", ignote)
                : new(false, motivo + " Schierata comunque: Fleet:BlockDuplicateTriple e' spento.", ignote);
        }

        return new(false, ignote > 0
            ? $"Nessun duplicato fra le corsie leggibili, ma {ignote} corsie non dichiarano le proprie gambe: "
              + "il confronto e' parziale."
            : null,
            ignote);
    }
}
