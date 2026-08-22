using Microsoft.EntityFrameworkCore;
using Microsoft.ML;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Validation;
using MathCorrelation = MathNet.Numerics.Statistics.Correlation;
using Statistics = ProcioneMGR.Services.Optimization.Statistics;
using WalkForwardConfiguration = ProcioneMGR.Services.Optimization.WalkForwardConfiguration;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// Stage 7 — trains a return predictor on the SELECTION range (temporal train/test split with
/// a purge gap of <c>forwardHorizon</c> rows at the boundary, so no test label overlaps the
/// training window), persists it as a SavedMlModel and registers it as an "Ml" strategy
/// candidate for the holdout validation.
/// </summary>
public sealed class MlModelTrainingStage(
    IAlphaFactorFactory factorFactory,
    IDatasetBuilder datasetBuilder,
    IDbContextFactory<ApplicationDbContext> dbFactory) : IPipelineStage
{
    public string Name => "MlModelTraining";
    public string DisplayName => "Training modello ML";
    public string Description => "Addestra un predittore dei rendimenti sui fattori selezionati e lo registra come candidato.";
    public int DefaultOrder => 7;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("FeatureEngineering")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("modelType", "Tipo modello", "RandomForest", "Linear | RandomForest | GradientBoosting | Mlp"),
        new("testFraction", "Frazione test temporale", "0.25", "ultima parte del range di selezione riservata al test"),
        new("saveModel", "Salva il modello", "true", "richiede un utente proprietario del run"),
        new("minTestCorrelation", "Correlazione test minima per candidarlo", "0.02", "sotto questa soglia il modello NON diventa un candidato strategia"),
        new("longThreshold", "Soglia Long (rendimento atteso)", "0.002", "parametro della MlStrategy candidata"),
        new("shortThreshold", "Soglia Short (rendimento atteso)", "0.002", ""),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Features is null || ctx.Features.SelectedFactorNames.Count == 0
            ? "Nessun fattore selezionato da FeatureEngineering: impossibile addestrare il modello."
            : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var features = ctx.Features!;
        var modelType = config.GetString("modelType", "RandomForest");
        var horizon = features.ForwardHorizon;

        var candles = await ctx.Candles.GetAsync(features.Symbol, features.Timeframe, ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, ct);
        var factors = features.SelectedFactorNames
            .Select(name =>
            {
                var factor = factorFactory.Create(name);
                var defaults = factor.ParameterDefinitions.ToDictionary(d => d.Key, d => d.Default);
                return new FactorSpec(name, factor, defaults);
            })
            .ToList();

        var dataset = datasetBuilder.Build(candles, factors, horizon);
        if (dataset.RowCount < 200)
        {
            throw new InvalidOperationException($"Dataset troppo piccolo ({dataset.RowCount} righe): servono almeno 200 osservazioni.");
        }

        // Temporal split with a purge gap: the last `horizon` train labels would overlap the
        // first test rows, so we drop them from the training set (poor man's purged CV,
        // exactly the leakage the embargo in PurgedTimeSeriesCv exists to prevent).
        var testFraction = (double)config.GetDecimal("testFraction", 0.25m);
        var splitIdx = (int)(dataset.RowCount * (1 - testFraction));
        var trainEnd = Math.Max(1, splitIdx - horizon);
        var trainIdx = Enumerable.Range(0, trainEnd).ToList();
        var testIdx = Enumerable.Range(splitIdx, dataset.RowCount - splitIdx).ToList();

        var mlContext = new MLContext(seed: ctx.Seed);
        using IReturnPredictor predictor = modelType switch
        {
            "Linear" => new LinearReturnPredictor(),
            "GradientBoosting" => new GradientBoostingReturnPredictor(),
            "Mlp" => new MlpReturnPredictor(),
            _ => new RandomForestReturnPredictor(),
        };
        predictor.Fit(mlContext, dataset.ToDataView(mlContext, trainIdx));

        // Out-of-sample diagnostic: prediction vs realized forward return on the test split.
        var predicted = new double[testIdx.Count];
        var actual = new double[testIdx.Count];
        for (var i = 0; i < testIdx.Count; i++)
        {
            predicted[i] = predictor.Predict(dataset.Rows[testIdx[i]].Features);
            actual[i] = dataset.Rows[testIdx[i]].Label;
        }
        var testCorrelation = testIdx.Count >= 3 ? MathCorrelation.Pearson(predicted, actual) : 0.0;
        if (double.IsNaN(testCorrelation)) testCorrelation = 0.0;

        var importances = predictor
            .ComputeFeatureImportance(mlContext, dataset.ToDataView(mlContext, testIdx), dataset.FeatureNames)
            .Select(f => new FeatureImportanceDto(f.FeatureName, f.MeanDecreaseInRSquared))
            .ToList();

        var output = new MlTrainingOutput
        {
            ModelType = modelType,
            Symbol = features.Symbol,
            Timeframe = features.Timeframe,
            TrainRows = trainIdx.Count,
            TestRows = testIdx.Count,
            TestCorrelation = testCorrelation,
            FeatureImportances = importances,
        };

        var minCorr = (double)config.GetDecimal("minTestCorrelation", 0.02m);
        if (config.GetBool("saveModel", true) && !string.IsNullOrEmpty(ctx.UserId))
        {
            var diventaCandidato = testCorrelation >= minCorr;
            output.SavedMlModelId = await PersistModelAsync(ctx, mlContext, predictor, factors, dataset.RowCount, testCorrelation, modelType, horizon, diventaCandidato, ct);
            ctx.LogLine($"[{Name}] Modello salvato (Id {output.SavedMlModelId}), correlazione test {testCorrelation:F4}.");

            if (diventaCandidato)
            {
                // Register as an "Ml" strategy candidate: the holdout stage validates it with
                // the SAME discipline as every rule-based candidate — the model proposes, the
                // backtest disposes.
                ctx.Candidates.Add(new DiscoveryCandidate
                {
                    StrategyName = "Ml",
                    Symbol = features.Symbol,
                    Timeframe = features.Timeframe,
                    Parameters = new Dictionary<string, decimal>
                    {
                        ["SavedModelId"] = output.SavedMlModelId.Value,
                        ["LongThreshold"] = config.GetDecimal("longThreshold", 0.002m),
                        ["ShortThreshold"] = config.GetDecimal("shortThreshold", 0.002m),
                    },
                    // [2026-08-22] Nessuna discovery ha misurato questo candidato: OutOfSampleSharpe
                    // restava lo 0m del default e finiva in /research come «0,00», indistinguibile
                    // da una misura. 51 righe su 51 in archivio. Ora e' dichiarato assente.
                    WalkForwardSource = DiscoveryCandidate.SourceNone,
                });
            }
            else
            {
                ctx.LogLine($"[{Name}] Correlazione test {testCorrelation:F4} < {minCorr}: modello salvato ma NON candidato.");
            }
        }
        else if (string.IsNullOrEmpty(ctx.UserId))
        {
            ctx.LogLine($"[{Name}] Nessun utente proprietario del run: modello non persistito (SavedMlModel richiede un utente).");
        }

        ctx.MlTraining = output;
    }

    /// <param name="diventaCandidato">
    /// [M2c] Se il modello supererà il gate di correlazione e quindi entrerà nella validazione. Serve
    /// a scrivere subito la provenienza giusta: un modello che NON diventa candidato non verrà mai
    /// giudicato, e senza questa riga resterebbe per sempre un trattino in /registry — indistinguibile
    /// da uno mai valutato per altre ragioni. Al 2026-08-20 era il caso di 114 modelli su 164.
    /// </param>
    private async Task<int> PersistModelAsync(
        PipelineContext ctx, MLContext mlContext, IReturnPredictor predictor, List<FactorSpec> factors,
        int rowCount, double testCorrelation, string modelType, int horizon, bool diventaCandidato, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mlmodel_pipeline_{Guid.NewGuid():N}.zip");
        byte[] bytes;
        try
        {
            predictor.Save(mlContext, tempPath);
            bytes = await File.ReadAllBytesAsync(tempPath, ct);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        var factorsDto = factors
            .Select(f => new SavedFactorSpecDto(f.FeatureName, f.Factor.Name, f.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value)))
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var saved = new SavedMlModel
        {
            UserId = ctx.UserId!,
            Name = $"Pipeline {ctx.RunId.ToString("N")[..8]} {modelType}",
            ModelType = modelType,
            Symbol = ctx.Features!.Symbol,
            Timeframe = ctx.Features.Timeframe,
            TrainingDataFrom = ctx.Ranges.SelectionFrom,
            TrainingDataTo = ctx.Ranges.SelectionTo,
            ForwardHorizon = horizon,
            FactorsJson = System.Text.Json.JsonSerializer.Serialize(factorsDto),
            ModelBytes = bytes,
            TrainRowCount = rowCount,
            TrainCorrelation = testCorrelation,
            // [M2c] La provenienza nasce col modello. Se non diventa candidato, questo è tutto ciò
            // che si saprà mai di lui, e va detto subito invece di lasciare un campo vuoto che
            // significa «non so».
            DeflatedSharpeSource = diventaCandidato ? null : SavedMlModel.DsrSourceNeverValidated,
        };
        db.SavedMlModels.Add(saved);
        await db.SaveChangesAsync(ct);
        return saved.Id;
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var o = ctx.MlTraining ?? new MlTrainingOutput();
        var topFeature = o.FeatureImportances.OrderByDescending(f => f.Importance).FirstOrDefault();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{o.ModelType} su {o.Symbol} {o.Timeframe}: {o.TrainRows} righe train / {o.TestRows} test, correlazione test {o.TestCorrelation:F4}"
                 + (o.SavedMlModelId is int id ? $" (salvato, Id {id})." : " (non salvato).")
                 + (topFeature is null ? "" : $" Feature principale: {topFeature.FeatureName}."),
            Metrics = new()
            {
                ["RigheTrain"] = o.TrainRows,
                ["RigheTest"] = o.TestRows,
                ["CorrelazioneTest"] = (decimal)o.TestCorrelation,
            },
        };
    }
}

