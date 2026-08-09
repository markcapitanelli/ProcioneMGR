using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Microstructure;

namespace ProcioneMGR.Services.ML;

/// <summary>Esito del filtro per un singolo fattore: tenuto o scartato, col verdetto del gate.</summary>
/// <param name="Spec">Il fattore valutato.</param>
/// <param name="Kept">True se entra nell'insieme selezionato.</param>
/// <param name="Outcome">Verdetto del gate (null per il capostipite: è il primo controllo, non un candidato).</param>
public sealed record IncrementalFilterEntry(FactorSpec Spec, bool Kept, IncrementalIcOutcome? Outcome);

/// <summary>Risultato del filtro incrementale: ogni fattore col suo verdetto, più i soli tenuti.</summary>
public sealed record IncrementalFilterResult(IReadOnlyList<IncrementalFilterEntry> Entries)
{
    public IReadOnlyList<FactorSpec> Kept => Entries.Where(e => e.Kept).Select(e => e.Spec).ToList();
    public int DroppedCount => Entries.Count(e => !e.Kept);
}

/// <summary>
/// [2.6 PRD-RISANAMENTO, chiude C-03/G-16] Il ponte fra la selezione per IC e
/// l'<see cref="IncrementalIcGate"/> del modulo Microstructure — il gate anti-ridondanza che
/// l'audit ha trovato completo, testato e irraggiungibile («il dato c'è, chi lo legge no»).
///
/// La domanda a cui risponde: con 158+ fattori Alpha158 nel catalogo, molti candidati portano la
/// STESSA informazione a orizzonti vicini. La selezione per |IC| assoluto li tiene tutti; questo
/// filtro GREEDY li passa in ordine di priorità (|IC| decrescente, l'ordine della selezione) e
/// tiene solo chi AGGIUNGE informazione oltre ai già tenuti — IC parziale contro l'insieme
/// corrente, con soglia di rumore e nullo per permutazione, tutto dentro il gate.
///
/// Statico e puro come il gate che usa: nessuna registrazione DI, deterministico a parità di
/// input (il gate ha il suo seed interno). Le convenzioni di calcolo (Compute del fattore,
/// ForwardReturns) sono le STESSE del <see cref="FactorEvaluator"/>: il filtro giudica sugli
/// stessi numeri della selezione, non su una copia divergente.
/// </summary>
public static class IncrementalFactorFilter
{
    /// <summary>
    /// Applica il filtro greedy. <paramref name="orderedByPriority"/> DEVE arrivare già ordinato
    /// (tipicamente per |IC| decrescente): il primo è il capostipite e si tiene sempre, gli altri
    /// devono guadagnarsi il posto contro i tenuti.
    /// </summary>
    public static IncrementalFilterResult Apply(
        IReadOnlyList<FactorSpec> orderedByPriority,
        IReadOnlyList<OhlcvData> candles,
        int forwardHorizon,
        IncrementalIcConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(orderedByPriority);
        ArgumentNullException.ThrowIfNull(candles);
        if (orderedByPriority.Count == 0)
        {
            return new IncrementalFilterResult([]);
        }

        var horizon = Math.Max(1, forwardHorizon);

        // Stesse convenzioni della selezione: serie del fattore da IAlphaFactor.Compute,
        // forward return dal FactorEvaluator. NaN dove il valore manca (maschera del gate).
        var evaluator = new FactorEvaluator();
        var fwdRaw = evaluator.ForwardReturns(candles, horizon);
        var fwd = new double[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            fwd[i] = fwdRaw[i].HasValue ? (double)fwdRaw[i]!.Value : double.NaN;
        }
        var forwardByHorizon = new Dictionary<int, IReadOnlyList<double>> { [horizon] = fwd };

        static IReadOnlyList<double> Series(FactorSpec spec, IReadOnlyList<OhlcvData> candles)
        {
            var raw = spec.Factor.Compute(candles, spec.Parameters);
            var values = new double[raw.Count];
            for (var i = 0; i < raw.Count; i++)
            {
                values[i] = raw[i].HasValue ? (double)raw[i]!.Value : double.NaN;
            }
            return values;
        }

        var entries = new List<IncrementalFilterEntry>(orderedByPriority.Count);
        var controls = new List<IcCandidate>
        {
            new(orderedByPriority[0].FeatureName, Series(orderedByPriority[0], candles)),
        };
        entries.Add(new IncrementalFilterEntry(orderedByPriority[0], Kept: true, Outcome: null));

        foreach (var spec in orderedByPriority.Skip(1))
        {
            var candidate = new IcCandidate(spec.FeatureName, Series(spec, candles));
            var report = IncrementalIcGate.Run(controls, forwardByHorizon, [candidate], config);
            var outcome = report.Outcomes.Count > 0 ? report.Outcomes[0] : null;
            var kept = outcome?.AddsInformation == true;

            entries.Add(new IncrementalFilterEntry(spec, kept, outcome));
            if (kept)
            {
                // Il tenuto diventa controllo: il prossimo deve aggiungere oltre a TUTTI i tenuti.
                controls.Add(candidate);
            }
        }

        return new IncrementalFilterResult(entries);
    }
}
