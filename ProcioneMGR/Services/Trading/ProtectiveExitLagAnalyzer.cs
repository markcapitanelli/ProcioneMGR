using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Trading.Internal;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// [B3] Misura del RITARDO delle uscite protettive: quanto tempo, e quanto prezzo, separa il
/// momento in cui il mercato tocca il livello di stop dal momento in cui il percorso a candele
/// se ne accorge — cioè la chiusura della barra di corsia.
///
/// Nasce da un difetto del gate B3. Il gate chiede «confronto tick-vs-candela nelle metriche», ma
/// in assetto osservativo (<c>DriveProtectiveExits=false</c>) i tick vengono scartati e
/// <c>procione.trading.protective_exits</c> si incrementa solo quando un'uscita SCATTA: la serie
/// <c>source=tick</c> non può esistere finché non si accende il drive. Il confronto che deve
/// autorizzare l'accensione richiedeva l'accensione.
///
/// Qui la domanda si chiude OFFLINE, senza toccare l'assetto: le candele a risoluzione fine (5m,
/// o 1m dove esistono) fanno da surrogato dei tick contro le barre di corsia. È un surrogato
/// CONSERVATIVO in tre modi dichiarati:
///  - il momento di scoperta sul percorso fine è la CHIUSURA della barra fine, non il tick esatto:
///    il vantaggio misurato è quindi un limite INFERIORE di quello vero;
///  - il fill sul percorso fine passa dallo stesso <see cref="ProtectiveExitEvaluator"/> del
///    motore, quindi eredita la stessa lettura pessimistica (peggiore fra livello e apertura);
///  - dove i due percorsi divergono sul motivo dell'uscita, la posizione è contata come
///    disaccordo e non come vittoria del feed.
///
/// Il riuso dell'evaluator del motore non è un dettaglio: due regole di uscita diverse
/// misurerebbero la differenza fra le due regole, non fra le due risoluzioni.
/// </summary>
public sealed class ProtectiveExitLagAnalyzer
{
    /// <summary>
    /// Passo di un timeframe, dalla tabella canonica <see cref="Timeframes.Supported"/> — non da
    /// una seconda copia locale, che potrebbe divergere. Deliberatamente SENZA ripiego su un valore
    /// di comodo: un passo sbagliato falserebbe in silenzio proprio la grandezza che si sta
    /// misurando, il tempo.
    /// </summary>
    public static TimeSpan Step(string timeframe) =>
        Timeframes.Supported.TryGetValue(timeframe, out var span)
            ? span
            : throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Timeframe non riconosciuto.");

    /// <summary>
    /// Simula posizioni sulle barre di corsia e le fa vivere DUE VOLTE: una col solo percorso a
    /// candela di corsia, una col percorso a risoluzione fine. Ogni posizione è aperta alla
    /// chiusura di una barra di corsia — non si sceglie quando entrare perché la domanda non
    /// riguarda l'edge della strategia ma la fisica dell'uscita: quanto tardi arriva.
    /// </summary>
    public ProtectiveExitLagReport Measure(
        IReadOnlyList<OhlcvData> laneCandles,
        IReadOnlyList<OhlcvData> fineCandles,
        ProtectiveExitLagRequest request)
    {
        ArgumentNullException.ThrowIfNull(laneCandles);
        ArgumentNullException.ThrowIfNull(fineCandles);
        ArgumentNullException.ThrowIfNull(request);

        if (request.StopLossPercent <= 0m)
        {
            throw new ArgumentException("Serve uno stop loss: senza livello non esiste uscita protettiva da ritardare.", nameof(request));
        }

        var laneStep = Step(request.LaneTimeframe);
        var fineStep = Step(request.FineTimeframe);
        if (fineStep > laneStep)
        {
            throw new ArgumentException(
                $"La risoluzione fine ({request.FineTimeframe}) è più GROSSA della corsia ({request.LaneTimeframe}): non può fare da surrogato dei tick.",
                nameof(request));
        }

        var lane = laneCandles.OrderBy(c => c.TimestampUtc).ToList();
        var fine = fineCandles.OrderBy(c => c.TimestampUtc).ToList();
        var sample = Math.Max(1, request.SampleEveryNBars);
        var hold = Math.Max(1, request.MaxHoldBars);

        var observations = new List<ProtectiveExitLagObservation>();

        for (var i = 0; i + hold < lane.Count; i += sample)
        {
            var entryBar = lane[i];
            if (entryBar.Close <= 0m) continue;

            var entryTime = entryBar.TimestampUtc + laneStep;   // il motore agisce a barra CHIUSA
            var horizonEnd = lane[i + hold].TimestampUtc + laneStep;

            var candle = WalkLane(lane, i + 1, i + hold, entryBar.Close, entryTime, laneStep, request);
            var fineWalk = WalkFine(fine, entryTime, horizonEnd, entryBar.Close, fineStep, request);

            observations.Add(Combine(entryTime, entryBar.Close, request.Side, candle, fineWalk));
        }

        return Summarize(observations, request);
    }

