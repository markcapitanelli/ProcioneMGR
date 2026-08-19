using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.PairsTrading;

// =============================================================================================
//  [I14c] La storia dello SPREAD di una coppia, a finestre mobili.
//
//  PERCHÉ SI PERSISTE. Lo stesso argomento di FactorIcWindows, con la stessa risposta: il calcolo
//  è deterministico dalle candele, quindi salvarlo sarebbe una cache — finché le candele ci sono.
//  Il guscio si riavvia di continuo (è il senso di «core caldo / guscio freddo») e le finestre di
//  storico verranno ruotate: una relazione misurata quando i dati c'erano, e non più ricostruibile,
//  è un'OSSERVAZIONE, non una cache.
//
//  COSA SI PERSISTE, E COSA NO. Solo le FINESTRE — un fatto per finestra. Il VERDETTO non si
//  persiste: è una funzione pura della serie di finestre più le soglie, e si ricalcola con lo
//  stesso giudice ovunque serva. Salvarlo significherebbe tenere allineato uno stato derivato, e
//  avere due strade che possono divergere.
//
//  È L'UNICA COSA DI QUESTA ONDATA CON CARICO DI SCRITTURA PERMANENTE sul Postgres condiviso con
//  motore e ingestion. Per questo: worker SPENTO di fabbrica, solo le coppie che l'operatore
//  elenca, e il carico dichiarato in numeri nel doc del worker.
// =============================================================================================

/// <summary>
/// ENTITÀ EF (tabella <c>PairSpreadWindows</c>): il comportamento dello spread di UNA coppia su UNA
/// finestra. La riga è l'osservazione elementare; la serie storica è l'insieme delle righe ordinate
/// per <see cref="WindowEndUtc"/>.
///
/// <para>L'indice unico su (coppia, estimatore, ampiezza, fine finestra) rende la scrittura
/// IDEMPOTENTE: il worker ricalcola le stesse finestre a ogni giro, e senza quel vincolo la tabella
/// crescerebbe di un duplicato per giro, per sempre.</para>
/// </summary>
public class PairSpreadWindow
{
    public int Id { get; set; }

    /// <summary>Identità della coppia secondo <see cref="PairKey.Build"/> — non orientata.</summary>
    public string PairKeyValue { get; set; } = string.Empty;

