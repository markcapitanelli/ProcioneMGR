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
    int ConfigurationId, int Runs, int GreyKeys, double KeysPerRun, HuntYieldVerdict Verdict,
    double HoursSpent = 0, double MedianRunMinutes = 0)
{
    /// <summary>
    /// [K54b, 2026-09-02] Chiavi grigie distinte per ORA di caccia. E' la lettura che regge meglio
    /// il confronto: <see cref="KeysPerRun"/> ha il numero di run al denominatore, e quel numero e'
    /// una scelta di pianificazione, non una proprieta' della caccia.
    ///
    /// <para><b>Il numero che lo dimostra:</b> stessa configurazione, stesso motore, stesso
    /// universo — cfg 17 fino al 20/08 rende 0,477 per run, nei soli 21-22/08 rende 7,250. Il tasso
    /// di grigi resta piatto (14,6% contro 13,3%): a muoversi e' solo quanti run stanno sotto la
    /// frazione. Un fattore 15 dal nulla.</para>
    /// </summary>
    public double KeysPerHour => HoursSpent > 0 ? GreyKeys / HoursSpent : 0;
};

/// <summary>Il verdetto su una configurazione di caccia.</summary>
public enum HuntYieldVerdict
{
    /// <summary>Meno di <see cref="HuntYield.MinRunsForVerdict"/> run: non si giudica. L'ignoranza non condanna.</summary>
    TroppoPresto,

    /// <summary>Resa in linea con le altre configurazioni attive.</summary>
    Produttiva,

    /// <summary>Resa molto sotto la mediana delle attive E costo non trascurabile: budget sprecato.</summary>
    Sterile,

    /// <summary>
    /// [K54b, 2026-09-02] Non gira piu': nessun run nella finestra, oppure un costo cosi' basso che
    /// non c'e' niente da liberare.
    ///
    /// <para><b>Perche' e' un verdetto a se'.</b> La prima versione di K50 dichiarava «sterile» la
    /// configurazione 8. Misurato il 2026-09-02: la config 8 e' ferma dal <b>20 agosto</b> e
    /// consuma <b>0 ore su 48,7</b> al mese. Metterla in sonno non libera niente — non era una
    /// decisione, era un'etichetta. E non l'aveva spenta un giudizio: l'aveva dimenticata un gate
    /// (commit 932eb21) introdotto con il commento «le campagne reali sono gia' tutte a timeframe
    /// singolo, quindi nessun run esistente cambia», che era falso — 28 run su 29 della config 8
    /// sono a timeframe misti.</para>
    ///
    /// <para><b>Uno spreco e' resa bassa MOLTIPLICATA per costo reale.</b> Con la sola resa si
    /// condanna chi non costa nulla e si assolve chi costa tutto: la configurazione 19 consuma la
    /// mediana di 43,7 minuti a run — undici volte la 17 — e non ha schierato una gamba.</para>
    Dormiente,
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
    /// <summary>
    /// [K54b] Ore di caccia sotto cui non si condanna nessuno: una configurazione che consuma meno
    /// di questo non e' uno spreco, qualunque sia la sua resa. Mezz'ora sulla finestra osservata e'
    /// gia' generoso — la config 8, dichiarata «sterile» dalla prima versione di K50, ne consumava
    /// ZERO perche' era ferma da tredici giorni.
    /// </summary>
    public const double MinHoursForWasteVerdict = 0.5;

    public static List<HuntYieldRow> Judge(IReadOnlyList<(int ConfigurationId, int Runs, int GreyKeys)> osservazioni)
        => Judge([.. osservazioni.Select(o => (o.ConfigurationId, o.Runs, o.GreyKeys, 0.0, 0.0))]);