/// <summary>
/// Stage 8 — systematic walk-forward strategy discovery over the whole universe, restricted
/// to the SELECTION range. Applies the noise gates of the strategy-hunt reports (minimum OOS
/// Sharpe AND minimum OOS trades — a Sharpe 3 with 2 trades is noise, not edge).
/// </summary>
public sealed class StrategyDiscoveryStage(IStrategyDiscovery discovery) : IPipelineStage
{
    public string Name => "StrategyDiscovery";
    public string DisplayName => "Discovery strategie";
    public string Description => "Spazza strategia × simbolo × timeframe in walk-forward e filtra i candidati robusti.";
    public int DefaultOrder => 8;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("strategies", "Strategie (csv)", "", "vuoto = tutte quelle disponibili"),
        new("topN", "Candidati massimi", "15", ""),
        new("inSampleMonths", "Finestra in-sample (mesi)", "12", ""),
        new("outOfSampleMonths", "Finestra out-of-sample (mesi)", "3", ""),
        new("stepMonths", "Passo walk-forward (mesi)", "3", ""),
        new("embargoBars", "Embargo (barre)", "0", "[T0.1] barre saltate a inizio OOS: blocca il leakage al confine IS/OOS"),
        new("minOosSharpe", "Sharpe OOS minimo", "0.3", "gate anti-rumore del Report Caccia"),
        new("minTrades", "Trade minimi", "12", "candidati con meno trade sono rumore statistico"),
        // [R2] I costi erano dichiarati solo sugli stage di validazione: la discovery, che è dove i
        // candidati vengono SCELTI, girava a sole commissioni di default e senza attrito.
        .. PipelineCosts.ParameterDefinitions,
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Universe.Count == 0 ? "Universo vuoto." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        // [R2] Stessi costi degli stage di validazione: senza, la discovery sceglierebbe i candidati
        // sotto un modello di costo più generoso di quello con cui verranno poi giudicati — e la
        // classifica sarebbe già inquinata prima che il gate onesto la veda.
        var costs = PipelineCosts.FromConfig(config);

        var discoveryConfig = new StrategyDiscoveryConfiguration
        {
            ExchangeName = ctx.ExchangeName,
            Symbols = ctx.Universe.Select(u => u.Symbol).Distinct().ToList(),
            Timeframes = ctx.Universe.Select(u => u.Timeframe).Distinct().ToList(),
            Strategies = config.GetList("strategies"),
            From = ctx.Ranges.SelectionFrom,
            To = ctx.Ranges.SelectionTo,
            InitialCapital = ctx.InitialCapital,
            CommissionPercent = costs.FeePercent,
            SlippagePercent = costs.SlippagePercent,
            // [M3, 2026-08-20] La terza manopola dei costi era DICHIARATA qui (PipelineCosts.
            // ParameterDefinitions la espone da R2) e non propagata: la selezione girava a funding
            // zero mentre la validazione applicava 0,01%/8h. Una manopola visibile che non fa
            // niente è la classe di difetto del Filone E; e l'asimmetria premiava i long-biased.
            FundingRatePercentPer8h = costs.FundingRatePercentPer8h,
            TopN = config.GetInt("topN", 15),
            WalkForward = new WalkForwardConfiguration
            {
                InSampleMonths = config.GetInt("inSampleMonths", 12),
                OutOfSampleMonths = config.GetInt("outOfSampleMonths", 3),
                StepMonths = config.GetInt("stepMonths", 3),
                EmbargoBars = Math.Max(0, config.GetInt("embargoBars", 0)),
            },
        };

