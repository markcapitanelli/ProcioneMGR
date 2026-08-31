using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Health;

/// <summary>Un battito, come lo vede chi lo guarda: chi è, da quanto tace, e se è troppo.</summary>
/// <param name="Atteso">
/// Vero se questo ruolo DEVE battere in questo assetto. Un ruolo atteso e assente è un guasto; uno
/// non atteso e assente è solo una topologia diversa, e confonderli produce un rosso che non
/// rientra — la classe di allarme che questo progetto ha già imparato a non scrivere.
/// </param>
public sealed record BattitoRiga(string Ruolo, DateTime? UltimoUtc, string? Versione, bool Atteso, bool Muto);

/// <summary>Il quadro dei battiti, con la sua soglia dichiarata accanto.</summary>
public sealed record BattitoBoard(
    IReadOnlyList<BattitoRiga> Righe,
    TimeSpan Soglia,
    string? RevisioneGuscio,
    string? Errore);

/// <summary>
/// [K7+K8 — superficie UI, 2026-08-31] <b>I battiti, dove li guarda il proprietario.</b>
///
/// <para>Nasce da un audit della copertura UI della Fase 0: le manopole nuove avevano tutte il loro
/// pannello, ma <b>quello che i battiti dicono non si vedeva da nessuna parte in app</b> — solo in
/// <c>procione stato</c>, cioè in una console che si apre quando si sospetta già qualcosa. Ed è
/// esattamente il dato che il 2026-08-31 è passato da una riga a quattro: prima
/// <c>HostHeartbeats</c> conteneva solo <c>ingestion-sync</c>, e l'allarme «l'altro host è muto»
/// non poteva scattare.</para>
///
/// <para><b>Cosa NON fa, e perché.</b> Non confronta le revisioni dei tre piani con <c>master</c>:
/// quel confronto ha bisogno di git e di kubectl, che il guscio di proposito non ha — la plancia
/// deve poter dire «il guscio non compila» anche quando il guscio non compila, e per questo la
/// diagnosi dei piani vive lì. Qui si mostra ciò che il guscio SA per sé: chi batte, da quanto, e
/// con quale revisione si è dichiarato.</para>
///
/// <para>La soglia viaggia col verdetto perché un «muto da 12 minuti» significa cose diverse a
/// seconda di quanto si aspettava: mostrare il giudizio senza il metro costringe chi legge a
/// fidarsi.</para>
/// </summary>
public sealed class HeartbeatBoardProbe(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<HeartbeatBoardProbe> logger)
{
    /// <summary>
    /// I ruoli che ci si aspetta di vedere. <c>carry</c> è atteso solo col carry acceso, ma qui non
    /// si legge quella configurazione: quella del GUSCIO non comanda il carry (vive nel pod, e il
    /// ConfigMap che lo governa è un altro file). Dire «atteso» sulla base della configurazione
    /// sbagliata sarebbe peggio che non dirlo: si segna come atteso solo ciò che è atteso SEMPRE.
    /// </summary>
    private static readonly string[] SempreAttesi = [HostHeartbeat.ShellRole, HostHeartbeat.EngineRole];

    /// <summary>Oltre questa età un battito è muto. Dieci minuti: lo stesso di HeartbeatOptions.</summary>
    public static readonly TimeSpan Soglia = TimeSpan.FromMinutes(10);

    public async Task<BattitoBoard> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var righe = await db.HostHeartbeats.AsNoTracking()
                .Select(h => new { h.Host, h.LastUtc, h.Version })
                .ToListAsync(ct);

            var ora = DateTime.UtcNow;
            var viste = righe
                .Select(r => new BattitoRiga(r.Host, r.LastUtc, r.Version,
                    Atteso: SempreAttesi.Contains(r.Host),
                    Muto: ora - r.LastUtc > Soglia))
                .ToList();

            // Un ruolo atteso che NON HA RIGA non compare da solo: e la sua assenza è precisamente
            // il guasto più grave dei due — «non ha mai battuto» contro «ha smesso». Senza questo
            // ciclo il quadro sarebbe verde per sottrazione, che è la forma pura del controllo che
            // rassicura.
            foreach (var atteso in SempreAttesi.Where(a => !viste.Any(v => v.Ruolo == a)))
            {
                viste.Add(new BattitoRiga(atteso, null, null, Atteso: true, Muto: true));
            }

            return new BattitoBoard(
                viste.OrderByDescending(v => v.Atteso).ThenBy(v => v.Ruolo).ToList(),
                Soglia, BuildRevision.Sha, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fail-open DICHIARATO: è diagnostica, e una lettura fallita non deve togliere la Home.
            // Ma non si mostra un quadro vuoto come se fosse un quadro sano.
            logger.LogWarning(ex, "Quadro dei battiti: lettura fallita.");
            return new BattitoBoard([], Soglia, BuildRevision.Sha,
                $"battiti non leggibili ({ex.GetType().Name})");
        }
    }
}
