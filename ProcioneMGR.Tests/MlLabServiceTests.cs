using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Exchanges;
using ProcioneMGR.Services.Experiments;
using ProcioneMGR.Services.Indicators;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.ML.Shap;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// Test dell'orchestrazione estratta da <c>MlLab.razor</c> (P1-5, PRD-CONSOLIDAMENTO-ARCHITETTURA.md
/// §3.3): prima di questa estrazione tutta la logica — validazione, addestramento/backtest, CRUD dei
/// modelli salvati e (de)serializzazione validata dei preset — viveva nel blocco <c>@code</c> del
/// componente, senza test indipendenti da Blazor. Qui è esercitata direttamente su
/// <see cref="MlLabService"/> con le dipendenze reali (factory alpha, dataset builder, backtest engine,
/// Postgres effimero) e un tracker no-op, incluso il round-trip completo train→backtest→save→load.
/// </summary>
[Collection("Postgres")]
public sealed class MlLabServiceTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public MlLabServiceTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    private const string UserA = "user-a";
    private const string UserB = "user-b";

    /// <summary>Tracker no-op: le chiamate SafeStartRun/SafeLogMetrics/SafeComplete non devono influire sull'esito.</summary>
    private sealed class NoopTracker : IExperimentTracker
    {
        public Task<Guid> StartRunAsync(string kind, string name, object? parameters, string? symbol = null,
            string? timeframe = null, string? createdBy = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task LogMetricsAsync(Guid runId, IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(Guid runId, string status, string? errorLog = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    /// <summary>
    /// [D1] Rilevatore di regimi finto: decide se esiste un modello attivo e con quali etichette,
    /// così i test esercitano sia la lente K-means sia il ripiego senza addestrare un K-means vero.
    /// </summary>
    private sealed class FakeRegimeDetector(
        ProcioneMGR.Services.Regime.RegimeModel? activeModel,
        Func<List<ProcioneMGR.Services.Regime.MarketFeatures>, List<ProcioneMGR.Services.Regime.MarketFeatures>>? labeller = null,
        bool throwOnLoad = false) : ProcioneMGR.Services.Regime.IRegimeDetector
    {
        public Task<ProcioneMGR.Services.Regime.RegimeModel> TrainAsync(
            ProcioneMGR.Services.Regime.TrainingConfiguration config, bool activate = true, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task ActivateModelAsync(ProcioneMGR.Services.Regime.RegimeModel model, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<ProcioneMGR.Services.Regime.MarketFeatures>> LabelFeaturesAsync(
            List<ProcioneMGR.Services.Regime.MarketFeatures> features, CancellationToken ct = default)
            => Task.FromResult(labeller?.Invoke(features) ?? features);

        public Task<List<ProcioneMGR.Services.Regime.MarketFeatures>> LabelFeaturesAsync(
            List<ProcioneMGR.Services.Regime.MarketFeatures> features,
            string symbol, string timeframe, CancellationToken ct = default)
            => LabelFeaturesAsync(features, ct);

        public Task<ProcioneMGR.Services.Regime.RegimeModel?> LoadLatestModelAsync(CancellationToken ct = default)
            => Task.FromResult(activeModel);

        public Task<ProcioneMGR.Services.Regime.RegimeModel?> LoadActiveModelAsync(
            string symbol, string timeframe, CancellationToken ct = default)
            => throwOnLoad
                ? throw new InvalidOperationException("modello di regime illeggibile")
                : Task.FromResult(activeModel);
    }

    /// <summary>
    /// [D1] Estrattore finto: una <c>MarketFeatures</c> per candela, con il timestamp giusto. Qui
    /// interessa la SCELTA della lente, non il calcolo delle feature di mercato (che ha già i suoi test).
    /// </summary>
    private sealed class FakeFeatureExtractor : ProcioneMGR.Services.Regime.IMarketFeatureExtractor
    {
        public Task<List<ProcioneMGR.Services.Regime.MarketFeatures>> ExtractFeaturesAsync(
            string exchangeName, string symbol, string timeframe, DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(new List<ProcioneMGR.Services.Regime.MarketFeatures>());

        public List<ProcioneMGR.Services.Regime.MarketFeatures> ComputeFeatures(
            IReadOnlyList<OhlcvData> candles, string timeframe, CancellationToken ct = default)
            => candles.Select(c => new ProcioneMGR.Services.Regime.MarketFeatures
            {
                Timestamp = c.TimestampUtc,
                Price = c.Close,
            }).ToList();
    }

    // --- Setup ---------------------------------------------------------------------------------

    private async Task<(MlLabService Svc, IDbContextFactory<ApplicationDbContext> Db)> BuildAsync(
        bool ensureSchema = true,
        ProcioneMGR.Services.Regime.IRegimeDetector? regimeDetector = null,
        ProcioneMGR.Services.Regime.IMarketFeatureExtractor? featureExtractor = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        services.AddSingleton<ITechnicalIndicatorsService, TechnicalIndicatorsService>();
        services.AddSingleton<IAlphaFactorFactory, AlphaFactorFactory>();
        services.AddScoped<IBacktestEngine>(sp => new BacktestEngine(
            sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            null!, // strategyFactory: non usato dall'overload con strategia esplicita ("Ml")
            sp.GetRequiredService<ITechnicalIndicatorsService>(),
            sp.GetRequiredService<IAlphaFactorFactory>(),
            sp.GetRequiredService<ILogger<BacktestEngine>>()));
        _provider = services.BuildServiceProvider();

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        if (ensureSchema)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
        }

        var svc = new MlLabService(
            dbFactory,
            _provider.GetRequiredService<IBacktestEngine>(),
            _provider.GetRequiredService<IAlphaFactorFactory>(),
            new DatasetBuilder(),
            new NoopTracker(),
            // [E-04] Il catalogo VERO sul dbFactory del test, come in produzione.
            new ProcioneMGR.Services.MarketData.SymbolCatalog(dbFactory),
            regimeDetector,
            featureExtractor);
        return (svc, dbFactory);
    }

    private static MlConfigSnapshot DefaultSnapshot(string symbol = "TEST/USDT", string timeframe = "1h") => new(
        ExchangeName.Binance, symbol, timeframe,
        new DateTime(2023, 12, 1), new DateTime(2024, 6, 1),
        70, 1,
        ["Momentum"], [],
        "Linear", ["Linear", "RandomForest"], StackingMode.StackedRidge, 8, 16,
        0.005m, 0.005m, 10_000m, 20m, 0.1m);

    private static List<decimal> SyntheticMomentumCloses(int n, int seed)
    {
        var rnd = new Random(seed);
        var closes = new List<decimal> { 100m };
        for (var i = 1; i < n; i++)
        {
            var prevRet = i >= 2 ? (double)(closes[i - 1] / closes[i - 2] - 1m) : 0.0;
            var next = (double)closes[i - 1] * (1.0 + 0.5 * prevRet + (rnd.NextDouble() - 0.5) * 0.01);
            closes.Add((decimal)Math.Max(1.0, next));
        }
        return closes;
    }

    private static List<OhlcvData> MakeCandles(IReadOnlyList<decimal> closes, string symbol, string timeframe)
    {
        var list = new List<OhlcvData>(closes.Count);
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < closes.Count; i++)
        {
            var c = closes[i];
            var prev = i > 0 ? closes[i - 1] : c;
            list.Add(new OhlcvData
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = t0.AddHours(i),
                Open = prev,
                High = Math.Max(prev, c) * 1.01m,
                Low = Math.Min(prev, c) * 0.99m,
                Close = c,
                Volume = 100m,
            });
        }
        return list;
    }

    private async Task SeedCandlesAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int count, string symbol = "TEST/USDT", string timeframe = "1h")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.OhlcvData.AddRange(MakeCandles(SyntheticMomentumCloses(count, seed: 7), symbol, timeframe));
        await db.SaveChangesAsync();
    }

    private static async Task SeedUserAsync(IDbContextFactory<ApplicationDbContext> dbFactory, string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = userId + "@t.io", Email = userId + "@t.io" });
            await db.SaveChangesAsync();
        }
    }

    // --- Preset: (de)serializzazione validata --------------------------------------------------

    [Fact]
    public async Task ApplyConfig_RoundTrip_AppliesSerializedValues()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var saved = DefaultSnapshot() with
        {
            Exchange = ExchangeName.Binance,
            Timeframe = "4h",
            ModelType = "RandomForest",
            StackMode = StackingMode.InverseRmse,
            Factors = ["Momentum", "RsiFactor"],
            From = new DateTime(2024, 2, 1),
            To = new DateTime(2024, 4, 1),
            LongThreshold = 0.02m,
            TrainSplitPercent = 65,
        };

        var applied = svc.ApplyConfig(svc.SerializeConfig(saved), DefaultSnapshot() with { Timeframe = "1h", ModelType = "Linear" });

        Assert.Equal("4h", applied.Timeframe);
        Assert.Equal("RandomForest", applied.ModelType);
        Assert.Equal(StackingMode.InverseRmse, applied.StackMode);
        Assert.Equal(new[] { "Momentum", "RsiFactor" }, applied.Factors);
        Assert.Equal(new DateTime(2024, 2, 1), applied.From);
        Assert.Equal(0.02m, applied.LongThreshold);
        Assert.Equal(65, applied.TrainSplitPercent);
    }

    [Fact]
    public async Task ApplyConfig_MalformedJson_ReturnsCurrentUnchanged()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var current = DefaultSnapshot();
        Assert.Same(current, svc.ApplyConfig("{ this is not json", current));
    }

    [Fact]
    public async Task ApplyConfig_DropsInvalidCatalogValues_KeepsCurrentForScalars_AppliesFreeFields()
    {
        var (svc, _) = await BuildAsync(ensureSchema: false);
        var current = DefaultSnapshot() with { Timeframe = "1h", ModelType = "Linear" };

        var raw = svc.SerializeConfig(DefaultSnapshot() with
        {
            Factors = ["Momentum", "NonEsiste"],
            StackBaseModels = ["Linear", "Fasullo"],
            ModelType = "ModelloInventato",
            Timeframe = "99z",
            From = new DateTime(2024, 5, 1),
            TrainSplitPercent = 55,
        });
        var applied = svc.ApplyConfig(raw, current);

        Assert.Equal(new[] { "Momentum" }, applied.Factors);          // fattore inesistente scartato
        Assert.Equal(new[] { "Linear" }, applied.StackBaseModels);    // modello base fasullo scartato
        Assert.Equal("Linear", applied.ModelType);                    // modello non valido → resta il corrente
        Assert.Equal("1h", applied.Timeframe);                        // timeframe non supportato → resta il corrente
        Assert.Equal(new DateTime(2024, 5, 1), applied.From);         // campo libero: sempre applicato
        Assert.Equal(55, applied.TrainSplitPercent);
    }

    // --- Caricamento iniziale ------------------------------------------------------------------

    [Fact]
    public async Task LoadInitialDataAsync_LoadsSymbolsAndOnlyOwnFactorsAndModels()
    {
        var (svc, db) = await BuildAsync();
        await SeedUserAsync(db, UserA);
        await SeedUserAsync(db, UserB);
        await SeedCandlesAsync(db, 60, "BTC/USDT");
        await SeedCandlesAsync(db, 60, "ETH/USDT");

        await using (var ctx = await db.CreateDbContextAsync())
        {
            ctx.SavedFactors.Add(new SavedFactor { UserId = UserA, Name = "Fa", Symbol = "BTC/USDT", Timeframe = "1h", Expression = "close", CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            ctx.SavedFactors.Add(new SavedFactor { UserId = UserA, Name = "Fb", Symbol = "BTC/USDT", Timeframe = "1h", Expression = "open", CreatedAtUtc = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            ctx.SavedFactors.Add(new SavedFactor { UserId = UserB, Name = "Fother", Symbol = "BTC/USDT", Timeframe = "1h", Expression = "high", CreatedAtUtc = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc) });
            ctx.SavedMlModels.Add(new SavedMlModel { UserId = UserA, Name = "Ma", ModelType = "Linear", Symbol = "BTC/USDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [], CreatedAtUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            ctx.SavedMlModels.Add(new SavedMlModel { UserId = UserB, Name = "Mother", ModelType = "Linear", Symbol = "BTC/USDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [], CreatedAtUtc = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            await ctx.SaveChangesAsync();
        }

        await svc.LoadInitialDataAsync(UserA);

        Assert.Equal(new[] { "BTC/USDT", "ETH/USDT" }, svc.KnownSymbols);
        Assert.Equal(new[] { "Fb", "Fa" }, svc.SavedFactors.Select(f => f.Name)); // ordine: CreatedAtUtc desc
        Assert.Equal("Ma", Assert.Single(svc.SavedModels).Name);                  // solo il modello di UserA
    }

    // --- Guardie di addestramento --------------------------------------------------------------

    [Fact]
    public async Task TrainAsync_InsufficientCandles_ReturnsError_NoModel()
    {
        var (svc, db) = await BuildAsync();
        await SeedCandlesAsync(db, 30); // < 50

        var res = await svc.TrainAsync(DefaultSnapshot(), UserA);

        Assert.True(res.IsError);
        Assert.Contains("almeno 50", res.Message);
        Assert.False(svc.HasTrainedModel);
    }

    [Fact]
    public async Task TrainAsync_NoFactorsSelected_ReturnsError()
    {
        var (svc, db) = await BuildAsync();
        await SeedCandlesAsync(db, 120);

        var res = await svc.TrainAsync(DefaultSnapshot() with { Factors = [], SavedFactorIds = [] }, UserA);

        Assert.True(res.IsError);
        Assert.Contains("almeno un fattore", res.Message);
        Assert.False(svc.HasTrainedModel);
    }

    // --- Round-trip completo: train → backtest → save → load -----------------------------------

    [Fact]
    public async Task TrainThenBacktestThenSaveThenLoad_HappyPath()
    {
        var (svc, db) = await BuildAsync();
        await SeedUserAsync(db, UserA);
        await SeedCandlesAsync(db, 400);
        var snap = DefaultSnapshot() with { Factors = ["Momentum"], ModelType = "Linear" };

        // Train
        var train = await svc.TrainAsync(snap, UserA);
        Assert.False(train.IsError);
        Assert.True(svc.HasTrainedModel);
        Assert.True(svc.TrainRowCount > 0);
        Assert.NotNull(svc.TestCandles);

        // Backtest out-of-sample
        var bt = await svc.BacktestAsync(snap);
        Assert.False(bt.IsError);
        Assert.NotNull(svc.Result);
        Assert.NotNull(svc.Tearsheet);
        Assert.NotEmpty(svc.EquitySeries);

        // Save
        var save = await svc.SaveModelAsync(snap, "Modello di prova", UserA);
        Assert.False(save.IsError);
        var savedModel = Assert.Single(svc.SavedModels);
        Assert.Equal("Modello di prova", savedModel.Name);

        // Load (round-trip dal blob): riallinea symbol/timeframe/modelType e ripopola lo stato
        var load = await svc.LoadSavedModelAsync(savedModel.Id, snap.From, snap.To, UserA);
        Assert.False(load.IsError);
        Assert.Equal("TEST/USDT", load.Symbol);
        Assert.Equal("1h", load.Timeframe);
        Assert.Equal("Linear", load.ModelType);
        Assert.True(svc.HasTrainedModel);
    }

    // --- Cancellazione con isolamento per utente -----------------------------------------------

    [Fact]
    public async Task DeleteSavedModelAsync_IgnoresOtherUsersModel_ThenDeletesOwn()
    {
        var (svc, db) = await BuildAsync();
        await SeedUserAsync(db, UserA);
        int aId;
        await using (var ctx = await db.CreateDbContextAsync())
        {
            var a = new SavedMlModel { UserId = UserA, Name = "Mia", ModelType = "Linear", Symbol = "BTC/USDT", Timeframe = "1h", FactorsJson = "[]", ModelBytes = [], CreatedAtUtc = DateTime.UtcNow };
            ctx.SavedMlModels.Add(a);
            await ctx.SaveChangesAsync();
            aId = a.Id;
        }

        // Utente sbagliato: nessuna cancellazione (clausola WHERE UserId).
        await svc.DeleteSavedModelAsync(aId, UserB);
        await svc.LoadSavedModelsListAsync(UserA);
        Assert.Single(svc.SavedModels);

        // Proprietario: cancellata.
        await svc.DeleteSavedModelAsync(aId, UserA);
        Assert.Empty(svc.SavedModels);
    }

    // --- [D1] Lente della matrice SHAP: regimi K-means con ripiego -------------------------------

    /// <summary>Addestra un modello AD ALBERI (SHAP si applica solo a quelli) e restituisce il servizio.</summary>
    private async Task<MlLabService> TrainTreeModelAsync(
        ProcioneMGR.Services.Regime.IRegimeDetector? detector,
        ProcioneMGR.Services.Regime.IMarketFeatureExtractor? extractor)
    {
        var (svc, db) = await BuildAsync(regimeDetector: detector, featureExtractor: extractor);
        await SeedCandlesAsync(db, 400);
        var res = await svc.TrainAsync(DefaultSnapshot() with { ModelType = "RandomForest" }, UserA);
        Assert.False(res.IsError, res.Message);
        Assert.True(svc.ShapAvailable, "il modello ad alberi deve esporre la struttura per SHAP");
        return svc;
    }

    private static ProcioneMGR.Services.Regime.RegimeModel ModelWithProfiles(params (int Id, string Label)[] profiles) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        NumberOfRegimes = profiles.Length,
        RegimeProfilesJson = System.Text.Json.JsonSerializer.Serialize(
            profiles.Select(p => new ProcioneMGR.Services.Regime.RegimeProfile
            {
                RegimeId = p.Id,
                SuggestedLabel = p.Label,
            }).ToList()),
    };

    [Fact]
    public async Task ComputeShap_WithAnActiveRegimeModel_UsesTheKMeansLens()
    {
        // Etichettatore finto: alterna due regimi noti, così le colonne attese sono deterministiche.
        var detector = new FakeRegimeDetector(
            ModelWithProfiles((0, "Laterale"), (1, "Tendenza")),
            labeller: feats =>
            {
                for (var i = 0; i < feats.Count; i++)
                {
                    feats[i].RegimeId = i % 2;
                    feats[i].RegimeLabel = i % 2 == 0 ? "Laterale" : "Tendenza";
                }
                return feats;
            });

        var svc = await TrainTreeModelAsync(detector, new FakeFeatureExtractor());
        var res = await svc.ComputeShapAnalysisAsync();

        Assert.False(res.IsError, res.Message);
        Assert.NotNull(svc.ShapAnalysis);
        Assert.StartsWith("Regimi K-means", svc.ShapAnalysis!.LensName);
        Assert.Contains("Laterale", svc.ShapAnalysis.Contexts);
        Assert.Contains("Tendenza", svc.ShapAnalysis.Contexts);
        // Il messaggio all'operatore deve dire quale lente ha usato.
        Assert.Contains("Regimi K-means", res.Message);
    }

    [Fact]
    public async Task ComputeShap_WithNoActiveRegimeModel_FallsBackToVolatility()
    {
        var svc = await TrainTreeModelAsync(
            new FakeRegimeDetector(activeModel: null),
            new FakeFeatureExtractor());

        var res = await svc.ComputeShapAnalysisAsync();

        Assert.False(res.IsError, res.Message);
        Assert.Equal(ShapAnalyzer.VolatilityLensName, svc.ShapAnalysis!.LensName);
        Assert.All(svc.ShapAnalysis.Contexts, c => Assert.Contains(c, ShapAnalyzer.VolatilityLabels));
    }

    [Fact]
    public async Task ComputeShap_WhenTheRegimeDetectorThrows_StillProducesTheAnalysis()
    {
        // La lente e' DESCRITTIVA: un suo guasto non deve mai far fallire il calcolo SHAP, che e' la
        // cosa che l'utente ha chiesto. Si ripiega, e il nome della lente lo dichiara.
        var svc = await TrainTreeModelAsync(
            new FakeRegimeDetector(activeModel: null, throwOnLoad: true),
            new FakeFeatureExtractor());

        var res = await svc.ComputeShapAnalysisAsync();

        Assert.False(res.IsError, res.Message);
        Assert.NotNull(svc.ShapAnalysis);
        Assert.Equal(ShapAnalyzer.VolatilityLensName, svc.ShapAnalysis!.LensName);
    }

    [Fact]
    public async Task ComputeShap_WithoutRegimeDependencies_FallsBackWithoutThrowing()
    {
        // I call-site che costruiscono il servizio senza le dipendenze opzionali (test legacy, tool)
        // devono continuare a funzionare: e' la ragione per cui sono opzionali.
        var svc = await TrainTreeModelAsync(detector: null, extractor: null);

        var res = await svc.ComputeShapAnalysisAsync();

        Assert.False(res.IsError, res.Message);
        Assert.Equal(ShapAnalyzer.VolatilityLensName, svc.ShapAnalysis!.LensName);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }
}
