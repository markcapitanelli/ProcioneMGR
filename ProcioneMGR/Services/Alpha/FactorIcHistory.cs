using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

// =============================================================================================
//  [D2, persistenza] La storia dell'IC su tabella, decisa dal proprietario il 2026-07-28.
//
//  PERCHÉ CAMBIA IDEA RISPETTO AL PRIMO GIRO. La prima versione non persisteva nulla, con
//  l'argomento che l'IC storico è una funzione deterministica delle candele e quindi salvarlo
//  sarebbe una cache. L'argomento è vero e resta vero — ma è incompleto, per due ragioni che si
//  vedono soltanto guardando come la piattaforma vive davvero:
//
//  1. IL GUSCIO SI RIAVVIA DI CONTINUO. È il senso stesso di "core caldo / guscio freddo": il
//     motore sta acceso, l'app di ricerca la si spegne e riaccende a piacere. Una fotografia
//     tenuta solo in memoria muore a ogni riavvio, e l'alert in Home — che esiste per farsi
//     trovare senza doverci pensare — resta vuoto proprio nei minuti in cui uno guarda la Home.
//  2. LE CANDELE NON SONO ETERNE. "Deterministica dalle candele" vale finché le candele ci sono:
//     un giorno la finestra di storico più fine verrà ruotata o ristretta, e la storia dell'IC
//     calcolata quando i dati c'erano non sarà più ricostruibile. Allora sì che è
//     un'osservazione.
//
//  COSA SI PERSISTE, E COSA NO. Solo le FINESTRE (una riga per finestra, per fattore, per serie).
//  Il verdetto NON si persiste: è una funzione pura della serie più la soglia, e si ricostruisce
//  con lo stesso Judge che gira sul calcolo fresco — così non esistono due strade che possono
//  divergere. Salvare anche il verdetto significherebbe tenere allineato uno stato derivato.
// =============================================================================================

/// <summary>
/// ENTITÀ EF (tabella <c>FactorIcWindows</c>): l'IC di UN fattore su UNA finestra di UNA serie.
/// La riga è l'osservazione elementare della deriva: la serie storica è l'insieme delle righe
/// ordinate per <see cref="WindowEndUtc"/>.
///
/// L'indice unico su (serie, fattore, orizzonte, ampiezza, fine finestra) rende la scrittura
/// IDEMPOTENTE: il worker gira ogni 12 ore sulle stesse candele e ricalcola le stesse finestre —
/// senza quel vincolo la tabella crescerebbe di un duplicato per giro, per sempre.
/// </summary>
public class FactorIcWindow
{
    public int Id { get; set; }

    /// <summary>Serie di appartenenza (es. "BTC/USDT").</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Timeframe della serie (es. "1h").</summary>
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Nome del fattore, come lo espone <c>IAlphaFactor.Name</c>.</summary>
    public string FactorName { get; set; } = string.Empty;

    /// <summary>Orizzonte del rendimento forward su cui l'IC è stato misurato (in barre).</summary>
    public int ForwardHorizon { get; set; }

    /// <summary>
    /// Ampiezza della finestra in osservazioni. Fa parte della chiave logica perché un IC su 500
    /// osservazioni e uno su 2000 sono misure DIVERSE: il pavimento di rumore è 1,96/√n, quindi
    /// mescolarle nella stessa serie confronterebbe numeri con soglie diverse.
    /// </summary>
    public int WindowSize { get; set; }

    public DateTime WindowStartUtc { get; set; }

    public DateTime WindowEndUtc { get; set; }

    /// <summary>IC di Spearman sulla finestra.</summary>
    public double InformationCoefficient { get; set; }

    /// <summary>Quando questa riga è stata calcolata (UTC). Serve a distinguere una storia viva da una ferma.</summary>
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>
/// Legge e scrive la storia dell'IC. Interfaccia separata dal worker perché la UI la legge senza
/// dipendere da un BackgroundService.
/// </summary>
public interface IFactorIcHistoryStore
{
    /// <summary>
    /// Scrive (upsert) le finestre dei report passati. Ritorna quante righe sono state INSERITE
    /// come nuove — le finestre già note vengono aggiornate, non duplicate.
    /// </summary>
    Task<int> SaveAsync(
        string symbol, string timeframe, int forwardHorizon,
        IReadOnlyList<FactorDriftReport> reports, DateTime computedAtUtc, CancellationToken ct = default);

    /// <summary>
    /// Serie storica registrata per un fattore, in ordine cronologico (vuota se non c'è).
    /// L'orizzonte forward fa parte della domanda: l'IC a 1 barra e quello a 5 barre sono misure
    /// diverse e mescolarle darebbe una spezzata senza significato.
    /// </summary>
    Task<IReadOnlyList<FactorIcPoint>> LoadSeriesAsync(
        string symbol, string timeframe, string factorName, int forwardHorizon = 1, CancellationToken ct = default);

