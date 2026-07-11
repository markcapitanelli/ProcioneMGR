using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace ProcioneMGR.Services.Security;

/// <summary>
/// Implementazione AES-256-GCM di <see cref="IEncryptionService"/>.
///
/// Formato di output (poi codificato base64):
///   [1 byte versione][12 byte nonce][16 byte tag GCM][N byte ciphertext]
/// Il nonce e' casuale per ogni cifratura (mai riusato con la stessa chiave),
/// requisito di sicurezza fondamentale per GCM.
///
/// La chiave master a 256 bit e' derivata dal valore di configurazione
/// "Security:MasterKey":
///   - se il valore e' base64 di esattamente 32 byte, viene usato direttamente;
///   - altrimenti viene derivata via SHA-256 della stringa UTF-8.
///
/// TODO(produzione): NON tenere la master key in appsettings.json. Spostarla in
/// User Secrets (dev) e in Azure Key Vault / variabile d'ambiente protetta
/// (PROCIONE_MGR_MASTER_KEY) in produzione, idealmente con rotazione via il byte
/// di versione gia' previsto nel formato.
/// </summary>
public sealed class AesGcmEncryptionService : IEncryptionService, IMasterKeyStatus
{
    private const byte SchemeVersion = 1;
    private const int NonceSize = 12;   // 96 bit, raccomandato per GCM
    private const int TagSize = 16;     // 128 bit
    private const int KeySize = 32;     // 256 bit

    /// <summary>
    /// SHA-256 (hex) del placeholder "__CHANGE_ME_BASE64_32_BYTES__" committato in
    /// appsettings.json.example: si confronta l'HASH e non il plaintext, così questo file non
    /// contiene una seconda copia letterale della stringa da cercare/sostituire. Se la chiave
    /// configurata è ancora il placeholder, i segreti "cifrati" sono decifrabili da chiunque
    /// legga il repository: Production e Live devono rifiutarsi di partire (fail-fast).
    /// </summary>
    private const string DevPlaceholderKeySha256Hex =
        "8FDD03E694F0A2690B962293A4DAA9F46276D8BC0DC58830C706BF92A2B7E686";

    private readonly byte[] _key;

    /// <inheritdoc />
    public bool IsDefaultDevKey { get; }

    public AesGcmEncryptionService(IConfiguration configuration)
    {
        // Priorita': variabile d'ambiente PROCIONE_MGR_MASTER_KEY (prod), poi appsettings (dev).
        var configured = Environment.GetEnvironmentVariable("PROCIONE_MGR_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = configuration["Security:MasterKey"];
        }
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Master key mancante. Imposta 'Security:MasterKey' in appsettings.json " +
                "(dev) o nella variabile d'ambiente PROCIONE_MGR_MASTER_KEY.");
        }

        IsDefaultDevKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configured)))
            .Equals(DevPlaceholderKeySha256Hex, StringComparison.OrdinalIgnoreCase);
        _key = DeriveKey(configured);
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // [versione][nonce][tag][ciphertext]
        var output = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
        output[0] = SchemeVersion;
        Buffer.BlockCopy(nonce, 0, output, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, output, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, output, 1 + NonceSize + TagSize, cipherBytes.Length);

        return Convert.ToBase64String(output);
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertext);

        var input = Convert.FromBase64String(ciphertext);
        if (input.Length < 1 + NonceSize + TagSize)
        {
            throw new CryptographicException("Payload cifrato troppo corto o corrotto.");
        }

        if (input[0] != SchemeVersion)
        {
            throw new CryptographicException($"Versione schema di cifratura non supportata: {input[0]}.");
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLength = input.Length - 1 - NonceSize - TagSize;
        var cipherBytes = new byte[cipherLength];

        Buffer.BlockCopy(input, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(input, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(input, 1 + NonceSize + TagSize, cipherBytes, 0, cipherLength);

        var plainBytes = new byte[cipherLength];
        using var aes = new AesGcm(_key, TagSize);
        // Lancia AuthenticationTagMismatchException se il dato e' stato manomesso.
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static byte[] DeriveKey(string configured)
    {
        // Caso 1: la master key e' gia' una chiave a 256 bit codificata base64.
        if (TryDecodeBase64Key(configured, out var raw))
        {
            return raw;
        }

        // Caso 2: e' una passphrase arbitraria -> derivazione deterministica a 32 byte.
        return SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    }

    private static bool TryDecodeBase64Key(string value, out byte[] key)
    {
        key = [];
        Span<byte> buffer = stackalloc byte[KeySize];
        if (Convert.TryFromBase64String(value, buffer, out var written) && written == KeySize)
        {
            key = buffer.ToArray();
            return true;
        }
        return false;
    }
}
