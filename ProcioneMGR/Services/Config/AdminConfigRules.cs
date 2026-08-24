using ProcioneMGR.Services.Agents;
using ProcioneMGR.Services.Carry;
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

namespace ProcioneMGR.Services.Config;

/// <summary>
/// Validazione LATO SERVER dei pannelli di configurazione admin (/admin/autonomy,
/// /admin/protections, i parametri di /execution).
///
/// <para>Perché serve, visto che i campi hanno già <c>min=</c> nell'HTML: l'attributo
/// <c>min</c> di un <c>&lt;input type="number"&gt;</c> vincola la validazione di un FORM, non il
/// binding di Blazor — con <c>@bind</c> il valore digitato arriva al modello comunque, e da lì
/// dritto in <c>appsettings.json</c>. Prima di questa classe si poteva salvare
/// <c>Llm:MaxTokens=0</c> (ogni chiamata all'API rifiutata), <c>Drift:IntervalHours=0</c> (il
/// <c>PeriodicTimer</c> del worker lancia <see cref="ArgumentOutOfRangeException"/> all'avvio e la
/// funzione muore in silenzio fino al riavvio successivo), o soglie di ingresso/uscita invertite
/// che fanno aprire e chiudere il carry alla stessa valutazione.</para>
///
/// <para>Il contratto è volutamente semplice: un solo punto di ingresso
/// (<see cref="Validate"/>), <c>null</c> = configurazione accettabile, altrimenti il messaggio da
/// mostrare all'operatore. Nessuna correzione silenziosa dei valori: un numero cambiato sotto le
/// dita di chi lo ha appena scritto è peggio di un rifiuto esplicito.</para>
/// </summary>
public static class AdminConfigRules
{
    /// <summary>
    /// Valida le opzioni di una sezione. Restituisce <c>null</c> se sono accettabili, altrimenti il
    /// messaggio d'errore. I tipi non riconosciuti passano: questa classe non è un gate
    /// obbligatorio per ogni sezione esistente, è il posto dove vivono le regole di quelle che ne
    /// hanno bisogno.
    /// </summary>
    public static string? Validate(object options) => options switch
    {
        // [E1] Le soglie pre-ordine del motore. Fino al 2026-07-31 questa sezione non aveva regola:
        // la garanzia «un valore rifiutato in UI non entra da un'altra porta» per Trading:Safety era
        // vuota, e il canale gRPC l'avrebbe accettata qualunque. Stessi vincoli del pannello, più
        // gli invarianti che il pannello non esprimeva.
        SafetyConfiguration o => Check(
            (o.MaxPositionSizePercent > 0m, "La size massima per posizione dev'essere > 0."),
            (o.PositionSizePercent > 0m, "La size per apertura dev'essere > 0."),
            (o.MaxTotalExposurePercent > 0m, "L'esposizione totale massima dev'essere > 0."),
            (o.MaxDailyLossPercent > 0m, "La perdita giornaliera massima dev'essere > 0: a 0 ogni ordine è rifiutato."),
            (o.MaxDrawdownPercent > 0m, "Il drawdown massimo dev'essere > 0: a 0 l'emergency stop scatta subito."),
            (o.MaxOpenPositions >= 1, "Serve almeno 1 posizione aperta consentita."),
            (o.MinOrderIntervalSeconds >= 0, "L'intervallo minimo tra ordini non può essere negativo."),
            (o.MaxLeverageAllowed >= 1, "La leva massima dev'essere almeno 1."),
            (o.MaintenanceMarginPercent >= 0m, "Il margine di mantenimento non può essere negativo."),
            (o.FeePercent >= 0m, "La fee non può essere negativa."),
            (!o.VolatilityTargetingEnabled || o.TargetAnnualVolatilityPercent > 0m,
                "Col dosaggio sulla volatilità acceso serve una volatilità obiettivo > 0: è il numeratore del fattore."),
            (o.VolatilityLookbackBars >= 2, "Servono almeno 2 barre per stimare la volatilità."),
            (o.MinExposureMultiplier >= 0m, "Il fattore minimo non può essere negativo."),
            (o.MaxExposureMultiplier >= o.MinExposureMultiplier,
                "Il fattore massimo dev'essere >= del minimo, altrimenti il dosaggio non ha un intervallo."),
            (o.MaxFillPriceDeviationPercent > 0m,
                "La banda di plausibilità del prezzo di fill dev'essere > 0: a 0 ogni fill è sospetto."),
            (o.MaxFillQuantityDeviationPercent > 0m,
                "La tolleranza sulla quantità di fill dev'essere > 0: a 0 ogni fill è sospetto.")),

        LiveExecutionOptions o => Check(
            (o.DefaultWindowMinutes >= 1, "La finestra di default dev'essere almeno 1 minuto."),
            (o.WorkerTickSeconds >= 5, "Il tick del worker dev'essere almeno 5 secondi."),
            (o.AbandonGraceMinutes >= 0, "La grazia di abbandono non può essere negativa.")),

        // [AF5.1] L'heartbeat incrociato: la soglia di stantiezza deve stare BEN sopra il periodo
        // di scrittura, o il rumore di un tick perso diventa un falso allarme di host morto.
        ProcioneMGR.Services.Health.HeartbeatOptions o => Check(
            (o.WriteSeconds >= 10, "Il battito non può essere più fitto di 10 secondi."),
            (o.StaleMinutes >= 1, "La soglia di stantiezza dev'essere almeno 1 minuto."),
            (o.StaleMinutes * 60 >= o.WriteSeconds * 3,
                "La soglia di stantiezza dev'essere almeno 3 volte il periodo di scrittura: sotto, un tick perso per rumore dichiara morto un host vivo.")),

        // [AF3] Il comitato: un quorum a 0 renderebbe "maggioranza" un voto solo di rumore.
        ProcioneMGR.Services.Llm.Committee.CommitteeOptions o => Check(
            (o.MinValidVotes >= 1, "Il quorum dev'essere almeno 1 voto valido."),
            (o.TimeoutSeconds >= 5, "Il timeout del voto dev'essere almeno 5 secondi."),
            (o.Providers.All(p => ProcioneMGR.Services.Llm.AiProviders.Known.Contains(p, StringComparer.OrdinalIgnoreCase)),
                $"Provider sconosciuto nella lista: i validi sono {string.Join(", ", ProcioneMGR.Services.Llm.AiProviders.Known)}.")),

        // [AF5.4] Il digest giornaliero: un orario impossibile non è "mai", è un bug silenzioso.
        ProcioneMGR.Services.Notifications.DigestOptions o => Check(
            (o.Hour is >= 0 and <= 23, "L'ora del digest dev'essere fra 0 e 23."),
            (o.Minute is >= 0 and <= 59, "I minuti del digest devono essere fra 0 e 59.")),

        // [AF2] L'orchestratore di flotta: i minimi qui sotto sono ciò che separa "prudente per
        // costruzione" da "impazzito per un refuso" (un tick a 0 o un ritiro senza storia).
        ProcioneMGR.Services.Fleet.FleetOptions o => Check(
            (o.TickMinutes >= 1, "Il tick dev'essere almeno 1 minuto."),
            (o.RetireMinWeeks >= 1, "Il ritiro richiede almeno 1 settimana di osservazione."),
            (o.RetireMinTrades >= 1, "Il ritiro richiede almeno 1 trade osservato."),
            (o.RetireConfirmTicks >= 1, "Serve almeno 1 tick di conferma per un ritiro."),
            (o.MaxAssignmentsPerTick >= 1, "Serve almeno 1 assegnazione possibile per tick."),
            (o.MinTradesPerMonth >= 0m, "La frequenza minima non può essere negativa."),
            (o.CandidateMaxAgeDays >= 1, "L'età massima dei candidati dev'essere almeno 1 giorno."),
            (o.MaxLanesWithoutExposureGuard >= 1, "Il tetto senza guardia di esposizione dev'essere almeno 1."),
            (o.CarrySilenceAlertHours >= 1, "La soglia di silenzio del carry dev'essere almeno 1 ora."),
            // [I12-rev] Le manopole del ritiro per inedia e del braccio esecutivo. La frazione e'
            // l'unica di tutta la sezione che puo' fare danno restando "valida" per il binder: a 1
            // condanna ogni corsia che non superi l'atteso, sopra 1 le condanna TUTTE — e col
            // braccio armato le ferma sul serio, una per tick. Zero resta ammesso ed e' il modo
            // dichiarato di spegnere il criterio.
            (o.StarvationFraction is >= 0m and <= 1m,
                "La frazione di inedia sta fra 0 e 1 (0 = criterio spento). Sopra 1 condannerebbe ogni corsia, "
                + "compresa quella che opera esattamente quanto promesso."),
            (o.StarvationMinDays >= 1, "L'osservazione minima per l'inedia dev'essere almeno 1 giorno: a 0 si giudicherebbe una corsia appena avviata."),
            (o.MaxExecutionsPerTick >= 1, "Serve almeno 1 azione possibile per tick."),
            (o.ExecutionLanes.All(l => l >= 0),
                "Le corsie autorizzate all'esecuzione sono numeri di corsia: nessuno puo' essere negativo.")),

        // [I14c] La sorveglianza dello spread. La soglia di persistenza e' quella che puo' fare
        // danno restando "valida" per il binder: a 0 ogni coppia risulta persistente (anche il puro
        // rumore), sopra 1 nessuna lo e' mai — in entrambi i casi il verdetto smette di dire
        // qualcosa. La finestra sotto le 60 candele toglie potenza all'ADF fino a renderlo un
        // generatore di rumore.
        ProcioneMGR.Services.PairsTrading.PairsWatchOptions o => Check(
            (o.IntervalHours is >= 1 and <= 168, "La cadenza sta fra 1 ora e 7 giorni."),
            (o.WindowSize >= 60, "La finestra dev'essere almeno 60 candele: sotto, l'ADF non ha potenza e il verdetto e' rumore."),
            (o.MaxCandles >= o.WindowSize * ProcioneMGR.Services.PairsTrading.PairSpreadJudge.MinWindows,
                $"Servono almeno {ProcioneMGR.Services.PairsTrading.PairSpreadJudge.MinWindows} finestre non sovrapposte per un giudizio: "
                + "le candele per giro devono bastare a coprirle."),
            (o.PersistenceThreshold is > 0 and <= 1,
                "La soglia di persistenza sta fra 0 (escluso) e 1: a 0 anche il puro rumore risulterebbe una relazione persistente."),
            (o.RecentWindows >= 1, "Serve almeno 1 finestra recente per poter giudicare una rottura."),
            (o.Estimator is "Kalman" or "RollingOls", "L'estimatore e' \"Kalman\" o \"RollingOls\".")),

        AutoReapplyOptions o => Check(
            (o.LookbackDays >= 1, "Il lookback dev'essere almeno 1 giorno."),
            (o.MaxPerTick >= 1, "Serve almeno un run per tick.")),

        PromotionEvaluatorOptions o => Check(
            (o.MinTradeCount >= 1, "Servono almeno 1 trade per valutare una promozione."),
            (o.MinObservationWeeks >= 1, "Serve almeno 1 settimana di osservazione."),
            (o.MaxDrawdownPercent > 0m, "Il drawdown massimo dev'essere > 0."),
            (o.HardMaxDrawdownPercent >= o.MaxDrawdownPercent,
                "Il drawdown di hard-block dev'essere >= del drawdown massimo, altrimenti blocca prima di quello che dovrebbe essere il limite."),
            (o.MinWinRate is >= 0m and <= 1m, "Il win rate minimo è una frazione fra 0 e 1."),
            (o.DemoteSharpeThreshold <= o.MinSharpeRealized,
                "La soglia di retrocessione dev'essere <= dello Sharpe minimo di promozione: altrimenti una corsia verrebbe promossa e retrocessa in continuazione."),
            (o.DemoteMinWeeks >= 1, "Serve almeno 1 settimana prima di retrocedere."),
            (o.EvaluationIntervalHours >= 1, "L'intervallo di valutazione dev'essere almeno 1 ora.")),

        LlmOptions o => Check(
            (!string.IsNullOrWhiteSpace(o.Model), "Il modello non può essere vuoto."),
            (o.MaxTokens >= 256, "Max token dev'essere almeno 256: sotto, la risposta viene troncata a metà."),
            (o.PollIntervalMinutes >= 1, "Il poll dev'essere almeno 1 minuto."),
            (o.RequestTimeoutSeconds >= 5, "Il timeout di chiamata dev'essere almeno 5 secondi."),
            (o.BreakerFailureThreshold >= 1, "Serve almeno 1 errore per aprire il breaker."),
            (o.BreakerCooldownMinutes >= 1, "Il cooldown del breaker dev'essere almeno 1 minuto."),
            // [G6] A 0 la spiegazione chiederebbe all'AI di commentare una lista vuota: una chiamata
            // pagata per forza. Il tetto alto tiene il prompt (e il costo) entro una misura sensata.
            (o.ExplainRejectionsTopN is >= 1 and <= 20,
                "I candidati bocciati da riportare per esteso stanno fra 1 e 20."),
            // Il budget per-provider: 0 = spento (comportamento storico), sopra il tetto sarebbe
            // solo un modo confuso di scriverlo — il budget complessivo scade comunque prima.
            (o.PerProviderTimeoutSeconds is >= 0 and <= 600,
                "Il budget per provider sta fra 0 (spento) e 600 secondi.")),

        // [G4] Post-mortem: una soglia a 0 analizzerebbe ogni operazione chiusa in pari, e un
        // tetto a 0 per giro renderebbe l'interruttore acceso indistinguibile da spento.
        ProcioneMGR.Services.Llm.Narration.PostMortemOptions o => Check(
            (o.LossThresholdPercent > 0m, "La soglia di perdita dev'essere > 0 (è un valore positivo: 1,0 = perdite oltre l'1%)."),
            (o.MaxPerRun is >= 1 and <= 50, "Le analisi per giro stanno fra 1 e 50."),
            (o.CommitteeContextCount is >= 0 and <= 50, "I post-mortem passati al comitato stanno fra 0 (nessuno) e 50.")),

        SupervisorAgentOptions o => Check(
            (o.Provider is "Logging" or "Claude", "Provider ammessi: Logging oppure Claude."),
            (o.TimeoutSeconds >= 5, "Il timeout dev'essere almeno 5 secondi.")),

        SentimentOptions o => Check(
            (o.MetricsIntervalMinutes >= 5, "L'intervallo metriche dev'essere almeno 5 minuti (sono API pubbliche di terzi)."),
            (o.NewsIntervalMinutes >= 5, "L'intervallo news dev'essere almeno 5 minuti."),
            (o.Symbols.Count > 0, "Serve almeno un simbolo."),
            (o.BaselineDays >= 1, "Il baseline dev'essere almeno 1 giorno."),
            (o.ExtremeZScore >= 0.5d, "La soglia z degli estremi dev'essere almeno 0,5."),
            (o.FearGreedExtremeLow < o.FearGreedExtremeHigh,
                "La soglia Fear & Greed bassa dev'essere < della alta."),
            (o.FearGreedExtremeLow is >= 0 and <= 100 && o.FearGreedExtremeHigh is >= 0 and <= 100,
                "Le soglie Fear & Greed stanno fra 0 e 100."),
            (o.NewsRetentionDays >= 1 && o.MetricRetentionDays >= 1, "Le retention devono essere almeno 1 giorno."),
            // Guardiano serie-patrimonio: un intervallo a 0 fa esplodere il PeriodicTimer all'avvio
            // (funzione morta in silenzio), una soglia a 0 punti rende il controllo un via libera
            // vuoto, una data-àncora nel futuro è una violazione perpetua per costruzione.
            (o.HeritageGuard.CheckIntervalHours >= 1, "Il controllo di profondità dev'essere almeno ogni 1 ora."),
            // [I15] Le soglie si nominano UNA PER UNA e vanno estese con ogni riga nuova: una
            // chiave dimenticata qui passa a 0 (via libera vuoto) o con l'àncora nel futuro
            // (violazione perpetua) senza che nulla protesti. Vale anche per le righe che nascono
            // NON sorvegliate: l'interruttore si accende, la soglia sbagliata resta.
            (o.HeritageGuard.FundingMinEventsPerSymbol >= 1
                && o.HeritageGuard.FearGreedMinPoints >= 1
                && o.HeritageGuard.LiquidationsMinPoints >= 1
                && o.HeritageGuard.NewsMinPoints >= 1,
                "Le soglie di punti del guardiano devono essere almeno 1: a 0 il controllo è un via libera vuoto."),
            (o.HeritageGuard.FundingMinStartUtc < DateTime.UtcNow
                && o.HeritageGuard.FearGreedMinStartUtc < DateTime.UtcNow
                && o.HeritageGuard.LiquidationsMinStartUtc < DateTime.UtcNow
                && o.HeritageGuard.NewsMinStartUtc < DateTime.UtcNow,
                "Le date-àncora del guardiano devono stare nel passato: nel futuro sono una violazione perpetua.")),

        DriftMonitorOptions o => Check(
            (o.IntervalHours >= 1, "L'intervallo dev'essere almeno 1 ora."),
            (o.RecentCandles >= 20, "Servono almeno 20 candele recenti perché il confronto di distribuzioni significhi qualcosa."),
            (o.MinAlertsToRetire >= 1, "Serve almeno 1 feature in alert per ritirare un Champion."),
            // [I6] Le soglie sono ora amministrabili, quindi vanno anche validate: una soglia di
            // alert MENO severa di quella di warning renderebbe l'alert irraggiungibile — un livello
            // di allarme che non può scattare è peggio di un livello assente, perché sembra esserci.
            (o.Thresholds.PsiBins >= 2, "Il PSI ha bisogno di almeno 2 bin."),
            (o.Thresholds.PsiWarning > 0, "La soglia PSI di warning dev'essere positiva."),
            (o.Thresholds.PsiAlert >= o.Thresholds.PsiWarning,
                "La soglia PSI di alert non può essere più permissiva di quella di warning: l'alert non scatterebbe mai."),
            (o.Thresholds.KsPValueWarning is > 0 and < 1, "Il p-value KS di warning dev'essere fra 0 e 1."),
            (o.Thresholds.KsPValueAlert is > 0 and < 1, "Il p-value KS di alert dev'essere fra 0 e 1."),
            (o.Thresholds.KsPValueAlert <= o.Thresholds.KsPValueWarning,
                "Il p-value KS di alert dev'essere più stringente (più piccolo) di quello di warning: altrimenti l'alert non scatterebbe mai."),
            (o.Thresholds.PageHinkleyDelta >= 0, "La tolleranza di Page-Hinkley non può essere negativa."),
            (o.Thresholds.PageHinkleyWarning > 0, "La soglia Page-Hinkley di warning dev'essere positiva."),
            (o.Thresholds.PageHinkleyAlert >= o.Thresholds.PageHinkleyWarning,
                "La soglia Page-Hinkley di alert non può essere più permissiva di quella di warning: l'alert non scatterebbe mai."),
            (o.Thresholds.MinObservations >= 20,
                "Servono almeno 20 osservazioni per lato perché un test di distribuzione significhi qualcosa.")),

        // [M1] Deriva dei fattori alpha. I limiti sono quelli che il worker applicava in silenzio a
        // ogni lettura (Math.Max/Math.Clamp): dirli al salvataggio è meglio che correggerli dopo,
        // perché un valore fuori scala salvato e poi silenziosamente ricondotto è un pannello che
        // mostra un numero e ne applica un altro.
        ProcioneMGR.Services.Alpha.FactorDriftOptions o => Check(
            (o.IntervalHours >= 1, "L'intervallo del monitor dev'essere almeno 1 ora."),
            (o.MaxSeries is >= 1 and <= 30,
                "Serie per giro fra 1 e 30: sopra, un giro può saturare il database condiviso; sotto, non guarda niente."),
            (o.MaxCandles is >= 1_000 and <= 200_000,
                "Candele per serie fra 1.000 e 200.000: sotto, l'information coefficient è rumore; sopra, il costo del giro esplode.")),

        NotificationOptions o => Check(
            (o.Provider is "Logging" or "Telegram", "Provider ammessi: Logging oppure Telegram."),
            (o.MaxPerHour >= 1, "Il rate-limit dev'essere almeno 1 messaggio all'ora."),
            (!o.Enabled || o.Provider != "Telegram" || !string.IsNullOrWhiteSpace(o.ChatId),
                "Con provider Telegram serve il ChatId di destinazione (il TOKEN del bot no: solo env TELEGRAM_BOT_TOKEN).")),

        CampaignOptions o => Check(
            (o.TickSeconds >= 10, "Il tick del planner dev'essere almeno 10 secondi."),
            // [I7] Zero è legittimo (comportamento storico), negativo no: una pausa negativa
            // scadrebbe nel passato, cioè sarebbe una pausa che non mette in pausa.
            (o.CancelPauseMinutes >= 0, "La pausa dopo un annullamento non può essere negativa (0 = nessuna pausa).")),

        RegimeTriggerOptions o => Check(
            (o.CheckIntervalMinutes >= 1, "L'intervallo di controllo dev'essere almeno 1 minuto."),
            (o.CooldownHours >= 0, "Il cooldown non può essere negativo."),
            (o.VolBandMultiple > 1d, "Il moltiplicatore della banda dev'essere > 1: a 1 la banda è un punto e il trigger scatta sempre.")),

        EnsembleComparatorOptions o => Check(
            (o.MinSharpeImprovementPercent >= 0m, "Il miglioramento minimo di Sharpe non può essere negativo."),
            (o.MinRiskFactorImprovementPercent >= 0m, "Il miglioramento minimo del fattore di rischio non può essere negativo."),
            (o.MinLegs >= 1, "Serve almeno 1 gamba."),
            (o.MinDistinctSymbols >= 1, "Serve almeno 1 simbolo distinto."),
            (o.MinSharpeSignificanceZ >= 0m, "La z di significatività non può essere negativa.")),

        ModelRegistryOptions o => Check(
            (o.MinChampionDeflatedSharpe is >= 0d and <= 1d,
                "Il Deflated Sharpe è una probabilità: la soglia sta fra 0 e 1 (0,95 = significatività difendibile).")),

        MlComparisonOptions o => Check(
            (o.TimeoutMs >= 50, "Il timeout del confronto dev'essere almeno 50 ms."),
            (!o.Enabled || !string.IsNullOrWhiteSpace(o.RemoteUrl),
                "Il dual-read acceso richiede Ml:RemoteUrl (che si applica solo al riavvio: il canale gRPC si crea una volta sola).")),

        RealtimeFeedOptions o => Check(
            (o.SubscriptionRefreshSeconds >= 5, "Il refresh delle sottoscrizioni dev'essere almeno 5 secondi."),
            (o.StaleAfterSeconds >= 5, "La soglia di staleness dev'essere almeno 5 secondi."),
            (o.ReconnectInitialDelayMs >= 100, "L'attesa iniziale di riconnessione dev'essere almeno 100 ms."),
            (o.ReconnectMaxDelayMs >= o.ReconnectInitialDelayMs,
                "Il tetto del backoff dev'essere >= dell'attesa iniziale."),
            (o.MaxSpreadPercent > 0m, "Lo spread massimo accettato dev'essere > 0, altrimenti ogni tick viene scartato.")),

        ProtectiveExitShadowOptions o => Check(
            (o.AlertAboveBps > 0d, "La soglia di allerta dev'essere > 0 bps.")),

        CorrelatedExposureOptions o => Check(
            (o.MaxCorrelatedExposurePercent > 0m, "Il tetto di esposizione correlata dev'essere > 0."),
            (o.MinCorrelationToCount is >= 0d and <= 1d, "La correlazione minima è un valore assoluto fra 0 e 1."),
            (!string.IsNullOrWhiteSpace(o.Timeframe), "Il timeframe non può essere vuoto."),
            (o.LookbackBars >= o.MinOverlappingBars,
                "La finestra di stima dev'essere >= delle barre sovrapposte minime, altrimenti nessuna correlazione sarà mai stimabile."),
            (o.MinOverlappingBars >= 2, "Servono almeno 2 barre sovrapposte per una correlazione."),
            (o.CacheTtl > TimeSpan.Zero, "Il TTL della cache dev'essere > 0.")),

        RegimeRoutingOptions o => Check(
            (o.MinCandles >= 2, "Servono almeno 2 candele per classificare un regime."),
            (o.ModelCheckTtl > TimeSpan.Zero, "Il TTL del controllo modello dev'essere > 0."),
            (o.Rules.Select(r => r.RegimeId).Distinct().Count() == o.Rules.Count,
                "C'è più di una regola per lo stesso regime: gli id devono essere unici."),
            (o.Rules.All(r => r.RegimeId >= 0), "Gli id di regime non possono essere negativi.")),

        LaneInvariantOptions o => Check(
            (o.CheckIntervalSeconds >= 5, "Il check dev'essere almeno ogni 5 secondi."),
            (o.AvailableCapitalTolerance >= 0m, "La tolleranza sul capitale non può essere negativa."),
            (o.MaxAbsPnlCapitalMultiple > 0m, "Il multiplo di PnL dev'essere > 0."),
            (o.MaxExposureCapitalMultiple > 0m, "Il multiplo di esposizione dev'essere > 0.")),

        CarryOptions o => Check(
            // Live non è nemmeno un valore di CarryMode: qui si rifiuta comunque per dirlo a voce
            // alta a chi lo scrive, invece di lasciarlo degradare a Paper con un warning nei log.
            (o.Mode is "Paper" or "Testnet", "Modalità ammesse: Paper oppure Testnet. Il carry non opera MAI con denaro reale."),
            (o.Symbols.Count > 0, "Serve almeno un simbolo."),
            (o.EvaluationMinutes >= 5, "La valutazione dev'essere almeno ogni 5 minuti."),
            (o.TrailingFundingEvents >= 1, "Serve almeno 1 evento di funding nella finestra."),
            (o.PositionSizePercent is > 0m and <= 100m, "La size per gamba è una percentuale fra 0 (escluso) e 100."),
            (o.ExitAnnualFundingPercent < o.EnterAnnualFundingPercent,
                "La soglia di uscita dev'essere < di quella di ingresso: senza isteresi il carry aprirebbe e chiuderebbe nella stessa valutazione.")),

        LiquidationsOptions o => Check(
            (o.FlushMinutes >= 1, "Il flush dev'essere almeno ogni minuto."),
            (o.StaleSeconds >= 60, "La soglia di silenzio dev'essere almeno 60 secondi: !forceOrder@arr è un feed di eventi sparsi, e i vuoti brevi sono normali."),
            (o.BlockedRetryMinutes >= 1, "La pausa dopo un endpoint bloccato dev'essere almeno 1 minuto.")),

        // [2026-08-23] Backup del database. La regola che conta e' l'ultima: un percorso RELATIVO
        // verrebbe risolto contro la directory di lavoro del processo, e quella dell'app non e'
        // quella del Task Scheduler — la pagina guarderebbe una cartella, lo script ne riempirebbe
        // un'altra, ed e' precisamente il difetto che questa sezione esiste per chiudere.
        ProcioneMGR.Services.Admin.BackupOptions o => Check(
            (o.StaleAfterHours >= 1,
                "La soglia di stantiezza dev'essere almeno 1 ora: a 0 ogni backup risulterebbe fermo appena creato."),
            (o.RetentionDays >= 1,
                "La conservazione dev'essere almeno 1 giorno."),
            (!string.IsNullOrWhiteSpace(o.ScheduledTaskName),
                "Serve il nome dell'operazione pianificata: senza, il suo esito non e' interrogabile e la pagina saprebbe solo guardare i file."),
            (string.IsNullOrWhiteSpace(o.NightlyDirectory) || Path.IsPathFullyQualified(o.NightlyDirectory),
                "La cartella dei backup notturni dev'essere un percorso ASSOLUTO (oppure vuota, per %USERPROFILE%\\ProcioneMGR-Backup): "
                + "un percorso relativo significa cartelle diverse per l'app e per il Task Scheduler.")),

        ExecutionParameters o => Check(
            (o.MaxSlices >= 1, "Serve almeno 1 fetta."),
            (o.IcebergClipFraction is > 0m and <= 1m, "Il clip Iceberg è una frazione fra 0 (escluso) e 1."),
            (o.ImpactCoefficient >= 0m, "Il coefficiente di impatto non può essere negativo."),
            (o.MaxImpactPct is > 0m and <= 1m, "Il tetto d'impatto è una frazione fra 0 (escluso) e 1."),
            (o.HalfSpreadPct >= 0m, "Il mezzo spread non può essere negativo."),
            (o.ReferenceVolatility > 0m, "La volatilità di riferimento dev'essere > 0: è il denominatore dell'urgenza."),
            (o.DecayBaseRate >= 0m, "Il tasso di decadimento non può essere negativo.")),

        _ => null,
    };

    /// <summary>Primo predicato falso vince: si mostra un errore alla volta, quello più a monte.</summary>
    private static string? Check(params (bool Ok, string Message)[] rules)
    {
        foreach (var (ok, message) in rules)
        {
            if (!ok) return message;
        }
        return null;
    }
}
