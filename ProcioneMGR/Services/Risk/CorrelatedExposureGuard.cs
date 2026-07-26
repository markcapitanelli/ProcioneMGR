using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Risk;

/// <summary>
/// Esito della valutazione di esposizione correlata per una candidata apertura.
/// </summary>
/// <param name="IsMeasurable">
/// False quando manca il necessario per una misura onesta (storico insufficiente, capitale non
/// determinabile). In quel caso il guard NON blocca: vedi la nota sul fail-safe in
/// <see cref="CorrelatedExposureGuard"/>.
/// </param>
/// <param name="CorrelatedNotional">
/// Esposizione correlata NETTA e con segno (positiva = sbilanciata al rialzo). È la somma del
/// nozionale candidato più quello delle posizioni già aperte, ciascuna pesata per la sua
/// correlazione col simbolo candidato.
/// </param>
public sealed record CorrelatedExposureAssessment(
    bool IsMeasurable,
    decimal CorrelatedNotional,
    decimal LimitNotional,
    decimal AggregateCapital,
    IReadOnlyList<CorrelatedExposureContribution> Contributions,
    string? UnmeasurableReason)
{
    /// <summary>True se questa apertura porterebbe l'esposizione correlata oltre il limite.</summary>
    public bool Exceeds => IsMeasurable && Math.Abs(CorrelatedNotional) > LimitNotional;

    public static CorrelatedExposureAssessment NotMeasurable(string reason) =>
        new(false, 0m, 0m, 0m, [], reason);
}

/// <summary>Contributo di una singola posizione aperta all'esposizione correlata.</summary>
public sealed record CorrelatedExposureContribution(int LaneId, string Symbol, double Correlation, decimal SignedNotional)
{
    /// <summary>Quota di questa posizione che "vale come" esposizione sul simbolo candidato.</summary>
    public decimal WeightedNotional => SignedNotional * (decimal)Correlation;
}

/// <summary>Opzioni del limite di esposizione correlata. Default SPENTO.</summary>
public sealed class CorrelatedExposureOptions
{
    /// <summary>
    /// Interruttore generale. Default FALSE: la funzione va prima calibrata sui dati delle corsie
    /// realmente attive, perché una soglia scelta male non protegge — paralizza.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Tetto dell'esposizione correlata netta, in % del capitale aggregato delle corsie nella
    /// stessa modalità. Il default coincide col tetto di esposizione totale per singola corsia
    /// (<see cref="SafetyConfiguration.MaxTotalExposurePercent"/>): un insieme di posizioni che si
    /// muovono all'unisono non deve poter superare il limite che varrebbe se fossero una sola.
    /// </summary>
    public decimal MaxCorrelatedExposurePercent { get; set; } = 50m;

    /// <summary>
    /// Sotto questa correlazione (in valore assoluto) una posizione è trattata come indipendente e
    /// non contribuisce. Serve a non accumulare rumore da decine di correlazioni spurie piccole.
    /// </summary>
    public double MinCorrelationToCount { get; set; } = 0.5d;

    /// <summary>Timeframe delle barre su cui si stima la correlazione.</summary>
    public string Timeframe { get; set; } = "1h";

    /// <summary>Numero di barre della finestra di stima (720 barre 1h ≈ 30 giorni).</summary>
    public int LookbackBars { get; set; } = 720;

    /// <summary>Barre in comune minime perché una correlazione sia considerata stimabile.</summary>
    public int MinOverlappingBars { get; set; } = 100;

