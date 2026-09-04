using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Llm.Committee;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Una caccia proposta: un buco di copertura, con il prezzo e il modello da cui copia la forma.</summary>
/// <param name="Timeframe">Il timeframe del buco.</param>
/// <param name="Simboli">Le serie scoperte che occuperebbe (già seguite in watchlist).</param>
/// <param name="ModelloId">La configurazione in rotazione da cui copiare fasi e finestre: stesso timeframe, o la più vicina.</param>
/// <param name="MinutiStimati">Costo stimato per run, scalato dal modello sul numero di serie.</param>
/// <param name="CadenzaOre">La cadenza con cui entrerebbe, scelta per stare nel budget residuo.</param>
public sealed record CacciaProposta(
    string Timeframe, IReadOnlyList<string> Simboli, int ModelloId, double MinutiStimati, int CadenzaOre)
{
    public string Id => $"{Timeframe}:{Simboli.Count}";
    public double OreAlMese => CadenzaOre <= 0 ? 0 : MinutiStimati / 60.0 * (30.0 * 24.0 / CadenzaOre);

    public string Etichetta =>
        $"{Timeframe} su {Simboli.Count} serie mai cacciate ({string.Join(", ", Simboli.Take(4))}"
        + (Simboli.Count > 4 ? ", …" : "") + $") — ~{MinutiStimati:F0} min/run, {OreAlMese:F1} h/mese a {CadenzaOre}h di cadenza";
}

/// <summary>L'esito: che cosa è stato proposto, chi ha scelto, e perché.</summary>
public sealed record EsitoProposta(
    IReadOnlyList<CacciaProposta> Menu, CacciaProposta? Scelta, string Fonte, string Motivazione);

public interface IHuntProposer
{
    /// <param name="inRotazione">Le configurazioni in rotazione, da cui copiare la forma.</param>
    /// <param name="oreResidueAlMese">
    /// Ore che restano nel tetto mensile. <c>null</c> = <b>nessun tetto impostato</b>: la cadenza
    /// proposta è quella del modello (o 24h), e lo si dice — prima arrivava qui
    /// <c>double.MaxValue/4</c>, la proposta usciva alla cadenza più fitta e il comitato riceveva
    /// «Budget residuo: 4494232837…,0 ore/mese» nel prompt.
    /// </param>
    Task<EsitoProposta> ProponiAsync(
        IReadOnlyCollection<int> inRotazione, double? oreResidueAlMese, CancellationToken ct = default);
}

