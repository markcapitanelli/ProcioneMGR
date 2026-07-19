using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Security;

/// <summary>
/// Credenziale exchange decifrata riga per riga. Se <see cref="IsDecryptable"/> è false la riga
/// esiste sul DB ma è cifrata con una master key DIVERSA da quella del processo corrente: i campi
/// segreti sono null (mai plaintext parziale) e la UI deve mostrare il badge "reinserire le
/// credenziali" invece di usarla.
/// </summary>
public sealed record DecryptedExchangeCredential(
    int Id,
    ExchangeName ExchangeName,
    string Label,
    bool IsTestnet,
    DateTime CreatedAt,
    bool IsDecryptable,
    string? ApiKey,
    string? ApiSecret,
    string? Passphrase)
{
    /// <summary>ApiKey mascherata per la UI (mai esporre il secret). Vuota se non decifrabile.</summary>
    public string MaskedApiKey => ApiKey is null ? string.Empty : ExchangeCredential.Mask(ApiKey);
}

/// <summary>
/// Lettura RESILIENTE delle credenziali exchange (bug B2, docs/TEST-UI-2026-07-18.md): il
/// converter EF decifra dentro la materializzazione, quindi una sola riga cifrata con una master
/// key diversa faceva esplodere l'intera query (AuthenticationTagMismatchException) — Internal
/// Server Error su /settings/exchanges e avvio Testnet/Live abbattuto da un'eccezione grezza.
/// Qui si legge il ciphertext (proiezione keyless <see cref="ExchangeCredentialCiphertext"/>) e si
/// decifra in memoria riga per riga: il fallimento di UNA riga la flagga soltanto.
/// </summary>
public interface IExchangeCredentialReader
{
    /// <summary>Tutte le credenziali di un utente (per /settings/exchanges), le più recenti prima.</summary>
    Task<IReadOnlyList<DecryptedExchangeCredential>> LoadForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// La credenziale da usare per il trading su (exchange, testnet) — stessa semantica storica di
    /// TradingEngine.LoadCredentialsAsync: qualunque utente (piattaforma a operatore singolo).
    /// PREFERISCE una riga decifrabile se ne esiste una (caso tipico: credenziali reinserite dopo
    /// un cambio di master key, con la vecchia riga ancora in tabella); se esistono solo righe
    /// indecifrabili restituisce la prima, flaggata; null se non ce n'è nessuna.
    /// </summary>
    Task<DecryptedExchangeCredential?> FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default);

    /// <summary>
    /// Censimento per il probe di avvio (Fase 3-C2, PRD Autonomia): quante credenziali esistono e
    /// quante NON si decifrano con la master key corrente — di qualunque utente. Non espone dati.
    /// </summary>
    Task<(int Total, int Unreadable)> CountUnreadableAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IExchangeCredentialReader"/>
public sealed class ExchangeCredentialReader(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IEncryptionService encryption,
    ILogger<ExchangeCredentialReader> logger) : IExchangeCredentialReader
{
    public async Task<IReadOnlyList<DecryptedExchangeCredential>> LoadForUserAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ExchangeCredentialCiphertexts
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(DecryptRow).ToList();
    }

    public async Task<DecryptedExchangeCredential?> FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ExchangeCredentialCiphertexts
            .Where(c => c.ExchangeName == exchange && c.IsTestnet == testnet)
            .OrderBy(c => c.Id) // deterministico (il vecchio FirstOrDefault non aveva ordine)
            .ToListAsync(ct);
        var decrypted = rows.Select(DecryptRow).ToList();
        return decrypted.FirstOrDefault(d => d.IsDecryptable) ?? decrypted.FirstOrDefault();
    }

    public async Task<(int Total, int Unreadable)> CountUnreadableAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ExchangeCredentialCiphertexts.ToListAsync(ct);
        var unreadable = rows.Count(r => !DecryptRow(r).IsDecryptable);
        return (rows.Count, unreadable);
    }

    private DecryptedExchangeCredential DecryptRow(ExchangeCredentialCiphertext row)
    {
        // Tutto o niente: se anche UN solo campo non si decifra la riga è inutilizzabile per
        // firmare ordini, e non si espone mai plaintext parziale.
        string? passphrase = null;
        if (TryDecrypt(row.ApiKey, out var apiKey)
            && TryDecrypt(row.ApiSecret, out var apiSecret)
            && (row.Passphrase is null || TryDecrypt(row.Passphrase, out passphrase)))
        {
            return new DecryptedExchangeCredential(row.Id, row.ExchangeName, row.Label, row.IsTestnet,
                row.CreatedAt, IsDecryptable: true, apiKey, apiSecret, passphrase);
        }

        logger.LogWarning(
            "Credenziale exchange Id={Id} ({Exchange} '{Label}') NON decifrabile con la master key " +
            "corrente: fu cifrata con una Security:MasterKey diversa. Va reinserita in /settings/exchanges.",
            row.Id, row.ExchangeName, row.Label);
        return new DecryptedExchangeCredential(row.Id, row.ExchangeName, row.Label, row.IsTestnet,
            row.CreatedAt, IsDecryptable: false, ApiKey: null, ApiSecret: null, Passphrase: null);
    }

    private bool TryDecrypt(string ciphertext, out string? plaintext)
    {
        try
        {
            plaintext = encryption.Decrypt(ciphertext);
            return true;
        }
        // CryptographicException copre sia l'AuthenticationTagMismatchException (chiave diversa)
        // sia payload corrotti/versione ignota; FormatException il base64 invalido (es. dato
        // scritto in chiaro da un vecchio tool); ArgumentException la stringa vuota.
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            plaintext = null;
            return false;
        }
    }
}
