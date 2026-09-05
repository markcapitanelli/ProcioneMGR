using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-05] <b>Il forward test del carry lascia una misura.</b> Prima lo stato viveva in
/// memoria e il funding «incassato» veniva solo azzerato all'apertura: a ogni rischieramento del pod
/// i sei simboli «riaprivano» e nulla sopravviveva. Qui si prova che un episodio si apre una volta
/// sola, accredita ogni evento di funding una volta sola, chiude col netto del modello di costo, e
/// che al riavvio il motore ritrova le posizioni aperte invece di riaprirle.
/// </summary>
[Collection("Postgres")]
public sealed class CarryLedgerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public CarryLedgerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(CarryLedger Ledger, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        return (new CarryLedger(dbFactory, NullLogger<CarryLedger>.Instance), dbFactory);
    }

    private static readonly DateTime T0 = new(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Livello 1: l'aritmetica contro numeri a mano. 5 000 × 0,01 % = 0,50; giro completo 2×(0,1+0,03)+2×(0,05+0,03) = 0,42 %.</summary>
    [Fact]
    public void LAritmetica_ControValoriAMano()
    {
        Assert.Equal(0.5m, CarryLedgerMath.FundingQuote(5_000m, 0.01m));
        Assert.Equal(-0.25m, CarryLedgerMath.FundingQuote(5_000m, -0.005m));
        Assert.Equal(0.42m, CarryLedgerMath.RoundTripCostPercent(new CarryConfiguration()));
        Assert.Equal(0.25m - 18m, CarryLedgerMath.NetQuote(0.25m, 5_000m, 0.36m));
    }

    [Fact]
    public async Task UnEpisodio_SiApreUnaVoltaSola_AccreditaOgniEventoUnaVoltaSola_EChiudeColNetto()
    {
        var (ledger, db) = await BuildAsync();

        await ledger.OpenAsync("BTC/USDT", "Paper", 5_000m, 6.6m, 0.36m, lastFundingUtc: T0, nowUtc: T0);
        await ledger.OpenAsync("BTC/USDT", "Paper", 5_000m, 6.6m, 0.36m, lastFundingUtc: T0, nowUtc: T0); // doppione: ignorato

        // L'evento che ha fatto aprire (T0) non si incassa; T1 sì, una volta sola; T2 negativo paga.
        Assert.Equal(0m, await ledger.AccrueAsync("BTC/USDT", "Paper", T0, 0.02m));
        Assert.Equal(0.01m, await ledger.AccrueAsync("BTC/USDT", "Paper", T0.AddHours(8), 0.01m));
        Assert.Equal(0.01m, await ledger.AccrueAsync("BTC/USDT", "Paper", T0.AddHours(8), 0.01m));
        Assert.Equal(0.005m, await ledger.AccrueAsync("BTC/USDT", "Paper", T0.AddHours(16), -0.005m));

        await ledger.CloseAsync("BTC/USDT", "Paper", 3.0m, T0.AddHours(20), "test");

        await using var ctx = await db.CreateDbContextAsync();
        var righe = await ctx.CarryLedger.OrderBy(e => e.Id).ToListAsync();
        var e = Assert.Single(righe);
        Assert.Equal(2, e.FundingEventsAccrued);
        Assert.Equal(0.005m, e.FundingCollectedPercent);
        Assert.Equal(0.25m, e.FundingCollectedQuote);
        Assert.Equal(T0.AddHours(20), e.ClosedUtc);
        Assert.Equal(3.0m, e.ExitAnnualizedPercent);
        Assert.Equal(0.25m - 18m, e.NetQuote);

        // Dopo la chiusura non c'è più nulla da accreditare.
        Assert.Null(await ledger.AccrueAsync("BTC/USDT", "Paper", T0.AddHours(24), 0.01m));
    }

    /// <summary>Al riavvio il motore ritrova gli episodi aperti della SUA modalità, con il funding già incassato, e non li riapre.</summary>
    [Fact]
    public async Task AlRiavvio_IlMotoreRitrovaGliEpisodiAperti_ENonLiRiapre()
    {
        var (ledger, _) = await BuildAsync();
        await ledger.OpenAsync("ETH/USDT", "Paper", 5_000m, 7.0m, 0.36m, T0, T0);
        await ledger.AccrueAsync("ETH/USDT", "Paper", T0.AddHours(8), 0.02m);
        await ledger.OpenAsync("SOL/USDT", "Testnet", 5_000m, 7.0m, 0.36m, T0, T0);   // altra modalità: non si ripristina
        await ledger.OpenAsync("XRP/USDT", "Paper", 5_000m, 7.0m, 0.36m, T0, T0);
        await ledger.CloseAsync("XRP/USDT", "Paper", 1.0m, T0.AddHours(8), "chiuso");  // chiuso: non si ripristina

        var engine = new CarryEngine(new PaperCarryExecutor(NullLogger<PaperCarryExecutor>.Instance), new CarryConfiguration(), NullLogger<CarryEngine>.Instance);
        var n = await ledger.RestoreAsync(engine, "Paper");

        Assert.Equal(1, n);
        var st = Assert.Contains("ETH/USDT", engine.States);
        Assert.True(st.InPosition);
        Assert.Equal(5_000m, st.NotionalQuote);
        Assert.Equal(0.02m, st.FundingCollectedPercent);
        Assert.Equal(T0, st.OpenedUtc);

        // Con la posizione ripristinata, un funding ancora alto NON riapre: tiene.
        var azione = await engine.EvaluateAsync("ETH/USDT", Enumerable.Repeat(0.01m, 9).ToList(), CancellationToken.None);
        Assert.Equal(CarryAction.Hold, azione);
    }
}
