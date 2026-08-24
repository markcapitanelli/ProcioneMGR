namespace Procione;

/// <summary>
/// Lettura e scrittura dei due file che la plancia condivide fra i suoi processi: lo stato del
/// supervisore e le preferenze sui lavori.
///
/// Non e' burocrazia. Un supervisore residente scrive lo stato ogni dieci secondi; nello stesso
/// istante un `procione stato` lanciato da un'altra finestra lo legge, e la plancia interattiva lo
/// rilegge ogni dodici. Con <c>File.ReadAllText</c>/<c>File.WriteAllText</c> nudi i due si
/// incontrano prima o poi: chi legge prende un'IOException e — nei catch che ci sono in giro —
/// diventa «nessuno stato», cioe' «nessun supervisore attivo». Un guasto inventato una volta ogni
/// tanto e' peggio di nessun controllo, perche' insegna a non fidarsi del quadro.
/// </summary>
internal static class Files
{
    private const int Tentativi = 4;
    private const int PausaMs = 40;

    /// <summary>
    /// Legge un file che qualcun altro potrebbe stare scrivendo. <c>null</c> se non c'e' o se non
    /// si e' riusciti a leggerlo: sono due cose che il chiamante distingue dal contesto, e in
    /// entrambi i casi la risposta onesta e' «non lo so», non un valore inventato.
    /// </summary>
    public static string? ReadShared(string path)
    {
        for (var i = 0; i < Tentativi; i++)
        {
            try
            {
                // ReadWrite | Delete: si accetta che l'altro stia scrivendo o rinominando sopra.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(PausaMs); }
            catch (UnauthorizedAccessException) { Thread.Sleep(PausaMs); }
        }
        return null;
    }

    /// <summary>
    /// Scrive in due tempi: prima un file accanto, poi lo sposta sopra al posto giusto. Chi legge
    /// non deve mai poter incontrare un file a meta' — un JSON troncato si legge come «stato
    /// assente», che qui significa «nessuno sta vegliando».
    /// </summary>
    public static bool WriteAtomic(string path, string contenuto)
    {
        var tmp = path + ".tmp";
        try
        {
            var cartella = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(cartella)) Directory.CreateDirectory(cartella);
            File.WriteAllText(tmp, contenuto);
        }
        catch { return false; }

        // Lo spostamento puo' fallire se in quel millisecondo qualcuno tiene aperto il file di
        // destinazione: si riprova, invece di perdere la scrittura.
        for (var i = 0; i < Tentativi; i++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (IOException) { Thread.Sleep(PausaMs); }
            catch (UnauthorizedAccessException) { Thread.Sleep(PausaMs); }
            catch { break; }
        }
        try { File.Delete(tmp); } catch { }
        return false;
    }
}