        var progress = new Progress<DiscoveryProgress>(p => ctx.LogLine($"[{Name}] {p.Completed}/{p.Total} job — {p.Message}"));
        var result = await discovery.DiscoverAsync(discoveryConfig, progress, ct);

        var minSharpe = config.GetDecimal("minOosSharpe", 0.3m);
        var minTrades = config.GetInt("minTrades", 12);
        // [D-01] Le combinazioni provate — non i soli candidati tenuti — alimentano l'N del gate DSR.
        ctx.TrialsExplored += result.CombinationsTested;

        var kept = result.Candidates
            .Where(c => c.OutOfSampleSharpe >= minSharpe && c.TotalTrades >= minTrades)
            .ToList();

        ctx.Candidates.AddRange(kept);
        ctx.LogLine($"[{Name}] {result.Candidates.Count} candidati grezzi → {kept.Count} oltre i gate (Sharpe OOS ≥ {minSharpe}, trade ≥ {minTrades}).");
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        // [2026-08-22] Chi non ha una misura non compete per il titolo di «migliore»: lo 0m dei
        // candidati Ml batteva qualunque Sharpe negativo e poteva vincere questa riga.
        var best = ctx.Candidates
            .Where(c => c.WalkForwardSource != DiscoveryCandidate.SourceNone)
            .OrderByDescending(c => c.OutOfSampleSharpe)
            .FirstOrDefault();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{ctx.Candidates.Count} candidati totali dopo i gate"
                 + (best is null ? "." : $"; migliore: {best.StrategyName} {best.Symbol} {best.Timeframe} (OOS Sharpe {best.OutOfSampleSharpe:F2})."),
            Metrics = new() { ["Candidati"] = ctx.Candidates.Count },
        };
    }
}

