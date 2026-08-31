# PRD — La Regina che governa: dall'esecuzione automatica al governo autonomo (diciottesima ondata, 2026-08-31)

*Nasce da una richiesta del proprietario, testuale: «voglio che faccia tutto da solo, analisi,
controlli, test, campagne, backtest, pipeline, nuove strategie, modifiche dei parametri per nuove
strategie o campagne o backtest, TUTTO deve essere fatto in autonomia, ancora meglio se controllato
dalla queen bee o da delle AI dove è possibile».*

*Il metodo è quello del Filone I e del Filone J: prima le verifiche, poi il piano che ne discende.
Sei lenti indipendenti hanno misurato codice vivo, configurazione viva e database reale il
2026-08-30/31; sei avversari hanno provato a demolirle. **Un rilievo su quattro non è sopravvissuto
alla confutazione**, compreso uno mio, e le correzioni cambiano il piano — non lo decorano.*

---

## 1. La diagnosi: non manca l'autonomia, manca il governo

La piattaforma **esegue** da sola quasi tutto. Non **decide** nulla, non **si mantiene** e non sa
**in che stato è**. Un'autonomia che non produce mai un'uscita, vista da fuori, è indistinguibile
dal non averla — ed è esattamente ciò che il proprietario percepisce.

Tre affermazioni, ognuna con la sua prova, che insieme spiegano la sensazione:

1. **La catena è fail-closed in ogni giunto, e nessun giunto ha mai lasciato passare niente.** In
   trenta giorni: 138 run di ricerca completati, **0 in banda «pass»**, 115 decisioni della flotta
   **tutte** `ProposeGrey`, **zero** schieramenti automatici, **zero** ritiri da sempre
   (`SELECT "Kind", count(*) FROM "OrchestratorDecisions"` → ProposeGrey 109, Assign 6 tutte
   `Source='human'` fra il 3 e il 13 agosto).
2. **Due piani su tre girano codice vecchio, e nessuna superficie lo dice.** Il motore si aggiorna
   da solo ogni 30′; il guscio e la plancia no. Stanotte la plancia si è riavviata da un binario del
   25/08, **13 commit indietro**, privo del fix `6fee9f7` — e il supervisore si è appeso sullo
   stesso pipe ereditato dell'incidente del 28 agosto, fermando veglia, deploy e il backup delle
   03:30. Il guscio si è allineato **per incidente**, non per progetto: il riavvio della macchina
   ha fatto ricompilare `dotnet run`.
3. **Nessuno sceglie.** La rotazione è una lista scritta a mano di quattro configurazioni; nessuno
   ritocca i parametri della caccia; nessuno genera ipotesi nuove; e il comitato AI è acceso da
   settimane **su un ramo irraggiungibile**.

### La scoperta che cambia la diagnosi

**Il braccio è già armato, scatterà da solo fra quattro giorni, e nessuno lo sa.**

La premessa da cui partivano tutte le analisi precedenti — «manca `ExpectedTradesPerMonth`, quindi
il ritiro non può maturare» — è **falsa**. J9 è stato eseguito il 2026-08-25 e le cinque corsie di
flotta hanno l'atteso scritto:

| corsia | gamba | atteso | trade dall'ancora |
|---|---|---|---|
| 3 | RsiOversold ETC/USDT 4h | 2,47/mese | **0** |
| 4 | GridMeanReversion XRP/USDT 4h | 2,88/mese | **0** |
| 5 | GridMeanReversion UNI/USDT 4h | 3,70/mese | 1 (−51,89) |
| 6 | Composite LTC/USDT 15m | 2,17/mese | **0** |
| 7 | BollingerMeanReversion STX/USDT 4h | 3,50/mese | **0** |

*(`SELECT s->>'expectedTradesPerMonth' FROM "EnsembleStates", LATERAL
jsonb_array_elements(("ConfigurationJson")::jsonb->'strategies') s` — le chiavi sono **camelCase**:
la stessa query in PascalCase restituisce zero righe, ed è così che il fatto è stato dato per
accertato al contrario, da me compreso.)*

L'unico gate rimasto è `StarvationMinDays = 10` (default del POCO, `FleetModels.cs:67`, chiave
assente dall'`appsettings` vivo) contro 5,12 giorni di osservazione cumulata. Servono 864.000
secondi, ne mancano 421.271: **soglia raggiunta il 2026-09-04 verso le 19:15 UTC.** Con `DryRun`
spento, `ExecutionLanes = [3,4,5,6,7]`, `RetireConfirmTicks = 2` e `MaxExecutionsPerTick = 1
(default)`, la flotta fermerà **quattro corsie su cinque** — 3, 4, 6, 7 — una per tick da 15
minuti, **nell'arco di un'ora**, con notifica Telegram per ognuna.

