using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Regime;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// Il "collante" fra i modelli ML (cap. 6-12 del libro) e il backtest: carica un
/// <see cref="IReturnPredictor"/> già addestrato, in <see cref="InitializeAsync"/> pre-calcola i
/// fattori usati in addestramento su tutta la serie (stesso schema di <c>DatasetBuilder</c>, ma
/// senza target), e in <see cref="EvaluateSignal"/> traduce la predizione di rendimento forward
/// in un <see cref="Signal"/> tramite soglie long/short. Così ogni modello diventa
/// immediatamente back-testabile, ottimizzabile e inseribile nell'ensemble.
///
/// DEVIAZIONE FLAGGATA: a differenza delle altre strategie, <c>MlStrategy</c> non è creabile
/// dalla <c>StrategyFactory</c> per nome (switch senza reflection, zero-arg) perché richiede un
/// predittore già addestrato e la lista dei fattori con cui è stato addestrato — non
/// rappresentabili come <c>Dictionary&lt;string, decimal&gt;</c>. Si usa costruendola
/// direttamente e passandola al nuovo overload <c>IBacktestEngine.RunBacktestAsync(config,
/// candles, strategy, ct)</c>. L'integrazione con Optimization/Discovery/Ensemble/UI (selezione
/// del modello persistito) è un passo successivo, non parte di questa fondazione.
/// </summary>
public sealed class MlStrategy : IStrategy
{
    public string Name => "Ml";
    public string DisplayName => "ML (predittore di rendimento)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions { get; } =
    [
        new StrategyParameterDefinition("LongThreshold", "Soglia Long (rendimento atteso)", 0.005m, 0.0001m, 1m),
        new StrategyParameterDefinition("ShortThreshold", "Soglia Short (rendimento atteso)", 0.005m, 0.0001m, 1m),
    ];

    private readonly IReturnPredictor _predictor;
    private readonly IReadOnlyList<FactorSpec> _factors;
    private readonly ISequencePredictor? _sequence;
    private readonly Alpha.IFactorCache? _factorCache;

    // Regime one-hot (opzionale, default OFF): se detector+extractor sono forniti e _regimeCount>0,
    // InitializeAsync appende K colonne one-hot del regime a ogni riga di _featureMatrix, con lo
    // STESSO percorso causale usato in costruzione dataset (parità train/serve). Vedi RegimeAugmentation.
    private readonly IRegimeDetector? _regimeDetector;
    private readonly IMarketFeatureExtractor? _featureExtractor;
    private readonly int _regimeCount;

    private float[][] _featureMatrix = [];
    private DateTime[] _timestamps = [];   // per la guardia di contiguità dei modelli sequenziali
    private long _stepTicks;
    private decimal _longThreshold = 0.005m;
    private decimal _shortThreshold = 0.005m;

    public MlStrategy(
        IReturnPredictor predictor,
        IReadOnlyList<FactorSpec> factors,
        Alpha.IFactorCache? factorCache = null,
        IRegimeDetector? regimeDetector = null,
        IMarketFeatureExtractor? featureExtractor = null,
        int regimeCount = 0)
    {
        _predictor = predictor ?? throw new ArgumentNullException(nameof(predictor));
        _factors = factors ?? throw new ArgumentNullException(nameof(factors));
        if (_factors.Count == 0) throw new ArgumentException("Servono i fattori con cui il predittore è stato addestrato.", nameof(factors));
        _factorCache = factorCache;

        // Se il predittore è sequenziale (es. attention), l'inferenza impacchetta gli ultimi T
        // vettori di fattori in una finestra — vedi EvaluateSignal. Additivo: gli altri modelli
        // restano puntuali e non ne risentono.
        _sequence = predictor as ISequencePredictor;

        var wantRegime = regimeCount > 0 && regimeDetector is not null && featureExtractor is not null;
        if (wantRegime && _sequence is not null)
            throw new NotSupportedException("Il regime one-hot non è supportato con i predittori sequenziali (la finestra impacchetta i soli fattori).");
        _regimeDetector = wantRegime ? regimeDetector : null;
        _featureExtractor = wantRegime ? featureExtractor : null;
        _regimeCount = wantRegime ? regimeCount : 0;
    }

    public async Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        if (!_predictor.IsFitted)
        {
            throw new InvalidOperationException("MlStrategy richiede un IReturnPredictor già addestrato (Fit) o caricato (Load).");
        }

