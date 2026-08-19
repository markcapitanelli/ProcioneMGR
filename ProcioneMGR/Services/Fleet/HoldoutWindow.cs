using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [I11] «Quanto era largo l'holdout di questo run?», per i due percorsi di schieramento che partono
/// da un <b>candidato indicizzato</b> e non da una raccomandazione: il click della fascia grigia in
/// <c>/fleet</c> (<see cref="GreyDeployer"/>) e l'aggiunta di una gamba grigia in <c>/ensemble</c>.
///
/// <para>Il terzo percorso — l'applicatore della pipeline — non passa di qui: la finestra viaggia
/// già sulla raccomandazione (<c>PipelineRecommendation.HoldoutMonths</c>), perché quella porta è
/// chiamata anche senza conoscere il run. Sono due strade diverse verso lo stesso numero, ma
/// entrambe finiscono nell'unico calcolo di <see cref="Pipeline.PipelineDateRanges.HoldoutMonths"/>:
/// il punto non è avere una sola strada, è non avere due aritmetiche.</para>
///
/// <para>Esiste come helper condiviso invece che ripetuto perché la query è identica nei due
/// chiamanti (run → configurazione → JSON delle date), e una copia sola è una copia che non
/// diverge.</para>
/// </summary>
internal static class HoldoutWindow
{
    /// <summary>
    /// Mesi di holdout del run, o <c>null</c> se il run non esiste, la configurazione è sparita, il
    /// JSON è illeggibile o la finestra è più corta di una settimana. Non lancia mai: chi la chiama
    /// sta schierando, e un numero descrittivo mancante non deve far fallire lo schieramento.
    /// </summary>
    public static async Task<decimal?> MonthsForRunAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, Guid runId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var json = await db.PipelineRuns.AsNoTracking()
                .Where(r => r.Id == runId)
                .Join(db.PipelineConfigurations.AsNoTracking(), r => r.ConfigurationId, c => c.Id, (_, c) => c.DateRangesJson)
                .FirstOrDefaultAsync(ct);
            return FleetStateReader.HoldoutMonths(json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// La coppia (frequenza attesa, provenienza in chiaro) da scrivere sulla gamba. La provenienza
    /// non è decorazione: senza, un numero DERIVATO dall'holdout si legge come una misura del
    /// forward test, ed è l'errore di lettura che rende inutile qualunque confronto successivo.
    /// </summary>
    public static async Task<(decimal? PerMonth, string? Source)> ForCandidateAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory, Guid runId, int holdoutTrades, CancellationToken ct = default)
    {
        var months = await MonthsForRunAsync(dbFactory, runId, ct);
        if (months is not decimal m) return (null, null);

        var perMonth = TradeFrequency.PerMonth(holdoutTrades, m);
        return perMonth is null
            ? (null, null)
            : (perMonth, $"holdout del run {runId.ToString()[..8]}: {holdoutTrades} trade su {m:0.##} mesi");
    }
}