E subito dopo non schiererà nessuno, per tre ragioni indipendenti (§4.2).

**Il ritiro non è irreversibile**: `ExecuteRetireAsync` chiama solo `engine.StopAsync`, la corsia
resta configurata e si riavvia con un click. Ma la decisione va presa **prima** del 4 settembre, e
oggi non c'è nessuna superficie che la annunci.

---

## 2. La catena, anello per anello

| # | Anello | Da solo? | Prova |
|---|---|---|---|
| 1 | **Dati** | ✅ | 222/222 serie abilitate entro **2,32 barre** del proprio timeframe; `ingestion-sync` batte ogni 5′. Le 12 serie stantie sono tutte già disabilitate |
| 2 | **Ricerca** | ✅ | 4-8 run/giorno dal 25/08; riarmo a 13h e finestre scorrevoli funzionanti; **42,4 ore di pipeline al mese**, 152 run/mese |
| 3 | **Archivio della ricerca** | ❌ | `ResearchCandidates` fermo al **25/08 13:15**: **34 run completati non indicizzati**. `IResearchCandidateIndexer` è iniettato **solo** in `ResearchPageService` — l'archivio cresce quando qualcuno apre `/research` |
| 4 | **Validazione** | ⚠️ | **94 chiavi distinte** con Sharpe holdout ≥ 0,5 fermate dal conteggio trade (69% delle 136 in guadagno), fra cui il massimo d'archivio: Supertrend ADA/USDT 4h, **3,19 su 17 trade**. Ma il DSR non ha **mai** raggiunto 0,95: 0 righe su 15.149, massimo storico 0,7729 |
| 5 | **Decisione — campagna** | ❌ | **18 artifact `AutoReapplyGreyBlocked` su 18 tentativi** di config 19/20. Config 17 e 18: **119 run completati su 119 con zero gambe assemblate** — non raggiungono nemmeno il valutatore |
| 6 | **Decisione — flotta** | ❌ | `GreyAutoDeploy=true` dal 25/08 su un serbatoio vuoto: **18 identità su 18** della finestra a 30 giorni sono già `AlreadyHandled` |
| 7 | **Comitato AI** | ❌ | mai interrogato. Il menù nasce solo con ≥1 assegnazione in banda «pass» e coda >1: **0 run pass su 138**. Zero righe `Blocked` in tutto il journal |
| 8 | **Esecuzione** | ⚠️ | 7/7 corsie `IsRunning`, candele fresche — **ultimo ordine di qualunque corsia: 24/08**; ultimo trade chiuso: **27/08 16:00** |
| 9 | **Misura** | ✅ | il decay monitor vede: corsia 1 Sharpe −1,75 vs atteso 4,05; corsia 4 **−21,08 vs 2,10** su 20 trade |
| 10 | **Ritiro** | ⚠️ | armato, scatta il **4-5 settembre** — e per il motivo sbagliato (§4.1) |
| 11 | **Ritorno alla ricerca** | ❌ | non esiste: nessun verdetto del forward test rientra nella prossima caccia |
| 12 | **Manutenzione di sé** | ❌ | nessuna sonda confronta la revisione viva con `HEAD`, su nessuno dei tre piani |

---

## 3. Cinque cose che oggi nessuno fa

1. **Nessuno sceglie cosa cacciare.** Quattro config in rotazione, scritte a mano. Config 17 e 18
   hanno consumato 57 tentativi ciascuna e prodotto **zero gambe su 119 run**: nessuno le mette in
   sonno, nessuno sposta il budget altrove.
2. **Nessuno tocca i parametri della ricerca.** Universo, `topN`, ampiezza delle finestre,
   `confirmTopN`, timeframe: tutti fissi. `MKR/USDT` è delistato da Binance (`BREAK`, ultima candela
   **2025-09-15**) ed è ancora nell'universo di 17 e 18: produce 11 chiavi candidate con **0 trade in
   holdout** e gonfia del 3% i tentativi che entrano nel DSR.
3. **Nessuno genera ipotesi nuove.** Il catalogo dei segnali è fisso; `CreativeDiscovery` ricombina.
   Il generatore via LLM è chiuso per decisione, e la motivazione regge: più tentativi alzano SR\*.