    // ------------------------------------------------------------------ i due cammini

    /// <summary>Esito di un cammino: quando l'uscita è stata SCOPERTA, a che prezzo si può uscire davvero.</summary>
    private readonly record struct Walk(
        bool Exited,
        DateTime DiscoveredAtUtc,
        string Reason,
        decimal BookedFill,
        decimal ObtainablePrice);

    /// <summary>
    /// Percorso a candele di corsia: quello di oggi. La scoperta avviene alla CHIUSURA della barra
    /// che contiene il tocco, e il prezzo realmente ottenibile in quel momento è la chiusura di
    /// quella barra — non il livello dello stop, che è ciò che il motore invece registra
    /// (<see cref="Walk.BookedFill"/>). La distanza fra i due è la parte di ritardo che oggi non
    /// compare da nessuna parte.
    /// </summary>
    private static Walk WalkLane(
        IReadOnlyList<OhlcvData> lane, int from, int to, decimal entryPrice, DateTime entryTime,
        TimeSpan laneStep, ProtectiveExitLagRequest request)
    {
        var pos = BuildPosition(entryPrice, entryTime, request);

        for (var j = from; j <= to && j < lane.Count; j++)
        {
            var bar = lane[j];
            var exit = ProtectiveExitEvaluator.EvaluateStopAndTarget(pos, bar.Open, bar.High, bar.Low);
            if (exit.ShouldClose)
            {
                return new Walk(true, bar.TimestampUtc + laneStep, exit.Reason, exit.FillPrice, bar.Close);
            }

            ProtectiveExitEvaluator.UpdateBestSinceEntry(pos, bar.High, bar.Low);
        }

        return default;
    }

    /// <summary>
    /// Percorso a risoluzione fine: il surrogato dei tick. Si parte dalla prima barra fine che
    /// APRE non prima dell'ingresso — una barra a cavallo dell'ingresso conterrebbe prezzi
    /// precedenti alla posizione, e farebbe scattare uscite su un passato che la posizione non ha
    /// vissuto.
    /// </summary>
    private static Walk WalkFine(
        IReadOnlyList<OhlcvData> fine, DateTime entryTime, DateTime horizonEnd, decimal entryPrice,
        TimeSpan fineStep, ProtectiveExitLagRequest request)
    {
        var pos = BuildPosition(entryPrice, entryTime, request);

        var start = LowerBound(fine, entryTime);
        for (var k = start; k < fine.Count; k++)
        {
            var bar = fine[k];
            var closesAt = bar.TimestampUtc + fineStep;
            if (closesAt > horizonEnd) break;

            var exit = ProtectiveExitEvaluator.EvaluateStopAndTarget(pos, bar.Open, bar.High, bar.Low);
            if (exit.ShouldClose)
            {
                // Sul percorso fine il prezzo registrato e quello ottenibile coincidono: il motore
                // agisce nell'istante in cui vede il livello, non alla fine di una barra successiva.
                return new Walk(true, closesAt, exit.Reason, exit.FillPrice, exit.FillPrice);
            }

            ProtectiveExitEvaluator.UpdateBestSinceEntry(pos, bar.High, bar.Low);
        }

        return default;
    }

