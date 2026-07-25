using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// [Fase 5b — docs/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Mean reversion a gradini fissi attorno a un
/// ancoraggio mobile: entra quando il prezzo si allontana di <c>EntryRungs</c> gradini dall'SMA di
/// riferimento, esce quando ne ha recuperato uno. È il **ciclo finito e restartabile** che il PDF
/// descrive come cuore economico del grid trading.
///
/// <b>Non è grid trading, e il nome lo dice apposta.</b> Un grid vero appoggia molti ordini limite
/// simultanei sopra e sotto il prezzo e porta più posizioni insieme; questo motore è a posizione
/// singola per costruzione (<c>Portfolio</c> ha un solo stato flat/long/short), quindi un grid
/// multi-ordine <b>non è esprimibile</b> qui. Chiamarlo "Grid" e attribuirgli i numeri del PDF
/// (rendimento 8,39%, Sharpe 0,38 — già deboli di loro) significherebbe misurare una cosa e
/// raccontarne un'altra: il rischio che questa piattaforma ha imparato a evitare.
///
/// Cosa cattura davvero: l'idea che in un mercato laterale si possa raccogliere ripetutamente un
/// gradino di oscillazione, con un obiettivo fisso invece che una tendenza da cavalcare. Cosa non
/// cattura: la media dei prezzi su più gradini di ingresso e l'inventario simultaneo, che sono
/// proprio ciò che rende il grid pericoloso quando il laterale finisce.
///
/// Rispetto alla <see cref="BollingerMeanReversionStrategy"/> la differenza è deliberata e la
/// ragione per cui vale la pena averle entrambe nel catalogo: là la banda è <i>adattiva</i> alla
/// volatilità, qui il gradino è <i>fisso</i>. Quale delle due funzioni meglio è una domanda
/// empirica, ed è la caccia a doverla decidere.
///
/// Lo stop loss non è compito della strategia: resta l'overlay del motore, come per tutte le altre.
/// </summary>
public sealed class GridMeanReversionStrategy : IStrategy
{
    public string Name => "GridMeanReversion";
    public string DisplayName => "Grid Mean Reversion (gradini fissi)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("AnchorPeriod", "Periodo SMA di ancoraggio", 50m, 5m, 500m),
        new StrategyParameterDefinition("StepPercent", "Ampiezza del gradino (%)", 1m, 0.1m, 20m),
        new StrategyParameterDefinition("EntryRungs", "Gradini di distanza per entrare", 1m, 1m, 10m),
        new StrategyParameterDefinition("Direction", "Direzione (0=long, 1=short, 2=both)", 0m, 0m, 2m),
    ];

    private decimal?[] _anchor = [];
    private decimal _stepFrac;
    private int _entryRungs;
    private int _direction;

    private int _side;             // 0 flat, +1 long, -1 short (specchio della posizione del motore)
    private decimal _entryPrice;   // prezzo del ciclo in corso, per l'obiettivo a un gradino

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        var anchorPeriod = (int)parameters.GetOrDefault("AnchorPeriod", 50m);
        var stepPercent = parameters.GetOrDefault("StepPercent", 1m);
        _entryRungs = (int)parameters.GetOrDefault("EntryRungs", 1m);
        _direction = (int)parameters.GetOrDefault("Direction", 0m);

        if (anchorPeriod < 5 || stepPercent <= 0m || _entryRungs < 1 || _direction is < 0 or > 2)
        {
            throw new ArgumentException(
                "Parametri GridMeanReversion non validi: AnchorPeriod >= 5, StepPercent > 0, EntryRungs >= 1, Direction in [0,2].");
        }

        _stepFrac = stepPercent / 100m;
        _anchor = [.. await indicators.CalculateSmaAsync([.. closes], anchorPeriod, ct)];
        _side = 0;
        _entryPrice = 0m;
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        // L'ancoraggio è quello della barra PRECEDENTE: una SMA che include la close corrente
        // saprebbe già dove il prezzo è andato, e la distanza da essa sarebbe in parte una
        // retroazione invece che un segnale.
        if (index < 1) return Signal.Hold;
        if (_anchor[index - 1] is not decimal anchor || anchor <= 0m) return Signal.Hold;

        // Uscita prima dell'ingresso: il ciclo in corso ha la precedenza.
        if (_side == 1)
        {
            if (currentPrice >= _entryPrice * (1m + _stepFrac)) { _side = 0; return Signal.Close; }
            return Signal.Hold;
        }
        if (_side == -1)
        {
            if (currentPrice <= _entryPrice * (1m - _stepFrac)) { _side = 0; return Signal.Close; }
            return Signal.Hold;
        }

        var distance = _entryRungs * _stepFrac;
        if (_direction is 0 or 2 && currentPrice <= anchor * (1m - distance))
        {
            _side = 1;
            _entryPrice = currentPrice;
            return Signal.Long;
        }
        if (_direction is 1 or 2 && currentPrice >= anchor * (1m + distance))
        {
            _side = -1;
            _entryPrice = currentPrice;
            return Signal.Short;
        }

        return Signal.Hold;
    }
}