4. **L'AI non decide niente e non le si chiede niente.** `Committee:Enabled` e `Fleet:UseCommittee`
   sono accesi su un ramo che non si può raggiungere. Il supervisore ha solo potere di veto — e per
   giunta gira **prima** del comparatore (`RunApplyEvaluator.cs:109` contro `:112`), quindi ogni run
   che supera la guardia grigia paga una chiamata LLM anche quando il verdetto è già scritto: il
   costo esatto che il commento a `:81-83` dichiara di voler evitare.
5. **Nessuno sorveglia chi sorveglia.** L'heartbeat incrociato guscio↔motore è **spento** (sezione
   `Heartbeat` assente ⇒ default `false`): «l'altro host è muto» non può scattare. Il carry — l'unica
   classe con edge positivo misurato — vive nel pod e il suo guardiano nel guscio fa
   `GetService<CarryWorker>()`, che è sempre `null`: `Fleet:CarrySilenceAlertHours` è una manopola
   che non può scattare. E il rate limiter delle notifiche è **cieco alla severità**: a budget pieno
   un `Critical` viene **scartato**, non accodato.

---

## 4. Le tre catene bloccate, misurate

### 4.1 Il ritiro: un conteggio travestito da giudizio

Due criteri, il primo che morde vince (`FleetOrchestrator.cs:52`).

**Il criterio Sharpe non può esprimersi.** Pretende 20 trade dall'ancora
(`FleetOrchestrator.cs:45-47`): a 2,17-3,70 trade/mese servono **da 5,4 a 9,2 mesi**. Tutte e cinque
le corsie sono in perdita realizzata oggi (da −8,72 a −287,10) e nessuna è ritirabile per questo.
Verranno fermate per non aver operato, non per aver perso. In più, il numeratore e il denominatore
verrebbero da due storie diverse: `GetPerformanceAsync(from:)` filtra solo i trade, mentre lo Sharpe
è calcolato sulla curva di equity **in memoria dell'avvio corrente** — esattamente l'ancoraggio che
il commento a `FleetStateReader.cs:88-89` dichiara di aver corretto.

**Il criterio inedia condanna sul rumore.** A 10 giorni l'atteso vale 0,71-1,15 trade e la soglia è
il 20% di quello, cioè 0,14-0,23: **qualunque corsia con zero trade è condannata**. Sotto l'ipotesi
nulla «la corsia opera esattamente al ritmo atteso», la probabilità di Poisson di vedere zero trade
è **44% (c3), 39% (c4), 49% (c6), 32% (c7)**. Da un terzo a metà delle corsie *sane* verrebbe
fermata comunque. Non c'è nessun test di potenza dietro il verdetto — il livello 2 dello standard di
verifica, applicato a una decisione operativa, non è mai stato preteso qui.

Il controesempio che lo dimostra: **la corsia 5 si salva per un solo trade**, e quel trade ha perso
51,89. Un trade in perdita compra 36 giorni di immunità (fino al ~5 ottobre) mentre quattro corsie a
zero vengono fermate al giorno 10. Il criterio premia l'aver operato, non l'aver operato bene.

**Nessun criterio di danno conclamato esiste.** `FleetLaneState` non porta né drawdown né PnL, che
pure stanno già in `TradingEngineStates`. Una corsia che perdesse il 19% in tre giorni non verrebbe
né ritirata (servono 20 trade e 21 giorni) né fermata (sotto il 20%). E se superasse il 20%,
l'emergency stop la marca `EmergencyStopped`, che `FleetOrchestrator.cs:197` **esclude** da
`FleetLanes`: non viene ritirata, non viene liberata, non conta come libera — resta congelata fuori
dalla flotta finché non interviene una mano umana.

**E l'isteresi vive nella memoria del guscio.** `_retireStreak` è un campo d'istanza del
`BackgroundService`: ogni riavvio azzera i due tick di conferma. Bastano 15m45s di uptime per
arrivare a `streak = 2` — ma in cinque giorni il ledger ha perso **6,8 ore** in buchi oltre i 45
minuti di `MaxCreditPerGap`, cioè il guscio si è già fermato più volte, e nessuna riga di journal
registra la serie in corso.

### 4.2 Lo schieramento: tre tappi in fila, e il serbatoio è vuoto

Se il 4 settembre si liberassero delle corsie, **non verrebbe schierato nessuno**.

