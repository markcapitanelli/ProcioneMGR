# 22 — INVENTARIO DOCUMENTO PER DOCUMENTO

> Tutti i documenti Markdown del repository: **115** in `docs/` più 4 in radice.
> Per ciascuno: titolo, scopo dichiarato nella prima riga, data più recente citata nel testo.

**Come leggere la colonna «ultima data»:** è la data più recente che compare *dentro* il
documento, non la data del file. Per i REPORT coincide con la misura; per le roadmap indica
l'ultimo aggiornamento sostanziale.

| Categoria | Documenti | Righe |
|---|---:|---:|
| Radice del repository | 4 | 845 |
| Documenti operativi vivi | 7 | 1.832 |
| PRD — documenti di prodotto | 10 | 2.579 |
| REPORT — esiti misurati | 24 | 4.493 |
| Audit | 27 | 8.978 |
| Documentazione per pagina UI | 33 | 3.333 |
| Roadmap archiviate (chiuse o superate) | 14 | 5.053 |
| **Totale** | **119** | **27.113** |

---

## Radice del repository

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `AUDIT-VALORE.md` | 201 | 2026-08-01 | **AUDIT DI VALORE — ProcioneMGR (2026-08-01)**<br/>Domanda a cui risponde: *l'aggiornamento massiccio ha creato valore o feature bloat, rigidità e |
| `AUDIT_REPORT.md` | 283 | 2026-07-31 | **AUDIT REPORT — ProcioneMGR (analisi a freddo, 2026-07-31)**<br/>**Metodo.** Analisi da contesto fresco, solo su ciò che è scritto nei file: inventario con |
| `CLAUDE.md` | 111 | 2026-08-04 | **ProcioneMGR — istruzioni per Claude Code**<br/>Questo progetto ha una memoria esterna già costruita e mantenuta. **Interrogala prima di esplorare |
| `README.md` | 250 | — | **ProcioneMGR**<br/>**Piattaforma di ricerca e trading algoritmico** per criptovalute, costruita in **.NET 10 / Blazor Server**. Copre l'intero ciclo di vita di una strategia — dall'ingestione dei dati di me… |

## Documenti operativi vivi

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `docs/NAVBAR.md` | 102 | — | **NavBar — struttura per workflow utente**<br/>La sidebar di navigazione è organizzata in **blocchi per workflow** invece di una lista piatta. |
| `docs/POSTGRES_MIGRATION.md` | 287 | 2026-07-09 | **Migrazione a PostgreSQL**<br/>Guida operativa (storica) per la migrazione di ProcioneMGR da SQLite a **PostgreSQL**. La migrazione |
| `docs/REVISIONE-STATO-ARTE-2026-07.md` | 166 | 2026-07-25 | **Revisione contro lo stato dell'arte — 2026-07-25**<br/>Revisione completa su richiesta: ogni area della piattaforma (pagine, roadmap, codice) confrontata |
| `docs/ROADMAP.md` | 886 | 2026-08-06 | **ROADMAP — Integrazione, core caldo e scoperta pattern (viva, 2026-07-28)**<br/>*Questa è l'unica roadmap corrente. Le otto precedenti sono in `docs/archive/` — chiuse o |
| `docs/STANDARD-VERIFICA.md` | 149 | 2026-08-01 | **Standard di verifica per ogni fase della roadmap**<br/>*Nato il 2026-07-27 da un'osservazione del proprietario: «servono più tipi di test per convalidare |
| `docs/STATO-DELLA-PIATTAFORMA.md` | 106 | 2026-07-26 | **Stato della piattaforma — cosa c'è, cosa funziona, cosa non c'è**<br/>*Aggiornato 2026-07-20. Da leggere prima di usare la piattaforma per decidere qualcosa.* |
| `docs/TEST-UI-2026-07-18.md` | 136 | 2026-07-19 | **Test completo UI + Vaglio strategie — 2026-07-18 (sessione autonoma)**<br/>**Contesto di mercato** (concordi tra pipeline, GARCH e K-means): 1h-4h-1d = **Bear Low-Vol** |