/// <summary>
/// Stage 9 — the verdict: every candidate is backtested on the HOLDOUT range (never seen by
/// any prior decision), with slippage. Survivors must clear the Sharpe/trade-count gates.
/// </summary>
public sealed class HoldoutValidationStage(IBacktestEngine backtest, IDbContextFactory<ApplicationDbContext> dbFactory) : IPipelineStage
{
    public string Name => "HoldoutValidation";
    public string DisplayName => "Validazione holdout";
    public string Description => "Verdetto sul range holdout mai visto: sopravvivono solo i candidati che restano profittevoli.";
    public int DefaultOrder => 9;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("StrategyDiscovery", "MlModelTraining", "CreativeDiscovery")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("minHoldoutSharpe", "Sharpe holdout minimo", "0.5", ""),
        new("minHoldoutTrades", "Trade holdout minimi", "10", ""),
        .. PipelineCosts.ParameterDefinitions,
        new("positionSizePercent", "Size posizione (%)", "10", ""),
        new("minDeflatedSharpe", "Deflated Sharpe minimo", "0.95", "gate anti-overfitting (Bailey-López de Prado): sotto questa soglia il candidato è scartato"),
        new("maxPbo", "PBO massimo di pannello", "0.5", "se la Probability of Backtest Overfitting del batch supera questa soglia l'intero ensemble è bloccato"),
        new("trialCorrelationThreshold", "Soglia ρ cluster tentativi", "0.5", "DSR con N effettivo: candidati con correlazione dei rendimenti holdout ≥ questa soglia contano come un solo test (1 = disattivo, usa N nominale)"),
    ];

    public string? ValidateInput(PipelineContext ctx)
    {
        if (ctx.Candidates.Count == 0)
        {
            return "Nessun candidato da validare (Discovery/MlTraining non hanno prodotto candidati).";
        }

        // [A2, 2026-08-20] Il PBO di pannello di questo stage confronta gli Sharpe PER BARRA dei
        // candidati su partizioni costruite per INDICE, non per data. È corretto solo se tutte le
        // serie hanno la stessa granularità: con timeframe misti nello stesso pannello, «la barra
        // i-esima» significa istanti diversi per candidati diversi e il verdetto — che è BLOCCANTE
        // sull'intero batch — poggia su un confronto che non ha un asse comune. L'ipotesi era
        // implicita e non scritta da nessuna parte; ora è un rifiuto esplicito PRIMA di spendere il
        // run, non un numero prodotto lo stesso. Le campagne reali sono già tutte a timeframe
        // singolo, quindi nessun run esistente cambia: si fissa un vincolo, non un comportamento.
        //
        // Resta aperto l'altro mezzo difetto, che questo gate non copre: a parità di timeframe le
        // serie possono comunque avere lunghezze diverse (listing recenti, buchi di ingestione) e
        // il PBO tronca alla più corta. Quello si chiude allineando il pannello per DATA invece che
        // per indice, e va misurato su un run archiviato prima di cambiare un numero che blocca un
        // batch: qui sotto, intanto, il troncamento viene DICHIARATO nel log del run.
        var timeframes = ctx.Universe.Select(u => u.Timeframe).Distinct().ToList();
        if (timeframes.Count > 1)
        {
            return $"Universo a timeframe MISTI ({string.Join(", ", timeframes.OrderBy(t => t))}): il PBO di pannello "
                 + "confronta Sharpe per barra su partizioni per indice, e con granularità diverse le barre non sono "
                 + "confrontabili. Usa una configurazione per timeframe (è ciò che fanno già le campagne).";
        }

        return null;
    }

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var minSharpe = config.GetDecimal("minHoldoutSharpe", 0.5m);
        var minTrades = config.GetInt("minHoldoutTrades", 10);
        var minDeflatedSharpe = (double)config.GetDecimal("minDeflatedSharpe", 0.95m);
        var maxPbo = (double)config.GetDecimal("maxPbo", 0.5m);
        var trialCorrThreshold = (double)config.GetDecimal("trialCorrelationThreshold", 0.5m);
        var costs = PipelineCosts.FromConfig(config);
        var sizePercent = config.GetDecimal("positionSizePercent", 10m);

        ctx.Validated.Clear();
        // Rendimenti periodici holdout per candidato, allineati per indice a ctx.Validated: alimentano
        // il gate anti-overfitting (DSR per-candidato + PBO di pannello) applicato DOPO il ciclo.
        var holdoutReturns = new List<double[]>(ctx.Candidates.Count);
        // [2026-08-22] Quanti produttori non hanno dichiarato la provenienza dello Sharpe di
        // selezione. Si conta per dirlo: il numero scartato in silenzio sarebbe invisibile.
        var undeclared = 0;

        foreach (var candidate in ctx.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            var validated = new ValidatedCandidate
            {
                StrategyName = candidate.StrategyName,
                Symbol = candidate.Symbol,
                Timeframe = candidate.Timeframe,
                Parameters = new(candidate.Parameters),
                // [2026-08-22] Il numero viaggia con la sua provenienza, e SENZA provenienza non
                // viaggia affatto. Due sorgenti diverse finivano su questa colonna senza dirlo: il
                // MASSIMO su centinaia di combinazioni (discovery classica) e, fino a oggi, una
                // copia arrotondata dello Sharpe di selezione (scoperta creativa: 9.665 righe su
                // 9.665 in ResearchCandidates). I candidati "Ml" non hanno nessuna misura e
                // lasciavano lo 0m del default — 51 righe su 51, indistinguibili da una misura.
                //
                // Un produttore che NON dichiara la provenienza perde il numero, e lo si dice nel
                // log del run: un null silenzioso qui sarebbe la stessa classe di difetto del
                // Filone E, un controllo che rassicura a prescindere.
                WalkForwardOosSharpe = candidate.WalkForwardSource is null
                                       || candidate.WalkForwardSource == DiscoveryCandidate.SourceNone
                    ? null
                    : candidate.OutOfSampleSharpe,
                WalkForwardSource = candidate.WalkForwardSource ?? DiscoveryCandidate.SourceUndeclared,
            };
            if (candidate.WalkForwardSource is null) undeclared++;
            var holdoutRets = Array.Empty<double>();

            try
            {
                var ppy = Statistics.PeriodsPerYear(candidate.Timeframe);

                var selection = await RunAsync(ctx, candidate, ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, costs, sizePercent, ct);
                validated.SelectionSharpe = Statistics.SharpeRatio(selection.EquityCurve, ppy);
                validated.SelectionReturn = selection.TotalReturnPercent;
                validated.SelectionMaxDrawdown = selection.MaxDrawdownPercent;
                validated.SelectionTrades = selection.TotalTrades;

                var holdout = await RunAsync(ctx, candidate, ctx.Ranges.HoldoutFrom, ctx.Ranges.HoldoutTo, costs, sizePercent, ct);
                holdoutRets = PeriodicReturns(holdout.EquityCurve);
                validated.HoldoutSharpe = Statistics.SharpeRatio(holdout.EquityCurve, ppy);
                validated.HoldoutReturn = holdout.TotalReturnPercent;
                validated.HoldoutMaxDrawdown = holdout.MaxDrawdownPercent;
                validated.HoldoutTrades = holdout.TotalTrades;
                validated.HoldoutProfitFactor = ProfitFactor(holdout.Trades);

                if (validated.HoldoutSharpe < minSharpe)
                {
                    validated.RejectReason = $"Sharpe holdout {validated.HoldoutSharpe:F2} < {minSharpe}";
                }
                else if (validated.HoldoutTrades < minTrades)
                {
                    validated.RejectReason = $"Solo {validated.HoldoutTrades} trade in holdout (< {minTrades})";
                }
                validated.Survived = validated.RejectReason is null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                validated.Survived = false;
                validated.RejectReason = $"Backtest fallito: {ex.Message}";
            }

            ctx.Validated.Add(validated);
            holdoutReturns.Add(holdoutRets);
            ctx.LogLine($"[{Name}] {validated.Key}: holdout Sharpe {validated.HoldoutSharpe:F2}, {validated.HoldoutTrades} trade → {(validated.Survived ? "SOPRAVVISSUTO (pre-gate)" : validated.RejectReason)}");
        }

        if (undeclared > 0)
        {
            ctx.LogLine($"[{Name}] {undeclared} candidati senza provenienza dichiarata per lo Sharpe di "
                + "selezione: il numero e' stato SCARTATO. E' un produttore che non imposta "
                + "DiscoveryCandidate.WalkForwardSource.");
        }

        ApplyOverfittingGate(ctx, holdoutReturns, minDeflatedSharpe, maxPbo, trialCorrThreshold);
        await PersistMlDeflatedSharpeAsync(ctx, ct);
    }

    /// <summary>
    /// Scrive l'esito della validazione sui <c>SavedMlModel</c> dei candidati "Ml" (quelli con un
    /// <c>SavedModelId</c>), così anche i modelli addestrati DALLA PIPELINE hanno il DSR che il
    /// <c>ModelRegistry</c> richiede per la promozione a Champion — completa P0-6 sul percorso pipeline.
    ///
    /// <para><b>[M2b, 2026-08-20] Il commento precedente diceva «il DSR viene persistito anche per i
    /// candidati scartati dal gate», e non era vero.</b> Il DSR non veniva nemmeno CALCOLATO per loro:
    /// <c>OverfittingGate.Apply</c> salta i non sopravvissuti (<c>if (!v.Survived) continue;</c>), che è
    /// giusto — la deflazione serve a giudicare chi è arrivato in fondo, e il permutation test costa.
    /// La conseguenza però era invisibile e ha morso: al 2026-08-20, su <b>164 modelli salvati, zero
    /// avevano un DSR</b>. Non per un guasto della catena — 50 candidati "Ml" erano stati regolarmente
    /// validati — ma perché <b>nessuno è mai sopravvissuto</b>: Sharpe holdout da −1,06 a −61,89. La
    /// colonna vuota in /registry si leggeva come «non misurato», mentre la verità era «misurato, e
    /// bocciato prima di arrivare al gate».</para>
    ///
    /// <para>Ora si scrive comunque la PROVENIENZA, anche senza numero: il modello porta con sé il
    /// fatto di essere stato validato e scartato. Il DSR resta null — non si inventa un numero che il
    /// gate non ha prodotto, e soprattutto non si allarga l'insieme che alimenta la fascia grigia
    /// (<see cref="GreyZone.IsGrey"/> guarda proprio <c>DeflatedSharpe</c>).</para>
    /// </summary>
    private async Task PersistMlDeflatedSharpeAsync(PipelineContext ctx, CancellationToken ct)
    {
        // Esito per modello: il DSR se il gate l'ha prodotto, altrimenti null = validato e scartato prima.
        var byModelId = new Dictionary<int, double?>();
        foreach (var v in ctx.Validated)
        {
            if (v.StrategyName != "Ml") continue;
            if (v.Parameters.TryGetValue("SavedModelId", out var idDec)) byModelId[(int)idDec] = v.DeflatedSharpe;
        }
        if (byModelId.Count == 0) return;

        // [M2, 2026-08-20] Insieme al numero si scrive con QUANTI tentativi è stato deflazionato e
        // da quale percorso viene. Senza, la colonna resta un valore senza unità: l'altro scrittore
        // (/ml) mette lì un DSR a N=1 — cioè un PSR, non deflazionato — e il registry li confronta.
        // È l'N EFFETTIVO del gate (nominale × collasso dei correlati), lo stesso che ha prodotto il
        // numero: usare il nominale direbbe una deflazione più severa di quella applicata.
        var trials = ctx.DsrEffectiveTrials > 0 ? ctx.DsrEffectiveTrials : (int?)null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var scartati = 0;
        foreach (var (id, dsr) in byModelId)
        {
            var model = await db.SavedMlModels.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (model is null) continue;

            if (dsr is double misurato)
            {
                model.DeflatedSharpe = misurato;
                model.DeflatedSharpeTrials = trials;
                model.DeflatedSharpeSource = SavedMlModel.DsrSourcePipeline;
            }
            else
            {
                // Nessun numero, ma un fatto: questo modello è passato per la validazione ed è stato
                // fermato prima del gate DSR. Il DSR resta null di proposito.
                model.DeflatedSharpeTrials = null;
                model.DeflatedSharpeSource = SavedMlModel.DsrSourceRejectedBeforeGate;
                scartati++;
            }
        }
        await db.SaveChangesAsync(ct);

        if (scartati > 0)
        {
            ctx.LogLine($"[{Name}] {scartati} modelli ML validati e scartati PRIMA del gate DSR: "
                      + "nessun Deflated Sharpe da scrivere, e il registry lo dichiara invece di mostrare un trattino.");
        }
    }

    /// <summary>
    /// Gate anti-overfitting (López de Prado) sull'INTERO batch, dopo l'holdout — riusa
    /// <see cref="SelectionValidator"/>/<see cref="BacktestOverfitting"/> (stesso rigore di
    /// OptimizationEngine e ModelRegistry):
    ///  - <b>Deflated Sharpe</b> per candidato: probabilità che l'edge holdout sia reale DOPO la
    ///    correzione per il numero di tentativi (tutti i candidati) e la non-normalità dei rendimenti;
    ///    sotto <paramref name="minDeflatedSharpe"/> il candidato è scartato (non entra nell'ensemble).
    ///  - <b>PBO di pannello</b> (CSCV) sui rendimenti holdout: se il PROCESSO di selezione è
    ///    complessivamente overfit (PBO ≥ <paramref name="maxPbo"/>), l'intero batch è bloccato.
    /// I candidati già scartati da Sharpe/trade non vengono ri-valutati.
    /// </summary>
    private void ApplyOverfittingGate(PipelineContext ctx, List<double[]> holdoutReturns, double minDeflatedSharpe, double maxPbo, double trialCorrelationThreshold)
    {
        var result = OverfittingGate.Apply(ctx.Validated, holdoutReturns, minDeflatedSharpe, maxPbo, trialCorrelationThreshold,
            log: m => ctx.LogLine($"[{Name}] {m}"),
            trialsExplored: ctx.TrialsExplored); // [D-01] l'N vero della ricerca, non il Top-N
        // [Fase 3] I numeri del gate restano nel contesto: il riepilogo di stage (e quindi
        // /pipeline) li mostra, invece di lasciarli sepolti in una riga di log.
        ctx.DsrNominalTrials = result.NominalTrials;
        ctx.DsrEffectiveTrials = result.EffectiveTrials;
        ctx.LogLine($"[{Name}] Gate DSR/PBO: {result.Survivors}/{ctx.Validated.Count} sopravvissuti"
                  + (result.PanelPbo is double pp ? $"; PBO pannello {pp:P0}" : "; PBO n/d")
                  + $" (soglie DSR>{minDeflatedSharpe:F2}, PBO<{maxPbo:P0}).");
    }

    /// <summary>Rendimenti periodici (frazione/periodo) di una equity curve, come double, saltando i capitali ≤ 0.</summary>
    private static double[] PeriodicReturns(IReadOnlyList<EquityPoint> equity)
    {
        if (equity is null || equity.Count < 2) return [];
        var returns = new List<double>(equity.Count - 1);
        for (var i = 1; i < equity.Count; i++)
        {
            var prev = equity[i - 1].Capital;
            if (prev > 0m) returns.Add((double)((equity[i].Capital - prev) / prev));
        }
        return returns.ToArray();
    }

    private Task<BacktestResult> RunAsync(PipelineContext ctx, DiscoveryCandidate candidate, DateTime from, DateTime to, PipelineCosts costs, decimal sizePercent, CancellationToken ct)
        => backtest.RunBacktestAsync(costs.ApplyTo(new BacktestConfiguration
        {
            ExchangeName = ctx.ExchangeName,
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe,
            From = from,
            To = to,
            InitialCapital = ctx.InitialCapital,
            PositionSizePercent = sizePercent,
            StrategyName = candidate.StrategyName,
            StrategyParameters = new(candidate.Parameters),
        }), ct);

    /// <summary>Pubblico per testabilità diretta (stesso trattamento di OptimizationEngine.ComboKey).</summary>
    public static decimal ProfitFactor(IReadOnlyList<BacktestTrade> trades)
    {
        var grossProfit = trades.Where(t => t.Pnl > 0m).Sum(t => t.Pnl);
        var grossLoss = Math.Abs(trades.Where(t => t.Pnl < 0m).Sum(t => t.Pnl));
        return grossLoss == 0m ? (grossProfit > 0m ? 999m : 0m) : grossProfit / grossLoss;
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var survivors = ctx.Validated.Count(v => v.Survived);
        var best = ctx.Validated.Where(v => v.Survived).OrderByDescending(v => v.HoldoutSharpe).FirstOrDefault();

        // [Fase 3, D-01 lato UI] Il gate deve dire contro quale N ha deflazionato. Se le
        // combinazioni provate dal run superano i candidati osservati, la divergenza si dichiara:
        // è la differenza fra «15 candidati» e «3.000 prove», cioè il bug D-01 reso visibile.
        var trialsNote = "";
        if (ctx.DsrEffectiveTrials > 0)
        {
            trialsNote = $" Gate DSR su N={ctx.DsrEffectiveTrials} tentativi effettivi";
            if (ctx.TrialsExplored > ctx.Validated.Count)
            {
                trialsNote += $" ({ctx.TrialsExplored} combinazioni provate dal run, {ctx.Validated.Count} candidati osservati: il gate usa le prove, non i soli sopravvissuti)";
            }
            trialsNote += ".";
        }

        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{ctx.Validated.Count} candidati validati, {survivors} sopravvissuti all'holdout"
                 + (best is null ? "." : $"; migliore: {best.Key} (Sharpe holdout {best.HoldoutSharpe:F2}, PF {best.HoldoutProfitFactor:F2}).")
                 + trialsNote,
            Metrics = new()
            {
                ["Validati"] = ctx.Validated.Count,
                ["Sopravvissuti"] = survivors,
                ["CombinazioniProvate"] = ctx.TrialsExplored,
                ["TentativiDsr"] = ctx.DsrEffectiveTrials,
            },
        };
    }
}

