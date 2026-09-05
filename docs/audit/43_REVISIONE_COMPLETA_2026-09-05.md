# 43 — Revisione completa della piattaforma e presa in carico operativa (2026-09-05)

*Richiesta del proprietario: «una revisione completa della mia piattaforma, passo per passo, sezione
per sezione, codice per codice … dopo di che utilizza tu al meglio la piattaforma e regola tu corsie,
coppie, strategie, variabili, tutto tranne il live trade … massimizzandone i profitti».*

Metodo: quattro lenti indipendenti sul codice (motore e barriere; flotta e comitato; macchina di
ricerca e ri-applica; ingestion, carry, WebSocket, infrastruttura), ognuna con domande chiuse e
obbligo di citare `file:riga`; ogni rilievo che toccava lo stato è stato **ri-verificato sul database
vivo** prima di essere accettato; giro completo delle 40 pagine dell'interfaccia dal browser; sonde
sul cluster kind e sul nodo. Un rilievo su cinque non è sopravvissuto alla verifica (§6).

---

## 0. Il risultato in dieci righe

1. **Le barriere reggono.** `SafetyChecker` statico e puro su ogni apertura; nessun percorso
   automatico verso Live; stop e take profit a barra chiusa; contabilità Spot/Futures senza doppia
   fee. Verificato riga per riga (§1).
2. **La piattaforma era in ordine ma ferma dove conta.** 26 controlli verdi della plancia, ricerca
   viva (169 run su 170 in trenta giorni), e intanto: corsia 0 vuota e in quarantena da un mese,
   corsia 1 su una strategia scelta a mano nel luglio che perde su 42 operazioni, flotta in stallo
   con 20 candidati grigi schierabili e nessuna corsia che si libera, e una ri-applica automatica
   che in trenta giorni non ha mai applicato nulla e nel frattempo **nascondeva alla Regina i grigi
   migliori**.
