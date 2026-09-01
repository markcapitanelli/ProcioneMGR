using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Riassunto di una corsia: quel tanto che basta per sceglierla senza doverla aprire.
/// </summary>
/// <param name="Symbol">Vuoto se la corsia non è mai stata configurata.</param>
public sealed record LaneSummary(
    int Id, string Symbol, string Timeframe, string Mode, bool IsRunning,
    // [I12] Ritmo ATTESO della corsia sul simbolo attuale: la somma delle gambe attive, o null se
    // anche una sola non lo dichiara. Vedi LaneDirectory.ExpectedTradesPerMonth per il perche' la
    // somma sia parziale-o-niente e non parziale-e-basta.
    decimal? ExpectedTradesPerMonth = null,
    // [I12-rev] Gli StrategyId delle gambe ATTIVE IN CONFIGURAZIONE. Servono a confrontarli con
    // quelle che il motore sta davvero eseguendo: se le due fotografie divergono, il ritmo atteso
    // qui sopra non descrive cio' che sta operando. Vedi FleetStateReader.
    IReadOnlyList<string>? ActiveStrategyIds = null,
    // [J14] La corsia esegue gambe GRIGIE? true = almeno una gamba attiva con SourceVerdict
    // "Grey"; false = tutte dichiarate "Survived"; null = provenienza ignota (gambe senza
    // etichetta, pre-T1). Serve al tetto MaxGreyLanes, dove l'ignoto conta come grigio.
    bool? HasGreyLegs = null,
    // [K33, 2026-09-01] L'IDENTITA' CANONICA delle gambe attive (PipelineCandidateKey: strategia +
    // coppia + timeframe + impronta dei parametri). Serve alla guardia che impedisce alla stessa
    // ipotesi di occupare due corsie.
    //
    // Vive qui e non in una seconda lettura perche' questa classe deserializza gia' la
    // configurazione di tutte le corsie in una query: una seconda deserializzazione altrove sarebbe
    // una seconda definizione di «gamba attiva», che e' il difetto gia' pagato con SeriesFreshness.
    //
    // Lista vuota = configurazione assente o illeggibile. NON e' «nessun duplicato»: e' «non lo so»,
    // e chi consuma deve trattarla come tale.
    IReadOnlyList<string>? ActiveCandidateKeys = null)
{
    public bool IsConfigured => !string.IsNullOrEmpty(Symbol);
}

/// <summary>Elenca le corsie con il loro stato corrente.</summary>
public interface ILaneDirectory
{
    Task<IReadOnlyList<LaneSummary>> ListAsync(CancellationToken ct = default);
}