/// <summary>
/// Stage 10 — robustness probe on the survivors: stop-loss variants (chosen on SELECTION data
/// only), seeded Monte Carlo of the trade sequence (shutdown guard level), Kelly sizing.
/// </summary>
public sealed class RobustnessProbeStage(
    IBacktestEngine backtest,
    MonteCarloAnalyzer monteCarlo,
    KellyCalculator kelly) : IPipelineStage
{
    public string Name => "RobustnessProbe";
    public string DisplayName => "Prova di robustezza";
    public string Description => "Monte Carlo + varianti stop + Kelly sui sopravvissuti (scelte solo su dati di selezione).";
    public int DefaultOrder => 10;
    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("HoldoutValidation")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("mcShuffles", "Ricombinazioni Monte Carlo", "500", ""),
        new("mcNoisePercent", "Rumore MC (%)", "10", ""),
        new("maxRiskFactor95", "RiskFactor95 massimo", "2.5", "oltre questo il candidato viene scartato"),
        new("stopVariants", "Varianti stop/target (csv)", "base,SL3,SL5,TRAIL5,TP4,TP6,SL3_TP6,SL2_TP5", "base | SLx (stop) | TRAILx (trailing) | TPx (take profit) | combinabili con '_' (es. SL2_TP4). Il take profit viene comunque aggiunto in automatico se assente."),
        .. PipelineCosts.ParameterDefinitions,
        new("positionSizePercent", "Size posizione (%)", "10", ""),
    ];

    public string? ValidateInput(PipelineContext ctx)
        => ctx.Validated.Count(v => v.Survived) == 0 ? "Nessun sopravvissuto all'holdout da sondare." : null;

    public async Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var variants = config.GetList("stopVariants");
        if (variants.Count == 0) variants = ["base"];
        // Valuta sempre anche take profit e combinazioni SL+TP (autonomia: vale anche per config
        // salvate prima di questa feature). Il miglior variant è scelto sui dati di SELEZIONE.
        variants = RobustnessProbeStage.EnsureTakeProfitVariants(variants);
        var costs = PipelineCosts.FromConfig(config);
        var sizePercent = config.GetDecimal("positionSizePercent", 10m);
        var maxRiskFactor = config.GetDecimal("maxRiskFactor95", 2.5m);

        foreach (var survivor in ctx.Validated.Where(v => v.Survived))
        {
            ct.ThrowIfCancellationRequested();

            // Best stop variant: chosen on SELECTION data only (the holdout stays verdict-only).
            var ppy = Statistics.PeriodsPerYear(survivor.Timeframe);
            BacktestResult? bestResult = null;
            var bestSharpe = decimal.MinValue;
            foreach (var variant in variants)
            {
                var cfg = BuildConfig(ctx, survivor, ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, costs, sizePercent);
                ApplyVariant(cfg, variant);
                var result = await backtest.RunBacktestAsync(cfg, ct);
                var sharpe = Statistics.SharpeRatio(result.EquityCurve, ppy);
                if (sharpe > bestSharpe)
                {
                    bestSharpe = sharpe;
                    bestResult = result;
                    survivor.BestStopVariant = variant;
                }
            }

            if (bestResult is null || bestResult.Trades.Count == 0)
            {
                survivor.Survived = false;
                survivor.RejectReason = "Nessun trade nel range di selezione con le varianti stop.";
                continue;
            }

            // Seeded Monte Carlo on the selection trade PnLs: DD95 is the shutdown guard.
            var mc = monteCarlo.Run(
                bestResult.Trades.Select(t => t.Pnl).ToList(),
                new MonteCarloConfig
                {
                    NumberOfShuffles = config.GetInt("mcShuffles", 500),
                    NoisePercent = config.GetDecimal("mcNoisePercent", 10m),
                    Seed = ctx.Seed,
                });
            survivor.MonteCarloRiskFactor95 = mc.RiskFactor95;
            survivor.MonteCarloDrawdown95 = mc.MaxDrawdown95;

            var kellySuggestion = kelly.FromTradeHistory(bestResult.Trades);
            survivor.KellyFraction = kellySuggestion.KellyFraction;

            // Kelly EMPIRICO sui rendimenti reali dei trade: cattura le code grasse (crash) che il
            // Kelly binario/gaussiano ignora. Il sizing usa la METÀ del MINIMO tra i due — la scelta
            // più prudente vince, così non si sovra-scommette sulle code (audit 2026-07 §4). Con pochi
            // trade il campione empirico non è affidabile → si ripiega sul solo Kelly binario.
            var tradeReturns = bestResult.Trades
                .Where(t => t.PnlPercent != 0m)
                .Select(t => (double)(t.PnlPercent / 100m))
                .ToList();
            var empiricalKelly = tradeReturns.Count >= 20
                ? (decimal)KellyCalculator.EmpiricalKelly(tradeReturns)
                : kellySuggestion.KellyFraction;
            survivor.EmpiricalKelly = empiricalKelly;
            survivor.HalfKelly = Math.Min(kellySuggestion.KellyFraction, empiricalKelly) / 2m;

            if (mc.RiskFactor95 > maxRiskFactor)
            {
                survivor.Survived = false;
                survivor.RejectReason = $"MC RiskFactor95 {mc.RiskFactor95:F2}× > {maxRiskFactor}×";
            }
            ctx.LogLine($"[{Name}] {survivor.Key} [{survivor.BestStopVariant}]: RF95 {mc.RiskFactor95:F2}×, Kelly bin {kellySuggestion.KellyFraction:P1}/emp {empiricalKelly:P1} → half {survivor.HalfKelly:P1} → {(survivor.Survived ? "OK" : survivor.RejectReason)}");
        }
    }

    private static BacktestConfiguration BuildConfig(PipelineContext ctx, ValidatedCandidate candidate, DateTime from, DateTime to, PipelineCosts costs, decimal sizePercent)
        => costs.ApplyTo(new()
        {
            ExchangeName = ctx.ExchangeName,
            Symbol = candidate.Symbol,
            Timeframe = candidate.Timeframe,
            From = from,
            To = to,
            InitialCapital = ctx.InitialCapital,
            PositionSizePercent = sizePercent,
            StrategyName = candidate.StrategyName,
            StrategyParameters = new(candidate.Parameters),
        });

    /// <summary>
    /// Applica un variant a una configurazione di backtest. Un variant può COMBINARE più componenti
    /// separate da "_" (es. "SL2_TP4" = stop 2% + take profit 4%). Token riconosciuti: SLx (stop x%),
    /// TRAILx (trailing x%), TPx (take profit x%); "base" (o qualsiasi altro) = nessuno stop lato
    /// motore. Pubblico per testabilità diretta e riuso da EnsembleAssemblyStage/Pipeline.razor.
    /// </summary>
    public static void ApplyVariant(BacktestConfiguration cfg, string variant)
    {
        foreach (var token in variant.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("SL", StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse(token[2..], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sl))
            {
                cfg.StopLossPercent = sl;
            }
            else if (token.StartsWith("TRAIL", StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse(token[5..], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var trail))
            {
                cfg.TrailingStopPercent = trail;
            }
            else if (token.StartsWith("TP", StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse(token[2..], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tp))
            {
                cfg.TakeProfitPercent = tp;
            }
        }
    }

    /// <summary>
    /// Autonomia: garantisce che la prova di robustezza valuti SEMPRE anche il take profit e alcune
    /// combinazioni SL+TP, anche per configurazioni salvate prima di questa feature (che elencano solo
    /// varianti di stop). Se la lista non contiene già un token "TP", vi aggiunge un piccolo grid di
    /// varianti con target — così il motore può scegliere la strategia col miglior stop+target, non
    /// solo il miglior stop. Non tocca le liste che già includono un TP (rispetta la scelta esplicita).
    /// </summary>
    public static List<string> EnsureTakeProfitVariants(List<string> variants)
    {
        var hasTp = variants.Any(v => v.Contains("TP", StringComparison.OrdinalIgnoreCase));
        if (hasTp) return variants;
        return [.. variants, "TP4", "TP6", "SL3_TP6", "SL2_TP5"];
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var survivors = ctx.Validated.Where(v => v.Survived).ToList();
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = $"{survivors.Count} sopravvissuti dopo la prova di robustezza"
                 + (survivors.Count == 0 ? "." : $": {string.Join("; ", survivors.Select(s => $"{s.Key} [{s.BestStopVariant}] RF95 {s.MonteCarloRiskFactor95:F2}× half-Kelly {s.HalfKelly:P1}"))}."),
            Metrics = new()
            {
                ["SopravvissutiFinali"] = survivors.Count,
                ["RF95Medio"] = survivors.Count > 0 ? Math.Round(survivors.Average(s => s.MonteCarloRiskFactor95), 2) : 0m,
            },
        };
    }
}