        _longThreshold = parameters.GetOrDefault("LongThreshold", 0.005m);
        _shortThreshold = parameters.GetOrDefault("ShortThreshold", 0.005m);

        // Ogni fattore calcolato UNA VOLTA su tutta la serie (stesso contratto anti-look-ahead
        // usato da DatasetBuilder in addestramento — coerenza fra training e inferenza).
        var n = candles.Count;
        var featureSeries = new IReadOnlyList<decimal?>[_factors.Count];
        for (var f = 0; f < _factors.Count; f++)
        {
            // Via cache quando disponibile: la STESSA serie usata in addestramento (DatasetBuilder)
            // viene riusata all'inferenza per gli stessi (fattore+parametri, candele) — coerenza train/serve.
            featureSeries[f] = _factorCache is not null
                ? _factorCache.GetOrCompute(_factors[f].Factor, _factors[f].Parameters, candles)
                : _factors[f].Factor.Compute(candles, _factors[f].Parameters);
        }

        _featureMatrix = new float[n][];
        for (var i = 0; i < n; i++)
        {
            var vec = new float[_factors.Count];
            var complete = true;
            for (var f = 0; f < _factors.Count; f++)
            {
                var v = featureSeries[f][i];
                if (!v.HasValue) { complete = false; break; }
                vec[f] = (float)v.Value;
            }
            // Vettore vuoto = warm-up di almeno un fattore: EvaluateSignal risponde Hold.
            _featureMatrix[i] = complete ? vec : [];
        }

        // Regime one-hot (opzionale): stesso percorso causale del dataset (ComputeFeatures +
        // LabelFeaturesAsync), quindi le colonne appese all'inferenza coincidono con quelle viste
        // in addestramento sulla stessa serie. Le righe in warm-up (vettore vuoto) restano vuote.
        if (_regimeCount > 0 && _regimeDetector is not null && _featureExtractor is not null && n > 0)
        {
            var timeframe = candles[0].Timeframe;
            var regimeIds = await RegimeAugmentation.LabelByCandleAsync(_featureExtractor, _regimeDetector, candles, timeframe, ct);
            for (var i = 0; i < n; i++)
            {
                if (_featureMatrix[i].Length == 0) continue;   // warm-up: resta Hold
                _featureMatrix[i] = RegimeAugmentation.Append(_featureMatrix[i], regimeIds[i], _regimeCount);
            }
        }

        // Per i modelli sequenziali: timestamp + passo, per esigere una finestra CONTIGUA nel tempo
        // (stessa semantica di SequenceWindowing in training — nessuna finestra a cavallo di una lacuna).
        if (_sequence is not null)
        {
            _timestamps = new DateTime[n];
            for (var i = 0; i < n; i++) _timestamps[i] = candles[i].TimestampUtc;
            _stepTicks = SequenceWindowing.InferStepTicks(_timestamps);
        }
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        var features = _featureMatrix[index];
        if (features.Length == 0)
        {
            return Signal.Hold; // warm-up: almeno un fattore non ancora calcolabile
        }

        var input = features;
        if (_sequence is not null)
        {
            // Modello sequenziale: costruisci la finestra degli ultimi T timestep (dal più vecchio
            // al più recente). Se anche una sola candela della finestra è in warm-up → Hold.
            var t = _sequence.WindowLength;
            var f = _factors.Count;
            if (index < t - 1) return Signal.Hold;

            // La finestra deve essere contigua nel tempo (nessuna candela mancante), come in training.
            if (!ML.SequenceWindowing.IsContiguous(_timestamps, index - t + 1, index, _stepTicks)) return Signal.Hold;

            var window = new float[t * f];
            for (var k = 0; k < t; k++)
            {
                var vec = _featureMatrix[index - t + 1 + k];
                if (vec.Length == 0) return Signal.Hold;
                Array.Copy(vec, 0, window, k * f, f);
            }
            input = window;
        }

        var predicted = (decimal)_predictor.Predict(input);
        if (predicted > _longThreshold) return Signal.Long;
        if (predicted < -_shortThreshold) return Signal.Short;

        // Predizione vicina a zero (idiom identico a MomentumStrategy): esci dalla posizione.
        var flatBand = Math.Min(_longThreshold, _shortThreshold) / 2m;
        if (Math.Abs(predicted) < flatBand) return Signal.Close;

        return Signal.Hold;
    }
}
