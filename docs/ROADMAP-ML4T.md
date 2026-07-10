# ProcioneMGR — Roadmap ML4T

**Da "Machine Learning for Algorithmic Trading" (S. Jansen, 2ª ed.) a ProcioneMGR in C#**

> Documento di architettura e pianificazione. Mappa ogni capitolo del libro (Python) sulle
> lacune concrete della piattaforma, sceglie le librerie C# equivalenti e definisce un piano
> di implementazione a fasi. Runtime scelto: **C# puro (ML.NET / TorchSharp)**, nessuna
> dipendenza Python in produzione.

---

## 0. Executive summary

ProcioneMGR è già una piattaforma di trading algoritmico matura e ben progettata: .NET 10 /
Blazor, EF Core + PostgreSQL, ingestion OHLCV multi-exchange (Binance, Bitget), motore di
backtesting event-driven, ottimizzazione walk-forward, discovery, ensemble regime-aware,
trading engine live/paper/testnet con safety checker, e già **ML.NET** in uso per il
rilevamento dei regimi via K-means. La base è solida e i pattern (Strategy, Factory,
anti-look-ahead, servizi scoped/singleton) sono coerenti.

Il libro di Jansen aggiunge, rispetto a questa base, quattro grandi blocchi che oggi mancano:

1. **Ricerca di alpha factor** — un framework per costruire, valutare (Information
   Coefficient, factor returns) e combinare segnali predittivi. Oggi la piattaforma ha
   *indicatori* ma non *fattori alpha* con valutazione statistica. È la fondazione di tutto
   il resto.
2. **Modelli ML supervisionati per la previsione dei rendimenti** — lineari regolarizzati,
   Random Forest, Gradient Boosting — con cross-validation temporale corretta
   (purged/embargoed) e collegamento predizione → segnale → backtest.
3. **NLP / sentiment + autonomia LLM** — dati testuali (news, on-chain, social), sentiment,
   e un *layer di agenti LLM* (non presente nel libro, pubblicato nel 2020) che rende la
   piattaforma davvero autonoma: ragiona su regime + performance, propone/ottimizza
   strategie, decide allocazioni con supervisione umana.
4. **Ottimizzazione di portafoglio, risk e deep RL** — mean-variance, risk parity, HRP,
   metriche di performance complete, e un agente di Deep Reinforcement Learning.

Il principio guida, ripreso dal libro stesso, è che **il valore non sta nel modello ma nella
disciplina del processo**: niente look-ahead, cross-validation temporale, controllo del
data-snooping, valutazione out-of-sample. La piattaforma già rispetta questi principi nel
backtest e nel regime detector; li estenderemo a ogni nuovo modulo.

---

## 1. Stato attuale della piattaforma (baseline)

| Area | Servizio esistente | Cosa fa oggi |
|---|---|---|
| Dati | `Ingestion/*`, `Exchanges/*` | Sync OHLCV, client Binance/Bitget firmati, symbol filters |
| Indicatori | `Indicators/TechnicalIndicatorsService` | EMA, RSI, MACD, Bollinger (decimal, allineati per indice) |
| Backtest | `Backtesting/BacktestEngine` + `IStrategy` | Event-driven, 5 strategie a regole, fee, equity curve, drawdown |
| Ottimizzazione | `Optimization/OptimizationEngine` | Grid search walk-forward, selezione in-sample Sharpe |
| Discovery | `Discovery/StrategyDiscoveryEngine` | Sweep (strategia × coppia × timeframe) + walk-forward |
| Ensemble | `Ensemble/EnsembleManager` + worker | Allocazione multi-strategia, rolling-Sharpe, regime-aware |
| Regime | `Regime/RegimeDetector` (ML.NET K-means) | Clustering di market features, silhouette, profili, retraining |
| Trading | `Trading/TradingEngine` + `SafetyChecker` | Live/paper/testnet, ordini, posizioni, SL/TP, audit log |
| Metriche | `Optimization/Statistics` | Sharpe annualizzato |
| Sicurezza | `Security/AesGcmEncryptionService` | Cifratura credenziali |

**Punti di forza da preservare:** il pattern `IStrategy` (init pre-calcola indicatori →
hot-loop O(1)), l'attenzione all'anti-look-ahead nel `MarketFeatureExtractor`, la selezione
in-sample nell'ottimizzazione, la factory senza reflection.

**Le nuove capacità si innesteranno su questi punti di aggancio**, senza riscrivere:
- nuove strategie ML → nuove `IStrategy` che consumano predizioni di modello;
- nuovi fattori → alimentano sia le feature dei modelli sia i profili di regime;
- nuove metriche → estendono `Statistics`;
- nuovi allocatori → estendono/affiancano `EnsembleAllocator`.

---

## 2. Stack tecnologico C# scelto

Runtime **C# puro**. Pacchetti NuGet previsti (da introdurre solo quando la fase relativa
inizia, per non appesantire il build):

| Pacchetto | Uso | Fase |
|---|---|---|
| `Microsoft.ML` (già presente, 5.0.0) | Regressioni, FastTree/FastForest, LightGBM, PCA, metriche | 1–3 |
| `Microsoft.ML.LightGbm` | Gradient boosting (equivalente XGBoost/LightGBM del libro) | 3 |
| `MathNet.Numerics` | Algebra lineare, statistica, regressione, ottimizzazione QP | 1–4 |
| `TorchSharp` (+ `libtorch` runtime) | Deep learning: MLP, CNN, LSTM, autoencoder, GAN, RL | 5 |
| `TorchSharp.PyBridge` *(opz.)* | Import di pesi PyTorch/`.pt` addestrati offline, se serve | 5 |
| `Microsoft.SemanticKernel` **o** SDK REST diretto | Orchestrazione agenti LLM, tool-calling, embeddings | 4 |
| `System.Text.Json` (già in uso) | Serializzazione modelli/feature (coerente con l'esistente) | tutte |

Note di scelta:
- **ML.NET copre nativamente** i capitoli 6–8 e 11–13 (lineari, RF, boosting, PCA, K-means).
  È già nel progetto: massima coerenza, zero attrito.
- **TorchSharp** è la libreria .NET ufficiale su libtorch: è l'unica via realistica in C#
  puro per i capitoli 17–22 (DL/RNN/CNN/autoencoder/GAN/RL). Aggiunge un runtime nativo
  (~alcune centinaia di MB) ma resta *in-process*, senza Python.
- **GARCH / ARIMA (cap. 9)** non hanno una libreria C# di prima classe: si implementano su
  `MathNet.Numerics` (MLE per GARCH(1,1), OLS/Levinson-Durbin per ARIMA). Volume/complessità
  contenuti, dettaglio in §3.9.
- **LLM (layer di autonomia)**: consigliato `Microsoft.SemanticKernel` per orchestrazione,
  tool-calling e memoria; in alternativa chiamate REST dirette all'API del provider. Le
  credenziali passano dal già presente `AesGcmEncryptionService`.

---

## 3. Mappatura capitolo → piattaforma

Legenda priorità: **P1** fondamenta (sblocca il resto) · **P2** alto valore · **P3**
avanzato/opzionale. "Copertura" = quanto è già presente in ProcioneMGR.

### Quadro sintetico

| Cap. | Titolo (breve) | Copertura oggi | Da aggiungere | Libreria C# | Priorità |
|---|---|---|---|---|---|
| 1 | ML4T: dall'idea all'esecuzione | Concettuale | — (allineamento processo) | — | — |
| 2 | Market & fundamental data | OHLCV completo | Barre volume/dollar, order book*, on-chain crypto | nativo/EF | P2 |
| 3 | Alternative data | Assente | Connettori news/social/on-chain, storage | HttpClient/EF | P2 |
| 4 | **Alpha factor research** | Solo indicatori | **`Alpha/` factor library + valutazione (IC, factor returns)** | MathNet/ML.NET | **P1** |
| 5 | Portfolio optimization & perf | Sharpe, rolling-Sharpe | MPT/mean-var, risk parity, HRP, metriche complete | MathNet | P1/P2 |
| 6 | Il processo ML | Assente (per ML) | Pipeline ML, purged/embargoed CV, dataset builder | ML.NET | **P1** |
| 7 | Modelli lineari | Assente | Ridge/Lasso/Logistic per return forecast | ML.NET | P2 |
| 8 | ML4T workflow | Assente | `MlStrategy`: predizione → segnale → backtest | proprio | **P1** |
| 9 | Time-series (vol, stat-arb) | Assente | GARCH vol forecast, cointegrazione, pairs trading | MathNet | P2 |
| 10 | Bayesian ML | Assente | Sharpe dinamico, pairs bayesiano | MathNet | P3 |
| 11 | Random Forests | Assente | RF long-short su fattori | ML.NET FastForest | P2 |
| 12 | Boosting | Assente | LightGBM su fattori (il "cavallo di battaglia") | ML.NET LightGbm | **P2** |
| 13 | Unsupervised: risk factors | K-means (regimi) | PCA risk factors, clustering gerarchico | ML.NET/MathNet | P2 |
| 14 | Text data / sentiment | Assente | Pipeline sentiment (LLM-based) | SemanticKernel/LLM | **P2** |
| 15 | Topic modeling | Assente | Sintesi/temi news (LLM al posto di LDA) | LLM | P3 |
| 16 | Word embeddings | Assente | Embeddings (LLM) per similarità/ricerca semantica | LLM/ML.NET | P3 |
| 17 | Deep learning | Assente | MLP feed-forward su fattori | TorchSharp | P3 |
| 18 | CNN time series | Assente | CNN 1D su finestre di prezzo | TorchSharp | P3 |
| 19 | RNN / LSTM | Assente | LSTM per previsione multivariata | TorchSharp | P3 |
| 20 | Autoencoders | Assente | AE per risk factor / denoising | TorchSharp | P3 |
| 21 | GAN (dati sintetici) | Assente | TimeGAN per stress test / data augmentation | TorchSharp | P3 |
| 22 | **Deep RL trading agent** | Assente | Env di trading + agente (DQN/PPO) | TorchSharp | P3 |
| 23 | Conclusioni | — | — | — | — |
| 24 | Appendice: alpha factor library | Solo indicatori | Catalogo fattori (alimenta cap. 4) | MathNet | **P1** |

\* Order book / tick data: rilevante solo se si vuole microstruttura/HFT; per lo swing/pos
trading crypto attuale è opzionale.

Segue il dettaglio dei capitoli che comportano lavoro reale.

### 3.2 — Market & fundamental data (P2)
Il libro tratta la costruzione di barre non temporali (tick/volume/dollar bars) per
de-rumorizzare i dati e le fonti fondamentali (SEC/XBRL). Per una piattaforma crypto:
- **Volume bars / dollar bars**: nuovo `Ingestion/BarBuilder` che aggrega gli OHLCV di base
  in barre a volume/dollari costanti — riducono l'eteroschedasticità e migliorano i modelli.
- **On-chain come "fondamentali" crypto**: al posto di XBRL, metriche on-chain (active
  addresses, exchange flows, funding rate). Nuovo `TrackedSeries` già esiste come contenitore
  generico → estenderlo per serie non-OHLCV.

### 3.3 — Alternative data (P2)
Nuovo namespace `Services/AltData/` con `IAltDataSource` (stesso spirito di `IExchangeClient`):
connettori per news finanziarie, funding/derivati, sentiment social. Storage in una nuova
entità `AltDataPoint` (timestamp, source, symbol, key, value/testo). Alimenta cap. 14.

### 3.4 — Alpha factor research (P1, fondazione)
**Il modulo più importante.** Oggi mancano sia una *libreria di fattori* sia la loro
*valutazione*. Nuovo namespace `Services/Alpha/`:
- `IAlphaFactor` — interfaccia analoga a `IStrategy` ma che, dato un set di serie, produce un
  valore numerico per candela (allineato per indice, anti-look-ahead per costruzione).
- `AlphaFactorLibrary` — catalogo (momentum multi-orizzonte, mean-reversion, volatilità,
  volume, RSI/MACD "fattorizzati", ecc.) — vedi anche cap. 24.
- `FactorEvaluator` — calcola l'**Information Coefficient** (correlazione di Spearman fattore
  vs rendimento forward), **factor returns** per quantili, decadimento dell'IC, turnover.
  È la controparte C# di Alphalens.
