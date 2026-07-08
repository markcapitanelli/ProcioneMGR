# Report — Monitor di Decadimento Strategia (Realizzato vs Atteso)

## Obiettivo

Trasformare "l'edge è morto?" da intuizione a segnale misurabile: confrontare automaticamente lo
Sharpe **realizzato** (dai trade effettivamente chiusi da `TradingEngine` in Paper/Testnet/Live)
di ogni gamba dell'ensemble con lo Sharpe **atteso** dal backtest/holdout che l'ha validata,
con alert quando il realizzato scende sotto una soglia configurabile per un numero sufficiente
di trade.

## File modificati/creati

- `Services/Ensemble/EnsembleModels.cs` — `EnsembleStrategy` +3 campi nullable
  (`ExpectedSharpe`, `ExpectedProfitFactor`, `ExpectedMaxDrawdown`); nessuna migrazione EF
  necessaria (l'ensemble è persistito come JSON, come già accertato nel lavoro precedente
  sullo stop-loss).
- `Services/Pipeline/PipelineModels.cs` — `ProposedLeg` +3 campi (`HoldoutSharpe`,
  `HoldoutProfitFactor`, `HoldoutMaxDrawdown`), copiati 1:1 dal `ValidatedCandidate` originario
  (che li aveva già, ma restavano incorporati solo nel testo del `BestCandidate`/`DisplayName`).
- `Services/Pipeline/Stages/DecisionStages.cs` — `EnsembleAssemblyStage` popola i 3 nuovi campi.
- `Components/Pages/Pipeline.razor` — `ApplyRecommendationAsync` traduce le metriche holdout in
  `EnsembleStrategy.Expected*`.
- **`Services/Monitoring/StrategyDecayMonitor.cs`** (nuovo) — `IStrategyDecayMonitor`/
  `StrategyDecayMonitor`, `DecayMonitorOptions`, `DecayReport`. Puro/deterministico, nessuna
  dipendenza da DB o orologio nel calcolo.
- `Services/Ensemble/IEnsembleManager.cs` / `EnsembleManager.cs` — nuovo metodo
  `GetDecayReportsAsync()`: carica la configurazione + tutti i `TradeRecords` (via
  `IServiceScopeFactory`, stesso pattern già usato per `LoadConfigAsync`) e chiama il monitor
  per ogni gamba.
- `Program.cs` — registrazione `IStrategyDecayMonitor` (Singleton, stateless).
- `Components/Pages/Ensemble.razor` — 3 colonne editabili in più nella tabella delle gambe
  (Sharpe/PF/MaxDD attesi); nuovo pannello "Monitor decadimento" con una card per gamba
  (Sharpe atteso/realizzato/delta/%, stato, messaggio, link a Trading), bottone "Aggiorna",
  filtro "solo gambe in alert"; `AddFromSaved` popola `ExpectedSharpe` da
  `SavedStrategy.OptimizationSharpe`; `GuidaPanel` aggiornato con la distinzione
  Rolling-Sharpe-simulato vs Monitor-decadimento-realizzato.
- **`Components/Pages/Home.razor`** (non `Dashboard.razor` — vedi deviazioni) — widget di
  alert quando una o più gambe sono in decadimento, con link al dettaglio in `/ensemble`.
- `ProcioneMGR.Tests/StrategyDecayMonitorTests.cs` (nuovo) — 9 test unitari.
- `ProcioneMGR.Tests/EnsembleManagerDecayTests.cs` (nuovo) — 2 test di integrazione.

## Decisioni architetturali

### 1. Annualizzazione dello Sharpe realizzato: **trade/anno stimati dalla cadenza reale**, non candele/anno

