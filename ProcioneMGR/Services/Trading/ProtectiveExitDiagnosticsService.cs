using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// [B3] Le tre cose che il 2026-07-28 sono state costruite e lasciate senza nessuno che le
/// guardasse: i confronti d'ombra fra tick e candela, le posizioni rimaste su corsie che non
/// esistono più, e la misura del ritardo delle uscite protettive.
///
/// Il difetto era lo stesso di C4 prima del suo consumo: codice corretto, testato, e **mai chiamato
/// da niente** — verde a livello di classe, inesistente a livello di prodotto. La sentinella
/// scriveva su una tabella che nessuna query leggeva; l'allarme sulle posizioni orfane viveva solo
/// nei log del pod, dove lo vede chi va a cercarlo sapendo già che esiste; l'analizzatore del
/// ritardo era raggiungibile solo da riga di comando.
///
/// Qui vivono le letture; il pannello in <c>Trading.razor</c> le mostra. Sola lettura su Postgres,
/// nessuna chiamata al motore: si può interrogare anche a core remoto irraggiungibile — che è
/// proprio il momento in cui uno vuole sapere cosa è successo.
/// </summary>
public sealed class ProtectiveExitDiagnosticsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ProtectiveExitLagAnalyzer lagAnalyzer,
    ILogger<ProtectiveExitDiagnosticsService> logger)
{
    /// <summary>
    /// Ultimi confronti d'ombra della corsia. Ordinati per uscita più recente: la domanda tipica è
    /// «cosa è successo l'ultima volta», non «cosa è successo in media» — la media la dà il replay,
    /// su migliaia di posizioni invece che su queste poche.
    /// </summary>
    public async Task<IReadOnlyList<ProtectiveExitShadow>> RecentShadowsAsync(
        int laneId, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ProtectiveExitShadows.AsNoTracking()
            .Where(s => s.LaneId == laneId)
            .OrderByDescending(s => s.ActualExitAtUtc)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Posizioni su corsie oltre <see cref="TradingLanes.Count"/>: nessun motore ne valuta stop,
    /// target o trailing, e nessuno le chiuderà mai da solo. Il watchdog le urla nei log una volta
    /// per corsia; qui si vedono senza doverli leggere.
    ///
    /// La query NON filtra per corsia visualizzata: una posizione orfana è un problema della
    /// piattaforma, non della corsia che si sta guardando, e mostrarla solo a chi per caso ha
    /// selezionato la corsia giusta significherebbe non mostrarla.
    /// </summary>
    public async Task<IReadOnlyList<OpenPosition>> OrphanPositionsAsync(CancellationToken ct = default)
    {
        var laneCount = TradingLanes.Count;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.OpenPositions.AsNoTracking()
            .Where(p => p.LaneId >= laneCount)
            .OrderBy(p => p.LaneId).ThenBy(p => p.OpenedAtUtc)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Misura del ritardo su richiesta, per la corsia indicata: le candele fini fanno da surrogato
    /// dei tick contro le barre di corsia, coi bracket VERI letti dalla configurazione dell'ensemble.
    /// È lo stesso <see cref="ProtectiveExitLagAnalyzer"/> della fase CLI e del CronJob mensile —
    /// una sola implementazione, tre modi di invocarla.
    ///
    /// Restituisce null (con motivo) invece di lanciare: una diagnostica che fa esplodere la pagina
    /// da cui si guarda il motore è peggio di una diagnostica assente.
    /// </summary>
    public async Task<(ProtectiveExitLagReport? Report, string? Reason)> MeasureLagAsync(
        int laneId, int sampleEveryNBars = 4, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var ensemble = await db.EnsembleStates.AsNoTracking()
                .FirstOrDefaultAsync(e => e.LaneId == laneId, ct);
            if (ensemble is null) return (null, $"La corsia {laneId} non ha una configurazione salvata.");

            using var doc = System.Text.Json.JsonDocument.Parse(ensemble.ConfigurationJson);
            var root = doc.RootElement;
            var symbol = root.GetProperty("symbol").GetString();
            var laneTf = root.GetProperty("timeframe").GetString();
            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(laneTf))
            {
                return (null, "Configurazione della corsia incompleta (simbolo o timeframe mancante).");
            }

            var strat = root.GetProperty("strategies").EnumerateArray().FirstOrDefault();
            decimal Pct(string name) =>
                strat.ValueKind == System.Text.Json.JsonValueKind.Object
                && strat.TryGetProperty(name, out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDecimal() : 0m;

            var sl = Pct("stopLossPercent");
            if (sl <= 0m) return (null, "Nessuno stop configurato: non c'è uscita protettiva di cui misurare il ritardo.");

            var laneStep = ProtectiveExitLagAnalyzer.Step(laneTf);

            // La risoluzione fine più fine DISPONIBILE per questa serie. Si sceglie dai dati, non da
            // una costante: XLM non ha 5m, e pretenderli darebbe "non misurabile" su una serie che
            // invece si può misurare a 15m.
            List<Data.OhlcvData>? fine = null;
            string? fineTf = null;
            foreach (var cand in new[] { "1m", "5m", "15m", "30m" })
            {
                if (ProtectiveExitLagAnalyzer.Step(cand) >= laneStep) break;
                var bars = await db.OhlcvData.AsNoTracking()
                    .Where(c => c.Symbol == symbol && c.Timeframe == cand)
                    .OrderBy(c => c.TimestampUtc).ToListAsync(ct);
                if (bars.Count == 0) continue;
                fine = bars; fineTf = cand; break;
            }

            if (fine is null || fineTf is null)
            {
                return (null, $"Nessuna risoluzione più fine di {laneTf} disponibile per {symbol}: non misurabile.");
            }

            var from = fine[0].TimestampUtc;
            var lane = await db.OhlcvData.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == laneTf && c.TimestampUtc >= from)
                .OrderBy(c => c.TimestampUtc).ToListAsync(ct);

            if (lane.Count < 200) return (null, $"Troppe poche barre di corsia ({lane.Count}) per una misura sensata.");

            var report = lagAnalyzer.Measure(lane, fine, new ProtectiveExitLagRequest
            {
                Symbol = symbol,
                LaneTimeframe = laneTf,
                FineTimeframe = fineTf,
                StopLossPercent = sl,
                TakeProfitPercent = Pct("takeProfitPercent") is var tp && tp > 0m ? tp : null,
                TrailingStopPercent = Pct("trailingStopPercent") is var tr && tr > 0m ? tr : null,
                MaxHoldBars = 96,
                SampleEveryNBars = Math.Clamp(sampleEveryNBars, 1, 64),
            });

            return (report, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Misura del ritardo fallita per la corsia {Lane}.", laneId);
            return (null, $"Misura fallita: {ex.Message}");
        }
    }
}