- Persistenza: `SavedFactor` / valutazioni, riuso in backtest e nelle feature dei modelli ML.

Questo modulo è il **ponte** tra indicatori (già presenti) e modelli ML (cap. 6–12): i
fattori diventano le *feature*.

### 3.5 — Portfolio optimization & performance evaluation (P1/P2)
Due parti:
- **Metriche (P1, rapido)**: estendere `Optimization/Statistics` con Sortino, Calmar, Omega,
  tail ratio, VaR/CVaR storico, max drawdown duration, esposizione, hit-rate — così ogni
  backtest/ensemble ha un "tearsheet" completo (controparte di pyfolio).
- **Allocatori (P2)**: nuovo `Services/Portfolio/` con `IPortfolioOptimizer` e implementazioni
  Mean-Variance (Markowitz, QP con `MathNet`), **Risk Parity**, **HRP** (Hierarchical Risk
  Parity, clustering gerarchico + bisezione — non richiede inversione di matrice, robusto).
  Si affiancano all'`EnsembleAllocator` esistente come strategie di pesatura selezionabili.

### 3.6 — Il processo ML (P1, fondazione)
Nuovo namespace `Services/ML/` con l'infrastruttura condivisa da tutti i modelli:
- `DatasetBuilder` — da (fattori + target forward-return) a `IDataView` di ML.NET, con
  gestione dei timestamp e degli allineamenti.
- `PurgedTimeSeriesCV` — cross-validation temporale con **purging** ed **embargo** (López de
  Prado) per evitare leakage tra train e test su serie con overlap: fondamentale e assente in
  ML.NET di default.
- `IReturnPredictor` — astrazione comune (Fit/Predict/Persist) implementata da lineari, RF,
  boosting, DL. Persistenza modelli come artefatti versionati (riuso del pattern `RegimeModel`).

### 3.7 — Modelli lineari (P2)
`LinearReturnPredictor` su ML.NET (`Sdca`, regolarizzazione L1/L2) per regressione dei
rendimenti forward e classificazione up/down. Baseline interpretabile prima dei modelli
complessi.

### 3.8 — ML4T workflow (P1)
Il collante: `Backtesting/MlStrategy : IStrategy`. In `InitializeAsync` carica un
`IReturnPredictor` addestrato e pre-calcola i fattori; in `EvaluateSignal` traduce la
predizione in `Signal` (soglie long/short, sizing per confidenza). Così **ogni modello ML
diventa immediatamente back-testabile, ottimizzabile e inseribile nell'ensemble** riusando
tutta l'infrastruttura esistente. Questo è il punto di aggancio chiave dell'intero libro.

### 3.9 — Time-series: volatilità e statistical arbitrage (P2)
- **GARCH(1,1)** su `MathNet` (stima MLE) per la previsione di volatilità → position sizing
  dinamico e stop adattivi.
- **Cointegrazione / pairs trading**: test di Engle-Granger (OLS + ADF), z-score dello
  spread, nuova `PairsTradingStrategy : IStrategy` (opera su due simboli). Il backtest engine
  va esteso per gestire strategie multi-simbolo (oggi è single-symbol).

### 3.11–3.12 — Random Forests & Boosting (P2)
`RandomForestReturnPredictor` (ML.NET `FastForest`) e `GradientBoostingReturnPredictor`
(ML.NET `LightGbm`). Il boosting è, nel libro, il modello con il miglior rapporto
performance/sforzo per i dati tabellari di fattori: **priorità alta tra i modelli**. Include
**feature importance** per capire quali fattori contano davvero.

### 3.13 — Unsupervised: risk factors & asset allocation (P2)
- **PCA** (ML.NET) sui rendimenti per estrarre risk factor statistici e per de-correlare le
  feature prima dei modelli.
- **Clustering gerarchico** riusato dall'HRP (§3.5). Il K-means dei regimi già esistente è la
  prova che l'infrastruttura unsupervised in ML.NET funziona bene qui.

### 3.14–3.16 — Testo, sentiment, topic, embeddings (P2/P3) — *qui entra l'LLM*
Invece di riprodurre LDA/word2vec del 2020, si usa un LLM moderno:
- **Sentiment (P2)**: `Services/Nlp/SentimentAnalyzer` che, dato un batch di news/post,
  restituisce sentiment strutturato per simbolo (via LLM con output JSON). Diventa un fattore
  alpha (`SentimentFactor`) e/o una feature di regime.
- **Sintesi/temi (P3)**: digest automatici delle news rilevanti per la watchlist.
- **Embeddings (P3)**: ricerca semantica su news/eventi, clustering di notizie simili.

