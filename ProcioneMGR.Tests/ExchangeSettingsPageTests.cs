using System.Security.Claims;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests;

/// <summary>
/// Bug B2 (docs/TEST-UI-2026-07-18.md), lato pagina: /settings/exchanges andava in Internal
/// Server Error (AuthenticationTagMismatchException nella materializzazione EF) se in tabella
/// c'era UNA riga cifrata con una master key diversa. Ora la pagina carica via
/// <see cref="IExchangeCredentialReader"/>: la riga indecifrabile deve comparire col badge
/// "reinserire le credenziali" (Test disabilitato), le altre righe restare pienamente usabili.
/// </summary>
public class ExchangeSettingsPageTests : BunitContext
{
    public ExchangeSettingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    // --- Fakes ----------------------------------------------------------------------------------

    private sealed class FakeReader(IReadOnlyList<DecryptedExchangeCredential> rows) : IExchangeCredentialReader
    {
        public Task<IReadOnlyList<DecryptedExchangeCredential>> LoadForUserAsync(string userId, CancellationToken ct = default)
            => Task.FromResult(rows);

        public Task<DecryptedExchangeCredential?> FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)
            => throw new NotSupportedException("La pagina non usa il percorso trading.");
    }

    /// <summary>Il rendering non deve toccare il DB: LoadAsync passa dal reader, il DbContext serve solo ad Aggiungi/Elimina.</summary>
    private sealed class ThrowingDbFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Il rendering non deve toccare il DB.");
    }

    private sealed class ThrowingExchangeFactory : IExchangeClientFactory
    {
        public IExchangeClient Create(ExchangeName exchange) => throw new NotSupportedException();
        public IExchangeClient Create(string exchangeName) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(ExchangeName exchange) => throw new NotSupportedException();
        public IFuturesExchangeClient CreateFutures(string exchangeName) => throw new NotSupportedException();
    }

    // --- Test -----------------------------------------------------------------------------------

    [Fact]
    public void UndecryptableRow_ShowsBadge_AndKeepsTheOtherRowsUsable()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("mark");
        // La pagina risolve l'utente dal claim NameIdentifier, non dal nome.
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "u1"));

        var now = DateTime.UtcNow;
        var bad = new DecryptedExchangeCredential(1, ExchangeName.Binance, "Vecchia", IsTestnet: true,
            now.AddDays(-30), IsDecryptable: false, ApiKey: null, ApiSecret: null, Passphrase: null);
        var good = new DecryptedExchangeCredential(2, ExchangeName.Bitget, "Nuova", IsTestnet: false,
            now, IsDecryptable: true, ApiKey: "abcd1234efgh5678", ApiSecret: "s", Passphrase: "p");

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ThrowingDbFactory());
        Services.AddSingleton<IExchangeClientFactory>(new ThrowingExchangeFactory());
        Services.AddSingleton<IExchangeCredentialReader>(new FakeReader([bad, good]));

        var cut = Render<ProcioneMGR.Components.Pages.ExchangeSettings>();

        // La pagina renderizza (niente Internal Server Error) e la riga rotta ha il badge col rimedio.
        cut.WaitForAssertion(() =>
            Assert.Contains("Non decifrabile con la chiave corrente", cut.Markup));
        Assert.Contains("reinserire le credenziali", cut.Markup);

        // La riga sana resta normale: API key mascherata, mai il plaintext completo.
        Assert.Contains("abcd******5678", cut.Markup);
        Assert.DoesNotContain("abcd1234efgh5678", cut.Markup);

        // Test disabilitato SOLO sulla riga indecifrabile (ordine di render: prima "Vecchia").
        var testButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Test")).ToList();
        Assert.Equal(2, testButtons.Count);
        Assert.True(testButtons[0].HasAttribute("disabled"));
        Assert.False(testButtons[1].HasAttribute("disabled"));

        // Elimina resta disponibile anche sulla riga rotta: è il rimedio suggerito.
        var deleteButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Elimina")).ToList();
        Assert.Equal(2, deleteButtons.Count);
        Assert.False(deleteButtons[0].HasAttribute("disabled"));
    }

    [Fact]
    public void NoCredentials_RendersEmptyState()
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("mark");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "u1"));

        Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new ThrowingDbFactory());
        Services.AddSingleton<IExchangeClientFactory>(new ThrowingExchangeFactory());
        Services.AddSingleton<IExchangeCredentialReader>(new FakeReader([]));

        var cut = Render<ProcioneMGR.Components.Pages.ExchangeSettings>();

        cut.WaitForAssertion(() => Assert.Contains("Nessuna credenziale ancora", cut.Markup));
    }
}
