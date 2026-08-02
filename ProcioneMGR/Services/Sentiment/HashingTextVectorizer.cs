using System.Text;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Vettorizzatore testuale a feature hashing, PURO e DETERMINISTICO: minuscole → token
/// alfanumerici (≥2 caratteri) → unigrammi + bigrammi → hash FNV-1a a 32 bit modulo la
/// dimensione → conteggi, normalizzati L2.
///
/// <para>È il punto architetturale del pilota ONNX (PRD-ONNX-SENTIMENT-PILOT): la parte testuale
/// resta codice C# CONDIVISO fra addestramento e inferenza — il modello ONNX riceve solo il
/// vettore numerico. Questo elimina per costruzione il rischio di parità del tokenizer (un
/// tokenizer subword sbagliato produce punteggi plausibili ma errati, peggio di un crash) e il
/// rischio di copertura degli operatori nell'export ML.NET→ONNX (le trasformazioni testuali di
/// ML.NET non sono esportabili; un ingresso già-vettoriale sì).</para>
///
/// <para>MAI usare <c>string.GetHashCode()</c> qui: è randomizzato per processo, e il modello
/// addestrato in un processo darebbe risposte diverse in un altro. FNV-1a è fisso per sempre.</para>
/// </summary>
public static class HashingTextVectorizer
{
    /// <summary>Dimensione del vettore (2^15): abbastanza larga da tenere basse le collisioni su un vocabolario di notizie, abbastanza piccola da addestrare in secondi.</summary>
    public const int Dimension = 32768;

    /// <summary>Vettorizza titolo+sommario in un vettore denso L2-normalizzato di dimensione <see cref="Dimension"/>.</summary>
    public static float[] Vectorize(string title, string? summary)
    {
        var tokens = Tokenize($"{title} {summary}");
        var vector = new float[Dimension];

        for (var i = 0; i < tokens.Count; i++)
        {
            vector[IndexOf(tokens[i])] += 1f;
            if (i + 1 < tokens.Count)
            {
                vector[IndexOf(tokens[i] + "_" + tokens[i + 1])] += 1f;
            }
        }

        // Normalizzazione L2: le notizie lunghe non devono pesare più di quelle corte solo
        // perché hanno più parole.
        double sumSq = 0;
        foreach (var v in vector) sumSq += v * v;
        if (sumSq > 0)
        {
            var norm = (float)Math.Sqrt(sumSq);
            for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        }
        return vector;
    }

    /// <summary>Token alfanumerici in minuscolo, lunghezza ≥ 2 (i singoli caratteri sono rumore).</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                if (current.Length >= 2) tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length >= 2) tokens.Add(current.ToString());
        return tokens;
    }

    private static int IndexOf(string token) => (int)(Fnv1a(token) % Dimension);

    /// <summary>FNV-1a a 32 bit sul token UTF-8: stabile fra processi, piattaforme e versioni.</summary>
    internal static uint Fnv1a(string token)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
