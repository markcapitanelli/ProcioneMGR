# Revisione della sessione «Filone K — PRD Autonomia piena» (2026-08-31 → 09-03)

> **Data:** 2026-09-03, revisione autonoma eseguita mentre il proprietario era assente.
> **Oggetto:** i 64 commit fra `9e4b690` (master del 28/08) e `e68e956` (PR #133), più il lavoro
> **non committato** nel worktree `procione-mgr-roadmap-ai-829af2`.
> **Metodo:** dieci dimensioni di lettura (flotta/ritiro, comitato AI, caccia, motore/corsie,
> migrazioni, notifiche/UI, qualità dei test, coerenza dei documenti, regole/sicurezza, plancia)
> con verifica avversariale delle segnalazioni, poi i quattro livelli di `STANDARD-VERIFICA.md`.
> Due dimensioni (regole/sicurezza, plancia/script) **non sono state completate**: il limite di
> sessione ha ucciso gli agenti due volte. Quello che ne è stato coperto di sponda è in §6.

---

## 0. Il fatto più importante: K59 non è in master

La PR #133 («K58 copertura, K59 il tetto in ore, K60 il proponitore») è stata mergiata alle 06:41
UTC, ma contiene solo `HuntBudget` (la funzione pura) e `HuntProposer`. **Il guardiano
`HuntBudgetWorker`, le tre manopole in `/admin/autonomy`, il riquadro «Budget della caccia» in
`/pipeline`, `GuardianoDelBudgetK59Tests` e il §8 del doc 41 sono ancora modifiche non committate**
nel worktree della sessione (`git status`: 7 file modificati, 2 non tracciati). Il doc 41 in master
dice onestamente «non ancora agganciato a un worker»; la versione nel worktree dice «chiusi».

La **suite completa finale** della sessione non era mai stata registrata: il comando in background è
morto col processo. Rieseguita qui (§1): verde.

Quel diff non va committato com'è: porta i difetti di §3.8-3.10.

---

## 1. I quattro livelli

| Livello | Esito |
|---|---|
| Build del worktree (ramo + non committato) | `dotnet build ProcioneMGR.sln -c Release`: **0 errori**, 2 avvisi NU1902 (AngleSharp, pre-esistenti) |
| Test mirati (K58-K60, K59, K57, K54b, K50, guardiani config/migrazioni) | **57/57** in 4 s |
| Suite completa (`--no-build`, Testcontainers) | **3595/3595, 0 ignorati, 28 m 30 s** — durata normale, quindi corsa valida (la soglia «macchina satura» è 1 h+) |
| Dal vivo su `f510ddd` (guscio riavviato da `sync-piani` alle 08:59, pod deployato) | 6 conferme e **3 difetti** (§2) |

---

## 2. Dal vivo (livello 4)

**Regge:**
- `/market/watchlist`: le 5 serie a 30m (BTC, ETH, SOL, LINK, LTC) sono abilitate e fresche —
  27.806 candele ciascuna, ultima 06:30Z, sincronizzate 06:52Z (doc 41 §8.2).
- `/campaign`: **9 configurazioni** in rotazione; le 5 nuove sono partite al riarmo (in `/pipeline`
  compaiono con «1 run»), doc 41 §2.
- `/pipeline` → Modifica: il campo K56 «Ore minime fra due run» c'è (24 sulla cfg 9).
- `/admin/autonomy`: sonda K1/K2 («Campaign Planner ACCESO E OPERANTE», «Orchestratore … 5 corsie
  sotto governo»), K47 «Vita degli esperimenti», K55 «Esposizione grigia su due percorsi: 5 di
  flotta + 2 d'impronta», backfill K22 con anteprima.
- `/admin/ai-supervisor`: comitato con 3/3 provider, tabella K52 presente.
- Home: «Battiti: 3 attivi · guscio su f510ddd9», «Decadimento delle gambe: nessun allarme,
  0 gambe misurabili su 8».

**Non regge:**

| # | Dove | Cosa si vede | Causa (verificata nel codice) |
|---|---|---|---|
| L4-1 | `/pipeline`, riquadro K58 | **«0 su 227 celle (0%)»**: tutte le serie dichiarate «mai cacciate», anche le 34 a 1h/4h cacciate da 17 e 18 (il doc 41 misura 125 cacciate su 222) | `Pipeline.razor` `ReloadAsync`: `CaricaCoperturaAsync()` gira **prima** di `Svc.ReloadAsync()`, quindi al primo caricamento `_configs` è vuota, `attive = []` e il lettore non vede nessuna caccia. Riproducibile a ogni apertura della pagina |
| L4-2 | `/admin/ai-supervisor`, tabella K52 | «ora configurato: claude-opus-4-8» su **tutti e quattro** i provider, badge «modello cambiato» sempre acceso | `AiSupervisor.razor:1154` `GetModelFor` confronta con le costanti PascalCase (`"Nvidia"`), ma `LlmUsage.cs:142` salva il provider in minuscolo: lo switch cade sempre sul default `Model` |
| L4-3 | `/admin/autonomy`, journal | Ogni riga `Blocked` («NESSUNA corsia di flotta libera») porta il badge verde **«eseguita»**, una ogni 15′ | Sette scrittori del journal non impostano `Outcome`, che nasce `Applied` (§3.1) |

---

## 3. Segnalazioni confermate — gravità alta

Verificate nel codice (righe riaperte, chiamanti seguiti); dove c'è un test che le copre lo dico.

### 3.1 K51: sette scrittori del journal non scrivono `Outcome`
`FleetOrchestratorWorker.cs` righe 531, 574, 595, 615, 640, 658, 797: nessun `Outcome =`; la riga
nasce con l'inizializzatore `= DecisionOutcome.Applied` (`OrchestratorDecision.cs:52`). Solo il
percorso «intento» (riga 840) lo scrive. Un `Retire` fallito (`Applied=false, Error=…`), un
`Assign` rifiutato, un `Blocked`, un `RetirePending` risultano **«eseguita»** nel pannello (ramo
`default` dello switch in `Autonomy.razor:1022-1048`). Nessun codice di produzione scrive mai
`Refused`. Il default a DB (`SET DEFAULT 'Applied'`, migrazione `DecisionOutcomeDefault`) ha lo
stesso verso: chi non conosce la colonna dichiara «avvenuto». **Fix:** `Outcome` esplicito in ogni
scrittore (`Refused` nei gate, `error is null ? Applied : Failed` nel ritiro), default DB/CLR su
`Unknown`, migrazione correttiva idempotente per le righe con `Applied=false`.

### 3.2 K51 non copre il ritiro
`ExecuteRetireAsync` chiama `engine.StopAsync` (786) e **poi** `JournalAsync` (797): l'INSERT che
fallisce lascia la corsia ferma senza riga — il caso «quattro arresti su quattro senza riga» che ha
motivato K51. Gli intenti esistono solo per `Assign`. **Fix:** riga `Retire/Intended` prima dello
stop, fail-closed; `RiconciliaIntentiAppesiAsync` già la chiuderebbe.

### 3.3 Un `Assign` rifiutato brucia il candidato per 14 giorni
`FleetStateReader.cs:213-217`: `assignedByFleet` prende ogni riga `Kind="Assign"` **senza guardare
`Applied`/`Outcome`**, quindi anche i rifiuti del gate, i dry-run e gli intenti chiusi `Failed`. Il
candidato (e per identità tutti i run della stessa chiave in `CandidateMaxAgeDays`) diventa
`AlreadyHandled` e il no-op dice «tutti già schierati». **Fix:** contare solo `d.Applied`; i `Failed`
in un insieme separato con finestra breve e motivo proprio.

### 3.4 Comitato: la conferma conta i pareggi, non i giri
`DeclareCommitteeFaultAsync` è chiamata solo dentro `if (… plan.Menu is { } menu)` (434→475): tre
«giri» sono tre **pareggi**, non 45 minuti. Nel caso che ha motivato K52 (Groq/NVIDIA morti dal
17/08, due sole consultazioni in sedici giorni) la conferma non sarebbe mai arrivata. In più
(verificato): un giro con zero voti (budget esaurito, comitato spento) o un'astensione per 429/503
azzera `LastCommitteeFault`, spegne il riquadro, logga «tornati a rispondere» e **riarma la notifica
critica** — una notifica per ogni esaurimento del budget. **Fix:** contare il tempo dalla prima
caduta senza voto valido nel frattempo, o un probe leggero per tick; con `Votes.Count == 0` non
toccare stato né flag.

### 3.5 K36: l'allarme «corsia ferma con posizioni» non si riarma
`LaneInvariantWatchdog.cs:139-142`: il flag si toglie solo se la corsia compare fra le ferme con zero
posizioni; una corsia che riparte e si ferma di nuovo con posizioni aperte non allarma più.
**Fix:** `Remove(laneId)` quando `IsRunning` è true.

### 3.6 K56: il riarmo a tempo non conosce la cadenza propria
`CampaignPlanner.cs:166-170` giudica eleggibile per solo backoff; `TryStartNextConfigAsync`
(454-461) poi salta per cadenza e, senza avvii, passa a `WaitingForTrigger` con **Warning**. Il riarmo
non muove `LastRunAtUtc`: con tutte le config entro cadenza (oggi 7 su 9 a 24-48h) il ciclo
Waiting→Rotating→«esaurita» si ripete ogni due tick, un Warning ogni ~2 minuti fino al tetto 20/h,
che poi zittisce gli altri produttori. **Fix:** eleggibilità = backoff **e** cadenza; «tutte in
attesa» = esito informativo senza cambio di stato.

### 3.7 K41: `RecordedAtUtc` è scritto e mai letto
Zero lettori (solo DbContext e modelli). Fermo + riavvio manuale di una corsia Paper con la stessa
gamba → `StartAsync` crea stato nuovo (`LastCandleUtc` null) → replay a −30 giorni → trade con tempi
di candela nel periodo fermo, dopo l'ancora K18 e senza originale da deduplicare: entrano nel ritiro
e nel decadimento come vivi. **Fix:** filtro «trade vivo» su `RecordedAtUtc − ClosedAtUtc` in
`TradeDeduplication`, col conteggio scartato dichiarato.

### 3.8 K59 (non committato): con `BudgetAutoApply` le cadenze raddoppiano fino a 336 h
`LeggiCostiAsync` proietta le ore/mese dalle sole durate **osservate** (`oreMese = Ore/Giorni*30`),
senza la cadenza in vigore; `Riallinea` assume che quelle ore corrispondano alla cadenza attuale.
Dopo una riscrittura le ore osservate non cambiano per settimane → lo sforo è «visto» di nuovo al
giro dopo → 48→96→192→336 in tre ore, poi la config successiva. Con un solo run lo span è 0 →
«1 giorno» → 21,9 h/mese per la cfg 19. Il commento di `CostoCaccia` («contando la cadenza propria»)
descrive ciò che il codice non fa. **Fix:** proiettare al ritmo in vigore quando la cadenza è > 0
(`mediana/60 × 720/cadenza`), osservato solo con span sufficiente.

### 3.9 K59 (non committato): «Guarda adesso» scrive, e nessuno lascia il nome
`GuardaBudgetAsync` e `ProponiCacciaAsync` chiamano `TickAsync`, che con `BudgetAutoApply` riscrive
`MinHoursBetweenRuns` e notifica; `Ultimo`/`_sforoNotificato` sono condivisi col worker senza
sincronizzazione; `SaveConfigAsync` riscrive la cadenza dalla bozza (stantia) e annulla la
riscrittura in silenzio — nessun token di concorrenza su `PipelineConfigurations`. La cadenza inoltre
è letta **solo** dal `CampaignPlanner`: per una config a cron la «riscrittura» è un no-op dichiarato
«cadenze riallineate». **Fix:** `TickAsync(applica:false)` per il pulsante, un solo scrittore della
cadenza (applicazione dal pannello via `PipelinePageService`, con identità), campo «chi ha scritto».

### 3.10 K60: senza tetto il proponitore riceve 4,5·10³⁰⁷ ore
`Pipeline.razor:1134` `residue = double.MaxValue/4` con `MonthlyHourBudget = 0` (stato vivo): il
comitato riceve «Budget residuo: 4494232837…,0 ore/mese», la proposta esce sempre a 12 h (la più
costosa), e il pannello non dice che non c'è un tetto. `CadenzaCheEntra` inoltre salta da 192 a 384 e
non prova mai 336. **Fix:** residuo `double?`, cadenza del modello quando è null, «senza tetto» a
schermo.

### 3.11 K49: la guardia dell'auto-apply è un no-op per i gruppi multi-gamba
`PipelineApplier.cs:151-153`: `chiaveGamba` esiste solo se `Strategies.Count == 1`; con chiave
vuota `HypothesisGuard.Check` restituisce «non bloccato». I gruppi (simbolo, timeframe) dell'auto-apply
sono di norma multi-gamba: la porta che K49 dichiara chiusa è aperta nel caso ordinario, e nessun
test la esercita (`PotaturaEPorteK49Tests` copre solo K49b). Il messaggio conta le corsie saltate
come distribuite (176). **Fix:** una chiave per gamba, `Check` per ciascuna; `result.LanesUsed` nel
messaggio; test dell'applier.

### 3.12 K54: l'àncora è l'ora di schieramento (non verificata avversarialmente)
`ExpectationEvidence` conta le rivalutazioni dopo `leg.ExpectedSharpeAtUtc`, che i tre percorsi di
schieramento timbrano a `DateTime.UtcNow` (`GreyDeployer:239`, `PipelineApplier:313`,
`EnsemblePageService:280/428`). La corsia 6 porta un numero del 21/08 ma è stata schierata il 31/08:
le rivalutazioni «dopo» sono meno di `MinMisurePerGiudicare = 5`, quindi non è giudicabile e
`/ensemble` mostra solo «Sharpe atteso 1,88» (visto dal vivo). I test K54 fissano l'àncora sintetica
al 21/08 e non lo vedono. **Da confermare** con una lettura dedicata; se regge, il falso allarme che
K54 doveva togliere resta.

### 3.13 Test che non provano ciò che dichiarano
- `FleetRitiroK18Tests:32`: **guardiano di sorgente** (`Assert.Contains` sul testo di
  `TradingEngine.cs`), mentre `TradingEngineEquityRetentionTests` ha già il motore vero per un test
  di comportamento.
- `GuardianoDelBudgetK59Tests`: il worker non è mai esercitato; «scrive solo se glielo si dice» è
  asserito su un record costruito dal test; l'asserzione «la non giudicabile va in fondo» è dentro
  un `if` che con quei fixture non entra mai.
- Nessun test per: K42 (isteresi del ritiro), K46, K56, la guardia K49 dell'applier, il gate K57 in
  `GreyDeployer`, `ProponiAsync`/`HuntCoverageReader` (K58/K60), `ExpectationEvidenceReader` (K54),
  la notifica K52. `ScriptsSintassiTests` e `PlanciaRisveglioTests` escono subito su Linux: in CI
  (ubuntu) sono verdi senza aver parsato nulla.

### 3.14 Documenti: K1 dichiara una scheda in Home che non esiste
Il criterio di K1 nel PRD («la scheda deve dire guscio 0 · plancia 13 · pod 0») non è realizzato:
Home mostra solo «guscio su <sha>»; il confronto con HEAD vive solo in `procione stato`.

---

## 4. Confermate — gravità media (in breve)

- **Migrazioni:** default `Outcome='Applied'` a DB/modello/CLR (vedi 3.1); il guardiano K53 misura lo
  schema del **modello** (`EnsureCreated`) su 3 tabelle, non la catena delle migrazioni — il guasto che
  porta il suo nome passerebbe di nuovo; `WidenOrchestratorDecisionSource.Down()` non è eseguibile sul
  DB vivo (22001) e non lo dichiara.
- **Flotta:** K37 con journal muto etichetta dal run più recente contro commento e PRD (e il test lo
  cementa); K48 non conserva la configurazione precedente che dichiara irrecuperabile;
  `RetireStreaks` copia il dizionario vivo senza lock.
- **Comitato:** «Prova il comitato» dice «QUORUM IRRAGGIUNGIBILE … è per sempre» su **un** 404,
  contro la rettifica K53; il journal scrive `default:provider-guasti` su un solo 404; tabella K52,
  badge «non risponde da N giorni» e prova K53 dipendono da `Llm:Budget:TrackingEnabled` senza dirlo
  (oggi è acceso); nel guard un 404/410 **chiude** il breaker e azzera i fallimenti («operativo» in
  verde con l'AI attiva morta); `HuntProposer` etichetta sempre `quorum-mancato` e non alimenta
  l'isteresi; la «prova» K53 conta come funzionante un modello che risponde senza testo.
- **Motore:** K54 `StatusMessage` e log raccontano il rapporto storico anche quando il verdetto usa la
  stima corrente («In linea … (27%)»); K35 con `DriveProtectiveExits=true` una UPDATE per **tick** per
  posizione, non per candela (latente: il flag è false per misura); K57 il lettore aggrega righe di
  entrambi i motori walk-forward mentre la soglia è calibrata sul corrente.
- **Caccia:** `HuntBudgetWorker` è un terzo scrittore senza token di concorrenza; K49b equipara
  «disabilitata in watchlist» a «sospesa dall'exchange»; `Riallinea` spinge la prima caccia a 336 h
  prima di toccare la seconda.
- **UI/config:** nessuna regola in `AdminConfigRules` per le tre chiavi K59 (il worker clampa
  5..1440, il pannello mostra il valore salvato); `appsettings.json.example` senza le chiavi K59 e
  `Fleet:BlockDuplicateTriple`; soglia dei battiti in Home cablata a 10′ mentre `Heartbeat:StaleMinutes`
  è amministrabile; `/regimes` resta una scrittura immediata sulla corsia 0; la corsia dei critici K6
  non ha superficie di pressione.
- **Documenti:** Fase 3 del PRD ha K22/K27 ✅ dentro «Restano» e K51/K21 aperti benché chiusi; §8
  «decisioni che restano al proprietario» sono tutte già prese; §6 e ROADMAP dicono ancora
  `DriveProtectiveExits=true` nel file vivo (oggi è **false**, verificato); K56-K60 e i doc 39-41
  assenti dal PRD e dalla ROADMAP; K23 non nel codice e K24 non menzionato nel doc 39; il gate della
  Fase 1 (una riga `Retire` non umana) non è mai stato raggiunto ma si è passati oltre; `00_INDEX.md`
  fermo al 4 agosto; `docs/pagine` non toccate da nessun commit del filone; tre monte-ore diversi
  (35 h/8, 35 h/9, 32 h/9) nello stesso giorno.

## 5. Bassa (elenco)
Down non eseguibile dichiarato; ordine di rilascio guscio→pod per `RecordedAtUtc` (RETURNING su
colonna assente) non documentato; K8 il battito del carry avanza anche con zero simboli valutati;
K55 ternario morto e frase fissa su `MaxGreyLegs`; K47 lettura fallita che sparisce senza dirlo;
K58 badge verde su un'approssimazione; asserzioni vacue/tautologiche in K58K60, K59, K54b, K48, K5b;
pin di costanti misurate; guardiani di sorgente per K15/K41/hosted service e dipendenza da
`PROCIONE_REPO`; MKR «delistato» vs «sospeso» in due punti del PRD.

---

## 6. Ciò che NON è stato coperto, e cosa se ne sa di sponda

- **Regole e sicurezza (trasversale):** agente caduto. Di sponda: nessuna riga in `Services/Fleet` o
  `Services/Ensemble` usa `TradingMode.Live/Testnet` per avviare (solo `Paper` letterale in
  `GreyDeployer:290`); `Services/Llm` resta senza esecuzione (`CommitteeDiagnosis` e classificatore
  puri; `HuntProposer` sta in `Services/Pipeline` e solo propone); `SafetyChecker` non compare nel
  diff; `DriveProtectiveExits` **false** nel file vivo e nei default; nessun segreto trovato nei
  diff letti (l'id account NVIDIA nel doc 39 è troncato). Non verificati: gate K4 «CI verde» in
  `deploy-trading.ps1` con GitHub irraggiungibile, `[Authorize]` su tutte le pagine toccate (solo
  quelle di §UI), endpoint `/health/quiet` (senza auth, rivela «2 posizioni aperte»).
- **Plancia e script:** agente caduto. Osservato dal vivo: `sync-piani` ha **riavviato il guscio alle
  08:59 con un utente loggato e 2 posizioni aperte** («vivono nel pod»); la plancia si è aggiornata da
  sola («segnale inviato ma il supervisore risulta ancora vivo», poi «supervisore fermato»/riavviato);
  la veglia è scaduta **due volte** a 240 s al riavvio del PC mentre Docker partiva — allarme
  prevedibile, non un guasto; il `procione.cmd` di un worktree compila un exe **stantio** che non
  conosce il lavoro `piani` (usare l'exe del repo principale o `--ricompila`).

---

## 7. Che cosa fare, in ordine

1. **Decidere il diff non committato** del worktree: non committarlo com'è (3.8-3.10); correggere
   proiezione/scrittori/residuo, poi committarlo **insieme** al doc 41 §8.
2. **Due correzioni da una riga** sul codice già in master: ordine `Svc.ReloadAsync()` →
   `CaricaCoperturaAsync()` in `Pipeline.razor` (L4-1); confronto case-insensitive in `GetModelFor`
   (L4-2).
3. **`Outcome` esplicito nei sette scrittori** + default `Unknown` + migrazione correttiva (3.1), e
   l'intento anche sul ritiro (3.2). Poi il filtro `Applied` in `assignedByFleet` (3.3).
4. Riarmo K56 (3.6) prima che le nove cadenze producano il ciclo di Warning.
5. Comitato: conferma nel tempo e giri non informativi (3.4); watchdog K36 (3.5).
6. Test veri per K18, K42, K49, K56, K59; guardiano delle migrazioni sulla **catena** e non sul
   modello; gate su `pwsh` invece che su Windows nei test degli script.
7. Documenti: PRD (Fase 3, §6, §8, K56-K60), ROADMAP, `00_INDEX.md`, `docs/pagine`; poi ricaricare
   39-42 nel notebook.

---

## 8. Applicato la sera stessa (2026-09-03, non committato)

Su richiesta del proprietario, nel worktree della sessione, insieme al diff K59:

| Segnalazione | Correzione |
|---|---|
| L4-1 copertura «0 su 227» | `Pipeline.razor`: le configurazioni si caricano prima della copertura |
| L4-2 «ora configurato» su tutti | `GetModelFor` confronta senza distinzione di maiuscole |
| L4-3 / 3.1 «eseguita» su tutto | `Outcome` esplicito nei sette scrittori; nuova costante `Noted` per Blocked/RetirePending; default **`Unknown`** nel modello, nello snapshot e a database (migrazione `20260903190000_DecisionOutcomeUnknownDefault`, che riclassifica le righe contraddittorie: Error ⇒ Failed, Blocked/RetirePending ⇒ Noted, altrimenti Refused); il pannello mostra «annotata» e «esito non dichiarato» invece del verde di default |
| 3.2 ritiro senza intento | `ExecuteRetireAsync` apre una riga `Retire/Intended` prima dello stop (fail-closed), la chiude Applied/Failed; rifiuti per modalità/corsia ferma ⇒ Refused |
| 3.3 candidato bruciato | `assignedByFleet` conta Applied/Intended/Unknown e i Failed delle ultime 24 h; mai i Refused |
| 3.4 comitato | zero voti ⇒ nessun cambio di stato; confermato = serie ≥ 3 senza voto valido nel giro; `provider-guasti` a journal solo se il caduto è confermato; il commento e il probe dicono che i «giri» sono consultazioni |
| 3.5 K36 | il flag si riarma quando la corsia gira |
| 3.6 K56 riarmo | eleggibilità = backoff **e** cadenza; «tutte entro cadenza» senza Warning |
| 3.8 K59 raddoppio | `HuntBudget.ProiettaOreAlMese`: al ritmo in vigore quando c'è una cadenza |
| 3.9 «Guarda adesso» | `MisuraAsync` (sola lettura); `TickAsync` serializzato; la cadenza vale anche per lo scheduler a cron; log di chi ha riscritto |
| 3.10 K60 senza tetto | residuo `double?`; cadenza del modello e testo «nessun tetto impostato»; `CadenzaCheEntra` prova 336; badge «senza tetto» |
| 3.11 K49 multi-gamba | una chiave per ogni gamba; messaggio con `LanesUsed` decurtato |
| media: K54 messaggio, probe «per sempre», regole K59, esempio di configurazione | `StatusMessage` sulla stima corrente; testo del probe con la rettifica K53; `AdminConfigRules` per le tre chiavi; `appsettings.json.example` con K59 e `Fleet:BlockDuplicateTriple` |
| test | K52 (provider-guasti solo con conferma + nullo), K53 (default Unknown), K59 (asserzione senza `if`), K58K60 (proiezione, un solo run, cadenza massima) |

**Non applicato, di proposito:** 3.7 (`RecordedAtUtc` mai letto: servono una soglia misurata e una
decisione sulle 371 righe storiche a `NULL`); 3.12 (àncora K54 = schieramento: richiede il run
sorgente della gamba); K57 filtro per motore (richiede una chiave di configurazione e il suo pannello);
la tabella «chi ha riscritto la cadenza» (colonna nuova); i test di integrazione su Postgres per
`HuntBudgetWorker`, `LaneInvariantWatchdog` (riarmo) e `FleetStateReader` (filtro).

Verifica del primo giro: build 0 errori; 348 test mirati verdi; **suite completa 3601/3601 in
27 m 33 s**.

### 8.1 Il secondo giro: le regressioni trovate dalla revisione delle correzioni

Tre revisori sul diff delle correzioni hanno trovato ciò che il primo giro aveva rotto o lasciato a
metà. Corretto la sera stessa:

| Regressione | Correzione |
|---|---|
| Senza «bruciare» i rifiutati, un candidato che il braccio non può eseguire per una causa stabile (ensemble multi-gamba, corsia non autorizzata) restava in testa alla coda FIFO e bloccava gli altri per 14 giorni | i `Refused` a dry-run SPENTO contano come gestiti per 24 h (la coda avanza); in dry-run non brucia nulla; `Unknown` conta solo se porta un errore o è applicato |
| Un rifiuto per tick = 96 righe al giorno per candidato, e il comitato riconsultato a ogni tick sullo stesso menù (budget LLM bruciato per decisioni che nessuno esegue) | i rifiuti si scrivono una volta per causa (come i `Blocked`); lo stesso menù non si riconsulta: il verdetto precedente si riusa, voti ed eletto compresi |
| Confermato = serie ≥ 3 senza vincolo sul giro: un provider tolto dal comitato restava «confermato», falsava i superstiti e dichiarava irraggiungibile un quorum appena raggiunto | confermato = serie ≥ 3 **e interrogato in questo giro** senza voto valido; test «tolto dal comitato esce dalla diagnosi» |
| Il riquadro del comitato spariva quando il confermato si asteneva per altra causa (503, timeout) | acceso finché c'è un confermato; elenco = sospetti ∪ confermati; con zero voti il quadro dichiara di essere più vecchio dell'ultima interrogazione |
| Proiezione K59 con due stimatori (mediana con cadenza, somma senza): scrivere una cadenza faceva «rientrare» lo sforo senza cambiare il consumo; un run solo a cadenza 0 valeva ancora 30 run/mese; una cadenza valeva come schedulazione anche per una caccia lanciata una volta a mano | un solo stimatore: `durata media × min(ritmo della cadenza, ritmo osservato)`, con l'età della finestra misurata fino a oggi; `RunAlMese` nel `CostoCaccia` e nella cadenza implicita |
| Scrittura fallita con `BudgetAutoApply` acceso: la notifica diceva «è spento» | terzo stato `ScritturaFallita`: notifica Critical dedicata, badge nel pannello, si rinotifica finché non riesce |
| Wake di regime non azzerato nel ramo «tutte entro cadenza»: il riarmo a tempo lo ritrovava e marcava «Event» un run partito ore dopo | `PendingWakeReason` azzerato e dichiarato «assorbito» nell'esito |
| Il cron contava solo i run `Completed`: una config che fallisce veniva rilanciata a ogni slot | qualunque esito terminale (Completed/Failed/Cancelled) |
| «smentito dalle rivalutazioni» anche quando l'evidenza CONFERMA l'atteso; log di decadimento ancora sull'atteso d'origine | testo su `Contraddetta`; log sul metro del verdetto |
| Testi: tooltip della cadenza (ora vale anche a cron), 35 vs 32 ore nel POCO, «Scarica modelli» che non esiste, soglia del probe dal form invece che in vigore, avvisi ripetuti per gamba, ritiri storici «corsia già ferma» marcati `Failed` dalla migrazione | tutti allineati |

Test aggiunti nel secondo giro: K52 (zero voti non cancella la conferma; timeout non guarisce;
provider tolto esce dalla diagnosi), K58K60 (la cadenza non inventa un ritmo; un solo run decade con
l'età della finestra).

**Ancora senza test di integrazione** (dichiarato): il filtro di `FleetStateReader` su Postgres,
l'intento del ritiro con un motore finto, il riuso del menù del comitato.

### 8.2 Livello 4 sul codice corretto (2026-09-03, 23:15-23:25)

Build 0 errori; 293 test mirati verdi dopo il secondo giro. App del worktree avviata sulla 5199 con
login reale (guscio e supervisore fermati per la durata del collaudo, poi ripristinati); la
migrazione `20260903190000_DecisionOutcomeUnknownDefault` è stata applicata al DB vivo all'avvio
(«Migrazioni pendenti (1)… Le applico ora»), nessun errore server, nessun errore di console
dell'app.

| Pagina | Visto |
|---|---|
| `/pipeline` | copertura **130 su 227 celle (57 %)**, 97 mai cacciate (prima: «0 su 227»); budget «40,0 ore/mese al ritmo attuale su 13 configurazioni · tetto: nessuno · propone e basta», misurato dal worker alle 23:17 e rimisurato da «Guarda adesso» alle 23:21 senza scrivere; «Proponi una caccia» → **scelta dal comitato** (fonte `committee`) fra 4 buchi: 5m su 10 serie, ~15 min/run, 7,7 h/mese **a 24h di cadenza** (la cadenza del modello, cfg 13), col badge **«senza tetto»** e la riga «nessuna proposta si adotta da sola» |
| `/admin/ai-supervisor` | tabella «Ultima risposta valida per provider» con i quattro provider e **zero** badge «ora configurato» (prima: quattro falsi) |
| `/admin/autonomy` | journal: le righe `Blocked` portano **«annotata»** (15), le `ProposeGrey` «eseguita» (5), nessun verde sui rifiuti; nessun riquadro del comitato (nessun guasto); le tre manopole K59 («Tetto caccia (ore/mese)», «Giro del budget (min)», «Applica da solo i rallentamenti») presenti con la spia ✅ delle regole |

Il numero del budget (40 h su 13 configurazioni) è più alto delle «~32 su nove» del doc 41 perché
conta TUTTE le configurazioni non disabilitate con un run negli ultimi 30 giorni, al ritmo osservato:
è il consumo vero, non quello della sola rotazione.

---

## 9. Chiusura dei punti rimasti (2026-09-04, ramo `fase4/revisione-filone-k`)

Su richiesta del proprietario («completare l'automazione»), i quattro punti dichiarati aperti in §8:

| Punto | Fatto |
|---|---|
| 3.7 `RecordedAtUtc` mai letto | `TradeDeduplication.Vivi(righe, timeframe)`: una riga scritta più di **tre barre + 30′** dopo la sua candela è replay, non un trade. Applicato in `TradingEngine.GetPerformanceAsync` (il ritiro di flotta e la promozione) e nel monitor di decadimento, con lo scarto dichiarato (`TradesExcludedReplay`, «Esclusi perché replay» in `/ensemble`). Le 371 righe storiche senza ora di parete restano. Test `TradeViviK41Tests` |
| 3.12 àncora K54 = schieramento | `ExpectationEvidenceReader` ancora l'evidenza al **run che ha prodotto il numero** (ultima misura della stessa identità, non posteriore allo schieramento, con lo stesso Sharpe holdout); se non si trova resta lo schieramento, e il racconto dice quale àncora ha usato (`AncoraDalRun`). Con i numeri veri della corsia 6: 11 rivalutazioni, mediana 0,479, contraddetta — prima non era giudicabile. Test `AncoraEvidenzaK54Tests` su Postgres |
| K57 filtro per motore | `Fleet:StabilitaDaUtc` (default 2026-08-23, la sostituzione del walk-forward su cui la soglia è stata misurata; `null` = tutte le righe), manopola in `/admin/autonomy` accanto alla guardia K33, esempio in `appsettings.json.example`; `StabilitaReader` filtra `RunCompletedUtc`. Test: sei misure larghe del motore vecchio + sei strette del nuovo ⇒ stabile col filtro, instabile senza |
| test di integrazione | `RitiroConIntentoTests` (Postgres + motore finto: riga Intended→Applied con uno stop; corsia non Paper ⇒ Refused senza stop; journal irraggiungibile ⇒ **nessuno stop**); `CandidatiGestitiDallaFlottaTests` (la regola di `ContaComeGestito` riga per riga, e 96 rifiuti in dry-run che non bruciano nulla); `AncoraEvidenzaK54Tests` (K54 e K57 su Postgres). Il riuso del menù del comitato resta senza test dedicato |

Due fixture di test hanno dovuto dichiarare quando i loro trade sono stati scritti: `EnsembleManagerDecayTests`
(righe di settimane fa inserite adesso = firma del replay) e `MultiLaneIsolationTests` (candele
sintetiche del 2026-01-01). Non è un aggiustamento: è la regola nuova che li ha trovati.

## 10. Cacce a 1m e 30m, strategie nuove, e dove stanno gli stop (2026-09-04, mattina)

Tre richieste del proprietario, arrivate insieme: le serie a 1 e 30 minuti per le stesse dieci
coppie della caccia 5m, cacce anche su quei timeframe; la caccia su **tutte** le strategie, anche
quelle future; e la domanda se stop e take profit non dovrebbero essere aggiornati «con i timeframe
più corti piuttosto che con le barre lunghe».

### 10.1 Serie e cacce: fatto, dalla UI

Tutto via interfaccia, come autorizzato (le scritture dirette al DB sono state rifiutate dal
classificatore, e va bene così: la UI è il percorso che lascia traccia).

| Cosa | Dove | Esito verificato (psql, `OhlcvData` e `PipelineConfigurations`) |
|---|---|---|
| 10 serie a 30m abilitate (5 nuove: BNB, XRP, DOGE, ADA, AVAX) | `/data` watchlist | 10/10 con 28.608 candele dal 2025-01-16: lo stage DataIngestion del primo run ha scaricato la finestra intera in ~26 s per serie |
| 10 serie a 1m abilitate (4 nuove: ADA, AVAX, LINK, LTC) | `/data` watchlist | 6 storiche da 2025-07-20 (592k candele l'una), 4 nuove con 7 gg di backfill; il resto della finestra (120+30 gg, ~860k candele) lo scarica il primo run a 1m |
| Config **21** «Caccia intraday 30m - 10 majors» | `/pipeline`, clone della 19 | finestre scorrevoli 484/112, cadenza propria 48h, stessa catena di 18 stage |
| Config **22** «Caccia intraday 1m - 10 majors» | `/pipeline`, clone della 19 | finestre **120/30** (a 1m sono ~173k e ~43k barre per serie: la 484/112 costerebbe 5× senza aggiungere regimi), cadenza 72h |
| Campagna **3** «Caccia intraday 1m+30m» | `/campaign` | rotazione [21, 22], backoff 12h, **avvio corsie Paper: no**, abilitata; primo run `8a4067ce` sulla 21 partito alle 06:10 UTC col «Tick adesso» |

Perché una campagna nuova e non la rotazione esistente: la pagina `/campaign` crea campagne, non
modifica la rotazione di una campagna viva (K-revisione §3.x), e la campagna 1 è «esaurita, in attesa
di trigger». Il planner ha una sola corsia di esecuzione (`un altro run è già in corso` →
rimandato), quindi due campagne non producono due run insieme.

La copertura K58 in `/pipeline` sul guscio vivo dice ancora «0 su 241»: è il difetto «0 su 227»
corretto in questa PR (§8), non ancora schierato. Al merge le celle 30m e 1m risulteranno cacciate.

### 10.2 «Tutte le strategie, anche le nuove»: già così, e ora c'è un guardiano

La caccia enumera `IStrategyFactory.Prototypes` quando la configurazione non elenca strategie —
ed è il caso di tutte le configurazioni in rotazione (`strategies` vuoto nello stage Discovery). Una
strategia nuova registrata nella fabbrica entra da sola. La trappola è altrove:
`StrategyDiscoveryEngine.DefaultRanges` è uno `switch` sul nome col default vuoto, quindi una
strategia nella fabbrica ma dimenticata lì verrebbe «cacciata» con zero parametri, cioè non
cacciata, senza che nulla lo dica. `GriglieDiCacciaPerOgniStrategiaTests` fa rosso alla prima
strategia senza griglia (oggi 14/14 ne hanno una).

### 10.3 Stop e take profit: come funzionano davvero, e perché restano a barra chiusa

**Come sono valutati.** `ProtectiveExitEvaluator` è puro e unico per i due percorsi. A ogni
candela chiusa della corsia guarda High/Low della barra: stop (o trailing, causale) con precedenza
sul target, fill al livello oppure all'apertura se la barra ha aperto oltre — sempre l'esito
peggiore. Il feed real-time passa dallo stesso evaluator con una barra degenere
open=high=low=close=prezzo, ma con `DriveProtectiveExits=false` **osserva e basta**: rileva
l'ombra, non chiude.

**La domanda del proprietario è esattamente la misura B3.** «Aggiornare con i timeframe più corti»
è ciò che il replay B3 ha simulato usando 5m/1m come surrogato dei tick contro le barre di corsia:

| Misura | Esito | Fonte |
|---|---|---|
| B3 (2026-07-28), stop, 24 configurazioni | uscire al tocco è **peggio** in 24/24 (mediane −2 … −77 bps): lo stop preso sull'ombra è rumore che rientra | `docs/REPORT-B3-EXITLAG-2026-07-28.md` |
| B3-bis (2026-08-06), stop | negativo 4/4, −2,4 … −10,7 bps | `docs/REPORT-B3BIS-USCITE-PER-TIPO-2026-08-06.md` |
| B3-bis, take profit | al tocco meglio in 3/4, **+1,9 … +10,6 bps** prima di slippage; la 4h dice −8,8 | idem |

+1,9 bps è sotto i costi; solo una corsia (+10,6) respira. Il report chiude: per accendere servono
un interruttore **solo-target** (non esiste: il flag accende entrambi i lati), la decisione **per
corsia** e i costi dentro il criterio. Nessuno dei tre è stato fatto e questa sessione non lo fa:
è la regola 7, l'interruttore è una misura, non una svista. Su barre 1m e 30m, che ora entrano
nella caccia, la distanza fra tocco e chiusura è per costruzione più piccola che a 4h.

**Il pezzo che invece era rotto: la sentinella dal vivo.** `ProtectiveExitShadows` è lo strumento
che dovrebbe dire se il mercato smette di comportarsi come nel replay (crollo con gap). Letta oggi:
24 righe dal 4/08, **14 con |costo| fra 700 e 2.000 bps, tutte in due minuti** (23/08 18:05-18:07
corsie 1 e 6; 31/08 20:24-25 corsia 4), anticipo zero. Incrociate con `TradeRecords`: le uscite
«reali» avevano fill di barre del 20-22/08 — **replay al riavvio della corsia**, il tick di oggi
confrontato con la barra di tre giorni prima. Tre di quelle righe superavano la soglia dei 200 bps e
hanno prodotto allarmi «il feed avrebbe fatto meglio del 10%» che non descrivevano alcun mercato.
Stessa cecità di K41. Correzione: `ProtectiveExitShadowReplayGuard.EReplay` (puro: tick più
giovane della barra di oltre due passi ⇒ replay, non si scrive; timeframe ignoto ⇒ comportamento
di prima), agganciato in `ResolveShadowAsync`; `SentinellaOmbraReplayTests` (12 casi). Le 14 righe
storiche restano nella tabella e nel pannello di `/admin/protections`: vanno lette come replay, non
come mercato.

### 10.4 CI

Il run `pull_request` della PR #134 era rosso per `RegistryPageRenderTests.Ritira_AlSecondoClic…`
(`Find("input.form-control")` subito dopo il clic, senza attesa del render: passa in locale e nel
run `push`, cade sotto carico). Sostituito con `WaitForElement(…, 10 s)` nei due punti. Rilanciato
il job fallito: verde.

### 10.5 Dopo il merge (2026-09-04, sera): schierato, verificato, e il run a 1m costava 5 ore di coppie

PR #134 fusa alle 11:16 UTC. La plancia ha fatto da sola ciò che doveva: guscio ricompilato e
riavviato alle 12:34 (revisione f17b16e = pin del deploy sopra il merge), motore riavviato alle
11:48 sull'immagine `local-3233bd04`, 0 riavvii, 0 errori nei log del pod, `procione stato`
«tutto in ordine (26 controlli)». Ingestion e ml restano sulle immagini di agosto: la PR non tocca
codice che gira lì. Nessuna migrazione da applicare (la `Unknown` era già sul DB dal 3/09).

Verifiche dal vivo sul codice fuso:

| Cosa | Prima | Dopo |
|---|---|---|
| K58 copertura in `/pipeline` | «0 su 241» (difetto §8) | **145 su 241 (60%)**; 30m e 1m non più fra le «mai cacciate» |
| Journal `Blocked` senza corsia libera | 14-15 righe al giorno | **1 riga** dal riavvio (dedupe per tick): esito `Noted` |
| Comitato AI | — | vivo dopo il riavvio: voti alle 14:13, 14:17, 14:35, 17:40; modello Nvidia risolto (casing) |
| Corsie 1-7 | — | tutte `IsRunning`, ultima candela attuale (15m alle 18:45, 4h alle 12:00) |
| K59 budget | — | 101,7 ore/mese al ritmo attuale su 15 configurazioni, tetto nessuno |

**Il run a 1m.** Completato in **5h43m**: DataIngestion 14 min (823.576 candele scaricate, le 4
serie nuove ora hanno 216k candele = 150 gg), StrategyDiscovery 5 min, CreativeDiscovery 11 min,
e **PairsScreening 5h04m**: cointegrazione Engle-Granger su 45 coppie con 216k barre l'una. Esito:
2 candidati, 0 sopravvissuti. Con cadenza 72h sarebbero ~57 ore/mese, più della metà dell'intero
budget di caccia, spese in uno stage che nessuna corsia a 1m può usare. **Stage «Screening
coppie» disabilitato sulla config 22** dall'editor di `/pipeline` (verificato a DB:
`PairsScreening:false`, le altre 17 fasi invariate). Il prossimo run a 1m dovrebbe costare
~35 minuti. La 30m (17 min/run) resta com'è.

**Sette righe `Blocked` con `Outcome=Applied` e `Applied=false`** scritte il 3-4/09 dal guscio
vecchio DOPO la migrazione correttiva: il pannello le mostra come «eseguita». Nessun effetto sulle
corsie (LaneId nullo), ma vanno lette come «annotata». Rieseguire l'UPDATE della migrazione
`20260903190000` le sistemerebbe; non l'ho fatto dal vivo (scrittura diretta a DB).

**Visto di sponda, non corretto:** `SentimentSyncWorker` ha fallito 2 tick su ~80 con
`23505 IX_AltDataPoints_DedupeKey` (chiave duplicata): due scrittori sugli AltDataPoints nello
stesso processo (il worker e lo stage AltDataSync dei run) si pestano di rado. Si riprova al tick
dopo, nessun dato perso. Da guardare se cresce.

**Trappola del pannello browser** (per chi verifica dopo di me): con il pannello più stretto del
layout (782 px contro ~1120), i pulsanti della colonna Azioni sono fuori viewport e il clic
fallisce; l'emulazione di un viewport largo (`resize_window`) viene scalata e i clic non arrivano
al componente; anche Tab+Invio sul pulsante non produce il render. Funziona `document.body.style.zoom`
a 0,66 (solo per vedere, non è una modifica) e poi il clic per ref.

## 11. K61 — La sostituzione: rimpiazzare un occupante inerte (2026-09-04, sera)

Richiesta del proprietario, in due parti: «vorrei attivare lo schieramento automatico» e «vorrei che
la Regina potesse schierare in **sostituzione** di una corsia meno conveniente e priva di operazioni
da pochi giorni».

### 11.1 La prima parte era già fatta, e nessuno lo sapeva

`Fleet:GreyAutoDeploy` è **`true`** nel file vivo, con `DryRun: false`, `ExecutionLanes: [3,4,5,6,7]`,
`MaxGreyLanes: 6`, `UseCommittee: true`. L'interruttore «Schiera i GRIGI da solo (J14)» in
`/admin/autonomy` risulta acceso. Il default del POCO è `false` e i documenti dicono «default
spento»: la confusione nasce da lì, ed è la trappola già scritta in memoria — **i flag si leggono nel
file vivo, non nel `.example` né nel PRD**.

### 11.2 Il vincolo vero, misurato

Il braccio automatico è acceso, armato, e gira a vuoto da giorni scrivendo *«N candidati grigi
schierabili ma NESSUNA corsia di flotta libera (7 attive): il vincolo sono le corsie, non i
candidati»*. Le cinque corsie di flotta al 2026-09-04:

| corsia | serie | attesi/mese | osservati | trade | ultimo trade | posizione aperta |
|---|---|---|---|---|---|---|
| 3 | AAVE/USDT 4h MacdTrend | 11,11 | 3,6 gg | 1 | 01/09 | no |
| 4 | XLM/USDT 4h Composite | 1,65 | 2,6 gg | 0 | mai | no |
| 5 | UNI/USDT 4h GridMeanReversion | **null** | 9,2 gg | 3 | 27/08 | no |
| 6 | DOGE/USDT 15m GridMeanReversion | 3,80 | 3,5 gg | 4 | 04/09 | no |
| 7 | TRX/USDT 4h Supertrend | 3,29 | 2,0 gg | 0 | mai | **sì, dal 31/08** |

**Nessuna può liberarsi.** Il ritiro per Sharpe pretende 20 trade e tre settimane; quello per inedia
pretende un ritmo atteso dichiarato, che sulla corsia 5 è `null` — e la 5 è l'unica abbastanza
anziana. Il sistema è in stallo per costruzione: la caccia produce candidati che non hanno dove
andare.

### 11.3 La domanda difficile: «meno conveniente» rispetto a che cosa

L'istinto sarebbe confrontare lo Sharpe del candidato con quello della corsia. **Non sono la stessa
grandezza.** Quello del candidato è annualizzato sui rendimenti di barra (fattore √ppy: 46,8 a 4h,
187,2 a 15m), su un backtest, dopo migliaia di tentativi; quello della corsia è realizzato, fuori
campione, su 0-4 trade. Peggio: `RealizedSharpePerTrade` è `null` sotto due trade — cioè proprio
sulla corsia «senza operazioni», il numero non esiste. Un comparatore che producesse comunque un
verdetto sarebbe un «controllo che rassicura a prescindere dalla realtà», la classe di difetto del
Filone E.

**Quindi non si confronta.** Il criterio è di sola constatazione: *la corsia non sta producendo
prove*. Zero operazioni chiuse oltre la soglia di silenzio, e nessuna posizione aperta. È anche
l'argomento che risponde all'obiezione seria — il forward test Paper è l'unico giudice immune al
multiple testing e ricambiare corsie lo consuma: **una corsia muta non accumula evidenza che si
possa sprecare**, occupa uno slot senza dire niente. Sostituire una corsia che *opera* sarebbe
un'altra cosa, e questa regola non lo fa mai.

### 11.4 La soglia si scala sul ritmo dichiarato

Una soglia secca punirebbe le corsie lente: la corsia 4 dichiara 1,65 trade/mese, cioè
un'operazione ogni 18,4 giorni, e a 10 giorni risulterebbe «inerte» mentre rispetta il proprio
ritmo. La soglia è quindi `max(ReplaceIdleDays, ReplaceIdleExpectedMultiple × intervallo medio
atteso)`, e senza ritmo dichiarato resta il solo pavimento. Con i default (10 giorni, ×2,0) sulle
corsie vere: 3 → 10 gg, 4 → 36,9 gg, 5 → 10 gg, 6 → 16 gg, 7 → 18,5 gg. **Oggi scatterebbe solo
sulla corsia 5**, e fra circa due giorni: è la corsia che nessun criterio può giudicare, muta dal
27 agosto, con PnL medio −0,33 %.

### 11.5 Il danno che la guardia sulle posizioni evita

Verificato riga per riga: `StopAsync` **lascia le posizioni aperte** e logga «posizioni lasciate
aperte»; da quell'istante le uscite protettive non sono più valutate; e il successivo `StartAsync`
in Paper esegue `db.OpenPositions.Where(p => p.LaneId == laneId).ExecuteDeleteAsync(ct)` **senza
filtro di modalità** — la posizione sparisce **senza `TradeRecord`, senza PnL, senza audit**. È il
danno K36 descritto nel doc-comment di `LaneInvariantWatchdog`, già avvenuto il 2026-08-31 sulla
corsia 6 (short DOGE/USDT, 799 USDT nominali).

Oggi fra un ritiro e il riempimento passano ≥15 minuti e un umano può ancora chiudere a mano. Una
sostituzione «ferma e schiera nello stesso giro» chiude quella finestra ed esegue di sua iniziativa
la cancellazione. Il sorvegliante K36 **non è la rete di sicurezza**: si arma una volta per episodio
e si ri-arma appena la corsia riparte, quindi se stop e schieramento cadono nello stesso giro la sua
finestra di osservazione può non aprirsi mai.

Per questo `OpenPositions == 0` è nel predicato **e** viene riletto dal motore un istante prima
dello stop. Non si appiattisce la posizione: `LanePromoter` chiama `CloseAllPositionsAsync` prima
dello stop ed è giusto lì, perché un cambio di modalità deve spostare la corsia intera e non ha
alternative; qui l'alternativa c'è ed è gratis — aspettare. E una corsia con una posizione aperta,
del resto, **sta operando**: non è il bersaglio di una regola che si chiama «sostituisci ciò che è
inerte».

### 11.6 Che cosa è stato scritto

| Pezzo | Dove |
|---|---|
| Predicato puro `IsIdle` + `Silenzio` + `SogliaSilenzio` | `Services/Fleet/FleetOrchestrator.cs` |
| Ramo di sostituzione (ultima risorsa, dichiara ogni rifiuto) | idem, blocco 4 di `Decide` |
| Azione `ReplaceLaneOccupant` — **una sola** per corsia | `Services/Fleet/FleetModels.cs` |
| Campi `LastTradeUtc` e `OpenPositions` sulla corsia | `FleetModels.cs` + `FleetStateReader.cs` |
| Stabilità K57 sui candidati (`StabilityMedian/Measures/Spread`) | `FleetModels.cs` + `FleetStateReader.cs` |
| Esecuzione `ExecuteReplaceAsync` con rilettura fail-closed | `Services/Fleet/FleetOrchestratorWorker.cs` |
| Isteresi condivisa col ritiro | `ApplyRetireHysteresis`, stesso file |
| Terzo budget `MaxReplacementsPerTick` | idem |
| Otto manopole + due conteggi nel pannello | `Components/Pages/Admin/Autonomy.razor` |
| Validazioni | `Services/Config/AdminConfigRules.cs` |
| 20 prove nuove + fuzz esteso alla quarta azione | `SostituzioneCorsieK61Tests.cs`, `FleetOrchestratorTests.cs` |

**Il journal resta a due righe** (`Retire` e `Assign`, motivo prefissato da «[Sostituzione]»): i
consumatori esistenti filtrano per quei due `Kind` — in particolare la deduplica dei candidati già
gestiti — e un `Kind` nuovo li renderebbe ciechi, facendo riproporre per sempre lo stesso candidato.

**L'esito peggiore accettabile è dichiarato.** Non esiste uno schieramento atomico: il deployer
rifiuta di scrivere su una corsia che gira, quindi si ferma e poi si schiera. Se lo schieramento
fallisce, la corsia resta ferma e configurata come prima — lo stesso stato in cui la lascerebbe un
ritiro normale, reversibile con un clic — e al giro dopo il braccio ordinario la vede libera e la
riempie da sé.

### 11.7 K61b: il braccio automatico sceglieva per data

Rilievo emerso strada facendo, e vale da solo: `GreyAccorpati` ordina i candidati per
`CompletedAtUtc` **crescente**. Con 19 schierabili e uno slot, la Regina prende **il più vecchio**.
La stabilità K57 vive solo nell'ordinamento della lista che legge un umano in `/admin/autonomy`.
Misurato sulla fascia grigia del 2026-09-04:

| candidato | Sharpe del run | mediana K57 | misure | ventaglio | trade holdout |
|---|---|---|---|---|---|
| MacdTrend AAVE/USDT 4h `#f523b2ee` | 3,93 | **3,98** | 5 | 0,21 | 52 |
| EventTrigger GRT/USDT 4h `#69438482` | 3,91 | **2,79** | 20 | **3,26** | 3 |

I due sono indistinguibili per data e quasi identici per Sharpe del run. `Fleet:PreferStableGrey`
(default **spento**, perché cambia un braccio già in esercizio) ordina per mediana; il pannello
mostra accanto il conteggio dei «rimpiazzi stabili pronti», così la differenza si vede prima di
accendere.

### 11.8 Visto e non fatto

- **Nessuna metrica di flotta**: `ProcioneMetrics` non ha una sola occorrenza di «fleet». Ritiri,
  assegnazioni e sostituzioni sono invisibili a `/metrics` e a qualunque allarme Prometheus.
- **Lo schieramento non consulta la freschezza delle serie**: né `GreyDeployer` né il worker leggono
  `SeriesFreshness`/`OhlcvData`. Una corsia può partire su una serie che nessuno sincronizza. Vale
  già oggi per J14, non è introdotto da K61.
- **`AutoDemoteToPaper` è acceso**: una corsia Testnet può tornare Paper da sola ogni 6 ore e
  diventare in quell'istante una corsia di flotta. Il pavimento di residenza la protegge solo se il
  ledger dell'osservazione le assegna un'identità nuova.
- **Il lease non viene toccato** dallo schieramento: in topologia remota la corsia risulta avviata
  finché l'host del feed non riacquisisce. Vale già oggi per ogni schieramento.

### 11.9 La revisione avversariale, e le otto correzioni che ha prodotto

Sei revisori indipendenti con lenti diverse (correttezza del predicato, braccio esecutivo,
interazioni con il resto del sistema, provenienza dei dati, configurazione e superficie, qualità dei
test) hanno attaccato il commit; ogni rilievo è poi stato affidato a un verificatore incaricato di
**confutarlo**. Diciannove rilievi confermati, cinque caduti in verifica. Al netto delle
sovrapposizioni sono otto difetti distinti, tutti corretti:

| # | Difetto | Perché contava |
|---|---|---|
| 1 | `ExecuteReplaceAsync` schierava sempre col percorso grigio | Un rimpiazzo di banda «pass» avrebbe fermato la corsia e poi sarebbe **sempre** fallito allo schieramento. La banda ora viaggia sull'azione. |
| 2 | La guardia sui duplicati stava solo dentro il deployer, cioè **dopo** lo stop | È il rifiuto più probabile di tutti: il decisore puro non può vederlo (la corsia non porta l'identità delle gambe) e la caccia ritrova di continuo tarature vicine di ciò che gira già. Ora `HypothesisGuard` è consultata prima di fermare. |
| 3 | Il cancello del rimpiazzo applicava solo la mediana K57, non il criterio di instabilità | `StabilitaIpotesi.Instabile` è «mediana ≤ 0 **oppure** ventaglio > mediana». Senza il secondo mezzo, il braccio automatico avrebbe ammesso — chiamandola stabile — proprio l'ipotesi che la lista del clic umano marca «⚠ INSTABILE». |
| 4 | Il ritmo atteso azzerato per **divergenza** veniva letto come «non dichiarato» | Il lettore lo azzera per far *astenere* il ritiro per inedia; la sostituzione faceva l'opposto, applicando il pavimento secco al posto della soglia scalata — un'ammissione di ignoranza trasformata in aggravante. Ora la corsia porta il flag e la sostituzione si astiene. |
| 5 | Il tetto `MaxGreyLanes` poteva essere superato di una corsia | `GreyOccupied` legge la fotografia, dove un grigio assegnato poche righe prima non risulta ancora in corsa — e quello è proprio il giro in cui il ramo grigio consuma l'ultima corsia libera, cioè la condizione che apre la sostituzione. |
| 6 | Il pannello contava fra gli «inerti sostituibili» anche le corsie che il ritiro condanna per Sharpe | Due definizioni della stessa cosa: il difetto di D2 e di `SeriesFreshness`. Estratto `IsRetirable`, usato da decisione e spiegazione. |
| 7 | Il candidato scelto per la sostituzione veniva **anche** proposto al clic umano | Una notifica che chiede all'operatore di fare a mano ciò che la Regina sta già facendo, sulla stessa corsia. |
| 8 | Nessuna notifica quando la corsia resta ferma a metà sostituzione | La notifica dello schieramento dice «non riuscito» e non nomina il fatto nuovo: che una corsia è stata fermata e non è ripartita. Ora sono due messaggi, uno per tempo. |

Aggiunto anche un difetto trovato prima della revisione: `SogliaSilenzio` poteva far traboccare
l'aritmetica (`decimal` prima, `TimeSpan.FromDays` poi) con un ritmo atteso minuscolo, e
un'eccezione dentro la funzione pura avrebbe fermato **l'intera decisione della flotta**, ritiri
compresi, per un campo di configurazione scritto male. Ora il conto si fa in `double` e si limita.

**I test.** Da 20 a 33 prove nel file dedicato, più il fuzz. Fra le aggiunte: la prova deterministica
di K61b che mancava (a interruttore spento vince la data, acceso vince la mediana — e invertire
l'ordinamento adesso fa rosso), la tabella dell'instabilità K57, il tetto grigio nello stesso giro,
il confronto fra ciò che il pannello conta e ciò che la decisione fa, e i ritmi assurdi.

**Il fuzz** genera ora anche `ExpectedTradesPerMonth` (null, zero, potenze di dieci fino a 1e-25,
valori negativi), `LastTradeUtc`, `OpenPositions`, identità e stabilità dei candidati, e valuta le
invarianti della quarta azione: mai a interruttore spento, mai sull'impronta, mai su corsia ferma,
**mai con posizioni aperte**, mai su corsia vincolata, mai con un rimpiazzo non giudicabile.
