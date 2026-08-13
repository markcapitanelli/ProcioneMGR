using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Execution;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.ML;
using ProcioneMGR.Services.Monitoring.Drift;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Registry;
using ProcioneMGR.Services.Risk;
using ProcioneMGR.Services.Sentiment;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Validazione lato server dei pannelli admin. Il rischio che copre non è teorico: l'attributo
/// <c>min=</c> di un input HTML non vincola il binding di Blazor, quindi prima di
/// <see cref="AdminConfigRules"/> qualunque numero digitato finiva in appsettings.json — e da lì
/// nei worker, che in alcuni casi (intervallo 0 su un PeriodicTimer) muoiono all'avvio successivo.
///
/// I test qui sotto sono divisi in due gruppi: i DEFAULT devono passare tutti (una regola che
/// rifiuta la configurazione di fabbrica è un bug della regola), e ogni regola ha il suo caso
/// patologico.
/// </summary>
public class AdminConfigRulesTests
{
    public static TheoryData<object> DefaultOptions() =>
    [
        new SafetyConfiguration(),
        new LiveExecutionOptions(),
        new AutoReapplyOptions(),
        new PromotionEvaluatorOptions(),
        new LlmOptions(),
        new SupervisorAgentOptions(),
        new SentimentOptions(),
        new DriftMonitorOptions(),
        new NotificationOptions(),
        new CampaignOptions(),
        new RegimeTriggerOptions(),
        new EnsembleComparatorOptions(),
        new ModelRegistryOptions { MinChampionDeflatedSharpe = 0.95d },
        new MlComparisonOptions(),
        new RealtimeFeedOptions(),
        new ProtectiveExitShadowOptions(),
        new CorrelatedExposureOptions(),
        new RegimeRoutingOptions(),
        new LaneInvariantOptions(),
        new CarryOptions(),
        new LiquidationsOptions(),
        new ExecutionParameters(),
    ];

    [Theory]
    [MemberData(nameof(DefaultOptions))]
    public void Defaults_AreAlwaysAccepted(object options)
        => Assert.Null(AdminConfigRules.Validate(options));

    [Fact]
    public void UnknownType_PassesThrough()
        => Assert.Null(AdminConfigRules.Validate(new { Whatever = 1 }));

    // --- Intervalli a zero: il caso che uccide un PeriodicTimer all'avvio ----------------------