Questo è il punto tecnico più delicato del prompt, esplicitamente segnalato come ambiguità da
risolvere. Lo Sharpe **holdout** del backtest è annualizzato con `sqrt(candele/anno)` del
timeframe (`Statistics.PeriodsPerYear`, es. 365×24 per 1h) — perché i suoi "periodi" sono
candele, non trade. Lo Sharpe **realizzato** qui invece ha come "periodi" i TRADE, che non
avvengono a ogni candela. Usare `sqrt(candele/anno)` per il realizzato (come suggerito alla
lettera dal prompt) avrebbe prodotto numeri privi di senso: una strategia con un trade a
settimana su un timeframe 1h avrebbe un'annualizzazione basata su 8760 candele/anno invece che
sui reali ~52 trade/anno, gonfiando lo Sharpe realizzato di oltre 100×.

**Soluzione adottata**: si stima la cadenza REALE dei trade dal campione stesso —
`trade/anno = N / giorni_di_ampiezza_del_campione × 365` — e si annualizza con
`sqrt(trade/anno)`, la convenzione standard per uno Sharpe "a trade" quando la frequenza non è
fissa. L'ampiezza è vincolata ad almeno 1 giorno per evitare stime patologiche su campioni
compressi (es. un burst di trade nello stesso giorno). Documentato estesamente nel codice
(`StrategyDecayMonitor.ComputeRealizedMetrics`) e bloccato da un test di regressione dedicato
(`RealizedSharpe_MatchesIndependentlyComputedAnnualization`) che ricalcola la stessa formula
indipendentemente e verifica l'uguaglianza esatta.

### 2. Sharpe atteso non positivo: la soglia percentuale non è applicabile

Se `ExpectedSharpe <= 0`, un rapporto realizzato/atteso può capovolgere il significato (es.
-0.6/-1.2 = 50% "sembrerebbe" un dimezzamento quando -0.6 è in realtà MEGLIO di -1.2). In questo
caso il monitor calcola comunque `RealizedSharpe` e `SharpeDelta` (utili a vista) ma non genera
un alert basato sul rapporto, e lo segnala esplicitamente in `StatusMessage`. Coperto dal test
`NonPositiveExpectedSharpe_SkipsRatioAlert_ButReportsDelta`.

### 3. `DecayMonitorOptions.WindowTradeCount` unifica finestra e soglia minima

Il prompt descrive sia "le ultime N operazioni (rolling, default 20)" sia "alert se sotto soglia
per 20+ trade" — stesso numero, due ruoli. Un solo parametro (`WindowTradeCount = 20`) copre
entrambi: è insieme l'ampiezza della finestra rolling e il minimo di trade richiesto prima di
poter valutare, evitando due opzioni ridondanti che dovrebbero sempre essere tenute in sync.

### 4. Rinominato `AlertMessage` → `StatusMessage`

