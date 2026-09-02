using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>La resa di una configurazione di caccia sulla finestra osservata.</summary>
/// <param name="ConfigurationId">La configurazione.</param>
/// <param name="Runs">Run completati nella finestra.</param>
/// <param name="GreyKeys">Chiavi candidate DISTINTE in fascia grigia prodotte da quei run.</param>
/// <param name="KeysPerRun">Resa: chiavi grigie per run. È il numero che si confronta.</param>
/// <param name="Verdict">Il giudizio, o perché non se ne dà uno.</param>
public sealed record HuntYieldRow(
    int ConfigurationId, int Runs, int GreyKeys, double KeysPerRun, HuntYieldVerdict Verdict);

/// <summary>Il verdetto su una configurazione di caccia.</summary>
public enum HuntYieldVerdict
{
    /// <summary>Meno di <see cref="HuntYield.MinRunsForVerdict"/> run: non si giudica. L'ignoranza non condanna.</summary>
    TroppoPresto,

    /// <summary>Resa in linea con le altre configurazioni attive.</summary>
    Produttiva,

    /// <summary>Resa molto sotto la mediana delle attive: sta consumando budget di caccia per niente.</summary>
    Sterile,
}

/// <summary>
/// [K50, PRD autonomia-piena — Fase 4, 2026-09-02] <b>Quale caccia sta producendo, e quale no.</b>
///
/// <para><b>Il fatto.</b> Nessuno mette in sonno una configurazione che non rende: il PRD lo elenca
/// come primo dei «cinque che nessuno fa» — <i>«config 17 e 18 hanno consumato 57 tentativi ciascuna
/// e prodotto zero gambe su 119 run: nessuno le mette in sonno, nessuno sposta il budget
/// altrove»</i>.</para>
///
/// <para><b>E il criterio ovvio è una trappola.</b> Misurato il 2026-09-02 sui 30 giorni:
/// <c>ensembleLegs</c> è vuoto in <b>173 run su 173</b>, su tutte e cinque le configurazioni attive.
/// «Zero gambe assemblate» non distingue una caccia morta da una viva — le addormenterebbe
/// <b>tutte</b>, perché il collo di bottiglia è il gate, non la caccia. È lo stesso errore di
/// misurare una corsia su un criterio che nessuna corsia può raggiungere.</para>
///
/// <para><b>Ciò che discrimina è la fascia grigia.</b> Le chiavi candidate DISTINTE in banda grigia
/// per run, misurate sulle stesse trenta giornate:</para>
///
/// <code>
/// cfg 17 : 62 run →  82 chiavi grigie → 1,32 per run
/// cfg 18 : 63 run →  26 chiavi grigie → 0,41
/// cfg 20 : 15 run →  13 chiavi grigie → 0,87
/// cfg 19 : 16 run →   5 chiavi grigie → 0,31
/// cfg  8 : 17 run →   1 chiave  grigia → 0,06   ← ventidue volte meno della migliore
/// </code>
///
/// <para><b>Il confronto è RELATIVO, e va detto perché.</b> Configurazioni diverse cacciano universi
/// e timeframe diversi: una resa assoluta bassa può essere la domanda che è più difficile, non la
/// caccia che è rotta. Ciò che non è difendibile è consumare il 10% del budget per rendere il 5% di
/// quanto rende la mediana — e quello si vede solo confrontando.</para>
///
/// <para><b>Chiavi distinte, non righe.</b> La caccia rigira gli stessi parametri sugli stessi
/// mercati: contare le righe misurerebbe quante volte ha ritrovato la stessa cosa. È la trappola
/// numero uno di questo database (18.267 righe per 1.028 chiavi), e qui produrrebbe l'errore
/// peggiore: premiare la configurazione più ripetitiva.</para>
///
/// Puro e statico nel giudizio: si prova senza database.
/// </summary>
public static class HuntYield
{
    /// <summary>
    /// Run minimi prima di dare un verdetto. Sotto, si tace: con cinque run una resa di zero è
    /// ordinaria anche per una caccia sana, e condannare su quel campione sarebbe rumore.
    /// </summary>
    public const int MinRunsForVerdict = 12;

    /// <summary>
    /// Frazione della mediana sotto cui una configurazione è dichiarata sterile. 0,25 = rende meno
    /// di un quarto di quanto rende la caccia mediana. Deliberatamente largo: il verdetto deve
    /// cogliere i casi fuori scala (il fattore 22 misurato), non arbitrare fra vicini.
    /// </summary>
    public const double SterileFractionOfMedian = 0.25;

