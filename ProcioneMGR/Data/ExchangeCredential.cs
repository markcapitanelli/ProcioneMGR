using System.ComponentModel.DataAnnotations;

namespace ProcioneMGR.Data;

/// <summary>Exchange supportati. Valori espliciti per stabilita' della serializzazione.</summary>
public enum ExchangeName
{
    Binance = 1,
    Bitget = 2,
}

/// <summary>
/// Credenziali API di un exchange, appartenenti a un singolo utente.
///
/// SICUREZZA: <see cref="ApiKey"/>, <see cref="ApiSecret"/> e <see cref="Passphrase"/>
/// sono cifrati a riposo via EncryptedStringConverter (AES-256-GCM) configurato
/// nel <see cref="ApplicationDbContext"/>. Sul DB non compaiono mai in chiaro.
/// </summary>
public class ExchangeCredential
{
    public int Id { get; set; }

    /// <summary>FK verso AspNetUsers (IdentityUser).</summary>
    [Required]
    public string UserId { get; set; } = string.Empty;

    public ApplicationUser? User { get; set; }

    public ExchangeName ExchangeName { get; set; }

    /// <summary>Etichetta leggibile scelta dall'utente, es. "Binance Main".</summary>
    [Required]
    [MaxLength(64)]
    public string Label { get; set; } = string.Empty;

    /// <summary>Cifrata a riposo.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Cifrato a riposo.</summary>
    [Required]
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Cifrata a riposo. Obbligatoria per Bitget, nulla/assente altrove.</summary>
    public string? Passphrase { get; set; }

    public bool IsTestnet { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Regola di dominio: Bitget richiede la passphrase. Restituisce l'eventuale
    /// messaggio d'errore (null = valido). Usata sia dalla UI sia dal layer exchange.
    /// </summary>
    public string? ValidateBusinessRules()
    {
        if (ExchangeName == ExchangeName.Bitget && string.IsNullOrWhiteSpace(Passphrase))
        {
            return "La passphrase e' obbligatoria per le credenziali Bitget.";
        }
        return null;
    }

    /// <summary>ApiKey mascherata per la UI (mai esporre il secret).</summary>
    public string MaskedApiKey => Mask(ApiKey);

    private static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value.Length <= 8)
        {
            return new string('*', value.Length);
        }
        return $"{value[..4]}{new string('*', 6)}{value[^4..]}";
    }
}
