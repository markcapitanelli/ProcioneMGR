using System.Reflection;

namespace ProcioneMGR.Services.Health;

/// <summary>
/// [K1, PRD autonomia-piena 2026-08-31] <b>La revisione con cui QUESTO processo è stato compilato.</b>
///
/// <para>Nasce da un difetto misurato il 2026-08-30: il guscio girava da un binario compilato alle
/// 12:21, la correzione della sonda della ricerca era stata mergiata alle 15:53, e per due giorni la
/// Home ha detto «un run è in corso adesso» con zero run in corso — il difetto che quel commit
/// correggeva. Il pod del motore si sincronizza da solo ogni 30′; il guscio e la plancia no, e
/// <b>nessuna superficie diceva quanto fossero indietro</b>. Lo scarto misurato quella notte era di
/// 7 commit per il guscio e 13 per la plancia.</para>
///
/// <para><b>Il dato c'è già e non costa nulla:</b> il SDK .NET timbra da sé
/// <c>AssemblyInformationalVersion</c> nella forma <c>1.0.0+&lt;sha completo&gt;</c>. Non serve
/// SourceLink, non serve un file generato al build, non serve una proprietà nel csproj: serve
/// leggere un attributo che esiste già. Il canale che sarebbe stato naturale — la colonna
/// <c>HostHeartbeats.Version</c> — è invece occupata da una stringa di stato («ciclo ok · intervallo
/// 5m») e il worker che la scrive è spento: riusarla produrrebbe un confronto fra "1.0.0.0" e uno
/// sha, cioè un controllo che rassicura sempre.</para>
///
/// <para>L'estrazione è una funzione <b>pura</b> (<see cref="Extract"/>) perché è l'unica parte che
/// si può provare: <c>Assembly.GetEntryAssembly()</c> sotto il runner dei test è l'assembly del
/// runner, non questo.</para>
/// </summary>
public static class BuildRevision
{
    /// <summary>Lo sha completo della revisione compilata, o <c>null</c> se il timbro non c'è.</summary>
    public static string? Sha { get; } = Extract(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Le prime 8 cifre, la forma con cui si scrivono i tag delle immagini (<c>local-&lt;sha8&gt;</c>).</summary>
    public static string? Short => Sha is { Length: >= 8 } s ? s[..8] : Sha;

    /// <summary>
    /// Estrae lo sha da un <c>AssemblyInformationalVersion</c>. Restituisce <c>null</c> — mai una
    /// stringa inventata — quando il timbro manca o non è uno sha: chi legge deve poter distinguere
    /// «non misurato» da un valore, altrimenti il confronto a valle direbbe sempre «allineato».
    /// </summary>
    internal static string? Extract(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;

        // Il metadato di build è tutto ciò che segue il PRIMO '+', per SemVer 2.0. Un secondo '+'
        // non è ammesso, ma se arrivasse si terrebbe comunque il primo segmento.
        var i = informationalVersion.IndexOf('+');
        if (i < 0 || i == informationalVersion.Length - 1) return null;

        var candidato = informationalVersion[(i + 1)..].Trim();

        // Solo cifre esadecimali, e lunghezza da sha abbreviato in su: un metadato di build può
        // contenere qualunque cosa (un numero di CI, un nome di ramo), e trattarla come revisione
        // produrrebbe un confronto con git che fallisce ogni volta senza spiegare perché.
        if (candidato.Length is < 7 or > 40) return null;
        foreach (var c in candidato)
        {
            var esadecimale = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!esadecimale) return null;
        }

        return candidato.ToLowerInvariant();
    }
}