### 3.17–3.22 — Deep Learning, CNN, RNN, Autoencoder, GAN, Deep RL (P3, TorchSharp)
Blocco avanzato, tutto su **TorchSharp**, ognuno come `IReturnPredictor` o modulo dedicato:
- **MLP / CNN 1D / LSTM**: predittori su fattori o su finestre di prezzo grezze.
- **Autoencoder**: risk factor non lineari, denoising, anomaly detection sui regimi.
- **GAN (TimeGAN)**: generazione di serie sintetiche per stress test e per aumentare i dati
  di training/backtest (utile contro l'overfitting su una sola storia di prezzi).
- **Deep RL (cap. 22, il più ambizioso)**: `Services/Rl/` con un `TradingEnvironment`
  (stato = fattori + posizione; azioni = long/flat/short; reward = PnL netto - costi) e un
  agente DQN/PPO in TorchSharp. È il tassello più vicino all'"autonomia algoritmica" nel
  senso del libro; va trattato come progetto a sé, dopo che fattori e backtest sono solidi.

### 3.24 — Appendice: alpha factor library (P1)
Catalogo di riferimento dei fattori da implementare in `Services/Alpha/` (§3.4): momentum
(diversi orizzonti e skip), mean-reversion, volatilità (realizzata, Parkinson, Garman-Klass),
volume/liquidità, fattori tecnici normalizzati. Guida diretta all'implementazione del cap. 4.

---

## 4. Il layer di autonomia LLM (estensione oltre il libro)

Il libro si ferma al 2020: gli "LLM ed ecc." che vuoi sono un'aggiunta progettata su misura.
L'idea è un **layer di agenti che orchestra i moduli sottostanti**, senza mai scavalcare le
safety già presenti nel `TradingEngine`. Architettura proposta in `Services/Agents/`:

- **`MarketAnalystAgent`** — legge regime corrente, sentiment (§3.14) e performance recente,
  produce un brief testuale + segnali qualitativi.
- **`StrategyResearchAgent`** — propone nuove combinazioni (strategia/fattori/parametri) da
  passare a Discovery/Optimization *esistenti*; l'LLM genera ipotesi, il backtest le valida
  (l'LLM non decide "a sentimento": ogni proposta è verificata dai numeri).
- **`AllocationAdvisorAgent`** — suggerisce ribilanciamenti all'`EnsembleManager`, motivandoli.
- **`RiskGuardianAgent`** — sorveglia drawdown/anomalie e può *proporre* un de-risking
  (l'esecuzione resta soggetta a `SafetyChecker` e, per il Live, alla conferma manuale già
  prevista da `ConfirmOrderAsync`).

Principi non negoziabili del layer LLM:
1. **L'LLM propone, i backtest dispongono.** Ogni decisione operativa passa da validazione
   quantitativa e dalle safety esistenti.
2. **Tool-calling verso i servizi reali** (backtest, discovery, metriche), non verso azioni
   dirette sull'exchange.
3. **Human-in-the-loop sul Live** già garantito dall'architettura ordini attuale.
4. **Tracciabilità**: ogni azione dell'agente loggata (riuso di `TradingAuditLog`).

---

## 5. Piano a fasi

Ogni fase è autoconsistente, testabile (riuso di `ProcioneMGR.Tests`) e porta valore da sola.

**Fase A — Fondamenta quantitative (P1)**
`Services/Alpha/` (factor library + `FactorEvaluator`/IC), estensione di `Statistics` con le
metriche complete, `Services/ML/` (DatasetBuilder + PurgedTimeSeriesCV + `IReturnPredictor`),
e `MlStrategy` per collegare le predizioni al backtest. Alla fine di A puoi già addestrare un
modello e back-testarlo end-to-end.

**Fase B — Modelli supervisionati (P2)**
Lineari → Random Forest → **LightGBM** su ML.NET, con feature importance; integrazione in
Optimization/Discovery/Ensemble. PCA e clustering (cap. 13) per risk factor e de-correlazione.

**Fase C — Portfolio, risk e time-series (P2)**
`Services/Portfolio/` (Mean-Variance, Risk Parity, HRP), GARCH per vol forecast e sizing
dinamico, pairs trading (con estensione multi-simbolo del backtest).

**Fase D — NLP/sentiment + layer LLM (P2)**
Connettori alt-data/news, `SentimentAnalyzer`, `SentimentFactor`, poi gli agenti di §4 con
tool-calling verso i servizi esistenti. È qui che la piattaforma diventa "autonoma".

**Fase E — Deep learning & RL (P3)**
TorchSharp: MLP/CNN/LSTM come predittori, autoencoder per risk factor, GAN per dati
sintetici, e infine l'agente di Deep RL con il suo `TradingEnvironment`.

**Ordine consigliato:** A → B → (C ∥ D) → E. Le fasi C e D sono parallelizzabili; E richiede
che A/B siano consolidate.

---

## 6. Principi trasversali (dal libro, da applicare ovunque)

- **Anti-look-ahead**: ogni feature/fattore usa solo dati fino a `t` (già lo standard nel
  `MarketFeatureExtractor`; estenderlo a ogni nuovo fattore, con test di troncamento).
- **Cross-validation temporale con purging/embargo**: mai CV casuale su serie storiche.
- **Selezione in-sample, misura out-of-sample**: già il default in `OptimizationEngine`.
- **Controllo del data-snooping**: penalizzare test multipli (deflated Sharpe), diffidare dei
  risultati "troppo belli" della Discovery.
- **Costi realistici**: fee/slippage già nel backtest; mantenerli nei modelli ML.
- **Versionamento dei modelli**: ogni artefatto (predictor, factor set) tracciato come già
  fatto per `RegimeModel`.

---

## 7. Prossimo passo operativo

Il primo modulo da implementare è la **Fase A / `Services/Alpha/`** (factor library +
`FactorEvaluator`), perché sblocca modelli, ensemble e layer LLM. Su tua conferma parto da lì:
interfacce (`IAlphaFactor`, `IFactorEvaluator`), 6–8 fattori dall'appendice cap. 24, il
calcolo dell'Information Coefficient, e i relativi test in `ProcioneMGR.Tests` — tutto nello
stile e nei pattern già presenti nel progetto.

---

## 8. Stato di avanzamento

**Fase A = COMPLETA (2026-07-01).** 116/116 test xUnit passano, build pulita.

- `Services/Alpha/` — 8 fattori (Momentum, MeanReversion, RealizedVol, ParkinsonVol,
  RelativeVolume, RsiFactor, MacdFactor, DistanceFromMa), `AlphaFactorFactory` (pattern
  `StrategyFactory`), `FactorEvaluator` (IC via Spearman, IR, quantile returns, IC decay).
  Anti-look-ahead verificato per troncamento su tutti i fattori.
- `Statistics.cs` esteso con tearsheet completo: Sortino, Calmar, Omega, Tail Ratio,
  VaR/CVaR storici, max drawdown duration, exposure, hit-rate, `ComputeTearsheet(...)`.
- `Services/ML/` — `IDatasetBuilder`/`DatasetBuilder` (fattori + forward return → righe
  allineate, scarto warm-up/coda), `IPurgedTimeSeriesCv`/`PurgedTimeSeriesCv` (K-fold con
  bande di purge/embargo, López de Prado), `IReturnPredictor` (Fit/Predict/Save/Load),
  `LinearReturnPredictor` (ML.NET SDCA + normalizzazione — necessaria, SDCA è instabile su
  feature a scala piccola non normalizzate).
- `Backtesting/MlStrategy` — collega un `IReturnPredictor` addestrato al backtest (soglie
  Long/Short/Close come `MomentumStrategy`).

**Deviazioni flaggate rispetto al piano originale:**
1. `IStrategy.InitializeAsync` esteso con un parametro `IReadOnlyList<OhlcvData> candles`
   (oltre a `closes`) — necessario perché i fattori alpha (volume, high/low) richiedono più
   del solo close. Cambio meccanico su interfaccia + 5 strategie esistenti + `BacktestEngine`
   + `TradingEngine` (tutte ignorano il nuovo parametro tranne `MlStrategy`); nessuna
   regressione (116/116 test).
2. `IBacktestEngine` ha un nuovo overload `RunBacktestAsync(config, candles, IStrategy, ct)`
   che accetta un'istanza già pronta invece del nome. Necessario perché `MlStrategy` richiede
   un predittore addestrato + la lista dei fattori (non rappresentabili come
   `Dictionary<string, decimal>`), quindi **non è registrata nello switch di
   `StrategyFactory`**. Al momento si usa costruendola direttamente in codice/test; la
   selezione di un modello persistito da UI (analoga a `RegimeModel`) resta un passo
   successivo, non ancora implementato.
3. Non ancora fatto (rimandato, non necessario per "modello addestrabile e back-testabile
   end-to-end"): persistenza di modelli/feature-set come artefatti versionati in DB,
   integrazione in Optimization/Discovery/Ensemble, UI dedicata (`/ml` o simile).

**UI dimostrativa `/ml` (2026-07-01, browser-verificata):** `MlLab.razor` (Admin/Manager) —
seleziona dati + fattori, addestra `LinearReturnPredictor` su uno split temporale Train/Test
(niente look-ahead: il modello vede solo il periodo Train), poi esegue il backtest reale
**fuori campione** sul periodo Test tramite `MlStrategy` + il nuovo overload di
`IBacktestEngine`. Mostra diagnostica di training (righe, correlazione in-sample), tearsheet
completo (Sharpe/Sortino/Calmar/Omega/VaR/CVaR/MaxDD-duration/Exposure), equity curve e trade
list. **Verificato in browser** su BTC/USDT 1h (~2900 candele, split 70/30): con soglie
larghe (0.5%) il modello non fa trade (segnale troppo debole, come atteso da una correlazione
in-sample di 0.027); abbassando le soglie a 0.005% produce 65 trade OOS con Sharpe -9.70 —
risultato onestamente negativo, che è esattamente il punto del test fuori campione: rivela
che 3 fattori grezzi + un modello lineare non hanno ancora un vero edge su BTC 1h, invece di
nascondere l'overfitting con un backtest in-sample. Nessuna persistenza del modello (si
riaddestra a ogni visita pagina, stato solo nel componente) — è una scelta consapevole per
restare nello scope della Fase A.

**Fase B = COMPLETA (2026-07-01).** 145/145 test xUnit passano, build pulita, verificata anche
in browser (Random Forest addestrato su BTC/USDT 1h: correlazione in-sample 0.409 contro 0.027
del lineare — coerente con l'attesa che gli alberi catturino meglio le non-linearità).

- **Refactor `Services/ML/`**: `RegressionPredictorBase` (classe astratta) centralizza
  Fit/Predict/Save/Load/ComputeFeatureImportance; `LinearReturnPredictor` ora è una
  sottoclasse minimale (solo `BuildPipeline`). Aggiunte `RandomForestReturnPredictor` (ML.NET
  FastForest, cap. 11) e `GradientBoostingReturnPredictor` (ML.NET LightGBM, cap. 12) — stesso
  pattern, nessuna normalizzazione (gli alberi sono invarianti alla scala).
- **Feature importance**: `IReturnPredictor.ComputeFeatureImportance` — permutation importance
  implementata a mano (non l'overload ML.NET `PermutationFeatureImportance`, che per un
  `ITransformer` generico tratta l'intera colonna vettoriale Features come UN solo "feature"
  invece che per singolo fattore — inutile qui). Per ogni fattore: mescola quello slot nel
  dataset di valutazione, rimisura R², ripete 5 volte, riporta media/deviazione standard del
  calo di R². Verificato su dataset sintetici con una feature informativa + una di rumore puro:
  la prima si classifica sempre più in alto, per il modello lineare e per Random Forest.
- **`RiskFactorPca`** (`Services/ML/`): PCA sui rendimenti standardizzati (matrice di
  correlazione) via eigen-decomposizione **MathNet.Numerics** — **deviazione dal piano**
  (indicava ML.NET): `ProjectToPrincipalComponents` non espone pubblicamente gli autovalori,
  necessari per la varianza spiegata per componente. MathNet dà accesso diretto e verificabile.
  Verificato su casi noti: 2 simboli identici → prima componente spiega ~100% varianza; 3
  simboli indipendenti → varianza ripartita, nessuna componente dominante.
- **`HierarchicalClustering`** (`Services/ML/`): agglomerative clustering (Lance-Williams,
  linkage single/complete/average) su una matrice di distanza, + `CorrelationDistance` (distanza
  di Mantegna `sqrt(0.5*(1-corr))`). Costruisce solo il dendrogramma — sarà riusato da HRP
  (Hierarchical Risk Parity) in Fase C per la quasi-diagonalizzazione e la bisezione dei pesi.
- **UI `/ml` estesa**: selettore modello (Lineare/Random Forest/Gradient Boosting) + tabella di
  feature importance dopo l'addestramento. Verificato in browser.
- Nuovi pacchetti NuGet: `Microsoft.ML.LightGbm`, `MathNet.Numerics` (quest'ultimo servirà
  anche per GARCH/cointegrazione/mean-variance nelle fasi successive, come da piano §2).

**Persistenza dei modelli ML = FATTA (2026-07-01), decisa con l'utente e browser-verificata.**
Entità EF `SavedMlModel` (`Data/SavedMlModel.cs`, migrazione `AddSavedMlModels`): a differenza
di `RegimeModel` (che salva solo i parametri numerici del K-means e reimplementa l'inferenza a
mano), qui si salva il **modello ML.NET già serializzato** (lo stesso blob prodotto da
`IReturnPredictor.Save`, `byte[]` in colonna) — decisione esplicita dell'utente: reimplementare
a mano l'inferenza di Random Forest/LightGBM (decine di alberi) sarebbe stato complesso e
rischioso, mentre il round-trip Save/Load è già testato per tutti e tre i modelli. Insieme al
blob si salvano: tipo di modello, symbol/timeframe, periodo di training, orizzonte forward,
fattori+parametri (JSON), e le metriche diagnostiche (righe, correlazione in-sample). UI in
`/ml`: tabella "Modelli salvati" con Carica/Elimina (stesso pattern di `SavedStrategy`), "Salva
modello" dopo l'addestramento. **Verificato in browser**: salva → reload pagina → la lista
persiste → Carica (nessun riaddestramento) → backtest funziona sul modello ricaricato →
Elimina. NOTA: caricando un modello salvato, l'intero intervallo Da/A in UI viene usato
direttamente come test out-of-sample (nessuno split train/test, il modello è già addestrato) —
l'utente deve scegliere un intervallo successivo al periodo di training originale (mostrato nel
messaggio di stato dopo il caricamento).

**NON ancora fatto (prossimo passo naturale, non blocca la Fase C)**: usare `SavedMlModel` per
integrare i modelli ML in Optimization/Discovery/Ensemble (che lavorano per nome-strategia +
parametri decimali — andrebbe aggiunto un modo per riferire un `SavedMlModel` per Id, es. un
parametro "ModelId" e un adattatore che costruisce `MlStrategy` da un record salvato). Stesso
discorso per il "clustering gerarchico riusato da HRP": l'algoritmo è pronto, l'uso concreto
(quasi-diagonalizzazione + bisezione dei pesi) arriva ora con HRP in Fase C.

**Fase C = COMPLETA (2026-07-01).** 172/172 test xUnit passano, build pulita.

- **`Services/Portfolio/`** — `IPortfolioOptimizer` con tre implementazioni:
  `MeanVarianceOptimizer` (Markowitz, soluzione analitica via `Σ⁻¹` risolto con MathNet invece
  di un QP iterativo — più stabile ed esatto per i due obiettivi supportati, MaxSharpe/
  MinVariance; niente inversione esplicita, si risolve il sistema lineare direttamente),
  `RiskParityOptimizer` (naive a volatilità inversa — **deviazione dichiarata**: la vera
  "equal risk contribution" richiede un solutore non lineare iterativo, l'inverse-vol è
  l'approssimazione standard usata in pratica), `HierarchicalRiskParityOptimizer` (HRP di López
  de Prado: correlazione → distanza di Mantegna → dendrogramma via `HierarchicalClustering` →
  bisezione ricorsiva con pesi a inverse-varianza per ramo — riuso diretto del clustering
  costruito in Fase B, come previsto). Tutti e tre riusano il water-filling vincolato già
  testato di `EnsembleAllocator` per i limiti Min/Max, invece di reimplementarlo.
- **`Services/TimeSeries/`** — `GarchModel` (GARCH(1,1), stima MLE via Nelder-Mead con
  riparametrizzazione libera ω=exp/α,β=sigmoid per rispettare sempre i vincoli di
  stazionarietà senza un solutore vincolato): verificato su processi GARCH simulati con
  parametri noti (persistenza recuperata entro ±0.15), volatility clustering, forecast con
  mean-reversion verso la varianza di lungo periodo. `EngleGrangerCointegrationTest` (OLS +
  ADF con costante) + `PairsSpreadAnalyzer` (z-score rolling causale sullo spread): verificato
  su coppie costruite per essere cointegrate (hedge ratio recuperato entro ±0.1 dal vero beta)
  e su random walk indipendenti (correttamente NON cointegrate).

**NON fatto in Fase C (deviazione dichiarata, richiede una decisione architetturale a sé,
come già successo per la persistenza dei modelli ML)**: `PairsTradingStrategy : IStrategy`
backtestabile. Il motivo è duplice: (1) l'hedge ratio in `PairsSpreadAnalyzer` è stimato UNA
VOLTA sull'intero campione (adatto allo *screening* di quali coppie sono cointegrate, non
ancora anti-look-ahead in senso stretto — servirebbe una ristima rolling/walk-forward); (2)
`IStrategy`/`IBacktestEngine` sono **single-symbol** by design (un solo array di candele), una
strategia di pairs trading ne richiede DUE sincronizzate con gestione di due gambe della
posizione — un'estensione del motore più grande del cambio già fatto in Fase A (che aggiunse
solo un parametro, non una seconda serie temporale intera). Gli strumenti statistici (test di
cointegrazione, hedge ratio, z-score) sono pronti e testati; il collegamento a un vero backtest
multi-simbolo resta un passo successivo esplicito, da decidere insieme come già fatto per la
persistenza dei modelli.