1. **Il tetto grigio è saturo.** `greySlots = MaxGreyLanes(3) − greyRunning(5) = −2`. Le cinque
   gambe di flotta non hanno `sourceVerdict` nel JSON (schierate col click F5 prima dell'etichetta
   T1), e `LaneDirectory` conta l'ignoto come grigio — fail-closed corretto, per la ragione giusta:
   le sei righe `Assign` del journal sono tutte `[F5, click umano]` sulla fascia grigia. Gli slot
   tornano ≥1 **solo dopo il terzo ritiro**, e a quel tick il ritiro consuma già l'unico budget di
   esecuzione.
2. **La coda grigia è vuota per costruzione.** Tutte e 18 le identità della finestra a 30 giorni
   risultano `AlreadyHandled`, perché `FleetStateReader` fa ereditare la marcatura **per identità**:
   basta che una chiave sia stata proposta una volta al click umano perché il braccio automatico la
   consideri gestita. Il miglior candidato disponibile oggi — **MacdTrend AAVE/USDT 4h `#0623ee92`,
   Sharpe holdout 3,66 su 55 trade**, run di stamattina — è soppresso perché la stessa chiave era
   stata *proposta* il 28 agosto. **Proporre a un umano e schierare in automatico sono due azioni
   diverse, e oggi la prima consuma la seconda.**
3. **Il ramo grigio non dichiara mai perché tace.** Il ramo «pass» ha il suo `FleetNoOp`; il grigio
   no. In 115 decisioni ci sono **zero righe `Blocked`**: chi guarda `/admin/autonomy` vede solo
   proposte e non sa se il vincolo sia il tetto, le corsie o la coda.

### 4.3 La campagna: tre tappi in serie, e il primo è l'unico che si vede

Tutti i «NotApplied» di config 19/20 sono la guardia grigia J12, verificato a DB: **18 artifact
`AutoReapplyGreyBlocked` su 18**, e **zero** `AutoReapplyDecision` — il comparatore e il supervisore
non sono **mai** stati esercitati su questa campagna. Testo integrale del motivo, dal run di
stamattina:

> «3 gambe su 3 non sono sopravvissuti pieni (3 di fascia grigia): l'applica AUTOMATICA è riservata
> ai sopravvissuti (F5: il grigio si propone al click, non si schiera da solo;
> AutoReapply:MaxGreyLegs=0). L'applica manuale da /pipeline resta possibile.»

Cosa succede girando la manopola, misurato sui 18 run bloccati:

| `MaxGreyLegs` | effetto |
|---|---|
| 0 (oggi, per assenza della chiave) | blocca tutto |
| 1 | **blocca tutto lo stesso** — nessuno dei 18 run ha meno di 2 gambe grigie |
| 2 | sblocca **3 run su 18** (config 19 del 25, 26 e 27/08) |
| 3 | sblocca tutti e 18 |

E subito dopo ne trova un altro: **il fail-closed RF0**. Le tre gambe attive delle corsie 1 e 2
hanno `expectedSharpe` valorizzato ma `expectedSharpeAtUtc` **assente** dal JSON ⇒
`MetricsConvention.IsRiskFreeZero(null) = false` ⇒ `HasLegacyMetrics = true` ⇒
`EnsembleComparator.cs:188` rifiuta senza guardare i numeri. **Nessun candidato, nemmeno perfetto,
sostituirà mai l'ensemble per via automatica** finché quel timbro manca. E il timbro si scrive
**solo quando una gamba viene aggiunta**: un semplice «Salva» su `/ensemble` non ri-timbra nulla —
vanno rimosse e ri-aggiunte.

Due cose da sapere prima di toccare qualunque manopola qui:

- **L'impronta di scrittura è 0..2 e non guarda `IsRunning`.** Il motore congela symbol e timeframe
  in `StartAsync` e ripristina le gambe dalla fotografia della sessione, quindi il danno non è il
  trade sbagliato subito: è **la corsia che riparte su una configurazione che nessuno ha scelto**.
  L'avvio, a differenza della scrittura, il controllo ce l'ha già (`CampaignPlanner.cs:503-508`).
- **`/pipeline` non è l'unica porta manuale.** `/bot` («Modalità Semplice») chiama
  `ApplyLatestResearchAsync` → `ApplyRunAsync` sull'ultimo run applicabile **trovato da solo**, con
  un bottone e senza alcuna guardia grigia. Il messaggio dell'artifact nomina solo `/pipeline`:
  sottostima le porte aperte.

### 4.4 E il gate del conteggio trade? La manopola ovvia peggiora la scheda

