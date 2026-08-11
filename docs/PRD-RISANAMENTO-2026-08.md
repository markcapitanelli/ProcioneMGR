# PRD — Risanamento: bug, sincronizzazione, autonomia dal cloud (2026-08-08)

*Quattordicesima ondata. Nasce dall'audit in cinque passaggi di `docs/audit/` (00-10, 20-28) e dalla
richiesta del proprietario: fixare i bug riscontrati, eliminare o sincronizzare le parti dissociate,
riallineare configurazioni e UI, rendere il server indipendente da GitHub, far ripartire tutto
all'avvio su qualunque dispositivo.*

**Stato (2026-08-11): FASI 0-1-2-3-4-5 COMPLETE (2.4 RITIRATA) · FASE 6 da fare.**
**Mandato aggiuntivo del proprietario (2026-08-09, vincolante da qui in avanti):** nessuna
configurazione può esistere senza UI — l'amministratore governa ogni parte della macchina
dall'interfaccia. `DeliberatelyNotExposed` del guardiano è stata SVUOTATA: le ragioni che
tenevano fuori una chiave (topologia, riavvio, pericolo) sono diventate testo accanto alla
manopola, non un motivo per nasconderla.
Le decisioni di prodotto sono state prese il 2026-08-08:
**Microstructure si integra** (DI + gate IC in `/feature-selection`) · **JumpModel si cabla dietro
flag** (`IRegimeModel`, K-means resta il default) · **la portabilità si fa con Docker Compose**.
Suite di partenza: 2.096 metodi di test (inventario in `docs/audit/27_TEST_INVENTORY.md`).

### Registro di esecuzione

