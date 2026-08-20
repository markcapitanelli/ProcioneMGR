using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Breakout di canale Donchian (il sistema di riferimento di Trombetta, cap. 6):
///  - Long quando la close supera il Donchian High (HHV) a <c>EntryPeriod</c> della barra
///    precedente; chiusura quando la close viola il Donchian Low (LLV) a <c>ExitPeriod</c>
///    della barra precedente.
///  - Short speculare (breakdown su LLV a EntryPeriod, uscita su HHV a ExitPeriod),
///    abilitabile con il parametro <c>Direction</c>.
///
/// Il confronto con il canale della barra PRECEDENTE e' obbligatorio: la close, per
/// definizione, non puo' mai superare l'HHV calcolato sulla barra in corso (retroazione).
/// Direction: 0 = solo long, 1 = solo short, 2 = entrambi.
/// </summary>
public sealed class DonchianBreakoutStrategy : IStrategy, IPositionMirroringStrategy
{
    public string Name => "DonchianBreakout";
    public string DisplayName => "Donchian Breakout";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("EntryPeriod", "Periodo canale di ingresso", 20m, 2m, 1000m),
        new StrategyParameterDefinition("ExitPeriod", "Periodo canale di uscita", 5m, 1m, 1000m),
        new StrategyParameterDefinition("Direction", "Direzione (0=long, 1=short, 2=both)", 0m, 0m, 2m),
    ];

    private decimal?[] _entryUpper = [];
    private decimal?[] _entryLower = [];
    private decimal?[] _exitUpper = [];
    private decimal?[] _exitLower = [];
    private int _direction;
    private int _side; // 0 flat, +1 long, -1 short (specchio della posizione del motore)

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var entryPeriod = (int)parameters.GetOrDefault("EntryPeriod", 20m);
        var exitPeriod = (int)parameters.GetOrDefault("ExitPeriod", 5m);
        _direction = (int)parameters.GetOrDefault("Direction", 0m);
        if (entryPeriod < 2 || exitPeriod < 1 || _direction is < 0 or > 2)
        {
            throw new ArgumentException("Parametri DonchianBreakout non validi: EntryPeriod >= 2, ExitPeriod >= 1, Direction in [0,2].");
        }

        var highs = new List<decimal>(candles.Count);
        var lows = new List<decimal>(candles.Count);
        foreach (var c in candles)
        {
            highs.Add(c.High);
            lows.Add(c.Low);
        }

        var (entryUpper, entryLower) = await indicators.CalculateDonchianAsync(highs, lows, entryPeriod, ct);
        var (exitUpper, exitLower) = await indicators.CalculateDonchianAsync(highs, lows, exitPeriod, ct);
        _entryUpper = [.. entryUpper];
        _entryLower = [.. entryLower];
        _exitUpper = [.. exitUpper];
        _exitLower = [.. exitLower];
        _side = 0;
    }
    /// <summary>
    /// [revisione 2026-08-20] Rimette in pari lo specchio della posizione: dal vivo il motore crea
    /// un'istanza nuova a ogni candela, quindi senza questo <c>_side</c> restava 0 e le uscite dal
    /// canale non scattavano mai.
    /// </summary>
    public void RestorePosition(ProcioneMGR.Services.Trading.OrderSide? side, decimal entryPrice, DateTime openedAtUtc)
        => _side = side switch
        {
            ProcioneMGR.Services.Trading.OrderSide.Buy => 1,
            ProcioneMGR.Services.Trading.OrderSide.Sell => -1,
            _ => 0,
        };


    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (index < 1)
        {
            return Signal.Hold;
        }

        var prevEntryUpper = _entryUpper[index - 1];
        var prevEntryLower = _entryLower[index - 1];
        var prevExitUpper = _exitUpper[index - 1];
        var prevExitLower = _exitLower[index - 1];

        // Uscite prima degli ingressi: proteggono la posizione in corso.
        if (_side == 1 && prevExitLower.HasValue && currentPrice < prevExitLower.Value)
        {
            _side = 0;
            return Signal.Close;
        }
        if (_side == -1 && prevExitUpper.HasValue && currentPrice > prevExitUpper.Value)
        {
            _side = 0;
            return Signal.Close;
        }

        if (_side == 0)
        {
            var allowLong = _direction is 0 or 2;
            var allowShort = _direction is 1 or 2;
            if (allowLong && prevEntryUpper.HasValue && currentPrice > prevEntryUpper.Value)
            {
                _side = 1;
                return Signal.Long;
            }
            if (allowShort && prevEntryLower.HasValue && currentPrice < prevEntryLower.Value)
            {
                _side = -1;
                return Signal.Short;
            }
        }

        return Signal.Hold;
    }
}
