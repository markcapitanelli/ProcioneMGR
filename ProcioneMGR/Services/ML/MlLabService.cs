using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.AlphaMining;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.ML;

/// <summary>
/// Fotografia completa del form di <c>MlLab.razor</c> — usata sia per i preset/memoria
/// dell'ultima configurazione, sia come input dei metodi di orchestrazione (train/backtest/save).
/// </summary>
public sealed record MlConfigSnapshot(
    ExchangeName Exchange, string Symbol, string Timeframe, DateTime From, DateTime To,
    int TrainSplitPercent, int ForwardHorizon,
    IReadOnlyList<string> Factors, IReadOnlyList<int> SavedFactorIds,
    string ModelType, IReadOnlyList<string> StackBaseModels, StackingMode StackMode, int AttnWindow, int AttnEmbed,
    decimal LongThreshold, decimal ShortThreshold, decimal InitialCapital, decimal PositionSizePercent, decimal FeePercent,
    // [1.V] In coda e con default per compatibilità con i preset salvati prima del campo.
    MlTargetKind TargetKind = MlTargetKind.ForwardReturn);

/// <summary>Esito di un'azione con messaggio per l'operatore.</summary>
public sealed record MlActionResult(string Message, bool IsError)
{
    public static MlActionResult Ok(string message) => new(message, false);
    public static MlActionResult Error(string message) => new(message, true);
}

/// <summary>Esito del caricamento di un modello salvato: oltre al messaggio, i campi form che la UI deve riallineare.</summary>
public sealed record MlLoadResult(string Message, bool IsError, string? Symbol, string? Timeframe, string? ModelType,
    // [1.V fase 2] In coda e opzionale: la UI riallinea anche il target del modello caricato.
    MlTargetKind TargetKind = MlTargetKind.ForwardReturn);

