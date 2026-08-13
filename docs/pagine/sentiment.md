# Sentiment — `/sentiment`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Sentiment.razor`](../../ProcioneMGR/Components/Pages/Sentiment.razor) (~860 righe, la pagina più grande) |
| **Route** | `/sentiment` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Porta nella piattaforma i **dati alternativi**: notizie (CoinDesk, Cointelegraph, The Block,
Decrypt, FXStreet, calendario economico), posizionamento retail e — con **Sentiment 2.0** —
il **market mood composito** (Fear & Greed + metriche dai futures Binance). Il filo
conduttore è empirico: non "cosa dicono le notizie", ma **se e quanto hanno davvero mosso il
prezzo** in passato.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 30–69 | Categorie, punteggio sentiment (keyword-based, dichiaratamente semplice), lettura contrarian del retail |
| **Market mood (composite)** | 72–140 | Fear & Greed (valore, label, Δ7g), **mood composite [-1,+1]** (z-score compositi), news 24h, card per simbolo con funding (z), long/short ratio (z), taker z, ΔOI 24h; **alert "Estremi (lettura contrarian)"**; bottone Ricalcola |
| **Salute delle fonti** | 143–163 | Badge verde/rosso per fonte (registro in-memory, tooltip con dettaglio errore); "una fonte giù non ferma le altre" |
| **Profondità delle serie-patrimonio** | — | [2026-08-13] Tabella misurato-contro-dichiarato per le tre serie ESENTI dalla purge (funding per simbolo, Fear & Greed, liquidazioni): storia da / punti / soglia attesa / stato (OK, VIOLATA col perché, o «NON SORVEGLIATA» se l'interruttore delle liquidazioni è spento — mai un OK finto), badge rosso «N sotto soglia» nell'header, bottone «Controlla ora», avviso se il guardiano è spento. Il funding dal 2019 è sparito due volte in silenzio: questa card è il posto dove la prossima perdita si vede. Verificata sul DB vero il 2026-08-13: 6 funding OK dal listing, F&G OK dal 2018, liquidazioni ASSENTE (accumulo mai partito: stream EEA muto per MiCA) |
| 1. Sincronizza notizie | 165–189 | Fonti configurate come badge, sync manuale immediata (il worker fa quella periodica; cadenze/toggle in /admin/autonomy) |
| Notizie ingerite | 191–258 | Ultime 200 con filtri categoria/fonte, badge colorati per categoria, punteggio, link |
| Sentiment retail | 260–290 | Barre long/short per simbolo/fonte (gauge contrarian) |
| 2. Valuta il fattore Sentiment | 292–423 | Il sentiment come **fattore alpha**: IC Spearman, Pearson, IR, consistenza, spread top-bottom, rendimenti per quantile, **decadimento dell'IC per orizzonte**; warning esplicito se < 30 osservazioni |
| **3. Scorer del sentiment** | — | [Fase B + pilota ONNX, 2026-08-01] Scorer ATTIVO (Keyword/Llm/Onnx, hot-reload, default Keyword = zero costi), **addestramento del pilota ONNX** (distillazione del lessico con parità come gate di pubblicazione), **confronto A/B/C**: le notizie storiche rigiocate con ciascuno scorer e giudicate con lo stesso IC della sezione 2, più la lista dei disaccordi |
| 4. Analisi di impatto storico | 425–542 | Movimento del prezzo a 1h/4h/24h dal timestamp di ogni notizia, aggregato per categoria e fonte; **confronto incrociato FXSSI vs MyFxBook** (concordi long / concordi short / in disaccordo) |

## Come funziona (flusso del codice)

### Market mood — Sentiment 2.0 (righe 610–630)
All'avvio il mood viene dalla **cache** (`SentimentSnapshotCache.Current`), popolata dal
`SentimentSyncWorker` a ogni ciclo; "Ricalcola" invoca `ISentimentSnapshotService.ComputeAsync`
on-demand. Il composite è uno **z-score composito** delle metriche (funding, long/short,
taker flow, OI) rispetto al loro storico in `SentimentMetricPoints`; gli **estremi** generano
letture contrarian esplicite ("rischio squeeze/svolta, mai da seguire ciecamente"). Le fonti
keyless: Fear & Greed di alternative.me + dati pubblici Binance USDS-M Futures (solo ~30
giorni di storico pubblico ⇒ il worker è default ON per accumularlo).

### Sync notizie — `SyncAsync` (righe 660–683)
`IAltDataSyncService.SyncAllAsync` interroga tutte le `IAltDataSource` registrate
(RSS crypto, ForexFactory, retail sentiment), classifica (`NewsImpactClassifier`), assegna
il punteggio con lo **scorer attivo** (`DelegatingSentimentScorer`: Keyword di default, Llm o
Onnx a scelta — ogni scorer non-lessicale ripiega da solo sul lessico se il suo canale manca)
e deduplica. Un elemento che non si riesce a scorare viene SALTATO e ritentato al giro dopo,
mai salvato con uno zero inventato. Il registro di salute si aggiorna a ogni sync.

### Scorer e confronto — sezione 3 (Fase B + pilota ONNX)
Lo scorer attivo si sceglie e salva qui (`Sentiment:ScorerProvider`, hot-reload). «Addestra
pilota ONNX» esegue `OnnxSentimentPilotService`: etichette deboli dal lessico → SDCA ML.NET →
export ONNX potato al solo "Score" → **parità ML.NET↔ONNX Runtime come gate** (oltre 1e-3 il
file si elimina). «Confronta gli scorer» rigioca le notizie storiche (tetto configurabile: il
costo LLM è dichiarato prima, non scoperto dalla bolletta; batch da 20 titoli per chiamata)
attraverso Keyword/Llm/Onnx e valuta OGNI scorer con lo **stesso** `FactorEvaluator` della
sezione 2 — stesse notizie, stesse candele, stesso giudice. La tabella dei «disaccordi più
grandi» mostra DOVE gli scorer leggono la stessa notizia in modo diverso. Il pannello
confronta, non promuove: il gate per l'uso live resta l'IC OOS oltre i costi.

### Fattore sentiment — `EvaluateAsync` (righe 685–746)
Costruisce un `SentimentAlphaFactor` dalle notizie punteggiate (filtrate per l'asset base
del symbol, es. "BTC"), con parametro `LookbackHours`: il valore del fattore a ogni candela
è la media del sentiment recente. Poi `IFactorEvaluator.Evaluate` — **la stessa macchina di
valutazione dei fattori tecnici** — produce IC/IR/quantili/decay. Il warning sotto le 30
osservazioni spiega che è un limite dei dati accumulati, non della logica.

### Impatto storico — `AnalyzeImpactAsync` (righe 748–802)
`INewsImpactAnalyzer.Analyze(symbol, allNews, candles)`: per ogni notizia misura i
rendimenti del **simbolo di riferimento** nelle finestre [t, t+1h/4h/24h] e aggrega per
categoria/fonte; per il retail confronta i casi in cui FXSSI e MyFxBook concordano (±70%)
vs divergono. Il footer chiarisce il limite: il rendimento misurato è del simbolo con
storico prezzi in piattaforma, non della coppia forex della notizia.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `SentimentSnapshotService` / `SentimentSnapshotCache` | Calcolo/cache dello snapshot di mood | [`Services/Sentiment/SentimentSnapshotService.cs`](../../ProcioneMGR/Services/Sentiment/SentimentSnapshotService.cs) |
| `SentimentCompositeCalculator` | Z-score compositi ed estremi contrarian | [`Services/Sentiment/SentimentCompositeCalculator.cs`](../../ProcioneMGR/Services/Sentiment/SentimentCompositeCalculator.cs) |
| `FearGreedClient` / `BinanceFuturesSentimentClient` | Le fonti keyless delle metriche | [`Services/Sentiment/Metrics/`](../../ProcioneMGR/Services/Sentiment/Metrics) |
| `SentimentSyncWorker` | Sync periodica di notizie + metriche (default ON) | [`Services/Sentiment/SentimentSyncWorker.cs`](../../ProcioneMGR/Services/Sentiment/SentimentSyncWorker.cs) |
| `SentimentSourceHealthRegistry` | Salute per-fonte in-memory | [`Services/Sentiment/SentimentSourceHealthRegistry.cs`](../../ProcioneMGR/Services/Sentiment/SentimentSourceHealthRegistry.cs) |
| `SentimentHeritageGuardWorker` + `SentimentHeritageSnapshot` | Guardiano di profondità delle serie-patrimonio (soglie in `Sentiment:HeritageGuard`, allarme su transizione, fotografia per Home e per la card) | [`Services/Sentiment/SentimentHeritageGuard.cs`](../../ProcioneMGR/Services/Sentiment/SentimentHeritageGuard.cs) |
| `IAltDataSyncService` + `IAltDataSource` | Ingestione notizie multi-fonte | [`Services/AltData/AltDataSyncService.cs`](../../ProcioneMGR/Services/AltData/AltDataSyncService.cs) |
| `KeywordSentimentScorer` | Punteggio [-1,+1] keyword-based (fallback universale) | [`Services/Sentiment/KeywordSentimentScorer.cs`](../../ProcioneMGR/Services/Sentiment/KeywordSentimentScorer.cs) |
| `LlmSentimentScorer` | Punteggio via provider AI attivo (guard "sentiment", batch, fallback interno) | [`Services/Sentiment/LlmSentimentScorer.cs`](../../ProcioneMGR/Services/Sentiment/LlmSentimentScorer.cs) |
| `OnnxSentimentScorer` + `HashingTextVectorizer` | Inferenza locale del pilota (CPU, in-process) | [`Services/Sentiment/OnnxSentimentScorer.cs`](../../ProcioneMGR/Services/Sentiment/OnnxSentimentScorer.cs) |
| `DelegatingSentimentScorer` | Instradamento hot-reload sullo scorer attivo | [`Services/Sentiment/DelegatingSentimentScorer.cs`](../../ProcioneMGR/Services/Sentiment/DelegatingSentimentScorer.cs) |
| `OnnxSentimentPilotService` | Addestra/esporta/verifica il pilota ONNX | [`Services/Sentiment/OnnxSentimentPilotService.cs`](../../ProcioneMGR/Services/Sentiment/OnnxSentimentPilotService.cs) |
| `SentimentScorerComparisonService` | Confronto A/B/C sugli stessi IC | [`Services/Sentiment/SentimentScorerComparisonService.cs`](../../ProcioneMGR/Services/Sentiment/SentimentScorerComparisonService.cs) |
| `SentimentAlphaFactor` | Le notizie come fattore alpha valutabile | [`Services/Sentiment/SentimentAlphaFactor.cs`](../../ProcioneMGR/Services/Sentiment/SentimentAlphaFactor.cs) |
| `IFactorEvaluator` | Valutazione IC standard dei fattori | [`Services/Alpha/FactorEvaluator.cs`](../../ProcioneMGR/Services/Alpha/FactorEvaluator.cs) |
| `INewsImpactAnalyzer` | Impatto storico per categoria/fonte + cross-source retail | [`Services/AltData/NewsImpactAnalyzer.cs`](../../ProcioneMGR/Services/AltData/NewsImpactAnalyzer.cs) |
| `SentimentFeatureFactor` (contesto) | Il sentiment come feature ML opt-in (default OFF) | [`Services/Sentiment/SentimentFeatureFactor.cs`](../../ProcioneMGR/Services/Sentiment/SentimentFeatureFactor.cs) |

## Dati letti / scritti

- **Legge**: `AltDataPoints` (notizie), `SentimentMetricPoints` (metriche di mood),
  `OhlcvData` (per valutazione/impatto).
- **Scrive**: `AltDataPoints` (sync), `SentimentMetricPoints` (via worker/ricalcolo),
  `UserPageConfigs`.

## Collegamenti con le altre pagine

- [Autonomia](admin-autonomy.md) — cadenze e toggle del worker Sentiment (card dedicata).
- [Pipeline](pipeline.md) — lo snapshot di mood entra nel run (badge "Sentiment" nella
  raccomandazione, alert in caso di estremi) e nel prompt advisory del supervisore.
- [ML Lab](ml.md) — il fattore sentiment come feature opt-in.

## Note di design

- Ogni numero "morbido" è accompagnato dal suo test duro: sentiment → IC; notizie → impatto
  misurato; mood → z-score su storico. La pagina insegna a non fidarsi dei propri stessi dati.
- Il composite è dichiarato "mood della folla": agli estremi va letto al contrario, e la UI
  lo ripete due volte.
- La salute delle fonti è volutamente in-memory: fotografa questo processo, non la storia.