## 9. Integrazione SavedMlModel + motore multi-simbolo (2026-07-01)

Le due decisioni architetturali lasciate aperte sopra sono state affrontate su richiesta
esplicita dell'utente. **189/189 test xUnit passano**, build pulita, entrambe verificate in
browser.

### 9.1 — SavedMlModel in Optimization/Discovery/Ensemble

Punto di aggancio unico: `BacktestEngine.RunBacktestAsync(config, candles, ct)` ora risolve
`StrategyName="Ml"` caricando un `SavedMlModel` referenziato da
`StrategyParameters["SavedModelId"]` (ricostruisce il predittore giusto per `ModelType`,
carica il blob, ricrea i `FactorSpec` dal `FactorsJson`) — **nessun cambiamento** a
`OptimizationEngine`/`EnsembleManager`, che già passano solo `BacktestConfiguration` a
`IBacktestEngine`. Il "SavedModelId" viaggia come un `ParameterRange` pinnato (Min=Max=id) per
Optimization, sfruttando il meccanismo di sweep già esistente senza toccarne lo schema.

- **UI `/optimization`**: opzione "Modello ML (salvato)" nel selettore strategia → mostra un
  picker filtrato per symbol/timeframe correnti + range Long/Short threshold. Verificato in
  browser: 64 combinazioni, 1 finestra walk-forward, heatmap renderizzata correttamente.
- **UI `/ensemble`**: "+ Aggiungi modello ML" (stesso pattern di "+ Aggiungi ottimizzata"),
  filtrato per symbol/timeframe dell'ensemble (un modello ETH non ha senso su un ensemble
  BTC). Verificato in browser: rebalance/simulazione includono il modello ML con capitale e
  Sharpe propri, persistenza dopo reload confermata.
- **Discovery: deliberatamente NON integrato** — Discovery sweepa (strategia × MOLTI symbol ×
  timeframe), ma un `SavedMlModel` è per costruzione legato a UN symbol/timeframe specifico
  (i fattori sono calibrati su quella serie): non ha senso semantico provare un modello ETH su
  candele BTC nello stesso modo delle strategie a regole. L'Optimization già copre il caso
  d'uso reale ("ottimizza le soglie per QUESTO modello, su QUESTO symbol").
- **Bug reale scoperto e corretto**: `OptimizationEngine.ComboKey` formattava i parametri
  decimal con la cultura CORRENTE del thread (es. it-IT → virgola come separatore), mentre
  l'heatmap li fa il parsing assumendo InvariantCulture — con parametri decimali non interi
  (le soglie di `MlStrategy`) lo split per virgola si rompeva a metà di un numero, crash
  `IndexOutOfRangeException`. Bug latente da sempre (mai emerso: tutte le strategie a regole
  sweepano solo interi), corretto con `ToString(CultureInfo.InvariantCulture)` + test di
  regressione dedicato.
- Nota di performance: ogni chiamata a un modello ML ricarica il blob dal DB e ricostruisce la
  `PredictionEngine` di ML.NET (nessuna cache per `SavedModelId`) — accettabile per sweep
  modesti (poche soglie, poche finestre); da rivedere se servissero sweep massivi.

### 9.2 — Motore di backtest multi-simbolo per il pairs trading

`Services/PairsTrading/` — sotto-sistema **parallelo e indipendente**, non tocca
`IStrategy`/`IBacktestEngine`/`StrategyFactory` (deviazione consapevole: estendere il motore
single-symbol esistente al multi-simbolo avrebbe richiesto toccare l'interfaccia usata da ogni
strategia e chiamante; qui zero rischio di regressione).