| Data | Cosa | Commit |
|---|---|---|
| 2026-08-08 | **Fase 0a** — file dei segreti fuori dall'indice, `.gitignore` di famiglia, guardia in CI | `8c9d887` |
| 2026-08-08 | **Fase 0b** — keyring multi-chiave (formato v1 invariato), `MasterKeyRotationService`, pannello "Ri-cifra ora" in `/settings/exchanges`, 8 test | `cc0f49b` |
| 2026-08-08 | **1.2 + 1.3** — esposizione futures in nozionale (`SafetyExposure`), `DriveProtectiveExits` a `false`, `SecurityDefaultsTests` (9 default presidiati) | `9e4a010` |
| 2026-08-08 | **1.5 + 1.6 + 1.7** — validazione range sul percorso obbligato (`PipelineDateRanges.Validate`), stoppini asimmetrici nel nullo, seed dichiarati | `dd3eff4` |
| 2026-08-08 | **1.1** — `TrialsExplored` nel contesto, gate DSR su `max(candidati, esplorate)` × rapporto di collasso; retrocompatibile a `trialsExplored=0` | `7bbab82` |
| 2026-08-08 | **1.4** — `IFundingHistoryProvider` agganciato al backtest (leva > 1), `FundingModelUsed` dichiarato in `/backtest` | `97b8fc1` |
| 2026-08-08 | **2.2 + 2.3** — registrazione morta di `BayesianSearch` rimossa, `ICombinatorialPurgedCv` eliminata; **G-12 RITIRATO** (è vero LightGBM: errore dell'audit, corretto nei doc 04/06) | `544c34f` |
| 2026-08-08 | **2.1** — `ISymbolCatalog` con cache e politica dichiarata al posto delle 7 scansioni; invalidazione dalla watchlist | `5c7d1bb` |
| 2026-08-09 | **PR #72 MERGED** in master (CI tutta verde, inclusi i 458 Testcontainers) | `734560c` |
| 2026-08-09 | **2.5 + 2.7 + 2.8** — `events.proto` marcato riservato nel file · JumpModel dietro flag `MarketRegime:Model` (contratto C1 rispettato: default KMeans bit-identico, confronto transizioni nei log di ogni training, formato persistito invariato) · optimizer dell'ensemble per nome (`portfolioOptimizer`, default HRP; `Method` dichiara quello reale; visibile in /pipeline; chiude **C-04** e **C-05**) | `99abd8a`+ |
| 2026-08-09 | **ROTAZIONE COMPLETA DEI TRE SEGRETI**: gRPC ruotato via script (mai mostrato) · password Postgres ruotata dal proprietario (ALTER ROLE + connection string) · Secret K8s riallineati col nuovo `scripts/update-k8s-secrets-from-appsettings.ps1` (lezione: per i pod `Host=host.docker.internal`) · pod trading/ingestion/ml riavviati, probe del motore pulito, carry Paper operativo | — |
| 2026-08-09 | **ROTAZIONE MASTER KEY ESEGUITA**: backup DB (315 MB) → vecchia chiave nel keyring → nuova generata e incollata dal proprietario → «Ri-cifra ora» = **7/7 righe** (3 exchange + 4 AI, 0 indecifrabili) → ring svuotato → probe finale «tutte decifrabili» a ring vuoto, app su 5199 | — |
| 2026-08-09 | **PR #74 MERGED** in master (2.5+2.7+2.8; suite completa con Docker 2.448/2.448) | `ba8d0cf` |
| 2026-08-09 | **2.6** — `IncrementalFactorFilter` (ponte statico e puro fra la selezione IC e `IncrementalIcGate`): filtro greedy sui tenuti in ordine di \|IC\|, il capostipite si tiene, gli altri devono AGGIUNGERE (IC parziale + nullo per permutazione). In `/feature-selection` come checkbox opzionale (badge «ridondante» col verdetto nel tooltip) e in `FeatureEngineeringStage` come parametro `incrementalIcGate` (default false; applicato PRIMA del top-K, così i posti dei ridondanti vanno al prossimo indipendente). 6 test con edge piantato. Chiude **C-03/G-16** | — |
| 2026-08-09 | **2.9** — la deriva sorveglia anche i fattori del **Champion** per serie (`ChampionSpecsAsync`: union per FeatureName con la base di 8; Staging/Challenger esclusi; FactorsJson rotto o fattore scomparso = log e si prosegue, fail-open regola 4). Non i 158: solo quelli che un modello in carica dichiara. 3 test su Postgres. Chiude **G-04** | — |
| 2026-08-09 | **PR #75 MERGED** (2.6+2.9; suite 2.457/2.457) — FASE 2 CHIUSA | `04c4829` |
| 2026-08-09 | **FASE 3 COMPLETA — mandato «tutto amministrabile da UI»**: (3.A) le 9 chiavi ex-DeliberatelyNotExposed esposte — card **Topologia** in /admin/autonomy (`Trading:UseRemoteTrading`, `Ml:RemoteUrl`, `Http:DisableHttpsRedirection`, `Database`, `FactorCache`, tutte ⟳ col pericolo scritto accanto), topologia ingestion nel pannello Sync (bottone che resta attivo a remoto acceso), `MarketRegime:Model`+`JumpLambda` nel pannello regime con nota C1, attestazione `Trading:Bitget:SpotMarketBuyVerified` in /admin/protections (sezione aggiunta alle Writable del canale motore); `IAppConfigWriter.SaveValueAsync` per gli scalari (scrittura chirurgica, 2 test). (3.B) esito B3 accanto a `DriveProtectiveExits` e campagna 2026-07-25 accanto a `DriveDecisions`; colonna «N gate» in /discovery + nota sui due conteggi; riepilogo stage holdout con combinazioni provate vs N effettivo del gate (divergenza dichiarata, `DsrNominalTrials`/`DsrEffectiveTrials` nel contesto); warning sovrapposizione holdout in /feature-selection (Q6); copertura sentiment in DataAvailability (C-06) accesa su feature-selection/ml/alpha-mining. (3.C) `appsettings.json.example` con `Database` e `PostMortem` (E-05); guardiano: DeliberatelyNotExposed **svuotata**, 9 voci migrate in ExposedBy | — |

| 2026-08-10 | **PR #77 MERGED** — FASE 3 CHIUSA; verifica browser livello 4 eseguita sull'app di master (salvataggio FactorCache esercitato; filtro incrementale dal vivo: 10 fattori → 1 tenuto, 9 ridondanti su BTC/USDT 1h; zero errori server). Residuo E-04 trovato e risolto in sessione parallela (SymbolCatalog nei page service + `SymbolScanGuardTests`) | `c8630c4` |
| 2026-08-10 | **FASE 4 COMPLETA — il cluster non dipende più da GitHub**: `scripts/build-images-local.ps1` (6 target, doppio tag `procionemgr/<t>:local` + `ghcr.io/…:local-<sha>`, import via `docker save \| docker exec -i ctr images import` col pipe da cmd.exe — quello di PowerShell corrompe i tar — e VERIFICA crictl obbligatoria); `imagePullPolicy: Never` sui 6 workload; kustomization pinnati a `local-cde4dda1` (risolta anche la divergenza del 2026-08-07: il digest CI pre-PR#70 che un apply avrebbe ripristinato). Rollout eseguito: trading/ingestion/ml sui build locali — il motore ora ha TUTTE le correzioni Fasi 1-3 (attestazione Bitget compresa, verificata da /admin/protections). Prova del nove: pod cancellato → ricreato con evento «already present on machine», zero pull. Trappola nuova: il controller-manager del kind perde la leader election sotto carico (540+ restart) e i rollout restano in coda minuti — chip creato per allungare i lease | `cde4dda1`+`b7ec271` |
| 2026-08-10 | **2.1 completata davvero** — la verifica browser ha ritrovato la scansione da ~5 s aprendo /feature-selection e /backtest: il censimento «7 pagine» non contava i **page service**. Convertite a `ISymbolCatalog` le 4 copie sfuggite (`FeatureSelection.razor`, `BacktestPageService`, `OptimizationPageService`, `MlLabService`); guardiano `SymbolScanGuardTests` (scansione sorgenti stile `ConfigurationUiCoverageTests`, allowlist con ragioni: solo il catalogo stesso e `Discovery.razor`, che chiede le coppie simbolo+timeframe) | — |
| 2026-08-10 | **2.1, ultimo tassello** — il catalogo esteso alle **coppie**: `GetKnownSeriesAsync` (serie con dati ∪ tracciate, MAI il cartesiano simboli × timeframe; stesso snapshot in cache TTL 5 min, stessa `Invalidate()`). `Discovery.razor` convertita e tolta dall'allowlist del guardiano, che ora contiene solo il catalogo stesso | — |

| 2026-08-11 | **Riallineamento Config↔UI su master verificato a browser** (app riavviata su `c06efea`, guardiani 14/14): D-01 su run reale («Gate DSR su N=12.263 tentativi effettivi, 18.394 combinazioni provate, 144 candidati osservati») · badge «funding **serie storica** (94 eventi, firmati)» su backtest a leva — FundingHistory è popolata e usata · DISTINCT dei simboli una volta sola (catalogo) · salvataggio Model/λ esercitato. Zero errori | — |
| 2026-08-11 | **FASE 5 COMPLETA — assetto Docker Compose**: `docker compose up -d` su macchina con solo Docker = Postgres 18 (volume, healthcheck, MAI pubblicato: isola per costruzione) + guscio su 5199 con **migrate-on-startup autosufficiente** + motore opzionale (`--profile engine`); tutti `restart: always`; segreti solo in `.env` gitignored + `.env.example`; Data Protection e appsettings-dei-pannelli su volumi (symlink, versione a un container dell'init-config K8s); `bringup.ps1`/`watchdog.ps1` riconoscono l'assetto dai label compose. **BUG GRAVE TROVATO E CHIUSO dal primo `up` su DB vergine**: il migrate-on-startup era rotto in silenzio OVUNQUE (host incluso) — Design 10.0.9 nel progetto migrazioni vs EF 10.0.8 dell'app ⇒ ogni classe Migration falliva il load, EF dichiarava «zero migrazioni» ⇒ «già allineato» su DB vuoto. Tre difese: versione allineata, guardia in DatabaseMigrator (DLL presente + zero migrazioni = ERRORE, mai «allineato»), `MigrationsEfVersionAlignmentTests`. Verificato: 20 migrazioni applicate da sole, riavvio pulito, welcome page a browser | `34c9a04`+`32014d3` |

**Restano:** `git filter-repo` (DIFFERITO per decisione: coi tre segreti ruotati la storia è
innocua; si farà se mai il repo dovesse diventare pubblico) · **Fase 6** (verifica finale +
rimisura carry col funding storico) ·
Job one-shot `strategyhunter-discover`: campo immutabile, il template nuovo si applica alla
prossima ricreazione (attrito strutturale già documentato nel kustomization dei job) ·
il bin dell'HOST ha ancora la DLL migrazioni stantia (10.0.9): si riallinea col primo build
della soluzione dopo il merge, e da lì vigila la guardia nuova.

---

## Princìpi di questa ondata

1. **Nessuna fase tocca il confine verso Live.** Le sette regole di `CLAUDE.md` restano invarianti;
   dove un default vi contraddice (C-02), si allinea il default alla regola, mai il contrario.
2. **Ogni fix di misura dichiara le conseguenze prima di partire.** D-01 abbasserà i DSR storici;
   E-01 può cambiare i numeri del carry. Sono risultati attesi, non regressioni.
3. **Una politica, una sola implementazione** — il principio già in uso (NullTwinJudge) guida le
   sincronizzazioni della Fase 2.
4. **Ogni fase chiude con l'aggiornamento di NotebookLM**: si rigenerano i documenti meccanici
   toccati (21-28), si caricano le versioni nuove e si rimuovono le superate. La memoria si
   mantiene, altrimenti invecchia.
5. **Verifica secondo `docs/STANDARD-VERIFICA.md`** (4 livelli) per ogni fase che tocca codice.

---

## Fase 0 — Segreti e igiene del repository 🔴 BLOCCANTE

**Obiettivo.** Chiudere C-01 con la procedura corretta (D-06: la rotazione non è implementata — le
credenziali vanno reinserite a mano).

**Interventi.**
1. `git rm --cached ProcioneMGR/appsettings.json.pre-audit-test-20260729-141448`; pattern
   `.gitignore` per la **famiglia** (`ProcioneMGR/appsettings.json.*` con eccezione `.example`).
2. Rotazione guidata (procedura D-06, `docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md` §5-bis):
   backup DB → annotare le API key fuori → **corsie Testnet/Live ferme** → nuova
   `PROCIONE_MGR_MASTER_KEY` (stessa copia in guscio e motore) → riavvio → reinserimento in
   `/settings/exchanges` guidato dal badge → rotazione indipendente di `Trading:GrpcSharedSecret` e
   password Postgres.
3. **Supporto multi-chiave** in `AesGcmEncryptionService` (l'unico TODO reale del codice): decifra
   con la vecchia, cifra con la nuova, usando il byte di versione già riservato + comando di
   ri-cifratura di massa. Così la **prossima** rotazione è ordinaria.
4. Valutare `git filter-repo` sulla storia (facoltativo dopo la rotazione: la rotazione rende la
   storia innocua).

**Acceptance criteria.**
- [ ] `git ls-files | grep -E "appsettings\.json\."` → solo `.example`
- [ ] controllo automatico in CI: nessun segreto valorizzato nei file tracciati
- [ ] credenziali exchange decifrabili con la SOLA chiave nuova; `MasterKeyProbe` pulito all'avvio
- [ ] una seconda rotazione di prova (chiave B→C) riesce **senza** reinserimento manuale (multi-chiave)

**Test minimi.** `MasterKeyRotationTests` (nuovo): round-trip v1→v2, riga vecchia leggibile durante
la transizione, riga illeggibile → `IsReadable=false` senza plaintext parziale.
**Rischi.** Corsia viva durante la rotazione = ordini falliti → la procedura impone corsie ferme.
**NotebookLM.** Aggiornare 20 (D-06 chiuso) e 26; rimuovere le versioni superate.

---

## Fase 1 — Fix dei bug di merito 🔴

**Obiettivo.** Chiudere i difetti misurabili trovati dall'audit: D-01, D-02, C-02, E-01, D-03, D-04, G-08.

**Interventi.**

| # | Fix | Dove |
|---|---|---|
| 1.1 | **D-01** — `ctx.TrialsExplored` nel `PipelineContext`, sommato da Discovery/Creative/AlphaMining/griglia ML; `OverfittingGate.Apply` usa `max(validated.Count, TrialsExplored)` come N nominale prima di `EffectiveTrials`; log esplicito quando i due divergono | `PipelineModels`, `ModelStages`, `StrategyDiscoveryStage` |
| 1.2 | **D-02** — unità omogenee nel check di esposizione: `UsedCapital` Futures = Σ nozionale (`Quantity × EntryPrice`), come lo Spot; correggere il commento «non un bug» | `TradingEngine.BuildSafetyStatus` |
| 1.3 | **C-02** — `DriveProtectiveExits` default `false` in classe e in example, con citazione del report B3 | `RealtimeMarketDataModels.cs:117`, example |
| 1.4 | **E-01** — iniettare `IFundingHistoryProvider` in `BacktestPageService` e negli stage futures; popolare `FundingHistory` quando la serie copre il periodo; fallback dichiarato alla costante | `BacktestPageService`, `ModelStages`, `DecisionStages` |
| 1.5 | **D-03** — validazione dei range (holdout dopo selezione, mai sovrapposti) all'avvio del run in `PipelineEngine`, non solo al salvataggio UI | `PipelineEngine` |
| 1.6 | **D-04** — stoppino asimmetrico nel gemello nullo: ripartizione sopra/sotto campionata dalla stessa barra sorgente | `NullTwinGenerator` |
| 1.7 | **G-08** — filtrare il `seed` nei 3 punti cablati (`MlpReturnPredictor:234`, `RegressionPredictorBase:109`, `StackedReturnPredictor:382`); `RunSeed` in `PipelineContext` + `ExperimentRun` | `Services/ML`, `Experiments` |

**Conseguenze dichiarate.** 1.1 abbassa i DSR storici e può far cadere candidati «sopravvissuti» —
risultato corretto. 1.4 cambia il PnL dei backtest futures su periodi con funding
negativo — **rimisurare il carry** e confrontare col numero storico è parte del lavoro, non un extra.

**Acceptance criteria.**
- [ ] run con 1.000 combinazioni provate e 15 tenute ⇒ SR\* **più alto** di un run con 15 provate
- [ ] N posizioni Futures a leva L fanno scattare `MaxTotalExposurePercent` alla stessa esposizione
      nozionale di N Spot equivalenti
- [ ] `new RealtimeFeedOptions().DriveProtectiveExits == false`
- [ ] backtest futures con `FundingHistory` popolata ≠ backtest con la sola costante, su periodo con
      funding negativo; UI dichiara quale modello è in uso
- [ ] config con range sovrapposti ⇒ il run **non parte**, con messaggio chiaro
- [ ] due run con lo stesso `RunSeed` ⇒ identici; con seed diversi ⇒ varianza non nulla su **tutti**
      i predittori stocastici

**Test minimi.** `TrialsCountPropagationTests`, `SafetyCheckerFuturesExposureTests`,
`SecurityDefaultsTests` (asserisce i default delle sette regole), `FundingHistoryWiringTests`,
`PipelineRangeValidationTests`, `NullTwinWickAsymmetryTests`, `MlDeterminismTests` esteso.
**Rischi.** 1.2 rende il limite più severo a parità di config: le corsie con `MaxOpenPositions`
alzato potrebbero rifiutare ordini che prima passavano — è il comportamento **dichiarato** del
limite. 1.4: cambio dei numeri storici, da comunicare.
**NotebookLM.** Rigenerare e ricaricare 20, 23, 24, 26, 27.

---

## Fase 2 — Sincronizzare le parti dissociate 🟠

**Obiettivo.** Eliminare o unificare ciò che è doppio, morto o dichiarato-ma-non-fatto.
Include le due decisioni di prodotto (Microstructure, JumpModel).

**Interventi.**

| # | Intervento | Chiude |
|---|---|---|
| 2.1 | **`ISymbolCatalog`**: un servizio unico per l'elenco simboli (politica esplicita: `TrackedSeries` + opzione «includi storici»); le 7 pagine lo consumano al posto delle scansioni su `OhlcvData` | E-04 |
| 2.2 | Rimuovere la registrazione Singleton di `BayesianSearch` (o factory `Func<int, BayesianSearch>` che dichiari il seed per-run) | E-03 |
| 2.3 | Rimuovere `ICombinatorialPurgedCv` (interfaccia mai risolta) | G-07 |
| ~~2.4~~ | ~~Etichetta LightGBM~~ — **RITIRATA**: verificato `Trainers.LightGbm(...)` + pacchetto `Microsoft.ML.LightGbm`, è vero LightGBM; l'errore era dell'audit (corretto nei doc 04/06) | ~~G-12~~ |
| 2.5 | `events.proto`: rimuovere o marcare `// riservato, nessun consumatore` nel file | G-14 |
| 2.6 | **Microstructure — integrazione** (decisione 2026-08-08): registrazione DI del modulo; `IncrementalIcGate` esposto in `/feature-selection` come filtro opzionale sui candidati e in `FeatureEngineeringStage` come gate configurabile (default off = comportamento invariato) | C-03, G-16 |
| 2.7 | **JumpModel — cablaggio dietro flag** (decisione 2026-08-08): `IRegimeModel` con `KMeansRegimeModel` (default, comportamento invariato) e `JumpRegimeModel`; selezione da `MarketRegime:Model`; `/regimes` mostra quale modello è attivo e il confronto transizioni | C-04 |
| 2.8 | **C-05** — `EnsembleAssemblyStage` dipende da `IPortfolioOptimizer` risolto per nome (`Pipeline:PortfolioOptimizer`, default `HRP` = pesi identici a oggi); scelta esposta in `/pipeline`; in `/portfolio` bottone «usa questa allocazione nel prossimo run» (scrive il parametro — **mai** un percorso diretto verso l'esecuzione) | C-05 |
| 2.9 | Deriva sorvegliata sui fattori Alpha158 **selezionati** da un modello attivo (non tutti i 158) | G-04 |

**Acceptance criteria.**
- [x] zero query `Distinct()` su `OhlcvData` nelle pagine; i menu mostrano gli stessi simboli di prima
- [x] `IncrementalIcGate` scarta almeno un fattore ridondante su un caso costruito
      (`IncrementalFactorFilterTests`: echo piantato scartato, indipendente tenuto); con gate off il
      run è identico a prima (test sul default)
- [x] `JumpRegimeModel` produce meno transizioni di `KMeansRegimeModel` sulla stessa serie; con
      `MarketRegime:Model` assente il comportamento è **byte-identico** a oggi
- [x] con `Pipeline:PortfolioOptimizer=HRP` i pesi dell'ensemble sono identici a oggi; cambiarlo
      cambia davvero i pesi proposti
- [x] la deriva di un fattore Alpha158 in uso è sorvegliata dal job (stesso percorso di alert dei
      fattori base: snapshot → Home); Staging esclusi, FactorsJson rotto non rompe il giro

**Test minimi.** `SymbolCatalogTests`, `IncrementalIcGateIntegrationTests`,
`RegimeModelSelectionTests`, `PortfolioOptimizerSelectionTests`,
`EnsembleAssemblyWeightsRegressionTests` (HRP invariato), `Alpha158DriftTests`.
**Rischi.** 2.8 cambia i pesi delle corsie se si cambia allocatore: applicare solo a corsie ferme.
2.1: la politica dei simboli va decisa una volta e dichiarata (storici inclusi o no).
**NotebookLM.** Rigenerare 21, 23-26, 28 (il grafo cambia: spariscono le registrazioni morte).

---

## Fase 3 — Configurazioni ↔ UI 🟡

**Obiettivo.** Ogni configurazione ha la UI giusta, ogni UI dice la verità sulla misura che ha
motivato il default.

**Interventi.**
1. **E-05** — `appsettings.json.example` completo: sezioni `Database` e `PostMortem` documentate.
2. `/admin/protections`: accanto a `DriveProtectiveExits` l'esito B3 («misurato 2026-07-28: peggiora
   in 24/24 configurazioni»); stesso pattern per gli altri interruttori nati da una misura
   (`DriveDecisions`, vol targeting).
3. `/pipeline` e `/discovery`: mostrare **combinazioni provate** e **N usato dal gate DSR**; se
   divergono, dirlo (completa D-01 lato UI).
4. `/feature-selection`: avviso quando l'intervallo esplorato si sovrappone all'holdout dell'ultima
   configurazione di pipeline (rischio umano residuo di Q6).
5. `/backtest`: indicatore «funding: serie storica / costante» (completa E-01 lato UI).
6. **C-06** — `DataAvailability` dichiara la copertura temporale del sentiment; solo dopo, decisione
   su `EnableMlFeature` misurando l'IC incrementale col gate 2.6.
7. Ricognizione con `ConfigurationUiCoverageTests` (esiste già): ogni opzione modificabile a caldo ha
   un pannello, ogni pannello scrive un'opzione che qualcuno legge.

**Acceptance criteria.**
- [x] partendo dal solo `.example` non esiste sezione letta dal codice che manchi
      (aggiunte `Database` e `PostMortem`; MarketRegime Model/λ e FactorCache c'erano già)
- [ ] ogni interruttore di sicurezza mostra il perché del suo default — codice fatto, la
      **verifica browser (livello 4) si fa dopo il merge** sull'app di master (regola worktree)
- [x] `ConfigurationUiCoverageTests` verde con le nuove voci (DeliberatelyNotExposed svuotata)

**Rischi.** Bassi.
**NotebookLM.** Rigenerare 22, 25, 26.

---

## Fase 4 — Indipendenza da GitHub 🟠

**Obiettivo.** Il server non deve dipendere da GitHub per **girare**. Oggi l'unica dipendenza runtime
è il pull delle immagini da `ghcr.io/markcapitanelli/*` nei manifesti K8s (repo già privato,
verificato 2026-08-06; CI resta facoltativa).

**Interventi.**
1. Script `scripts/build-images-local.ps1`: build locale delle 4 immagini + import nel nodo kind via
   `docker save | docker exec … ctr -n k8s.io images import -` (la via già validata — `kind` CLI non
   è installato e Git Bash converte i percorsi), con **verifica** `crictl images`.
2. Manifesti: `imagePullPolicy: Never` (o `IfNotPresent`) sulle immagini proprie, così il cluster
   non tenta mai il pull da ghcr.
3. Tag locali `procionemgr/*:local` accanto ai nomi ghcr, per non rompere chi usa ancora la CI.
4. Prova del nove: **rete staccata**, riavvio dei pod, tutto riparte dalle immagini locali.

**Acceptance criteria.**
- [x] riavvio pod senza alcun pull: `imagePullPolicy: Never` lo vieta per CONTRATTO al kubelet
      (più forte della prova a cavo staccato: il pull non viene nemmeno tentato) e la prova
      empirica c'è — pod ml cancellato e ricreato, evento kubelet «already present on machine»
- [x] `crictl images` elenca le 6 immagini locali (verifica automatica nello script, che fallisce
      se una manca)
- [x] la CI resta possibile: i kustomization tengono la storia dei pin CI nei commenti, e un
      overlay con newTag a uno sha di CI + IfNotPresent riporta al vecchio flusso

**Rischi.** Immagini stantie se ci si dimentica di rifare la build dopo una modifica: lo script
stampa data e commit dell'immagine importata, e `bringup.ps1` la mostra.
**NotebookLM.** Aggiornare 22 e 25 (nuovo script documentato).

---

## Fase 5 — Bring-up universale: Docker Compose 🟠

**Obiettivo.** Su **qualunque dispositivo** con Docker: `docker compose up -d` e riparte tutto —
Postgres, guscio (UI su 5199), motore — e **risopravvive ai riavvii** con `restart: always`.
(Decisione 2026-08-08: Compose portabile, non solo Windows robusto.)

**Interventi.**
1. `docker-compose.yml` alla radice: `postgres` (volume dati, healthcheck `pg_isready`) · `ui` (il
   guscio, porta 5199, dipende da postgres healthy) · `trading` (opzionale via profilo compose:
   `--profile engine`, con `Trading:UseRemoteTrading=true` sul guscio quando attivo) — tutti
   `restart: always`.
2. Segreti via `.env` **gitignored** (`PROCIONE_MGR_MASTER_KEY`, password Postgres, segreto gRPC) +
   `.env.example` committato coi placeholder. Stessa disciplina dei Secret K8s.
3. Il migrate-on-startup (2026-08-05) rende il primo avvio autosufficiente: nessun passo manuale di
   schema. Verificare che la DLL delle migrazioni sia copiata **e risolvibile** nell'immagine (la
   trappola già nota del deps.json).
4. Aggiornare `bringup.ps1`/`watchdog.ps1` perché riconoscano l'assetto compose (guscio su 5199 via
   container invece che `dotnet run`): il watchdog controlla gli stessi tre endpoint, indifferente a
   chi li serve.
5. Documentare i **due assetti supportati** in `README`: (a) Windows+kind (attuale, con
   `bringup.ps1 -Register` al logon), (b) Compose (portabile). Un solo scrittore per corsia resta
   garantito: mai i due assetti insieme sullo stesso DB — il lease Postgres per corsia lo impone
   comunque per costruzione.

**Acceptance criteria.**
- [x] macchina pulita con solo Docker: `docker compose up -d` → UI servita, **20 migrazioni
      applicate da sole** al primo avvio su DB vergine, pagina di benvenuto con Login/Registrati
      (verificato a browser su porta di collaudo 5299)
- [x] riavvio → riparte da solo: `restart: always` su tutti i servizi + prova col restart del
      container («già allineato (20 note)»)
- [x] `dotnet test` verde (suite completa a chiusura); smoke browser eseguito
- [x] i due assetti non si pestano per costruzione: il Postgres del compose NON è pubblicato
      sull'host (isola), `bringup.ps1` esce se il compose è attivo, `watchdog.ps1` riconosce
      l'assetto dai label; il lease per corsia resta il guardrail di ultima istanza

**Rischi.** Doppio scrittore se qualcuno avvia compose col cluster kind attivo sullo stesso DB → il
lease per corsia è il guardrail, e il README lo dichiara a caratteri grandi.
**NotebookLM.** Aggiornare 22, 25 e il master index.

---

## Fase 6 — Verifica finale e chiusura 🟢

1. Suite completa (`dotnet test`, Docker attivo per Testcontainers).
2. Verifica a 4 livelli per le fasi 1-2 (unità → controllo sul rumore → integrazione → browser).
3. **Rimisura del carry col funding reale** (da 1.4) e confronto documentato col numero storico.
4. Rigenerazione integrale dei documenti meccanici 21-28 + diff contro le versioni pre-ondata: il
   diff È il changelog dell'ondata.
5. Caricamento finale su NotebookLM, rimozione delle fonti superate, aggiornamento di
   `docs/ROADMAP.md` (la roadmap viva) con l'esito.

---

## Ordine di esecuzione e dipendenze

```
Fase 0 (segreti)  ──►  Fase 1 (bug)  ──►  Fase 2 (sincronizzazione)  ──►  Fase 3 (config↔UI)
                                                                              │
Fase 4 (no-GitHub) ── indipendente, in parallelo da subito ──►  Fase 5 (compose)  ──►  Fase 6
```

Fase 0 blocca tutto (non si costruisce su segreti compromessi). Le fasi 4-5 non toccano la logica e
possono procedere in parallelo alle 1-3. La 6 chiude.

## Cosa questa ondata NON fa (per decisione)

- Non tocca `RegimeRouting:DriveDecisions=false` né il GARCH fuori dal sizing (misure, regola 7).
- Non accende `Drift:Enabled` (resta la Fase 7 del blueprint: prima in sola segnalazione).
- Non rifattorizza `tools/PlatformExpand` (G-09 rimandato: costo alto, nessun rischio operativo).
- Non introduce alcun percorso automatico verso Live. Mai.
