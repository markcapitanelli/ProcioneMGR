using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Ml;

/// <summary>
/// <see cref="IEncryptionService"/> che lancia sempre eccezione. Serve solo a soddisfare la
/// dipendenza del costruttore di <c>ApplicationDbContext</c> (l'EncryptedStringConverter è
/// applicato alle colonne credenziali degli exchange, che il path di inferenza ML non tocca MAI —
/// legge solo SavedMlModels, sola lettura).
///
/// Deliberatamente NON un passthrough silenzioso (<c>Encrypt(x) => x</c>): questo è un servizio
/// long-running con un endpoint gRPC. Se in futuro qualcuno aggiungesse per errore una query su
/// ExchangeCredentials in questo processo, un passthrough scriverebbe/leggerebbe credenziali IN
/// CHIARO su colonne che il resto del sistema tratta come cifrate — fallimento silenzioso. Lanciare
/// trasforma quello scenario in un crash immediato. Conseguenza: a questo host non va distribuita
/// NESSUNA master key.
/// </summary>
internal sealed class NoOpEncryptionService : IEncryptionService
{
    private const string Message =
        "ProcioneMGR.Ml non gestisce segreti cifrati: il path di inferenza ML legge solo SavedMlModels. " +
        "Se questa eccezione appare, un componente sta tentando di cifrare/decifrare in un servizio " +
        "che non deve farlo.";

    public string Encrypt(string plaintext) => throw new NotSupportedException(Message);
    public string Decrypt(string ciphertext) => throw new NotSupportedException(Message);
}