- **`RollingPairsSpreadAnalyzer`**: a differenza di `PairsSpreadAnalyzer` (§8, hedge ratio
  stimato una volta sull'intero campione, adatto solo allo screening), qui l'hedge ratio è
  ristimato periodicamente in modo **walk-forward** (ogni `RecalibrationInterval` barre, OLS
  su una finestra di sole barre PASSATE) — anti-look-ahead corretto e verificato con la stessa
  invariante di troncamento usata per gli `IAlphaFactor`. Risolve esattamente la limitazione
  segnalata sopra ("l'hedge ratio vede l'intero campione").
- **`PairsCandleAligner`**: allinea due serie per timestamp (intersezione) — due simboli
  possono avere gap diversi, non si può assumere siano già sincronizzati indice-per-indice.
- **`PairsBacktestEngine`**: apre/chiude posizioni **dollar-neutral** (stesso notional sulle
  due gambe: Long Y/Short X o viceversa) in base allo z-score rolling, con commissioni per
  gamba, equity mark-to-market, determinismo verificato. Testato su coppie sintetiche con
  spread oscillante costruito ad hoc (genera trade verificabili) e su coppie con gap/nessuna
  sovrapposizione temporale (edge case).
### 9.3 — UI `/pairs-trading` e `/volatility` (2026-07-01, browser-verificate)

- **`/pairs-trading`**: seleziona Symbol Y/X + timeframe + range, mostra lo screening di
  cointegrazione full-sample (ADF, hedge ratio) come contesto, poi esegue il vero backtest
  walk-forward via `IPairsBacktestEngine` — metriche, equity curve, grafico z-score rolling,
  trade list con entrambe le gambe. **Verificato in browser** su ETH/USDT vs BTC/USDT (2026,
  4315 candele allineate): la coppia risulta **NON cointegrata** in questo periodo (ADF
  -2.123, sopra la soglia critica -2.86) e il backtest mostra coerentemente una perdita
  (-6.75%, 166 trade, drawdown 6.84%) — dimostrazione pratica del perché il test di
  cointegrazione va fatto PRIMA di tradare una coppia, non un problema della pipeline.
- **`/volatility`**: stima GARCH(1,1) su un symbol, mostra ω/α/β/persistenza, grafico della
  volatilità condizionale (visibile il volatility clustering) e tabella di previsione a più
  orizzonti con mean-reversion verso la varianza di lungo periodo. Verificato su BTC/USDT 1h
  (180gg, 4338 rendimenti): persistenza 0.9755 (tipica per crypto), vol. annualizzata
  59.5%→56.4% mean-reverting all'aumentare dell'orizzonte, esattamente come atteso.

**Prossimo passo:** Fase D (NLP/sentiment + layer di agenti LLM) — l'unico pezzo rimasto della
roadmap originale. Richiede decisioni che non competono a un'esecuzione autonoma (provider LLM,
fonte dati news/alt-data, gestione costi/credenziali API) — da allineare con l'utente prima di
implementare. Fase E (deep learning/RL, TorchSharp) richiede che A/B siano consolidate — lo sono.

---

## 10. Fase D, primo blocco: ingestion news + sentiment factor (2026-07-01)