    /// <summary>Il verso della regressione: <see cref="HedgeRatio"/> è l'elasticità di Y rispetto a X.</summary>
    public string SymbolY { get; set; } = string.Empty;
    public string SymbolX { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>
    /// "Kalman" | "RollingOls". <b>In chiave</b>: due estimatori danno due spread diversi per
    /// costruzione (l'uno adatta β a ogni barra, l'altro a intervalli), quindi mescolarli nella
    /// stessa serie confronterebbe misure che non sono la stessa misura.
    /// </summary>
    public string Estimator { get; set; } = string.Empty;

    /// <summary>
    /// Ampiezza della finestra in osservazioni. <b>In chiave</b> per la stessa ragione di
    /// <c>FactorIcWindow.WindowSize</c>: la potenza del test ADF dipende da n, quindi un verdetto su
    /// 250 osservazioni e uno su 1000 hanno soglie di rumore diverse e non stanno sulla stessa serie.
    /// </summary>
    public int WindowSize { get; set; }

    public DateTime WindowStartUtc { get; set; }
    public DateTime WindowEndUtc { get; set; }

    /// <summary>Statistica ADF sullo spread della finestra (più negativa = più stazionaria).</summary>
    public double AdfStatistic { get; set; }

    /// <summary>Il valore critico MacKinnon al 5% usato per questa finestra — dipende da n, quindi si salva.</summary>
    public double CriticalValue { get; set; }

    /// <summary>
    /// Lo spread di QUESTA finestra è stazionario? È un fatto per-finestra, <b>mai un verdetto</b>:
    /// al 5% su rumore puro il 5% delle finestre risulta stazionario per costruzione. Il verdetto
    /// vive in <see cref="PairSpreadJudge"/> e guarda la serie intera.
    /// </summary>
    public bool IsStationaryWindow { get; set; }

    /// <summary>Elasticità β media sulla finestra (log-spread: adimensionale).</summary>
    public double HedgeRatio { get; set; }

    /// <summary>Media e deviazione standard del log-spread sulla finestra: la scala su cui si legge lo z.</summary>
    public double SpreadMean { get; set; }
    public double SpreadStdDev { get; set; }

    /// <summary>Z-score causale all'ULTIMA barra della finestra: dove stava lo spread quando la finestra si è chiusa.</summary>
    public double LastZScore { get; set; }

    /// <summary>Quando questa riga è stata calcolata (UTC): distingue una storia viva da una ferma.</summary>
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>Un punto della serie storica, come lo legge chi la disegna.</summary>
public sealed record PairSpreadPoint(
    DateTime WindowStartUtc, DateTime WindowEndUtc, int Observations,
    double AdfStatistic, double CriticalValue, bool IsStationary,
    double HedgeRatio, double LastZScore);

/// <summary>
/// [I14c] <b>Il verdetto su una coppia — e perché non può essere per finestra.</b>
///
/// <para>Un test ADF al 5% dichiara stazionario il 5% delle finestre di puro rumore: <b>per
/// costruzione</b>, non per difetto. Un monitor che esponesse il verdetto della singola finestra
/// direbbe quindi «cointegrata» su due random walk indipendenti circa una volta su venti — e il
/// gate di verifica dell'item («su due random walk non deve MAI dichiarare cointegrazione») sarebbe
/// <b>insoddisfacibile alla lettera</b>. È la classe «gate senza strumento», e ci si accorgerebbe
/// dopo aver scritto tutto.</para>
///
/// <para><b>La risposta: il verdetto è una proprietà della SERIE, non della finestra.</b> Si guarda
/// la frazione di finestre <i>non sovrapposte</i> stazionarie contro una soglia alta. Sotto il nullo
/// quella frazione vale ~0,05 e non arriva mai a 0,6 su un numero decente di finestre; su una
/// relazione vera vale ~1. La distanza fra le due è enorme, ed è ciò che rende il gate verificabile
/// invece che aspirazionale.</para>
///
/// <para><b>E la ROTTURA si definisce come perdita di uno stato precedente.</b> Una coppia è
/// «rotta» se era persistentemente stazionaria e non lo è più. Sotto il nullo la persistenza non c'è
/// mai stata, quindi nessuna rottura è dichiarabile — <b>per costruzione, non per fortuna</b>: è la
/// forma che rende vera la seconda metà del gate.</para>
///
/// Puro e statico: si prova senza database, senza orologio e senza servizi.
/// </summary>
public static class PairSpreadJudge
{
    /// <summary>
    /// Finestre minime perché una frazione significhi qualcosa. Con tre finestre e p=0,05 sotto il
    /// nullo, «due su tre stazionarie» ha probabilità ~0,7% — bassa ma non trascurabile su molte
    /// coppie; con cinque scende a ~0,1%. È il pavimento che impedisce a una coincidenza di
    /// diventare un verdetto.
    /// </summary>
    public const int MinWindows = 5;

    public sealed record Verdict(
        int Windows, int StationaryWindows, double StationaryFraction,
        bool IsPersistentlyStationary, bool IsBroken, string Text)
    {
        /// <summary>Vero quando le finestre non bastano per esprimersi: né stazionaria, né rotta, e lo si dice.</summary>
        public bool NotEnoughHistory => Windows < MinWindows;
    }

    /// <summary>
    /// Il verdetto su una serie di finestre <b>già deduplicate</b> (vedi
    /// <see cref="PairSpreadHistoryStore.SelectDominantGrid"/>), in ordine cronologico.
    /// </summary>
    /// <param name="persistenceThreshold">Frazione di finestre stazionarie sopra cui si parla di relazione persistente.</param>
    /// <param name="recentWindows">Quante finestre finali contano come «adesso» per il giudizio di rottura.</param>
    public static Verdict Judge(
        IReadOnlyList<PairSpreadPoint> series,
        double persistenceThreshold = 0.6,
        int recentWindows = 3)
    {
        ArgumentNullException.ThrowIfNull(series);

        var n = series.Count;
        if (n == 0)
        {
            return new Verdict(0, 0, 0, false, false,
                "nessuna finestra registrata: la coppia non è mai stata sorvegliata");
        }

        var stationary = series.Count(p => p.IsStationary);
        var fraction = (double)stationary / n;

        if (n < MinWindows)
        {
            return new Verdict(n, stationary, fraction, false, false,
                $"{n} finestre su {MinWindows} minime: troppo poca storia per un giudizio "
                + $"({stationary} stazionarie, ma con così poche finestre una coincidenza basta a fabbricare una frazione alta)");
        }

        var persistent = fraction >= persistenceThreshold;

        // La ROTTURA richiede uno stato precedente da perdere: si confronta il passato con le ultime
        // finestre. Senza il "prima", non c'è nulla da rompere — ed è esattamente il caso del rumore.
        var recent = Math.Clamp(recentWindows, 1, n - 1);
        var passate = series.Take(n - recent).ToList();
        var recenti = series.Skip(n - recent).ToList();
        var eraPersistente = passate.Count >= MinWindows
                             && (double)passate.Count(p => p.IsStationary) / passate.Count >= persistenceThreshold;
        var oraNo = !recenti.Any(p => p.IsStationary);
        var rotta = eraPersistente && oraNo;

        var testo = rotta
            ? $"RELAZIONE ROTTA: era stazionaria sul {(double)passate.Count(p => p.IsStationary) / passate.Count:P0} "
              + $"delle {passate.Count} finestre precedenti, nelle ultime {recenti.Count} non lo è mai stata"
            : persistent
                ? $"relazione persistente: stazionaria su {stationary} finestre di {n} ({fraction:P0}), soglia {persistenceThreshold:P0}"
                : $"nessuna relazione persistente: stazionaria su {stationary} finestre di {n} ({fraction:P0}), "
                  + $"sotto la soglia {persistenceThreshold:P0} — su rumore puro ci si aspetta circa il 5%";

        return new Verdict(n, stationary, fraction, persistent, rotta, testo);
    }
}

/// <summary>
/// [I14c] Legge e scrive la storia dello spread. Interfaccia separata dal worker perché la UI la
/// legge senza dipendere da un <c>BackgroundService</c> — stessa scelta di <c>IFactorIcHistoryStore</c>.
/// </summary>
public interface IPairSpreadHistoryStore
{
    /// <summary>Upsert idempotente delle finestre di una coppia. Ritorna quante righe sono NUOVE.</summary>
    Task<int> SaveAsync(IReadOnlyList<PairSpreadWindow> windows, CancellationToken ct = default);

    /// <summary>
    /// La serie storica di una coppia, deduplicata dalle finestre sovrapposte e in ordine
    /// cronologico. Vuota se la coppia non è mai stata sorvegliata.
    /// </summary>
    Task<IReadOnlyList<PairSpreadPoint>> LoadSeriesAsync(string pairKey, string estimator, CancellationToken ct = default);

    /// <summary>Le coppie che hanno una storia registrata, con quando è stata aggiornata l'ultima volta.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> LoadLastComputedAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IPairSpreadHistoryStore"/>
public sealed class PairSpreadHistoryStore(IDbContextFactory<ApplicationDbContext> dbFactory) : IPairSpreadHistoryStore
{
    public async Task<int> SaveAsync(IReadOnlyList<PairSpreadWindow> windows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Le righe già note per QUESTE coppie in un colpo solo: un round-trip per finestra
        // trasformerebbe un giro del worker in centinaia di query sul Postgres condiviso.
        var chiavi = windows.Select(w => w.PairKeyValue).Distinct(StringComparer.Ordinal).ToList();
        var esistenti = await db.PairSpreadWindows
            .Where(w => chiavi.Contains(w.PairKeyValue))
            .ToListAsync(ct);

        var byKey = esistenti.ToDictionary(w => (w.PairKeyValue, w.Estimator, w.WindowSize, w.WindowEndUtc));

        var nuove = 0;
        foreach (var w in windows)
        {
            var key = (w.PairKeyValue, w.Estimator, w.WindowSize, w.WindowEndUtc);
            if (byKey.TryGetValue(key, out var riga))
            {
                // Stessa finestra ricalcolata: il valore non dovrebbe cambiare (funzione
                // deterministica delle candele), ma se le candele sono state corrette a posteriori
                // l'ultimo calcolo è quello buono.
                riga.AdfStatistic = w.AdfStatistic;
                riga.CriticalValue = w.CriticalValue;
                riga.IsStationaryWindow = w.IsStationaryWindow;
                riga.HedgeRatio = w.HedgeRatio;
                riga.SpreadMean = w.SpreadMean;
                riga.SpreadStdDev = w.SpreadStdDev;
                riga.LastZScore = w.LastZScore;
                riga.ComputedAtUtc = w.ComputedAtUtc;
                continue;
            }

            db.PairSpreadWindows.Add(w);
            byKey[key] = w;   // due finestre identiche nello stesso giro non si duplicano
            nuove++;
        }

        await db.SaveChangesAsync(ct);
        return nuove;
    }

    public async Task<IReadOnlyList<PairSpreadPoint>> LoadSeriesAsync(
        string pairKey, string estimator, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PairSpreadWindows.AsNoTracking()
            .Where(w => w.PairKeyValue == pairKey && w.Estimator == estimator)
            .OrderBy(w => w.WindowEndUtc)
            .ToListAsync(ct);

        return SelectDominantGrid(rows);
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> LoadLastComputedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PairSpreadWindows.AsNoTracking()
            .GroupBy(w => w.PairKeyValue)
            .Select(g => new { Chiave = g.Key, Ultimo = g.Max(w => w.ComputedAtUtc) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Chiave, r => r.Ultimo, StringComparer.Ordinal);
    }

    /// <summary>
    /// [I14c] <b>Le finestre sovrapposte si tolgono in LETTURA</b>, come per la storia dell'IC.
    ///
    /// <para>Il worker carica le ultime N candele e taglia le finestre a partire dalla più recente:
    /// al giro dopo sono arrivate altre candele, la griglia scivola, e in tabella si accumulano
    /// finestre che si sovrappongono. Punti che condividono dati sono <b>correlati per
    /// costruzione</b> e fanno sembrare la relazione più stabile di quanto è — cioè gonfiano proprio
    /// la frazione su cui si esprime il verdetto. Visto dal vivo sulla storia dell'IC: «18 × 2000»
    /// finestre là dove in 20.000 candele ce ne stanno 9.</para>
    ///
    /// <para>La catena si costruisce dalla finestra più recente <b>all'indietro</b> — la parte che
    /// interessa di più è il presente, e il giudizio di rottura guarda le ultime finestre — tenendo
    /// solo chi non tocca la precedente.</para>
    /// </summary>
    internal static IReadOnlyList<PairSpreadPoint> SelectDominantGrid(IReadOnlyList<PairSpreadWindow> rows)
    {
        if (rows.Count == 0) return [];

        // Una sola ampiezza: due ampiezze diverse hanno soglie di rumore diverse e non stanno sulla
        // stessa serie. Vince quella che arriva più avanti nel tempo, a parità la più larga.
        var stessaAmpiezza = rows
            .GroupBy(w => w.WindowSize)
            .OrderByDescending(g => g.Max(w => w.WindowEndUtc))
            .ThenByDescending(g => g.Key)
            .First();

        var catena = new List<PairSpreadWindow>();
        foreach (var w in stessaAmpiezza.OrderByDescending(w => w.WindowEndUtc))
        {
            if (catena.Count == 0 || w.WindowEndUtc <= catena[^1].WindowStartUtc) catena.Add(w);
        }

        return catena
            .OrderBy(w => w.WindowEndUtc)
            .Select(w => new PairSpreadPoint(
                w.WindowStartUtc, w.WindowEndUtc, w.WindowSize,
                w.AdfStatistic, w.CriticalValue, w.IsStationaryWindow,
                w.HedgeRatio, w.LastZScore))
            .ToList();
    }
}