    /// <summary>Validità della correlazione in cache: oltre, si ricalcola.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>Valuta l'esposizione correlata di una candidata apertura. Vedi <see cref="CorrelatedExposureGuard"/>.</summary>
public interface ICorrelatedExposureGuard
{
    Task<CorrelatedExposureAssessment> AssessAsync(
        int laneId, string candidateSymbol, OrderSide side, decimal candidateNotional,
        TradingMode mode, CancellationToken ct = default);
}

/// <summary>
/// [Fase 2 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Limite di esposizione su posizioni CORRELATE.
///
/// Tutti i limiti di rischio a runtime erano scalari e ciechi alla correlazione: tetto sulla singola
/// posizione, tetto sull'esposizione totale di una corsia, numero massimo di posizioni aperte. Tre
/// corsie che aprono long su tre altcoin ad alta correlazione con BTC contavano quindi come tre
/// scommesse indipendenti mentre erano, in sostanza, una sola scommessa di taglia tripla — ed è
/// esattamente nei crash, quando le correlazioni crypto tendono a 1, che quella distinzione conta.
/// La matematica per accorgersene esisteva già in piattaforma (<see cref="Correlation.Pearson"/>),
/// ma viveva solo nella ricerca: mai nel percorso decisionale.
///
/// <b>Somma con segno, non in valore assoluto.</b> Due long correlati sommano rischio, un long e uno
/// short correlati lo compensano: pesare per ρ mantenendo il segno del nozionale è ciò che rende la
/// misura un'esposizione e non un semplice conteggio. Una copertura genuina non viene punita.
///
/// <b>Fail-safe verso il permesso, non verso il blocco.</b> Se la correlazione non è stimabile
/// (storico corto, simbolo nuovo) il guard dichiara la misura non disponibile e lascia passare,
/// registrandolo. È l'opposto della scelta fatta sul capitale ≤ 0 nel <see cref="SafetyChecker"/>,
/// e di proposito: lì il dato mancante rende ogni limite percentuale indecidibile, qui rende
/// indecidibile UN limite aggiuntivo mentre tutti gli altri restano in piedi. Bloccare al buio
/// fermerebbe l'operatività per un buco di dati, che è un guasto peggiore del rischio che si evita.
/// </summary>
public sealed class CorrelatedExposureGuard(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<CorrelatedExposureOptions> options,
    ILogger<CorrelatedExposureGuard> logger) : ICorrelatedExposureGuard
{
    private readonly ConcurrentDictionary<(string A, string B), (double Rho, DateTime ComputedAtUtc)> _cache = new();

    public async Task<CorrelatedExposureAssessment> AssessAsync(
        int laneId, string candidateSymbol, OrderSide side, decimal candidateNotional,
        TradingMode mode, CancellationToken ct = default)
    {
        var cfg = options.CurrentValue;
        if (!cfg.Enabled) return CorrelatedExposureAssessment.NotMeasurable("limite disattivato");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Solo le posizioni della STESSA modalità: una posizione Paper non è un'esposizione reale
        // e non deve poter bloccare un'apertura Testnet (né viceversa). È lo stesso discriminatore
        // anti-mescolamento già usato al caricamento delle corsie.
        var positions = await db.OpenPositions.AsNoTracking()
            .Where(p => p.OpenedInMode == mode)
            .ToListAsync(ct);

        var capital = await db.TradingEngineStates.AsNoTracking()
            .Where(s => s.Mode == mode)
            .SumAsync(s => (decimal?)s.TotalCapital, ct) ?? 0m;

        if (capital <= 0m)
        {
            return CorrelatedExposureAssessment.NotMeasurable("capitale aggregato non determinabile");
        }

        var candidateSigned = side == OrderSide.Buy ? candidateNotional : -candidateNotional;
        var contributions = new List<CorrelatedExposureContribution>();
        var unmeasurable = 0;

        foreach (var pos in positions)
        {
            // La posizione sullo STESSO simbolo è correlata a sé stessa per definizione: nessuna
            // stima da fare, ρ = 1. Passare dallo stimatore qui sarebbe solo un modo di sbagliare.
            double rho;
            if (string.Equals(pos.Symbol, candidateSymbol, StringComparison.OrdinalIgnoreCase))
            {
                rho = 1d;
            }
            else
            {
                var estimated = await CorrelationAsync(db, candidateSymbol, pos.Symbol, cfg, ct);
                if (estimated is null) { unmeasurable++; continue; }
                rho = estimated.Value;
                if (Math.Abs(rho) < cfg.MinCorrelationToCount) continue;
            }

            var notional = pos.Quantity * (pos.CurrentPrice > 0m ? pos.CurrentPrice : pos.EntryPrice);
            var signed = pos.Side == OrderSide.Buy ? notional : -notional;
            contributions.Add(new CorrelatedExposureContribution(pos.LaneId, pos.Symbol, rho, signed));
        }

        if (unmeasurable > 0)
        {
            logger.LogDebug(
                "Esposizione correlata corsia {Lane}: {Count} posizioni escluse dalla misura (storico insufficiente).",
                laneId, unmeasurable);
        }

        var correlated = candidateSigned + contributions.Sum(c => c.WeightedNotional);
        var limit = capital * cfg.MaxCorrelatedExposurePercent / 100m;

        return new CorrelatedExposureAssessment(true, correlated, limit, capital, contributions, null);
    }

    /// <summary>
    /// Correlazione di Pearson fra i rendimenti logaritmici dei due simboli, sulle barre in comune.
    /// Null = non stimabile (barre in comune sotto la soglia). La cache è simmetrica: ρ(A,B) = ρ(B,A).
    /// </summary>
    private async Task<double?> CorrelationAsync(
        ApplicationDbContext db, string symbolA, string symbolB, CorrelatedExposureOptions cfg, CancellationToken ct)
    {
        var key = string.CompareOrdinal(symbolA, symbolB) <= 0 ? (symbolA, symbolB) : (symbolB, symbolA);
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.ComputedAtUtc < cfg.CacheTtl)
        {
            return cached.Rho;
        }

        var a = await ClosesAsync(db, symbolA, cfg, ct);
        var b = await ClosesAsync(db, symbolB, cfg, ct);

        var (x, y) = AlignedLogReturns(a, b);
        if (x.Count < cfg.MinOverlappingBars) return null;

        var rho = Correlation.Pearson(x, y);
        _cache[key] = (rho, DateTime.UtcNow);
        return rho;
    }

