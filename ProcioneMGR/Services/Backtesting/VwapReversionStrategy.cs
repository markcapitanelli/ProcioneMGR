using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// VWAP Reversion (la strategia intraday "per eccellenza"): il VWAP (Volume Weighted Average
/// Price) di SESSIONE e' il prezzo medio ponderato per i volumi dall'inizio della giornata UTC,
/// il benchmark che ogni operatore intraday osserva. Quando il prezzo si allontana dal VWAP
/// oltre una soglia si assume un rientro verso la media: Long se e' sotto il VWAP di
/// <c>Threshold</c>, Short se e' sopra (con <c>AllowShort=1</c>), Close al riattraversamento
/// del VWAP.
///
/// SESSIONE: il VWAP si azzera a ogni cambio di data UTC (convenzione standard, coerente con le
/// candele giornaliere e il funding degli exchange). ANTI-LOOK-AHEAD: il VWAP alla barra i usa
/// solo le barre dall'inizio sessione fino a i inclusa (valore corrente, non futuro).
/// </summary>
public sealed class VwapReversionStrategy : IStrategy
{
    public string Name => "VwapReversion";
    public string DisplayName => "VWAP Reversion (intraday)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("Threshold", "Scostamento dal VWAP (frazione)", 0.01m, 0.001m, 0.2m),
        new StrategyParameterDefinition("AllowShort", "Consenti short (0/1)", 1m, 0m, 1m),
    ];

    private decimal?[] _vwap = [];
    private decimal[] _closes = [];
    private decimal _threshold = 0.01m;
    private bool _allowShort = true;

    public Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        _threshold = parameters.GetOrDefault("Threshold", 0.01m);
        _allowShort = parameters.GetOrDefault("AllowShort", 1m) >= 0.5m;
        if (_threshold <= 0m)
        {
            throw new ArgumentException("Parametro VwapReversion non valido: Threshold > 0.");
        }

        var n = candles.Count;
        _closes = closes as decimal[] ?? [.. closes];
        _vwap = new decimal?[n];

        decimal cumPv = 0m, cumV = 0m;
        DateTime? sessionDate = null;
        for (var i = 0; i < n; i++)
        {
            var c = candles[i];
            var day = c.TimestampUtc.Date;
            if (sessionDate != day)
            {
                // Nuova sessione UTC: azzera gli accumulatori.
                cumPv = 0m;
                cumV = 0m;
                sessionDate = day;
            }

            var typical = (c.High + c.Low + c.Close) / 3m;
            cumPv += typical * c.Volume;
            cumV += c.Volume;
            _vwap[i] = cumV > 0m ? cumPv / cumV : typical;
        }

        return Task.CompletedTask;
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (_vwap[index] is not decimal vwap || vwap <= 0m)
        {
            return Signal.Hold;
        }

        var deviation = (currentPrice - vwap) / vwap;

        if (deviation < -_threshold)
        {
            return Signal.Long;   // sotto il VWAP -> rientro verso l'alto
        }
        if (deviation > _threshold)
        {
            return _allowShort ? Signal.Short : Signal.Hold;  // sopra il VWAP -> rientro verso il basso
        }

        // Chiudi al riattraversamento del VWAP (rientro alla media completato).
        if (index >= 1 && _vwap[index - 1] is decimal prevVwap && prevVwap > 0m)
        {
            var prevSide = _closes[index - 1] - prevVwap;
            var curSide = currentPrice - vwap;
            if (prevSide * curSide < 0m)
            {
                return Signal.Close;
            }
        }

        return Signal.Hold;
    }
}
