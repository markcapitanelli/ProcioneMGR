using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Supertrend (trend-following su ATR, il sistema intraday crypto piu' diffuso): due bande
/// attorno al prezzo medio (H+L)/2 a distanza <c>Multiplier * ATR</c>, con la logica standard
/// di "locking" delle bande finali; il trend commuta quando la close attraversa la banda
/// attiva. Long allo switch rialzista; allo switch ribassista Short (se <c>AllowShort=1</c>)
/// oppure Close.
///
/// ANTI-LOOK-AHEAD: la decisione alla barra i usa esclusivamente ATR/prezzo fino alla barra i
/// (stessa convenzione "decido alla chiusura della barra" di tutte le altre strategie).
/// </summary>
public sealed class SupertrendStrategy : IStrategy
{
    public string Name => "Supertrend";
    public string DisplayName => "Supertrend (ATR)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("AtrPeriod", "Periodo ATR", 10m, 2m, 100m),
        new StrategyParameterDefinition("Multiplier", "Moltiplicatore ATR", 3.0m, 0.5m, 10m),
        new StrategyParameterDefinition("AllowShort", "Consenti short (0/1)", 1m, 0m, 1m),
    ];

    private int[] _trend = [];   // +1 up, -1 down, 0 warm-up
    private bool _allowShort = true;

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var atrPeriod = (int)parameters.GetOrDefault("AtrPeriod", 10m);
        var mult = parameters.GetOrDefault("Multiplier", 3.0m);
        _allowShort = parameters.GetOrDefault("AllowShort", 1m) >= 0.5m;
        if (atrPeriod < 2 || mult <= 0m)
        {
            throw new ArgumentException("Parametri Supertrend non validi: AtrPeriod >= 2 e Multiplier > 0.");
        }

        var n = candles.Count;
        var highs = new List<decimal>(n);
        var lows = new List<decimal>(n);
        var closeList = closes as List<decimal> ?? [.. closes];
        foreach (var c in candles) { highs.Add(c.High); lows.Add(c.Low); }

        var atr = await indicators.CalculateAtrAsync(highs, lows, closeList, atrPeriod, ct);

        _trend = new int[n];
        decimal finalUpper = 0m, finalLower = 0m;
        var prevTrendUp = true;
        for (var i = 0; i < n; i++)
        {
            if (atr[i] is not decimal a)
            {
                _trend[i] = 0;
                continue;
            }

            var hl2 = (highs[i] + lows[i]) / 2m;
            var basicUpper = hl2 + mult * a;
            var basicLower = hl2 - mult * a;

            if (i == 0 || _trend[i - 1] == 0)
            {
                // Prima barra utile: inizializza le bande e parti dal trend implicito.
                finalUpper = basicUpper;
                finalLower = basicLower;
                prevTrendUp = closeList[i] >= hl2;
                _trend[i] = prevTrendUp ? 1 : -1;
                continue;
            }

            var prevClose = closeList[i - 1];
            finalUpper = (basicUpper < finalUpper || prevClose > finalUpper) ? basicUpper : finalUpper;
            finalLower = (basicLower > finalLower || prevClose < finalLower) ? basicLower : finalLower;

            // Il trend commuta quando la close supera la banda opposta a quella attiva.
            bool trendUp;
            if (prevTrendUp)
            {
                trendUp = closeList[i] >= finalLower;
            }
            else
            {
                trendUp = closeList[i] > finalUpper;
            }
            _trend[i] = trendUp ? 1 : -1;
            prevTrendUp = trendUp;
        }
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        var trend = _trend[index];
        if (trend == 0)
        {
            return Signal.Hold;   // warm-up ATR
        }

        // Emissione CONTINUA della direzione del trend (come RsiOversold/Bollinger): il motore
        // apre se flat, ignora se già nella stessa direzione, flippa sul segnale opposto. Così
        // la strategia entra anche sul PRIMO trend consolidato, non solo dopo un'inversione.
        if (trend == 1)
        {
            return Signal.Long;
        }
        // trend ribassista: Short se consentito, altrimenti chiudi l'eventuale long.
        return _allowShort ? Signal.Short : Signal.Close;
    }
}