/// <summary>
/// Gate anti-overfitting universale (P0-3) applicato dall'<see cref="HoldoutValidationStage"/> sull'intero
/// batch di candidati dopo l'holdout. Puro e testabile in isolamento (nessun DB/backtest): muta i flag
/// <see cref="ValidatedCandidate.Survived"/>/<see cref="ValidatedCandidate.DeflatedSharpe"/>/
/// <see cref="ValidatedCandidate.PanelPbo"/> dei candidati passati. Riusa la libreria di rigore
/// (<see cref="SelectionValidator"/>, <see cref="BacktestOverfitting"/>) — stesso pattern di
/// OptimizationEngine e ModelRegistry.
/// </summary>
public static class OverfittingGate
{
    /// <summary>
    /// [Fase 3, D-01 lato UI] Oltre all'esito, il gate DICHIARA i suoi numeri: quante combinazioni
    /// nominali ha considerato e quanti tentativi effettivi ha usato dopo il collasso dei
    /// correlati. Prima vivevano solo in una riga di log: per giudicare un DSR serve sapere contro
    /// quale N è stato deflazionato.
    /// </summary>
    public readonly record struct Result(int Survivors, double? PanelPbo, int NominalTrials, int EffectiveTrials);

    /// <param name="validated">Candidati validati (con SelectionSharpe/Survived già impostati).</param>
    /// <param name="holdoutReturns">Rendimenti periodici holdout, allineati per indice a <paramref name="validated"/>.</param>
    /// <param name="minDeflatedSharpe">Sotto (o pari a) questa soglia il candidato è scartato (default 0.95).</param>
    /// <param name="maxPbo">Se il PBO di pannello ≥ questa soglia l'intero batch è bloccato (default 0.5).</param>
    /// <param name="trialCorrelationThreshold">Soglia ρ per il conteggio EFFETTIVO dei tentativi nel DSR:
    /// candidati con correlazione dei rendimenti holdout ≥ soglia contano come un solo test (R1.4). 1 =
    /// disattivo (usa N nominale). Default 0.5.</param>
    /// <param name="maxPermutationPValue">[T1.5] Soglia sul p-value del test di permutazione a blocchi:
    /// candidati con p ≥ soglia vengono scartati. Default 1.0 = NON blocca (solo informativo): il
    /// p-value va prima osservato sul campo, poi promosso a gate — la stessa strada fatta dal DSR.</param>
    /// <param name="log">Callback opzionale di logging (una riga per evento).</param>
    /// <param name="trialsExplored">[D-01, Fase 1 PRD-RISANAMENTO] Le combinazioni REALMENTE provate
    /// dal run (discovery × parametri × composizioni), non i soli sopravvissuti al Top-N. Il DSR
    /// assolve o condanna in base a QUANTE volte si è provato: con 3.000 combinazioni la soglia SR*
    /// è il doppio che con 15 (docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §2), e prima questo numero —
    /// già misurato da StrategyDiscoveryEngine — finiva solo nella UI. 0 = ignoto: si usa il solo
    /// conteggio dei candidati (comportamento storico).</param>
    public static Result Apply(
        IReadOnlyList<ValidatedCandidate> validated,
        IReadOnlyList<double[]> holdoutReturns,
        double minDeflatedSharpe,
        double maxPbo,
        double trialCorrelationThreshold = 0.5,
        double maxPermutationPValue = 1.0,
        Action<string>? log = null,
        int trialsExplored = 0)
    {
        ArgumentNullException.ThrowIfNull(validated);
        ArgumentNullException.ThrowIfNull(holdoutReturns);

        // Sharpe OOS di selezione annualizzati di TUTTI i candidati = distribuzione dei tentativi.
        var trialSharpes = validated.Select(v => v.SelectionSharpe).ToList();

        // N EFFETTIVO, in due mosse dichiarate:
        //  1. [D-01] N NOMINALE = quante combinazioni la ricerca ha DAVVERO provato, non i soli
        //     candidati tenuti dal Top-N. PowerCheckStage lo dice nel suo stesso help text:
        //     «sottostimarlo gonfia la potenza dichiarata» — ed era esattamente ciò che accadeva
        //     qui, con N ≤ topN (15) contro migliaia di combinazioni esplorate.
        //  2. (R1.4) COLLASSO dei correlati: i candidati osservati con rendimenti holdout correlati
        //     (ρ ≥ soglia) contano come un solo test. Il rapporto di collasso osservato sui
        //     candidati si applica all'intera popolazione esplorata: se il 40% dei sopravvissuti è
        //     ridondante, si assume la stessa ridondanza fra gli esplorati — meglio un'estensione
        //     dichiarata che ignorare 99 prove su 100.
        // Con trialsExplored = 0 il calcolo coincide col comportamento storico (min con nominale).
        var nominalTrials = Math.Max(validated.Count, trialsExplored);
        var effectiveAmongObserved = Math.Min(
            EffectiveTrials.Count(holdoutReturns.Select(r => (IReadOnlyList<double>)r).ToList(), trialCorrelationThreshold),
            validated.Count);
        var collapseRatio = validated.Count > 0 ? (double)effectiveAmongObserved / validated.Count : 1.0;
        var trials = Math.Max(1, (int)Math.Round(nominalTrials * collapseRatio));
        if (trials != validated.Count)
        {
            log?.Invoke(
                $"N tentativi DSR: {validated.Count} candidati, {trialsExplored} combinazioni esplorate → " +
                $"{nominalTrials} nominali × collasso {collapseRatio:F2} (cluster ρ≥{trialCorrelationThreshold:F2}) = {trials} effettivi.");
        }

        // PBO di pannello sui rendimenti holdout (serie ≥ 10 punti per il CSCV a 10 partizioni).
        double? panelPbo = null;
        var panel = holdoutReturns.Where(r => r.Length >= 10).Select(r => (IReadOnlyList<double>)r).ToList();
        if (panel.Count >= 2)
        {
            // [A2, 2026-08-20] Il CSCV taglia tutte le serie alla più corta. Non è un dettaglio
            // interno: se un candidato ha una serie molto più breve (listing recente, buco di
            // ingestione), il verdetto sull'INTERO pannello — che può azzerare il batch — poggia su
            // quella frazione di finestra, e per giunta le serie lunghe vengono troncate in CODA
            // mentre la corta copre un altro tratto di calendario. Finché il pannello non sarà
            // allineato per DATA (lavoro a sé: cambia il numero su ogni configurazione, va misurato
            // su un run archiviato prima), il minimo onesto è dire quanto si sta buttando via.
            var minLen = panel.Min(p => p.Count);
            var maxLen = panel.Max(p => p.Count);
            if (minLen < maxLen)
            {
                log?.Invoke(
                    $"PBO di pannello: serie di lunghezza disomogenea ({minLen}..{maxLen} barre), il CSCV le tronca a {minLen} "
                    + $"— il verdetto poggia sul {(double)minLen / maxLen:P0} della finestra più lunga, allineato per indice e non per data.");
            }

            try { panelPbo = BacktestOverfitting.ProbabilityOfOverfitting(panel, partitions: 10).ProbabilityOfBacktestOverfitting; }
            catch (ArgumentException) { panelPbo = null; } // serie troppo corte per le partizioni
        }

        for (var i = 0; i < validated.Count; i++)
        {
            var v = validated[i];
            v.PanelPbo = panelPbo;
            if (!v.Survived) continue;

            var rets = i < holdoutReturns.Count ? holdoutReturns[i] : [];
            if (rets.Length < 2 || trials < 1) continue;

            var ppy = Statistics.PeriodsPerYear(v.Timeframe);
            var validation = SelectionValidator.Validate(trialSharpes, rets, ppy, trials);
            v.DeflatedSharpe = double.IsNaN(validation.DeflatedSharpe) ? null : validation.DeflatedSharpe;

            // [T1.5] Permutation test a BLOCCHI DI SEGNO lungo il tempo (l'unica randomizzazione
            // onesta su questi dati): p = probabilità di uno Sharpe holdout almeno così alto senza
            // alcuna deriva. Complementare al DSR (che corregge per il test multiplo, non per la
            // struttura temporale). Riportato sempre; blocca solo se maxPermutationPValue < 1.
            if (rets.Length >= 20)
            {
                var perm = Validation.PermutationTest.SharpeSignificance(rets);
                v.PermutationPValue = perm.PValue;
            }

            if (v.DeflatedSharpe is double dsr && dsr <= minDeflatedSharpe)
            {
                v.Survived = false;
                v.RejectReason = $"DSR {dsr:F3} ≤ {minDeflatedSharpe:F2} (probabile overfitting da selezione)";
                log?.Invoke($"{v.Key}: scartato dal gate — {v.RejectReason}.");
            }

            if (v.Survived && v.PermutationPValue is double pperm && pperm >= maxPermutationPValue)
            {
                v.Survived = false;
                v.RejectReason = $"permutation p {pperm:F3} ≥ {maxPermutationPValue:F2} (Sharpe holdout compatibile col rumore)";
                log?.Invoke($"{v.Key}: scartato dal gate — {v.RejectReason}.");
            }
        }

        // [F5b, 2026-08-20] LA BANDA GRIGIA PUÒ ESSERE IRRAGGIUNGIBILE, e nessuno lo diceva.
        //
        // La fascia grigia ammette due strade: bocciatura per finestra corta, oppure DSR dentro
        // [0,80; 0,95). Misurato sui 30 giorni al 2026-08-20: 402 candidati sono arrivati al gate
        // DSR e il **massimo osservato è 0,773** — sotto il pavimento. La banda non è «rara»: è
        // sopra il tetto di ciò che questo assetto produce, perché il DSR è deflazionato su
        // migliaia di tentativi e SR* cresce con la dimensione della ricerca. Risultato: OGNI
        // grigio arriva dalla finestra corta, e la seconda strada è un ramo morto che nessuna
        // superficie dichiarava. È la famiglia «gate senza strumento» già pagata il 2026-07-28:
        // chiedersi «dove si legge il numero, e può esistere in questo assetto?».
        //
        // Qui non si cambia nessuna soglia — è una decisione del proprietario. Si dice il fatto,
        // con il numero accanto, in modo che la scelta sia informata invece che rimandata.
        var dsrMisurati = validated.Where(v => v.DeflatedSharpe is not null).Select(v => v.DeflatedSharpe!.Value).ToList();
        if (dsrMisurati.Count > 0)
        {
            var dsrMax = dsrMisurati.Max();
            if (dsrMax < GreyZone.DsrFloor)
            {
                log?.Invoke(
                    $"Banda grigia IRRAGGIUNGIBILE in questo run: DSR massimo {dsrMax:F3} su {dsrMisurati.Count} candidati misurati, "
                  + $"sotto il pavimento {GreyZone.DsrFloor:F2}. Nessun candidato può entrare in fascia grigia per DSR: "
                  + "gli unici grigi possibili sono quelli bocciati per finestra corta.");
            }
        }

        // Processo di selezione complessivamente overfit ⇒ blocca l'ensemble.
        if (panelPbo is double pbo && pbo >= maxPbo)
        {
            foreach (var v in validated.Where(v => v.Survived))
            {
                v.Survived = false;
                v.RejectReason = $"PBO di pannello {pbo:P0} ≥ {maxPbo:P0}: selezione inaffidabile";
            }
            log?.Invoke($"PBO di pannello {pbo:P0} ≥ soglia {maxPbo:P0}: ENSEMBLE BLOCCATO (nessun sopravvissuto).");
        }

        return new Result(validated.Count(v => v.Survived), panelPbo, nominalTrials, trials);
    }
}
