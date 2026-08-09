using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Security;

/// <summary>Esito della ri-cifratura di massa: quante righe viste, riportate sulla chiave corrente, saltate.</summary>
/// <param name="Total">Righe cifrate censite (credenziali exchange + chiavi AI).</param>
/// <param name="ReEncrypted">Righe riscritte con la chiave corrente (erano su una chiave precedente).</param>
/// <param name="AlreadyCurrent">Righe già sulla chiave corrente: non toccate.</param>
/// <param name="Unreadable">Righe che NESSUNA chiave del ring apre: restano com'erano, vanno reinserite a mano (badge in /settings/exchanges).</param>
public sealed record MasterKeyReEncryptReport(int Total, int ReEncrypted, int AlreadyCurrent, int Unreadable)
{
    public override string ToString() =>
        $"{Total} righe: {ReEncrypted} ri-cifrate, {AlreadyCurrent} già sulla chiave corrente, {Unreadable} indecifrabili.";
}

/// <summary>
/// Ri-cifratura di massa dei segreti a riposo con la chiave CORRENTE (Fase 0 PRD-RISANAMENTO:
/// lo "strumento di re-cifratura" che il TODO storico di <see cref="AesGcmEncryptionService"/>
/// dichiarava mancante). Si usa DURANTE una rotazione, quando il keyring ha la vecchia chiave in
/// PreviousMasterKeys: le righe ancora sulla vecchia vengono decifrate col ring e riscritte con la
/// corrente. Al termine si può svuotare PreviousMasterKeys.
/// </summary>
public interface IMasterKeyRotationService
{
    /// <summary>
    /// Ri-cifra tutte le righe apribili col keyring che NON sono già sulla chiave corrente.
    /// Idempotente: una seconda esecuzione trova tutto AlreadyCurrent. Le righe indecifrabili non
    /// vengono mai toccate (nessuna perdita di dato: restano leggibili se la loro chiave tornasse).
    /// </summary>
    Task<MasterKeyReEncryptReport> ReEncryptAllAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IMasterKeyRotationService"/>
/// <remarks>
/// COME funziona la riscrittura: si carica l'entità EF (il converter decifra col RING, quindi le
/// righe su chiave precedente ora materializzano), si marca la proprietà cifrata come modificata a
/// parità di valore, e al SaveChanges il converter ri-cifra con la chiave CORRENTE. Il forcing di
/// IsModified è necessario perché lo snapshot di EF confronta il valore in chiaro — identico — e
/// senza il flag non scriverebbe nulla.
///
/// RESILIENZA (lezione del bug B2): la materializzazione EF decifra DENTRO la query, quindi una
/// sola riga indecifrabile abbatterebbe una query cumulativa. Qui si classifica prima dal
/// ciphertext grezzo (vista keyless per le credenziali exchange, SqlQueryRaw per le chiavi AI) e
/// si caricano SOLO le righe di cui il ring risponde, una per una.
/// </remarks>
public sealed class MasterKeyRotationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMasterKeyRing ring,
    IEncryptionService encryption,
    ILogger<MasterKeyRotationService> logger) : IMasterKeyRotationService
{
    public async Task<MasterKeyReEncryptReport> ReEncryptAllAsync(CancellationToken ct = default)
    {
        int total = 0, reEncrypted = 0, alreadyCurrent = 0, unreadable = 0;

        // --- Credenziali exchange: classificazione dal ciphertext grezzo (vista keyless B2). ---
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.ExchangeCredentialCiphertexts.ToListAsync(ct);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                total++;

                var fields = new List<string> { row.ApiKey, row.ApiSecret };
                if (row.Passphrase is not null) fields.Add(row.Passphrase);

                if (fields.All(ring.IsEncryptedWithCurrentKey))
                {
                    alreadyCurrent++;
                    continue;
                }
                if (!fields.All(CanDecryptWithRing))
                {
                    unreadable++;
                    logger.LogWarning(
                        "Rotazione: credenziale exchange Id={Id} ({Exchange} '{Label}') indecifrabile con " +
                        "l'intero keyring — non toccata, va reinserita in /settings/exchanges.",
                        row.Id, row.ExchangeName, row.Label);
                    continue;
                }

                // Apribile col ring ma non sulla corrente: riscrittura riga singola.
                var entity = await db.ExchangeCredentials.FirstAsync(c => c.Id == row.Id, ct);
                var entry = db.Entry(entity);
                entry.Property(e => e.ApiKey).IsModified = true;
                entry.Property(e => e.ApiSecret).IsModified = true;
                if (entity.Passphrase is not null) entry.Property(e => e.Passphrase).IsModified = true;
                await db.SaveChangesAsync(ct);
                reEncrypted++;
                logger.LogInformation(
                    "Rotazione: credenziale exchange Id={Id} ({Exchange} '{Label}') ri-cifrata con la chiave corrente.",
                    row.Id, row.ExchangeName, row.Label);
            }
        }

        // --- Chiavi AI: nessuna vista keyless — il ciphertext si legge con SQL grezzo, una riga
        //     alla volta, così una riga straniera non abbatte le altre (stesso principio B2). ---
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var aiRows = await db.Database
                .SqlQuery<AiCredentialCiphertextRow>($"""SELECT "Id", "Provider", "ApiKey" FROM "AiCredentials" """)
                .ToListAsync(ct);
            foreach (var row in aiRows)
            {
                ct.ThrowIfCancellationRequested();
                total++;

                if (ring.IsEncryptedWithCurrentKey(row.ApiKey))
                {
                    alreadyCurrent++;
                    continue;
                }
                if (!CanDecryptWithRing(row.ApiKey))
                {
                    unreadable++;
                    logger.LogWarning(
                        "Rotazione: chiave AI '{Provider}' (Id={Id}) indecifrabile con l'intero keyring — " +
                        "non toccata, va reinserita in /admin/ai-supervisor.", row.Provider, row.Id);
                    continue;
                }

                var entity = await db.AiCredentials.FirstAsync(c => c.Id == row.Id, ct);
                db.Entry(entity).Property(e => e.ApiKey).IsModified = true;
                entity.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                reEncrypted++;
                logger.LogInformation("Rotazione: chiave AI '{Provider}' ri-cifrata con la chiave corrente.", row.Provider);
            }
        }

        var report = new MasterKeyReEncryptReport(total, reEncrypted, alreadyCurrent, unreadable);
        logger.LogInformation("Rotazione master key completata: {Report}", report);
        return report;
    }

    private bool CanDecryptWithRing(string ciphertext)
    {
        try
        {
            encryption.Decrypt(ciphertext);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                       or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Proiezione grezza di una riga AiCredentials: il ciphertext così com'è sul DB.</summary>
    private sealed record AiCredentialCiphertextRow(int Id, string Provider, string ApiKey);
}
