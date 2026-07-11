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

/// <summary>
/// Stato della master key, separato da <see cref="IEncryptionService"/> perché i consumer del
/// guard (startup di produzione, gate Live del TradingEngine) non devono poter cifrare nulla —
/// solo sapere se la chiave in uso è ancora il PLACEHOLDER committato nel template. Con quella
/// chiave (pubblica su git) i segreti "cifrati" sono di fatto in chiaro per chiunque legga il repo.
/// </summary>
public interface IMasterKeyStatus
{
    /// <summary>True se la master key configurata è il placeholder di sviluppo committato nel template.</summary>
    bool IsDefaultDevKey { get; }
}
