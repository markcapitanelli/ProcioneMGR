# PRD — Autonomia Finanziaria (dodicesima roadmap, 2026-08-02)

*Nato dal sesto PDF esterno («Progettare l'Autonomia Finanziaria: un framework per ProcioneMGR
basato su agenti intelligenti e cicli decisionali chiusi») e dalla richiesta del proprietario:
sfruttare i cinque provider AI per rendere la piattaforma quasi completamente autonoma, H24/7,
su più corsie delle 3 storiche, fino alla promozione finale e anche dopo (portafoglio, SL/TP).*

## 0. Il confronto col già-costruito (metodo dei PDF esterni)

Come per i cinque PDF precedenti: prima il confronto, poi solo il genuinamente nuovo.

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| Model router multi-obiettivo fra provider | `DelegatingLlmClient` (failover) + `ModelAutoSelector` + `ILlmClientResolver`, Fasi D/D.1 | già fatto |
| Agente di Gestione del Rischi | `SafetyChecker` (10 check), `FillSanityCheck`, `LaneInvariantWatchdog`, `CorrelatedExposureGuard`, auto SL/TP da escursioni | già fatto, e resta DETERMINISTICO |
| Auto-osservazione / ciclo chiuso | advisory worker + auto-reapply (`RunApplyEvaluator`) + trigger di regime (`RegimeChangeTriggerWorker`) | già fatto in larga parte |
| Esponente di Hurst come selettore di regime/asset | **zero occorrenze nel repo**; il direzionale-tecnico ha 10 conferme negative control-validate; il gate HMM è già fallito due volte | NON come pilastro; al più misura opzionale in fondo alla coda, con aspettativa dichiarata di esito negativo |
| Orchestratore centrale multi-agente | **non esiste** un gestore della flotta di corsie | il buco vero → AF2 |
| Budget e controllo costi AI (cost runaway) | **nessun tracking** token/costi: i client scartano `usage` | il secondo buco vero → AF1 |
| Kafka, GKE, Bigtable, agenti containerizzati separati | due host + Postgres bastano alla scala reale (~decine di chiamate LLM/ora) | non pertinente |

## 1. Decisioni del proprietario (2026-08-02, vincolanti)

1. **Gate Live = ultimo click umano.** Autonomia piena fino a Testnet compreso e DOPO il Live
   (retrocessione automatica Live→Testnet inclusa, in sola direzione di sicurezza); Testnet→Live
   resta un click su /trading. I 5 failsafe anti-Live restano intatti.
2. **La «Queen Bee» è codice deterministico**, non un LLM né l'ONNX (che è una regressione lineare
   di sentiment): servizio .NET con regole esplicite, fuzzabile, journal persistito. Le AI esterne
   sono un comitato consultivo.
3. **Potere AI = scelta vincolata a menù**: le AI scelgono solo fra opzioni pre-validate dal
   codice; risposta invalida o assente → default deterministico; più il veto già esistente. Solo su
   decisioni reversibili Paper/Testnet.