/// <summary>
/// Orchestrazione estratta da <c>Components/Pages/MlLab.razor</c> (P1-5, PRD-CONSOLIDAMENTO-
/// ARCHITETTURA.md §3.3): validazione, addestramento/backtest ML, tracking degli esperimenti,
/// CRUD dei modelli salvati e (de)serializzazione validata dei preset — tutta la logica che prima
/// viveva nel blocco <c>@code</c> del componente senza test indipendenti da Blazor. Il componente
/// resta responsabile solo di ciò che è intrinsecamente Blazor: binding del form, ciclo di vita
/// (<c>OnInitializedAsync</c>/<c>Dispose</c>), spinner <c>_busy</c>/<c>_stage</c>, <c>StateHasChanged</c>.
///
/// Lo stato della "sessione di modello" (predittore addestrato, fattori, candele di test, risultato
/// del backtest, liste salvate) vive qui perché è stato applicativo condiviso fra i passi
/// train→backtest→save, non stato di UI. Registrato Scoped: in Blazor Server uno scope = un circuito,
/// quindi un'istanza per sessione utente, come il componente che la consuma.
/// </summary>
public sealed class MlLabService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IBacktestEngine engine,
    IAlphaFactorFactory factorFactory,
    IDatasetBuilder datasetBuilder,
    IExperimentTracker tracker) : IDisposable
{
    // --- Stato caricato / di sessione (letto dal markup, mai scritto dal componente) -----------

    public IReadOnlyList<string> KnownSymbols { get; private set; } = [];
    public List<SavedMlModel> SavedModels { get; private set; } = [];
    public List<SavedFactor> SavedFactors { get; private set; } = [];

    private IReturnPredictor? _predictor;
    public bool HasTrainedModel => _predictor is not null;
    public List<FactorSpec>? TrainedFactors { get; private set; }
    public List<OhlcvData>? TestCandles { get; private set; }
    public int TrainRowCount { get; private set; }
    public double TrainCorrelation { get; private set; }
    public List<FeatureImportance> FeatureImportance { get; private set; } = [];

    /// <summary>
    /// [1.V fase 2] Il target del modello IN SESSIONE (addestrato o caricato): è questo — non lo
    /// stato del form, che può cambiare dopo — a decidere cosa ha senso farci (backtest direzionale
    /// vs valutazione della previsione di vol) e cosa viene persistito al salvataggio.
    /// </summary>
    public MlTargetKind SessionTargetKind { get; private set; } = MlTargetKind.ForwardReturn;

    /// <summary>Orizzonte forward del modello in sessione (allineato a <see cref="SessionTargetKind"/>).</summary>
    public int SessionForwardHorizon { get; private set; }

    public BacktestResult? Result { get; private set; }
    public TearsheetMetrics? Tearsheet { get; private set; }
    public List<IndicatorSeries> EquitySeries { get; private set; } = [];

    /// <summary>[1.V fase 2] Esito della valutazione vol (QLIKE/MSE vs EWMA/naive) del modello in sessione.</summary>
    public VolForecastEvaluation? VolEvaluation { get; private set; }

    // --- Caricamento iniziale ------------------------------------------------------------------

    /// <summary>Simboli disponibili + fattori minati e modelli salvati dell'utente (OnInitializedAsync).</summary>
    public async Task LoadInitialDataAsync(string? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        KnownSymbols = await db.OhlcvData.Select(c => c.Symbol).Distinct().OrderBy(s => s).ToListAsync(ct);
        SavedFactors = userId is null
            ? []
            : await db.SavedFactors.Where(f => f.UserId == userId).OrderByDescending(f => f.CreatedAtUtc).ToListAsync(ct);
        await LoadSavedModelsListAsync(userId, ct);
    }

    public async Task LoadSavedModelsListAsync(string? userId, CancellationToken ct = default)
    {
        if (userId is null) { SavedModels = []; return; }
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        SavedModels = await db.SavedMlModels
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync(ct);
    }

    // --- Addestramento -------------------------------------------------------------------------

    /// <summary>
    /// Addestra un predittore sul periodo di train (split cronologico) e ne calcola la diagnostica
    /// in-sample. Su successo popola lo stato di sessione (predittore, fattori, candele di test).
    /// La validazione dei dati (numero candele, praticabilità dello split, fattori, finestre) è qui.
    /// </summary>
    public async Task<MlActionResult> TrainAsync(MlConfigSnapshot cfg, string? userId, CancellationToken ct = default)
    {
        ResetTrainedModel();
        ResetBacktest();

        var fromUtc = DateTime.SpecifyKind(cfg.From, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(cfg.To.AddDays(1).AddSeconds(-1), DateTimeKind.Utc);
        var symbol = cfg.Symbol.Trim();

        List<OhlcvData> candles;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            candles = await db.OhlcvData
                .Where(c => c.Symbol == symbol && c.Timeframe == cfg.Timeframe
                            && c.TimestampUtc >= fromUtc && c.TimestampUtc <= toUtc)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }

        if (candles.Count < 50)
            return MlActionResult.Error("Dati insufficienti nel range selezionato (servono almeno 50 candele).");

        var splitIndex = candles.Count * Math.Clamp(cfg.TrainSplitPercent, 10, 90) / 100;
        var trainCandles = candles.Take(splitIndex).ToList();
        var testCandles = candles.Skip(splitIndex).ToList();
        if (trainCandles.Count < 30 || testCandles.Count < 10)
            return MlActionResult.Error("Split train/test non praticabile: allarga il range o cambia la percentuale.");

        var factors = cfg.Factors.Select(name =>
        {
            var factor = factorFactory.Create(name);
            var defaults = factor.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);
            return new FactorSpec(name, factor, defaults);
        }).ToList();

        // Fattori minati (Alpha Mining) scelti dall'utente: il nome "expr:…" li fa ricostruire dalla
        // factory al reload, quindi il round-trip del modello resta valido come per gli altri fattori.
        foreach (var sf in SavedFactors.Where(f => cfg.SavedFactorIds.Contains(f.Id)))
        {
            var factor = factorFactory.Create(AlphaExpressionFactor.NamePrefix + sf.Expression);
            factors.Add(new FactorSpec(sf.Name, factor, new Dictionary<string, decimal>()));
        }

        if (factors.Count == 0)
            return MlActionResult.Error("Seleziona almeno un fattore (alpha o minato).");

        var dataset = datasetBuilder.Build(trainCandles, factors, cfg.ForwardHorizon, targetKind: cfg.TargetKind);
        if (dataset.RowCount < 20)
            return MlActionResult.Error("Troppo poche righe utilizzabili dopo il warm-up dei fattori: allarga il periodo di train.");

        if (cfg.ModelType == "Stacked" && cfg.StackBaseModels.Count == 0)
            return MlActionResult.Error("Seleziona almeno un modello base per lo stacking.");

        var mlContext = new MLContext(seed: 42);
        IReturnPredictor predictor = CreatePredictor(cfg);

        // I modelli sequenziali (attention) si addestrano su finestre di T candele: la stessa
        // matrice di fattori, vista come sequenze. Gli altri restano puntuali.
        var mlData = predictor is ISequencePredictor seq ? SequenceWindowing.Build(dataset, seq.WindowLength) : dataset;
        if (mlData.RowCount < 20)
        {
            predictor.Dispose();
            return MlActionResult.Error("Troppe poche finestre dopo il windowing: riduci la finestra o allarga il periodo di train.");
        }

        try
        {
            var trainDataView = mlData.ToDataView(mlContext);
            predictor.Fit(mlContext, trainDataView);

            // Diagnostica in-sample: correlazione fra predizione e rendimento forward realizzato.
            var predicted = new double[mlData.RowCount];
            var actual = new double[mlData.RowCount];
            for (var i = 0; i < mlData.RowCount; i++)
            {
                predicted[i] = predictor.Predict(mlData.Rows[i].Features);
                actual[i] = mlData.Rows[i].Label;
            }

            _predictor = predictor;
            TrainedFactors = factors;
            TestCandles = testCandles;
            TrainRowCount = mlData.RowCount;
            TrainCorrelation = Correlation.Pearson(predicted, actual);
            FeatureImportance = predictor.ComputeFeatureImportance(mlContext, trainDataView, mlData.FeatureNames).ToList();
            SessionTargetKind = cfg.TargetKind;
            SessionForwardHorizon = cfg.ForwardHorizon;
        }
        catch
        {
            predictor.Dispose();
            throw;
        }

        // Experiment tracking (osservabilità confrontabile): registra questo training accanto
        // a ogni altro run. Best-effort: non fa mai cadere il training.
        var mlRunId = await tracker.SafeStartRunAsync(
            "MlTraining",
            $"{cfg.ModelType} · {symbol} · {factors.Count} fattori",
            new
            {
                ModelType = cfg.ModelType,
                Symbol = symbol,
                Timeframe = cfg.Timeframe,
                ForwardHorizon = cfg.ForwardHorizon,
                TrainSplitPercent = cfg.TrainSplitPercent,
                From = cfg.From,
                To = cfg.To,
                FactorCount = factors.Count,
                Factors = factors.Select(f => f.FeatureName).OrderBy(n => n).ToList(),
            },
            symbol, cfg.Timeframe, userId);
        await tracker.SafeLogMetricsAsync(mlRunId, new Dictionary<string, decimal>
        {
            ["TrainRows"] = dataset.RowCount,
            ["FeatureCount"] = dataset.FeatureCount,
            ["TrainCorrelation"] = (decimal)TrainCorrelation,
        });
        await tracker.SafeCompleteAsync(mlRunId, "Completed");

        return MlActionResult.Ok($"Modello addestrato su {dataset.RowCount} righe (train). Ora esegui il backtest sul periodo di test, mai visto in addestramento.");
    }

    private IReturnPredictor CreatePredictor(MlConfigSnapshot cfg) => cfg.ModelType switch
    {
        "Stacked" => new StackedReturnPredictor(cfg.StackBaseModels.OrderBy(b => b).ToList(), cfg.StackMode),
        "Attention" => new AttentionReturnPredictor(windowLength: cfg.AttnWindow, embedDim: cfg.AttnEmbed),
        "RandomForest" => new RandomForestReturnPredictor(),
        "GradientBoosting" => new GradientBoostingReturnPredictor(),
        "Mlp" => new MlpReturnPredictor(),
        _ => new LinearReturnPredictor(),
    };

    // --- Backtest out-of-sample ----------------------------------------------------------------

    /// <summary>Backtesta il modello addestrato sul periodo di test mai visto. Popola Result/Tearsheet/EquitySeries.</summary>
    public async Task<MlActionResult> BacktestAsync(MlConfigSnapshot cfg, CancellationToken ct = default)
    {
        // [1.V fase 2] La semantica del MODELLO IN SESSIONE (non del form) decide: un modello di
        // rischio confrontato con soglie long/short darebbe segnali privi di senso (vol alta ≠ compra).
        if (SessionTargetKind != MlTargetKind.ForwardReturn)
            return MlActionResult.Error(
                $"Il modello in sessione predice '{SessionTargetKind}', non un rendimento: il backtest direzionale " +
                "non ha senso. Usa 'Valuta previsione di vol' per il confronto QLIKE/MSE con la baseline EWMA.");

        if (_predictor is null || TrainedFactors is null || TestCandles is null)
            return MlActionResult.Error("Nessun modello addestrato da backtestare.");

        ResetBacktest();

        var strategy = new MlStrategy(_predictor, TrainedFactors);
        var config = new BacktestConfiguration
        {
            ExchangeName = cfg.Exchange.ToString(),
            Symbol = cfg.Symbol.Trim(),
            Timeframe = cfg.Timeframe,
            InitialCapital = cfg.InitialCapital,
            PositionSizePercent = cfg.PositionSizePercent,
            FeePercent = cfg.FeePercent,
            StrategyName = "Ml",
            StrategyParameters = new Dictionary<string, decimal>
            {
                ["LongThreshold"] = cfg.LongThreshold,
                ["ShortThreshold"] = cfg.ShortThreshold,
            },
        };

        var result = await engine.RunBacktestAsync(config, TestCandles, strategy, ct);
        Result = result;
        Tearsheet = Statistics.ComputeTearsheet(result.EquityCurve, result.Trades, Statistics.PeriodsPerYear(cfg.Timeframe));
        EquitySeries =
        [
            new IndicatorSeries
            {
                Title = "Equity",
                Color = "#2962FF",
                Type = IndicatorSeriesType.Line,
                Points = result.EquityCurve
                    .Select(p => new IndicatorPoint(
                        new DateTimeOffset(DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                        (double)p.Capital))
                    .ToList(),
            },
        ];

        return MlActionResult.Ok($"Backtest out-of-sample completato su {result.CandlesEvaluated} candele mai viste in training.");
    }

    // --- Valutazione previsione di volatilità (1.V fase 2) --------------------------------------

    /// <summary>
    /// Valuta il modello di volatilità in sessione sul periodo di test MAI visto in addestramento,
    /// contro le due baseline senza ML (EWMA λ=0,94 e naive "vol passata"). QLIKE è il verdetto:
    /// se il modello non batte l'EWMA out-of-sample, il vol-targeting deve continuare a usare la
    /// misura semplice — questo confronto è esattamente la precondizione dichiarata dall'item 1.V
    /// per instradare la previsione ML nel sizing.
    /// </summary>
    public Task<MlActionResult> EvaluateVolForecastAsync(CancellationToken ct = default)
    {
        VolEvaluation = null;

        if (_predictor is null || TrainedFactors is null || TestCandles is null)
            return Task.FromResult(MlActionResult.Error("Nessun modello in sessione da valutare."));
        if (SessionTargetKind != MlTargetKind.ForwardRealizedVol)
            return Task.FromResult(MlActionResult.Error(
                "La valutazione confronta la vol PER-BARRA prevista con EWMA e vol passata: serve il target " +
                "'Volatilità realizzata forward' (ForwardRealizedVol)."));

        // Dataset sul test: stesse feature e stesso target del training, su candele mai viste.
        var dataset = datasetBuilder.Build(TestCandles, TrainedFactors, SessionForwardHorizon, targetKind: SessionTargetKind);
        var mlData = _predictor is ISequencePredictor seq ? SequenceWindowing.Build(dataset, seq.WindowLength) : dataset;
        if (mlData.RowCount < 10)
            return Task.FromResult(MlActionResult.Error("Troppo poche righe di test per una valutazione sensata (servono almeno 10)."));

        // Baseline allineate per timestamp: EWMA e naive calcolate CAUSALMENTE su tutte le candele
        // di test, poi campionate sulle righe del dataset.
        var ewmaSeries = VolForecastEvaluator.EwmaPerBarVol(TestCandles);
        var naiveSeries = VolForecastEvaluator.PastRealizedVol(TestCandles, SessionForwardHorizon);
        var indexByTs = new Dictionary<DateTime, int>(TestCandles.Count);
        for (var i = 0; i < TestCandles.Count; i++) indexByTs[TestCandles[i].TimestampUtc] = i;

        var model = new List<double?>(mlData.RowCount);
        var ewma = new List<double?>(mlData.RowCount);
        var naive = new List<double?>(mlData.RowCount);
        var actual = new List<double?>(mlData.RowCount);
        for (var r = 0; r < mlData.RowCount; r++)
        {
            ct.ThrowIfCancellationRequested();
            if (!indexByTs.TryGetValue(mlData.Timestamps[r], out var i)) continue;
            // Riga inclusa solo se TUTTE le previsioni esistono: stesso campione per i tre contendenti.
            if (ewmaSeries[i] is not { } e || naiveSeries[i] is not { } nv) continue;
            model.Add(_predictor.Predict(mlData.Rows[r].Features));
            ewma.Add(e);
            naive.Add(nv);
            actual.Add(mlData.Rows[r].Label);
        }

        var (mQ, mMse, rows) = VolForecastEvaluator.Score(model, actual);
        if (rows < 10)
            return Task.FromResult(MlActionResult.Error("Troppo poche righe confrontabili (baseline in warm-up o vol realizzata nulla)."));
        var (eQ, eMse, _) = VolForecastEvaluator.Score(ewma, actual);
        var (nQ, nMse, _) = VolForecastEvaluator.Score(naive, actual);

        VolEvaluation = new VolForecastEvaluation(rows, mQ, eQ, nQ, mMse, eMse, nMse);
        var verdict = VolEvaluation.ModelBeatsEwma
            ? "il modello batte l'EWMA out-of-sample: l'instradamento nel vol-targeting è giustificabile."
            : "il modello NON batte l'EWMA: il vol-targeting deve restare sulla misura semplice.";
        return Task.FromResult(MlActionResult.Ok($"Valutazione su {rows} barre di test: {verdict}"));
    }

    // --- Persistenza modelli -------------------------------------------------------------------

    public async Task<MlActionResult> SaveModelAsync(MlConfigSnapshot cfg, string modelName, string? userId, CancellationToken ct = default)
    {
        // [1.V fase 2] La guardia di semantica si è SPOSTATA dal salvataggio al consumo: il
        // TargetKind viene persistito sul modello e fatto rispettare nei punti direzionali
        // (MlModelLoader.LoadAsync, ModelRegistry Gate 0). Un modello di rischio si può quindi
        // salvare e riusare, ma non potrà mai alimentare segnali long/short né diventare Champion.
        if (_predictor is null || TrainedFactors is null || userId is null)
            return MlActionResult.Error("Nessun modello addestrato da salvare.");
        if (string.IsNullOrWhiteSpace(modelName))
            return MlActionResult.Error("Inserisci un nome per salvare il modello.");

        var tempPath = Path.Combine(Path.GetTempPath(), $"mlmodel_save_{Guid.NewGuid():N}.zip");
        try
        {
            _predictor.Save(new MLContext(), tempPath);
            var bytes = await File.ReadAllBytesAsync(tempPath, ct);

            var factorsDto = TrainedFactors
                .Select(f => new SavedFactorSpecDto(f.FeatureName, f.Factor.Name, new Dictionary<string, decimal>(f.Parameters)))
                .ToList();

            // Deflated Sharpe del modello dal backtest sul range di test (se disponibile): è ciò che
            // il ModelRegistry richiede per la promozione a Champion. Singolo track ⇒ un solo trial.
            // [1.V fase 2] Solo per i modelli direzionali: per un modello di rischio non esiste un
            // backtest direzionale, e un DSR nullo lo tiene fuori dal Champion per costruzione.
            var deflatedSharpe = SessionTargetKind == MlTargetKind.ForwardReturn
                ? Statistics.DeflatedSharpeSingleTrack(Result?.EquityCurve, Statistics.PeriodsPerYear(cfg.Timeframe))
                : null;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.SavedMlModels.Add(new SavedMlModel
            {
                UserId = userId,
                Name = modelName.Trim(),
                ModelType = cfg.ModelType,
                Symbol = cfg.Symbol.Trim(),
                Timeframe = cfg.Timeframe,
                TrainingDataFrom = cfg.From,
                TrainingDataTo = cfg.To,
                ForwardHorizon = SessionForwardHorizon > 0 ? SessionForwardHorizon : cfg.ForwardHorizon,
                TargetKind = SessionTargetKind.ToString(),
                FactorsJson = JsonSerializer.Serialize(factorsDto),
                ModelBytes = bytes,
                TrainRowCount = TrainRowCount,
                TrainCorrelation = TrainCorrelation,
                DeflatedSharpe = deflatedSharpe,
            });
            await db.SaveChangesAsync(ct);

            await LoadSavedModelsListAsync(userId, ct);
            return MlActionResult.Ok("Modello salvato.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Carica un modello salvato e ne prepara il backtest sull'intervallo <paramref name="from"/>/
    /// <paramref name="to"/> (nessuno split: il modello è già addestrato). Restituisce symbol/timeframe/
    /// modelType del modello perché la UI riallinei i campi del form.
    /// </summary>
    public async Task<MlLoadResult> LoadSavedModelAsync(int id, DateTime from, DateTime to, string? userId, CancellationToken ct = default)
    {
        ResetBacktest();
        ResetTrainedModel();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var saved = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (saved is null)
            return new MlLoadResult("Modello non trovato.", IsError: true, null, null, null);

        IReturnPredictor predictor = saved.ModelType switch
        {
            "Stacked" => new StackedReturnPredictor(),
            "Attention" => new AttentionReturnPredictor(), // config reale letta dal blob al Load
            "RandomForest" => new RandomForestReturnPredictor(),
            "GradientBoosting" => new GradientBoostingReturnPredictor(),
            "Mlp" => new MlpReturnPredictor(),
            _ => new LinearReturnPredictor(),
        };

        var tempPath = Path.Combine(Path.GetTempPath(), $"mlmodel_load_{Guid.NewGuid():N}.zip");
        try
        {
            await File.WriteAllBytesAsync(tempPath, saved.ModelBytes, ct);
            predictor.Load(new MLContext(), tempPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        var factorsDto = JsonSerializer.Deserialize<List<SavedFactorSpecDto>>(saved.FactorsJson) ?? [];
        var factors = factorsDto
            .Select(dto => new FactorSpec(dto.FeatureName, factorFactory.Create(dto.FactorName), dto.Parameters))
            .ToList();

        // Nessuno split: l'intero intervallo Da/A selezionato è usato direttamente come test out-of-sample.
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.AddDays(1).AddSeconds(-1), DateTimeKind.Utc);
        List<OhlcvData> candles;
        await using (var db2 = await dbFactory.CreateDbContextAsync(ct))
        {
            candles = await db2.OhlcvData
                .Where(c => c.Symbol == saved.Symbol && c.Timeframe == saved.Timeframe
                            && c.TimestampUtc >= fromUtc && c.TimestampUtc <= toUtc)
                .OrderBy(c => c.TimestampUtc)
                .ToListAsync(ct);
        }

        _predictor = predictor;
        TrainedFactors = factors;
        TestCandles = candles;
        TrainRowCount = saved.TrainRowCount;
        TrainCorrelation = saved.TrainCorrelation;
        FeatureImportance = []; // non ricalcolata: servirebbe il dataset di training originale

        // [1.V fase 2] La semantica viaggia col modello: un TargetKind non riconosciuto (versione
        // futura?) degrada a ForwardReturn SOLO se dichiarato tale, mai in silenzio.
        SessionTargetKind = Enum.TryParse<MlTargetKind>(saved.TargetKind, out var tk) ? tk : MlTargetKind.ForwardReturn;
        SessionForwardHorizon = saved.ForwardHorizon;

        var warn = candles.Count == 0 ? " ATTENZIONE: nessuna candela nell'intervallo Da/A selezionato." : "";
        var usage = SessionTargetKind == MlTargetKind.ForwardReturn
            ? "verrà usato per il backtest"
            : "verrà usato per la valutazione della previsione di vol (QLIKE/MSE vs EWMA)";
        var message = $"Modello '{saved.Name}' caricato (addestrato su {saved.TrainingDataFrom:yyyy-MM-dd}→{saved.TrainingDataTo:yyyy-MM-dd}). " +
                      $"L'intervallo Da/A sopra ({from:yyyy-MM-dd}→{to:yyyy-MM-dd}, {candles.Count} candele) {usage}: " +
                      $"assicurati che sia successivo al periodo di addestramento originale.{warn}";
        return new MlLoadResult(message, IsError: candles.Count == 0, saved.Symbol, saved.Timeframe, saved.ModelType, SessionTargetKind);
    }

    public async Task DeleteSavedModelAsync(int id, string? userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var saved = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (saved is null) return;
        db.SavedMlModels.Remove(saved);
        await db.SaveChangesAsync(ct);
        await LoadSavedModelsListAsync(userId, ct);
    }

    // --- Preset: (de)serializzazione VALIDATA ---------------------------------------------------

    /// <summary>Forma JSON dei preset — invariata rispetto al blocco @code originale, così i preset già salvati restano leggibili (enum come stringa).</summary>
    private sealed record ConfigDto(
        string Exchange, string Symbol, string Timeframe, DateTime From, DateTime To,
        int TrainSplitPercent, int ForwardHorizon, List<string> Factors, List<int> SavedFactorIds,
        string ModelType, List<string> StackBaseModels, string StackMode, int AttnWindow, int AttnEmbed,
        decimal LongThreshold, decimal ShortThreshold, decimal InitialCapital,
        decimal PositionSizePercent, decimal FeePercent);

    public string SerializeConfig(MlConfigSnapshot cfg) => JsonSerializer.Serialize(new ConfigDto(
        cfg.Exchange.ToString(), cfg.Symbol.Trim(), cfg.Timeframe, cfg.From, cfg.To,
        cfg.TrainSplitPercent, cfg.ForwardHorizon, cfg.Factors.ToList(), cfg.SavedFactorIds.ToList(),
        cfg.ModelType, cfg.StackBaseModels.ToList(), cfg.StackMode.ToString(), cfg.AttnWindow, cfg.AttnEmbed,
        cfg.LongThreshold, cfg.ShortThreshold, cfg.InitialCapital, cfg.PositionSizePercent, cfg.FeePercent));

    /// <summary>
    /// Applica un preset alla configurazione <paramref name="current"/>: ogni campo con vincolo di
    /// catalogo (exchange/timeframe/modello/base-stacking/fattori) è preso dal preset SOLO se ancora
    /// valido, altrimenti si tiene il valore corrente; i campi liberi (date, soglie, split…) sono
    /// sempre applicati. JSON malformato ⇒ <paramref name="current"/> invariato. Stessa semantica del
    /// vecchio <c>ApplyConfigJson</c>, ora testabile in isolamento.
    /// </summary>
    public MlConfigSnapshot ApplyConfig(string json, MlConfigSnapshot current)
    {
        ConfigDto? dto;
        try { dto = JsonSerializer.Deserialize<ConfigDto>(json); }
        catch (JsonException) { return current; }
        if (dto is null) return current;

        var exchange = Enum.TryParse<ExchangeName>(dto.Exchange, ignoreCase: true, out var ex) ? ex : current.Exchange;
        var symbol = string.IsNullOrWhiteSpace(dto.Symbol) ? current.Symbol : dto.Symbol;
        var timeframe = Timeframes.Supported.ContainsKey(dto.Timeframe) ? dto.Timeframe : current.Timeframe;

        // Solo nomi/ID che esistono ancora: cataloghi e fattori minati possono cambiare nel tempo.
        var factors = dto.Factors.Where(f => factorFactory.Prototypes.Any(p => p.Name == f)).ToList();
        var savedFactorIds = dto.SavedFactorIds.Where(id => SavedFactors.Any(s => s.Id == id)).ToList();

        var modelType = ReturnPredictorCatalog.BaseTypes.Contains(dto.ModelType) || dto.ModelType is "Stacked" or "Attention"
            ? dto.ModelType : current.ModelType;
        var stackBaseModels = dto.StackBaseModels.Where(m => ReturnPredictorCatalog.BaseTypes.Contains(m)).ToList();
        var stackMode = Enum.TryParse<StackingMode>(dto.StackMode, out var sm) ? sm : current.StackMode;

        return current with
        {
            Exchange = exchange,
            Symbol = symbol,
            Timeframe = timeframe,
            From = dto.From.Date,
            To = dto.To.Date,
            TrainSplitPercent = dto.TrainSplitPercent,
            ForwardHorizon = dto.ForwardHorizon,
            Factors = factors,
            SavedFactorIds = savedFactorIds,
            ModelType = modelType,
            StackBaseModels = stackBaseModels,
            StackMode = stackMode,
            AttnWindow = dto.AttnWindow,
            AttnEmbed = dto.AttnEmbed,
            LongThreshold = dto.LongThreshold,
            ShortThreshold = dto.ShortThreshold,
            InitialCapital = dto.InitialCapital,
            PositionSizePercent = dto.PositionSizePercent,
            FeePercent = dto.FeePercent,
        };
    }

    /// <summary>
    /// Link a Optimization precompilata: strategia ML + questo modello, periodo che parte dalla fine
    /// del training (le soglie Long/Short si scelgono su dati che il modello non ha visto).
    /// </summary>
    public static string OptimizationHandoffUrl(SavedMlModel m) =>
        "optimization?strategy=Ml"
        + $"&model={m.Id}"
        + $"&symbol={Uri.EscapeDataString(m.Symbol)}"
        + $"&timeframe={Uri.EscapeDataString(m.Timeframe)}"
        + $"&from={m.TrainingDataTo:yyyy-MM-dd}"
        + $"&to={DateTime.UtcNow:yyyy-MM-dd}";

    // --- Reset interni -------------------------------------------------------------------------

    private void ResetTrainedModel()
    {
        _predictor?.Dispose();
        _predictor = null;
        TrainedFactors = null;
        TestCandles = null;
        TrainRowCount = 0;
        TrainCorrelation = 0;
        FeatureImportance = [];
        SessionTargetKind = MlTargetKind.ForwardReturn;
        SessionForwardHorizon = 0;
    }

    private void ResetBacktest()
    {
        Result = null;
        Tearsheet = null;
        EquitySeries = [];
        VolEvaluation = null;
    }

    public void Dispose() => _predictor?.Dispose();
}
