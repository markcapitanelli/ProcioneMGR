namespace ProcioneMGR.Services.Security;

/// <summary>
/// Cifratura simmetrica autenticata per i segreti a riposo (API key / secret /
/// passphrase degli exchange). L'implementazione usa AES-256-GCM.
/// </summary>
public interface IEncryptionService
{
    /// <summary>Cifra un testo in chiaro e restituisce una stringa portabile (base64, con nonce e tag inclusi).</summary>
    string Encrypt(string plaintext);

    /// <summary>Decifra una stringa prodotta da <see cref="Encrypt"/>. Lancia se il testo e' manomesso.</summary>
    string Decrypt(string ciphertext);
}
