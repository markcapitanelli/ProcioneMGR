using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Regime;

/// <summary>Regola di instradamento: in questo regime operano SOLO queste strategie.</summary>
public sealed class RegimeRoutingRule
{
    /// <summary>Id del cluster K-means (come mostrato in <c>/regimes</c>).</summary>
    public int RegimeId { get; set; }

    /// <summary>
    /// Nomi di strategia ammessi (quelli di <c>IStrategyFactory</c>, es. "Supertrend").
    /// Una lista <b>vuota</b> significa "in questo regime la corsia sta ferma" — è la scelta più
    /// utile del PDF, non un caso degenere: saper riconoscere il regime in cui non si ha edge vale
    /// quanto saper scegliere la strategia in quello in cui lo si ha.
    /// </summary>
    public List<string> Strategies { get; set; } = [];
}

/// <summary>Opzioni del router di regime. Default SPENTO.</summary>
public sealed class RegimeRoutingOptions
{
    /// <summary>
    /// Interruttore generale. Default FALSE: prima di dare a un modello K-means il potere di
    /// spegnere una strategia dal vivo, quel potere va guadagnato in validazione.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Politica per i regimi senza regola. Default TRUE (permissivo): un regime nuovo — o un
    /// modello riaddestrato con più cluster di quanti ne conosca la configurazione — non deve
    /// zittire la corsia di soppiatto. Il caso "non so" e il caso "so che qui non si opera" sono
    /// cose diverse e vanno configurate diversamente.
    /// </summary>
    public bool AllowUnmappedRegimes { get; set; } = true;

    /// <summary>Candele minime in memoria perché la classificazione sia tentata.</summary>
    public int MinCandles { get; set; } = 60;

    public List<RegimeRoutingRule> Rules { get; set; } = [];
}

/// <summary>
/// Esito della classificazione per la barra corrente. <see cref="IsKnown"/> false = regime non
/// determinabile (nessun modello attivo per la serie, candele insufficienti, router spento): in
/// quel caso <see cref="Allows"/> risponde sempre sì.
/// </summary>
public sealed record RegimeRoutingDecision(bool IsKnown, int RegimeId, string Reason, IReadOnlyList<string> AllowedStrategies, bool AllowUnmapped)
{
    public static RegimeRoutingDecision Unknown(string reason) => new(false, -1, reason, [], true);

