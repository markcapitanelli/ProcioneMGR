using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [K47] La distribuzione della vita degli esperimenti, e — la parte che decide — <b>quanti
/// sarebbero arrivati ai cancelli in vigore</b>.
/// </summary>
/// <param name="Episodi">Esperimenti CHIUSI misurati. Sotto ~5 nessuna mediana è una distribuzione.</param>
/// <param name="MedianaGiorniOsservati">Mediana dell'osservazione accreditata, in giorni.</param>
/// <param name="MinGiorniOsservati">Il più corto: dice quanto in basso può andare, che è ciò che una soglia deve sopravvivere.</param>
/// <param name="MassimoGiorniOsservati">Il più lungo mai raggiunto: il tetto empirico di ogni cancello.</param>
/// <param name="MedianaGiorniCalendario">Mediana del tempo di parete, per confronto: la differenza col dato osservato è il duty.</param>
/// <param name="RaggiungonoCancelloInedia">Quanti episodi avrebbero superato <c>StarvationMinDays</c>.</param>
/// <param name="RaggiungonoCancelloSharpe">Quanti avrebbero superato le settimane del giudizio per Sharpe.</param>
public sealed record LaneIdentityLifetimeReport(
    int Episodi,
    double MedianaGiorniOsservati,
    double MinGiorniOsservati,
    double MassimoGiorniOsservati,
    double MedianaGiorniCalendario,
    int RaggiungonoCancelloInedia,
    int RaggiungonoCancelloSharpe)
{
    /// <summary>
    /// Vero quando il campione è troppo piccolo perché «mediana» significhi qualcosa. La soglia è
    /// dichiarata e bassa apposta: serve a impedire che il pannello mostri un numero autorevole
    /// costruito su due episodi, non a nascondere il dato.
    /// </summary>
    public bool CampioneTroppoPiccolo => Episodi < 5;

    /// <summary>
    /// <b>Il cancello dell'inedia è raggiungibile?</b> Falso quando nessun esperimento chiuso ci
    /// sarebbe arrivato: in quel caso la soglia non è severa, è <b>spenta</b> — e non lo si vede da
    /// nessuna altra parte, perché una regola che non si esprime mai è indistinguibile da una
    /// regola che non condanna nessuno.
    /// </summary>
    public bool CancelloInediaRaggiungibile => Episodi == 0 || RaggiungonoCancelloInedia > 0;

    /// <summary><inheritdoc cref="CancelloInediaRaggiungibile" path="/summary"/></summary>
    public bool CancelloSharpeRaggiungibile => Episodi == 0 || RaggiungonoCancelloSharpe > 0;
}

/// <summary>
/// [K47, 2026-09-02] Legge l'archivio degli episodi e risponde alla domanda che sette avversari
/// hanno chiesto: <b>quanto vive un'identità di corsia, e le soglie in vigore sono raggiungibili?</b>
///
/// <para>La seconda metà è quella che conta. Una soglia di ritiro più lunga della vita tipica di un
/// esperimento non è severa: è spenta, e finge di essere un meccanismo di governo — che in questo
/// progetto è il difetto ricorrente numero uno. Prima di K47 la sola risposta era una ricostruzione
/// a mano dal journal, su quattro episodi.</para>
/// </summary>
public interface ILaneIdentityLifetimeReader
{
    Task<LaneIdentityLifetimeReport> ReadAsync(CancellationToken ct = default);
}

public sealed class LaneIdentityLifetimeReader(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<FleetOptions> options) : ILaneIdentityLifetimeReader
{
    public async Task<LaneIdentityLifetimeReport> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var episodi = await db.FleetLaneIdentityEpisodes.AsNoTracking()
            .Select(e => new { e.ObservedSeconds, e.FirstSeenUtc, e.ClosedUtc })
            .ToListAsync(ct);

        var opt = options.CurrentValue;
        return Calcola(
            episodi.Select(e => (e.ObservedSeconds, (e.ClosedUtc - e.FirstSeenUtc).TotalDays)).ToList(),
            Math.Max(1, opt.StarvationMinDays),
            7 * Math.Max(1, opt.RetireMinWeeks));
    }

    /// <summary>Puro: si prova senza database, ed è dove vive la definizione di «mediana».</summary>
    internal static LaneIdentityLifetimeReport Calcola(
        IReadOnlyList<(long ObservedSeconds, double CalendarDays)> episodi,
        int giorniCancelloInedia,
        int giorniCancelloSharpe)
    {
        ArgumentNullException.ThrowIfNull(episodi);
        if (episodi.Count == 0) return new(0, 0, 0, 0, 0, 0, 0);

        var osservati = episodi.Select(e => e.ObservedSeconds / 86400.0).OrderBy(x => x).ToList();
        var calendario = episodi.Select(e => e.CalendarDays).OrderBy(x => x).ToList();

        return new LaneIdentityLifetimeReport(
            Episodi: episodi.Count,
            MedianaGiorniOsservati: Mediana(osservati),
            MinGiorniOsservati: osservati[0],
            MassimoGiorniOsservati: osservati[^1],
            MedianaGiorniCalendario: Mediana(calendario),
            RaggiungonoCancelloInedia: osservati.Count(g => g >= giorniCancelloInedia),
            RaggiungonoCancelloSharpe: osservati.Count(g => g >= giorniCancelloSharpe));
    }

    private static double Mediana(IReadOnlyList<double> ordinati) =>
        ordinati.Count % 2 == 1
            ? ordinati[ordinati.Count / 2]
            : (ordinati[ordinati.Count / 2 - 1] + ordinati[ordinati.Count / 2]) / 2.0;
}
