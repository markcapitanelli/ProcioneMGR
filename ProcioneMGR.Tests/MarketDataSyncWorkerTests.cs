using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>
/// La guardia di ciclo del worker di sync. DUE incidenti la motivano:
/// (2026-08-13) una richiesta klines rimasta appesa senza risposta (thread starvation nel pod) ha
/// parcheggiato il worker per 30 minuti — il budget trasforma quel per-sempre in "al massimo un
/// ciclo"; (2026-08-14) una TaskCanceledException di TIMEOUT DI RETE (Token=None) è stata letta
/// dal catch del loop come shutdown — worker morto alle 22:44, pod «healthy», 122 serie ferme per
/// 6 ore. Da qui le due regole: un'OCE che non viene dal token di shutdown è un ERRORE DI CICLO, e
/// il backstop WaitAsync abbandona anche una catena che ignora il token.
/// </summary>
public sealed class MarketDataSyncWorkerTests
{
    /// <summary>Il caso dell'incidente 2026-08-13: un sync che non risponde mai (ma rispetta il token).</summary>
    private sealed class HangingSyncService : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Il caso dell'incidente 2026-08-14: la TERZA sorgente di OperationCanceledException — un
    /// timeout di rete che il rate-limit handler sintetizza come TaskCanceledException con
    /// Token=None. Non è né il budget né lo shutdown: prima del fix uccideva il worker.
    /// </summary>
    private sealed class NetworkTimeoutSyncService : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default) =>
            Task.FromException(new TaskCanceledException("timeout per-tentativo simulato (Token=None)"));
    }

    /// <summary>
    /// Una catena che IGNORA il token, DI PROPOSITO: il caso che il budget cooperativo non può
    /// tagliare (CancelAfter cancella un token che nessuno osserva). Prima del backstop questo
    /// test non esisteva — il finto «appeso» dei test rispettava il token, e il buco era invisibile.
    /// </summary>
    private sealed class TokenIgnoringSyncService : IMarketDataSyncService
    {
        public Task<int> SyncSeriesAsync(int trackedSeriesId, CancellationToken ct = default) => Task.FromResult(0);
        public Task SyncAllEnabledAsync(CancellationToken ct = default) => Task.Delay(Timeout.InfiniteTimeSpan);
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
    public async Task RunCycle_TimeoutDiReteTravestitoDaCancellazione_CostaUnCicloNonIlWorker()
    {
        // L'incidente 2026-08-14: questa OCE usciva da RunCycleAsync (il filtro del budget non la
        // copriva) e il loop la leggeva come shutdown. Ora è un ciclo fallito: false, non throw.
        var worker = Build(new NetworkTimeoutSyncService());

        var completed = await worker.RunCycleAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.False(completed);
    }

    [Fact]
    public async Task RunCycle_CatenaCheIgnoraIlToken_AbbandonataDalBackstop()
    {
        // CancelAfter non interrompe un await che non osserva il token: senza il backstop questo
        // ciclo non tornerebbe MAI (worker parcheggiato con budget «attivo» — il punto cieco che i
        // vecchi test non coprivano perché il finto appeso onorava il token).
        var worker = Build(new TokenIgnoringSyncService());
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var completed = await worker.RunCycleAsync(
            TimeSpan.FromMilliseconds(100), CancellationToken.None, abandonAfter: TimeSpan.FromMilliseconds(400));

        sw.Stop();
        Assert.False(completed);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"il ciclo che ignora il token doveva essere abbandonato dal backstop, non dopo {sw.Elapsed}");
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
