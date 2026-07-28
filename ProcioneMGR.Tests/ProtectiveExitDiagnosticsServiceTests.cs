using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [B3] La sentinella d'ombra scriveva su una tabella che nessuna query leggeva, l'allarme sulle
/// posizioni orfane viveva solo nei log del pod, e la misura del ritardo era raggiungibile solo da
/// riga di comando. Codice corretto, testato, e <b>mai chiamato da niente</b>: la stessa forma di
/// C4 prima del suo consumo — verde a livello di classe, inesistente a livello di prodotto.
///
/// Questi test coprono le letture che il pannello di <c>/trading</c> consuma. Il più importante è
/// <see cref="Le_orfane_non_si_filtrano_per_corsia_visualizzata"/>: una posizione orfana è un
/// problema della piattaforma, non della corsia che si sta guardando, e mostrarla solo a chi per
/// caso ha selezionato la corsia giusta significa non mostrarla.
/// </summary>
[Collection("Postgres")]
public sealed class ProtectiveExitDiagnosticsServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ProtectiveExitDiagnosticsServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(ProtectiveExitDiagnosticsService Svc, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var db = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var ctx = await db.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();

        return (new ProtectiveExitDiagnosticsService(
            db, new ProtectiveExitLagAnalyzer(), NullLogger<ProtectiveExitDiagnosticsService>.Instance), db);
    }

    private static ProtectiveExitShadow Shadow(int laneId, DateTime exitAt, double costBps) => new()
    {
        LaneId = laneId, Symbol = "BTC/USDT", Mode = TradingMode.Paper,
        PositionId = Guid.NewGuid().ToString("N"), Side = OrderSide.Buy, EntryPrice = 100m,
        DetectedAtUtc = exitAt.AddMinutes(-10), DetectedPrice = 95m, DetectedReason = "StopLoss",
        ShadowFillPrice = 95m, ActualExitAtUtc = exitAt, ActualFillPrice = 94m, ActualReason = "StopLoss",
        LeadSeconds = 600d, DelayCostBps = costBps, CreatedAtUtc = exitAt,
    };

    // ------------------------------------------------------------------ confronti d'ombra

    /// <summary>Solo la corsia chiesta, e prima i piu' recenti: la domanda e' "cosa e' successo l'ultima volta".</summary>
    [Fact]
    public async Task Gli_ombra_sono_per_corsia_e_dal_piu_recente()
    {
        var (svc, db) = await BuildAsync();
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.ProtectiveExitShadows.AddRange(
                Shadow(0, t0, 10d),
                Shadow(0, t0.AddHours(5), 20d),
                Shadow(1, t0.AddHours(9), 30d));
            await ctx.SaveChangesAsync();
        }

        var lane0 = await svc.RecentShadowsAsync(0);
        Assert.Equal(2, lane0.Count);
        Assert.All(lane0, s => Assert.Equal(0, s.LaneId));
        Assert.Equal(t0.AddHours(5), lane0[0].ActualExitAtUtc);   // il piu' recente per primo
    }

    // ------------------------------------------------------------------ posizioni orfane

    /// <summary>
    /// Le orfane NON si filtrano per corsia visualizzata. Se lo facessero, la corsia 3 del
    /// 2026-07-27 sarebbe stata invisibile a chiunque non avesse per caso selezionato una corsia che
    /// non esiste piu' — cioe' a chiunque, visto che il selettore mostra solo le corsie configurate.
    /// </summary>
    [Fact]
    public async Task Le_orfane_non_si_filtrano_per_corsia_visualizzata()
    {
        var (svc, db) = await BuildAsync();
        var oltre = TradingLanes.Count;

        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OpenPositions.AddRange(
                new OpenPosition { LaneId = 0, Symbol = "BTC/USDT", Quantity = 1m, EntryPrice = 100m, OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow },
                new OpenPosition { LaneId = oltre, Symbol = "DOT/USDT", Quantity = 980m, EntryPrice = 0.816m, OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow },
                new OpenPosition { LaneId = oltre + 1, Symbol = "ETH/USDT", Quantity = 2m, EntryPrice = 3000m, OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var orfane = await svc.OrphanPositionsAsync();

        Assert.Equal(2, orfane.Count);
        Assert.All(orfane, p => Assert.True(p.LaneId >= TradingLanes.Count));
        Assert.DoesNotContain(orfane, p => p.Symbol == "BTC/USDT");   // la corsia 0 esiste: non e' orfana
    }

    /// <summary>Piattaforma sana: nessuna orfana, e il pannello non deve inventarne.</summary>
    [Fact]
    public async Task Senza_orfane_la_lista_e_vuota()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OpenPositions.Add(new OpenPosition
            {
                LaneId = 0, Symbol = "BTC/USDT", Quantity = 1m, EntryPrice = 100m,
                OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        Assert.Empty(await svc.OrphanPositionsAsync());
    }

    // ------------------------------------------------------------------ chiusura delle orfane

    /// <summary>
    /// IL CONFINE DI SICUREZZA. Il comando deve RIFIUTARE su una corsia che esiste davvero: quella
    /// posizione ha un motore che la sorveglia, e chiuderla da qui sarebbe un secondo scrittore sulla
    /// stessa corsia — cioe' l'invariante numero uno del PRD, aggirata da un pulsante di diagnostica.
    /// Il controllo sta sul SERVER, non sulla UI che nasconde il bottone.
    /// </summary>
    [Fact]
    public async Task Non_chiude_una_posizione_di_una_corsia_che_esiste()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OpenPositions.Add(new OpenPosition
            {
                LaneId = 0, PositionId = "viva", Symbol = "BTC/USDT", Side = OrderSide.Buy,
                Quantity = 1m, EntryPrice = 100m, OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var (ok, message) = await svc.CloseOrphanAsync("viva", "admin");

        Assert.False(ok);
        Assert.Contains("ESISTE", message);

        await using var check = await db.CreateDbContextAsync();
        Assert.Single(await check.OpenPositions.AsNoTracking().ToListAsync());   // intatta
        Assert.Empty(await check.TradeRecords.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Percorso felice: la posizione orfana si chiude AL PREZZO ATTUALE, non al livello dello stop.
    /// Registrare il fill al livello sarebbe la stessa finzione misurata in B3 — il motore non
    /// c'era, quel prezzo non lo ha ottenuto nessuno.
    /// </summary>
    [Fact]
    public async Task Chiude_lorfana_al_prezzo_attuale_e_lascia_traccia()
    {
        var (svc, db) = await BuildAsync();
        var oltre = TradingLanes.Count;

        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OpenPositions.Add(new OpenPosition
            {
                LaneId = oltre, PositionId = "orfana", Symbol = "DOT/USDT", Side = OrderSide.Buy,
                Quantity = 1000m, EntryPrice = 0.816m, StopLoss = 0.7856m,
                OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
            ctx.TradingEngineStates.Add(new TradingEngineState
            {
                LaneId = oltre, Mode = TradingMode.Paper, Symbol = "DOT/USDT",
                TotalCapital = 10_000m, AvailableCapital = 9_000m, RealizedPnl = 0m,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            // Prezzo attuale 0,766: SOTTO lo stop, che e' proprio il caso reale della corsia 3.
            ctx.OhlcvData.Add(new OhlcvData
            {
                Symbol = "DOT/USDT", Timeframe = "5m", TimestampUtc = DateTime.UtcNow,
                Open = 0.766m, High = 0.766m, Low = 0.766m, Close = 0.766m, Volume = 1m,
            });
            await ctx.SaveChangesAsync();
        }

        var (ok, _) = await svc.CloseOrphanAsync("orfana", "admin");
        Assert.True(ok);

        await using var check = await db.CreateDbContextAsync();
        Assert.Empty(await check.OpenPositions.AsNoTracking().ToListAsync());

        var trade = Assert.Single(await check.TradeRecords.AsNoTracking().ToListAsync());
        Assert.Equal(0.766m, trade.ExitPrice);                 // prezzo attuale, NON lo stop 0,7856
        Assert.Equal(-50m, trade.Pnl);                          // (0,766 - 0,816) x 1000
        Assert.Equal("OrphanClosed", trade.ExitReason);

        // Traccia di chi ha deciso: una chiusura fuori dal motore non puo' essere anonima.
        var audit = Assert.Single(await check.TradingAuditLogs.AsNoTracking()
            .Where(a => a.Action == "OrphanPositionClosed").ToListAsync());
        Assert.Equal("admin", audit.UserId);

        // Contabilita' allineata: lasciare il PnL scollegato dai trade e' lo stato incoerente che il
        // watchdog degli invarianti esiste per intercettare.
        var state = await check.TradingEngineStates.AsNoTracking().FirstAsync(s => s.LaneId == oltre);
        Assert.Equal(-50m, state.RealizedPnl);
    }

    /// <summary>Senza un prezzo recente non si chiude: non si sceglie un prezzo che non si conosce.</summary>
    [Fact]
    public async Task Senza_prezzo_recente_non_chiude()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.OpenPositions.Add(new OpenPosition
            {
                LaneId = TradingLanes.Count, PositionId = "senzaprezzo", Symbol = "IGNOTO/USDT",
                Side = OrderSide.Buy, Quantity = 1m, EntryPrice = 1m,
                OpenedInMode = TradingMode.Paper, OpenedAtUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var (ok, message) = await svc.CloseOrphanAsync("senzaprezzo", "admin");

        Assert.False(ok);
        Assert.Contains("prezzo", message, StringComparison.OrdinalIgnoreCase);
        await using var check = await db.CreateDbContextAsync();
        Assert.Single(await check.OpenPositions.AsNoTracking().ToListAsync());
    }

    // ------------------------------------------------------------------ misura su richiesta

    /// <summary>
    /// Ogni condizione mancante deve produrre un MOTIVO leggibile, non un'eccezione: questa
    /// diagnostica si guarda dalla pagina che comanda il motore, e farla esplodere li' e' peggio che
    /// non averla.
    /// </summary>
    [Fact]
    public async Task Senza_configurazione_della_corsia_si_spiega_invece_di_esplodere()
    {
        var (svc, _) = await BuildAsync();

        var (report, reason) = await svc.MeasureLagAsync(0);

        Assert.Null(report);
        Assert.NotNull(reason);
        Assert.Contains("configurazione", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Una corsia senza stop non ha un'uscita protettiva di cui misurare il ritardo, e lo dice.</summary>
    [Fact]
    public async Task Senza_stop_configurato_si_spiega()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.EnsembleStates.Add(new EnsembleState
            {
                LaneId = 0,
                ConfigurationJson = """
                {"symbol":"BTC/USDT","timeframe":"1h","strategies":[{"stopLossPercent":0}]}
                """,
                StatusJson = "{}",
                LastUpdatedUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var (report, reason) = await svc.MeasureLagAsync(0);

        Assert.Null(report);
        Assert.Contains("stop", reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Serie di corsia senza alcuna risoluzione piu' fine: non misurabile, e va detto invece di
    /// restituire un rapporto vuoto che sembra una misura riuscita con zero risultati.
    /// </summary>
    [Fact]
    public async Task Senza_risoluzione_piu_fine_si_dichiara_non_misurabile()
    {
        var (svc, db) = await BuildAsync();
        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.EnsembleStates.Add(new EnsembleState
            {
                LaneId = 0,
                ConfigurationJson = """
                {"symbol":"BTC/USDT","timeframe":"1m","strategies":[{"stopLossPercent":2}]}
                """,
                StatusJson = "{}",
                LastUpdatedUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var (report, reason) = await svc.MeasureLagAsync(0);

        Assert.Null(report);
        Assert.Contains("non misurabile", reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Percorso felice: con candele di corsia e candele fini a database, la misura gira e produce
    /// gli stessi campi che il pannello mostra — usando i bracket VERI letti dalla configurazione,
    /// non parametri inventati dal servizio.
    /// </summary>
    [Fact]
    public async Task Con_i_dati_a_posto_la_misura_gira_coi_bracket_veri()
    {
        var (svc, db) = await BuildAsync();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.EnsembleStates.Add(new EnsembleState
            {
                LaneId = 0,
                ConfigurationJson = """
                {"symbol":"BTC/USDT","timeframe":"1h","strategies":[{"stopLossPercent":2,"takeProfitPercent":5}]}
                """,
                StatusJson = "{}",
                LastUpdatedUtc = DateTime.UtcNow,
            });

            // 400 barre orarie che oscillano, e le 5m corrispondenti: abbastanza perche' qualche
            // posizione simulata tocchi lo stop.
            for (var i = 0; i < 400; i++)
            {
                var close = 100m + (decimal)(Math.Sin(i / 7.0) * 4.0);
                ctx.OhlcvData.Add(new OhlcvData
                {
                    Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = t0.AddHours(i),
                    Open = close, High = close + 0.5m, Low = close - 0.5m, Close = close, Volume = 1m,
                });
                for (var k = 0; k < 12; k++)
                {
                    ctx.OhlcvData.Add(new OhlcvData
                    {
                        Symbol = "BTC/USDT", Timeframe = "5m", TimestampUtc = t0.AddHours(i).AddMinutes(5 * k),
                        Open = close, High = close + 0.5m, Low = close - 0.5m, Close = close, Volume = 1m,
                    });
                }
            }
            await ctx.SaveChangesAsync();
        }

        var (report, reason) = await svc.MeasureLagAsync(0, sampleEveryNBars: 8);

        Assert.Null(reason);
        Assert.NotNull(report);
        Assert.Equal("1h", report!.LaneTimeframe);
        Assert.Equal("5m", report.FineTimeframe);        // scelta dai dati, non da una costante
        Assert.Equal(2m, report.StopLossPercent);        // bracket VERO della configurazione
        Assert.Equal(5m, report.TakeProfitPercent);
        Assert.True(report.PositionsSimulated > 0);
    }
}