    /// <summary>Prima barra con apertura >= <paramref name="t"/>, per ricerca binaria su lista ordinata.</summary>
    private static int LowerBound(IReadOnlyList<OhlcvData> bars, DateTime t)
    {
        int lo = 0, hi = bars.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (bars[mid].TimestampUtc < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static OpenPosition BuildPosition(decimal entryPrice, DateTime entryTime, ProtectiveExitLagRequest request)
    {
        var isLong = request.Side == OrderSide.Buy;
        var sl = isLong
            ? entryPrice * (1m - request.StopLossPercent / 100m)
            : entryPrice * (1m + request.StopLossPercent / 100m);

        decimal? tp = request.TakeProfitPercent is decimal t and > 0m
            ? (isLong ? entryPrice * (1m + t / 100m) : entryPrice * (1m - t / 100m))
            : null;

        return new OpenPosition
        {
            Symbol = request.Symbol,
            Side = request.Side,
            EntryPrice = entryPrice,
            Quantity = 1m,
            StopLoss = sl,
            TakeProfit = tp,
            TrailingStopPercent = request.TrailingStopPercent,
            OpenedAtUtc = entryTime,
            Leverage = 1,
        };
    }

    // ------------------------------------------------------------------ confronto e sintesi

    private static ProtectiveExitLagObservation Combine(
        DateTime entryTime, decimal entryPrice, OrderSide side, Walk candle, Walk fine)
    {
        var o = new ProtectiveExitLagObservation
        {
            EntryTimeUtc = entryTime,
            EntryPrice = entryPrice,
            CandleExited = candle.Exited,
            FineExited = fine.Exited,
            CandleReason = candle.Reason,
            FineReason = fine.Reason,
        };

        if (candle.Exited)
        {
            o.CandleDiscoveredAtUtc = candle.DiscoveredAtUtc;
            // Ottimismo della contabilità attuale: il motore registra il LIVELLO, ma a barra chiusa
            // il prezzo davvero ottenibile è la chiusura. Positivo = registrato meglio del reale.
            o.CandleFillOptimismBps = Signed(candle.BookedFill, candle.ObtainablePrice, entryPrice, side);
        }

        if (fine.Exited)
        {
            o.FineDiscoveredAtUtc = fine.DiscoveredAtUtc;
        }

        if (candle.Exited && fine.Exited)
        {
            o.LeadSeconds = (candle.DiscoveredAtUtc - fine.DiscoveredAtUtc).TotalSeconds;
            o.DelayCostBps = Signed(fine.ObtainablePrice, candle.ObtainablePrice, entryPrice, side);
            o.ReasonsAgree = string.Equals(candle.Reason, fine.Reason, StringComparison.Ordinal);
        }

        return o;
    }

    /// <summary>
    /// Differenza fra due prezzi in punti base dell'ingresso, ORIENTATA sulla posizione: positivo =
    /// <paramref name="a"/> è il prezzo migliore per chi detiene la posizione. Per un long uscire
    /// più in alto è meglio; per uno short è il contrario.
    /// </summary>
    private static double Signed(decimal a, decimal b, decimal entryPrice, OrderSide side)
    {
        if (entryPrice <= 0m) return 0d;
        var diff = side == OrderSide.Buy ? a - b : b - a;
        return (double)(diff / entryPrice) * 10_000d;
    }

    private static ProtectiveExitLagReport Summarize(
        List<ProtectiveExitLagObservation> obs, ProtectiveExitLagRequest request)
    {
        var both = obs.Where(o => o.CandleExited && o.FineExited).ToList();

        var leads = both.Select(o => o.LeadSeconds).OrderBy(v => v).ToList();
        var costs = both.Select(o => o.DelayCostBps).OrderBy(v => v).ToList();
        var optimism = obs.Where(o => o.CandleExited)
            .Select(o => o.CandleFillOptimismBps).OrderBy(v => v).ToList();

        return new ProtectiveExitLagReport
        {
            Symbol = request.Symbol,
            LaneTimeframe = request.LaneTimeframe,
            FineTimeframe = request.FineTimeframe,
            StopLossPercent = request.StopLossPercent,
            TakeProfitPercent = request.TakeProfitPercent,
            TrailingStopPercent = request.TrailingStopPercent,

            PositionsSimulated = obs.Count,
            BothExited = both.Count,
            OnlyCandleExited = obs.Count(o => o.CandleExited && !o.FineExited),
            OnlyFineExited = obs.Count(o => !o.CandleExited && o.FineExited),
            NeitherExited = obs.Count(o => !o.CandleExited && !o.FineExited),
            ReasonDisagreements = both.Count(o => !o.ReasonsAgree),

            MedianLeadSeconds = P(leads, 0.50m),
            P90LeadSeconds = P(leads, 0.90m),
            MeanLeadSeconds = leads.Count == 0 ? 0d : leads.Average(),

            MedianDelayCostBps = P(costs, 0.50m),
            MeanDelayCostBps = costs.Count == 0 ? 0d : costs.Average(),
            P10DelayCostBps = P(costs, 0.10m),
            P90DelayCostBps = P(costs, 0.90m),
            AdverseShare = costs.Count == 0 ? 0d : (double)costs.Count(c => c < 0d) / costs.Count,

            MedianCandleFillOptimismBps = P(optimism, 0.50m),
            MeanCandleFillOptimismBps = optimism.Count == 0 ? 0d : optimism.Average(),

            Observations = obs,
        };
    }

    /// <summary>Percentile sulla stessa interpolazione lineare usata ovunque nella piattaforma.</summary>
    private static double P(IReadOnlyList<double> sorted, decimal p) =>
        sorted.Count == 0 ? 0d : (double)TradeStatistics.Percentile(sorted.Select(v => (decimal)v).ToList(), p);
}

/// <summary>Parametri della misura. Il bracket è quello REALE della corsia che si vuole valutare.</summary>
public sealed class ProtectiveExitLagRequest
{
    public required string Symbol { get; init; }

    /// <summary>Timeframe su cui opera la corsia (quello che oggi decide le uscite).</summary>
    public required string LaneTimeframe { get; init; }

    /// <summary>Timeframe fine che fa da surrogato dei tick (5m, o 1m dove esiste).</summary>
    public required string FineTimeframe { get; init; }

    public required decimal StopLossPercent { get; init; }
    public decimal? TakeProfitPercent { get; init; }
    public decimal? TrailingStopPercent { get; init; }

    public OrderSide Side { get; init; } = OrderSide.Buy;

    /// <summary>Orizzonte massimo in barre di corsia: oltre, la posizione è dichiarata non uscita.</summary>
    public int MaxHoldBars { get; init; } = 96;

    /// <summary>Passo di campionamento degli ingressi, per non simulare ogni singola barra su serie lunghe.</summary>
    public int SampleEveryNBars { get; init; } = 1;
}

/// <summary>Una posizione simulata, vista dai due percorsi.</summary>
public sealed class ProtectiveExitLagObservation
{
    public DateTime EntryTimeUtc { get; init; }
    public decimal EntryPrice { get; init; }

    public bool CandleExited { get; init; }
    public bool FineExited { get; init; }
    public string CandleReason { get; init; } = string.Empty;
    public string FineReason { get; init; } = string.Empty;

    public DateTime? CandleDiscoveredAtUtc { get; set; }
    public DateTime? FineDiscoveredAtUtc { get; set; }

    /// <summary>Secondi di anticipo del percorso fine sulla scoperta dell'uscita.</summary>
    public double LeadSeconds { get; set; }

    /// <summary>
    /// Costo del ritardo in punti base dell'ingresso: quanto si perde uscendo al prezzo ottenibile
    /// a barra chiusa invece che al momento del tocco. Positivo = il percorso fine esce meglio.
    /// </summary>
    public double DelayCostBps { get; set; }

    /// <summary>
    /// Scarto fra il fill che il motore REGISTRA sul percorso a candela (il livello) e il prezzo
    /// davvero ottenibile alla chiusura di quella barra. Positivo = la contabilità è ottimista.
    /// </summary>
    public double CandleFillOptimismBps { get; set; }

    public bool ReasonsAgree { get; set; }
}

/// <summary>Sintesi della misura su una serie.</summary>
public sealed class ProtectiveExitLagReport
{
    public string Symbol { get; init; } = string.Empty;
    public string LaneTimeframe { get; init; } = string.Empty;
    public string FineTimeframe { get; init; } = string.Empty;
    public decimal StopLossPercent { get; init; }
    public decimal? TakeProfitPercent { get; init; }
    public decimal? TrailingStopPercent { get; init; }

    public int PositionsSimulated { get; init; }
    public int BothExited { get; init; }
    public int OnlyCandleExited { get; init; }
    public int OnlyFineExited { get; init; }
    public int NeitherExited { get; init; }
    public int ReasonDisagreements { get; init; }

    public double MedianLeadSeconds { get; init; }
    public double P90LeadSeconds { get; init; }
    public double MeanLeadSeconds { get; init; }

    public double MedianDelayCostBps { get; init; }
    public double MeanDelayCostBps { get; init; }
    public double P10DelayCostBps { get; init; }
    public double P90DelayCostBps { get; init; }

    /// <summary>Quota di posizioni in cui il percorso fine esce PEGGIO: il feed non è gratis per definizione.</summary>
    public double AdverseShare { get; init; }

    public double MedianCandleFillOptimismBps { get; init; }
    public double MeanCandleFillOptimismBps { get; init; }

    public IReadOnlyList<ProtectiveExitLagObservation> Observations { get; init; } = [];
}
