using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ProcioneMGR.Services.Security;

/// <summary>
/// ValueConverter EF Core che cifra una stringa quando viene scritta sul DB e la
/// decifra quando viene letta. EF NON invoca il converter per i valori null,
/// quindi le proprieta' nullable (es. Passphrase) sono gestite automaticamente.
/// </summary>
public sealed class EncryptedStringConverter : ValueConverter<string, string>
{
    public EncryptedStringConverter(IEncryptionService encryption)
        : base(
            plaintext => encryption.Encrypt(plaintext),
            ciphertext => encryption.Decrypt(ciphertext))
    {
    }
}
