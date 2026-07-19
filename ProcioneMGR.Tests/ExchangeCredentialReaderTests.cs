using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Bug B2 (docs/TEST-UI-2026-07-18.md): una riga di ExchangeCredentials cifrata con una master key
/// DIVERSA da quella del processo corrente abbatteva l'intera query EF (il converter decifra
/// dentro la materializzazione → AuthenticationTagMismatchException) e con essa la pagina
/// /settings/exchanges. <see cref="ExchangeCredentialReader"/> legge il ciphertext grezzo (vista
/// keyless) e decifra RIGA PER RIGA: qui si verifica che la riga "straniera" venga flaggata senza
/// rompere le altre, che non trapeli mai plaintext parziale, e che il percorso trading preferisca
/// una riga decifrabile quando esiste.
///
/// Le righe sono inserite via SQL grezzo con ciphertext PRE-calcolato (mai via converter EF): il
/// converter cattura l'IEncryptionService del PRIMO model building e renderebbe il seed dipendente
/// dall'ordine di esecuzione della suite.
/// </summary>
[Collection("Postgres")]
public sealed class ExchangeCredentialReaderTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ExchangeCredentialReaderTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    // --- Setup ---------------------------------------------------------------------------------

    /// <summary>AES-GCM reale con la chiave indicata (env PROCIONE_MGR_MASTER_KEY neutralizzata,
    /// come in MasterKeyDetectionTests: su una macchina configurata vincerebbe sul config).</summary>
    private static AesGcmEncryptionService BuildAes(string masterKey)
    {
        var saved = Environment.GetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY");
        Environment.SetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY", null);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:MasterKey"] = masterKey })
                .Build();
            return new AesGcmEncryptionService(config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY", saved);
        }
    }

    private static string RandomKey() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private async Task<IDbContextFactory<ApplicationDbContext>> BuildDbAsync()
    {
        var services = new ServiceCollection();
        // Passthrough SOLO per soddisfare il costruttore del context: il seed avviene via SQL
        // grezzo e il reader non usa mai il converter.
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new ApplicationUser { Id = "u1", UserName = "t", Email = "t@t.io" });
            await db.SaveChangesAsync();
        }
        return dbFactory;
    }

    /// <summary>Inserisce una riga col ciphertext così com'è (o plaintext corrotto, per il caso base64 invalido).</summary>
    private static async Task SeedRawAsync(IDbContextFactory<ApplicationDbContext> dbFactory,
        ExchangeName exchange, bool testnet, string label, string apiKeyStored, string apiSecretStored,
        string? passphraseStored, DateTime createdAt)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""ExchangeCredentials""
                (""UserId"", ""ExchangeName"", ""Label"", ""ApiKey"", ""ApiSecret"", ""Passphrase"", ""IsTestnet"", ""CreatedAt"")
            VALUES ('u1', {exchange.ToString()}, {label}, {apiKeyStored}, {apiSecretStored},
                    CAST({passphraseStored} AS text), {testnet}, {createdAt})");
    }

    // --- Test ----------------------------------------------------------------------------------

    [Fact]
    public async Task LoadForUser_RowFromForeignKey_IsFlagged_WithoutBreakingTheOthers()
    {
        var dbFactory = await BuildDbAsync();
        var foreignAes = BuildAes(RandomKey());   // la chiave "vecchia", non più in uso
        var currentAes = BuildAes(RandomKey());   // la chiave del processo corrente

        await SeedRawAsync(dbFactory, ExchangeName.Binance, testnet: true, "Vecchia",
            foreignAes.Encrypt("abcd1234efgh5678"), foreignAes.Encrypt("old-secret"), null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedRawAsync(dbFactory, ExchangeName.Bitget, testnet: false, "Nuova",
            currentAes.Encrypt("wxyz9876stuv5432"), currentAes.Encrypt("new-secret"), currentAes.Encrypt("pass"),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var rows = await reader.LoadForUserAsync("u1");

        Assert.Equal(2, rows.Count);

        // Ordinamento come la pagina: le più recenti prima.
        var good = rows[0];
        Assert.True(good.IsDecryptable);
        Assert.Equal("Nuova", good.Label);
        Assert.Equal("wxyz9876stuv5432", good.ApiKey);
        Assert.Equal("new-secret", good.ApiSecret);
        Assert.Equal("pass", good.Passphrase);
        Assert.Equal("wxyz******5432", good.MaskedApiKey);

        var bad = rows[1];
        Assert.False(bad.IsDecryptable);
        Assert.Equal("Vecchia", bad.Label);
        // Mai plaintext (nemmeno parziale) da una riga indecifrabile.
        Assert.Null(bad.ApiKey);
        Assert.Null(bad.ApiSecret);
        Assert.Null(bad.Passphrase);
        Assert.Equal(string.Empty, bad.MaskedApiKey);
    }

    [Fact]
    public async Task LoadForUser_OnlyPassphraseFromForeignKey_FlagsTheWholeRow_NoPartialPlaintext()
    {
        // Caso subdolo: ApiKey/Secret decifrabili ma passphrase no (riga toccata a metà da una
        // rotazione). La riga è inutilizzabile per firmare su Bitget: tutto-o-niente.
        var dbFactory = await BuildDbAsync();
        var foreignAes = BuildAes(RandomKey());
        var currentAes = BuildAes(RandomKey());

        await SeedRawAsync(dbFactory, ExchangeName.Bitget, testnet: false, "Mista",
            currentAes.Encrypt("abcd1234efgh5678"), currentAes.Encrypt("s"), foreignAes.Encrypt("p"),
            DateTime.UtcNow);

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var row = Assert.Single(await reader.LoadForUserAsync("u1"));

        Assert.False(row.IsDecryptable);
        Assert.Null(row.ApiKey);
        Assert.Null(row.ApiSecret);
        Assert.Null(row.Passphrase);
    }

    [Fact]
    public async Task LoadForUser_GarbageNonBase64Row_IsFlagged_NotThrown()
    {
        // Dato scritto in chiaro (es. da un tool con cifratura passthrough): base64 invalido →
        // FormatException, stessa sorte della chiave sbagliata: flag, non crash.
        var dbFactory = await BuildDbAsync();
        var currentAes = BuildAes(RandomKey());

        await SeedRawAsync(dbFactory, ExchangeName.Binance, testnet: true, "InChiaro",
            "non-cifrata!", "nemmeno-questa!", null, DateTime.UtcNow);

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var row = Assert.Single(await reader.LoadForUserAsync("u1"));

        Assert.False(row.IsDecryptable);
    }

    [Fact]
    public async Task FindForTrading_PrefersTheDecryptableRow_OverAnOlderUndecryptableOne()
    {
        // Scenario-rimedio del badge: l'utente reinserisce le credenziali dopo il cambio chiave ma
        // NON elimina la vecchia riga. L'avvio Testnet/Live deve scegliere quella nuova, non
        // inciampare (per ordine di Id) su quella vecchia.
        var dbFactory = await BuildDbAsync();
        var foreignAes = BuildAes(RandomKey());
        var currentAes = BuildAes(RandomKey());

        await SeedRawAsync(dbFactory, ExchangeName.Binance, testnet: true, "Vecchia",
            foreignAes.Encrypt("old-key"), foreignAes.Encrypt("old-secret"), null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedRawAsync(dbFactory, ExchangeName.Binance, testnet: true, "Nuova",
            currentAes.Encrypt("new-key"), currentAes.Encrypt("new-secret"), null,
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var found = await reader.FindForTradingAsync(ExchangeName.Binance, testnet: true);

        Assert.NotNull(found);
        Assert.True(found.IsDecryptable);
        Assert.Equal("Nuova", found.Label);
        Assert.Equal("new-key", found.ApiKey);
    }

    [Fact]
    public async Task FindForTrading_OnlyUndecryptableRows_ReturnsTheFlaggedRow()
    {
        var dbFactory = await BuildDbAsync();
        var foreignAes = BuildAes(RandomKey());
        var currentAes = BuildAes(RandomKey());

        await SeedRawAsync(dbFactory, ExchangeName.Binance, testnet: true, "Vecchia",
            foreignAes.Encrypt("k"), foreignAes.Encrypt("s"), null, DateTime.UtcNow);

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);
        var found = await reader.FindForTradingAsync(ExchangeName.Binance, testnet: true);

        Assert.NotNull(found);
        Assert.False(found.IsDecryptable);
        Assert.Equal("Vecchia", found.Label);
    }

    [Fact]
    public async Task FindForTrading_NoMatchingRows_ReturnsNull()
    {
        var dbFactory = await BuildDbAsync();
        var currentAes = BuildAes(RandomKey());

        var reader = new ExchangeCredentialReader(dbFactory, currentAes, NullLogger<ExchangeCredentialReader>.Instance);

        Assert.Null(await reader.FindForTradingAsync(ExchangeName.Binance, testnet: true));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