Il campo è sempre valorizzato (anche quando NON c'è alert: "trade insufficienti", "metriche non
disponibili", "in linea"), quindi il nome originale del prompt sarebbe stato fuorviante.
Deviazione minore, cosmetica, documentata qui come richiesto.

### 5. Nessuna migrazione EF per `EnsembleStrategy`; nessuna estensione di `SavedStrategy`

Come nel lavoro precedente sullo stop-loss, `EnsembleConfiguration` è JSON (nessuna colonna EF
da aggiungere). `SavedStrategy` invece **non è stata estesa**: oggi ha solo `OptimizationSharpe`
(nessun campo per ProfitFactor/MaxDrawdown), quindi le strategie aggiunte all'ensemble da
"ottimizzata" (`AddFromSaved`) portano con sé SOLO lo Sharpe atteso, non PF/MaxDD (restano
null, mostrati come "—" nella card). Estendere `SavedStrategy` con questi due campi in più
(più una migrazione EF, più modifiche a `Discovery.razor`/`Optimization.razor` per salvarli)
è un lavoro a sé, fuori dal perimetro di "solidità > velocità > feature extra" di questo task:
segnalato come prossimo passo consigliato.

### 6. `EnsembleManager.GetDecayReportsAsync()` interroga `TradeRecords` direttamente, non `ITradingEngine`

Il prompt immaginava un modello EF con `Ensembles`/`Positions` collegati da chiavi esterne — non
esiste (l'ensemble è JSON, `TradeRecord.StrategyId` è la stringa GUID condivisa con
`EnsembleStrategy.StrategyId`, non un intero). `EnsembleManager` interroga `db.TradeRecords`
con lo stesso pattern già usato per `LoadConfigAsync` (scope + `IDbContextFactory`), evitando di
introdurre una dipendenza Ensemble→Trading che non esiste oggi (è vero il contrario: Trading
dipende da Ensemble). Include ORA trade di **tutte** le modalità (Paper/Testnet/Live): scelta
deliberata per massimizzare il campione disponibile, specialmente nelle prime settimane quando
esistono solo trade Paper — documentata qui, non filtrata per non ridurre inutilmente i dati
disponibili quando ce n'è già poco.

### 7. Widget di alert su `Home.razor`, non `Dashboard.razor` — deviazione dal prompt

Verificato che `Components/Pages/Dashboard.razor` (route `/dashboard`) **non è** una dashboard
riassuntiva: è l'esploratore dati di mercato OHLCV (fetch al volo + grafico candele), senza
alcun contesto di ensemble/trading iniettato. La vera pagina "overview" con statistiche e
percorso guidato è `Home.razor` (route `/`, voce di menu "Home"). Il widget "gambe in alert" è
stato aggiunto lì, accanto alle altre stat card esistenti, con `try/catch` non bloccante (se il
monitor fallisce per qualunque motivo, la home resta comunque utilizzabile).

## Test

- **9 test unitari** (`StrategyDecayMonitorTests.cs`, dati sintetici, nessun DB):
  meno-trade-della-finestra → nessun alert con messaggio esplicativo; `ExpectedSharpe` null →
  nessun alert, messaggio dedicato; realizzato molto sotto l'atteso → alert; realizzato in
  linea → nessun alert; atteso non positivo → soglia percentuale disattivata ma delta comunque
  calcolato; varianza zero → Sharpe 0 senza eccezioni; Profit Factor realizzato = grossProfit /
  |grossLoss|; **regressione sull'annualizzazione** (ricalcolo indipendente della formula);
  trade di altre gambe correttamente ignorati.
- **2 test di integrazione** (`EnsembleManagerDecayTests.cs`, `EnsembleManager` reale + SQLite
  su file temp, nessun DB in-memory EF): un report per gamba con isolamento corretto dei trade
  per `StrategyId`; nessun trade → report con `TradeCount=0`, nessun alert.
- **Suite completa**: 441/441 verdi (430 preesistenti dopo il lavoro sullo stop-loss + 11
  nuovi), 0 regressioni.
- **Build**: 0 errori, 0 warning nuovi (i 4 preesistenti non toccati da questo lavoro).

## Verifica browser

- `/ensemble`: le 3 nuove colonne (Sharpe/PF/MaxDD attesi) compaiono nella tabella delle gambe
  reali; il pannello "Monitor decadimento" (`#decay-monitor`) mostra correttamente **entrambe**
  le gambe reali dell'utente ("MacdTrend NEAR/USDT 4h [base]", "Momentum NEAR/USDT 4h [SL5]")
  con badge "In attesa" e messaggio "Trade insufficienti (0/20)" — comportamento corretto, dato
  che nessun trade è ancora stato eseguito dal vivo per queste gambe. Il filtro "Mostra solo
  gambe in alert" verificato funzionante (nasconde entrambe le card quando attivato, dato che
  nessuna è in alert).
- `/` (Home): nessun widget di alert visibile — corretto, coerente con `TradeCount=0` su
  entrambe le gambe. Nessun errore in console né nei log del server.
- **Non è stato simulato un decadimento reale inserendo TradeRecord fittizi nel DB di
  produzione dell'utente**: farlo avrebbe permanentemente contaminato lo storico trade reale
  con dati inventati. Lo scenario "alert scatta quando previsto" è invece verificato in modo
  deterministico e ripetibile dal test `RealizedFarBelowExpected_TriggersAlert` (dati sintetici)
  e dal test di integrazione `EnsembleManagerDecayTests` (DB isolato su file temporaneo, mai
  quello reale) — stessa scelta prudente già adottata nel lavoro precedente per non toccare lo
  stato condiviso dell'utente senza necessità.

