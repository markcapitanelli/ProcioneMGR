using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// Analisi dei Gap e dei Lap di prezzo (Trombetta, cap. 4): base statistica per i sistemi
/// "Gap Filling" (mean reverting sul riassorbimento) o trend following (continuazione).
///
/// Definizioni (per ogni barra rispetto alla precedente):
///  - Gap Up:   open &gt; high precedente;                        entita' = open - high[-1]
///  - Gap Down: open &lt; low precedente;                         entita' = open - low[-1]
///  - Lap Up:   close[-1] &lt; open &lt;= high[-1];                entita' = open - close[-1]
///  - Lap Down: low[-1] &lt;= open &lt; close[-1];                 entita' = open - close[-1]
/// Sotto-eventi:
///  - Refilled:      il prezzo ha ricolmato il salto in barra (gap up: low &lt;= high[-1];
///                    gap down: high &gt;= low[-1]; lap: raggiunta la close[-1]).
///  - DeepRefilled:  (solo gap) violata anche la close[-1].
///  - Pos / Neg:     la barra del gap ha chiuso sopra / sotto la propria apertura.
///
/// Nota crypto: su mercati continui (spot 24/7) open ~= close[-1], quindi Gap/Lap "veri"
/// emergono solo su dati con buchi (weekend CME, pause, illiquidita', delisting).
/// L'analisi resta valida su qualunque serie OHLCV con sessioni discontinue.
/// </summary>
public sealed class GapLapAnalyzer
{
    /// <param name="candles">Serie OHLCV ordinata cronologicamente (tipicamente daily o "sessione").</param>
    /// <param name="pointValue">
    /// Controvalore monetario di 1 punto di prezzo (bigpointvalue dei future; 1 per spot).
    /// Serve per esprimere le entita' medie in valuta.
    /// </param>
    public GapLapReport Analyze(IReadOnlyList<OhlcvData> candles, decimal pointValue = 1m)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var gapUp = new EventAccumulator();
        var gapDown = new EventAccumulator();
        var lapUp = new EventAccumulator();
        var lapDown = new EventAccumulator();

        var totalBars = Math.Max(candles.Count - 1, 0);

        for (var i = 1; i < candles.Count; i++)
        {
            var prev = candles[i - 1];
            var cur = candles[i];

            if (cur.Open > prev.High)
            {
                gapUp.Add(
                    entity: cur.Open - prev.High,
                    dayResult: cur.Close - cur.Open,
                    refilled: cur.Low <= prev.High,
                    deepRefilled: cur.Low <= prev.Close);
            }
            else if (cur.Open < prev.Low)
            {
                gapDown.Add(
                    entity: cur.Open - prev.Low,
                    dayResult: cur.Close - cur.Open,
                    refilled: cur.High >= prev.Low,
                    deepRefilled: cur.High >= prev.Close);
            }
            else if (cur.Open > prev.Close)
            {
                // Lap: il "refill" coincide col raggiungimento della close precedente,
                // quindi non esiste il livello "deep".
                lapUp.Add(
                    entity: cur.Open - prev.Close,
                    dayResult: cur.Close - cur.Open,
                    refilled: cur.Low <= prev.Close,
                    deepRefilled: false);
            }
            else if (cur.Open < prev.Close)
            {
                lapDown.Add(
                    entity: cur.Open - prev.Close,
                    dayResult: cur.Close - cur.Open,
                    refilled: cur.High >= prev.Close,
                    deepRefilled: false);
            }
            // open == close[-1]: nessun evento (caso tipico dei mercati continui).
        }