L'utente ha scelto esplicitamente: (1) nessun provider LLM ancora disponibile → costruire
l'astrazione ma senza integrazione end-to-end per ora; (2) partire dal **sentiment factor**
(riusa l'infrastruttura di valutazione IC della Fase A) invece che dal layer di agenti; (3)
fonte notizie da **ricercare e proporre**, non imposta a priori.

### 10.1 — Ricerca fonti news (RSS, gratuite, con riscontro di mercato)

Selezionati 4 feed RSS pubblici di editori crypto che la letteratura event-study associa a
movimenti di prezzo misurabili (notizie regolatorie, hack, flussi istituzionali — non semplice
rumore social): **CoinDesk**, **Cointelegraph**, **The Block**, **Decrypt**. Tutti HTTPS, nessuna
API key richiesta, nessun costo. Il set è centralizzato in `NewsFeeds.KnownFeeds` (facile da
estendere) e ogni fonte è isolata dietro `IAltDataSource` per essere sostituibile — es. da un
domani un aggregatore a pagamento (CryptoPanic, LunarCrush) implementerebbe la stessa interfaccia.

### 10.2 — Pipeline: ingestion → classificazione → sentiment → storage

- **`Services/AltData/RssNewsSource`** — fetch + parsing via `System.ServiceModel.Syndication`
  (`SyndicationFeed`), parsing isolato in un metodo statico testabile (`ParseFeed`) senza I/O
  di rete nei test.
- **`Services/AltData/NewsImpactClassifier`** — categorizza ogni notizia (Regulatory, Security,
  Institutional, Other) e rileva i simboli menzionati, tutto via **regex a word-boundary**
  (`\bword\b`) invece di substring match: un controllo substring ingenuo avrebbe prodotto falsi
  positivi sistematici ("ban" dentro "banana", "sol" dentro "absolute"/"resolve", "ada" dentro
  "canada"/"adapter" — con l'alias ADA ristretto a "cardano" per questo motivo).
  - **Deviazione dichiarata durante lo sviluppo:** la classificazione non usa più "prima keyword
    che matcha in ordine di priorità" ma un punteggio per numero di keyword trovate per
    categoria (a parità vince Regulatory > Security > Institutional). Il primo design falliva su
    "BlackRock reports record ETF inflows" (classificato Regulatory per la sola parola "etf",
    invece di Institutional dove due segnali più specifici — "blackrock", "inflows" — indicano
    chiaramente flusso istituzionale). Scoperto da un test xUnit fallito, non da revisione manuale.
- **`Services/Sentiment/ISentimentScorer`** + **`KeywordSentimentScorer`** — scorer lessicale
  basato su liste di parole positive/negative pesate per frequenza, normalizzato in [-1, 1].
  Interfaccia pensata esplicitamente come stand-in sostituibile da uno scorer LLM-based non
  appena l'utente sceglierà un provider — nessun altro componente della pipeline dipende
  dall'implementazione concreta.
- **`Services/Sentiment/SentimentAlphaFactor`** — implementa `IAlphaFactor` (categoria `Sentiment`,
  nuova nel `FactorCategory` enum): media rolling del sentiment delle notizie in una finestra di
  lookback configurabile (ore), allineata candela per candela, verificata anti-look-ahead per
  troncamento come tutti gli altri fattori della Fase A. **Deviazione dichiarata (stesso pattern
  di `MlStrategy`):** non registrata in `AlphaFactorFactory` — richiede una lista di notizie
  già scorate come dipendenza esterna, costruita direttamente dal chiamante.
- **`Data/AltDataPoint`** (nuova entità EF, migrazione `AddAltDataPoints`) — storage delle notizie
  ingerite con dedupe key univoca (`Source:Url` o `Source:Title` se manca l'URL), categoria,
  simboli rilevati (JSON), punteggio di sentiment. Migrazione verificata applicandola a una
  **copia dello sqlite di sviluppo reale** (non solo `EnsureCreated` in test): tabella e indici
  creati correttamente, nessun conflitto con lo schema Identity esistente.
- **`Services/AltData/AltDataSyncService`** — orchestratore fetch→classifica→scora→deduplica→
  salva, tollerante a fonti irraggiungibili (log warning e continua con le altre, stesso principio
  già usato per `MarketDataSyncService`). **Deliberatamente nessuno scheduler/worker in
  background** — sync solo on-demand per ora, per non far partire automaticamente chiamate di
  rete esterne periodiche senza un'esplicita azione dell'utente.

Registrato tutto in `Program.cs` (HttpClient nominato "AltDataRss", le 4 fonti RSS come
`IEnumerable<IAltDataSource>` singleton, lo scorer lessicale come singleton, il sync service come
scoped).

**220/220 test xUnit passano** (`NewsImpactClassifierTests`, `KeywordSentimentScorerTests`,
`SentimentAlphaFactorTests`, `RssNewsSourceTests`, `AltDataSyncServiceTests` — quest'ultimo con
sorgenti finte in-memory, nessuna chiamata di rete reale nei test).

**NON ancora fatto (prossimo passo naturale per completare la Fase D):**
- Uno scorer `ISentimentScorer` basato su LLM reale, non appena l'utente fornisce provider/API key.
- Il layer di agenti LLM (§4 della roadmap: MarketAnalystAgent, StrategyResearchAgent,
  AllocationAdvisorAgent, RiskGuardianAgent) — esplicitamente rimandato dall'utente a favore del
  sentiment factor.

### 10.3 — UI `/sentiment` (2026-07-01, browser-verificata con notizie reali)

`Sentiment.razor`: sezione 1 mostra le fonti configurate e un pulsante "Sincronizza ora" che
chiama `IAltDataSyncService.SyncAllAsync` on-demand, poi elenca le ultime notizie ingerite
(categoria colorata, simboli rilevati, punteggio di sentiment, link alla fonte). Sezione 2 valuta
`SentimentAlphaFactor` con `IFactorEvaluator` (già esistente dalla Fase A, mai usato finora in
una UI: **prima vera utilizzatrice della valutazione IC/quantili/decay fuori dai test/console**)
— stessi controlli di symbol/timeframe/range delle altre pagine, più lookback (ore) del fattore e
orizzonte forward del target.

**Verificato in browser con una sincronizzazione reale** (non un fixture): 109 notizie ingerite
dai 4 feed RSS live in un colpo solo, correttamente categorizzate (es. un pezzo su una proposta
di legge crypto classificato Regulatory, notizie generiche Other) e con simboli rilevati (BTC,
XRP) dai titoli reali. Valutazione IC su BTC/USDT 1h (30gg, 740 candele, lookback 24h): 51
osservazioni valide, IC -0.055, quantile spread e IC-decay calcolati correttamente (decadimento
monotono da -0.055 a -0.55 su orizzonti 1→10) — con solo poche decine di minuti di storico notizie
accumulato in questa sessione, il numero di osservazioni è necessariamente basso; la UI mostra un
avviso esplicito sotto le 30 osservazioni per non far scambiare un IC su pochi dati per un segnale
affidabile. Nessun errore in console, nessuna regressione sulle altre pagine.

**Fase D COMPLETA per il blocco "sentiment factor"** (220/220 xUnit pass). Restano solo, come
già annotato: lo scorer LLM-based e il layer di agenti, entrambi bloccati su una decisione
dell'utente (provider LLM) non ancora presa.

---

## 11. Passata di qualità: audit, hardening, code review (2026-07-01)

Su richiesta esplicita dell'utente ("testare e migliorare tutto quello fatto finora"), quattro
verifiche in parallelo dopo il completamento della Fase D:

**Smoke-test browser su tutte le pagine**: tutte le 14 pagine principali caricano senza errori
server/console dopo le modifiche di oggi (nessuna regressione dai cambi DI in `Program.cs`).

**Audit dei punti "NON fatto"/deviazione**: rilettura di ogni TODO/deviazione flaggato nella
roadmap. Conclusione: quasi tutto ciò che resta aperto è bloccato su una decisione dell'utente
(provider LLM, chiavi testnet con saldo) oppure è una scelta di design deliberata e già
documentata (RiskParity naive, Discovery non legata a un modello ML single-symbol, ecc.) — non
un gap silenzioso da segnalare come urgente.

**Hardening Fase D**: `AltDataSyncService` interrogava le fonti RSS in sequenza con
`catch (OperationCanceledException) { throw; }`; aggiungendo un timeout di 15s all'HttpClient
"AltDataRss" (prima illimitato, default ~100s per fonte lenta/morta), un timeout avrebbe
sollevato un `TaskCanceledException` (sottoclasse di `OperationCanceledException`) e fatto
fallire l'INTERA sync invece di saltare solo quella fonte. Risolto parallelizzando le fetch
(`Task.WhenAll`, ogni fonte cattura il proprio errore) e ri-sollevando solo quando è il chiamante
a cancellare esplicitamente. Verificato dal vivo contro i feed RSS reali.

**Code review mirata (Fase A-C, via sub-agent)** su Alpha/ML/Portfolio/TimeSeries/PairsTrading/
BacktestEngine. Trovati e corretti:
- **`MeanVarianceOptimizer`/`OlsRegression`**: `covariance.Solve()`/`xtx.Inverse()` senza
  regolarizzazione — su una matrice quasi singolare (asset crypto fortemente correlati, o
  regressori quasi collineari come livelli di prezzo cointegrati) la LU-solve di MathNet può
  restituire pesi/coefficienti numericamente instabili SENZA sollevare un errore. Aggiunta
  diagonal loading (ridge trascurabile su matrici ben condizionate) in `PortfolioMath.Regularize`
  e direttamente in `OlsRegression.Fit`.
- **`RegressionPredictorBase`**: il `PredictionEngine` di ML.NET (risorsa nativa) non veniva mai
  liberato — confermato ATTIVO (non solo latente) nel path `BacktestEngine.LoadMlStrategyAsync`,
  che crea un predittore nuovo per ogni combo/finestra di uno sweep di Optimization: uno sweep di
  64 combinazioni perdeva 64 prediction engine nativi. Aggiunto `IDisposable` a
  `IReturnPredictor`; `BacktestEngine` dispone il predittore SOLO quando lo crea lui stesso
  (risoluzione per nome, `StrategyName="Ml"`) — quando il chiamante passa una strategia già
  pronta (es. `MlLab.razor`, che riusa lo stesso predittore fra Train/Backtest/Save nella stessa
  sessione UI) la proprietà resta sua e non viene toccata. Verificato dal vivo: training +
  backtest + save in ML Lab, poi uno sweep di Optimization da 64 combinazioni sul modello
  salvato — entrambi i percorsi funzionano correttamente col nuovo schema di ownership.
- **`PairsCandleAligner`**: `ToDictionary` avrebbe sollevato un `ArgumentException` generico su
  timestamp duplicati nella serie di candele (percorso reale da DB impossibile grazie all'indice
  univoco Symbol+Timeframe+TimestampUtc, ma il metodo è pubblico e testabile con dati arbitrari) —
  ora un errore esplicito che nomina il timestamp duplicato.
- Nessun bug di look-ahead, CultureInfo/locale, o corruzione dati trovato altrove in Fase A-C.

220/220 xUnit pass dopo ogni fix; verificato anche dal vivo in browser (ML Lab, Optimization con
modello ML salvato, Pairs Trading) senza errori server/console.

---

## 12. Fase D.2 — Forex & Macro Sentiment Sources (2026-07-01)

Estensione della pipeline di Fase D con fonti forex/macro/sentiment retail, su richiesta esplicita
dell'utente con un prompt molto dettagliato (4 fonti nominate, vincoli espliciti su cosa NON
toccare, richiesta di verificare gli URL prima di scrivere codice). **253/253 xUnit pass** (220
baseline + 33 nuovi), build pulita, **0 nuovi warning**. Nessun file di Fase A-C toccato.

### 12.1 — Ricerca preliminare (prima di scrivere codice)

Ogni fonte è stata verificata dal vivo (HTTP HEAD/GET reali) prima dell'implementazione:

| Fonte | Esito verifica | Decisione |
|---|---|---|
| FXStreet RSS generale | 200, `text/xml`, feed valido | Usato: `https://www.fxstreet.com/rss` |
| FXStreet RSS Central Banks | 200, `text/xml`, feed dedicato | Usato: `https://www.fxstreet.com/rss/news/central-banks` |
| ForexFactory `/rss` | 403 | Non esiste un feed pubblico |
| ForexFactory `/calendar` (HTML) | 200 via `curl`, righe evento reali (non una challenge page) | Scraping tentato (vedi 12.3) |
| forexclientsentiment.com | 403, pagina Cloudflare "Just a moment" (~5KB) | **Bloccato**, sostituito (vedi 12.4) |
| fxssi.com/tools/current-ratio (pagina) | 200 ma 1MB di HTML/JS senza i dati embeddati (widget client-side) | Pagina non scrapabile |
| fxssi.com — endpoint JSON reale (`c.fxssi.com/api/current-ratio`) | 200, JSON pulito, multi-broker | **Usato** (vedi 12.4) |
| investing.com — RSS calendario economico | Nessun feed esiste (solo feed di news per categoria, es. "Forex News" id=1) | Scartato come alternativa |
| MyFxBook — pagina calendario | 403 (Cloudflare) | Scartato come alternativa calendario |

### 12.2 — Nuove categorie e classificatore

`NewsCategory` esteso con `CentralBanks`, `Macro` (derivate per keyword, stesso meccanismo a
punteggio già usato per Regulatory/Security/Institutional — non "prima categoria che matcha") e
`EconomicCalendar`/`RetailSentiment` (categorie **strutturali**, assegnate direttamente dal
rispettivo ingestor via i nuovi campi opzionali `RawNewsItem.CategoryOverride` /
`SentimentScoreOverride` / `SymbolsOverride` — nessun testo da classificare per un evento di
calendario o una lettura numerica di posizionamento retail). `AltDataSyncService` onora questi
override quando presenti, altrimenti usa la classificazione/scoring automatica come prima —
**zero modifiche al comportamento delle fonti testuali esistenti**. `NewsImpactClassifier` ha
nuove keyword (`fed`, `fomc`, `ecb`, `rate decision`, `powell`, ... per CentralBanks; `cpi`, `nfp`,
`gdp`, `unemployment`, ... per Macro) e nuovi alias di simbolo per le major forex (EURUSD, GBPUSD,
ecc.), sempre a word-boundary.

### 12.3 — FXStreet (funziona) e ForexFactory (bloccato in produzione, documentato)

**FXStreet**: nessuna classe dedicata — `RssNewsSource` (già esistente) gestisce qualunque feed
RSS/Atom, quindi si sono aggiunte due righe a `NewsFeeds.KnownFeeds` ("FXStreet",
"FXStreet-CentralBanks"). **DECISIONE ARCHITETTURALE**: una classe wrapper "FxStreetRssIngestor"
che non aggiunge comportamento sarebbe stata duplicazione, non riuso — coerente col vincolo
esplicito dell'utente di non duplicare l'architettura esistente. **Verificato dal vivo**: sync
reale ha ingerito notizie vere, correttamente classificate (es. un pezzo su un rate decision
categorizzato CentralBanks).

**ForexFactory**: `ForexFactoryIngestor` (nuovo, con HtmlAgilityPack) fa scraping di
`/calendar` — verificato che l'HTML statico contiene righe evento reali (`data-event-id`,
`data-day-dateline`, orario, valuta, icona di impatto) e li estrae correttamente (7 test su
fixture salvata). **LIMITAZIONE 1 (documentata nel codice)**: i valori Actual/Forecast/Previous
NON sono nell'HTML statico — verificato dal vivo, tutte le celle `calendar__actual` sono vuote
nella risposta server (popolate via JS/AJAX lato client dopo il caricamento pagina). **LIMITAZIONE
2 — BLOCCO CONFERMATO IN PRODUZIONE**: la stessa richiesta che con `curl` (da questa macchina)
riceve 200, dall'app .NET reale (`HttpClient`) riceve **403** — verificato dal vivo lanciando una
sync reale. Ripetendo poi la richiesta anche `curl` ha iniziato a ricevere 403 (blocco Cloudflare
adattivo dopo richieste ripetute, non un fluke isolato). Non è un problema di header (User-Agent,
Accept, Accept-Language testati uno per uno, nessuna differenza) ma quasi certamente di
fingerprint TLS/JA3, che uno scraper HTTP non può aggirare senza un browser headless (fuori
scope). **ALTERNATIVA VALUTATA E SCARTATA**: né il feed RSS di Investing.com per il calendario
economico (non esiste) né il calendario di MyFxBook (anch'esso dietro Cloudflare, verificato 403)
sono disponibili gratuitamente. **ALTERNATIVA DI FATTO GIÀ IMPLEMENTATA**: le categorie
CentralBanks/Macro via FXStreet (funzionanti, verificate dal vivo) coprono già oggi il segnale
"evento macro/banca centrale che muove il mercato" come reazione giornalistica in tempo reale,
anche se non come calendario con orario/consensus pre-schedulato. `ForexFactoryIngestor` resta
registrato (il fallimento è tollerato dalla resilienza già esistente di `AltDataSyncService`, che
salta le fonti irraggiungibili con un warning) — pronto a funzionare se in futuro si integrasse
un'API a pagamento (es. Trading Economics) o un browser headless.

### 12.4 — Sentiment retail: pivot da 2 siti a 1 endpoint con 2 fonti indipendenti

`RetailSentimentIngestor` (nuovo): **non** fa scraping HTML di due siti separati come da piano
originale, perché **entrambi** si sono rivelati irraggiungibili in modo diretto (vedi tabella
12.1). **ALTERNATIVA EQUIVALENTE implementata**: lo stesso sito FXSSI espone pubblicamente
l'endpoint JSON che il suo widget interroga lato client (`https://c.fxssi.com/api/current-ratio`,
verificato dal vivo: 200, nessuna autenticazione), che aggrega il posizionamento long/short di PIÙ
broker reali sotto un'unica risposta — usiamo la chiave `"fxssi"` come fonte "FXSSI" e la chiave
`"myfxbook"` come fonte "MyFxBook" (sostituisce forexclientsentiment.com con una fonte ancora più
riconosciuta nel settore), ottenendo le due fonti indipendenti richieste per il confronto
incrociato con **una sola integrazione verificata funzionante** invece di due scraping fragili.
Un'istanza della stessa classe per fonte (stesso pattern di `RssNewsSource`/`NewsFeeds.KnownFeeds`
— "un ingestor, più istanze"). `SentimentScore = (%long - 50) / 50` come richiesto; i simboli
crypto dell'aggregatore (BTCUSD, ETHUSD) sono mappati ai ticker canonici già usati dalla
piattaforma (BTC, ETH) per essere compatibili con `SentimentAlphaFactor`/OHLCV esistenti; le
coppie forex restano col proprio ticker. Deduplica: bucket orario nell'Url (le letture sono
istantanee ripetute dello stesso simbolo, non articoli storici univoci — senza il bucket,
`AltDataSyncService` tratterebbe ogni sync successiva come "già vista"). **Verificato dal vivo**:
sync reale → 25 letture FXSSI + 17 MyFxBook, percentuali long/short realistiche e coerenti con la
UI reale di fxssi.com.