## PRD — documenti di prodotto

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `docs/PRD-AI-MULTIPROVIDER-2026-08.md` | 155 | 2026-08-02 | **PRD — Layer AI multi-provider: chiavi, instradamento, usi (2026-08)**<br/>*Nasce dalla richiesta del proprietario (2026-08-01): la pagina /admin/ai-supervisor non è mai |
| `docs/PRD-AUTONOMIA-FINANZIARIA-2026-08.md` | 236 | 2026-08-05 | **PRD — Autonomia Finanziaria (dodicesima roadmap, 2026-08-02)**<br/>*Nato dal sesto PDF esterno («Progettare l'Autonomia Finanziaria: un framework per ProcioneMGR |
| `docs/PRD-AUTONOMIA-OPERATIVA.md` | 272 | 2026-07-19 | **PRD — Autonomia Operativa di ProcioneMGR**<br/>**Stato**: **TUTTE E 5 LE FASI COMPLETE** (2026-07-19, ordine 0 → 1 → 4 → 2 → 3 come |
| `docs/PRD-BENCHMARK-BITGET-AI-2026-08.md` | 312 | 2026-08-05 | **PRD — Benchmark Bitget/AI (tredicesima roadmap, 2026-08-04)**<br/>*Nato dal settimo PDF esterno («Architettura di un Bot di Trading AI per Altcoin: Guida allo |
| `docs/PRD-CONSOLIDAMENTO-ARCHITETTURA.md` | 632 | 2026-07-18 | **PRD — Consolidamento Architetturale di ProcioneMGR**<br/>**Stato**: **tutte e 3 le fasi completate** — Fase 0 (PR #13, 2026-07-17), Fase 1 (PR |
| `docs/PRD-INTEGRAZIONE-CORE-CALDO.md` | 174 | 2026-07-28 | **PRD — Integrazione "core caldo / guscio freddo" (2026-07-26)**<br/>*Documento di prodotto del filone B della [ROADMAP](ROADMAP.md). Le fasi A (consolidamento) e C |
| `docs/PRD-ONNX-SENTIMENT-PILOT-2026-08.md` | 77 | 2026-08-01 | **PRD — Pilota di inferenza locale ONNX per il sentiment (2026-08)**<br/>*Nasce dal PDF di ricerca esterna "Architetture AI per Trading in C#" (2026-08-01), che proponeva |
| `docs/PRD-PRESTAZIONI-2026-08.md` | 198 | 2026-08-05 | **PRD — Prestazioni e risorse (Filone H, 2026-08-05)**<br/>*Nato dalla richiesta del proprietario: «ottimizzare tutti i processi della piattaforma e il suo |
| `docs/PRD-SCOPERTA-PATTERN-ANTIOVERFITTING.md` | 304 | 2026-07-28 | **PRD — Scoperta di pattern e interpretabilità anti-overfitting (2026-07-27)**<br/>*Valutazione di fattibilità del report "Dalla Ricerca di Pattern alla Validazione Anti-Overfitting" |
| `docs/PRD-VALORE-2026-08.md` | 219 | 2026-08-01 | **PRD — Valore: costo del calcolo, voce della validazione, igiene dei verdetti (2026-08)**<br/>*Undicesima ondata. Nasce dall'[audit di valore 2026-08-01](../AUDIT-VALORE.md) (statica + test |

## REPORT — esiti misurati

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `docs/REPORT-1M-COSTI-R2.md` | 192 | 2026-07-20 | **R2 — Dati 1m e verifica dell'edge al netto dei costi**<br/>Data: 2026-07-20 · Branch `claude/procione-trading-bot-roadmap-faa381` · Suite **1317/1317** |
| `docs/REPORT-ANALISI-RICOSTRUZIONE-2026-07.md` | 465 | 2026-07-08 | **Report Analisi e Piano di Ricostruzione ProcioneMGR**<br/>**Data:** 2026-07-08 |
| `docs/REPORT-AUDIT-CONSOLIDAMENTO-2026-07.md` | 157 | 2026-07-17 | **Report di Audit Architetturale — Consolidamento Totale (2026-07-17)**<br/>Audit condotto file-per-file sull'intero repository (401 file C#, ~35.000 righe nel monolite + ~22.000 di test), con tracciamento dei caller per ogni modulo sospetto. Build verificata: **… |
| `docs/REPORT-AUTONOMY.md` | 97 | 2026-07-05 | **Gli ultimi 3 passi verso l'autonomia — Report di implementazione**<br/>Data: 2026-07-05 · Build: 0 errori, 0 warning nuovi · Test: **507/507** (28 nuovi) · Verificato dal vivo su PostgreSQL. |
| `docs/REPORT-B2-FRESCHEZZA-2026-07-28.md` | 90 | 2026-08-02 | **B2 — Il gate non sapeva vedere una serie ferma (2026-07-28)**<br/>*Chiude una cecità del gate B2 del [PRD-INTEGRAZIONE-CORE-CALDO](PRD-INTEGRAZIONE-CORE-CALDO.md), |
| `docs/REPORT-B3-EXITLAG-2026-07-28.md` | 163 | 2026-07-28 | **B3 — Il gate tick-vs-candela, sbloccato e misurato (2026-07-28)**<br/>*Chiude il secondo tempo del gate B3 del [PRD-INTEGRAZIONE-CORE-CALDO](PRD-INTEGRAZIONE-CORE-CALDO.md). |
| `docs/REPORT-CACCIA-STRATEGIE-2026-07.md` | 152 | 2026-07-02 | **Report caccia alle strategie — 2 luglio 2026**<br/>Ingestione da Binance (klines pubbliche): **10 coppie × 3 timeframe (1h/4h/1d), dal |
| `docs/REPORT-CREATIVE-DISCOVERY.md` | 173 | 2026-07-03 | **Report — Creative Strategy Discovery (2026-07-03)**<br/>Implementazione completa del layer di scoperta creativa: genera automaticamente strategie |
| `docs/REPORT-D3-OFI-2026-07-28.md` | 322 | 2026-07-28 | **Report — D3: l'OFI vero, misurato senza il pilota di raccolta**<br/>*2026-07-28. Chiusura dell'ultimo item aperto del Filone D della [ROADMAP](ROADMAP.md), che era |
| `docs/REPORT-DECAY-MONITOR.md` | 204 | — | **Report — Monitor di Decadimento Strategia (Realizzato vs Atteso)**<br/>Trasformare "l'edge è morto?" da intuizione a segnale misurabile: confrontare automaticamente lo |
| `docs/REPORT-DOSAGGIO-VOLATILITA.md` | 273 | 2026-07-20 | **Dosaggio della volatilità — un risultato che NON ha replicato**<br/>*2026-07-20. Universo: 24 alt su Binance, 1d, 2021-05-11 → 2026-07-20 (1897 giorni allineati). |
| `docs/REPORT-E1-STATARB-2026-07-24.md` | 107 | 2026-07-24 | **Report E1 — Stat-arb cointegrazione 2.0 + F-queue (primo sviluppo ROADMAP-PROFITTO-INTRADA**<br/>*2026-07-24. Primi due item della roadmap sviluppati, integrati, testati e MISURATI sul database |
| `docs/REPORT-EXPANSION-STRESS-TEST.md` | 119 | — | **Report — Espansione dati + stress test completo della piattaforma**<br/>Obiettivo (richiesta utente): espandere al massimo i dati (storici, strategici, di analisi), |
| `docs/REPORT-FILONE-D-2026-07-27.md` | 230 | 2026-07-28 | **Report — Filone D eseguito: D1 (SHAP) e D2 (deriva dei fattori)**<br/>*2026-07-27. Esecuzione dei primi due item del Filone D della [ROADMAP](ROADMAP.md), nati dalla |
| `docs/REPORT-FRONTIERE-2026-07.md` | 159 | 2026-07-24 | **Report Frontiere — prima ondata di esecuzione della ROADMAP-FRONTIERE-PROFITTO**<br/>*2026-07-24. Esecuzione del "percorso consigliato" §5 della roadmap: le mosse senza rischio (F4, |
| `docs/REPORT-GIORNATA-OPERATIVA-2026-07-24.md` | 155 | 2026-07-24 | **Report giornata operativa — 2026-07-24**<br/>*Sessione autonoma su mandato dell'utente: caccia a nuove strategie, rinnovo delle configurazioni |
| `docs/REPORT-MODALITA-SEMPLICE-R3.md` | 138 | 2026-07-20 | **R3 — Modalità Semplice e profili di rischio per corsia**<br/>Data: 2026-07-20 · Branch `claude/procione-trading-bot-roadmap-faa381` · Suite **1358/1358** |
| `docs/REPORT-MULTI-LANE.md` | 189 | — | **Report — Supporto Multi-Coppia Concorrente nell'Ensemble/Trading (corsie isolate)**<br/>Il prompt "PROMPT — Supporto Multi-Coppia Concorrente nell'Ensemble/Trading" chiedeva un |
| `docs/REPORT-PERCHE-NON-CONSOLIDA-2026-07-28.md` | 167 | 2026-07-28 | **Perché i candidati non consolidano mai (2026-07-28)**<br/>*Risponde a un'osservazione del proprietario: «di candidati se ne trovano un buon numero, ma non |
| `docs/REPORT-PIPELINE-AUTONOMO.md` | 146 | 2026-07-02 | **Report — Autonomous Research & Strategy Pipeline (2026-07-02)**<br/>Implementazione completa del pipeline autonomo richiesto: un orchestratore end-to-end che |
| `docs/REPORT-PIPELINE-SCHEDULER.md` | 172 | 2026-07-05 | **Report — Schedulazione Automatica delle Cacce Pipeline**<br/>Automatizzare l'esecuzione periodica delle `PipelineConfiguration`: un worker in background legge |
| `docs/REPORT-REALTIME-FEED-R1.md` | 149 | 2026-07-20 | **R1 — Feed di prezzo real-time e uscite reattive**<br/>Data: 2026-07-20 · Suite: **1305/1305** · Feature **spenta di default** |
| `docs/REPORT-RICERCA-2026-07.md` | 325 | 2026-07-20 | **Ricerca di edge, luglio 2026 — cinque angoli, un controllo, nessuna opportunità**<br/>Data: 2026-07-20 · Universo: 45 coppie, 12,14M candele, timeframe 1m→1d |
| `docs/REPORT-STOPLOSS-WIRING.md` | 149 | — | **Report — Wiring Stop-Loss/Trailing dal Backtest al Trading Live**<br/>Lo stop-loss/trailing-stop validato nel backtest (`BestStopVariant` di una strategia |

## Audit

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `docs/audit/00_INDEX.md` | 106 | 2026-08-04 | **00 — Indice dell'audit**<br/>**Progetto:** ProcioneMGR |
| `docs/audit/00_MASTER_INDEX.md` | 248 | 2026-08-08 | **00 — MASTER INDEX (audit di ricostruzione)**<br/>`docs/audit/` conteneva **già** un audit del 2026-08-04 in 14 documenti (`00_INDEX.md` … |
| `docs/audit/01_PROJECT_MAP.md` | 355 | — | **01 — PROJECT MAP**<br/>Mappa navigabile del progetto. Per l'elenco esaustivo e sintetico di **tutti** i file vedi |
| `docs/audit/01_PROJECT_OVERVIEW.md` | 162 | 2026-08-04 | **01 — Panoramica del progetto**<br/>**ProcioneMGR** è una piattaforma personale di **quant research e trading algoritmico** su |
| `docs/audit/02_ARCHITECTURE.md` | 206 | — | **02 — Architettura**<br/>**Monolite modulare Blazor Server** con **microservizi opzionali estratti dietro feature-toggle**. |
| `docs/audit/02_ARCHITECTURE_AS_IS.md` | 378 | — | **02 — ARCHITETTURA AS-IS (reale, non teorica)**<br/>Tutto ciò che segue è ricavato dal codice. Dove una cosa **non** è come ci si aspetterebbe, è |
| `docs/audit/03_CODE_MAP.md` | 256 | 2026-08-04 | **03 — Mappa del codice**<br/>Riferimenti verificati per navigare il progetto senza aprirlo a caso. Conteggi al 2026-08-04. |
| `docs/audit/03_DOMAIN_QUANT_MACHINE.md` | 450 | 2026-07-31 | **03 — LA MACCHINA QUANTITATIVA**<br/>Un blocco per meccanismo. Per ciascuno: **esiste? · completo? · integrato? · usato dalla UI? · |
| `docs/audit/04_ALGORITHMS_AND_MODELS.md` | 422 | 2026-07-28 | **04 — CATALOGO ALGORITMI E MODELLI**<br/>Ogni voce riporta: nome · file · classe/metodo · categoria · scopo · formula/pseudocodice quando |
| `docs/audit/04_RUNTIME_AND_DATA_FLOW.md` | 206 | 2026-08-04 | **04 — Runtime e flusso dei dati**<br/>[Program.cs:24](../../ProcioneMGR/Program.cs#L24) attiva il *Npgsql legacy timestamp behavior*, che |
| `docs/audit/05_UI_BACKEND_BINDING.md` | 189 | 2026-07-31 | **05 — UI ↔ BACKEND BINDING**<br/>89 file `.razor` (22.607 righe), di cui **35 file** in `Components/Pages/` (18.604 righe): |
| `docs/audit/05_UI_PAGES_AND_ROUTES.md` | 158 | — | **05 — Pagine, route e protezioni**<br/>**66 route** dichiarate con `@page`: 34 pagine applicative + 32 pagine Identity scaffolded. |
| `docs/audit/06_API_AND_INTEGRATIONS.md` | 198 | 2026-08-04 | **06 — API e integrazioni**<br/>L'app è **Blazor Server**. Le pagine non fanno `fetch`: chiamano direttamente servizi C# |
| `docs/audit/06_INTEGRATION_GAPS.md` | 442 | 2026-08-08 | **06 — GAP DI INTEGRAZIONE**<br/>Ogni gap: **descrizione · evidenza · impatto · severità · proposta**. |
| `docs/audit/07_BROWSER_CHECK_REPORT.md` | 202 | 2026-08-04 | **07 — Controllo nel browser**<br/>**Data:** 2026-08-04, ~20:15 CEST |
| `docs/audit/07_RECONSTRUCTION_BLUEPRINT.md` | 405 | — | **07 — BLUEPRINT DI RICOSTRUZIONE**<br/>La forma resta quella attuale — **monolite modulare con satelliti opzionali** — con tre correzioni |
| `docs/audit/08_TEST_PLAN.md` | 179 | 2026-08-04 | **08 — Piano di test**<br/>`ProcioneMGR.Tests/` — **259 file**, ~1999 metodi annotati `[Fact]`/`[Theory]`. |
| `docs/audit/08_TRACEABILITY_MATRIX.md` | 86 | — | **08 — MATRICE DI TRACCIABILITÀ**<br/>**dominio → file/classi → stato → gap → azione**, per tutti i domini dichiarati nel mandato. |
| `docs/audit/09_OPEN_QUESTIONS.md` | 210 | 2026-08-08 | **09 — DOMANDE APERTE**<br/>Ciò che **non** ho potuto determinare con certezza dal codice, e le decisioni che spettano a un |
| `docs/audit/09_RISKS_AND_TECH_DEBT.md` | 329 | 2026-08-04 | **09 — Rischi e debito tecnico**<br/>Ordinati per priorità. Ogni voce ha: cosa, dove si verifica, perché conta, cosa farci. |
| `docs/audit/10_CLAUDE_CODE_MEMORY.md` | 196 | 2026-08-04 | **10 — Memoria per Claude Code**<br/>ProcioneMGR è una piattaforma personale di **quant research e trading algoritmico su cripto**, |
| `docs/audit/10_FILE_INVENTORY.md` | 832 | 2026-08-08 | **10 — INVENTARIO FILE-BY-FILE**<br/>Inventario consultabile di tutti i file sorgente del repository, raggruppati per area. |
| `docs/audit/11_NOTEBOOKLM_EXPORT.md` | 557 | 2026-08-04 | **ProcioneMGR — memoria completa del progetto**<br/>**ProcioneMGR** è una piattaforma personale di **ricerca quantitativa e trading algoritmico su |
| `docs/audit/12_UI_WALKTHROUGH.md` | 328 | 2026-08-04 | **12 — Giro completo dell'applicazione, dal vivo**<br/>**Data:** 2026-08-04, 21:00–21:40 CEST · **Target:** `http://localhost:5199` · **Sessione:** autenticata |
| `docs/audit/13_DEEP_DIVE_CODE.md` | 265 | 2026-07-26 | **13 — Approfondimento del codice**<br/>Secondo passaggio, più in profondità del primo. Qui si guarda dentro i file, non solo la struttura. |
| `docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md` | 491 | 2026-08-08 | **20 — DEEP DIVE: lettura del codice riga per riga**<br/>E, altrettanto importante, **ciò che ho verificato essere corretto** — vedi §7. |
| `docs/audit/21_FILE_BY_FILE_CATALOG.md` | 1122 | 2026-08-08 | **21 — CATALOGO FILE PER FILE**<br/>**Legenda:** `[n]` = righe del file · il testo è il `&lt;summary>` del tipo principale, |

## Documentazione per pagina UI

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `docs/pagine/README.md` | 98 | 2026-07-19 | **Documentazione delle pagine — ProcioneMGR**<br/>Un file per ogni pagina della piattaforma: **a cosa serve, come è strutturata la UI, come |
| `docs/pagine/account.md` | 72 | — | **Pagine Account (Identity) — `/Account/*`**<br/>È il blocco di autenticazione e gestione del profilo utente, basato sullo scaffold Identity |
| `docs/pagine/admin-ai-supervisor.md` | 98 | 2026-08-02 | **Supervisione AI — `/admin/ai-supervisor`**<br/>Mostra i pareri dell'**advisory layer Claude** sul ciclo di ricerca: a ogni run di pipeline |
| `docs/pagine/admin-autonomy.md` | 172 | 2026-07-31 | **Autonomia — `/admin/autonomy`**<br/>Il **pannello unico di tutti gli automatismi** della piattaforma, prima controllabili solo |
| `docs/pagine/admin-backup.md` | 63 | 2026-07-09 | **Backup Database — `/admin/backup`**<br/>Backup e restore del **database PostgreSQL** (che contiene tutto lo stato: strategie, |
| `docs/pagine/admin-protections.md` | 148 | 2026-07-29 | **Protezioni di trading — `/admin/protections`**<br/>Le soglie del pannello sicurezza di [`/trading`](trading.md) agiscono sul **singolo ordine di |
| `docs/pagine/admin-users.md` | 56 | — | **Gestione Utenti — `/admin/users`**<br/>Gestisce **utenti e ruoli**. Il modello a tre ruoli con permessi crescenti (dal `GuidaPanel`): |
| `docs/pagine/alpha-mining.md` | 84 | — | **Alpha Mining — `/alpha-mining`**<br/>Cerca **NUOVE formule**, non combinazioni di fattori esistenti: un algoritmo **genetico** |
| `docs/pagine/backtest.md` | 120 | — | **Backtest — `/backtest`**<br/>Simula una strategia sui dati storici: la piattaforma rilegge le candele una a una come se |
| `docs/pagine/campaign.md` | 83 | — | **Campagne — `/campaign`**<br/>Il **Campaign Planner** decide **cosa fare DOPO un run di pipeline**, automaticamente. Una |
| `docs/pagine/dashboard.md` | 96 | — | **Dashboard — `/dashboard`**<br/>La Dashboard fa due cose, spiegate anche dal `GuidaPanel` in cima alla pagina (righe 23–41): |
| `docs/pagine/discovery.md` | 88 | — | **Discovery — `/discovery`**<br/>"**Non so quale strategia usare, trovamela tu**": mentre Optimization ottimizza UNA strategia |
| `docs/pagine/ensemble.md` | 128 | 2026-07-25 | **Ensemble — `/ensemble`**<br/>Combina **più strategie in un unico portafoglio** su una corsia di trading, dividendo il |
| `docs/pagine/execution.md` | 83 | — | **Execution Lab — `/execution`**<br/>Misura **quanto costa davvero eseguire un ordine**: su size significative, distribuire |
| `docs/pagine/experiments.md` | 77 | — | **Esperimenti — `/experiments`**<br/>È il **registro degli esperimenti** (in stile MLflow): ogni backtest, ottimizzazione, |
| `docs/pagine/feature-selection.md` | 166 | 2026-07-28 | **Feature Selection (IC) — `/feature-selection`**<br/>Prima di addestrare un modello ML, misura **quali fattori (indicatori) hanno davvero un |
| `docs/pagine/home.md` | 90 | 2026-07-28 | **Home — `/`**<br/>La Home è il **punto di ingresso e orientamento** della piattaforma. Non esegue operazioni: |
| `docs/pagine/market-analysis.md` | 103 | — | **Analisi Serie — `/market-analysis`**<br/>Applica alla serie storica le **analisi "a priori" del metodo quantitativo** (impostazione |
| `docs/pagine/market-bars.md` | 93 | — | **Barre informative — `/market/bars`**<br/>Costruisce e confronta le **barre informative** (ML4T cap. 2): invece di chiudere una candela |
| `docs/pagine/metrics.md` | 111 | 2026-07-25 | **Metriche — `/metrics`**<br/>Dashboard di **osservabilità runtime**: legge i contatori interni emessi dal motore |
| `docs/pagine/ml.md` | 142 | — | **ML Lab — `/ml`**<br/>Addestra un modello di **machine learning** a prevedere il rendimento futuro a partire dai |
| `docs/pagine/optimization.md` | 103 | — | **Optimization — `/optimization`**<br/>Ottimizza i parametri di una strategia **senza illudersi**: prova le combinazioni (Grid |
| `docs/pagine/pairs-trading.md` | 76 | — | **Pairs Trading — `/pairs-trading`**<br/>**Statistical arbitrage**: invece di scommettere sulla direzione di un asset, si scommette |
| `docs/pagine/pipeline.md` | 119 | — | **Pipeline — `/pipeline`**<br/>Il pipeline **automatizza l'intero flusso di ricerca**: scarica i dati, valuta i fattori, |
| `docs/pagine/portfolio.md` | 95 | — | **Portafoglio — `/portfolio`**<br/>Risponde alla domanda "**come dividere il capitale tra più asset?**", con la premessa che |
| `docs/pagine/regimes.md` | 111 | 2026-07-25 | **Regimes — `/regimes`**<br/>Il mercato non si comporta sempre allo stesso modo: una strategia a media mobile ama le |
| `docs/pagine/registry.md` | 79 | — | **Registry Modelli — `/registry`**<br/>Governa il **ciclo di vita dei modelli ML**: ogni modello salvato vive in uno stadio |
| `docs/pagine/sentiment.md` | 118 | 2026-08-01 | **Sentiment — `/sentiment`**<br/>Porta nella piattaforma i **dati alternativi**: notizie (CoinDesk, Cointelegraph, The Block, |
| `docs/pagine/settings-exchanges.md` | 87 | 2026-07-01 | **Credenziali Exchange — `/settings/exchanges`**<br/>Salva le **chiavi API degli exchange** (Binance/Bitget), necessarie solo per Testnet e Live |
| `docs/pagine/strategies.md` | 53 | — | **Le mie Strategie — `/strategies`**<br/>È l'**archivio personale delle configurazioni salvate**: ogni volta che in |
| `docs/pagine/trading.md` | 149 | 2026-07-31 | **Trading — `/trading`**<br/>È il **control center dell'operatività reale**: qui le strategie girano davvero (non è una |
| `docs/pagine/volatility.md` | 74 | — | **Volatilità — `/volatility`**<br/>Stima la **volatilità futura** con un modello **GARCH(1,1)**, sfruttando il fatto empirico |
| `docs/pagine/watchlist.md` | 98 | 2026-07-31 | **Watchlist — `/market/watchlist`**<br/>È il punto dove si dichiara **quali serie di mercato la piattaforma deve scaricare e tenere |

## Roadmap archiviate (chiuse o superate)

| Documento | Righe | Ultima data | Titolo e scopo |
|---|---:|---|---|
| `docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md` | 834 | 2026-07-25 | **Roadmap Architetture di Esecuzione — 2026-07**<br/>**Sesta roadmap.** Origine: studio del report *"Architetture di Trading Algoritmico per Crypto e Forex: Dalla Logica Quantitativa all'Esecuzione Avanzata"* (PDF, 29 pagine, fornito il 202… |
| `docs/archive/ROADMAP-FRONTIERE-PROFITTO.md` | 356 | 2026-07-24 | **ProcioneMGR — Roadmap "Frontiere di Profitto": domande nuove per una macchina diventata on**<br/>*2026-07-23. Quarta roadmap di metodo, scritta a valle della chiusura di ROADMAP-MACCHINA-RICERCA. |
| `docs/archive/ROADMAP-K8S-FASE0.md` | 307 | 2026-07-08 | **ProcioneMGR — Roadmap Kubernetes, Fase 0: Fondamenti e Valutazione Preparatoria**<br/>**Da un PDF generico ("Da Monolito a Cloud-Native... Roadmap Pragmatica per l'Orchestrazione di |
| `docs/archive/ROADMAP-K8S-FASE1.md` | 231 | — | **ProcioneMGR — Roadmap Kubernetes, Fase 1: Modernizzazione del Backend e Containerizzazione**<br/>**Continuazione di `docs/ROADMAP-K8S-FASE0.md`** — stessa disciplina: ogni raccomandazione del PDF |
| `docs/archive/ROADMAP-K8S-FASE2.md` | 215 | — | **ProcioneMGR — Roadmap Kubernetes, Fase 2: Orchestrazione su Kubernetes e Gestione Infrastr**<br/>**Continuazione di `docs/ROADMAP-K8S-FASE1.md`** — la Fase 1 ha progettato il Dockerfile, il Job di |
| `docs/archive/ROADMAP-K8S-FASE3.md` | 189 | — | **ProcioneMGR — Roadmap Kubernetes, Fase 3: Osservabilità e Monitoraggio Avanzato**<br/>**Continuazione di `docs/ROADMAP-K8S-FASE0/1/2.md`** — il Deployment che gira su `kind` |
| `docs/archive/ROADMAP-K8S-FASE4.md` | 179 | — | **ProcioneMGR — Roadmap Kubernetes, Fase 4: Automazione, CI/CD e Autoscaling Intelligente**<br/>**Continuazione di `docs/ROADMAP-K8S-FASE0/1/2/3.md`** — a questo punto esistono (su carta: Dockerfile |
| `docs/archive/ROADMAP-K8S-FASE5.md` | 173 | — | **ProcioneMGR — Roadmap Kubernetes, Fase 5: Frontend e Integrazione Finale**<br/>**Continuazione di `docs/ROADMAP-K8S-FASE0/1/2/3/4.md`** — chiude la struttura a 6 fasi del PDF |
| `docs/archive/ROADMAP-MACCHINA-RICERCA.md` | 493 | 2026-07-23 | **ProcioneMGR — Roadmap "Macchina di Ricerca": spremere i dati già in casa**<br/>*2026-07-20. Terza roadmap di metodo dopo ML4T e QLIB. Ogni claim "esiste/manca" è stato verificato |
| `docs/archive/ROADMAP-ML4T.md` | 1007 | 2026-07-02 | **ProcioneMGR — Roadmap ML4T**<br/>**Da "Machine Learning for Algorithmic Trading" (S. Jansen, 2ª ed.) a ProcioneMGR in C#** |
| `docs/archive/ROADMAP-OPERATIVA.md` | 132 | 2026-07-05 | **ProcioneMGR — Roadmap operativa (autonomia)**<br/>Data: 2026-07-05. Stato: piattaforma su PostgreSQL, 3 corsie in Paper (ATOM/DOGE/SHIB 4h), |
| `docs/archive/ROADMAP-PROFITTO-INTRADAY-2026-07.md` | 339 | 2026-07-24 | **ProcioneMGR — Roadmap "Profitto Intraday": cambiare classe di edge, non fare più tentativi**<br/>*2026-07-24. Quinta roadmap di metodo. Nasce da una domanda diretta dell'utente — "voglio di più, |
| `docs/archive/ROADMAP-QLIB.md` | 476 | — | **ProcioneMGR — Roadmap "prestiti da Qlib"**<br/>**Da microsoft/qlib a ProcioneMGR in C#** — otto idee proposte dall'utente, verificate contro lo |
| `docs/archive/ROADMAP-RENDIMENTO-2026-07.md` | 122 | 2026-07-25 | **Roadmap Rendimento — attuazione del §10 della revisione (2026-07-25)**<br/>**Settima roadmap**, la più corta: quattro azioni, tutte già motivate e quantificate dalla |
