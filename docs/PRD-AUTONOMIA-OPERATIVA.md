# PRD — Autonomia Operativa di ProcioneMGR

**Stato**: **TUTTE E 5 LE FASI COMPLETE** (2026-07-19, ordine 0 → 1 → 4 → 2 → 3 come
raccomandato in §9): A1 = PR #24, A2 = PR #26, A3 + Fasi 1/4/2/3 = questo branch. Dettagli e
scostamenti dal design nei blocchi **FATTO** di ogni sezione. Restano da esercitare dal vivo:
prova reale Telegram (serve il bot token) e kill-test del processo durante uno smoke run (C1).
**Creato**: 2026-07-19 · **Tipo**: documento vivo (aggiornare ad ogni fase completata, come il
PRD di consolidamento).

## Scopo di questo documento

Questo PRD nasce da una **sessione di esercizio autonomo reale** (2026-07-18/19, evidenze complete
in `docs/TEST-UI-2026-07-18.md`): un operatore automatico (Claude) ha guidato la piattaforma per
un'intera giornata — test completo della UI, 4 cacce pipeline complete (356 candidati valutati,
0 sopravvissuti all'holdout), piste di analisi mirate (pairs, GARCH, autocorrelazioni), gestione
di 2 bug reali trovati sul campo. La domanda a cui risponde: **che cosa manca perché la piattaforma
faccia da sola ciò che in quella sessione ha fatto l'operatore?**

Il metodo è lo stesso del PRD di consolidamento: ogni proposta è **verificata contro il codice**
(cosa esiste già, cosa manca davvero), non dedotta da principi generali. La tesi di fondo,
dimostrata sul campo: il "cervello di ricerca" (holdout, DSR, SafetyChecker, mai-Live) è maturo e
NON va toccato; il gap è interamente nel **contorno operativo** — fiducia negli input esterni,
politica di reazione agli esiti, trigger contestuali, resilienza ai riavvii, notifica.

## Legenda

- **Fase 0..4**: raggruppamento cronologico di questo PRD, ordinate per priorità (0 = abilitatore
  di sicurezza, senza cui l'autonomia è pericolosa; 4 = comfort operativo).
- Ogni item cita i file verificati e lo stato reale al 2026-07-19.

---

## §1 — Stato attuale: cosa esiste già (verificato)

L'autonomia NON parte da zero. Componenti già in produzione che questo PRD riusa senza riscriverli:

| Componente | Dove | Cosa fa già |
|---|---|---|
| Scheduler pipeline | `Services/Pipeline/PipelineSchedulerWorker.cs` | Run schedulati via cron (UTC, 5 campi), slot singolo, skip-mai-Live, ri-applica automatica dell'ensemble con isteresi + veto del supervisore AI |
| Recovery post-riavvio | `IPipelineEngine.RecoverOrphanedRunsAsync` | Le righe "Running" orfane di un processo morto diventano "Paused" resumabili (chiamato a startup) |
| Promozione corsie | `Services/Trading/PromotionWorker.cs` | Valutazione periodica Paper→Testnet (mai Live per costruzione) |
| Supervisore AI | `Services/Agents/` + `PipelineSchedulerWorker` | Giudizio advisory/veto-only sui run completati (provider Logging/Claude) |
| Analisi di contesto | `Services/Volatility/` (GARCH), `Services/Regime/` (K-means, retraining 7gg) | Regime e volatilità già calcolati e persistiti |
| Freni di sicurezza | `SafetyChecker`, guardie in `TradingEngine.StartAsync`, masterkey-placeholder-block | Verificati funzionanti dal vivo nella sessione di test |
| Trigger "Event" | `Services/Pipeline/PipelineEntities.cs:72` | **Esiste solo come valore documentato** (`"Manual" \| "Scheduled" \| "Event"`): zero produttori nel codebase — è il punto d'aggancio naturale della Fase 2 |

---

## §2 — Non-goals (espliciti, per non rivalutarli tra un mese)

| Idea | Decisione | Motivazione |
|---|---|---|
| Auto-promozione a **Live** | **MAI** — invariante di piattaforma | Il failsafe anti-Live a 5 livelli è verificato dai test (`Audit*`); nessuna fase di questo PRD lo tocca. L'autonomia si ferma a Testnet. |
| Reinforcement learning / auto-tuning continuo dei parametri | Scartata | Stessa motivazione del rifiuto RL in ROADMAP-QLIB (sim-to-real overfitting); il vaglio resta walk-forward + holdout + DSR. |
| Bus di messaggi / infrastruttura di notifica pesante (RabbitMQ, SignalR verso app mobile) | Scartata | Progetto solo-operatore: un provider di notifica minimale (Fase 4) copre il bisogno reale. |
| Riscrittura del motore pipeline | Scartata | Le 4 cacce della sessione hanno dimostrato che il motore funziona ed è onesto; si aggiunge SOPRA (planner), non DENTRO. |
| Multi-exchange failover automatico | Backlog condizionale (§8) | Nessun bisogno osservato oggi. |

---

## §3 — Fase 0: Fiducia negli input e integrità contabile

**Perché prima di tutto**: la sessione di test ha trovato la corsia 2 Testnet a **PnL -1.817.925 su
capitale 10.000** per fill patologici dall'exchange (`Sell 171.673.819 @ 0,00 → Filled`;
`Buy 1.039 ETH ≈ 1,8M nozionale`) adottati senza verifica — e **nessun automatismo se n'è accorto**
finché un umano non ha aperto la pagina. Un sistema autonomo senza questa fase è pericoloso.