### 12.5 — `NewsImpactAnalyzer`: impatto storico

Nuovo servizio (`Services/AltData/NewsImpactAnalyzer.cs`, Singleton, stateless): per ogni
notizia/evento misura il rendimento di un **simbolo di riferimento** (scelto dall'utente, tipico
BTC/USDT) nelle finestre [t,t+1h], [t,t+4h], [t,t+24h] via ricerca binaria sulle candele
(`FirstIndexAtOrAfter`), poi aggrega per categoria e per fonte; per il sentiment retail confronta
anche i casi in cui FXSSI e MyFxBook concordano (entrambi oltre ±70%, cioè `|score| > 0.4`) contro
i casi in cui divergono, appaiando le letture per (simbolo, ora).

**DECISIONE ARCHITETTURALE ESPLICITA**: la piattaforma ingerisce OHLCV solo per crypto — non
esiste uno storico prezzi per le coppie forex di cui parlano le fonti macro/calendario/sentiment
retail. Misurare l'impatto "sul proprio strumento" richiederebbe OHLCV forex, fuori scope
dichiarato. Si misura quindi l'impatto di OGNI notizia/evento (qualunque sia lo strumento nominale
di cui parla) sul simbolo crypto di riferimento — una domanda empirica legittima e nota in
letteratura ("risk-on/risk-off": Fed/ECB e il sentiment macro muovono anche gli asset di rischio
come le crypto). Se in futuro la piattaforma ingerisse OHLCV forex, lo stesso analyzer
funzionerebbe passando quella serie come `referenceCandles`, zero modifiche di codice. Documentato
sia nel codice sia nella UI (nota a piè di tabella).

7 test con OHLCV sintetici a **gradino noto** (non solo "diverso da zero"): un salto di prezzo
esatto (es. +10%) a un'ora nota permette di asserire il numero esatto atteso, non solo l'assenza
di NaN — l'analyzer supera tutti i test al primo colpo.

### 12.6 — UI `/sentiment` estesa e verificata dal vivo con dati reali

- Filtri Categoria/Fonte sopra la tabella notizie (client-side sulle ultime 200 già caricate).
- Sezione dedicata "Sentiment retail" con **gauge a barra** (verde/rosso, % long/short) per
  simbolo+fonte — non più solo righe testuali nella tabella generica, come richiesto.
- Sezione 4 "Analisi di impatto storico": bottone che esegue `NewsImpactAnalyzer` sul
  symbol/timeframe/range già configurati in sezione 2, tre tabelle (per categoria, per fonte,
  confronto incrociato retail).
- Lista "Fonti configurate" ora letta dinamicamente da `IEnumerable<IAltDataSource>` (DI) invece
  che dal solo `NewsFeeds.KnownFeeds` — mostra anche ForexFactory/FXSSI/MyFxBook, unica fonte di
  verità (niente elenco hardcoded secondario che potrebbe disallinearsi).

**Verificato dal vivo (sync reale, non fixture)**: 216 notizie/eventi totali dopo sync (109
crypto pre-esistenti + 107 nuove forex/macro/retail), categorie popolate correttamente
(CentralBanks 25, Macro 18, RetailSentiment 42, Regulatory/Security/Institutional/Other invariati
per le fonti crypto). Click su "Analizza impatto storico" → BTC/USDT 1h, 742 candele, 216
notizie: **risultato reale** (non tutti zero, non NaN):

| Categoria | N | 1h | 4h | 24h |
|---|---|---|---|---|
| Other | 93 | 0.14% | 0.51% | 0.20% |
| Regulatory | 20 | 0.11% | 0.47% | 1.00% |
| Security | 4 | 0.60% | 1.01% | -3.04% |
| CentralBanks | 25 | 0.26% | 0.31% | 0.00% |
| Macro | 18 | -0.16% | -0.05% | 0.00% |
| Institutional | 14 | 0.29% | 0.47% | 1.42% |
| RetailSentiment | 42 | 0.00% | 0.00% | 0.00% |

(RetailSentiment/FXSSI/MyFxBook a 0.00% ovunque è un artefatto onesto di questa essere la prima
sincronizzazione di sempre — un'unica istantanea oraria, senza storico accumulato per popolare
finestre 24h in avanti; non un bug, si risolverà sincronizzando nei prossimi giorni.) Zero errori
console/server durante tutto il test.

### 12.7 — File creati/modificati

**Nuovi**: `Services/AltData/ForexFactoryIngestor.cs`, `Services/AltData/RetailSentimentIngestor.cs`,
`Services/AltData/NewsImpactAnalyzer.cs`, fixture di test (`Tests/Fixtures/forexfactory_calendar_sample.html`,
`fxssi_current_ratio_sample.json`), `ProcioneMGR.Tests/ForexFactoryIngestorTests.cs`,
`RetailSentimentIngestorTests.cs`, `NewsImpactAnalyzerTests.cs`.
**Modificati**: `NewsImpactClassifier.cs` (nuove categorie/keyword), `IAltDataSource.cs`
(`RawNewsItem` con override opzionali), `AltDataSyncService.cs` (onora gli override),
`RssNewsSource.cs` (due voci FXStreet in `NewsFeeds.KnownFeeds`), `Program.cs` (DI: HttpClient
dedicati per ForexFactory/RetailSentiment con timeout 15s, registrazione dei 3 nuovi ingestor +
`INewsImpactAnalyzer`), `Sentiment.razor` (filtri, gauge, sezione impatto storico),
`ProcioneMGR.csproj` (+HtmlAgilityPack 1.12.4), `ProcioneMGR.Tests.csproj` (copia fixture in
output), `NewsImpactClassifierTests.cs`/`RssNewsSourceTests.cs` (nuovi casi).

### 12.8 — Prossimi passi consigliati

- **Quando ci sarà una chiave LLM**: `ISentimentScorer` resta l'unico punto da sostituire (nessun
  altro file coinvolto) — varrebbe la pena usarlo prima di tutto per i testi Macro/CentralBanks
  (frasi lunghe e sfumate, es. "hawkish but data-dependent", dove il lessico fa più fatica che
  sulle notizie crypto brevi tipo "hack"/"ban").
  Anche un LLM potrebbe estrarre "Actual/Forecast/Previous" da un riassunto testuale delle notizie
  FXStreet sui rate decision, come ripiego parziale per il buco lasciato da ForexFactory.
- **RetailSentiment nel tempo**: la copertura oggi è di una sola istantanea; con sync ripetute nei
  giorni successivi il confronto incrociato FXSSI/MyFxBook e l'IC del fattore `Sentiment`
  guadagneranno osservazioni reali (stesso limite già documentato in Fase D.1 per le notizie
  testuali, qui più marcato perché ogni simbolo ha *un solo* punto per sync, non decine di
  articoli).
- **OHLCV forex**: se in futuro si aggiungesse un data source forex (fuori scope oggi, la
  piattaforma è nata crypto-only su Binance/Bitget), `NewsImpactAnalyzer` funzionerebbe subito
  anche per l'impatto "sul proprio strumento" — è già scritto in modo generico rispetto al simbolo
  di riferimento.
- **ForexFactory**: se servisse davvero il calendario con consensus/actual, la strada pulita è
  un'API a pagamento (Trading Economics, FCS API) dietro la stessa interfaccia `IAltDataSource` —
  lo scraping ha un soffitto strutturale (Cloudflare) che non ha senso continuare a inseguire.

---

## 13. Revisione UI: pannello Guida su ogni pagina + Home rifatta (2026-07-02)

Richiesta esplicita dell'utente: "in ogni pagina spiegare ogni elemento e a cosa serve, e
descrizione/spiegazione/funzionamento della pagina, così da renderlo comprensibile a chiunque
anche senza conoscenze in merito". Scelta di design condivisa via `AskUserQuestion`:
- Meccanismo: **pannello "Guida" espandibile in cima a ogni pagina** — controlli restano puliti,
  chi vuole la spiegazione la apre, chi la conosce la chiude e lavora senza ingombro.
- Ordine: prima una pagina campione (Backtest), verifica dell'utente, poi rollout su tutte.

### 13.1 — Componente `GuidaPanel` (riusabile)