    /// <summary>
    /// Il giudizio, dato rese E costi. Puro: nessun database, nessun orologio.
    /// </summary>
    public static List<HuntYieldRow> Judge(
        IReadOnlyList<(int ConfigurationId, int Runs, int GreyKeys, double HoursSpent, double MedianRunMinutes)> osservazioni)
    {
        ArgumentNullException.ThrowIfNull(osservazioni);
        if (osservazioni.Count == 0) return [];

        var rese = osservazioni
            .Select(o => (o.ConfigurationId, o.Runs, o.GreyKeys, o.HoursSpent, o.MedianRunMinutes,
                          PerRun: o.Runs > 0 ? (double)o.GreyKeys / o.Runs : 0))
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
            Verdetto(r.Runs, r.PerRun, r.HoursSpent, mediana),
            r.HoursSpent, r.MedianRunMinutes))
            // [K54b] Ordine per COSTO decrescente: la domanda che il pannello deve far venire in
            // mente e' «dove sono le ore», non «chi rende di piu'». A parita' di costo, la resa.
            .OrderByDescending(r => r.HoursSpent).ThenByDescending(r => r.KeysPerRun)
            .ToList();
    }

    private static HuntYieldVerdict Verdetto(int runs, double perRun, double ore, double mediana)
    {
        if (runs < MinRunsForVerdict) return HuntYieldVerdict.TroppoPresto;

        // [K54b] Il costo PRIMA della resa: condannare una caccia che non consuma nulla e' scrivere
        // un'etichetta, non prendere una decisione. La config 8 e' stata dichiarata «sterile»
        // mentre era ferma da tredici giorni.
        if (ore < MinHoursForWasteVerdict) return HuntYieldVerdict.Dormiente;

        // Con mediana zero nessuno rende: non è una configurazione che è rotta, è la caccia
        // intera — e condannarne una sarebbe scegliere un capro espiatorio.
        return mediana <= 0 || perRun >= mediana * SterileFractionOfMedian
            ? HuntYieldVerdict.Produttiva
            : HuntYieldVerdict.Sterile;
    }

    /// <summary>La frase da mostrare accanto alla configurazione, col numero che la sostiene.</summary>
    public static string Describe(HuntYieldRow r, double medianaPerRun) => r.Verdict switch
    {
        HuntYieldVerdict.TroppoPresto =>
            $"{r.Runs} run: sotto i {MinRunsForVerdict} necessari per un giudizio, non si dice nulla",
        HuntYieldVerdict.Dormiente =>
            $"{r.Runs} run per {r.HoursSpent:F1} ore nella finestra: consuma troppo poco perche' "
            + "metterla in sonno liberi qualcosa. Se non gira piu', il posto dove guardare non e' la resa: "
            + "e' PERCHE' ha smesso di essere invocata",
        HuntYieldVerdict.Sterile =>
            $"{r.GreyKeys} candidati grigi distinti in {r.Runs} run ({r.KeysPerRun:F2}/run) contro una mediana di "
            + $"{medianaPerRun:F2}, e COSTA {r.HoursSpent:F1} ore ({r.MedianRunMinutes:F0} min a run): "
            + $"resa {(medianaPerRun > 0 ? medianaPerRun / Math.Max(r.KeysPerRun, 0.01) : 0):F0} volte più bassa "
            + "a budget speso davvero",
        _ => $"{r.GreyKeys} candidati grigi distinti in {r.Runs} run ({r.KeysPerRun:F2}/run, "
             + $"{r.KeysPerHour:F2}/ora su {r.HoursSpent:F1} ore)",
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

        // [K54b] Anche la DURATA: senza il costo, la resa da sola condanna chi non consuma niente
        // e assolve chi consuma tutto. Wall-clock e non CPU — nessuna colonna misura la CPU vera —
        // quindi e' un limite SUPERIORE, e almeno due configurazioni si sovrappongono ogni notte.
        var run = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.StartedAt > da)
            .Select(r => new { r.Id, r.ConfigurationId, r.StartedAt, r.CompletedAt })
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
            .Select(g =>
            {
                var minuti = g
                    .Where(r => r.CompletedAt is not null)
                    .Select(r => (r.CompletedAt!.Value - r.StartedAt).TotalMinutes)
                    .Where(m => m >= 0)
                    .OrderBy(m => m)
                    .ToList();
                return (
                    ConfigurationId: g.Key,
                    Runs: g.Count(),
                    GreyKeys: chiavi.Where(c => configDiRun.TryGetValue(c.RunId, out var cfg) && cfg == g.Key)
                        .Select(c => c.CandidateKey).Distinct(StringComparer.Ordinal).Count(),
                    HoursSpent: minuti.Sum() / 60.0,
                    MedianRunMinutes: minuti.Count == 0
                        ? 0
                        : minuti.Count % 2 == 1
                            ? minuti[minuti.Count / 2]
                            : (minuti[minuti.Count / 2 - 1] + minuti[minuti.Count / 2]) / 2.0);
            })
            .ToList();

        return HuntYield.Judge(osservazioni);
    }
}
