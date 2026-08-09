using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests;

/// <summary>
/// Keyring della rotazione master key (Fase 0 PRD-RISANAMENTO, 2026-08-08 — chiude il TODO storico
/// di <see cref="AesGcmEncryptionService"/>: "manca il supporto multi-chiave"). Le proprietà che
/// contano, tutte qui:
///   1. un payload cifrato con la chiave VECCHIA si decifra quando la vecchia è in PreviousMasterKeys;
///   2. si CIFRA sempre con la corrente (un servizio con la SOLA corrente rilegge tutto il nuovo);
///   3. la classificazione <see cref="IMasterKeyRing.IsEncryptedWithCurrentKey"/> distingue
///      vecchio da nuovo — è ciò che guida la ri-cifratura di massa;
///   4. SENZA keyring il payload vecchio fallisce come sempre (nessun ammorbidimento di default);
///   5. un payload MANOMESSO fallisce anche col keyring pieno (il fallback prova altre chiavi,
///      non perdona i tag rotti);
///   6. il formato v1 è invariato: niente migrazione dei payload esistenti.
/// </summary>
public sealed class MasterKeyRotationTests
{
    private static string RandomKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Costruisce il servizio neutralizzando ENTRAMBE le env del keyring (una macchina
    /// configurata vincerebbe sul config in-memory — stessa cautela di MasterKeyDetectionTests).</summary>
    private static AesGcmEncryptionService Build(string masterKey, params string[] previousKeys)
    {
        var savedCurrent = Environment.GetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY");
        var savedPrevious = Environment.GetEnvironmentVariable("PROCIONE_MGR_PREVIOUS_MASTER_KEYS");
        Environment.SetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY", null);
        Environment.SetEnvironmentVariable("PROCIONE_MGR_PREVIOUS_MASTER_KEYS", null);
        try
        {
            var values = new Dictionary<string, string?> { ["Security:MasterKey"] = masterKey };
            for (var i = 0; i < previousKeys.Length; i++)
            {
                values[$"Security:PreviousMasterKeys:{i}"] = previousKeys[i];
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            return new AesGcmEncryptionService(config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY", savedCurrent);
            Environment.SetEnvironmentVariable("PROCIONE_MGR_PREVIOUS_MASTER_KEYS", savedPrevious);
        }
    }

    [Fact]
    public void OldPayload_DecryptsViaRing()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var oldPayload = Build(keyA).Encrypt("api-secret-di-prova");

        var ring = Build(keyB, keyA);
        Assert.True(ring.HasPreviousKeys);
        Assert.Equal("api-secret-di-prova", ring.Decrypt(oldPayload));
    }

    [Fact]
    public void Encrypt_AlwaysUsesCurrentKey()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var ring = Build(keyB, keyA);

        var fresh = ring.Encrypt("nuovo-segreto");

        // Un servizio con la SOLA chiave corrente (nessun ring) deve rileggere il nuovo payload:
        // se la cifratura usasse una chiave del ring, la rotazione non convergerebbe mai.
        var currentOnly = Build(keyB);
        Assert.False(currentOnly.HasPreviousKeys);
        Assert.Equal("nuovo-segreto", currentOnly.Decrypt(fresh));
    }

    [Fact]
    public void IsEncryptedWithCurrentKey_ClassifiesOldAndNew()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var oldPayload = Build(keyA).Encrypt("segreto-vecchio");

        var ring = Build(keyB, keyA);
        var newPayload = ring.Encrypt("segreto-nuovo");

        Assert.False(ring.IsEncryptedWithCurrentKey(oldPayload)); // da ri-cifrare
        Assert.True(ring.IsEncryptedWithCurrentKey(newPayload));  // già a posto
    }

    [Fact]
    public void WithoutRing_OldPayload_StillThrows()
    {
        var oldPayload = Build(RandomKey()).Encrypt("segreto");
        var other = Build(RandomKey()); // chiave diversa, NESSUNA previous

        Assert.Throws<AuthenticationTagMismatchException>(() => other.Decrypt(oldPayload));
    }

    [Fact]
    public void TamperedPayload_ThrowsEvenWithFullRing()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var payload = Build(keyA).Encrypt("segreto-integro");

        // Si corrompe un byte del ciphertext (dopo versione+nonce+tag): il tag GCM deve
        // bocciarlo con TUTTE le chiavi del ring, mai restituire plaintext spazzatura.
        var raw = Convert.FromBase64String(payload);
        raw[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(raw);

        var ring = Build(keyB, keyA);
        Assert.Throws<AuthenticationTagMismatchException>(() => ring.Decrypt(tampered));
        Assert.False(ring.IsEncryptedWithCurrentKey(tampered));
    }

    [Fact]
    public void RingOrder_TriesAllPreviousKeys()
    {
        var keyA = RandomKey();
        var keyB = RandomKey();
        var keyC = RandomKey();
        var payloadA = Build(keyA).Encrypt("della-piu-vecchia");

        // keyA è l'ULTIMA del ring: il fallback deve arrivarci, non fermarsi alla prima.
        var ring = Build(keyC, keyB, keyA);
        Assert.Equal("della-piu-vecchia", ring.Decrypt(payloadA));
    }

    [Fact]
    public void DuplicateOfCurrentKey_InPrevious_IsIgnored()
    {
        var key = RandomKey();
        // La corrente duplicata fra le previous non deve né rompere né contare come "rotazione in corso"...
        // ma HasPreviousKeys guida la UI: con il ring che contiene SOLO la corrente non c'è nulla da ri-cifrare.
        var ring = Build(key, key);
        Assert.False(ring.HasPreviousKeys);
        Assert.Equal("x", ring.Decrypt(ring.Encrypt("x")));
    }

    [Fact]
    public void V1Format_Unchanged_NoMigrationNeeded()
    {
        var key = RandomKey();
        var payload = Build(key).Encrypt("compatibilita");
        var raw = Convert.FromBase64String(payload);
        Assert.Equal(1, raw[0]); // SchemeVersion resta 1: i payload esistenti non cambiano forma.
        Assert.Equal("compatibilita", Build(key, RandomKey()).Decrypt(payload));
    }
}
