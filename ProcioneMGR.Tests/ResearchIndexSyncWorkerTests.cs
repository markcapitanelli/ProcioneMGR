using Microsoft.Extensions.Logging;
using ProcioneMGR.Services.Research;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K10, PRD autonomia-piena 2026-08-31] Prove del braccio che indicizza l'archivio della ricerca.
///
/// <para>Il difetto che chiude: <c>IResearchCandidateIndexer</c> era iniettato in un solo posto —
/// la page service di <c>/research</c> — quindi l'archivio cresceva <b>solo quando un umano apriva
/// quella pagina</b>. Al 2026-08-30 l'ultimo run indicizzato era del <b>25/08 13:15</b> con
/// <b>34 run completati</b> dietro. La ricerca girava 4-8 volte al giorno e depositava nel vuoto.</para>
/// </summary>
public class ResearchIndexSyncWorkerTests
{
    private sealed class IndicizzatoreFinto(ResearchIndexResult esito, Exception? esplode = null)
        : IResearchCandidateIndexer
    {
        public int Chiamate { get; private set; }

        public Task<ResearchIndexResult> IndexNewRunsAsync(CancellationToken ct = default)
        {
            Chiamate++;
            return esplode is not null ? Task.FromException<ResearchIndexResult>(esplode) : Task.FromResult(esito);
        }

        public Task<ResearchIndexResult> RebuildAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("il worker non deve MAI ricostruire: e' un'azione umana, costosa e distruttiva");
    }

    private sealed class LoggerSpia : ILogger<ResearchIndexSyncWorker>
    {
        public List<(LogLevel Level, string Message)> Righe { get; } = [];
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) =>
            Righe.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Tick_IndicizzaEDichiaraQuantiRunHaPreso()
    {
        var indexer = new IndicizzatoreFinto(new ResearchIndexResult(RunsIndexed: 34, CandidatesIndexed: 1200, RunsSkipped: 0));
        var log = new LoggerSpia();

        await new ResearchIndexSyncWorker(indexer, log).TickOnceAsync(CancellationToken.None);

        Assert.Equal(1, indexer.Chiamate);
        var riga = Assert.Single(log.Righe);
        Assert.Equal(LogLevel.Information, riga.Level);
        Assert.Contains("34", riga.Message);
        Assert.Contains("1200", riga.Message);
    }

    [Fact]
    public async Task Tick_ADindiceAllineato_TACE()
    {
        // A regime il giro costa una query e non deve dire niente: un worker che scrive una riga
        // ogni mezz'ora per dire «nulla da fare» insegna a non leggere il log.
        var indexer = new IndicizzatoreFinto(new ResearchIndexResult(0, 0, 0));
        var log = new LoggerSpia();

        await new ResearchIndexSyncWorker(indexer, log).TickOnceAsync(CancellationToken.None);

        Assert.Empty(log.Righe);
    }

    [Fact]
    public async Task Tick_ZeroIndicizzatiMaQualcunoSaltato_GRIDA()
    {
        // Il caso insidioso: l'indice NON è allineato agli artefatti, ma il conteggio dei run
        // indicizzati è zero esattamente come quando è tutto a posto. Senza questa riga l'unica
        // traccia del guasto sarebbe il silenzio — fail-open sulla diagnostica, ma DICHIARATO.
        var indexer = new IndicizzatoreFinto(new ResearchIndexResult(RunsIndexed: 0, CandidatesIndexed: 0, RunsSkipped: 3));
        var log = new LoggerSpia();

        await new ResearchIndexSyncWorker(indexer, log).TickOnceAsync(CancellationToken.None);

        var riga = Assert.Single(log.Righe);
        Assert.Equal(LogLevel.Warning, riga.Level);
        Assert.Contains("3", riga.Message);
    }

    [Fact]
    public async Task Tick_UnGuastoDelDatabaseNonUccideIlWorker()
    {
        // La classe del worker di sync morto su una OCE (2026-08-15): 122 serie ferme per sei ore
        // con il pod «healthy», perché un errore transitorio aveva chiuso il ciclo per sempre.
        var indexer = new IndicizzatoreFinto(new ResearchIndexResult(0, 0, 0),
                                             esplode: new InvalidOperationException("connessione persa"));
        var log = new LoggerSpia();

        var ex = await Record.ExceptionAsync(() =>
            new ResearchIndexSyncWorker(indexer, log).TickOnceAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.Single(log.Righe);
        Assert.Equal(LogLevel.Warning, log.Righe[0].Level);
    }

    [Fact]
    public void Cadenza_ArretratoMassimoUnRun()
    {
        // Le cacce sono 4-8 al giorno, cioè una ogni 3-6 ore: mezz'ora tiene l'arretrato a un run.
        Assert.True(ResearchIndexSyncWorker.Interval <= TimeSpan.FromHours(1));
        Assert.True(ResearchIndexSyncWorker.InitialDelay > TimeSpan.Zero,
            "senza attesa iniziale il primo giro cade prima che il DB sia migrato");
    }

    [Fact]
    public void Registrato_ComeHostedService()
    {
        // «Il verde a livello di classe non è integrazione» (STANDARD-VERIFICA, regola 1): questo
        // worker esiste PRECISAMENTE perché il suo indicizzatore era registrato e mai azionato.
        // Un test che lo provasse in isolamento e lo lasciasse fuori dal Program ripeterebbe il
        // difetto con una faccia nuova.
        var program = File.ReadAllText(Path.Combine(Procione.Platform.RepoRoot, "ProcioneMGR", "Program.cs"));

        Assert.Contains("AddHostedService<ProcioneMGR.Services.Research.ResearchIndexSyncWorker>", program);
        // E il gemello che ha fatto da modello non dev'essere sparito nel frattempo.
        Assert.Contains("AddHostedService<ProcioneMGR.Services.PairsTrading.PairIndexSyncWorker>", program);
    }
}
