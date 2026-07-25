using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Riassunto di una corsia: quel tanto che basta per sceglierla senza doverla aprire.
/// </summary>
/// <param name="Symbol">Vuoto se la corsia non è mai stata configurata.</param>
public sealed record LaneSummary(int Id, string Symbol, string Timeframe, string Mode, bool IsRunning)
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
            if (configs.TryGetValue(lane, out var json) && !string.IsNullOrWhiteSpace(json))
            {
                // Una configurazione illeggibile non deve far sparire la corsia dal selettore: senza
                // scheda non ci si può nemmeno cliccare sopra per andare a sistemarla.
                try
                {
                    var cfg = JsonSerializer.Deserialize<EnsembleConfiguration>(json, Json);
                    symbol = cfg?.Symbol ?? string.Empty;
                    timeframe = cfg?.Timeframe ?? string.Empty;
                }
                catch (JsonException) { /* corsia mostrata come non configurata */ }
            }

            var state = stateByLane.GetValueOrDefault(lane);
            result.Add(new LaneSummary(
                lane, symbol, timeframe,
                state?.Mode.ToString() ?? TradingMode.Paper.ToString(),
                state?.IsRunning ?? false));
        }
        return result;
    }
}
