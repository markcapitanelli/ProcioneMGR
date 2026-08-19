# PRD — Integrazione onesta dei sottosistemi accesi (sedicesima ondata, 2026-08-18)

*Nasce da una richiesta del proprietario: «rimangono sei componenti altamente sviluppate che generano
rumore o peso strutturale perché sono tenute parzialmente scollegate, disconnesse o spente per
configurazione — è giunta l'ora di validarli e implementare tutto quello che è stato sviluppato e non
ancora integrato correttamente sia in architettura sia in UI».*

*La validazione è stata fatta prima del piano, contro il codice del 2026-08-18 e contro il database
vero — non contro la memoria, che è una fotografia del 2026-08-04. Ha ribaltato la premessa su tre
punti su sei. Questo documento riporta prima le verifiche, poi il piano che ne discende.*

---

## 1. La scoperta che cambia la diagnosi

**Tre dei sei sottosistemi non sono spenti. Sono accesi, e non producono nulla.**

Il `false` che il proprietario ricorda vive in `ProcioneMGR/appsettings.json.example`. Il file che
l'app carica davvero è `ProcioneMGR/appsettings.json` del repo principale (non tracciato, ultima
modifica 2026-08-11), e dice questo:

| Sezione | `appsettings.json.example` | **File vivo** | Conseguenza |
|---|---|---|---|
| `Campaign:Enabled` | `false` | **`true`** | il pianificatore di cacce è un agente vivo |
| `Fleet:Enabled` / `DryRun` / `UseCommittee` | `false` / `true` / `false` | **`true`** / `true` / **`true`** | la Queen Bee gira e journalizza |
| `Committee:Enabled` | `false` | **`true`** | il comitato è armato |
| `Drift` | sezione presente, `Enabled=false` | **sezione ASSENTE** | valgono i default del POCO |
| `Sentiment` | sezione piena | **`{}` vuoto** | valgono i default, `EnableMlFeature=false` |
| `RegimeTrigger` | `Enabled=true` | **sezione ASSENTE** | valgono i default: acceso, 30′, cooldown 6h |

Due letture da fare subito.

**La prima**: il debito di questi sei non è di *collegamento*. È di **capacità di dire di no** e di
**dichiarazione di copertura**. Il comitato è acceso da sedici giorni e non ha mai votato — non per
guasto, ma perché arbitra i pareggi di `Decide` e la flotta non produce assegnazioni. Il planner è
acceso e non ruota, perché la campagna è in `WaitingForTrigger`. Nessuna delle due cose si legge da
nessuna parte: si legge «acceso», e «acceso» oggi significa «acceso e muto».

**La seconda, ed è una trappola armata**: la sezione `Drift` è *assente* dal file vivo, quindi
accendendo la spunta dal pannello `/admin/autonomy` entrerebbero in vigore i default del POCO —
fra cui `RetireChampionOnAlert = true` ([`FeatureDriftWorker.cs:24`](../ProcioneMGR/Services/Monitoring/Drift/FeatureDriftWorker.cs)).
Il ciclo chiuso partirebbe insieme al monitor, senza che nessuno lo abbia chiesto. Oggi è inerte
solo perché non esiste alcun Champion: è una salvezza per coincidenza, non per progetto.

---

## 2. Sei descrizioni contro sei verifiche

Ogni riga è stata aperta nel file, non dedotta.

### 2.1 Concept drift delle feature ML — **pessimista sul cablaggio, ottimista su due punti**

Il worker **non** è «scritto ma non gira in background»: è registrato incondizionatamente come
hosted service ([`Program.cs:390-391`](../ProcioneMGR/Program.cs)), gira, e valuta `Enabled` a ogni
tick ([`FeatureDriftWorker.cs:61`](../ProcioneMGR/Services/Monitoring/Drift/FeatureDriftWorker.cs)) —
a interruttore spento fa un ciclo a vuoto ogni 6h. Esistono già: pannello completo con cinque
manopole, tabella degli ultimi esiti e pulsante «Esegui check ora» (`Autonomy.razor`, card `p-drift`),
persistenza su `DriftCheckResults`, metrica `procione.drift.alerts`, validazione server-side in
`AdminConfigRules`, copertura pinnata da `ConfigurationUiCoverageTests`, e un percorso on-demand per
singolo modello in `/ensemble`. **Accenderlo è una spunta, non un lavoro di integrazione.**

I due punti ottimisti:

1. **«Mostrando l'allarme direttamente in Home» è falso oggi.** `Home.razor` legge
   `FactorDriftSnapshot`, che è il monitor **omonimo ma diverso**
   ([`Services/Alpha/FactorDriftMonitor.cs`](../ProcioneMGR/Services/Alpha/FactorDriftMonitor.cs),
   filone D2: deriva dell'**IC dei fattori**). Per la deriva delle **feature** in Home non esiste
   nulla. L'omonimia va sciolta una volta per tutte, perché due monitor con lo stesso nome che
   danno due verdetti è il difetto già pagato in D2 e con `SeriesFreshness`.
2. **Il ciclo chiuso non è «robusto ma inattivo»: è senza soggetto.** Il ramo di ritiro pretende
   `model.Stage == ModelStage.Champion`, e la decisione registrata il 2026-07-28 (ROADMAP §B4.a e
   §chiusura) è che **non esiste un solo Champion** — tutti i modelli sono in `Staging`, e la scelta
   di non promuoverne alcuno fu esplicita. Le stesse zero righe rendono inerti altre tre funzioni
   dichiarate «fatte»: la union dei fattori del Champion (Risanamento §2.9), il dual-read ML di B4,
   e le corsie configurate su `MlChampion`.

Un terzo dettaglio, minore ma che cambia la stima di copertura: il primo tick **non** cade dopo
`IntervalHours`. Il loop è `do { corpo } while (WaitForNextTickAsync())`, quindi ogni riavvio del
guscio produce un check a t+60s.

### 2.2 Controlli a caldo — **esatto sui consumatori, ottimista sul valore**

Esatto: `PerformanceControlService` è consumato solo da `BacktestPageService`, `LeverageAdvisor` da
`BacktestPageService` più `tools/StrategyHunter`; nel motore non entra nessuno dei due.

Ottimista su tre cose verificate:

1. **Delle due modalità del servizio, in produzione ne gira una — e non è quella nominata.**
   `ApplyEquityMovingAverageControl` (riga 60), cioè proprio «l'Equity Control», **non ha alcun
   chiamante fuori dai test**. Cablata è solo `ApplyWindowProfitControl`.
2. **Il gemello a caldo esiste già come componente.** `StrategyDecayMonitor` (Sharpe realizzato vs
   atteso, finestra 20 trade) è registrato **anche nel motore**
   ([`ProcioneMGR.Trading/Program.cs:69`](../ProcioneMGR.Trading/Program.cs)) e composto in ogni
   `EnsembleManager` di corsia — ma il suo unico chiamante è la UI del guscio e l'esito si ferma a un
   `LogWarning`. **Non manca la misura: manca l'azione.**
3. **«Prima del rebalance programmato della corsia» presuppone che il rebalance raggiunga la corsia
   viva. Non la raggiunge.** `EnsembleRebalanceWorker` è registrato solo nel guscio, e il motore
   legge la configurazione **una volta sola** in `StartAsync`, senza mai rileggerla. Una gamba
   disattivata da `/ensemble` continua a operare nel motore fino al riavvio della corsia, e **nessuno
   lo dice**.

Sul `LeverageAdvisor`: nessuna corsia opera a leva (`IsFutures` è `false` di default; i tre profili
ammettono leva 1/2/3), e il suo esito è **già incorporato** nel default `MaxLeverageAllowed=5`, con
la motivazione scritta in [`SafetyConfiguration.cs:71`](../ProcioneMGR/Services/Trading/SafetyConfiguration.cs).
È un gate senza soggetto allo stato puro.

### 2.3 Sentiment come feature ML — **ottimista su tre punti, pessimista su uno**

1. **Non è un fattore «macro/sentiment reale»: è news-only.** `SentimentFeatureFactor` delega a
   `SentimentAlphaFactor`, che legge **solo** le notizie RSS scorate (tabella `AltDataPoints`).
   Funding, Fear & Greed, long/short — cioè tutto il patrimonio profondo che il guardiano del
   2026-08-13 sorveglia — vivono in `SentimentMetricPoints`, e **nessun `IAlphaFactor` li legge**.
2. **Il look-ahead era già risolto e testato dal 2026-07**: la finestra interna si ferma a
   `PublishedUtc <= candleTime`, col commento anti-look-ahead in chiaro, e il test lo pinna. Il
   rischio residuo vero è un altro e nessuno lo ha scritto: **skew train/serve**, perché in backtest
   la notizia si vede all'ora di pubblicazione mentre dal vivo entra nello snapshot solo dopo il sync
   (`NewsIntervalMinutes = 60`).
3. **«Integrità temporale verificata» vale per `SentimentMetricPoints`, non per il substrato del
   fattore.** `SentimentSyncWorker:110` cancella `AltDataPoints` oltre `NewsRetentionDays = 180` con
   `ExecuteDeleteAsync`, e il `SentimentHeritageGuardWorker` **non guarda quella tabella**. Il corpus
   della feature è l'unica serie sentiment con una politica di distruzione attiva e senza guardiano:
   è la stessa classe di perdita silenziosa che il funding ha pagato **due volte**.

   In più il vocabolario dei ticker copre 7 crypto (BTC, ETH, SOL, BNB, XRP, DOGE, ADA): sui simboli
   delle corsie fuori da quella lista il fattore è `null` su **ogni** barra, per costruzione.

Pessimista invece qui: **l'IC si può misurare oggi, senza accendere nulla.** Il flag governa i
prototipi dei selettori, non la calcolabilità del fattore.

### 2.4 Campaign Planner — **esatto sull'architettura, sbagliato sullo stato**

Non c'è nulla da collegare: planner, worker, trigger contestuale, pagina con CRUD e «Tick adesso»,
17 test, pannello in `/admin/autonomy`, copertura pinnata. **Ed è acceso** (§1). L'unica cosa che
trattiene la rotazione H24 è il flag per-campagna sulla riga di `VettingCampaigns` e lo stato
`WaitingForTrigger`, che resta inerte finché un wake non lo riporta a `Rotating` — e il trigger
contestuale, mancando la sezione dal file vivo, gira coi default (acceso, 30′, cooldown 6h).

Due correzioni di merito.

- **La roadmap si contraddice con sé stessa e nessuna delle due righe si legge dal codice**:
  §A4 dichiara «verificato: nessuna azione — senza campagne abilitate `CheckAsync` esce alla prima
  query», §Filone R dichiara «1 attiva, `WaitingForTrigger`». Non possono essere entrambe vere.
- **«Ogni run chiama il supervisore AI» è per metà falso.** La chiamata di veto parte **solo** dal
  ramo `sopravvissuti > 0`, che da cinque settimane non si verifica mai. Quello che costa davvero a
  ogni run è una chiamata `advisory` del `LlmSupervisorWorker`.

E un difetto trovato leggendo: **un annullamento umano diventa un motivo per ripartire subito.**
Un run annullato a mano da `/pipeline` finisce nel ramo `Failed/Cancelled`, la config viene marcata
`Failed` e la rotazione passa alla successiva, che essendo mai eseguita è sempre eleggibile — entro
un tick da 60s parte un altro run automatico. Chi annulla ottiene il contrario di ciò che voleva.

### 2.5 Pairs trading — **la persistenza manca davvero, i «risultati» vanno corretti**

`PairCandidate` compare **una sola volta in tutto il repo**, e non è codice: è la proposta scritta in
[`docs/audit/06_INTEGRATION_GAPS.md:412`](audit/06_INTEGRATION_GAPS.md) (G-17). I risultati finiscono
oggi in tre posti, tutti ciechi: gli artefatti `PipelineArtifacts` con `Kind="PairScreen"`, **scritti
dal 2026-07-02 e mai letti da nessuno** (l'unico riferimento nel codice è la riga che li scrive); la
memoria del circuito Blazor della pagina; lo **stdout** della fase `pairs` di `PlatformExpand`, dove
vivono le misure C2/E1 vere.

Due difetti veri della pagina, che vanno chiusi **prima** di persistere qualunque numero:

1. **Due verità sullo z-score.** La pagina passa all'engine l'estimatore scelto
   (`HedgeRatioEstimator = _estimator`, default Kalman) ma disegna il grafico con un
   `new RollingPairsSpreadAnalyzer()` fisso. Scegliendo Kalman si vede la curva dell'OLS: il grafico
   descrive un backtest diverso da quello eseguito. È la contraddizione che
   `docs/pagine/pairs-trading.md` dichiara **impossibile** («nessuna doppia verità») — la doc è
   invecchiata al C2 del 2026-07-26.
2. **Slippage zero.** La pagina costruisce la configurazione **senza** `SlippagePercent`, mentre
   `/backtest` parte da `PipelineCosts.DefaultSlippagePercent` = 0,05% e il tool di ricerca lo
   applica. Su una strategia a **due gambe** il costo si paga due volte per trade: lo sconto è il più
   grande possibile esattamente dove fa più danno. Il funding sulla gamba short non è modellato
   affatto.

Il verdetto misurato resta quello di [`REPORT-E1-STATARB`](REPORT-E1-STATARB-2026-07-24.md) e di §C2:
**zero coppie confermate, classe pairs non schierata (0/5 sopravvissuti)**. La persistenza serve
quindi alla **ricerca** e all'eventuale forward test, non a una linea operativa già vinta — ed è
comunque un buon motivo per costruirla, con lo stesso argomento che vinse per `FactorIcWindows`: la
storia dello spread non è ricalcolabile per sempre.

### 2.6 Comitato AI e flotta — **acceso, e muto per fame**

Misurato sul database vero:

- `LlmUsageRecords` **non ha una sola riga con `Path='committee'`** (l'unico path è `advisory`: 92
  chiamate, 302.793 token, dal 02/08 al 18/08);
- tutte e **89** le righe di `OrchestratorDecisions` (03/08 → 18/08) hanno `VotesJson='[]'` e
  `Source` in (`rules`, `human`);
- il journal ha 83 `ProposeGrey`, 6 `Assign` umani, **zero `Retire` e zero `Blocked`** — e zero
  `Blocked` implica coda sempre vuota, quindi nessun menù, quindi **nessuna domanda al comitato**.

A monte c'è l'inedia: le corsie di flotta **3, 4, 5 hanno chiuso 1 trade ciascuna** e le **6, 7 zero**
dall'avvio (13-15 giorni), cioè ~0-2 trade/mese contro un orizzonte dichiarato intraday/swing breve e
contro `RetireMinTrades = 20`. **Non saranno mai ritirabili, quindi non si libera mai una corsia.**

E il dedup dei grigi (AF2c-5) non è fatto: la roadmap dice «lo stesso candidato proposto 7 volte in 2
giorni»; il dato reale al 18/08 è **40 riproposte** di `Composite DOT/USDT 1h` e **39** di
`GridMeanReversion XRP/USDT 4h` in 15 giorni, perché `AlreadyHandled` deduplica per `RunId` invece
che per identità del candidato. Sono anche 79 notifiche Telegram nello stesso budget da 20/ora che
serve gli allarmi veri.

AF2b è **confermato aperto**: con `DryRun=false` il worker emette solo un warning e journalizza
comunque `Applied=false`, e `IPipelineApplier` non ha alcun overload con `targetLanes`.

Il rovescio positivo, e va detto perché è l'argomento a favore: il comitato è il **meno rischioso**
dei sei se acceso, e non per opinione — le proprietà sono pinnate dai test (provider spazzatura al
100% ⇒ equivalente a comitato spento; parità ⇒ default deterministico; quorum mancato ⇒ default;
scelta fuori menù ⇒ astensione). Il problema non è la sua sicurezza: è che **nessuno può leggere un
voto**, perché la tabella del journal mostra `Source` e non espande `VotesJson`.

---

## 3. Le quattro decisioni del proprietario (2026-08-18)

Prese **prima** del piano, con le opzioni e la raccomandazione sul tavolo.

1. **Bersaglio dell'ondata**: *rendere misurabile e sicuro ciò che c'è*. Ogni sottosistema acceso
   deve poter **dire di no** e dichiarare la propria copertura; niente automatismo nuovo finché non si
   vede **perché tace**. Unica eccezione a caccia di valore: **F12**, la capacità del carry — l'unica
   cosa con edge positivo misurato che oggi nessuno sta dimensionando.
2. **Flotta**: si sblocca l'esecuzione, **ma partendo dal ritiro**. Prima il secondo criterio per
   **inedia** col suo strumento di misura, ancora in DryRun; liberate le corsie morte, allora start/stop
   reali su corsie Paper. Il comitato arriva a votare per conseguenza, non per decreto.
3. **Champion**: confermato che **non se ne promuove nessuno**. Le quattro funzioni restano, ma i
   pannelli dichiarano «Champion in carica: 0» **calcolato dal conteggio vero** e disabilitano le
   manopole inerti spiegando perché. Il giorno che ne esiste uno, si riabilitano da sole.
4. **Corpus notizie**: **esentato dalla purge**, con la sua riga nel guardiano di profondità. Senza,
   qualunque lavoro sulla feature ML costruisce su una serie che si autodistrugge.

---

## 4. Il piano

Ogni item è reversibile, e **da spento deve lasciare il comportamento bit-identico** — verificato al
livello 2 dello [Standard di verifica](STANDARD-VERIFICA.md), non promesso. Ogni gate dichiara
**dove si legge il numero**; dove lo strumento non esiste, costruirlo fa parte dell'item (è la lezione
dei tre gate insoddisfacibili del 2026-07-28).

### Fase 0 — Le dichiarazioni (rischio nullo, zero comportamento nuovo)

| # | Cosa | Stato | Gate / strumento |
|---|---|---|---|
| **I1** | **Sonda di stato degli agenti all'avvio del guscio.** `AgentStateProbe` censisce i quattro agenti del guscio (planner, flotta, comitato, drift) e per ognuno emette **uno di quattro stati**: `Spento`, `AccesoInerte`, `AccesoOperante`, `NonDeterminabile`. Card «Stato degli agenti autonomi» in cima a `/admin/autonomy`, con «Ricontrolla» | **fatto (2026-08-18)** — 19 test | ✅ L1 sulla parte pura, caso per caso; ✅ **L2, il controllo che conta**: soggetti in abbondanza (4 campagne, 999 modelli, 5 provider con chiave, 12 corsie) coi gate spenti ⇒ **nessun agente acceso** — se fallisse, la sonda leggerebbe la presenza di dati come attività; ✅ il caso-trappola: keyring muto ⇒ `NonDeterminabile`, mai `Spento`; ✅ lo **stato reale del 18/08** ricostruito dai numeri veri come test di regressione. L4 da fare sull'app vera |
| **I2** | **«Champion in carica: 0», calcolato.** Conteggio letto da `SavedMlModels`, mostrato nella card `p-drift`; a zero le due manopole del ritiro sono **disabilitate con la spiegazione** e l'avviso dichiara che l'interruttore risulta ARMATO per il default del POCO. `null` ⇒ «non determinabile», mai `0` | **fatto (2026-08-18)** per la card del drift; restano gli altri tre punti (union dei fattori §2.9, dual-read ML, selettore `MlChampion`) | il numero mostrato coincide con `SELECT count(*) FROM "SavedMlModels" WHERE "Stage"='Champion'` — due strade, stesso numero. Il giorno che passa a 1 il pannello si riabilita **senza toccare codice**: è il test di non-regressione della dichiarazione |

> **Deviazione dichiarata su I1 (2026-08-18), e il motivo.** L'item chiedeva `LogWarning` **+ una
> notifica**, e chiedeva **silenzio** sull'agente acceso a zero soggetti. Costruendolo sono emerse
> due ragioni per fare diversamente, entrambe scritte nel doc-comment della classe:
>
> 1. **La sonda non notifica affatto.** Lo stato degli agenti non cambia da solo: non è un
>    *evento*, è una *condizione*. Le condizioni si mostrano (log all'avvio + card), gli eventi si
>    notificano — è la stessa distinzione che `SeriesFreshnessWatchWorker` codifica notificando la
>    TRANSIZIONE e non lo stato. Il guscio riparte a ogni logon e sotto watchdog: una notifica per
>    avvio brucerebbe il budget condiviso `Notifications:MaxPerHour` (20, item I4) che serve agli
>    allarmi veri, cioè il difetto già pagato con la staleness su STX.
> 2. **«Acceso a zero soggetti» non è silenzio: è lo stato più insidioso dei quattro**, ed è
>    esattamente quello del comitato AI il 2026-08-18. Tacere su un agente acceso e inerte
>    riprodurrebbe il buco che questa sonda esiste per chiudere. Quindi quattro stati e non due, con
>    `NonDeterminabile` separato da `Spento` per la stessa ragione per cui li tiene separati
>    `LaneCountCoherenceProbe`.
>
> **Non fatto di proposito**: riallineare `appsettings.json.example` ai valori del file vivo. Il
> file example è il template di un'installazione nuova, e portarlo a `Campaign/Fleet/Committee =
> true` farebbe **nascere ogni installazione con gli agenti accesi**. Il difetto non era il valore
> nell'example: era che nessuno potesse leggere lo stato vero. Quello lo chiude la sonda.

#### La revisione avversaria della Fase 0, e le tre bugie che ha trovato (2026-08-18)

La prima versione della sonda è stata passata a tre lenti indipendenti (correttezza, coerenza col
progetto, «controlli che rassicurano»), ognuna col suo confutatore. Diciassette finding proposti,
**otto sopravvissuti**, e i confutatori hanno fatto il loro lavoro: hanno declassato metà delle
gravità e smontato errori di fatto in entrambe le direzioni. Il risultato utile è che la sonda nata
per dire con precisione che cosa agisce da solo **mentiva in tre punti**, e sempre nella stessa
direzione: *sistema più autonomo del vero*.

| Cosa diceva | Cosa dice il codice | Correzione |
|---|---|---|
| Con `Fleet:DryRun=false`: «ESECUZIONE ATTIVA: può avviare e fermare corsie da solo» | **Falso.** Finché AF2b non esiste, spegnere il dry-run non accende nulla: il worker emette un warning e journalizza comunque `Applied=false`. La sonda leggeva il FLAG e ne deduceva la CAPACITÀ — misurava contro sé stessa | Costante `FleetOrchestratorWorker.ExecutionArmImplemented`, dichiarata **accanto al ramo che la implementerà**. Chi scrive AF2b cambia una riga e la sonda dice la verità senza altre modifiche |
| Comitato con tutti gli interruttori a posto: «ACCESO E OPERANTE · arbitra i pareggi dell'orchestratore» | **Falso nel caso fondativo.** È esattamente lo stato in cui il comitato è rimasto sedici giorni senza un voto. Il fatto autoritativo era già in database e la sonda non lo guardava | Il verdetto si misura sui **voti realmente emessi** nel journal, finestra dichiarata di 14 giorni. Zero voti ⇒ `AccesoInerte` con la causa vera |
| Campagna non in rotazione: «un wake del trigger contestuale la rimette in rotazione da solo» | **Falso per metà dei casi.** `WakeAsync` filtra `Status != Observing`: una campagna in osservazione non è MAI risvegliata. E il gate `RegimeTrigger:Enabled` non veniva guardato affatto | Conteggio per stato + il gate del trigger nei fatti, e **tre messaggi distinti** invece di uno che vale per un caso solo |

**E un difetto che avevo introdotto io, della classe che questa ondata esiste per bonificare.** Con
zero Champion disabilitavo le due manopole del ritiro automatico — nella stessa card in cui l'avviso
dichiara che l'interruttore è **ARMATO**. Il ragionamento era invertito due volte: *(a)* l'unico
momento utile per disarmare il ciclo chiuso è proprio quello in cui non c'è ancora un Champion da
perdere; *(b)* il blocco non impediva nulla — «Salva» resta attivo, `@bind` conserva il valore del
modello e `SaveAsync` serializza l'intero POCO, quindi si sarebbe potuto **persistere `true` ma non
più toglierlo**. Un controllo che toglie il rimedio e lascia passare il rischio. Le manopole sono
tornate sempre modificabili e tutto il contributo di I2 è nel testo dell'avviso, che dice la verità.

Una nota che il confutatore ha aggiunto e che **non riguarda questa modifica**: un modello `Retired`
non è ripromuovibile dalla UI (`Registry.razor` offre «→ Champion» solo per `Staging`/`Challenger`, e
`TryPromoteToChampionAsync` rifiuta i `Retired`). Un ritiro accidentale sarebbe quindi irreversibile
da interfaccia. È un difetto **pre-esistente del registry**, da trattare a parte.

### Fase 1 — Gli strumenti prima degli interruttori

| # | Cosa | Gate / strumento |
|---|---|---|
| **I3** | **Il tetto di spesa AI che esiste davvero.** `Llm:Budget:TrackingEnabled` è già `true`, ma `DailyCallLimit`, `DailyTokenLimit` e `MonthlyTokenLimit` sono **tutti a 0**, e un limite a 0 non si applica: il controllo che il comitato esegue prima di ogni giro interroga un budget che **risponde sempre sì**. Misurare in `/admin/ai-supervisor` quanto costa una giornata reale (oggi: 92 chiamate advisory, 302.793 token in 16 giorni) e valorizzare i tre limiti. **Precondizione non negoziabile** di ogni item che aumenta le chiamate | L1: alla (N+1)-esima chiamata con `DailyCallLimit=N` il guard torna `SkippedBudgetExhausted` **senza muovere il breaker** (caso già modellato). L2: coi limiti a 0 il numero di chiamate servite in una giornata simulata è identico a oggi. L4: il pannello mostra il consumo e il residuo, e il residuo **cala** dopo una chiamata vera |
| **I4** | **Il budget delle notifiche non è infinito, e nessuno lo aveva sommato.** `Notifications:MaxPerHour = 20` è **condiviso** fra drift, deriva fattori, flotta, campagne, comitato, freschezza serie, guardiano patrimonio, digest — e le 79 riproposte grigie di 15 giorni ci sono già dentro. Sei sorveglianti su venti messaggi/ora significa che **il primo che sbaglia soglia zittisce gli altri cinque**. Raggruppamento per sorgente + una **spia di soppressione** leggibile in pagina (oggi la soppressione vive solo in un `LogWarning`) | L1: N messaggi della stessa sorgente in una finestra ⇒ un messaggio aggregato col conteggio. L2: sotto il tetto, il flusso è **carattere per carattere** identico a oggi. L4: la spia mostra «k notifiche soppresse nell'ultima ora» sull'app vera, e si azzera |
| **I5** | **F7 ridotto all'osso: la copertura si legge dal DB, non dai contatori di processo.** `MetricsCollector` è un `MeterListener` **in-processo** con una lista **hardcoded** di tre istogrammi, registrato solo nel guscio: il motore non ha né collector né pagina. Conseguenza per questa ondata: **ogni gate nella forma «il numero compare in `/metrics`» è insoddisfacibile** per qualunque metrica del motore, e silenziosamente inefficace per ogni istogramma nuovo. Non si fa F7 per intero: si tolgono i tre istogrammi dal codice a favore della scoperta dinamica, e i gate di questa ondata si scrivono **contro il database e l'audit log**, non contro `/metrics` | L1: un istogramma nuovo compare in pagina **senza modificare la lista**. L2: i tre storici restano identici. L4: `/metrics` sull'app vera continua a dichiarare, come già fa, che i contatori del motore restano a zero per costruzione |

#### Esecuzione della Fase 1 (2026-08-18) — fatta, con una deviazione dichiarata

Tutti e tre gli item **eseguiti in codice con test** (livelli 1-2; L3/L4 col resto dell'ondata).

- **I3** — Il badge diceva «tracking attivo», che si legge come *«la spesa è sorvegliata»*, mentre i
  tre limiti a `0` significano che `CheckBudget` **risponde sempre sì**. Ora, quando nessun tetto è in
  vigore, il pannello lo **dichiara** e dà i numeri per sceglierne uno: chiamate e token di oggi, e la
  media al giorno sul mese in corso. Un dettaglio che sembra un cavillo e non lo è: la condizione si
  legge da `BudgetMonitor.CurrentValue`, **non dai campi del form** — quelli sono la modifica che si
  sta digitando, e mostrarli come stato in vigore è la stessa forma dei pannelli che dichiaravano
  applicata una configurazione mai salvata.
- **I4** — `NotificationRateLimitPressure` sul dispatcher: quanti messaggi nell'ultima ora, quanti ne
  restano prima del silenzio, quanti soppressi **in attesa** e quanti **da questo avvio**. La
  distinzione fra i due conteggi è il punto: il primo si azzera col messaggio successivo (giusto, lì
  vengono dichiarati), il secondo mai — senza, un'occhiata al pannello un minuto dopo la tempesta
  direbbe che non è successo niente. 7 test, fra cui il controllo che **leggere la spia non consuma
  slot** e che la finestra scorre anche senza nuovi invii (altrimenti mostrerebbe come occupati slot
  liberi da un'ora: un valore vecchio spacciato per attuale).
- **I5** — Scoperta dinamica degli istogrammi: si raccoglie qualunque `Histogram<double>` del meter di
  piattaforma invece dei tre nomi cablati. I tre attesi restano pre-registrati **anche a zero misure**,
  perché «riga assente» e «non è ancora successo niente» non sono la stessa cosa. Gli strumenti double
  non-istogramma finiscono fra i contatori invece di essere scartati in silenzio. 4 test, e **la
  verifica che contava**: i due che asseriscono il comportamento nuovo sono stati eseguiti contro il
  codice *precedente* e **falliscono**, gli altri due passano in entrambi — una verifica che non può
  fallire non è una verifica.

> **Deviazione dichiarata su I4.** La pressione del rate-limit resta **sul guscio** e non passa dal
> canale gRPC. Il contratto `GetNotificationChannelStatusResponse` porta cinque campi e non questo:
> mapparlo comunque farebbe leggere **«0 soppresse» da qualunque motore**, cioè la rassicurazione
> falsa che questa ondata esiste per bonificare. Il pannello **dichiara il buco** al posto dello zero.
> È la scelta giusta anche nel merito: tutti e otto i sorveglianti che si contendono il tetto vivono
> nel guscio; il tetto del motore serve i suoi allarmi di quarantena, che sono pochi e di un'altra
> natura. Il campo si aggiunge quando servirà — è additivo, come i campi 22/23 di E6.

### Fase 2 — La capacità di dire di no (il cuore dell'ondata)

| # | Cosa | Gate / strumento |
|---|---|---|
| **I6** | **Drift delle feature: acceso in sola segnalazione, capace di fallire, e visibile in Home.** Quattro cose, in quest'ordine. **(a) Lo strumento del costo prima dell'interruttore**: istogramma `tick_ms` e contatori modelli/candele/feature, più una riga di log riassuntiva a fine tick — senza, «quanto costa» è un'opinione. **(b) La capacità di fallire**: colonna additiva `SkipReason` su `DriftCheckResult`, e marcatura esplicita come **SALTATO** (non come «pulito») quando la finestra corrente si sovrappone al periodo di training, quando le candele recenti non bastano al lookback dei fattori, o quando il modello non è valutabile. Oggi tutti e tre producono `Overall=None`, cioè **il verdetto rassicurante indipendentemente dalla realtà**. **(c) L'accensione**: `Drift:Enabled=true` **e `RetireChampionOnAlert=false` scritti esplicitamente nel file vivo** — chiude la trappola della sezione assente (§1). **(d) L'allarme in Home**, riusando il pattern D2 invece di reinventarlo: singleton `FeatureDriftSnapshot` modellato su `FactorDriftSnapshot`, scritto a fine tick e **ricostruito all'avvio** dall'ultimo tick in `DriftCheckResults` (il guscio si riavvia di continuo: è la ragione che vinse in D2.b), con l'etichetta che distingue le due derive. In più: le soglie (PSI 0,2/0,25 · KS p<0,05/0,01 · Page-Hinkley 25/50) oggi si cambiano **solo ricompilando** — vanno in configurazione e in pannello, come pretende il mandato del 2026-08-09 | **L2 è il livello che qui conta più di tutti**, e va scritto come due test che **oggi passano col verdetto sbagliato**: devono fallire prima della modifica e passare dopo — (i) `RecentCandles` al minimo legale ⇒ `SkipReason` valorizzato e **non** `Overall=None` con `TotalFeatures>0`; (ii) finestra corrente interamente dentro il periodo di training ⇒ SALTATO. Più il controllo sul rumore: su serie senza deriva, 20 semi, **zero** allarmi. L1: `candles_read` di un tick combacia entro ±1% col conteggio SQL sulle stesse finestre. L3: Postgres vero, retention e prune. L4: Home mostra il blocco col conteggio e **la copertura**, e con `Enabled=false` non compare nulla |
| **I7** | **Campagne: un annullamento umano è un ordine, non un esito.** Distinguere `Cancelled` da `Failed` e, sul `Cancelled`, mettere **la campagna** in pausa per una durata configurabile invece di far ripartire la rotazione entro 60s. In più, rendere **esplicito** il rapporto fra `Campaign:Enabled` e `AutoReapply:Enabled`: oggi il percorso campagna chiama l'applier senza consultare il gate della ri-applica, che è letto solo dallo scheduler. Con la ri-applica spenta, il percorso campagna **schiera lo stesso** — e questa è una decisione di prodotto sul significato di quell'interruttore, non un dettaglio (vedi §7, domanda 1) | L1: con `Status="Cancelled"` nessun run parte prima della scadenza della pausa; con `Failed` il comportamento resta quello di oggi (il test esistente non deve cambiare). L2: pausa a 0 ⇒ sequenza di config avviate **identica** al test odierno. L4: annullare un run da `/pipeline` e verificare sull'app vera che la campagna dichiari la pausa con la scadenza |
| **I8** | **Flotta e comitato: rendere leggibile il silenzio.** **(a)** I quattro numeri che lo spiegano, accanto ad «azioni ultimo piano»: candidati in banda pass in coda, candidati grigi, corsie di flotta libere, menù arbitrabili prodotti negli ultimi 7 giorni. Sul database di oggi devono leggere «0 / 83 / 0 / 0», che è la diagnosi. **(b)** `Source='default'` disambiguato: oggi collassa budget esaurito, zero provider configurati e quorum mancato in un'unica parola. **(c)** `VotesJson` espandibile nella riga del journal — un comitato che vota senza che nessuno possa leggere il voto è «verde a livello di classe, inesistente a livello di prodotto». **(d)** Pulsante **«Prova il comitato»**: una domanda sintetica a menù chiuso (`Kind='probe'`), voti reali provider per provider, e per gli astenuti **la causa**. È l'unico modo di sapere che funziona prima che serva | L1: tre client finti che rispondono spazzatura ⇒ 3 astensioni con la loro causa e verdetto = default. L2: con `Committee:Enabled=false` il pulsante risponde «spento», **zero** chiamate, e il tick della flotta resta bit-identico (`Source='rules'`, `VotesJson='[]'`). L3: Postgres vero, la riga di consumo viene scritta col path `committee`. L4: i quattro numeri combaciano con la query indipendente, sull'app vera |
| **I9** | **Sentiment: lo strumento misura ciò che il modello vede.** **(a)** La copertura in `/data-availability` diventa **per simbolo e per barra** invece che globale: percentuale di barre del periodo con valore del fattore **non nullo**, calcolata con lo stesso `ToBaseTicker` del fattore; se il ticker è fuori dal vocabolario, lo dice — oggi la pagina dichiara una copertura che il fattore non ha. **(b)** Il pannello IC di `/sentiment` conta le notizie con punteggio nullo come `0m`, mentre la via ML **le esclude**: due numeri diversi per la stessa domanda. **(c)** Pavimento di numerosità (`MinObservations`, default 0 = comportamento odierno) nella selezione per IC, perché un fattore nullo sul 95% delle barre può avere |IC| alto sul residuo ed è rumore | **L1 come riferimento indipendente**: la percentuale mostrata coincide col conteggio dei non-null di `Compute` sulle stesse candele — due strade per lo stesso numero. **L1 sul difetto (b)**: il test che confronta l'IC del pannello con l'IC costruito attraverso `SentimentNewsProvider` **oggi fallisce** — è la prova che il difetto esiste. **L2**: con `MinObservations=0` la classifica è **bit-identica** a oggi, fattore per fattore, cifra per cifra; e un fattore sintetico nullo sul 95% delle barre non deve mai comparire in testa |
| **I10** | **Pairs: una sola verità, e i costi in chiaro.** **(a)** Il grafico z-score si costruisce dall'oggetto che l'engine ha **davvero** usato, non da un `RollingPairsSpreadAnalyzer` fisso. **(b)** L'`AdvancedPanel` espone le manopole vive oggi invisibili: `SlippagePercent`, `StopZScore`, `MaxSpreadVolRatio`, col default di piattaforma (0,05% per gamba) e la dichiarazione che il costo si paga **due volte per trade**. **(c)** Ogni backtest lanciato dalla pagina apre un `ExperimentRun` di `Kind="Pairs"` coi parametri completi e le metriche — così i numeri smettono di morire col circuito Blazor | **L1**: con estimatore Kalman la serie disegnata coincide punto per punto con lo z-score del ramo Kalman. **Il test deve fallire sul codice attuale**: se passa subito, non è una verifica. **L4**: la stessa coppia con Kalman e con Rolling OLS deve produrre **due** curve diverse; oggi ne produce una sola. E: `/experiments` mostra una riga `Kind="Pairs"` col capitale finale identico a quello a schermo |

### Fase 3 — Il denominatore condiviso

| # | Cosa | Gate / strumento |
|---|---|---|
| **I11** | **«Trade attesi dall'holdout»: una regola sola, consumata da due.** Campo additivo `ExpectedTradesPerMonth` sulla gamba, scritto al momento dello schieramento (dai due percorsi: grigi e assegnazione), calcolato **sul simbolo attuale** (criterio AF2c-2: le corsie hanno vite precedenti — la corsia 0 aveva 159 trade storici, tutti su altri simboli) e col **tempo-al-verdetto dichiarato** (criterio AF2c-3: a ~2 trade/mese servono 10 mesi per 20 trade; non è un divieto, ma va detto allo schieramento). **Perché è un item a sé**: lo consumano il ritiro per inedia (I12) e il freno per gamba (I13), e se ognuno se lo calcolasse da sé avremmo **due regole che rispondono alla stessa domanda** — il difetto già pagato in D2 e con `SeriesFreshness` | L1: lo stimatore ricostruisce, sull'holdout di un run reale, lo stesso trade/mese che il run dichiara. L2: campo assente (corsie pre-flotta 0-2) ⇒ nessun consumatore agisce e lo si **dichiara**, invece di assumere zero. L4: il numero compare accanto alla corsia in `/trading` insieme al tempo-al-verdetto |

### Fase 4 — Le azioni, dietro il denominatore

| # | Cosa | Gate / strumento |
|---|---|---|
| **I12** | **Ritiro per inedia + dedup dei grigi, ancora in DryRun; poi AF2b su corsie Paper.** **(a)** Secondo criterio di ritiro dentro `Decide`, accanto a quello per Sharpe: dopo `RetireMinWeeks`, una corsia che ha prodotto meno di una **frazione dichiarata** dei trade attesi (I11) viene condannata, con la stessa isteresi `RetireConfirmTicks`. **(b)** Dedup delle proposte grigie per **identità** (`StrategyName\|Symbol\|Timeframe`) invece che per `RunId`, con soppressione anche quando quell'identità è già schierata su una corsia che gira: colonna additiva `CandidateKey`. **(c)** Solo dopo che (a) ha liberato corsie nel journal: `Fleet:DryRun=false` e `targetLanes` nell'applier, **una corsia per volta, solo Paper** | **L1 con lo stato reale di oggi**: la regola condanna le corsie 3, 5, 6, 7 e **non** condanna la 2 (avviata il 14/08, troppo giovane), né le corsie 0-2 dell'impronta, né le quarantenate. **L2**: soglia a 0 o interruttore spento ⇒ piano **bit-identico** su 100 tick fuzzati; nessun grigio in gioco ⇒ journal bit-identico. **L3**: Postgres reale, migrazione additiva su database vergine **prima** del reale. **L4**: 40 riproposte in 15 giorni devono diventare 1; e la prima esecuzione reale è **una** corsia Paper, verificata nel browser e dentro il pod |
| **I13** | **Freno per gamba: prima la misura, poi (solo se il gate passa) l'azione.** **(a)** Estendere l'avviso di deriva della configurazione — oggi limitato a simbolo e timeframe — all'insieme delle **gambe attive**: una gamba disattivata da `/ensemble` continua a operare nel motore fino al riavvio della corsia, e nessuno lo dice (§2.2). Diagnostica pura, fail-open, una notifica per transizione. **(b)** Pannello di **sola lettura** accanto al «Monitor decadimento» di `/ensemble` che applica `ApplyWindowProfitControl` e `ApplyEquityMovingAverageControl` ai trade **veri** di ogni gamba. **(c)** **Condizionato all'esito di (b)**: freno a caldo nel motore, applicato esattamente dove si applica `mayOpen` del router di regime — la gamba non **apre** più finché la metrica non risale; chiusure per segnale, inversioni e uscite protettive restano intatte. Mai un `continue` sulla gamba: lascerebbe posizioni orfane, e questa piattaforma ne ha già avuta una | **Il gate di (b) è il vero bivio, ed è onesto ammettere che può chiudere il filone**: con corsie a 2-6 trade/mese, «≥20 trade chiusi sul simbolo attuale» potrebbe restituire **zero gambe misurabili** — nel qual caso il pannello lo dice e (c) non si fa. Si prosegue solo se su almeno metà delle gambe misurabili il freno migliora il drawdown senza mangiare il profitto, **e** se il controllo sul rumore tace. **L1**: gli `ExecutedFlags` del freno coincidono con `PerformanceControlService` sugli stessi trade — il riferimento indipendente **esiste già in repo**. **L2**: spento ⇒ replay di ≥500 candele su corsia Paper produce ordini, trade e stato **bit-identici** |

### Fase 5 — Persistenza, ma solo insieme al suo lettore

Regola inversa a quella già imparata sui gate: **uno strumento va scritto insieme a chi lo legge.**

| # | Cosa | Gate / strumento |
|---|---|---|
| **I14** | **`PairCandidate` + `PairSpreadWindow`.** `PairCandidate` è una tabella **derivata** che indicizza a righe gli 86 artefatti `PairScreen` già in database e mai letti, copiando il progetto di `ResearchCandidateIndex` (indice unico che decide le gare fra processi, difensivo per run, ricostruibile). `PairSpreadWindow` registra la storia dello spread sul pattern `FactorIcWindows`: upsert idempotente, `HydrateAsync` all'avvio, e un worker che per le coppie **scelte dall'operatore** calcola hedge ratio, spread, z-score e ADF di finestra. Il lettore è un pannello in `/pairs-trading`. Sola lettura, nessuna decisione automatica | **L1**: `RebuildAsync` produce un numero di righe pari a `SUM(jsonb_array_length(...))` sugli artefatti, e run indicizzati pari al conteggio degli artefatti `PairScreen` (86 al 2026-08-06). **L2 controllo sul rumore, ed è il livello decisivo**: su **due random walk indipendenti** il monitor non deve **mai** dichiarare cointegrazione né allarme di rottura; su una relazione piantata, deve trovarla. **L2 idempotenza**: rebuild eseguito due volte ⇒ stesse righe, nessun duplicato. **L3**: migrazione additiva su Postgres vergine prima del reale |
| **I15** | **Corpus notizie esentato dalla purge, con il suo guardiano** (decisione 4 del proprietario). `AltDataPoints` con punteggio esce da `NewsRetentionDays` come già fanno funding, Fear & Greed e liquidazioni; nuova riga «notizie scorate» in `SentimentHeritageGuard` con `NewsMinStartUtc` / `NewsMinPoints` / `NewsEnforced`, stesso schema di `LiquidationsEnforced`: **da spenta la riga resta misurata e mostrata come «non sorvegliata», mai un OK finto** | **L1**: profondità e conteggio del guardiano coincidono con `SELECT min(TimestampUtc), count(*)` eseguito a mano. **L2**: su un corpus profondo il guardiano **tace** per tre giri consecutivi; una soglia nel futuro o a 0 punti viene **rifiutata** da `AdminConfigRules`. **L4**: la card in `/sentiment` e l'alert in Home nelle due configurazioni (sorvegliata e non) |

### Fase 6 — L'unica eccezione a caccia di valore

| # | Cosa | Gate / strumento |
|---|---|---|
| **I16** ≡ **F12** | **Capacità e universo del carry.** È l'unica classe con edge misurato **positivo** (netto 5,5-11,9%/anno, riprodotto nella chiusura del Risanamento), l'unica che opera oggi, e l'unica che nessuno sta dimensionando — mentre il benchmark esterno registra il basis in compressione da 25% a <5% in due anni. Premio per simbolo ed exchange, frequenza dei flip, curva di capacità con √-impact | **Report con verdetto scritto anche se il verdetto è «la soglia attuale è già ottima»** — un'analisi che può concludere solo in un modo non è un'analisi. Con trade/mese e durata mediana della posizione dichiarati, come pretende l'orizzonte di riferimento |

---

## 5. Non-obiettivi, con la motivazione

Nel progetto un non-obiettivo motivato è un deliverable di pari dignità di un item: serve a impedire
che qualcuno ricominci una strada già percorsa credendola nuova.

1. **Promuovere un Champion per dare un soggetto al ciclo chiuso del drift.** È ragionare al
   contrario, ed è la stessa forma-senza-sostanza respinta esplicitamente il 2026-07-28. Quattro
   funzionalità dipendono da una decisione di prodotto: **si dichiara** (I2), non si aggira.
2. **`LeverageAdvisor` a caldo.** Nessuna corsia opera a leva, e il suo esito è già incorporato nel
   default `MaxLeverageAllowed=5`. **Condizione di riapertura, falsificabile**: esiste almeno una
   corsia `Futures` con ≥20 trade chiusi a leva. Se la condizione si verifica e l'item resta chiuso,
   la dichiarazione è violata.
3. **Esecuzione pairs a due gambe.** Il motore esegue su corsie **mono-simbolo**; una coppia richiede
   due gambe coordinate, cioè toccare il confine dello scrittore unico. **Condizione di riapertura**:
   F13 eseguito con CPCV + gemello sintetico + power check F4, con almeno una coppia che sopravvive
   all'holdout e batte il P99 del nullo. Oggi la classe è a 0/5 sopravvissuti.
4. **Consumo del meta-labeling** (decoratore di `IStrategy`, sizing per probabilità). Misurato in
   C4.c: alza la precision da 5,7% a 19,2% e batte il caso di 7,05 σ, **ma conserva il 2% dei segnali
   e il rendimento medio peggiora**. Cablarlo nel motore significa costruire un amplificatore sul
   percorso dei soldi per un edge che dodici esiti negativi dicono non esserci. *Il meta-labeling
   amplifica, non crea.*
5. **Cablaggio dell'OFI come segnale.** Edge reale (p 0,005) ma **6-34× sotto il costo del giro**, e
   i dump `bookTicker` non esistono (404). Verdetto già scritto in C5.a/D3.a.
6. **G7 — parere del comitato accanto al click Live.** Il comitato non ha ancora votato una sola
   volta e il click Live non è mai stato esercitato. Mettere un parere AI accanto all'unico gate
   umano irreversibile è il rapporto rischio/valore peggiore dell'intero corpo. **Fuori da questa
   ondata per intero.**
7. **R4 — riesame dei 702 bocciati per potenza statistica.** È l'unico bacino con Sharpe medio
   positivo mai prodotto, ed è **precisamente per questo** che riesaminarlo «con la finestra giusta»
   significa scegliere la finestra dopo aver visto i risultati. Se si fa, si fa come esperimento
   dichiarato con la finestra fissata **prima** — non dentro un'ondata il cui tema è rendere
   misurabile e sicuro ciò che c'è.
8. **API di configurazione remota per i sei sottosistemi.** Non serve: sono **tutti e sei del
   guscio**, e in tutti e sei manopola e consumatore coincidono. È l'unica buona notizia strutturale
   della validazione — la classe di difetto «regola 6» qui **non c'è**, e va detto invece di
   costruire un canale per prudenza.
9. **F7 per intero** (`/metrics` ricollegata al motore). Si fa solo il minimo che rende misurabili i
   gate di questa ondata (I5); il collegamento vero resta il suo item.

---

## 6. La somma dei carichi: tre risorse finite, non una

Nessuna analisi per sottosistema può vederlo, perché il carico si somma **fra** sottosistemi. Chi
somma solo la CPU sbaglia bersaglio due volte su tre.

**Postgres condiviso.** Un tick di drift legge centinaia di migliaia di righe OHLCV più i blob di 53
modelli, ogni 6h, contro lo stesso database che serve motore e ingestion. Aggiungi flotta (15′),
promozioni (6h), planner, e la sorveglianza dello spread — **l'unica proposta che introduca un carico
di scrittura permanente**. È la condizione che il 2026-08-13 e il 2026-08-15 sono già costati due
giorni. Da qui l'ordine: I6(a), lo strumento del costo, viene **prima** dell'accensione.

**Un solo processo, un solo thread pool.** Tutti e sei stanno nel guscio, insieme ad altri worker
periodici. Un tick di drift da 60 secondi ritarda il tick della flotta e quello del planner: è
esattamente il modo di guasto «worker senza budget di ciclo» già pagato il 2026-08-13.

**Le due che nessuno aveva contato.** Il **budget delle notifiche** (I4) e il **budget AI** (I3),
entrambi condivisi e oggi entrambi senza tetto vero. Sono in Fase 1 per questo.

**E una nota di copertura che va dichiarata, non subita**: il guscio in cluster è a **zero repliche**;
i diciotto worker periodici vivono nel processo locale, avviato al logon da `bringup.ps1` e sorvegliato
ogni 5′ da `watchdog.ps1`. La copertura è quindi **uptime dell'host**, non «sessioni di lavoro» — ma
la gamba di riparazione del watchdog passa da `run-postgres.ps1`, che muore col cluster giù, cioè
proprio nella situazione in cui il watchdog scatta. Ogni pannello alimentato da un worker del guscio
deve **dichiarare la propria copertura**, come già fa il monitor di deriva dei fattori.

---

## 7. Domande residue (non bloccanti: il piano procede, l'esito cambia un item)

1. **`Campaign:Enabled=true` con `AutoReapply:Enabled=false`: il percorso campagna deve smettere di
   schierare, o deve continuare?** Oggi continua, perché il planner chiama l'applier senza consultare
   il gate della ri-applica. È una decisione sul **significato** di quell'interruttore. *Ipotesi con
   cui procedo se non arriva risposta*: il percorso campagna **rispetta** `AutoReapply:Enabled` — un
   interruttore che spegne la ri-applica da una porta e la lascia aperta dall'altra è la stessa forma
   dei pannelli che scrivevano sul processo sbagliato.
2. **La frazione dei trade attesi sotto cui una corsia è morta** (I12a): 20% dopo tre settimane è il
   mio default. E il timeframe 1d va escluso dalla flotta, o solo dichiarato allo schieramento?
3. **Le 79 notifiche da proposte grigie ripetute**: il dedup deve sopprimere anche la **notifica**, o
   la vuoi raggruppata una volta al giorno?
4. **Le coppie sorvegliate** (I14): scelte a mano, o alimentate da ciò che lo screening marca
   `IsTradeable`? La seconda è comoda ma sceglie fra centinaia di test ADF per timeframe **senza
   correzione per test multipli** — cioè fabbrica candidati per costruzione.

---

## 8. Ordine di esecuzione

```
Fase 0  I1 · I2                      dichiarazioni — rischio nullo, si fanno per prime
Fase 1  I3 · I4 · I5                 gli strumenti, prima degli interruttori
Fase 2  I6 · I7 · I8 · I9 · I10      la capacità di dire di no — il cuore
Fase 3  I11                          il denominatore condiviso
Fase 4  I12 · I13                    le azioni, e solo dietro il denominatore
Fase 5  I14 · I15                    persistenza, insieme al suo lettore
Fase 6  I16 ≡ F12                    l'unica eccezione a caccia di valore
```

Le Fasi 0-2 sono **tutte a rischio nullo o basso** e non cambiano una sola decisione operativa:
aggiungono la capacità di fallire e la dichiarazione di copertura a superfici che oggi rassicurano a
prescindere. La Fase 4 è la sola che cambia una decisione, ed è dietro I11 di proposito.

**L'invariante dell'ondata**, ereditato dal filone G e non negoziabile: ogni item è default-off o
reversibile, ognuno ha la sua manopola in UI (mai un flag che vive solo in `appsettings.json`), e
ognuno spento deve lasciare il comportamento **bit-identico** a prima — verificato al livello 2, non
promesso.