    /// <summary>
    /// Ricostruisce dalle righe persistite tutte le fotografie note, una per serie, col verdetto
    /// ricalcolato dalla serie. È ciò che permette all'alert in Home di esserci già al primo
    /// caricamento dopo un riavvio del guscio.
    /// </summary>
    Task<IReadOnlyList<FactorDriftSeriesSnapshot>> LoadSnapshotsAsync(
        FactorDriftConfig? config, CancellationToken ct = default);

    /// <summary>La fotografia registrata di UNA serie (null se il job non l'ha mai calcolata).</summary>
    Task<FactorDriftSeriesSnapshot?> LoadSnapshotAsync(
        string symbol, string timeframe, FactorDriftConfig? config, CancellationToken ct = default);

    /// <summary>
    /// Quando ogni serie è stata calcolata l'ultima volta, per chiave <c>"SIMBOLO|TF"</c>. È ciò che
    /// permette al job di girare **a rotazione** sulle serie più vecchie invece di macinare sempre le
    /// stesse prime N: senza, con una watchlist da centinaia di serie il monitor guarderebbe per
    /// sempre lo stesso 2%.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTime>> LoadLastComputedAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IFactorIcHistoryStore"/>
public sealed class FactorIcHistoryStore(IDbContextFactory<ApplicationDbContext> dbFactory) : IFactorIcHistoryStore
{
    public async Task<int> SaveAsync(
        string symbol, string timeframe, int forwardHorizon,
        IReadOnlyList<FactorDriftReport> reports, DateTime computedAtUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Le righe già note per QUESTA serie e questi fattori, in un colpo solo: un round-trip per
        // finestra trasformerebbe un giro del worker in centinaia di query.
        var factorNames = reports.Select(r => r.FeatureName).Distinct(StringComparer.Ordinal).ToList();
        var existing = await db.FactorIcWindows
            .Where(w => w.Symbol == symbol && w.Timeframe == timeframe && factorNames.Contains(w.FactorName))
            .ToListAsync(ct);

        var byKey = existing.ToDictionary(
            w => (w.FactorName, w.ForwardHorizon, w.WindowSize, w.WindowEndUtc));

        var inserted = 0;
        foreach (var report in reports)
        {
            foreach (var point in report.Series)
            {
                var key = (report.FeatureName, forwardHorizon, point.Observations, point.WindowEndUtc);
                if (byKey.TryGetValue(key, out var row))
                {
                    // Stessa finestra ricalcolata: il valore non dovrebbe cambiare (funzione
                    // deterministica delle candele), ma se le candele sono state corrette a
                    // posteriori l'ultimo calcolo è quello buono.
                    row.InformationCoefficient = point.InformationCoefficient;
                    row.ComputedAtUtc = computedAtUtc;
                    continue;
                }

                var fresh = new FactorIcWindow
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    FactorName = report.FeatureName,
                    ForwardHorizon = forwardHorizon,
                    WindowSize = point.Observations,
                    WindowStartUtc = point.WindowStartUtc,
                    WindowEndUtc = point.WindowEndUtc,
                    InformationCoefficient = point.InformationCoefficient,
                    ComputedAtUtc = computedAtUtc,
                };
                db.FactorIcWindows.Add(fresh);
                byKey[key] = fresh; // due report sullo stesso fattore nello stesso giro non si duplicano
                inserted++;
            }
        }

        await db.SaveChangesAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<FactorIcPoint>> LoadSeriesAsync(
        string symbol, string timeframe, string factorName, int forwardHorizon = 1, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.FactorIcWindows.AsNoTracking()
            .Where(w => w.Symbol == symbol && w.Timeframe == timeframe && w.FactorName == factorName
                        && w.ForwardHorizon == forwardHorizon)
            .OrderBy(w => w.WindowEndUtc)
            .ToListAsync(ct);

        return SelectDominantGrid(rows);
    }

    public async Task<IReadOnlyList<FactorDriftSeriesSnapshot>> LoadSnapshotsAsync(
        FactorDriftConfig? config, CancellationToken ct = default)
    {
        var horizon = Math.Max(1, (config ?? new FactorDriftConfig()).ForwardHorizon);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.FactorIcWindows.AsNoTracking()
            .Where(w => w.ForwardHorizon == horizon)
            .ToListAsync(ct);
        return BuildSnapshots(rows, config);
    }