    /// <summary>
    /// Il giudizio, dato tutte le rese osservate. Puro: nessun database, nessun orologio.
    /// </summary>
    public static List<HuntYieldRow> Judge(IReadOnlyList<(int ConfigurationId, int Runs, int GreyKeys)> osservazioni)
    {
        ArgumentNullException.ThrowIfNull(osservazioni);
        if (osservazioni.Count == 0) return [];

        var rese = osservazioni
            .Select(o => (o.ConfigurationId, o.Runs, o.GreyKeys, PerRun: o.Runs > 0 ? (double)o.GreyKeys / o.Runs : 0))
            .ToList();

        // La mediana si calcola SOLO sulle configurazioni giudicabili: includere quelle con tre run
        // farebbe muovere il metro con cui si giudicano le altre, e il metro non deve dipendere da
        // chi non si sta giudicando.
        var giudicabili = rese.Where(r => r.Runs >= MinRunsForVerdict).Select(r => r.PerRun).OrderBy(x => x).ToList();
        var mediana = giudicabili.Count == 0
            ? 0
            : giudicabili.Count % 2 == 1
                ? giudicabili[giudicabili.Count / 2]
                : (giudicabili[giudicabili.Count / 2 - 1] + giudicabili[giudicabili.Count / 2]) / 2.0;

        return rese.Select(r => new HuntYieldRow(
            r.ConfigurationId, r.Runs, r.GreyKeys, r.PerRun,
            r.Runs < MinRunsForVerdict
                ? HuntYieldVerdict.TroppoPresto
                // Con mediana zero nessuno rende: non è una configurazione che è rotta, è la caccia
                // intera — e condannarne una sarebbe scegliere un capro espiatorio.
                : mediana <= 0 || r.PerRun >= mediana * SterileFractionOfMedian
                    ? HuntYieldVerdict.Produttiva
                    : HuntYieldVerdict.Sterile))
            .OrderByDescending(r => r.KeysPerRun)
            .ToList();
    }

    /// <summary>La frase da mostrare accanto alla configurazione, col numero che la sostiene.</summary>
    public static string Describe(HuntYieldRow r, double medianaPerRun) => r.Verdict switch
    {
        HuntYieldVerdict.TroppoPresto =>
            $"{r.Runs} run: sotto i {MinRunsForVerdict} necessari per un giudizio, non si dice nulla",
        HuntYieldVerdict.Sterile =>
            $"{r.GreyKeys} candidati grigi distinti in {r.Runs} run ({r.KeysPerRun:F2}/run) contro una mediana di "
            + $"{medianaPerRun:F2}: sta consumando budget di caccia per una resa {(medianaPerRun > 0 ? medianaPerRun / Math.Max(r.KeysPerRun, 0.01) : 0):F0} volte più bassa",
        _ => $"{r.GreyKeys} candidati grigi distinti in {r.Runs} run ({r.KeysPerRun:F2}/run)",
    };
}

/// <summary>Legge la resa delle configurazioni di caccia dall'archivio dei run.</summary>
public interface IHuntYieldReader
{
    Task<IReadOnlyList<HuntYieldRow>> ReadAsync(int windowDays = 30, CancellationToken ct = default);
}

public sealed class HuntYieldReader(IDbContextFactory<ApplicationDbContext> dbFactory) : IHuntYieldReader
{
    public async Task<IReadOnlyList<HuntYieldRow>> ReadAsync(int windowDays = 30, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var da = DateTime.UtcNow.AddDays(-Math.Max(1, windowDays));

        var run = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.StartedAt > da)
            .Select(r => new { r.Id, r.ConfigurationId })
            .ToListAsync(ct);
        if (run.Count == 0) return [];

        var ids = run.Select(r => r.Id).ToList();
        // Chiavi DISTINTE per run, poi aggregate per configurazione: contare le righe premierebbe
        // la caccia più ripetitiva invece della più produttiva.
        var chiavi = await db.ResearchCandidates.AsNoTracking()
            .Where(c => ids.Contains(c.RunId) && c.IsGrey)
            .Select(c => new { c.RunId, c.CandidateKey })
            .Distinct()
            .ToListAsync(ct);

        var configDiRun = run.ToDictionary(r => r.Id, r => r.ConfigurationId);
        var osservazioni = run
            .GroupBy(r => r.ConfigurationId)
            .Select(g => (
                ConfigurationId: g.Key,
                Runs: g.Count(),
                GreyKeys: chiavi.Where(c => configDiRun.TryGetValue(c.RunId, out var cfg) && cfg == g.Key)
                    .Select(c => c.CandidateKey).Distinct(StringComparer.Ordinal).Count()))
            .ToList();

        return HuntYield.Judge(osservazioni);
    }
}