| # | Item | Stato | Dettaglio |
|---|---|---|---|
| A1 | **Sanity check sui fill di ritorno** | **In corso — PR #24** | In `PositionOpener`/`PositionCloser`/`OrderReconciler`: quantità entro tolleranza della richiesta, prezzo > 0 ed entro banda dal prezzo corrente; fuori banda ⇒ esito INCERTO (riconciliazione/reject + audit `FillSanityRejected`), mai adottare il fill. Test con client scriptati (prezzo 0, quantità 100×). |
| A2 | **Credenziali indecifrabili gestite con grazia** | **In corso — sessione parallela** | `/settings/exchanges` non deve andare in 500 (`AuthenticationTagMismatchException` dentro la query EF, `ExchangeSettings.razor:195`): decifratura per-riga con badge "reinserire"; `TradingEngine.LoadCredentialsAsync` fallisce con messaggio chiaro. |
| A3 | **Watchdog di invarianti contabili + quarantena corsia** | **FATTO (2026-07-19)** — `LaneInvariantWatchdog` + `LaneInvariantChecker` (puro) + tabella `LaneQuarantines` (migrazione `AddLaneQuarantine`); quarantena = stop trading SENZA chiusure forzate, riga persistita che blocca `StartAsync` finché un Admin non la rimuove in /trading (audit `LaneQuarantined`/`LaneQuarantineCleared`); config `Trading:LaneInvariants` (default ON, soglie lasche). Test: `LaneInvariantCheckerTests` + `LaneInvariantWatchdogTests` riproducono i numeri REALI della corsia 2 | Nuovo `LaneInvariantWatchdog` (BackgroundService leggero o check in coda a `ProcessCandleAsync`): invarianti per corsia — `AvailableCapital ≥ -ε`, `|TotalPnl| ≤ k × TotalCapital × Leverage` (k configurabile, default 2), `posizioni aperte × nozionale ≤ esposizione massima`. Violazione ⇒ corsia in **quarantena** (stop trading, stato marcato, nessuna chiusura forzata automatica — stessa filosofia della "difesa inversa" del `FuturesPositionReconciler`) + evento di notifica (Fase 4; finché non esiste, `LogCritical` + audit). Verificato: oggi nel codebase non esiste alcun watchdog contabile (grep `invariant/watchdog/quarantine` ⇒ solo contratti anti-look-ahead nei fattori alpha). |