## Rifiniture (dopo revisione dei criteri di qualità, su richiesta esplicita dell'utente)

Rileggendo i criteri non negoziabili del prompt originale, due non erano ancora del tutto
soddisfatti al momento della prima consegna:

1. **Tracciabilità (criterio 6, mancante)**: non c'era alcun log quando un alert veniva
   generato. `GetDecayReportsAsync` ora chiama `logger.LogWarning(...)` per ogni gamba in
   alert, con Sharpe realizzato/atteso/rapporto e conteggio trade. Non è stato riusato
   `TradingAuditLog` (come suggerito dal prompt): quella tabella richiede un `TradingMode`
   obbligatorio legato a una sessione di trading specifica, concetto che non si applica bene
   qui (un decay report può derivare da trade misti Paper/Testnet/Live) — forzare un valore
   sarebbe stato fuorviante in un log pensato per azioni di preciso ordine/posizione.
2. **Performance/scalabilità (criterio 3, insufficiente sul lungo periodo)**:
   `GetDecayReportsAsync` caricava l'**intera** tabella `TradeRecords` in memoria a ogni
   chiamata (una per ogni apertura di `/ensemble` E di `/` Home), per poi filtrare in-memory
   per gamba. Con l'operatività quotidiana che fa crescere lo storico nel tempo, sarebbe
   diventato un collo di bottiglia reale. Corretto con una query PER GAMBA, filtrata e
   limitata (`ORDER BY ClosedAtUtc DESC LIMIT 20`) direttamente al DB — costo O(gambe), non
   O(storico) — supportata da un nuovo indice composito `(StrategyId, ClosedAtUtc)` su
   `TradeRecords` (migrazione `AddTradeRecordStrategyClosedIndex`, applicata e verificata).

Verificato che entrambe le correzioni non alterano il comportamento osservabile: suite
completa ancora 441/441 verde, verifica browser ripetuta sul pannello `/ensemble` (dati
identici a prima, nessun errore in console/log).

## Nota Binance Futures — MiCA (fuori standard, aggiunta su segnalazione dell'utente)

L'utente ha segnalato che dal 1° luglio 2026 Binance ha cessato i servizi di derivati e leva
finanziaria per i residenti nello Spazio Economico Europeo (regolamento MiCA) — lo Spot resta
disponibile, ma **Binance Futures non è più un'opzione utilizzabile dall'Italia**. Bitget
diventa quindi l'UNICO exchange su cui la piattaforma può operare a leva per questo utente.
Aggiunto un warning contestuale (non bloccante, puramente informativo) in due punti:
- `Components/Pages/Ensemble.razor` — visibile quando "Futures (leva)" è selezionato con
  Exchange = Binance.
- `Components/Pages/ExchangeSettings.razor` — nella guida, accanto alla nota sulla passphrase
  Bitget.

Nessuna modifica al codice di trading/exchange (il client Binance Futures resta funzionante
per chi non è soggetto a questa restrizione; la piattaforma non fa geo-detection automatica).

## Prossimi passi consigliati

Questo era il punto 2 della roadmap (il singolo elemento con il maggior impatto su "massimizzare
i profitti" secondo l'analisi iniziale). Il punto 3 (verifica/provisioning credenziali futures)
si restringe ora a **sole credenziali Bitget** (Binance Futures non è più percorribile per
l'utente). Restano poi, nell'ordine già proposto:
4. Periodo di osservazione Paper obbligatorio prima di Testnet/Live.
5. Schedulazione automatica delle cacce del pipeline.

Suggerimento emerso da questo lavoro specifico: estendere `SavedStrategy` con `ProfitFactor`/
`MaxDrawdown` persistiti (oggi solo `OptimizationSharpe`), così le strategie aggiunte da
"ottimizzata" avrebbero un confronto completo nel monitor tanto quanto quelle applicate dal
Pipeline.