Nuovo `Components/Shared/GuidaPanel.razor`: card Bootstrap con header richiudibile
(chevron up/down), body renderizzato via `RenderFragment ChildContent` (lascia libertà su
struttura interna — la maggior parte delle pagine usa un `<dl class="row">` con `<dt>/<dd>`).
Chiuso di default. CSS in `wwwroot/app.css` (`.guida-panel-header`, hover, `dl` spacing).
Import globale via `Components/_Imports.razor` (`ProcioneMGR.Components.Shared`).

### 13.2 — Rollout su TUTTE le pagine principali

Pattern applicato: introduzione in linguaggio semplice + tabella di definizioni una-per-una di
ogni controllo/metrica/tabella, con link inter-pagina dove ha senso (es. "vai in Watchlist a
scaricare i dati", "salva in Le mie strategie"). Applicato a:
`Backtest` (campione), `Watchlist`, `ExchangeSettings`, `Strategies`, `Optimization`,
`Discovery`, `Ensemble`, `Regimes`, `MlLab`, `PairsTrading`, `Volatility`, `Sentiment`,
`Trading`, `AdminUsers`, `Dashboard` — 15 pagine coperte. Verificato dal vivo: tutte le pagine
restituiscono 200 e contengono il pannello (`hasGuida: true`), zero errori console/server.

### 13.3 — Home rifatta (autenticato) + Dashboard semplificata

**Home** (`Components/Pages/Home.razor`, ora `@rendermode InteractiveServer`): riscritta per
un utente autenticato con contenuto azionabile invece dei generici "feature card". Include:
- Saluto personalizzato "Bentornato, {email}".
- 4 statistiche vive dal DB: serie tracciate abilitate, candele totali in archivio, strategie
  salvate dall'utente corrente, stato Trading (Fermo / {Mode} attivo).
- Alert contestuale se `TrackedSeries == 0`: spiega qual è il primo passo per un utente nuovo
  ("aggiungi una serie in Watchlist o Dashboard").
- Sezione "Il percorso della piattaforma" — 5 card numerate cliccabili (Dati → Backtest →
  Ottimizza → Combina → Vai live) con frecce fra i passi su schermi larghi, che orientano
  visivamente il flusso naturale.
- Sezione "Strumenti di analisi avanzata" — le 6 pagine trasversali (Discovery, Regimi, ML Lab,
  Pairs Trading, Volatilità, Sentiment) come feature-card cliccabili con una riga di
  spiegazione ciascuna.
La versione non-autenticata resta l'hero semplice con Login/Registrati.
Aggiunto CSS: `.hero-compact`, `.workflow-step` (card numerata con hover teal e freccia
`chevron-right` di bootstrap-icons via `::after` su >=992px), `.workflow-step-number`
(cerchietto scuro in alto-sinistra).

**Dashboard**: rimossa la card "Il tuo profilo" (utente/ruoli) che era ridondante col menu di
navigazione e non azionabile; aggiunto `GuidaPanel` che chiarisce la distinzione con Watchlist
(la Dashboard è per esplorare on-demand, Watchlist per tracciare stabilmente in background).
Cleanup del metodo `GetRoles` ora inutilizzato.

### 13.4 — Fix reali durante il rollout

**Scrollbar (menu troppo lungo per lo schermo)**: il menu ha ~16 voci e su laptop 768–800px
finiva sotto la finestra. Righe compattate da 2.7rem→2.2rem, `font-size` 0.9→0.87rem: a
1280×800 nessuno scroll; a schermi più bassi la scrollbar del menu è ora sottile e scura,
coerente col tema della sidebar (stile custom sia per Firefox `scrollbar-width: thin` che
Chrome/Edge/Safari via `::-webkit-scrollbar` con thumb `rgba(255,255,255,0.25)`).

**Sidebar sticky rotta (regressione introdotta durante il fix scrollbar orizzontale)**:
avevo aggiunto `overflow-x: hidden` su `html, body` come guardia contro scrollbar orizzontali
indesiderate. Effetto collaterale non previsto: il browser **deriva implicitamente
`overflow-y: auto`** quando vede `overflow-x: hidden` — questo crea un nuovo scroll container
sul body, che rompe `position: sticky` sui discendenti (la sidebar smetteva di restare fissa
e scorreva col contenuto — segnalato dall'utente: "bloccata così che mi scorre assieme a tutta
la pagina non si può guardare"). **Fix**: sostituito con `overflow-x: clip`, che NON crea uno
scroll container ma taglia comunque l'overflow orizzontale — la sidebar torna sticky e la
scrollbar orizzontale resta comunque bloccata. Verificato dal vivo su pagina alta 2290px con
scroll di 800px: sidebar `top: 0, bottom: 900` (perfettamente incollata al viewport).

**File modificati**: `Components/Shared/GuidaPanel.razor` (nuovo), `Components/_Imports.razor`
(+using), `wwwroot/app.css` (+.guida-panel-*, +.workflow-step-*, +.hero-compact, overflow-x
clip), `Components/Layout/NavMenu.razor.css` (righe più compatte + scrollbar custom),
`Components/Pages/Home.razor` (riscritta), `Components/Pages/Dashboard.razor` (Guida panel +
rimozione card profilo), + le 15 pagine con `<GuidaPanel Title="..."><dl>...</dl></GuidaPanel>`.
Build pulita, 0 nuovi warning, 253/253 xUnit pass, verificato dal vivo in browser.

---

## 14. Rilettura del libro: colmati i gap residui dei cap. 2, 5 e 17 (2026-07-02)

L'utente ha chiesto una rilettura sistematica del libro ("fai lo stesso con questo libro",
dopo Trombetta e McAllen). La verifica capitolo-per-capitolo contro lo stato di avanzamento
(§8-13) ha confermato che le Fasi A-D coprono i cap. 4-14; i gap RESIDUI implementabili senza
decisioni utente erano tre, ora chiusi. 345/345 xUnit pass (326 baseline + 19 nuovi).

**Cap. 2 — Barre non temporali** → `Services/Ingestion/BarBuilder` (Singleton):
`BuildVolumeBars` / `BuildDollarBars` aggregano le candele temporali di base in barre a
volume/controvalore costante (controvalore = prezzo tipico (H+L+C)/3 × volume; VWAP incluso);
`SuggestVolumeThreshold`/`SuggestDollarThreshold` calcolano la soglia per un numero target di
barre (l'equivalente del `trades_per_min` del libro); `ToOhlcv` converte le barre aggregate in
`OhlcvData` sintetici riusabili da indicatori/fattori/analisi esistenti (tutti agnostici
rispetto alla spaziatura temporale). La coda incompleta viene scartata (una barra "mezza
piena" non è confrontabile con le altre). Nota: si parte dalle candele base (non dai tick, che
non ingestiamo) — la granularità minima della soglia è quella della serie sorgente.

**Cap. 5 — Kelly criterion** → `Services/Risk/KellyCalculator` (Singleton):
`BinaryKelly(p, b)` = p − (1−p)/b (0 se edge negativo); `FromTradeHistory(trades)` deriva
p/payoff dai trade di un backtest e restituisce `KellySuggestion` con **half-Kelly**
prudenziale; `ContinuousKelly(μ, σ)` = μ/σ² in forma chiusa; `ContinuousKellyNumeric(m, s)`
massimizza E[log(1+f·r)] sotto Normal (golden-section + Simpson su ±3σ) — verificato
sull'esempio numerico del libro (m=0.058, s=0.216 → f*≈1.1974); `MultiAssetKelly` = Σ⁻¹μ
(Chan 2008, equivalente al max-Sharpe non vincolato) con ridge diagonale e normalizzazione
Σ|w|=1. UI: la card "Performance report" di `/backtest` mostra il Kelly e l'half-Kelly
suggeriti dai trade (solo con ≥10 trade decisi).

**Cap. 17 — Deep learning, l'essenza senza TorchSharp** → `Services/ML/MlpReturnPredictor`:
MLP feed-forward in C# puro (1 strato nascosto tanh, uscita lineare, mini-batch GD + weight
decay, standardizzazione su statistiche del train, deterministico a parità di seed).
Implementa `IReturnPredictor` DIRETTAMENTE (non `RegressionPredictorBase`, che incapsula un
ITransformer ML.NET): Fit legge le righe dall'IDataView, persistenza JSON (pesi +
normalizzazione), permutation feature importance ricalcolata sulle predizioni dirette.
Integrato ovunque i modelli già arrivano: selettore in `/ml` ("Rete neurale (MLP, C# puro)"),
switch di caricamento `SavedMlModel` in MlLab e in `BacktestEngine.LoadMlStrategyAsync`
(ModelType "Mlp") → salvabile, ricaricabile, backtestabile, ottimizzabile e inseribile in
ensemble come gli altri tre modelli. Test: impara relazioni lineari E non lineari
(f0²−|f1| → R²>0.7 dove un lineare farebbe ~0), determinismo, round-trip JSON, importance.

**Restano ESCLUSI, con motivazione**:
- **Cap. 18-21 (CNN/LSTM/Autoencoder/GAN) e cap. 22 (Deep RL)**: richiedono realisticamente
  TorchSharp (~centinaia di MB di runtime nativo) — decisione di dipendenza che spetta
  all'utente, come da principio §2 ("da introdurre solo quando la fase relativa inizia").
  Il MLP copre l'essenza del deep learning tabellare su fattori; per sequenze/immagini/RL
  serve il framework vero.
- **Cap. 14-16 in versione LLM** (scorer sentiment LLM, topic modeling, embeddings): bloccati
  sulla scelta del provider/API key, esplicitamente rimandata dall'utente in Fase D.
- **Order book / tick data (cap. 2)**: fuori scope per lo swing trading crypto attuale (già
  così classificato nella mappatura §3).
- **On-chain data come "fondamentali" crypto (cap. 2-3)**: fattibile via l'infrastruttura
  `IAltDataSource` esistente, ma richiede la scelta di una fonte/API — da decidere insieme.