**Criteri di accettazione**: test di regressione che riproducono ESATTAMENTE il caso reale della
corsia 2 (fill a prezzo 0, fill con quantità 100×) e dimostrano: fill rifiutato, capitale intatto,
corsia in quarantena, evento emesso. **Rischio**: medio (tocca il percorso ordini; mitigato dai
client scriptati già esistenti in `TradingEngineReconcileTests`). **Approvazione**: A1/A2 già
avviate; A3 procede senza gate aggiuntivo (pattern esistenti).

---

## §4 — Fase 1: Politica di reazione — il Campaign Planner

**Perché**: la pipeline conclude onestamente "0 sopravvissuti: NON operare" **e poi si ferma**.
Nella sessione di test la mossa successiva (rotazione config, piste mirate, attesa) l'ha decisa
ogni volta l'operatore. Questo è il pezzo mancante concettualmente più importante: l'autonomia non
è eseguire run, è **decidere cosa fare dopo un run**.

Design (aggiunge SOPRA la pipeline, zero modifiche al motore):

- Nuova entità `VettingCampaign` (JSON su `PipelineArtifact` o tabella dedicata): elenco ordinato di
  configurazioni di caccia (es. le 7 già salvate), stato per config (ultima esecuzione, esito,
  tentativi), politica di rotazione.
- Nuovo `CampaignPlannerWorker` (stesso pattern `PeriodicTimer` degli altri 9 worker): al
  completamento di un run della campagna —
  - **0 sopravvissuti** → prossima config della rotazione, con **backoff** (non ripetere la stessa
    config prima di N ore se il regime non è cambiato — il segnale di cambio regime arriva dalla
    Fase 2);
  - **sopravvissuti > 0** → applica l'ensemble via `IPipelineApplier` (STESSA implementazione
    della ri-applica esistente, col veto del supervisore già cablato), avvia le corsie in
    Paper/Testnet secondo config, **ferma la rotazione** e passa in modalità osservazione
    (decay monitor);
  - rotazione esaurita senza sopravvissuti → stato campagna "in attesa di trigger" (Fase 2) +
    notifica (Fase 4).
- Le corsie senza strategie sopravvissute **restano ferme per scelta esplicita del planner**
  (oggi restano ferme solo se un umano non le avvia).

**Criteri di accettazione**: test del planner con motore fake (stesso approccio dei
`PipelinePageServiceTests`): rotazione, backoff, stop-su-successo, ripresa-su-trigger; un test
end-to-end Paper con config smoke ("Smoke test BTC+ETH 1h" già esistente). **Rischio**: medio-alto
(è IL cambio di natura: da strumento a agente) — **gate esplicito: default OFF**
(`Campaign:Enabled=false`), attivazione manuale per campagna. **Approvazione**: richiesta
sull'attivazione di default, non sul codice.

**FATTO (2026-07-19)** — `VettingCampaign` (tabella dedicata, migrazione `AddVettingCampaign`) +
`CampaignPlanner`/`CampaignPlannerWorker` + pagina `/campaign` (crea/abilita/riattiva, doppio
gate: `Campaign:Enabled` globale default OFF + flag per campagna). La catena valuta-e-applica è
stata ESTRATTA da `PipelineSchedulerWorker` in `IRunApplyEvaluator` (stessa identica
implementazione — supervisore con veto + isteresi + applier, un solo gate di atomicità) e
condivisa tra ri-applica automatica e planner. Run della rotazione = trigger `"Campaign"` (🎯
nello storico, così la ri-applica dello scheduler sui run "Scheduled" non li tocca), run da wake
= `"Event"` (⚡). **Scostamenti dal design**: (1) su sopravvissuti NON schierati (veto del
supervisore o isteresi "il corrente è meglio") la rotazione CONTINUA invece di fermarsi —
fermarsi senza aver schierato nulla lascerebbe la flotta ferma per un candidato rifiutato;
(2) avvio corsie SOLO Paper e solo se ferme (Testnet nel planner resta nel backlog §8);
(3) config in ExecutionMode Live sempre saltate (stessa regola dello scheduler). Test:
`CampaignPlannerTests` (12 casi: rotazione, backoff, esaurimento→WaitingForTrigger,
wake→Event, veto→continua, slot occupato, Live saltata, quarantena non fatale).

