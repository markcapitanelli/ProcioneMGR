using System.Reflection;
using System.Text.Json;

namespace Procione;

/// <summary>Uno dei tre piani su cui vive la piattaforma, e la revisione da cui sta girando.</summary>
/// <param name="Nome">Come si chiama nel quadro: «guscio», «plancia», «motore».</param>
/// <param name="Sha">Lo sha della revisione VIVA, o <c>null</c> se non e' stato possibile leggerlo.</param>
/// <param name="Fonte">Da dove viene il numero: si stampa, perche' tre piani hanno tre sorgenti diverse.</param>
/// <param name="Perche">Quando <paramref name="Sha"/> e' null: perche' non si sa. Mai lasciarlo implicito.</param>
internal sealed record Piano(string Nome, string? Sha, string Fonte, string? Perche = null);

/// <summary>
/// [K1, PRD autonomia-piena 2026-08-31] <b>Chi sta girando da quale revisione.</b>
///
/// <para>Il difetto che questa classe esiste per rendere visibile: il motore si sincronizza da solo
/// ogni 30′ (lavoro <c>deploy</c>), il guscio e la plancia no. La notte del 2026-08-30 il guscio era
/// indietro di <b>7 commit</b> e la plancia di <b>13</b>, e la plancia stantia si e' appesa sullo
/// stesso pipe ereditato dell'incidente del 28/08 — con dentro il binario il fix che lo impediva,
/// mergiato due giorni prima. <b>Nessuna superficie, ne' UI ne' plancia ne' /health, diceva quanto
/// fossero indietro</b>, e non lo avrebbe detto nemmeno se lo scarto fosse cresciuto a cinquanta.</para>
///
/// <para>Le tre sorgenti sono diverse per necessita': il guscio dichiara la propria revisione su
/// <c>/health</c> (la sola che descriva il processo VIVO — il binario su disco dice cosa e' stato
/// compilato per ultimo, che non e' la stessa cosa); la plancia legge il proprio attributo di
/// assembly; il motore la porta nel tag dell'immagine (<c>local-&lt;sha8&gt;</c>), perche' gira in un
/// pod e non c'e' altro modo di chiederglielo senza toccare il contratto gRPC.</para>
///
/// <para><b>Il confronto e' col contenuto, non col conteggio.</b> Il lavoro <c>deploy</c> committa
/// da solo il pin dell'immagine: se il metro fosse «quanti commit mancano», ogni deploy riuscito
/// lascerebbe la riga a «1 indietro» per sempre — un allarme che non puo' rientrare smette di essere
/// letto e si porta dietro anche quelli veri (e' la lezione gia' pagata con
/// <c>LiquidationsMinStartUtc</c>). Quindi si usa lo STESSO cancello di
/// <c>deploy-trading.ps1</c>: master differisce nel contenuto, escluso il file del pin?</para>
/// </summary>
internal static class Revisions
{
    /// <summary>Il file che l'automazione del deploy scrive da sola: non e' codice, e' un marcatore.</summary>
    public const string FilePin = "infra/k8s/trading/kustomization.yaml";

    /// <summary>La revisione di QUESTO eseguibile (la plancia), dal timbro che il SDK .NET mette da se'.</summary>
    public static string? Propria { get; } = DaInformationalVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Estrae lo sha da un <c>AssemblyInformationalVersion</c> (<c>1.0.0+&lt;sha&gt;</c>). Gemella
    /// di <c>ProcioneMGR.Services.Health.BuildRevision.Extract</c>: sono due processi diversi e non
    /// condividono codice, ma devono rispondere allo stesso modo — i test lo pretendono da entrambe.
    /// </summary>
    public static string? DaInformationalVersion(string? versione) => SoloSha(Dopo(versione, '+'));

    /// <summary>
    /// Estrae lo sha dal tag di un'immagine: <c>ghcr.io/…/procionemgr-trading:local-850290e5</c> ⇒
    /// <c>850290e5</c>. Un tag che non e' nella forma <c>local-&lt;sha&gt;</c> (per esempio
    /// <c>latest</c>, o un digest) restituisce null: un'immagine che non dichiara la propria origine
    /// e' esattamente il caso da segnalare, non da far passare per allineata.
    /// </summary>
    public static string? DaTagImmagine(string? immagine)
    {
        var tag = Dopo(immagine, ':');
        if (tag is null) return null;
        return tag.StartsWith("local-", StringComparison.OrdinalIgnoreCase) ? SoloSha(tag[6..]) : null;
    }

    /// <summary>
    /// Estrae la revisione dal corpo di <c>/health</c> del guscio. Un guscio piu' vecchio di K1 non
    /// porta il campo: si restituisce null e la riga lo dira' — e' esso stesso il sintomo di un
    /// guscio indietro.
    /// </summary>
    public static string? DaCorpoHealth(string? corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("revision", out var r)) return null;
            return r.ValueKind == JsonValueKind.String ? SoloSha(r.GetString()) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Tutto cio' che segue il primo separatore, oppure null.</summary>
    private static string? Dopo(string? testo, char separatore)
    {
        if (string.IsNullOrWhiteSpace(testo)) return null;
        var i = testo.IndexOf(separatore);
        return i < 0 || i == testo.Length - 1 ? null : testo[(i + 1)..].Trim();
    }

    /// <summary>Accetta solo cio' che PUO' essere uno sha: esadecimale, da 7 a 40 cifre.</summary>
    private static string? SoloSha(string? candidato)
    {
        if (string.IsNullOrWhiteSpace(candidato)) return null;
        if (candidato.Length is < 7 or > 40) return null;
        foreach (var c in candidato)
        {
            var esadecimale = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!esadecimale) return null;
        }
        return candidato.ToLowerInvariant();
    }
}
