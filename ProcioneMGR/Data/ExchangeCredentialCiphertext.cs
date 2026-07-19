namespace ProcioneMGR.Data;

/// <summary>
/// Proiezione KEYLESS di sola lettura sulla tabella <c>ExchangeCredentials</c> che espone il
/// CIPHERTEXT così com'è sul DB (nessun EncryptedStringConverter). Serve ai percorsi che devono
/// sopravvivere a una riga cifrata con una master key diversa da quella del processo corrente
/// (bug B2, docs/TEST-UI-2026-07-18.md): col converter la decifratura avviene DENTRO la
/// materializzazione EF, quindi una sola riga indecifrabile (AuthenticationTagMismatchException)
/// abbatteva l'intera query — e con essa la pagina /settings/exchanges o l'avvio Testnet/Live.
/// Qui il ciphertext arriva intatto e la decifratura è per-riga, in memoria: vedi
/// <see cref="ProcioneMGR.Services.Security.ExchangeCredentialReader"/>.
///
/// Mappata con <c>ToView</c> sulla tabella esistente: nessuna tabella nuova, nessuna migrazione
/// (le entità ToView sono escluse dal DDL di EnsureCreated/Migrations).
/// </summary>
public class ExchangeCredentialCiphertext
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ExchangeName ExchangeName { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Base64 del payload AES-GCM, NON decifrato.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base64 del payload AES-GCM, NON decifrato.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Base64 del payload AES-GCM, NON decifrato. Null dove non usata (Binance).</summary>
    public string? Passphrase { get; set; }

    public bool IsTestnet { get; set; }

    public DateTime CreatedAt { get; set; }
}