        return new GapLapReport
        {
            TotalBars = totalBars,
            PointValue = pointValue,
            GapUp = gapUp.ToStats(totalBars, pointValue, hasDeep: true),
            GapDown = gapDown.ToStats(totalBars, pointValue, hasDeep: true),
            LapUp = lapUp.ToStats(totalBars, pointValue, hasDeep: false),
            LapDown = lapDown.ToStats(totalBars, pointValue, hasDeep: false),
        };
    }

    /// <summary>
    /// Classificazione contestuale dei gap (McAllen, cap. 13): il TIPO di gap dipende da
    /// quanto trend c'era gia' prima del salto.
    ///  - Breakaway:  a inizio movimento (trend precedente nella direzione del gap ancora
    ///                debole) — segnala l'avvio di un movimento importante.
    ///  - Runaway:    a meta' movimento (trend moderato) — "measuring gap", scarso valore
    ///                previsivo secondo il libro.
    ///  - Exhaustion: dopo un trend gia' esteso — ultimo colpo di coda, spesso con volume in
    ///                picco (capitulation al ribasso, euforia al rialzo).
    /// Rileva anche l'Island Reversal (exhaustion gap seguito a breve da un breakaway gap in
    /// direzione opposta che isola alcune barre) e se/quando ogni gap e' stato ricolmato.
    /// </summary>
    public IReadOnlyList<GapEvent> ClassifyGaps(
        IReadOnlyList<OhlcvData> candles,
        int trendLookback = 10,
        decimal runawayMinTrendPercent = 3m,
        decimal exhaustionMinTrendPercent = 10m,
        int volumeWindow = 20,
        decimal volumeSpikeFactor = 1.5m,
        int islandMaxBars = 10)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var events = new List<GapEvent>();

        // Media mobile del volume per rilevare i picchi.
        var volAvg = new decimal[candles.Count];
        decimal volSum = 0m;
        for (var i = 0; i < candles.Count; i++)
        {
            volSum += candles[i].Volume;
            if (i >= volumeWindow) volSum -= candles[i - volumeWindow].Volume;
            volAvg[i] = volSum / Math.Min(i + 1, volumeWindow);
        }

        for (var i = 1; i < candles.Count; i++)
        {
            var prev = candles[i - 1];
            var cur = candles[i];

            var isUp = cur.Open > prev.High;
            var isDown = cur.Open < prev.Low;
            if (!isUp && !isDown) continue;

            // Trend precedente nella direzione del gap (variazione % delle chiusure).
            var start = Math.Max(0, i - 1 - trendLookback);
            var trendPercent = candles[start].Close > 0m
                ? (prev.Close - candles[start].Close) / candles[start].Close * 100m
                : 0m;
            var directionalTrend = isUp ? trendPercent : -trendPercent;

            var type = directionalTrend >= exhaustionMinTrendPercent ? GapType.Exhaustion
                     : directionalTrend >= runawayMinTrendPercent ? GapType.Runaway
                     : GapType.Breakaway;

            var volumeRatio = volAvg[i] > 0m ? cur.Volume / volAvg[i] : 0m;

            // Il gap e' stato ricolmato? (il prezzo torna a coprire lo spazio vuoto)
            int? filledAt = null;
            var gapLevel = isUp ? prev.High : prev.Low;
            for (var j = i; j < candles.Count; j++)
            {
                if (isUp ? candles[j].Low <= gapLevel : candles[j].High >= gapLevel)
                {
                    filledAt = j;
                    break;
                }
            }

            events.Add(new GapEvent(
                Index: i,
                Timestamp: cur.TimestampUtc,
                IsUp: isUp,
                Type: type,
                SizePercent: prev.Close > 0m ? Math.Abs(cur.Open - gapLevel) / prev.Close * 100m : 0m,
                PriorTrendPercent: trendPercent,
                VolumeRatio: volumeRatio,
                VolumeSpike: volumeRatio >= volumeSpikeFactor,
                FilledAtIndex: filledAt,
                IsIslandReversal: false));
        }

        // Island reversal: gap in una direzione seguito entro islandMaxBars da un gap opposto.
        for (var e = 1; e < events.Count; e++)
        {
            var first = events[e - 1];
            var second = events[e];
            if (first.IsUp != second.IsUp && second.Index - first.Index <= islandMaxBars)
            {
                events[e] = second with { IsIslandReversal = true };
            }
        }

        return events;
    }

    /// <summary>Accumulatore per una categoria di evento (gap up, gap down, lap up, lap down).</summary>
    private sealed class EventAccumulator
    {
        private int _count;
        private decimal _entitySum;
        private int _refilledCount;
        private decimal _refilledEntitySum;
        private int _deepCount;
        private decimal _deepEntitySum;
        private int _posCount;
        private decimal _posSum;
        private int _negCount;
        private decimal _negSum;

        public void Add(decimal entity, decimal dayResult, bool refilled, bool deepRefilled)
        {
            _count++;
            _entitySum += entity;
            if (refilled)
            {
                _refilledCount++;
                _refilledEntitySum += entity;
            }
            if (deepRefilled)
            {
                _deepCount++;
                _deepEntitySum += entity;
            }
            if (dayResult > 0m)
            {
                _posCount++;
                _posSum += dayResult;
            }
            else if (dayResult < 0m)
            {
                _negCount++;
                _negSum += dayResult;
            }
        }

        public GapLapCategoryStats ToStats(int totalBars, decimal pointValue, bool hasDeep) => new()
        {
            Count = _count,
            PercentOfBars = totalBars == 0 ? 0m : (decimal)_count / totalBars * 100m,
            EntitySum = _entitySum,
            EntityAvg = _count == 0 ? 0m : _entitySum / _count,
            MoneyAvg = _count == 0 ? 0m : _entitySum / _count * pointValue,

            RefilledCount = _refilledCount,
            RefilledPercent = _count == 0 ? 0m : (decimal)_refilledCount / _count * 100m,
            RefilledEntityAvg = _refilledCount == 0 ? 0m : _refilledEntitySum / _refilledCount,
            RefilledMoneyAvg = _refilledCount == 0 ? 0m : _refilledEntitySum / _refilledCount * pointValue,

            DeepRefilledCount = hasDeep ? _deepCount : null,
            DeepRefilledPercent = hasDeep ? (_count == 0 ? 0m : (decimal)_deepCount / _count * 100m) : null,
            DeepRefilledMoneyAvg = hasDeep ? (_deepCount == 0 ? 0m : _deepEntitySum / _deepCount * pointValue) : null,

            PositiveCount = _posCount,
            PositivePercent = _count == 0 ? 0m : (decimal)_posCount / _count * 100m,
            PositiveMoneyAvg = _posCount == 0 ? 0m : _posSum / _posCount * pointValue,

            NegativeCount = _negCount,
            NegativePercent = _count == 0 ? 0m : (decimal)_negCount / _count * 100m,
            NegativeMoneyAvg = _negCount == 0 ? 0m : _negSum / _negCount * pointValue,
        };
    }
}