Il pavimento assoluto di 20 trade ferma **94 chiavi distinte in guadagno** — il 69% delle 136 con
Sharpe holdout ≥ 0,5 — fra cui il massimo mai prodotto dalla piattaforma. È vero, ed è la porta più
grossa. Ma aprirla **non schiera nessuno**, e per due ragioni verificate:

1. **Il DSR non ha mai raggiunto 0,95**: 0 righe su 15.149, massimo storico 0,7729, massimo dopo la
   correzione del conteggio tentativi (09/08) **0,6737**. Le bocciature si sposterebbero dal gate del
   conteggio a quello del DSR, che è esattamente ciò che il commento di J6 avverte.
2. **Peggio: si ridurrebbe la fascia grigia.** `GreyZone.IsGrey` guarda il *prefisso* del
   `RejectReason`: chi è bocciato per «Solo N trade» è grigio, chi è bocciato per DSR non lo è.
   Abbassare il pavimento **converte l'unica popolazione oggi schierabile (100 chiavi `IsGrey`) in
   bocciati non schierabili.**

E `minHoldoutTradesFraction` (J6) non è la manopola che apre: per costruzione può solo **alzare** il
pavimento, mai abbassarlo. L'unica che apre è `minHoldoutTrades`.

**Il costo, invece, è sottostimato davvero — ma meno di quanto la ROADMAP dichiari.** L'attrito
modellato non è 0,05%: `PipelineCosts` applica anche `DefaultFeePercent = 0,1` e un funding
costante, quindi il round-trip modellato vale ~0,30%. La sottostima è dell'ordine di **2-14×**, non
5-40×. E il funding *è* costante perché `FundingHistory` non è popolata da nessuno e
`IFundingHistoryProvider` è collegato solo a `/backtest`, **mai alla pipeline**: ogni backtest della
caccia gira su una costante inventata.

---

## 5. Il piano

### Fase 0 — La macchina sa in che stato è, e non muore in silenzio
*Precondizione di tutto: ogni automazione costruita sopra un piano stantio è lavoro sprecato.*

| # | Cosa | Come si misura che è fatto |
|---|---|---|
| K1 | Sonda **«revisione viva vs HEAD»** per i tre piani. Il dato c'è già a costo zero: il SDK .NET 10 timbra `AssemblyInformationalVersion` con lo sha completo (guscio e plancia), e il pod porta `newTag: local-<sha8>`. Riga in `procione stato` + scheda in Home | oggi la scheda deve dire «guscio 0 · plancia 13 · pod 0», e i tre numeri devono muoversi da soli dopo una merge. **Non** riusare `HostHeartbeats.Version`: ospita già una stringa di stato, e il confronto direbbe sempre sì |
| K2 | **La plancia si ricompila da sola.** Oggi nemmeno il suo riavvio la aggiorna: `procione.exe` avviato stanotte da un binario del 25/08 | dopo una merge, entro 30′, la revisione della plancia coincide con `origin/master` |
| K3 | **Il guscio si aggiorna come il motore**, in finestra di quiete. Costo misurato: **3m36s**; non tocca posizioni né lease (stanno nel pod). Quiete = nessun run non terminale, campagna fuori finestra d'innesco, pod `Ready`, fascia oraria dichiarata | una merge alle 15:00 trova il guscio allineato entro la finestra successiva, e `PipelineRuns` non guadagna righe `Paused` |
| K4 | Il lavoro `deploy` **verifica la CI verde** prima di promuovere (`gh run list --commit <sha> --json conclusion`). Oggi il cancello è «master è cambiato», non «master è sano»: un test rosso arriva nel cluster in 30 minuti | un commit con CI rossa non produce rollout, e il rifiuto compare nel log del supervisore |
| K5 | Tetto del lavoro `avvio` da 15′ a 30′. Stanotte: 10m29s, col passo dichiarato «fino a 5 minuti» che ne ha presi **7m15s** (+45%). Altri 4-5 minuti di lentezza e il bring-up viene ucciso **prima** di avviare il guscio, e nulla lo rilancia | un bring-up da 20 minuti completa invece di essere ucciso |
| **K5b** | **Trigger di risveglio periodico** sull'attività pianificata. *Aggiunto il 2026-08-31 da un incidente misurato*: alle 14:38 un installer Microsoft ha usato il **Restart Manager** e ha terminato la plancia a metà bring-up (esito 1, nessun log, nessuna eccezione). `-RestartCount 3` era già configurato e **non è intervenuto**: il Task Scheduler non conta quella morte come un fallimento. La piattaforma è rimasta giù venti minuti e sarebbe rimasta giù fino al logon successivo | il supervisore ucciso da fuori torna su entro 10′ da solo. Idempotente per costruzione: con `IgnoreNew` il caso normale è un no-op deciso da Windows |
| **K5c** | **Il bring-up sprecava 6m25s a ogni giro per due difetti sovrapposti, entrambi nascosti da uno `2>$null`.** (a) La jsonpath del nodo perde le virgolette interne passando da PowerShell 5.1 a kubectl: arrivava `@.type==Ready` e kubectl rispondeva `unrecognized identifier Ready`, exit 1, per **tutti e trenta i giri** — il ciclo non poteva riuscire, e lo script concludeva «nodo NON Ready» accusando il cluster di un difetto di quoting. (b) La sonda `/livez` con `Invoke-WebRequest` falliva **sempre** sullo stack TLS di PS 5.1 mentre kubectl passava dallo stesso proxy: il socat veniva distrutto e ricreato a ogni bring-up, anche quando funzionava. *Corollario*: l'attesa del pod del motore era codice morto, perché stava dentro il ramo che non veniva mai preso | **fatto (2026-08-31)** — misurato: da **6m25s a 5 secondi** |
| K6 | **Corsia preferenziale per i `Critical`** + deduplica centrale per chiave nel dispatcher. Oggi 20 Info nell'ora scorrevole zittiscono l'allarme di invariante di corsia o di master key, e il messaggio è **perso**, non accodato | un test che satura il budget con Info e verifica che un `Critical` passi comunque |
| K7 | Accendere l'**heartbeat incrociato** (`Heartbeat:Enabled=true` in guscio **e** pod) | `HostHeartbeats` mostra le righe `shell` e `engine` accanto a `ingestion-sync` |
| K8 | Il **carry scrive un battito persistito** e il guardiano legge quello invece di `GetService` | `Fleet:CarrySilenceAlertHours` diventa una manopola che può scattare: spegnere il carry nel pod produce l'allarme |
| K9 | La **sonda della ricerca**, due correzioni: `StallAlertHours = 12` contro `RearmHours = 13` produce ~1h di falso allarme a **ogni** giro sano; e il verdetto è cieco ai candidati. Separare le due domande — «la ricerca gira» e «l'archivio cresce» sono guasti diversi | soglia > riarmo; e la card distingue «gira ma non deposita» da «ferma» |
| K10 | `IResearchCandidateIndexer` come **hosted worker a fine run** | l'archivio contiene i run delle ultime 24h senza che nessuno abbia aperto `/research` |