---

## §5 — Fase 2: Trigger contestuali (event-driven, non solo cron)

**Perché**: la caccia gira alle 03:00 UTC, ma il regime cambia quando cambia. Evidenza dalla
sessione: GARCH su SOL 15m con vol corrente al **39% del lungo periodo** e forecast in
mean-reversion verso l'alto — il momento giusto per ri-vagliare è **quando quella compressione si
scioglie**, non 12 ore dopo. L'aggancio esiste già nel modello dati (`Trigger = "Event"`,
`PipelineEntities.cs:72`) ma non ha alcun produttore.

- Nuovo `RegimeChangeTrigger` (worker leggero, riusa i calcoli esistenti): scatta quando —
  - il **cluster K-means corrente cambia** rispetto all'ultimo run della campagna (già persistito
    dall'`RegimeAnalysis` della pipeline), oppure
  - la **vol GARCH esce da una banda** rispetto al forecast dell'ultimo run (es. realized > 1,5×
    forecast — l'espansione di volatilità che oggi si aspetterebbe su SOL), oppure
  - (opzionale, appetito da valutare) uno **spike di sentiment** oltre soglia sulle ultime 24h.
- Il trigger NON lancia direttamente: **chiede al planner** (Fase 1) di anticipare la prossima
  esecuzione, con **cooldown** (default 6h) e rispetto dello slot singolo del motore (un run alla
  volta, già garantito da `StartRunAsync` che rifiuta se occupato).
- Ogni run così avviato usa `trigger: "Event"` — la UI lo mostra già (⚡ in `Pipeline.razor`).

**Criteri di accettazione**: test unitari del detector (cambio cluster sintetico, banda vol) e del
cooldown; un run Event visibile nello storico con ⚡. **Rischio**: basso (additivo, riusa
GARCH/K-means esistenti). **Dipendenza**: Fase 1 (il trigger parla col planner, non col motore).

**FATTO (2026-07-19)** — `RegimeChangeDetector` (decisione PURA `Evaluate` + realized vol come
stddev per-periodo dei log-rendimenti, confrontabile col `ForecastVolatility24` GARCH del
checkpoint): baseline = ultimo run COMPLETATO delle campagne abilitate, stato corrente via
`IMarketFeatureExtractor` + `IRegimeDetector.LabelFeaturesAsync` (stesso percorso
dell'EnsembleManager, zero calcoli nuovi). Banda vol nei DUE versi (espansione oltre k×forecast
E compressione sotto forecast/k). `RegimeChangeTriggerWorker`: cooldown 6h in-memory, parla SOLO
con `ICampaignPlanner.WakeAsync` (run marcati "Event" ⚡ — già visibili nello storico dalla
Fase 1), gate a monte su `Campaign:Enabled`, notifica Fase 4 quando scatta; se nessuna campagna
viene svegliata il cooldown NON si consuma. Config `RegimeTrigger` (Enabled default ON: inerte
senza campagne). Lo spike di sentiment resta nel backlog §8 come da PRD. Test:
`RegimeChangeTriggerTests` (14 casi: cluster sintetico, banda nei due versi, dati mancanti = mai
trigger, realized vol, cooldown, gate, wake).

---

## §6 — Fase 3: Resilienza operativa ai riavvii

**Perché**: evidenze dirette della sessione — il run interrotto dallo spegnimento del server è
rimasto **Paused tutto il giorno** (unico chiamante di `ResumeRunAsync` = il bottone in
`Pipeline.razor:373`); l'app avviata con la master key sbagliata **muore in silenzio** sul percorso
credenziali (B2) invece di dichiararlo a voce alta.

