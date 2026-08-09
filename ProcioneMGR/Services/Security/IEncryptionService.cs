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

/// <summary>
/// Vista sul KEYRING della rotazione (Fase 0 PRD-RISANAMENTO, 2026-08-08). Separata da
/// <see cref="IEncryptionService"/> per lo stesso principio di <see cref="IMasterKeyStatus"/>:
/// chi orchestra la rotazione (la pagina /settings/exchanges, il MasterKeyRotationService)
/// deve poter CLASSIFICARE i payload — non gli serve cifrare in proprio.
/// </summary>
public interface IMasterKeyRing
{
    /// <summary>True se sono configurate chiavi PRECEDENTI (una rotazione è in corso).</summary>
    bool HasPreviousKeys { get; }

    /// <summary>
    /// True se il payload si apre con la chiave CORRENTE (nessun bisogno di ri-cifratura).
    /// False sia per i payload sulla chiave precedente sia per quelli indecifrabili o corrotti:
    /// la distinzione fra i due casi la fa il chiamante provando <see cref="IEncryptionService.Decrypt"/>.
    /// </summary>
    bool IsEncryptedWithCurrentKey(string ciphertext);
}