### Fase 1 — Decidere prima del 4 settembre
*Quattro corsie verranno fermate da sole. Questa fase esiste perché la decisione sia presa, non subita.*

| # | Cosa |
|---|---|
| K11 | **La decisione del proprietario**: lasciar fare, alzare `StarvationMinDays`, o togliere corsie da `ExecutionLanes`. Il ritiro **non è irreversibile** (solo `StopAsync`; la corsia resta configurata) |
| K12 | Il ramo grigio **dichiara perché non schiera**: un `FleetNoOp` per «nessuna corsia libera» e uno per «tetto grigio saturo», coi numeri dentro. Stessa forma di quello già presente per la banda «pass» |
| K13 | **Etichettare `Grey` le cinque gambe di flotta** (lo sono: cinque click F5 documentati a journal). Il risultato non cambia, cambia che una superficie lo spiega |
| K14 | **«Già proposto al click umano» smette di consumare il braccio automatico.** È una riga di codice e una decisione di prodotto: oggi il candidato migliore dell'archivio è invisibile all'automatismo perché un umano l'ha visto due giorni fa |
| K15 | **Budget di esecuzione separato** fra ritiro e assegnazione: oggi `MaxExecutionsPerTick=1` fa sì che il tick che libera la terza corsia non possa anche assegnarla |

### Fase 2 — Il ritiro come giudizio, non come conteggio

| # | Cosa |
|---|---|
| K16 | **L'inedia con potenza statistica.** O `StarvationMinDays` sale finché l'atteso nel periodo supera ~3 trade (≥42 giorni a f≈2,2), o il criterio passa a un quantile di Poisson. Con il suo nullo, come pretende il livello 2 |
| K17 | **Criterio di danno conclamato**, senza vincolo di settimane: drawdown e PnL cumulato sono già in `TradingEngineStates`, `FleetLaneState` non li porta. E una corsia in emergency dev'essere **dichiarata**, non sparire da `FleetLanes` |
| K18 | Lo **Sharpe del ritiro ancorato** alla finestra dei trade: o `from` filtra anche l'equity, o si calcola dai `TradeRecords` della finestra |
| K19 | `RetireMinTrades = 20` **tarato sull'orizzonte reale**: a 2,17-3,70 trade/mese un forward test da tre settimane non può produrne venti. In alternativa, PnL cumulato con banda di confidenza |
| K20 | **Persistere `_retireStreak`** (colonna su `FleetLaneObservations` o riga di journal `RetirePending`) ed esporlo. Oggi ogni riavvio azzera la conferma, e non si vede |

