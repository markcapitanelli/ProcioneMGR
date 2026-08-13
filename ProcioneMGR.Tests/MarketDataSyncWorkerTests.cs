using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>
/// La guardia di ciclo del worker di sync. L'incidente che la motiva (2026-08-13): una richiesta
/// klines rimasta appesa senza risposta (thread starvation nel pod) ha parcheggiato il worker per
/// 30 minuti — il PeriodicTimer riarma solo a corpo completato, quindi una chiamata appesa senza
/// tetto significa un pod zombie e serie ferme in silenzio. Il budget trasforma quel per-sempre
/// in "al massimo un ciclo".
/// </summary>
public sealed class MarketDataSyncWorkerTests
{
    /// <summary>Il caso dell'incidente: un sync che non risponde mai (ma rispetta il token).</summary>
    private sealed class HangingSyncService : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private sealed class CompletingSyncService : IMarketDataSyncService
    {
        public int Cycles;
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default)
        {
            Cycles++;
            return Task.CompletedTask;
        }
    }

    private static MarketDataSyncWorker Build(IMarketDataSyncService sync)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sync); // lo scope del worker risolve il singleton del test
        var provider = services.BuildServiceProvider();

        return new MarketDataSyncWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<MarketDataSyncWorker>.Instance);
    }

    [Fact]
    public async Task RunCycle_ChiamataAppesa_SiFermaAlBudgetInveceCheMai()
    {
        var worker = Build(new HangingSyncService());
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var completed = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        sw.Stop();
        Assert.False(completed); // budget scaduto: dichiarato, non inghiottito
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"il ciclo appeso doveva fermarsi al budget, non dopo {sw.Elapsed}");
    }

    [Fact]
    public async Task RunCycle_CicloSano_CompletaEDichiaraTrue()
    {
        var sync = new CompletingSyncService();
        var worker = Build(sync);

        var completed = await worker.RunCycleAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(completed);
        Assert.Equal(1, sync.Cycles);
    }

    [Fact]
    public async Task RunCycle_Shutdown_RipropagaInveceDiTravestirsiDaBudget()
    {
        // Lo spegnimento dell'host NON è un timeout di ciclo: deve uscire dal loop del worker
        // (OperationCanceledException), non essere assorbito come "ciclo interrotto, ritento".
        var worker = Build(new HangingSyncService());
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.RunCycleAsync(TimeSpan.FromMinutes(5), stopping.Token));
    }
}
