using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// <see cref="ISentimentScorer"/> a inferenza LOCALE via ONNX Runtime (PRD-ONNX-SENTIMENT-PILOT,
/// Livello 1): carica il modello .onnx del pilota (addestrato in ML.NET dentro l'app, esportato
/// con ConvertToOnnx — filiera 100% C#, zero Python) e lo esegue in-process sulla CPU. Nessuna
/// chiave API, nessun costo per chiamata, nessun rate limit: è il contraltare locale dello
/// scorer LLM, dietro lo stesso contratto.
///
/// <para>La parte testuale (tokenizzazione + hashing) NON sta nel modello: è
/// <see cref="HashingTextVectorizer"/>, lo stesso codice usato in addestramento — la parità
/// train/inference è garantita per costruzione, non da un vocabolario da tenere allineato.</para>
///
/// <para>Se il file del modello manca (mai addestrato, o percorso cambiato) lo scorer NON è un
/// errore: ripiega sul lessico e lo dice nel log una volta per percorso. Il modello si addestra
/// dal pannello in /sentiment (OnnxSentimentPilotService), che al termine chiama
/// <see cref="Reload"/>.</para>
/// </summary>
public sealed class OnnxSentimentScorer(
    IOptionsMonitor<SentimentOptions> options,
    KeywordSentimentScorer fallback,
    ILogger<OnnxSentimentScorer> logger,
    IHostEnvironment? env = null) : ISentimentScorer, IDisposable
{
    private readonly object _gate = new();
    private InferenceSession? _session;
    private string? _inputName;
    private string? _outputName;
    private IReadOnlyList<(string Name, int[] Dims)> _extraFloatInputs = [];
    private string? _loadedFromPath;
    private string? _warnedMissingPath;

    /// <summary>True se un modello è caricato e pronto a inferire (per la UI).</summary>
    public bool IsAvailable => EnsureSession() is not null;

    /// <summary>
    /// PERCHÉ l'ultimo caricamento è fallito (null se mai fallito o poi riuscito). Un badge
    /// "non disponibile" senza causa è un controllo che non sa dire di no: la UI e il pilota
    /// lo mostrano.
    /// </summary>
    public string? LastLoadError { get; private set; }

    /// <summary>Percorso assoluto del file modello secondo la configurazione corrente.</summary>
    public string ResolvedModelPath
    {
        get
        {
            var configured = options.CurrentValue.OnnxModelPath;
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(env?.ContentRootPath ?? Directory.GetCurrentDirectory(), configured);
        }
    }

    public Task<decimal> ScoreAsync(string title, string? summary, CancellationToken ct = default)
    {
        var session = EnsureSession();
        if (session is null)
        {
            return Task.FromResult(fallback.Score(title, summary));
        }

        try
        {
            var vector = HashingTextVectorizer.Vectorize(title, summary);
            var tensor = new DenseTensor<float>(vector, [1, HashingTextVectorizer.Dimension]);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName!, tensor) };
            // Gli input float residui del grafo (es. una "Label" sopravvissuta alla potatura
            // dell'export) si alimentano con zeri: ORT pretende OGNI input dichiarato, e a
            // inferenza quelle colonne non esistono per definizione.
            foreach (var (name, dims) in _extraFloatInputs)
            {
                var shape = dims.Select(d => d < 0 ? 1 : d).ToArray();
                inputs.Add(NamedOnnxValue.CreateFromTensor(name,
                    new DenseTensor<float>(new float[shape.Aggregate(1, (a, b) => a * b)], shape)));
            }
            using var results = session.Run(inputs, [_outputName!]);
            var raw = results[0].AsEnumerable<float>().First();
            return Task.FromResult(Math.Clamp((decimal)raw, -1m, 1m));
        }
        catch (Exception ex)
        {
            // Un guasto d'inferenza non deve mai fermare la sync: si ripiega e si dice da dove.
            logger.LogWarning(ex, "Inferenza ONNX fallita: ripiego sul lessico.");
            return Task.FromResult(fallback.Score(title, summary));
        }
    }

    /// <summary>Ricarica il modello dal disco (dopo un nuovo addestramento, o un cambio percorso).</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _loadedFromPath = null;
            _warnedMissingPath = null;
        }
    }

    private InferenceSession? EnsureSession()
    {
        var path = ResolvedModelPath;
        lock (_gate)
        {
            if (_session is not null && _loadedFromPath == path)
            {
                return _session;
            }

            _session?.Dispose();
            _session = null;
            _loadedFromPath = null;

            if (!File.Exists(path))
            {
                if (_warnedMissingPath != path)
                {
                    logger.LogInformation(
                        "Modello ONNX del sentiment assente in {Path}: lo scorer Onnx ripiega sul lessico finché non viene addestrato (pannello in /sentiment).", path);
                    _warnedMissingPath = path;
                }
                return null;
            }

            try
            {
                var session = new InferenceSession(path);

                // Introspezione invece di nomi cablati: l'export ML.NET nomina input/output secondo
                // le colonne dell'IDataView, e un rename silenzioso a monte deve fallire QUI a voce
                // alta, non produrre punteggi da un tensore sbagliato. L'input delle feature è
                // quello che si chiama "Features" o, in mancanza, il float più largo; ogni ALTRO
                // input float va registrato per alimentarlo con zeri a inferenza.
                var floatInputs = session.InputMetadata
                    .Where(kv => kv.Value.ElementType == typeof(float))
                    .ToList();
                var input = floatInputs.FirstOrDefault(kv => kv.Key.Equals("Features", StringComparison.OrdinalIgnoreCase));
                if (input.Key is null)
                {
                    input = floatInputs
                        .OrderByDescending(kv => kv.Value.Dimensions.Aggregate(1L, (a, d) => a * Math.Max(1, d)))
                        .FirstOrDefault();
                }
                var output = session.OutputMetadata.FirstOrDefault(kv =>
                                 kv.Value.ElementType == typeof(float) && kv.Key.Contains("Score", StringComparison.OrdinalIgnoreCase));
                if (output.Key is null)
                {
                    output = session.OutputMetadata.FirstOrDefault(kv => kv.Value.ElementType == typeof(float));
                }

                if (input.Key is null || output.Key is null)
                {
                    var detail = $"nessun input/output float riconoscibile (input: {string.Join(",", session.InputMetadata.Keys)}; output: {string.Join(",", session.OutputMetadata.Keys)})";
                    session.Dispose();
                    LastLoadError = detail;
                    logger.LogError("Modello ONNX in {Path}: {Detail} — scorer non disponibile.", path, detail);
                    return null;
                }

                _session = session;
                _inputName = input.Key;
                _outputName = output.Key;
                _extraFloatInputs = floatInputs
                    .Where(kv => kv.Key != input.Key)
                    .Select(kv => (kv.Key, kv.Value.Dimensions.ToArray()))
                    .ToList();
                _loadedFromPath = path;
                LastLoadError = null;
                logger.LogInformation("Modello ONNX del sentiment caricato da {Path} (input '{Input}', output '{Output}'{Extra}).",
                    path, _inputName, _outputName,
                    _extraFloatInputs.Count == 0 ? "" : $", input extra a zero: {string.Join(",", _extraFloatInputs.Select(e => e.Name))}");
                return _session;
            }
            catch (Exception ex)
            {
                LastLoadError = $"{ex.GetType().Name}: {ex.Message}";
                logger.LogError(ex, "Caricamento del modello ONNX da {Path} fallito: scorer non disponibile (ripiego sul lessico).", path);
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