    public async Task<FactorDriftSeriesSnapshot?> LoadSnapshotAsync(
        string symbol, string timeframe, FactorDriftConfig? config, CancellationToken ct = default)
    {
        var horizon = Math.Max(1, (config ?? new FactorDriftConfig()).ForwardHorizon);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.FactorIcWindows.AsNoTracking()
            .Where(w => w.Symbol == symbol && w.Timeframe == timeframe && w.ForwardHorizon == horizon)
            .ToListAsync(ct);
        return BuildSnapshots(rows, config).FirstOrDefault();
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> LoadLastComputedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.FactorIcWindows.AsNoTracking()
            .GroupBy(w => new { w.Symbol, w.Timeframe })
            .Select(g => new { g.Key.Symbol, g.Key.Timeframe, Last = g.Max(w => w.ComputedAtUtc) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => $"{r.Symbol}|{r.Timeframe}", r => r.Last, StringComparer.Ordinal);
    }

    private static IReadOnlyList<FactorDriftSeriesSnapshot> BuildSnapshots(
        IReadOnlyList<FactorIcWindow> rows, FactorDriftConfig? config)
    {
        config ??= new FactorDriftConfig();
        if (rows.Count == 0) return [];

        var snapshots = new List<FactorDriftSeriesSnapshot>();
        foreach (var series in rows.GroupBy(w => (w.Symbol, w.Timeframe)))
        {
            var reports = new List<FactorDriftReport>();
            foreach (var factor in series.GroupBy(w => w.FactorName))
            {
                var points = SelectDominantGrid(factor.OrderBy(w => w.WindowEndUtc).ToList());
                if (points.Count == 0) continue;
                reports.Add(FactorDriftAnalyzer.JudgeSeries(factor.Key, factor.Key, points, config));
            }

            if (reports.Count == 0) continue;

            var computedAt = series.Max(w => w.ComputedAtUtc);
            snapshots.Add(new FactorDriftSeriesSnapshot(
                series.Key.Symbol, series.Key.Timeframe, computedAt,
                reports
                    .OrderByDescending(r => (int)r.Status)
                    .ThenByDescending(r => Math.Abs(r.ReferenceIc - r.RecentIc))
                    .ToList()));
        }

        return snapshots;
    }

    /// <summary>
    /// Ricostruisce una serie di finestre **NON SOVRAPPOSTE**, la più recente per prima e poi a
    /// ritroso, scartando ciò che si accavalla.
    ///
    /// DUE RAGIONI, e la seconda l'ha trovata la pagina viva, non un test.
    ///
    /// 1. L'ampiezza si adatta ai dati disponibili, quindi crescendo lo storico può cambiare: finestre
    ///    di ampiezza diversa hanno pavimenti di rumore diversi (1,96/√n) e mescolarle confronterebbe
    ///    numeri con soglie diverse. Si tiene la sola ampiezza più recente.
    ///
    /// 2. **La griglia SI SPOSTA fra un giro e l'altro.** Il job carica le ULTIME <c>MaxCandles</c>
    ///    candele: quando ne arrivano di nuove, l'inizio della fetta scivola in avanti e i confini
    ///    delle finestre cadono altrove. Le righe dei due giri hanno la stessa ampiezza ma offset
    ///    diverso, quindi in tabella si accumulano finestre **che si sovrappongono**. Visto dal vivo
    ///    su ETH/USDT 1h: «18 × 2000» finestre là dove in 20.000 candele ce ne stanno 9. Punti che
    ///    condividono dati sono correlati per costruzione e fanno sembrare la serie più stabile di
    ///    quanto è — esattamente ciò che <c>FactorDriftAnalyzer</c> evita usando finestre non
    ///    sovrapposte, aggirato dalla persistenza a sua insaputa.
    ///
    /// La catena si costruisce dalla finestra più recente all'indietro (la parte che interessa di più
    /// è il presente) tenendo solo chi non tocca la precedente.
    /// </summary>
    private static IReadOnlyList<FactorIcPoint> SelectDominantGrid(IReadOnlyList<FactorIcWindow> rows)
    {
        if (rows.Count == 0) return [];

        var sameWidth = rows
            .GroupBy(w => w.WindowSize)
            .OrderByDescending(g => g.Max(w => w.WindowEndUtc))
            .ThenByDescending(g => g.Key)
            .First();

        // Catena all'indietro dalla finestra più recente: si tiene una finestra solo se finisce prima
        // (o esattamente all'inizio) di quella già tenuta. Greedy dal presente = si conserva sempre
        // l'ultima misura, che è quella su cui si giudica il "recente".
        var chain = new List<FactorIcWindow>();
        foreach (var w in sameWidth.OrderByDescending(w => w.WindowEndUtc))
        {
            if (chain.Count == 0 || w.WindowEndUtc <= chain[^1].WindowStartUtc) chain.Add(w);
        }

        return chain
            .OrderBy(w => w.WindowEndUtc)
            .Select(w => new FactorIcPoint(w.WindowStartUtc, w.WindowEndUtc, w.InformationCoefficient, w.WindowSize))
            .ToList();
    }
}