    private static async Task<List<(DateTime T, decimal Close)>> ClosesAsync(
        ApplicationDbContext db, string symbol, CorrelatedExposureOptions cfg, CancellationToken ct) =>
        (await db.OhlcvData.AsNoTracking()
            .Where(o => o.Symbol == symbol && o.Timeframe == cfg.Timeframe)
            .OrderByDescending(o => o.TimestampUtc)
            .Take(cfg.LookbackBars)
            .Select(o => new { o.TimestampUtc, o.Close })
            .ToListAsync(ct))
        .Select(o => (o.TimestampUtc, o.Close))
        .OrderBy(o => o.TimestampUtc)
        .ToList();

    /// <summary>
    /// Rendimenti logaritmici sulle sole barre con lo STESSO timestamp. L'allineamento non è un
    /// dettaglio: due serie con buchi diversi, accostate per posizione, produrrebbero una
    /// correlazione fra istanti diversi — un numero che sembra una misura e non lo è.
    /// </summary>
    internal static (List<double> X, List<double> Y) AlignedLogReturns(
        IReadOnlyList<(DateTime T, decimal Close)> a, IReadOnlyList<(DateTime T, decimal Close)> b)
    {
        var byTimestamp = b.ToDictionary(p => p.T, p => p.Close);
        var pairs = new List<(decimal A, decimal B)>();
        foreach (var (t, closeA) in a)
        {
            if (byTimestamp.TryGetValue(t, out var closeB)) pairs.Add((closeA, closeB));
        }

        var x = new List<double>(Math.Max(0, pairs.Count - 1));
        var y = new List<double>(Math.Max(0, pairs.Count - 1));
        for (var i = 1; i < pairs.Count; i++)
        {
            var (prevA, prevB) = pairs[i - 1];
            var (curA, curB) = pairs[i];
            if (prevA <= 0m || prevB <= 0m || curA <= 0m || curB <= 0m) continue;
            x.Add(Math.Log((double)(curA / prevA)));
            y.Add(Math.Log((double)(curB / prevB)));
        }
        return (x, y);
    }
}