| # | Item | Dettaglio |
|---|---|---|
| C1 | **Auto-resume dei run Paused** | In coda a `RecoverOrphanedRunsAsync` (o nel primo tick dello scheduler): i run "Paused" con trigger Scheduled/Event della campagna attiva vengono ripresi automaticamente con backoff (1 tentativo, poi notifica; MAI auto-resume di run in modalità Live — che comunque non esistono per costruzione). I Paused manuali restano manuali. |
| C2 | **Fail-fast rumoroso sulle chiavi** | All'avvio, se `Security:MasterKey` non decifra un campione di credenziali esistenti: banner persistente in UI (`/trading` e `/settings/exchanges`) + `LogCritical` + evento di notifica — oggi lo scopri solo quando una pagina va in 500 o un avvio Testnet fallisce. |
| C3 | **Riallineamento corsie post-restart** | Dopo il riavvio, se la campagna era in "osservazione" con corsie attive, il planner verifica che le corsie siano nello stato atteso (running/mode) e le riallinea o notifica la divergenza. Oggi `EnsureLoadedAsync` ricostruisce lo stato della singola corsia, ma nessuno confronta lo stato ATTESO con quello REALE a livello di flotta. |

**Rischio**: basso-medio. **Criteri**: kill del processo durante un run di smoke test → al riavvio
il run riprende da solo e lo storico lo mostra; avvio con chiave sbagliata → banner visibile.