### Fase 3 — Riempire il serbatoio senza abbassare la barra

| # | Cosa |
|---|---|
| K21 | **`AutoReapply:MaxGreyLegs`, scritto esplicitamente.** Sapendo che 1 non fa nulla, 2 sblocca 3 run su 18, 3 li sblocca tutti — e che subito dopo c'è K22 |
| K22 | **Sbloccare il fail-closed RF0**: rimuovere e ri-aggiungere le tre gambe delle corsie 1 e 2 da `/ensemble` (il timbro `ExpectedSharpeAtUtc` si scrive solo in aggiunta). Finché non si fa, il percorso campagna è chiuso a chiave a valle di qualunque manopola |
| K23 | **Controllo `IsRunning` sulla scrittura** dell'impronta 0..2, come già ce l'ha l'avvio. Il rischio non è il trade sbagliato subito: è la corsia che riparte su una configurazione mai scelta |
| K24 | **Dichiarare la seconda porta**: `/bot` applica l'ultimo run trovato da solo, senza guardia grigia. O la si guarda, o il messaggio dell'artifact smette di dire che la porta è una sola |
| K25 | **Il funding reale nei backtest della pipeline**: collegare `IFundingHistoryProvider` allo stage, e popolare `FundingHistory`. Oggi la caccia gira su una costante inventata mentre il carry vivo usa il dato vero |
| K26 | **Il DSR anche per i bocciati «Solo N trade»**, con provenienza dichiarata e **senza cambiare il verdetto**: è l'unico modo di sapere se sotto il pavimento c'è qualcosa, senza ridurre la fascia grigia. Strumento di misura, non leva |
| K27 | **Potare l'universo con `CoversHoldout`** e farlo derivare dalle serie abilitate: `MKR/USDT` è delistato e produce 11 chiavi a zero trade più il 3% di tentativi contati nel DSR |

### Fase 4 — Qualcuno che sceglie (è qui che nasce la Regina)