    [Fact]
    public void Drift_IntervalZero_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new DriftMonitorOptions { IntervalHours = 0 }));

    [Fact]
    public void LiveExecution_TickBelowFiveSeconds_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new LiveExecutionOptions { WorkerTickSeconds = 0 }));

    [Fact]
    public void Llm_MaxTokensTooSmall_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new LlmOptions { MaxTokens = 10 }));

    // --- [E1] Trading:Safety: prima del 2026-07-31 questa sezione NON aveva regola — la garanzia
    // «un valore rifiutato in UI non entra da un'altra porta» per le soglie del motore era vuota,
    // e il canale gRPC avrebbe accettato qualunque payload. ------------------------------------

    [Theory]
    [InlineData(0, 50, 5, 5)]   // MaxPositionSizePercent <= 0
    [InlineData(10, 0, 5, 5)]   // MaxTotalExposurePercent <= 0
    [InlineData(10, 50, 0, 5)]  // MaxOpenPositions < 1
    [InlineData(10, 50, 5, 0)]  // MaxLeverageAllowed < 1
    public void Safety_PanelInvariants_AreAlsoEnforcedServerSide(
        decimal maxPos, decimal maxExposure, int maxOpen, int maxLeverage)
        => Assert.NotNull(AdminConfigRules.Validate(new SafetyConfiguration
        {
            MaxPositionSizePercent = maxPos,
            MaxTotalExposurePercent = maxExposure,
            MaxOpenPositions = maxOpen,
            MaxLeverageAllowed = maxLeverage,
        }));

    [Fact]
    public void Safety_NegativeFee_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new SafetyConfiguration { FeePercent = -0.1m }));

    [Fact]
    public void Safety_VolTargetingOnWithZeroTarget_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new SafetyConfiguration
        {
            VolatilityTargetingEnabled = true,
            TargetAnnualVolatilityPercent = 0m,
        }));

    [Fact]
    public void Safety_ExposureMultiplierRangeInverted_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new SafetyConfiguration
        {
            MinExposureMultiplier = 1.0m,
            MaxExposureMultiplier = 0.5m,
        }));

    [Fact]
    public void Safety_ZeroFillDeviationBands_AreRejected()
    {
        // A 0 la banda di plausibilità marca OGNI fill come sospetto: il trading si ferma in
        // silenzio, che è esattamente la classe di guasto per cui la regola esiste.
        Assert.NotNull(AdminConfigRules.Validate(new SafetyConfiguration { MaxFillPriceDeviationPercent = 0m }));
        Assert.NotNull(AdminConfigRules.Validate(new SafetyConfiguration { MaxFillQuantityDeviationPercent = 0m }));
    }

    // --- Soglie in relazione fra loro: il gruppo di errori che un min= non può nemmeno esprimere -

    [Fact]
    public void Promotion_HardBlockBelowMaxDrawdown_IsRejected()
    {
        // Un hard-block più basso del drawdown "normale" blocca prima del limite che dovrebbe
        // essere il limite: le due soglie si scambiano di ruolo in silenzio.
        var o = new PromotionEvaluatorOptions { MaxDrawdownPercent = 15m, HardMaxDrawdownPercent = 10m };
        Assert.NotNull(AdminConfigRules.Validate(o));
    }

    [Fact]
    public void Promotion_DemoteThresholdAbovePromoteThreshold_IsRejected()
    {
        // Promuovere sopra 0,8 e retrocedere sotto 1,0 significa promuovere e retrocedere la stessa
        // corsia a ogni ciclo, per sempre.
        var o = new PromotionEvaluatorOptions { MinSharpeRealized = 0.8m, DemoteSharpeThreshold = 1.0m };
        Assert.NotNull(AdminConfigRules.Validate(o));
    }

    [Fact]
    public void Carry_ExitAboveEnter_IsRejected()
    {
        // Senza isteresi il carry aprirebbe e chiuderebbe nella stessa valutazione.
        var o = new CarryOptions { EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 8m };
        Assert.NotNull(AdminConfigRules.Validate(o));
    }

    [Fact]
    public void Carry_LiveMode_IsRejected()
    {
        // Difesa in profondità: CarryMode non ha nemmeno il valore Live, ma se qualcuno lo scrive in
        // config lo si dice a voce alta invece di degradare a Paper con un warning nei log.
        Assert.NotNull(AdminConfigRules.Validate(new CarryOptions { Mode = "Live" }));
        Assert.Null(AdminConfigRules.Validate(new CarryOptions { Mode = "Testnet" }));
    }

    [Fact]
    public void Sentiment_FearGreedBoundsInverted_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new SentimentOptions { FearGreedExtremeLow = 80, FearGreedExtremeHigh = 20 }));

    [Fact]
    public void HeritageGuard_IntervalAtZero_IsRejected()
        // Un PeriodicTimer a 0 lancia all'avvio: il guardiano morirebbe in silenzio — lo stesso
        // guasto che deve sorvegliare.
        => Assert.NotNull(AdminConfigRules.Validate(new SentimentOptions
        {
            HeritageGuard = new SentimentHeritageGuardOptions { CheckIntervalHours = 0 },
        }));

    [Fact]
    public void HeritageGuard_ZeroPointThreshold_IsRejected()
        // Soglia 0 punti = ogni serie passa sempre: un controllo che rassicura a prescindere.
        => Assert.NotNull(AdminConfigRules.Validate(new SentimentOptions
        {
            HeritageGuard = new SentimentHeritageGuardOptions { FundingMinEventsPerSymbol = 0 },
        }));

    [Fact]
    public void HeritageGuard_FutureAnchorDate_IsRejected()
        // Una data-àncora nel futuro è una violazione perpetua: allarme sempre acceso = mai letto.
        => Assert.NotNull(AdminConfigRules.Validate(new SentimentOptions
        {
            HeritageGuard = new SentimentHeritageGuardOptions { FundingMinStartUtc = DateTime.UtcNow.AddYears(1) },
        }));

    [Fact]
    public void Realtime_BackoffCeilingBelowInitialDelay_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(
            new RealtimeFeedOptions { ReconnectInitialDelayMs = 10_000, ReconnectMaxDelayMs = 1_000 }));

    [Fact]
    public void CorrelatedExposure_LookbackBelowMinimumOverlap_IsRejected()
    {
        // Con la finestra più corta delle barre sovrapposte richieste, nessuna correlazione sarà
        // MAI stimabile — e il guard, che fallisce verso il permesso, lascerebbe passare tutto
        // sembrando acceso.
        var o = new CorrelatedExposureOptions { LookbackBars = 50, MinOverlappingBars = 100 };
        Assert.NotNull(AdminConfigRules.Validate(o));
    }

    [Fact]
    public void RegimeRouting_DuplicateRegimeIds_AreRejected()
    {
        var o = new RegimeRoutingOptions
        {
            Rules =
            [
                new RegimeRoutingRule { RegimeId = 0, Strategies = ["Supertrend"] },
                new RegimeRoutingRule { RegimeId = 0, Strategies = ["RsiOversold"] },
            ],
        };
        Assert.NotNull(AdminConfigRules.Validate(o));
    }

    [Fact]
    public void RegimeRouting_EmptyStrategyList_IsAccepted()
    {
        // "In questo regime la corsia sta ferma" è una decisione legittima, non un errore di
        // compilazione del form: la regola non deve rifiutarla.
        var o = new RegimeRoutingOptions { Rules = [new RegimeRoutingRule { RegimeId = 2, Strategies = [] }] };
        Assert.Null(AdminConfigRules.Validate(o));
    }

    [Fact]
    public void Notifications_TelegramWithoutChatId_IsRejected()
    {
        Assert.NotNull(AdminConfigRules.Validate(
            new NotificationOptions { Enabled = true, Provider = "Telegram", ChatId = "" }));

        // Spente, il ChatId vuoto non è un problema: non si sta configurando un recapito.
        Assert.Null(AdminConfigRules.Validate(
            new NotificationOptions { Enabled = false, Provider = "Telegram", ChatId = "" }));
    }

    [Fact]
    public void Ml_DualReadEnabledWithoutUrl_IsRejected()
        => Assert.NotNull(AdminConfigRules.Validate(new MlComparisonOptions { Enabled = true, RemoteUrl = "" }));

    [Fact]
    public void Registry_DeflatedSharpeOutsideProbabilityRange_IsRejected()
    {
        Assert.NotNull(AdminConfigRules.Validate(new ModelRegistryOptions { MinChampionDeflatedSharpe = 1.5d }));
        Assert.NotNull(AdminConfigRules.Validate(new ModelRegistryOptions { MinChampionDeflatedSharpe = -0.1d }));
    }

    [Fact]
    public void Execution_ReferenceVolatilityZero_IsRejected()
        // È il denominatore dell'urgenza di Adaptive: a zero il piano non è calcolabile.
        => Assert.NotNull(AdminConfigRules.Validate(new ExecutionParameters { ReferenceVolatility = 0m }));

    [Fact]
    public void LaneInvariants_ZeroMultipliers_AreRejected()
    {
        // Un multiplo a zero manda in quarantena qualunque corsia con una posizione aperta: il
        // watchdog passa da tripwire a interruttore generale.
        Assert.NotNull(AdminConfigRules.Validate(new LaneInvariantOptions { MaxAbsPnlCapitalMultiple = 0m }));
        Assert.NotNull(AdminConfigRules.Validate(new LaneInvariantOptions { MaxExposureCapitalMultiple = 0m }));
    }

    [Fact]
    public void RegimeTrigger_VolBandOfOne_IsRejected()
        // A k=1 la banda [forecast/1, forecast*1] è un punto: il trigger scatterebbe sempre.
        => Assert.NotNull(AdminConfigRules.Validate(new RegimeTriggerOptions { VolBandMultiple = 1d }));
}