**FATTO (2026-07-19)** — **C1**: auto-resume in `PipelineSchedulerWorker.AutoResumePausedRunsAsync`
(ogni tick, non solo a startup): run Paused con trigger Scheduled/Event/Campaign ripresi da soli;
Paused MANUALI mai toccati; config Live saltate; slot occupato = check saltato senza consumare
tentativi (`GetLiveStatus` pre-check); budget di **3 tentativi** per run (marker persistenti su
PipelineArtifacts, scostamento documentato dal "1 tentativo" del PRD: un run interrotto due volte
da riavvii innocenti merita più di un tentativo, il tetto esiste contro i crash-loop), a
esaurimento give-up + notifica una-tantum. **C2**: `IMasterKeyProbe`/`MasterKeyProbeWorker`
(probe one-shot a startup con retry DB, `IExchangeCredentialReader.CountUnreadableAsync`):
credenziali non decifrabili ⇒ LogCritical + notifica Critical + banner persistente in /trading e
/settings/exchanges. Registrato in entrambi gli host (sola lettura). **C3**: campo
`VettingCampaign.ObservedLanes` (stato ATTESO di flotta, scritto all'applica) +
`RealignObservedLanesOnceAsync` nel planner: al PRIMO tick per processo di una campagna in
osservazione confronta atteso vs reale — corsia ferma in Paper pulita ⇒ riavviata (Info); ferma
con emergency stop / modalità non-Paper / quarantena ⇒ SOLO notifica Warning (decisione umana);
una volta per processo per non combattere l'operatore. Test: `PipelineAutoResumeTests` (5),
`MasterKeyProbeTests` (3), riallineamento in `CampaignPlannerTests` (3). Il criterio "kill del
processo durante uno smoke run" resta da esercitare dal vivo alla prossima sessione operativa.

---

## §7 — Fase 4: Canale di notifica

**Perché**: corsia corrotta, 0 sopravvissuti, run fallito — oggi nessuno lo viene a sapere
(verificato: zero hit per `telegram|smtp|notification` in `Services/`). "Autonomo" senza canale di
ritorno significa solo "ignorato". Serve il contrario dell'autonomia cieca: un modo affidabile di
**chiamare l'umano quando serve**.

- Astrazione minima `INotifier` (`Services/Notifications/`), un solo metodo
  (`NotifyAsync(severity, title, body)`), provider iniziale **Telegram bot** (pragmatico per un
  solo operatore: gratuito, push su mobile, ~50 righe con `HttpClient`; token via env var, mai
  committato) + fallback `LoggingNotifier` (default).
- Producer cablati (in quest'ordine di valore): quarantena watchdog (Fase 0-A3), esiti campagna
  (Fase 1), run Failed, trigger Event scattato (Fase 2), fail-fast chiavi (Fase 3-C2), promozione
  Paper→Testnet eseguita dal `PromotionWorker`.
- **Default OFF** (`Notifications:Enabled=false`), rate-limit (max N messaggi/ora, coalescing).

**Rischio**: basso. **Criteri**: test con notifier fake sui producer; prova manuale reale su
Telegram.

**FATTO (2026-07-19)** — `Services/Notifications/`: `INotifier` (un metodo) implementato da
`NotificationDispatcher` (gate `Notifications:Enabled` default OFF hot-reload, rate-limit a
finestra scorrevole con coalescing "N soppresse riportate nel messaggio successivo", MAI
propaga eccezioni ai producer), provider `LoggingNotifier` (default) e `TelegramNotifier`
(token SOLO da env `TELEGRAM_BOT_TOKEN`, ChatId in config). Producer cablati: quarantena
watchdog (Critical), esiti campagna (schierato=Info, esaurita/run failed=Warning), run
pipeline Failed (`PipelineEngine.FinalizeRunAsync`, Warning), promozione automatica del
`PromotionWorker` (Info). Registrato in entrambi gli host (monolite e ProcioneMGR.Trading,
dove vive il watchdog in modalità remota). Test: `NotificationDispatcherTests` (gate,
rate-limit, coalescing, provider ignoto, errore contenuto) + `TelegramNotifierTests` (handler
scriptato: payload, token mancante, HTTP failure) + assert sui producer in
`CampaignPlannerTests`/`LaneInvariantWatchdogTests`. Resta da fare SOLO la prova manuale reale
su Telegram (serve un bot token dell'operatore). Trigger Event scattato (producer della
Fase 2) cablato nella Fase 2.

---

## §8 — Backlog condizionale

| Idea | Criterio di attivazione |
|---|---|
| Testnet automatico nel planner | La master key reale è disponibile all'app (oggi: placeholder ⇒ credenziali indecifrabili ⇒ Testnet impossibile — evidenza di sessione) E i fix A1/A2 sono mergiati |
| Riattivazione `PromotionWorker` dentro la campagna | Fase 1 stabile in Paper per ≥2 settimane senza interventi manuali |
| Trigger su news/sentiment (oltre lo spike) | L'impatto misurato (`NewsImpact`, es. CentralBanks +1,18%@24h su 293 oss.) supera una soglia di significatività su più categorie |
| Multi-exchange failover | Un'indisponibilità reale di Binance/Bitget osservata che abbia impedito un'operazione |
| Digest giornaliero via notifier | Fase 4 attiva e almeno una campagna in osservazione |

---

## §9 — Tabella riassuntiva

| Fase | Obiettivo | Rischio | Dipendenze | Gate | Stato |
|---|---|---|---|---|---|
| **0** | Fill sanity (A1), credenziali con grazia (A2), watchdog contabile + quarantena (A3) | Medio | — | No (A1/A2 già avviate) | **COMPLETA** — A1 = PR #24; A2 = PR #26; A3 = 2026-07-19 |
| **1** | Campaign Planner: rotazione cacce, applica-su-successo, corsie per scelta | Medio-alto | Fase 0 | **Sì**: `Campaign:Enabled` default OFF | **COMPLETA** (2026-07-19) |
| **2** | Trigger contestuali (regime/vol → "Event") | Basso | Fase 1 | No | **COMPLETA** (2026-07-19) |
| **3** | Auto-resume, fail-fast chiavi, riallineamento corsie | Basso-medio | — (C1 utile già da sola) | No | **COMPLETA** (2026-07-19) |
| **4** | Notifica (Telegram/logging, default off) | Basso | Massimo valore dopo 0-1 | No | **COMPLETA** (2026-07-19; resta la prova manuale Telegram) |

Ordine raccomandato: **0 → 1 → 4 → 2 → 3** (la notifica subito dopo il planner: un agente che
decide senza riferire è peggio di uno che non decide).

## §10 — Manutenzione del documento

Documento vivo: spuntare i criteri di accettazione a ogni fase completata e annotare gli
scostamenti dal design. Le evidenze empiriche che motivano ogni fase restano in
`docs/TEST-UI-2026-07-18.md` — se una futura sessione di esercizio autonomo trova nuovi gap,
aggiungerli lì e aggiornare qui il backlog, senza aprire un terzo documento.