| # | Cosa |
|---|---|
| K28 | **Pianificatore adattivo**: resa per configurazione (chiavi distinte in fascia utile per ora di CPU), budget spostato verso i terreni che rendono, config sterili in sonno dichiarata. Oggi 42,4 ore/mese, e 17/18 hanno prodotto zero gambe su 119 run |
| K29 | **Tuner dei parametri di caccia** — non delle strategie: universo, `topN`, ampiezza finestre, `confirmTopN`, timeframe. Una proposta per giro, A/B dichiarato, gate a valle **invariato**, registro di cosa ha cambiato e perché |
| K30 | **L'AI dal veto alla proposta motivata.** Spostare il comitato su una domanda che esiste davvero (la scelta fra grigi quando una corsia si libera) invece di un pareggio che questa pipeline non produce. E spostare il supervisore **dopo** il comparatore: oggi paga una chiamata LLM per un verdetto già scritto |
| K31 | **Il post-mortem rientra nella caccia**: una gamba che decade genera un'ipotesi (stesso simbolo altro timeframe, stessa strategia altri parametri, esclusione del simbolo) che entra in rotazione |
| K32 | **Generatore di candidati** (l'ex G3): **solo dopo** che K26 dice se esiste un terreno dove il gate respira. Poche ipotesi vincolate, non molte: più tentativi alzano SR\* |

---

## 6. Non-obiettivi dichiarati

- **Non si automatizza nulla verso Live.** `SafetyChecker` resta statico e puro; `PromotionEvaluator`
  resta com'è (Paper→Testnet automatico, Testnet→Live solo umano). Il confine è stato ri-verificato
  su quattro livelli indipendenti in questa ondata, e regge: `GreyDeployer` passa `TradingMode.Paper`
  come **letterale**, e il journal conferma 330 Paper + 1 Testnet + **0 Live** in tutta la storia.
- **Non si abbassano DSR, PBO o il gemello nullo.** Il problema è aritmetico: SR\* vale 2,65-2,86 e
  servirebbe uno Sharpe ≈ 5,2-5,5.
- **Non si abbassa `minHoldoutTrades` per far passare qualcuno.** È misurato che ridurrebbe la fascia
  grigia invece di allargarla (§4.4). Si può usare per **misurare**, mai per schierare.
- **Non si accende `MarketData:Realtime:DriveProtectiveExits`.** Il file vivo lo porta a `true`
  contro la regola 7; oggi non morde perché il feed realtime è spento, ma va rimesso a `false` prima
  che qualcuno accenda il realtime: uscire al tocco è peggio a barra chiusa in **24 configurazioni
  su 24**.
- **Niente raccolta permanente di microstruttura** (informa a p 0,005, edge 6-34× sotto il costo del
  giro) e **niente RL** (respinto due volte, sim-to-real gap).
- **Il comitato propone, le metriche decidono.** Nessun servizio di esecuzione entra in
  `Services/Llm/`.

---

## 7. Come si verifica

I quattro livelli di `docs/STANDARD-VERIFICA.md`, con l'accento su due che questa ondata pretende in
modo particolare:

- **Livello 2 (il rumore non deve accendere niente)** vale ora anche per le decisioni **operative**,
  non solo per i verdetti statistici. Il criterio di inedia è il caso di scuola: oggi condanna con
  probabilità 32-49% una corsia che opera esattamente al ritmo atteso. Ogni criterio di ritiro nuovo
  nasce col suo nullo.
- **Livello 4 (dal vivo, nel browser)**: le sonde di Fase 0 vanno lette sulla 5199 con i dati veri,
  perché è il livello che ha trovato tutti i difetti di questa classe finora. In questa ondata,
  quattro dei rilievi più importanti sono stati corretti proprio perché qualcuno ha guardato lo
  stato vivo invece del codice a riposo.

Un gate esplicito per la Fase 1: **entro sette giorni dal deploy deve esistere almeno una riga
`Retire` e almeno una riga `Assign` con `Source != 'human'` in `OrchestratorDecisions`.** Se non
esiste, la fase non è finita — non si passa oltre.

---

## 8. Le decisioni che restano al proprietario

1. **I quattro ritiri del 4-5 settembre**: voluti o no? Se sì, sapendo che nessuno verrà schierato al
   loro posto finché K12-K14 non sono fatti; se no, la manopola è `Fleet:StarvationMinDays` o
   `ExecutionLanes`, e va girata prima.
2. **`AutoReapply:MaxGreyLegs`**: 0, 2 o 3 — sapendo che il valore intermedio sblocca un sesto dei
   casi e che a valle c'è comunque K22.
3. **Le tre gambe delle corsie 1 e 2**: ri-aggiungerle da `/ensemble` per ri-timbrarle, oppure
   accettare che il percorso campagna resti chiuso.
4. **`/bot`**: guardato dalla stessa guardia di `/pipeline`, o dichiarato come porta aperta.

---

## Appendice — i numeri misurati il 2026-08-30/31

| | |
|---|---|
| Serie tracciate / abilitate | 234 / 222 — **0 stantie fra le abilitate** (max 2,32 barre di ritardo) |
| Candele | ≈ 12,18 M |
| Costo della ricerca | **42,4 ore di pipeline al mese**, 152 run/mese |
| Archivio ricerca | 15.149 righe · **887 chiavi distinte** · 177 run · **fermo al 25/08**, 34 run non indicizzati |
| Sopravvissuti (30 giorni) | **0** su 469 chiavi |
| Chiavi in guadagno fermate dal conteggio trade | **94** su 136 (69%) |
| DSR massimo mai prodotto | **0,7729** (post-correzione 09/08: 0,6737) — soglia 0,95 |
| Fascia grigia | 100 chiavi `IsGrey`; **18 identità nella finestra a 30 giorni, 18 già gestite** |
| Decisioni della flotta | 115 — 109 `ProposeGrey`, 6 `Assign` tutte `Source='human'`, **0 `Retire`** |
| Trade chiusi dalla flotta dal 25/08 | **1** (corsia 5, −51,89) |
| Ultimo ordine di qualunque corsia | **2026-08-24** |
| PnL realizzato aggregato, 7 corsie | ≈ **−601** su 70.000 di capitale nominale |
| Osservazione cumulata (ledger J8) | 5,12 giorni · 6,8 ore perse in buchi > 45′ |
| Revisione viva vs `HEAD` | guscio **0** (allineato stanotte, per incidente) · plancia **13** · pod **0** |