/// <summary>
/// Legge in un colpo solo ciò che serve al selettore di corsia: simbolo e timeframe dalla
/// configurazione dell'ensemble, modalità e stato di esecuzione dal motore.
///
/// Vive come servizio e non dentro le pagine perché lo usano <c>Trading</c> ed <c>Ensemble</c> allo
/// stesso modo, e perché con corsie configurabili la domanda "quali corsie ci sono e cosa ci gira"
/// smette di avere una risposta ovvia: prima erano tre e si conoscevano a memoria.
///
/// Due letture per tutte le corsie, non due per corsia: con dodici corsie la differenza fra una
/// query e ventiquattro si vede, e questo elenco si ridisegna a ogni refresh della pagina.
/// </summary>
public sealed class LaneDirectory(IDbContextFactory<ApplicationDbContext> dbFactory) : ILaneDirectory
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<IReadOnlyList<LaneSummary>> ListAsync(CancellationToken ct = default)
    {
        var laneCount = TradingLanes.Count;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Una corsia può avere più righe di stato ensemble: vale la prima, come ovunque nel motore.
        var configs = (await db.EnsembleStates.AsNoTracking()
                .Where(e => e.LaneId < laneCount)
                .Select(e => new { e.LaneId, e.Id, e.ConfigurationJson })
                .ToListAsync(ct))
            .GroupBy(e => e.LaneId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id).First().ConfigurationJson);

        var states = await db.TradingEngineStates.AsNoTracking()
            .Where(s => s.LaneId < laneCount)
            .Select(s => new { s.LaneId, s.Mode, s.IsRunning })
            .ToListAsync(ct);
        var stateByLane = states.GroupBy(s => s.LaneId).ToDictionary(g => g.Key, g => g.First());

        var result = new List<LaneSummary>(laneCount);
        for (var lane = 0; lane < laneCount; lane++)
        {
            var symbol = string.Empty;
            var timeframe = string.Empty;
            decimal? expected = null;
            IReadOnlyList<string>? activeIds = null;
            bool? greyLegs = null;
            IReadOnlyList<string>? candidateKeys = null;
            if (configs.TryGetValue(lane, out var json) && !string.IsNullOrWhiteSpace(json))
            {
                // Una configurazione illeggibile non deve far sparire la corsia dal selettore: senza
                // scheda non ci si può nemmeno cliccare sopra per andare a sistemarla.
                try
                {
                    var cfg = JsonSerializer.Deserialize<EnsembleConfiguration>(json, Json);
                    symbol = cfg?.Symbol ?? string.Empty;
                    timeframe = cfg?.Timeframe ?? string.Empty;
                    expected = ExpectedTradesPerMonth(cfg);
                    activeIds = cfg?.Strategies.Where(x => x.IsActive).Select(x => x.StrategyId).ToList();
                    greyLegs = HasGreyLegs(cfg);
                    candidateKeys = ActiveCandidateKeys(cfg);
                }
                catch (JsonException) { /* corsia mostrata come non configurata */ }
            }

            var state = stateByLane.GetValueOrDefault(lane);
            result.Add(new LaneSummary(
                lane, symbol, timeframe,
                state?.Mode.ToString() ?? TradingMode.Paper.ToString(),
                state?.IsRunning ?? false,
                expected, activeIds, greyLegs, candidateKeys));
        }
        return result;
    }

    /// <summary>
    /// [I12] Il ritmo atteso della CORSIA: la somma di quello delle gambe attive, perché le gambe
    /// operano in parallelo sullo stesso simbolo e i loro trade si sommano.
    ///
    /// <para><b>Parziale-o-niente, mai parziale-e-basta.</b> Se anche una sola gamba attiva non
    /// dichiara la propria frequenza, il risultato è <c>null</c>. Sommare solo quelle note darebbe un
    /// atteso SOTTOSTIMATO e quindi un confronto che assolve troppo — ma soprattutto, nel verso che
    /// conta, produrrebbe un atteso positivo su una corsia di cui in realtà non si sa nulla, cioè
    /// esattamente il caso in cui il ritiro per inedia non deve poter mordere. L'ignoranza non
    /// condanna, e una conoscenza parziale è ignoranza travestita.</para>
    ///
    /// <para>Le gambe DISATTIVATE non contano: non devono produrre trade, e includerle gonfierebbe
    /// l'atteso facendo sembrare affamata una corsia che sta rispettando la sua configurazione.</para>
    /// </summary>
    internal static decimal? ExpectedTradesPerMonth(EnsembleConfiguration? cfg)
    {
        var active = cfg?.Strategies.Where(s => s.IsActive).ToList();
        if (active is null or { Count: 0 }) return null;
        if (active.Any(s => s.ExpectedTradesPerMonth is null)) return null;
        return active.Sum(s => s.ExpectedTradesPerMonth!.Value);
    }

    /// <summary>
    /// [J14] La provenienza delle gambe attive: true = almeno una grigia; false = TUTTE dichiarate
    /// «Survived»; null = almeno una senza etichetta (pre-T1) e nessuna grigia — cioè non si sa, e
    /// il consumatore del tetto tratta l'ignoto come grigio (fail-closed). Confronto Ordinal:
    /// l'etichetta la scrive il nostro codice.
    /// </summary>
    internal static bool? HasGreyLegs(EnsembleConfiguration? cfg)
    {
        var active = cfg?.Strategies.Where(s => s.IsActive).ToList();
        if (active is null or { Count: 0 }) return null;
        if (active.Any(s => string.Equals(s.SourceVerdict, "Grey", StringComparison.Ordinal))) return true;
        return active.All(s => string.Equals(s.SourceVerdict, "Survived", StringComparison.Ordinal)) ? false : null;
    }

    /// <summary>
    /// [K33, 2026-09-01] L'identità canonica di ogni gamba ATTIVA, con la stessa funzione che la
    /// pipeline usa per identificare un candidato (<see cref="PipelineCandidateKey.Build"/>): la
    /// guardia contro l'ipotesi doppia deve confrontare le corsie con la stessa chiave con cui la
    /// ricerca le produce, altrimenti confronterebbe due cose diverse chiamandole con lo stesso nome.
    ///
    /// <para><b>Perché lo <c>StrategyId</c> non serve.</b> <c>EnsembleModels</c> conia un
    /// <c>Guid.NewGuid()</c> a ogni costruzione della gamba: due schieramenti della STESSA ipotesi
    /// hanno due <c>StrategyId</c> diversi. È esattamente così che, il 2026-08-31, GridMeanReversion
    /// DOGE/USDT 15m con parametri identici e <c>ExpectedSharpe</c> uguale a ventotto cifre è finita
    /// su due corsie e due dotazioni da 10.000 senza che nulla se ne accorgesse.</para>
    ///
    /// <para>Le gambe disattivate non contano: non operano, quindi non sono un'ipotesi in corso.</para>
    /// </summary>
    internal static IReadOnlyList<string>? ActiveCandidateKeys(EnsembleConfiguration? cfg)
    {
        var active = cfg?.Strategies.Where(s => s.IsActive).ToList();
        if (active is null or { Count: 0 }) return null;
        return active
            .Select(s => PipelineCandidateKey.Build(
                s.StrategyName, cfg!.Symbol, cfg.Timeframe, s.Parameters))
            .ToList();
    }
}