/// <summary>Tipo di gap secondo il contesto di trend (McAllen cap. 13).</summary>
public enum GapType
{
    Breakaway,
    Runaway,
    Exhaustion,
}

/// <summary>Singolo gap classificato per contesto, con volume ed eventuale riempimento.</summary>
public sealed record GapEvent(
    int Index,
    DateTime Timestamp,
    bool IsUp,
    GapType Type,
    decimal SizePercent,
    /// <summary>Variazione % delle chiusure nelle barre precedenti il gap (segno assoluto).</summary>
    decimal PriorTrendPercent,
    decimal VolumeRatio,
    bool VolumeSpike,
    /// <summary>Indice della barra che ha ricolmato il gap (null = mai ricolmato).</summary>
    int? FilledAtIndex,
    bool IsIslandReversal)
{
    public bool IsFilled => FilledAtIndex.HasValue;
}

/// <summary>Report complessivo dell'analisi gap/lap su una serie.</summary>
public sealed record GapLapReport
{
    /// <summary>Barre analizzate (dalla seconda in poi: serve la precedente per il confronto).</summary>
    public int TotalBars { get; init; }
    public decimal PointValue { get; init; }
    public GapLapCategoryStats GapUp { get; init; } = new();
    public GapLapCategoryStats GapDown { get; init; } = new();
    public GapLapCategoryStats LapUp { get; init; } = new();
    public GapLapCategoryStats LapDown { get; init; } = new();
}

/// <summary>Statistiche di una categoria di gap/lap (la "tavola riassuntiva" del libro).</summary>
public sealed record GapLapCategoryStats
{
    public int Count { get; init; }
    public decimal PercentOfBars { get; init; }
    /// <summary>Somma delle entita' (bacino potenziale cumulato, in punti prezzo).</summary>
    public decimal EntitySum { get; init; }
    public decimal EntityAvg { get; init; }
    public decimal MoneyAvg { get; init; }

    public int RefilledCount { get; init; }
    /// <summary>% di eventi ricolmati in barra: alta -> vocazione mean reverting.</summary>
    public decimal RefilledPercent { get; init; }
    public decimal RefilledEntityAvg { get; init; }
    public decimal RefilledMoneyAvg { get; init; }

    /// <summary>Solo per i gap: ricolmati fino a violare la close precedente. Null per i lap.</summary>
    public int? DeepRefilledCount { get; init; }
    public decimal? DeepRefilledPercent { get; init; }
    public decimal? DeepRefilledMoneyAvg { get; init; }

    public int PositiveCount { get; init; }
    /// <summary>% di barre chiuse sopra l'apertura dopo l'evento: alta -> vocazione trend following.</summary>
    public decimal PositivePercent { get; init; }
    public decimal PositiveMoneyAvg { get; init; }

    public int NegativeCount { get; init; }
    public decimal NegativePercent { get; init; }
    public decimal NegativeMoneyAvg { get; init; }
}
