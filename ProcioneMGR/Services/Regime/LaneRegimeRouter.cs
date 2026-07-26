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
    /// Separa l'<b>osservare</b> dal <b>decidere</b>, come già fa il feed real-time con
    /// <c>DriveProtectiveExits</c>. Default FALSE: acceso ma non decidente, il router classifica il
    /// regime a ogni candela e ne registra i cambi, senza impedire nulla.
    ///
    /// Non è prudenza generica, è la risposta a un problema concreto: le regole di instradamento si
    /// scrivono guardando come si comporta una strategia in ciascun regime, e quel dato oggi è
    /// sottile (sul modello BTC 1h la strategia in uso ha da 5 a 37 trade per regime). Scrivere
    /// regole su cinque trade sarebbe il curve-fitting che il resto della piattaforma rifiuta. In
    /// osservazione si accumula il dato mancante senza rischiare nulla; le regole si accendono dopo.
    /// </summary>
    public bool DriveDecisions { get; set; }

    /// <summary>
    /// Politica per i regimi senza regola. Default TRUE (permissivo): un regime nuovo — o un
    /// modello riaddestrato con più cluster di quanti ne conosca la configurazione — non deve
    /// zittire la corsia di soppiatto. Il caso "non so" e il caso "so che qui non si opera" sono
    /// cose diverse e vanno configurate diversamente.
    /// </summary>
    public bool AllowUnmappedRegimes { get; set; } = true;

    /// <summary>Candele minime in memoria perché la classificazione sia tentata.</summary>
    public int MinCandles { get; set; } = 60;

    /// <summary>
    /// Per quanto tempo si riusa l'esito della verifica "esiste un modello attivo per questa serie"
    /// senza rinterrogare il database. Il compromesso è dichiarato: attivare un modello nuovo da
    /// <c>/regimes</c> impiega fino a questo tempo a farsi sentire sul router, in cambio di zero
    /// query per candela. Con candele da un'ora il default è invisibile; abbassarlo ha senso solo
    /// mentre si sta sperimentando con i modelli.
    /// </summary>
    public TimeSpan ModelCheckTtl { get; set; } = TimeSpan.FromMinutes(5);

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

    /// <summary>
    /// True quando il router sta solo <b>guardando</b>: la classificazione avviene ed è registrata,
    /// ma non impedisce nulla. Vedi <see cref="RegimeRoutingOptions.DriveDecisions"/>.
    /// </summary>
    public bool Observing { get; init; }

    /// <summary>True se esiste una regola esplicita per questo regime (anche con lista vuota = stai fermo).</summary>
    public bool HasRule { get; init; }

    /// <summary>True se la strategia può operare nel regime corrente.</summary>
    public bool Allows(string strategyName)
    {
        if (Observing || !IsKnown) return true;
        if (AllowedStrategies.Count == 0) return AllowUnmapped && !HasRule;
        return AllowedStrategies.Contains(strategyName, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Classifica il regime corrente di una corsia e dice quali strategie possono operarvi.</summary>
public interface ILaneRegimeRouter
{
    Task<RegimeRoutingDecision> DecideAsync(
        string symbol, string timeframe, IReadOnlyList<OhlcvData> recentCandles, CancellationToken ct = default);
}

/// <summary>
/// [Fase 4 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Il router di regime che il PDF mette al centro
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Symbol, string Timeframe), (RegimeRoutingDecision? Refusal, DateTime CheckedAtUtc)> _modelCheck = new();

    /// <summary>Ultimo regime visto per serie, per registrare i CAMBI e non ogni candela.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Symbol, string Timeframe), int> _lastRegime = new();

    /// <summary>Ultimo motivo di rinuncia già segnalato, per non ripetere lo stesso avviso a ogni candela.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Symbol, string Timeframe), string> _lastRefusalLogged = new();

    /// <summary>
    /// Segnala a voce alta — ma UNA volta sola per motivo — che il router ha smesso di filtrare.
    /// Un filtro che si spegne in silenzio è peggio di un filtro assente: l'operatore continua a
    /// credere che ci sia. Il caso concreto che rende questo necessario: il detector carica il
    /// modello attivo più RECENTE senza filtrare per serie, quindi addestrare un modello per
    /// un'altra coppia toglie il router a questa corsia senza che nulla lo dica.
    /// </summary>
    private void LogRefusalOnce(string symbol, string timeframe, RegimeRoutingDecision refusal)
    {
        var key = (symbol, timeframe);
        if (_lastRefusalLogged.TryGetValue(key, out var already) && already == refusal.Reason) return;
        _lastRefusalLogged[key] = refusal.Reason;

        logger.LogWarning(
            "Router di regime INATTIVO su {Symbol} {Timeframe}: {Reason}. Le strategie operano senza filtro.",
            symbol, timeframe, refusal.Reason);
    }

    /// <summary>Registra i CAMBI di regime, non ogni candela: è il dato che serve per scrivere le regole.</summary>
    private void LogRegimeTransition(string symbol, string timeframe, int regimeId, bool observing)
    {
        var key = (symbol, timeframe);
        var known = _lastRegime.TryGetValue(key, out var previous);
        if (known && previous == regimeId) return;
        _lastRegime[key] = regimeId;

        logger.LogInformation(
            "Regime {Symbol} {Timeframe}: {Previous} → {Current}{Mode}.",
            symbol, timeframe, known ? previous.ToString() : "(primo rilevamento)", regimeId,
            observing ? " — osservazione, nessun filtro applicato" : string.Empty);
    }

    /// <summary>
    /// Verifica (con cache a tempo) che esista un modello attivo per QUESTA serie.
    /// Ritorna <c>null</c> se tutto è a posto, oppure la decisione di rifiuto già pronta.
    /// </summary>
    private async Task<RegimeRoutingDecision?> ModelMatchesAsync(
        string symbol, string timeframe, TimeSpan ttl, CancellationToken ct)
    {
        var key = (symbol, timeframe);
        if (_modelCheck.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.CheckedAtUtc < ttl)
        {
            return cached.Refusal;
        }

        // Modello DI QUESTA serie. Finché il detector sapeva restituire solo "il più recente fra
        // tutti", il router poteva servire una corsia sola e addestrare un modello per un'altra
        // coppia gliela toglieva; ora ogni corsia interroga la propria serie e le corsie non si
        // rubano più il router a vicenda.
        var model = await regimeDetector.LoadActiveModelAsync(symbol, timeframe, ct);
        var refusal = model is null
            ? RegimeRoutingDecision.Unknown($"nessun modello di regime attivo per {symbol} {timeframe}")
            : null;

        _modelCheck[key] = (refusal, DateTime.UtcNow);
        return refusal;
    }

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
            //
            // L'esito è tenuto in cache a tempo perché questo metodo gira a OGNI candela di OGNI
            // corsia: senza, sarebbe una query al database per candela solo per riscoprire che il
            // modello è lo stesso di un minuto fa. L'etichettatura vera e propria ha già una sua
            // cache in memoria dentro il detector, quindi questa era l'unica I/O rimasta sul
            // percorso caldo.
            var check = await ModelMatchesAsync(symbol, timeframe, cfg.ModelCheckTtl, ct);
            if (check is not null)
            {
                LogRefusalOnce(symbol, timeframe, check);
                return check;
            }

            var features = featureExtractor.ComputeFeatures(recentCandles, timeframe, ct);
            if (features.Count == 0) return RegimeRoutingDecision.Unknown("feature non calcolabili");

            // Etichettatura col modello DI QUESTA serie e con lo smoothing del detector: l'ultima
            // candela è il regime corrente.
            await regimeDetector.LabelFeaturesAsync(features, symbol, timeframe, ct);
            var current = features.LastOrDefault(f => f.RegimeId is not null)?.RegimeId;
            if (current is not int regimeId) return RegimeRoutingDecision.Unknown("nessuna candela etichettata");

            _lastRefusalLogged.TryRemove((symbol, timeframe), out _);   // torna a funzionare ⇒ il prossimo guasto si ridice
            LogRegimeTransition(symbol, timeframe, regimeId, !cfg.DriveDecisions);

            var rule = cfg.Rules.FirstOrDefault(r => r.RegimeId == regimeId);
            return new RegimeRoutingDecision(
                IsKnown: true,
                RegimeId: regimeId,
                Reason: rule is null ? "regime senza regola" : "regola applicata",
                AllowedStrategies: rule?.Strategies ?? [],
                AllowUnmapped: cfg.AllowUnmappedRegimes)
            {
                HasRule = rule is not null,
                Observing = !cfg.DriveDecisions,
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