    /// <summary>True se la strategia può operare nel regime corrente.</summary>
    public bool Allows(string strategyName)
    {
        if (!IsKnown) return true;
        if (AllowedStrategies.Count == 0) return AllowUnmapped && !HasRule;
        return AllowedStrategies.Contains(strategyName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True se esiste una regola esplicita per questo regime (anche con lista vuota = stai fermo).</summary>
    public bool HasRule { get; init; }
}

/// <summary>Classifica il regime corrente di una corsia e dice quali strategie possono operarvi.</summary>
public interface ILaneRegimeRouter
{
    Task<RegimeRoutingDecision> DecideAsync(
        string symbol, string timeframe, IReadOnlyList<OhlcvData> recentCandles, CancellationToken ct = default);
}

/// <summary>
/// [Fase 4 — docs/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Il router di regime che il PDF mette al centro
/// del suo framework ibrido: classifica il regime, e in base a quello lascia operare solo le
/// strategie che vi hanno senso.
///
/// <b>Perché serviva.</b> Il routing per regime esisteva già, ma solo dentro il backtest e con un
/// surrogato: <c>RegimeConditionalStrategy</c> usa la pendenza di una SMA con dead zone, non il
/// <see cref="IRegimeDetector"/> K-means, e lo dichiara nel proprio commento — le strategie di
/// questa piattaforma sono dependency-free per scelta, quindi una strategia legata al DB non
/// potrebbe girare negli sweep dell'ottimizzatore. Il motore live, dal canto suo, il regime non lo
/// consultava affatto. Questa classe è il "plumbing nuovo" che quel commento indicava, costruito
/// però <b>fuori</b> dalla strategia: al livello della corsia, dove il DB è già disponibile e dove
/// la decisione "chi opera adesso" appartiene naturalmente.
///
/// <b>È un filtro, non una mutazione.</b> Non tocca <c>EnsembleStrategy.IsActive</c> né alcuno
/// stato: risponde a una domanda quando gliela si fa. Mutare la configurazione dell'ensemble
/// significherebbe litigare con il ribilanciamento dell'<c>EnsembleManager</c> e lasciare la corsia
/// in uno stato che nessuno dei due possiede davvero.
///
/// <b>Isteresi gratuita.</b> Lo smoothing anti flip-flop non è riscritto qui: arriva da
/// <see cref="IRegimeDetector.LabelFeaturesAsync"/>, che applica la conferma a più candele di
/// <see cref="RegimeAssignment"/>. Un router che cambiasse idea a ogni barra di confine
/// spegnerebbe e riaccenderebbe le strategie sul rumore, che è il modo più rapido di pagare
/// commissioni per niente.
///
/// <b>Fallisce verso il permesso.</b> Nessun modello per la serie, candele insufficienti, feature
/// non calcolabili ⇒ regime "non noto" ⇒ tutte le strategie operano, come prima del router. Un
/// filtro di questo tipo che fallisse verso il blocco fermerebbe l'intera corsia per un modello
/// mancante, cioè trasformerebbe un'assenza di informazione in una decisione di trading.
/// </summary>
public sealed class LaneRegimeRouter(
    IMarketFeatureExtractor featureExtractor,
    IRegimeDetector regimeDetector,
    IOptionsMonitor<RegimeRoutingOptions> options,
    ILogger<LaneRegimeRouter> logger) : ILaneRegimeRouter
{
    public async Task<RegimeRoutingDecision> DecideAsync(
        string symbol, string timeframe, IReadOnlyList<OhlcvData> recentCandles, CancellationToken ct = default)
    {
        var cfg = options.CurrentValue;
        if (!cfg.Enabled) return RegimeRoutingDecision.Unknown("router disattivato");
        if (recentCandles.Count < Math.Max(10, cfg.MinCandles))
        {
            return RegimeRoutingDecision.Unknown($"candele insufficienti ({recentCandles.Count})");
        }

        try
        {
            // Il modello va verificato PRIMA di spendere il calcolo delle feature, e deve essere
            // quello della serie che stiamo instradando: etichettare BTC 1h col modello di ETH 4h
            // darebbe un numero perfettamente formato e completamente privo di senso.
            var model = await regimeDetector.LoadLatestModelAsync(ct);
            if (model is null) return RegimeRoutingDecision.Unknown("nessun modello di regime attivo");
            if (!string.Equals(model.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(model.Timeframe, timeframe, StringComparison.OrdinalIgnoreCase))
            {
                return RegimeRoutingDecision.Unknown(
                    $"il modello attivo è di {model.Symbol} {model.Timeframe}, non di {symbol} {timeframe}");
            }

            var features = featureExtractor.ComputeFeatures(recentCandles, timeframe, ct);
            if (features.Count == 0) return RegimeRoutingDecision.Unknown("feature non calcolabili");

            // Etichettatura con lo smoothing del detector: l'ultima candela è il regime corrente.
            await regimeDetector.LabelFeaturesAsync(features, ct);
            var current = features.LastOrDefault(f => f.RegimeId is not null)?.RegimeId;
            if (current is not int regimeId) return RegimeRoutingDecision.Unknown("nessuna candela etichettata");

            var rule = cfg.Rules.FirstOrDefault(r => r.RegimeId == regimeId);
            return new RegimeRoutingDecision(
                IsKnown: true,
                RegimeId: regimeId,
                Reason: rule is null ? "regime senza regola" : "regola applicata",
                AllowedStrategies: rule?.Strategies ?? [],
                AllowUnmapped: cfg.AllowUnmappedRegimes)
            {
                HasRule = rule is not null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un guasto del router non deve MAI diventare un guasto della corsia.
            logger.LogWarning(ex, "Router di regime: classificazione fallita per {Symbol} {Timeframe}, la corsia prosegue senza filtro.", symbol, timeframe);
            return RegimeRoutingDecision.Unknown("errore di classificazione");
        }
    }
}
