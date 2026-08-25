using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J8, PRD autonomia-operativa 2026-08-25] L'orologio dell'osservazione che non si azzera al
/// riavvio. Il fatto che lo motiva: con observation = now − StartedAtUtc, la finestra continua più
/// lunga mai raggiunta dalla flotta è stata 20g 3h contro i 21g di RetireMinWeeks — il criterio di
/// ritiro per Sharpe non ha MAI potuto esprimersi. Questi test pinnano la politica di accredito:
/// cumulo fra tick, azzeramento SOLO al cambio di identità, tetto sui buchi lunghi (sottostimare,
/// mai gonfiare), niente accredito da ferma.
/// </summary>
public class LaneObservationLedgerStaticTests
{
    [Fact]
    public void Identity_IsOrderInsensitive_OnStrategyIds()
    {
        // Lo stesso ensemble enumerato in due ordini è lo stesso esperimento, non due.
        var a = LaneObservationLedger.BuildIdentity("BTC/USDT", "4h", ["s1", "s2"]);
        var b = LaneObservationLedger.BuildIdentity("BTC/USDT", "4h", ["s2", "s1"]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Identity_Distinguishes_SymbolTimeframeAndLegs()
    {
        var basis = LaneObservationLedger.BuildIdentity("BTC/USDT", "4h", ["s1"]);
        Assert.NotEqual(basis, LaneObservationLedger.BuildIdentity("ETH/USDT", "4h", ["s1"]));
        Assert.NotEqual(basis, LaneObservationLedger.BuildIdentity("BTC/USDT", "1h", ["s1"]));
        Assert.NotEqual(basis, LaneObservationLedger.BuildIdentity("BTC/USDT", "4h", ["s1", "s2"]));
    }
}

[Collection("Postgres")]
public class LaneObservationLedgerTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public LaneObservationLedgerTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<LaneObservationLedger> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        return new LaneObservationLedger(dbFactory, NullLogger<LaneObservationLedger>.Instance);
    }

    private static readonly DateTime T0 = new(2026, 8, 25, 10, 0, 0, DateTimeKind.Utc);
    private const string Id1 = "BTC/USDT|4h|s1";

    [Fact]
    public async Task Accumula_FraTick_E_SopravviveAlRiavvio()
    {
        var ledger = await BuildAsync();

        // Primo avvistamento: zero osservazione, ancora = adesso.
        var (obs0, first0) = await ledger.AccumulateAsync(7, Id1, isRunning: true, T0);
        Assert.Equal(TimeSpan.Zero, obs0);
        Assert.Equal(T0, first0);

        // Due tick da 15 minuti: 30 minuti cumulati. Un «riavvio del motore» fra i due non esiste
        // per questo registro: il tempo è persistito, non dedotto da StartedAtUtc.
        await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(15));
        var (obs2, first2) = await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30), obs2);
        Assert.Equal(T0, first2); // l'ancora dei trade non si muove finché l'identità è la stessa
    }

    [Fact]
    public async Task BucoLungo_AccreditatoAlMassimoPerIlTetto()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(7, Id1, true, T0);

        // Guscio spento per 6 ore: si accredita il tetto (45′), non le 6 ore — durante l'assenza
        // il motore può aver operato oppure no, e non sapere non è un motivo per accreditare.
        var (obs, _) = await ledger.AccumulateAsync(7, Id1, true, T0.AddHours(6));
        Assert.Equal(LaneObservationLedger.MaxCreditPerGap, obs);
    }

    [Fact]
    public async Task CorsiaFerma_NonAccredita_MaAvanzaIlRiferimento()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(7, Id1, true, T0);
        await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(15));           // 15′

        // Tre tick da ferma: zero accredito, ma il riferimento avanza — al riavvio il delta non
        // deve inglobare la pausa.
        await ledger.AccumulateAsync(7, Id1, isRunning: false, T0.AddMinutes(30));
        await ledger.AccumulateAsync(7, Id1, isRunning: false, T0.AddMinutes(45));
        var (obsFerma, _) = await ledger.AccumulateAsync(7, Id1, false, T0.AddMinutes(60));
        Assert.Equal(TimeSpan.FromMinutes(15), obsFerma);

        // Riparte: si accredita solo il delta dal riferimento, non la pausa.
        var (obsDopo, _) = await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(75));
        Assert.Equal(TimeSpan.FromMinutes(30), obsDopo);
    }

    [Fact]
    public async Task CambioIdentita_AzzeraOrologioEAncora()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(7, Id1, true, T0);
        await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(15));

        // Un altro esperimento arriva in corsia: 15 minuti su GridMeanReversion UNI non dicono
        // nulla sul Composite DOT che la sostituisce.
        var t2 = T0.AddMinutes(30);
        var (obs, first) = await ledger.AccumulateAsync(7, "DOT/USDT|1h|s9", true, t2);
        Assert.Equal(TimeSpan.Zero, obs);
        Assert.Equal(t2, first);
    }

    [Fact]
    public async Task OrologioIndietro_NonAccreditaNegativo()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(7, Id1, true, T0);
        await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(15));

        var (obs, _) = await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(10)); // nel passato
        Assert.Equal(TimeSpan.FromMinutes(15), obs);
    }

    [Fact]
    public async Task CorsieDiverse_RegistriIndipendenti()
    {
        var ledger = await BuildAsync();
        await ledger.AccumulateAsync(6, Id1, true, T0);
        await ledger.AccumulateAsync(7, Id1, true, T0);
        await ledger.AccumulateAsync(6, Id1, true, T0.AddMinutes(15));

        var (obs6, _) = await ledger.AccumulateAsync(6, Id1, true, T0.AddMinutes(30));
        var (obs7, _) = await ledger.AccumulateAsync(7, Id1, true, T0.AddMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30), obs6);
        Assert.Equal(TimeSpan.FromMinutes(30), obs7);
    }
}