4. **H24/7 = PC attuale + watchdog** (autostart, bring-up idempotente, dead-man's-switch); la
   migrazione VPS resta fuori da questa roadmap.
5. **Più corsie**: flotta a 8 (tetto codice 12), le nuove dormienti finché qualcuno non le avvia.

## 2. Invarianti (valgono per ogni fase)

- **Default-off**: a config vuota il comportamento è bit-identico a prima della fase.
- **AI mai in DI con servizi di esecuzione** (PRD-AI-MULTIPROVIDER §1): il comitato riceve DTO.
- **4 livelli di verifica** (STANDARD-VERIFICA) con il livello 4 esplicito per fase.
- **Il forward test Paper resta l'unico giudice**: l'autonomia lo industrializza (più corsie = più
  candidati giudicati in parallelo), non lo aggira. La fascia grigia F5 resta a click umano.
- Incrementi piccoli mergiabili; ogni PR lascia la piattaforma funzionante.

## 3. Fasi

### AF0 — Flotta fisica: da 3 a 8 corsie (dormienti) — S

`Trading:LaneCount=8` nei due host (il valore si congela alla prima lettura: il doppio riavvio
coordinato si paga UNA volta ora). Corsie 3..7 dormienti: registrate, mai avviate, zero scritture.
`LaneCountCoherenceProbe` nel monolite remoto: all'avvio confronta il proprio conteggio con quello
che il motore dichiara via `IEngineConfigStore` (sezione read-only `Trading:LaneCount`) —
disallineamento = LogCritical + notifica; irraggiungibilità o valore illeggibile = ignoranza
dichiarata, MAI un allarme costruito su un default. L'impronta dell'auto-apply della pipeline
resta 3 (`PipelineApplier.AutoApplyLaneFootprint`): la flotta cresce, ciò che una ri-applica
schedulata sovrascrive NO — oltre l'impronta schiera solo l'orchestratore (AF2), con corsie
esplicite.

Verifica: L1 sonda (coerente/disallineata/ignoranza) + impronta applier; L2 corsie dormienti =
zero ordini e zero righe dopo 24h di esercizio; L3 `PromotionEvaluator` su flotta 8 con 5 corsie
mai avviate → nessuna eccezione, nessuna azione; L4 /trading e /ensemble nel browser con 8 corsie,
lease attivi sulle prime 3, sonda muta a configurazioni combacianti.

### AF1 — Sostenibilità: tracking token/costi + budget con breaker di spesa — M

`LlmUsageEvent {Provider, Model, Path, PromptTokens, CompletionTokens}` riportato dai client
(che oggi scartano `usage`) a un `ILlmUsageSink`; il `path` fluisce via AsyncLocal dal guard (la
firma di `ILlmClient` non cambia). Col failover il provider dichiarato è QUELLO CHE HA SERVITO.
`LlmBudgetOptions` (`Llm:Budget`, tutto 0 = spento): tetto chiamate/giorno e token/giorno/mese;
superato → il guard risponde `SkippedBudgetExhausted` SENZA chiamare (il breaker degli errori non
si muove; `forceProbe` non bypassa il budget: è un tetto, non un guasto), notifica Warning una per
transizione. Persistenza aggregata per `(DayUtc, Provider, Model, Path)` (`LlmUsageRecords`).
Pannello «Consumo e budget» in /admin/ai-supervisor.

### AF2 — Queen Bee: orchestratore di flotta deterministico — L

Nuovo `Services/Fleet/` nel MONOLITE (il cervello sta già lì; «si aggiunge SOPRA il planner, non
DENTRO»). Tre pezzi: `FleetOrchestrator.Decide(FleetState, FleetOptions) → FleetPlan` **puro e
fuzzabile** (azioni: `AssignCandidateToLane` / `StopAndFreeLane` / `ProposeGreyCandidate` /
`NoOp(reason)`); `FleetStateReader` in sola lettura (engine keyed, quarantene, coda candidati dai
run — un candidato senza trade/mese e durata mediana dichiarati NON entra in coda); worker con
tick 15′, `Fleet:Enabled=false` e `Fleet:DryRun=true` di default.

Regole iniziali: candidato DSR-pass più vecchio → corsia Paper libera con id più basso; ritiro a
`RealizedSharpe<0` dopo 3 settimane e ≥20 trade con isteresi di 2 tick; MAI toccare Live, Testnet
(restano del `PromotionWorker`), quarantene, corsie di campagne in Observing; fascia grigia F5 →
solo proposta a click umano; carry sorvegliato (worker muto >24h → notifica), non gestito.

Lo schieramento passa SEMPRE da `RunApplyEvaluator` (stesso `_applyGate`, stesso veto AI):
`ApplyRecommendationAsync` guadagna `targetLanes` opzionale, `null` = comportamento storico
(test di regressione bit-identico). Journal persistito (`OrchestratorDecisions`: opzioni, scelta,
fonte rules/committee/default, voti, esito) + sezione in /admin/autonomy.

Incrementi: (1) entità+reader+core+fuzz 20k; (2) DryRun dal vivo ~1 settimana; (3) esecuzione;
(4) `targetLanes` in PR separata.

#### AF2c — Criteri emersi dalla prima revisione manuale delle corsie (2026-08-05)

*Nati facendo a mano ciò che l'orchestratore dovrà fare da solo. Ognuno è una regola che oggi
manca, e il primo è un buco che rende la regola di ritiro inefficace proprio sul caso più comune.*

**1. 🔴 La corsia che non opera non viene MAI ritirata.** La regola scritta sopra è «ritiro a
`RealizedSharpe<0` dopo 3 settimane **e ≥20 trade**». Una corsia che produce **zero** trade non
raggiunge mai i 20, quindi non è mai candidata al ritiro: resta occupata per sempre senza produrre
informazione. Successo davvero — la corsia 0 (AAVE 1d) è rimasta 9,3 giorni con **zero trade sul
proprio simbolo**, e nessuna regola l'avrebbe mai toccata. È la stessa famiglia dei «controlli che
rassicurano» del Filone E: la regola sembra completa e non può scattare sul guasto più frequente.
**Serve un secondo criterio, per inedia**: se dopo N settimane i trade sono *sotto una frazione*
di quelli attesi dall'holdout, la corsia va proposta per il ritiro — con la stessa isteresi.

**2. Il conteggio va fatto sul SIMBOLO ATTUALE, non sulla corsia.** Le corsie hanno vite
precedenti su altri simboli: la corsia 0 mostrava 159 trade storici, tutti su NEAR/ATOM/BTC, e
zero su AAVE. Un criterio che guarda `TradeRecords` per `LaneId` legge la storia di qualcun altro.
La domanda giusta è: *questa corsia ha mai operato sul simbolo che ha ORA?*

**3. Il timeframe 1d non può produrre un verdetto in tempo utile, e va detto prima di schierarlo.**
A ~2 trade/mese servono **dieci mesi** per arrivare ai 20 trade minimi. Sommato all'aritmetica del
MinTRL (F4: Sharpe 1,0 ⇒ 6,2 anni), una corsia 1d è epistemicamente ferma: occupa uno slot che un
4h userebbe per accumulare evidenza. **Non è un divieto** — un test lungo può avere senso — ma
l'orizzonte va dichiarato al momento dello schieramento, non scoperto dopo nove giorni.

**4. Diversificare i simboli fra le corsie.** Al momento della revisione tre corsie su sei erano su
DOT/USDT. Con `CorrelatedExposureGuard` acceso al 30% è un rischio governato, ma resta evidenza
ridondante: sei test sullo stesso sottostante misurano più o meno la stessa cosa. A parità
ragionevole di Sharpe holdout, preferire un simbolo non ancora schierato — è il criterio con cui
oggi ho scelto ETC e STX scartando XRP e DOT, che pure avevano Sharpe più alto.

**5. I grigi riproposti all'infinito sono rumore.** Il journal mostra lo stesso candidato
(GridMeanReversion XRP/USDT 4h) proposto **sette volte in due giorni**, mentre era già schierato
sulla corsia 4. Serve il dedup per `PipelineCandidateKey` già annotato come task #12: la
ricorrenza di un candidato è un SEGNALE (lo trova run dopo run), ma va contata, non ripetuta come
notifica.

**6. Il pannello dei grigi offre solo i run che la flotta ha proposto.** Il candidato migliore
misurato quel giorno — Supertrend ADA/USDT 4h, **Sharpe 3,19 su 17 trade**, dalla configurazione
coi gate più severi — **non era schierabile**, perché il suo run non compariva nell'elenco. Il
filtro è coerente col disegno (si schiera ciò che la flotta ha valutato), ma taglia fuori i
candidati migliori di run non proposti: da rivedere, o almeno da rendere visibile il perché.

**7. Le corsie 0-2 non sono assegnabili ai grigi, ed è giusto così.** Appartengono all'impronta
dell'auto-apply (`AutoApplyLaneFootprint=3`): ciò che una ri-applica schedulata sovrascriverebbe
non si assegna a mano. Va ricordato quando si libera una di quelle corsie — si libera *per la
pipeline*, non per la flotta.

#### Cosa è stato deciso il 2026-08-05, e perché

Registro delle azioni, così fra un mese si sa cosa è stato cambiato e su quale base.

| Corsia | Prima | Azione | Motivo |
|---|---|---|---|
| 0 | AAVE/USDT 1d, 9,3 giorni | **svuotata** | zero trade su AAVE *mai* (né nei 9 giorni né nei 30 di replay: ~39 barre giornaliere senza un segnale) e orizzonte 1d che non produce verdetto in tempo utile. Torna alla pipeline (impronta auto-apply) |
| 2 | XLM/USDT 1h | **svuotata** (il giorno prima) | ~936 barre orarie fra replay e vivo, zero segnali |
| 3 | vuota (orfana di luglio) | **RsiOversold ETC/USDT 4h** | Sharpe holdout 1,66 su 13 trade; simbolo nuovo. SL 3,70% / TP 9,78% |
| 7 | mai configurata | **BollingerMeanReversion STX/USDT 4h** | Sharpe holdout 1,44 su **18 trade** — il più vicino alla soglia dei 20, quindi il meno rumoroso; simbolo nuovo. SL 4,84% / TP 14,45% |
| 1, 4, 5, 6 | — | **lasciate** | 1 e 5 hanno operato; 4 ha una posizione aperta; 6 ha solo 2,2 giorni |

**Scartati e perché**: `GridMeanReversion XRP/USDT 4h` (Sharpe 2,10, il più alto disponibile) era
già schierato sulla corsia 4; `GridMeanReversion DOT/USDT 4h` (1,60) avrebbe portato DOT a quattro
corsie. In entrambi i casi ha vinto la diversificazione sul numero — che è il criterio 4 di sopra,
applicato per la prima volta.

**Da guardare al prossimo giro**: `Supertrend ADA/USDT 4h`, Sharpe **3,19 su 17 trade**, dalla
configurazione «Caccia onesta majors» (embargo + gate severi) — il migliore misurato, e non
schierabile dal pannello per il criterio 6.

### AF3 — Comitato AI a scelta vincolata — M

`Services/Llm/Committee/`: `IAiCommittee.AskAsync(CommitteeQuestion) → CommitteeVerdict`. I
provider configurati (default nvidia+groq+gemini, fra chi ha la chiave) VOTANO TUTTI in parallelo
via `ILlmClientResolver` (semantica opposta al failover). Contratto JSON severo
`{"choice","confidence","reason"}`: scelta fuori menù = astensione; maggioranza semplice fra voti
validi, parità o meno di `MinValidVotes` (2) → `DefaultOptionId`. Il chiamante RIVALIDA il
verdetto contro il menù (difesa anti prompt-injection dai dati di mercato). Guard keyed dedicato
con path `"committee"` e breaker proprio (un'ecatombe del comitato non sospende advisory/veto);
budget AF1 condiviso e verificato PRIMA di ogni voto. Innesto: solo sui PAREGGI di
`FleetOrchestrator.Decide` (`Fleet:UseCommittee=false` default). Proprietà chiave al livello 2:
provider che rispondono spazzatura al 100% ⇒ comportamento ≡ comitato spento.

### AF4 — Post-Live e portafoglio di corsie — M+

- **AF4a** Retrocessione automatica Live→Testnet, SOLA direzione di sicurezza. Test-first: il fuzz
  di `AuditPromotionStateMachineTests` si aggiorna PRIMA del codice (flag OFF ⇒ bit-identico; da
  Live è raggiungibile SOLO Testnet, mai Paper diretto, mai start, mai promozione; nessuna
  combinazione di opzioni corrotte produce altro). Opzioni default-off
  (`AutoDemoteLiveToTestnet=false` + soglie + `DemoteLiveDryRun=true` che logga «retrocederei»).
  `PromotionWorker` allarga la whitelist con Live→Testnet (notifica Warning); `LanePromoter` NON
  cambia (il throw è su `newMode==Live`, e il flatten reduce-only pre-cambio è la semantica
  giusta). L4 onesto: non si può simulare Live senza Live → dry-run in produzione + verdetto
  visibile nel pannello promozioni.
- **AF4b** Guardie di flotta: `Decide` rifiuta più di `Fleet:MaxLanesWithoutExposureGuard` (3)
  corsie attive se `CorrelatedExposureGuard` è spento (NoOp a journal + notifica — la flotta larga
  È protetta senza flippare default globali); vol targeting acceso via config sulle corsie Paper.
- **AF4c** Pesi di capitale FRA corsie, SOLO advisory: PnL giornalieri per corsia → Ledoit-Wolf →
  ERC (riuso di `Services/Portfolio`) → frazioni suggerite a journal/pannello/digest
  (`Fleet:CapitalWeights:Enabled=false`). L2: su PnL bianco i pesi restano ~equal-weight.
  L'applicazione automatica dei pesi NON è in questa roadmap.

### AF5 — Continuità H24 — M

1. **Heartbeat incrociato via Postgres** (ogni host scrive SOLO la propria riga di
   `HostHeartbeats`; il monitor legge quella ALTRUI): stale > 10′ → Warning una per transizione,
   Info al rientro. `Heartbeat:Enabled=false` default; worker in ENTRAMBI gli host.
2. **Watchdog esterno** `scripts/watchdog.ps1` (Task Scheduler, 5′): /health del motore, HTTP del
   guscio, porta Postgres; su guasto Telegram DIRETTO via Bot API — fuori dall'app, sopravvive a
   tutto tranne che al PC spento.
3. **Autostart + bring-up** `scripts/bringup.ps1` idempotente (socat kind-apiproxy → attesa nodo →
   port-forward → Postgres → monolite), scheduled task al boot. I prerequisiti una-tantum restano
   quelli degli script `k8s-*-secret.ps1` (mai inventare i nomi delle chiavi).
4. **Digest giornaliero Telegram** (assorbe il backlog del PRD Autonomia §8): stato corsie con
   trade/mese e durata mediana, promozioni, decisioni della flotta, consumo AI, carry, heartbeat.
   **L'assenza del digest all'ora attesa È il dead-man's-switch percepibile dall'umano** — scritto
   nel messaggio stesso. L4 della fase: riavvio VERO del PC, tutto risale senza mani.

## 4. Non-obiettivi

1. **Auto-Live, in nessuna forma** — il throw di `LanePromoter` e il fuzz lo dimostrano a ogni build.
2. **LLM che scrive codice libero o tocca un parametro operativo senza gate** — resta vietato.
   **Reconsiderato in parte il 2026-08-04** (vedi [PRD-BENCHMARK-BITGET-AI-2026-08](PRD-BENCHMARK-BITGET-AI-2026-08.md)
   §2bis, item G3): l'AI può PROPORRE candidati strutturati (whitelist di componenti esistenti, mai
   codice libero) che entrano nell'imbuto di discovery esistente e sono giudicati dagli STESSI gate
   (DSR/PBO/CPCV/NullTwin/forward Paper) di ogni altro candidato — zero scorciatoie, zero soglie
   diverse, zero corsia riservata. Il testo libero (spiegazioni, veto, post-mortem) resta comunque
   confinato a journal/notifiche, mai un parametro diretto.
3. **Hurst come pilastro** — al più item di misura opzionale, con aspettativa dichiarata di esito
   negativo dopo 10 no del direzionale-tecnico.
4. **Il PDF a scatola chiusa** — RL-esecuzione e ibridi LSTM restano respinti; Kafka/GKE non
   pertinenti alla scala.
5. **Validazione più permissiva** — le soglie DSR non si toccano; F5 resta a click umano.
6. **Terzo host / lock distribuiti nuovi** — l'orchestratore vive nel monolite e comanda tramite
   le vie esistenti.

## 5. Ordine

AF0 → AF1 → AF5(1-3) → AF2(journal/DryRun) → AF4a(dry-run) → AF2(esecuzione) → AF5(digest) →
AF4b → AF3 → AF4c. Stato per fase nella tabella della ROADMAP (unica fonte viva).