3. **Tre difetti del governo della flotta sarebbero scattati nei prossimi giorni**: il ritiro per
   inedia condannava a zero trade dopo dieci giorni qualunque fosse il ritmo dichiarato (corsia 4,
   l'11/09, 58 % di probabilità di essere nella norma); il rimpiazzo K61 avrebbe scelto per sempre
   un candidato che collide con la corsia 3; un ritiro su una posizione viva l'avrebbe cancellata
   senza TradeRecord (corsia 7, short dal 31/08). Corretti in PR #139 con le prove.
4. **Accendere la promozione automatica a Testnet oggi avrebbe fatto danni**: una sola credenziale
   testnet (Bitget) contro sei corsie Binance, e la sequenza fermava la corsia, svuotava la sessione
   Paper e poi falliva. Ora il controllo precede il primo tocco (PR #139). Resta spenta.
5. **Il forward test del carry — l'unica classe di edge misurata positiva — non produce misure.**
   Stato in memoria, funding incassato mai accumulato, nessun episodio persistito; ogni rischieramento
   del pod (uno per merge) riparte da zero. È il buco più costoso e resta aperto (§4).
6. **La macchina è instabile**: 18 spegnimenti brutali di Windows in tre settimane, 0,6 GB di RAM
   libera, probe del cluster in timeout, veglia uccisa dal timeout. I 55-56 riavvii dei pod ingestion
   e ml sono quello, non un difetto del loro codice (§4).
7. **Operatività eseguita**: corsia 0 riaperta su Composite ADA/USDT 5m (mediana K57 3,48 su 8
   rimisurazioni), corsia 1 chiusa in utile e rischierata su Composite XLM/USDT 1h (mediana 2,43 su
   10, ventaglio 0,44). Entrambe in Paper, con bracket dalle escursioni (§5).

---

## 1. Motore di trading e barriere (verificato, rispettato)

| Invariante | Prova |
|---|---|
| `SafetyChecker` statico e puro, su ogni apertura | `SafetyChecker.cs:18-33`; unico punto di apertura `TradingEngine.ExecuteOpenAsync` (:1158); pre-check aggregato `ExecutionSlicePlanner.cs:115`. Le sole `PlaceOrder*` fuori dall'apertura sono chiusure (`PositionCloser.cs:136/292`, reduce-only). Il carry non ordina (nessun `IExchangeClientFactory` in `Services/Carry`) |
| Nessun automatismo verso Live | `LanePromoter.cs:33` throw; `PromotionEvaluator.cs:243/251` `SuggestedMode` mai Live; `PromotionWorker.cs:58-71` whitelist; ogni `StartAsync` automatico passa `Paper` a codice fisso (`GreyDeployer.cs:313`, `CampaignPlanner.cs:233/563`, `BotPageService.cs:216`); l'unico `Live` nasce da `Trading.razor:218-262` (radio + checkbox + click) |
| Bracket a barra chiusa | `ProcessCandleAsync:650` → `ApplyProtectiveExitsAsync` su OHLC della barra chiusa; `ProtectiveExitEvaluator.cs:98-115` stop prima del target, fill al livello o all'apertura se gap; tick solo osservativo (`:797-801`, `DriveProtectiveExits=false` nel pod, verificato nel file `/app/appsettings.json`) |
| Re-idratazione al riavvio | `EnsureLoadedAsync:138-185`; frontiera anti-replay `LastCandleUtc` (`:610-614`) |
| Fee e segno del PnL | Spot long/short e Futures chiudono la cassa esattamente a `pnl` (`PositionOpener.cs:156-157/340`, `PositionCloser.cs:193-199/364`) |
| Quarantena | `LaneInvariantChecker.cs:16-78` solo su corsie in corsa: una corsia ferma e vuota non finisce mai in quarantena |

### Difetti trovati nel motore

| # | Gravità | Cosa | Dove | Esito |
|---|---|---|---|---|
| M1 | alta (solo Testnet/Live) | `StartAsync` in Testnet/Live azzera la cache delle posizioni (`_positions.Clear()`, `_loaded=true`) senza ricaricare da DB: una posizione reale aperta prima di uno stop resta orfana in memoria (nessun mark, nessuno stop) e ricompare al riavvio del processo in una sessione che non l'ha contabilizzata | `TradingEngine.cs:387-390`, commento a `:441-444` falso | **aperto** — oggi nessuna corsia Testnet/Live |
| M2 | alta (solo Testnet/Live) | `LanePromoter` non verificava il flatten: `CloseAllPositionsAsync` è best-effort e una chiusura rifiutata dall'exchange non lancia; il `StartAsync(Paper)` che seguiva cancellava la riga della posizione reale senza ordine | `LanePromoter.cs:47-52` | **corretto** (PR #139): flatten verificato, la corsia resta com'era |
| M3 | alta (operativo) | Promozione a Testnet su corsia Binance senza credenziali testnet: ferma, sovrascrive la sessione Paper (PnL, picco, `StartedAtUtc`), poi fallisce; la corsia resta ferma in Testnet e il worker non ritenta | `LanePromoter.cs:47-52`, `TradingEngine.cs:365-415` | **corretto** (PR #139): pre-flight sulle credenziali PRIMA di toccare la corsia; test `PromozionePreFlightTests` |
| M4 | media | `DailyPnl` si azzerava solo alla chiusura successiva: una corsia a −5,1 % il giorno 1 e senza segnali per tre giorni chiedeva l'EMERGENCY STOP al primo segnale del giorno 4 | `PositionCloser.cs:203/367`, `SafetyChecker.cs:66-71` | **corretto** (PR #139): la finestra scorre con le candele (`ProcessCandleAsync`) |
| M5 | media (Live) | `ConfirmOrderAsync` ignora `IsRunning` e quarantena; dopo un riavvio la gamba non si trova e la posizione nasce senza stop né TP | `TradingEngine.cs:1406-1442`, `AutoStopApplier.cs:17-18` | aperto |
| M6 | media (Live) | La guardia J21 salta la prima barra delle posizioni aperte da conferma o da fetta (`OpenedAtUtc` a orologio di parete contro `ts` = apertura della barra) | `TradingEngine.cs:886` | aperto |
| M7 | media | Corsia ferma = posizioni senza alcuna protezione (le uscite girano solo a corsia in corsa); la quarantena le lascia aperte per scelta | `TradingEngine.cs:597/783`, `LaneInvariantWatchdog.cs:357-359` | mitigato: il ritiro di flotta e lo schieramento grigio ora non toccano corsie con posizioni (PR #139) |
| M8 | media | Fill parziale di chiusura adottato come chiusura totale: il resto resta scoperto e non tracciato | `PositionCloser.cs:89-106, 249/420` | aperto |
| M9 | bassa | Quarantena J19 senza via d'uscita da `/trading`: `ClearAsync` toglie la riga, `StartAsync` copia `cfg.Symbol` senza validarlo, nuova quarantena in ~90 s | `LaneInvariantChecker.cs:31-37`, `TradingEngine.cs:370-372` | aperto; oggi risolto configurando la corsia da `/ensemble` |
| M10 | bassa | Gap di candele non rilevati (`ts ≤ frontiera` è l'unico filtro) | `ProcessCandleAsync:610-614` | aperto |

---

## 2. Flotta, ritiro, sostituzione, comitato

### Ciò che era vero il 2026-09-05 alle 08:20 UTC

- 5 corsie di flotta (3-7) tutte in corsa e occupate; **43 righe `Blocked/Noted` in 14 giorni**
  «20 candidati grigi schierabili ma NESSUNA corsia di flotta libera»; a intermittenza «N corsie NON
  SONO LEGGIBILI (il motore non ha risposto)» con N = 2, 3, 5.
- Le «corsie non leggibili» sono **timeout gRPC di 10 s** (`DeadlineClientInterceptor.cs:45-58`)
  durante le tempeste del nodo: coincidono con i rischieramenti del pod (08:12, 07:25) e con le probe
  in timeout di etcd/apiserver/calico registrate negli eventi del cluster. Non sono impegni reali.
- Il catch è generico (`FleetStateReader.cs:154-161`): anche un timeout Npgsql nel ledger produce la
  frase «il motore non ha risposto» — una diagnosi congetturale scritta come fatto.

### Difetti, con l'esito

| # | Gravità | Fatto misurato | Correzione |
|---|---|---|---|
| F1 | **alta** | `IsStarving`: con zero trade «0 < attesi × 0,2» è vero per qualunque ritmo > 0. Corsia 4 (Composite XLM/USDT 4h, 1,65 trade/mese, dal 01/09) sarebbe stata ritirata **≈ l'11/09** con P(0 trade in 10 g) = e^−0,54 = **58 %**. Il test esistente provava 1 trade, non 0 | **PR #139 (K16)**: si condanna solo se la coda di Poisson P(X ≤ osservati \| attesi) < 5 %; per zero trade equivale ad attesi ≥ 3,0 (la corsia 4 matura verso i 55 giorni). `TradeFrequency.PoissonLowerTail` provata contro valori a mano |
| F2 | **alta** | Ritiro e schieramento grigio non guardavano le posizioni aperte: stop → corsia libera → grigio → `StartAsync` in Paper `ExecuteDelete` delle posizioni senza TradeRecord (K36). Caso vivo: corsia 7, short TRX/USDT dal 31/08, 0 chiusi, inedia a 10 giorni | **PR #139 (K36-bis)**: il decisore emette un `FleetNoOp` «aspetta che chiudano» invece dello stop; cintura nel worker (`ExecuteRetireAsync`) e nel `GreyDeployer` (rifiuta di avviare sopra posizioni) |
| F3 | **alta** | Il rifiuto di sostituzione (guardia dei duplicati dentro `ExecuteReplaceAsync`) scriveva `Retire/Refused` e non bruciava il candidato; la lista dei rimpiazzi è ordinata per mediana, quindi stabile → **stesso candidato, stessa corsia, stesso rifiuto a ogni tick, per sempre**. Il candidato in testa (MacdTrend AAVE/USDT 4h, mediana 4,01) collide per terna con la corsia 3 | **PR #139 (K33-bis + K61-bis)**: `FleetLaneState.ActiveCandidateKeys` dalla directory; `CollideConCorsiaInCorsa` salta nel decisore ciò che la guardia rifiuterebbe (rimpiazzi e coda grigia); i `Retire/Refused` con RunId bruciano 24 h come i rifiuti di assegnazione |
| F4 | **alta** | `handledByReapply` contava **qualunque** artifact `AutoReapplyDecision`, anche gli scarti. Sul DB: 14 «Run senza ensemble applicabile» e 2 «Candidato scartato: solo 1 simboli distinti» (02/09 e 05/09, caccia 5m) — e 0 `Applied=true` in tutta la storia. Per ereditarietà d'identità **Composite ADA/USDT 5m (mediana K57 3,48)** era invisibile al braccio automatico dalle 05:15 di oggi | **PR #139 (K14-bis)**: un artifact gestisce il run solo se `Applied=true`; payload illeggibile = gestito (fail-closed) |
| F5 | media | `PreferStableGrey` ordinava per sola mediana: sulla corsia libera la Regina poteva scegliere un'ipotesi che `/admin/autonomy` marca «⚠ INSTABILE» (ventaglio > mediana) | **PR #139 (K57-bis)**: stabili prima delle instabili, prima delle non giudicabili |
| F6 | media | La sostituzione non si astiene con corsie illeggibili (solo il ramo grigio lo fa): può fermare una corsia leggibile mentre non sa se una muta è libera | aperto |
| F7 | media | Illeggibilità intermittente = `ApplyRetireHysteresis` assolve («il verdetto NON si è ripetuto») una corsia che semplicemente non è stata letta; con giri alterni la conferma a 2 tick non arriva mai | aperto |
| F8 | media | Nel rimpiazzo lo stop precede i controlli del deployer (serie stantia, bracket, `ResolveGrey`): una corsia può essere fermata per niente; e a `StartAsync` fallito resta ferma **sulla nuova ipotesi** (config già scritta), contrariamente a quanto dice la notifica | aperto |
| F9 | media | Identità del ledger fragile: `IsActive` toggle, `JsonException` → identità `"\|\|"`, prima riga `EnsembleStates` per corsia: ogni reset di `FirstSeenUtc` regala ~10 g di immunità da inedia | aperto |
| F10 | media | Journal muto su piano vuoto e su tick fallito: silenzio e guasto indistinguibili a posteriori | aperto |
| F11 | bassa | Comitato: un votante morto è «confermato» solo dopo 3 consultazioni distinte (rare); il menù non porta la stabilità ai votanti | aperto |

**Quando scatta davvero il ritiro, col codice corretto** (date «non prima di», ledger cumulato):
corsia 3 (MacdTrend AAVE 4h, atteso 11,11/mese, 1 trade) inedia verso il **ventesimo giorno** se
resta a 1 trade (λ = 7,3, P(X ≤ 1) = 0,56 %); corsia 4 non prima di ~55 giorni; corsia 5 mai per
inedia (atteso non dichiarato), sostituibile dal ~7/09; corsia 6 Sharpe ≈ 25/09; corsia 7 come la 4,
e con la posizione aperta il ritiro aspetta.

---

## 3. Macchina di ricerca e ri-applica

- **Corsia 0 vuota dal 2026-08-05**: l'unico scrittore di `[]` con `Symbol=""` è il pulsante «Svuota
  corsia» di `/trading` (`TradingPageService.cs:335-341`) — un click umano. Nessun automatismo
  scrive `[]`. La corsia 0 **non è territorio di flotta** (`FleetStateReader.cs:84`,
  `Id ≥ LaneCount`), quindi nessuno poteva riempirla.
- **La ri-applica automatica è morta dal 2026-08-22**, per due tappi in serie: il comparatore
  rifiuta i candidati con un solo simbolo distinto (`EnsembleComparator.cs:156-163`; tutte le cacce
  per timeframe producono 1-2 gambe grigie sullo stesso simbolo) e, prima ancora, la gamba della
  corsia 1 (RsiOversold DOT 15m, `ExpectedSharpe` 4,05 dell'11/08 senza timbro) alzava
  `HasLegacyMetrics` (`PipelineApplier.cs:225-228`) che chiude il confronto a prescindere
  (`EnsembleComparator.cs:188-198`). Con la corsia 1 rischierata oggi il secondo tappo è rimosso; il
  primo resta ed è una scelta: `MinDistinctSymbols=2` protegge dalla concentrazione, ma con cacce
  mono-simbolo non lascerà mai passare nulla. Decisione rimandata al proprietario (§7).
- **Allocazione 0 % non è «non allocata»**: la size dell'ordine dipende da `TotalCapital ×
  PositionSizePercent` (`SignalOrderBuilder.cs:54-64`, decisione A3), non da `CurrentAllocation`. Le
  gambe aggiunte da `/ensemble` (grigie e salvate) restano a 0 (`EnsemblePageService.cs:420-435`) e
  la colonna «Alloc %» rassicura al contrario: due gambe sulla stessa corsia aprono **ciascuna** a
  taglia piena (corsia 2: fino a 2 × 8 %). Aperto: la UI dovrebbe dirlo.
- **Gate anti-overfitting**: DSR, permutation, PBO di pannello e gemello nullo girano **solo sui
  sopravvissuti** (`ModelStages.cs:1082`); i grigi «Solo N trade» non ne attraversano nessuno, e un
  batch con PBO ≥ soglia azzera i sopravvissuti ma lascia i grigi schierabili. È il prezzo dichiarato
  della fascia grigia: il forward test Paper è l'unico giudice, e per questo va tenuto pulito.
- `CoversHoldout=false` non è letto a valle (`DataStages.cs:73-76`): holdout più corto del dichiarato
  con `HoldoutMonths` nominale ⇒ z gonfiato. Aperto.
- Il trigger di regime confronta etichette di modelli diversi dopo un retrain
  (`AnalysisStages.cs:196-217` vs `RegimeChangeDetector.cs:216-226`). Aperto.
- L'indice `ResearchCandidates` è alimentato ogni 30′ da `ResearchIndexSyncWorker`, non a fine run;
  lo leggono la gamba grigia di `/ensemble` e K54, non la flotta. Stantio al più di 30′.

---

## 4. Ingestion, carry, infrastruttura

| # | Gravità | Fatto | Esito |
|---|---|---|---|
| I1 | **alta** | **Il forward test del carry non produce misure**: `CarryEngine` tiene lo stato in memoria (`:64`), `FundingCollectedPercent` viene solo azzerato all'apertura (`:94`) e nessun altro codice lo tocca; `PaperCarryExecutor` fa solo log; l'unica traccia persistita è `HostHeartbeats[carry]` con «Paper · 6/6 simboli». A ogni rischieramento (uno per merge, cinque nelle ultime 48 h) i sei simboli «riaprono». Dal guscio il pannello ottiene `null` e lo dichiara | **aperto**: è il buco più costoso — la sola classe di edge misurata positiva (5,5-11,9 %/anno, doc 30) non ha un forward test leggibile |
| I2 | alta | Sizing del carry senza tetto aggregato: `InitialCapital` fisso 10 000 (`CarryModels.cs:13`), 50 % per gamba × 6 simboli = **300 % per lato** | aperto |
| I3 | alta | `deploy-trading.ps1` scrive il pin nel kustomization **prima** di `kubectl apply` e del rollout (`:139-145`); se uno dei due fallisce, il giro dopo committa il pin orfano e dichiara «già allineato» (`:60-83`) mentre il cluster resta sull'immagine vecchia | aperto |
| I4 | alta | **La macchina**: 18 eventi Kernel-Power 41 (spegnimento brutale) dal 13/08; ultimo riavvio 05/09 05:07; RAM 7,7 GB con 0,6 liberi; calico-kube-controllers 240 riavvii, kube-controller-manager 166; veglia uccisa dal timeout due volte nella notte. I 55/56 riavvii di ingestion e ml sono **reboot del nodo + liveness su nodo saturo** (`Last State: Terminated, Reason: Unknown, Exit 255`, log precedenti puliti), non crash applicativi. Le probe di ingestion/ml (`timeoutSeconds: 5`, soglia 3) sono quattro volte più strette di quelle del trading (20 s × 10) | aperto: decisione del proprietario (hardware / probe) |
| I5 | media | Il motore batte «senza timbro (1.0.0.0)»: il `Dockerfile` (`:48-53`) pubblica senza `SourceRevisionId` e `.dockerignore` esclude `.git/`; `build-images-local.ps1:74` calcola lo sha e non lo passa | aperto (piccolo) |
| I6 | media | `/admin/backup` interroga il Task Scheduler («NON REGISTRATA», consiglia `-Register`) mentre dal 23/08 il backup gira nel supervisore della plancia (14 dump sani, ultimo 03:31). Dovrebbe leggere `%TEMP%\procionemgr-supervisore.json` e `~/.procione/lavori.json` | aperto — un controllo che allarma a prescindere dalla realtà, e il rimedio suggerito creerebbe un **secondo** backup notturno |
| I7 | media | `sync-piani.ps1` con `$ErrorActionPreference='Stop'` in PowerShell 5.1: una riga su stderr del build delle migrazioni (`:178`) abortisce a guscio già fermo, e il bring-up successivo non copia la DLL | aperto |
| I8 | media | Le immagini di ingestion e ml sono del 16/08 e del 10/08; nessun lavoro le ricostruisce | aperto |
| I9 | bassa | WebSocket senza `KeepAliveTimeout`; la watchdog per-serie notifica e non ricicla | aperto (feed in sola osservazione) |
| I10 | bassa | Veglia cieca su ingestion/ml/freschezza; dump sullo stesso disco del DB; drill di restore fermo al 26/07 | aperto |

Verificato e sano: sync delle candele vivo (241 serie, ultimo giro ogni 5′); guardiano
serie-patrimonio OK su funding e Fear & Greed; liquidazioni Binance mute per il blocco EEA (noto,
non risolvibile da qui); comitato AI con quorum (voti il 02 e 03/09); backup notturno sano; CI verde;
revisioni allineate sui tre piani.

---

## 5. Operatività eseguita (Paper, mai Live)

| Quando (UTC) | Cosa | Perché |
|---|---|---|
| 11:4x | **Corsia 0**: quarantena rimossa (era «IN CORSA ma non alimentabile» dal 25/08 su una corsia svuotata a mano) | uno slot su otto fermo da un mese |
| 11:50 | **Corsia 0** configurata da `/ensemble` su **Composite ADA/USDT 5m** (run 4769ff9b, holdout Sharpe 2,46 su 17 trade, ~4,6 trade/mese, dominante short, tempo a mercato 53 %), bracket dalle escursioni SL 0,59 % / TP 1,57 %, avviata in Paper | è il miglior rimpiazzo **libero** per la regola della piattaforma (mediana K57 3,48 su 8 rimisurazioni, ventaglio 1,28 ≤ mediana): MacdTrend AAVE 4h, più alto, collide con la corsia 3 |
| 11:52 | **Corsia 1**: chiuso a mercato lo short DOT/USDT (+4,55, TradeRecord `Manual`), fermata | RsiOversold DOT 15m era una scelta a mano di luglio con «Sharpe atteso 4,05» di selezione: **42 operazioni e −65,87 in totale**, −46 negli ultimi 30 giorni. La regola di ritiro della flotta (Sharpe per trade < 0 con ≥ 20 trade) l'avrebbe ritirata se fosse stata una corsia di flotta |
| 11:58 | **Corsia 1** rischierata su **Composite XLM/USDT 1h** (run 14a385fa, holdout 2,45 su 16 trade, ~3,3 trade/mese, dominante long), SL 1,62 % / TP 4,88 %, avviata in Paper | mediana K57 2,43 su 10 rimisurazioni, ventaglio 0,44: la più stabile fra le non collidenti sotto le 4 ore |
| — | **Promozione automatica a Testnet: lasciata spenta** | vedi M3: prima della PR avrebbe fermato e svuotato le corsie Binance; dopo la PR rifiuta senza toccare, ma senza credenziali testnet Binance non promuoverebbe comunque |
| — | Flotta: `GreyAutoDeploy`, `ReplaceIdleLanes`, `PreferStableGrey` lasciati accesi | con le correzioni di PR #139 il rimpiazzo della corsia 5 (muta dal 27/08) sceglierà Composite ADA/USDT 5m… che ora gira sulla corsia 0: la guardia dei duplicati lo salterà e prenderà il successivo non collidente (DOT/USDT 4h Composite, mediana 2,73 su 18) |

Orizzonte dichiarato, come vuole lo standard: corsia 0 ~4,6 trade/mese, corsia 1 ~3,3; a questi
ritmi i 20 trade del giudizio per Sharpe arrivano fra 4 e 6 mesi. La durata mediana delle posizioni
la misurerà il forward test (la trade list dei candidati non è persistita).

Le due corsie hanno rigiocato le candele dal 06/08 (corsia 0) e dal 09/08 (corsia 1) prima di
allinearsi al presente, alle 11:55: le eventuali righe di quel replay sono riconoscibili da
`RecordedAtUtc` lontano da `ClosedAtUtc` e la K41 le esclude dai giudizi.

---

## 6. Rilievi che NON sono sopravvissuti alla verifica

- «Le corsie non leggibili sono impegnate» — no: timeout durante le tempeste del nodo (§2).
- «Il pod perde la configurazione scritta dalla UI a ogni riavvio» — no: `/app/appsettings.json` è
  su PVC (`infra/k8s/trading/deployment.yaml:21, 157-159`), datato 31/07, e porta i valori salvati.
- «L'artifact “Run senza ensemble applicabile” sopprime i grigi» — vero nel meccanismo, ma sul DB
  vivo gli artifact che sopprimevano davvero erano i due «Candidato scartato» (§2 F4).
- «Il rifiuto di sostituzione scrive una riga a ogni tick» — sì per la guardia dei duplicati
  (dentro `ExecuteReplaceAsync`, fuori da `RifiutoNuovo`), no per i rifiuti di budget e corsia
  (deduplicati a `Worker.cs:778`).
- «Corsia 2 con allocazione 0 non opera» — no: opera a taglia piena per gamba (§3).
- «Lo Sharpe per regime a −146 in `/regimes` è un bug» — no: è lo Sharpe annualizzato (√525 600 su
  1 m) di strategie che sanguinano commissioni a ogni barra; brutto ma aritmeticamente esatto.

---

## 7. Decisioni che restano al proprietario, con il numero davanti

1. **`EnsembleComparator:MinDistinctSymbols`** (oggi 2): con cacce mono-simbolo la ri-applica sulle
   corsie 0-2 non applicherà mai nulla. A 1 tornerebbe viva, riscrivendo le corsie 0-1 con le due
   gambe grigie del run migliore quando batte l'incumbent (+10 %, z ≥ 0,35); oggi le corsie 0-1 sono
   governate a mano da questa revisione. Scelta: automazione (con il difetto F-LanesUsed aperto,
   `PipelineApplier.cs:185` vs `CampaignPlanner.cs:361-366`) o governo umano.
2. **Il carry**: persistere episodi e funding incassato (tabella nuova, migrazione scritta a mano) e
   un tetto aggregato al sizing. Senza, la sola classe positiva non produce evidenza.
3. **La macchina**: 18 spegnimenti brutali in tre settimane sono la causa prima di ogni «non
   leggibile», «tempo scaduto» e riavvio dei pod. Nessuna correzione software li sostituisce.
4. **`Campaign:MonthlyHourBudget`** (oggi nessun tetto, 101 h/mese misurate): la caccia 1m costa
   ancora 5,7 h/run nella stima della pagina (un run), le altre da 1 a 44 min.
5. **Probe di ingestion e ml** a 5 s × 3 contro 20 s × 10 del trading: allinearle costa una riga.

---

## 8. Cosa verificare dopo il merge di PR #139 (livello 4)

- `procione stato`: 26 controlli, revisione del motore = merge.
- Journal della flotta (`/admin/autonomy`): nessun `Retire/Refused` ripetuto per lo stesso RunId;
  al primo tick con la corsia 5 inerte, un `ReplaceLaneOccupant` verso un candidato **non**
  collidente; la corsia 4 **non** ritirata l'11/09 a zero trade.
- La corsia 7 con lo short aperto: un `FleetNoOp` «posizioni APERTE» al posto del ritiro, finché lo
  stop o il take profit non chiudono.
- `/trading` corsie 0 e 1 in corsa, prima operazione chiusa con `RecordedAtUtc` ≈ `ClosedAtUtc`.