/// <summary>
/// [K60, PRD autonomia-piena — Fase 4, 2026-09-03] <b>Quale caccia aggiungere: la decidono i buchi
/// misurati, non l'ispirazione.</b>
///
/// <para><b>Il fatto che la rende possibile.</b> Al 2026-09-03 la piattaforma tiene aggiornate 222
/// celle (serie × timeframe) e ne caccia 125: <b>97 si pagano a ogni giro dell'ingestione e non le
/// guarda nessuno</b>. Il buco più grande è a 15m (34 serie su 44) e a 5m (20 su 30). Non serve
/// inventare che cosa cercare: basta leggere che cosa manca.</para>
///
/// <para><b>L'AI sceglie dentro un menù chiuso, non genera configurazioni.</b> È la stessa forma di
/// AF3, e la ragione è la stessa: un modello che scrive JSON di configurazione produce universi
/// enormi, finestre assurde e duplicati mascherati — e <i>ognuno di quelli è tentativi in più che
/// nessun gate conta</i>, perché il DSR deflaziona per i tentativi del proprio run e non vede le
/// altre cacce. Qui il <b>codice</b> costruisce proposte già valide (niente timeframe misti, solo
/// serie abilitate e mai cacciate, forma copiata da una configurazione che gira davvero, costo
/// stimato entro il budget residuo) e l'AI <b>sceglie e argomenta</b>.</para>
///
/// <para><b>Nessuna proposta si adotta da sola.</b> Una caccia nuova costa ore e aggiunge tentativi:
/// entrambe le cose sono decisioni del proprietario. Questo servizio produce una proposta con il suo
/// prezzo; chi la adotta è un click, o un flag acceso apposta.</para>
/// </summary>
public sealed class HuntProposer(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHuntCoverageReader coverage,
    ILogger<HuntProposer> logger,
    IAiCommittee? committee = null) : IHuntProposer
{
    /// <summary>
    /// Serie per proposta. Dieci è la taglia delle cacce intraday che girano oggi (cfg 19 e 20), ed
    /// è deliberatamente piccola: un universo grande moltiplica i tentativi dentro il run, cioè
    /// alza SR\* — l'unico posto dove la molteplicità è davvero contata.
    /// </summary>
    public const int SeriePerProposta = 10;

    /// <summary>Quante alternative mettere sul menù. Poche: il comitato sceglie, non esplora.</summary>
    public const int ProposteMax = 4;

    public async Task<EsitoProposta> ProponiAsync(
        IReadOnlyCollection<int> inRotazione, double? oreResidueAlMese, CancellationToken ct = default)
    {
        var cop = await coverage.ReadAsync(inRotazione, ct);
        if (cop.Scoperte.Count == 0)
        {
            return new EsitoProposta([], null, "copertura",
                "Nessuna serie seguita è fuori dalla caccia: non c'è un buco da riempire.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var modelli = await db.PipelineConfigurations.AsNoTracking()
            .Where(c => inRotazione.Contains(c.Id))
            .Select(c => new { c.Id, c.UniverseJson, c.Name, c.MinHoursBetweenRuns })
            .ToListAsync(ct);

        var durate = await DurateMedianeAsync(db, ct);

        var menu = new List<CacciaProposta>();
        foreach (var (tf, simboli) in cop.BuchiPerTimeframe.Take(ProposteMax))
        {
            // Il modello: una configurazione dello STESSO timeframe che gira davvero. Copiarne la
            // forma è ciò che rende la proposta eseguibile senza inventare finestre — e se non
            // esiste un modello a quel timeframe, la proposta non si fa: proporre una caccia la cui
            // forma nessuno ha mai provato a quel ritmo sarebbe indovinare.
            var modello = modelli.FirstOrDefault(m =>
                HuntCoverageReader.Leggi(m.UniverseJson).Any(c => string.Equals(c.Timeframe, tf, StringComparison.OrdinalIgnoreCase)));
            if (modello is null)
            {
                logger.LogDebug("Buco a {Tf} su {N} serie, ma nessuna configurazione in rotazione lavora a quel timeframe: niente proposta.", tf, simboli.Count);
                continue;
            }

            var serieModello = Math.Max(1, HuntCoverageReader.Leggi(modello.UniverseJson).Count);
            var minutiModello = durate.TryGetValue(modello.Id, out var d) ? d : 0;
            if (minutiModello <= 0)
            {
                logger.LogDebug("Modello {Id} senza durata misurata: la proposta a {Tf} avrebbe un prezzo inventato.", modello.Id, tf);
                continue;
            }

            var scelte = simboli.Take(SeriePerProposta).ToList();
            // Il costo scala col numero di serie: è l'approssimazione più onesta che si può fare
            // senza aver mai girato quella caccia, e viene dichiarata come stima.
            var minuti = minutiModello * scelte.Count / (double)serieModello;

            // [Revisione 2026-09-03] Senza tetto la cadenza è quella del modello (o 24h): non c'è
            // un budget in cui «entrare», e proporre la cadenza più fitta sarebbe proporre la caccia
            // più costosa possibile presentandola come «entra nel residuo».
            var cadenza = oreResidueAlMese is double residuo
                ? CadenzaCheEntra(minuti, residuo)
                : (modello.MinHoursBetweenRuns > 0 ? modello.MinHoursBetweenRuns : 24);
            if (cadenza <= 0)
            {
                logger.LogDebug("Buco a {Tf}: non entra nelle {Ore:F1} ore residue nemmeno a cadenza massima.", tf, oreResidueAlMese);
                continue;
            }

            menu.Add(new CacciaProposta(tf, scelte, modello.Id, minuti, cadenza));
        }

        var budgetTesto = oreResidueAlMese is double r
            ? $"Budget residuo: {r:F1} ore/mese."
            : "Nessun tetto impostato (Campaign:MonthlyHourBudget = 0): la cadenza proposta è quella del modello, non una cadenza che entra in un budget.";

        if (menu.Count == 0)
        {
            return new EsitoProposta([], null, "budget",
                $"Ci sono {cop.Scoperte.Count} celle scoperte, ma nessuna proposta si può fare: "
                + (oreResidueAlMese is double r0
                    ? $"nessuna entra nelle {r0:F1} ore residue, o manca una configurazione modello a quel timeframe."
                    : "manca una configurazione modello (con durata misurata) a quel timeframe."));
        }

        // Ordine deterministico: prima il buco più grande. È anche il default se il comitato tace.
        var predefinita = menu[0];
        if (committee is null)
        {
            return new EsitoProposta(menu, predefinita, "rules",
                $"Comitato non disponibile: si propone il buco più grande — {predefinita.Etichetta}.");
        }

        var domanda = new CommitteeQuestion(
            "hunt-proposal",
            $"La piattaforma tiene aggiornate {cop.Seguite.Count} serie e ne caccia {cop.Cacciate.Count}: "
            + $"{cop.Scoperte.Count} non le guarda nessuno. {budgetTesto}\n"
            + "Ogni proposta occupa serie MAI cacciate e copia forma e finestre da una configurazione che gira già.\n"
            + "Criterio: preferire il buco che porta più informazione nuova per ora spesa, senza sbilanciare "
            + "il budget su un solo timeframe.",
            [.. menu.Select(p => new CommitteeOption(p.Id, p.Etichetta))],
            predefinita.Id);

        try
        {
            var verdetto = await committee.AskAsync(domanda, ct);
            var scelta = menu.FirstOrDefault(p => p.Id == verdetto.ChosenOptionId) ?? predefinita;
            var motivo = verdetto.Votes.FirstOrDefault(v => v.Valid && v.OptionId == verdetto.ChosenOptionId)?.Reason;
            return new EsitoProposta(menu, scelta,
                verdetto.ByQuorum ? "committee" : "default:quorum-mancato",
                verdetto.ByQuorum
                    ? $"Scelta dal comitato fra {menu.Count} buchi: {motivo}"
                    : $"Il comitato non ha formato una maggioranza: si propone il buco più grande — {scelta.Etichetta}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Il comitato che non risponde non deve impedire la proposta: il menù è già calcolato e
            // il default deterministico è una scelta valida quanto le altre.
            logger.LogWarning(ex, "Comitato non interrogabile per la proposta di caccia: decide la regola.");
            return new EsitoProposta(menu, predefinita, "default:non-interrogato",
                $"Comitato non interrogabile ({ex.GetType().Name}): si propone il buco più grande.");
        }
    }

    /// <summary>
    /// La cadenza più FITTA che sta nelle ore residue, fra quelle ammesse. Zero = non entra nemmeno
    /// al ritmo più lento: la proposta non si fa, invece di farla e sforare.
    /// </summary>
    internal static int CadenzaCheEntra(double minutiPerRun, double oreResidueAlMese)
    {
        if (minutiPerRun <= 0 || oreResidueAlMese <= 0) return 0;
        for (var ore = HuntBudget.MinCadenzaProponibile; ore <= HuntBudget.MaxCadenzaOre; ore *= 2)
        {
            var oreMese = minutiPerRun / 60.0 * (30.0 * 24.0 / ore);
            if (oreMese <= oreResidueAlMese) return ore;
        }
        // [Revisione 2026-09-03] Il raddoppio salta da 192 a 384 e non prova mai il massimo (336):
        // una caccia che entra SOLO a due settimane veniva scartata «nemmeno a cadenza massima»,
        // mentre Riallinea quella cadenza la propone. Stesso massimo per i due pezzi di K59/K60.
        var oreMeseMax = minutiPerRun / 60.0 * (30.0 * 24.0 / HuntBudget.MaxCadenzaOre);
        return oreMeseMax <= oreResidueAlMese ? HuntBudget.MaxCadenzaOre : 0;
    }

    private static async Task<Dictionary<int, double>> DurateMedianeAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var righe = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.CompletedAt != null && r.Status == "Completed")
            .Select(r => new { r.ConfigurationId, r.StartedAt, r.CompletedAt })
            .ToListAsync(ct);

        return righe
            .GroupBy(r => r.ConfigurationId)
            .ToDictionary(g => g.Key, g =>
            {
                var m = g.Select(r => (r.CompletedAt!.Value - r.StartedAt).TotalMinutes)
                    .Where(x => x >= 0).OrderBy(x => x).ToList();
                if (m.Count == 0) return 0d;
                var i = m.Count / 2;
                return m.Count % 2 == 1 ? m[i] : (m[i - 1] + m[i]) / 2;
            });
    }
}
