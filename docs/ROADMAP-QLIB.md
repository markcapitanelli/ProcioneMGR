# ProcioneMGR — Roadmap "prestiti da Qlib"

**Da microsoft/qlib a ProcioneMGR in C#** — otto idee proposte dall'utente, verificate contro lo
stato reale della piattaforma (non contro un progetto ipotetico) e tradotte in architettura C#
concreta: namespace, interfacce, punti di aggancio, effort, priorità.

---

## 0. Premessa: cosa significa "prestito" qui

Prima di mappare le otto idee è necessario dire cosa **non** sono: ProcioneMGR non è, rispetto a
Qlib, una piattaforma povera a cui aggiungere le fondamenta. È già, ad oggi (`ROADMAP-ML4T.md`,
`ROADMAP-OPERATIVA.md`, 700+ test xUnit), un sistema che copre la maggior parte di ciò che Qlib
offre, riletto sotto altro nome:

| Concetto Qlib | Equivalente ProcioneMGR già esistente |
|---|---|
| Alpha factor + IC evaluation | `Services/Alpha/` (8 fattori) + `FactorEvaluator` (IC Spearman, IR, quantili, decay) |
| Modelli ML tabellari (linear/RF/LightGBM) | `Services/ML/` — `LinearReturnPredictor`, `RandomForestReturnPredictor`, `GradientBoostingReturnPredictor`, `MlpReturnPredictor` |
| Backtest engine + walk-forward | `Backtesting/BacktestEngine`, `Optimization/OptimizationEngine` |
| Regime detection | `Regime/RegimeDetector` (K-means ML.NET) |
| Portfolio optimization | `Services/Portfolio/` (Mean-Variance, Risk Parity, HRP) |
| Tracking dei run (parziale) | `Services/Pipeline/PipelineEntities.cs` — `PipelineRun`/`PipelineArtifact` |
| "Ricerca automatica di segnali" (parziale) | `Discovery/StrategyDiscoveryEngine` + `CompositeSignalStrategy`/`SignalCatalog` (combinatoria AND/OR) |

Quindi le otto proposte non colmano un vuoto: **innestano capacità specifiche e reali che oggi
mancano davvero**, verificate una per una leggendo il codice sorgente, non per analogia. Dove
un'idea è già parzialmente coperta da un meccanismo esistente ma con un design diverso, lo dico
esplicitamente e propongo di **estendere quel meccanismo** invece di duplicarlo — lo stesso
principio architetturale già seguito in tutta la roadmap ML4T (vedi le "deviazioni dichiarate").

---

## 1. Le otto proposte, verificate e mappate

### 1.1 — Alpha158CSharp ⭐⭐⭐ (P1, fondazione)

**Cosa c'è oggi**: `Services/Alpha/Factors.cs` — 8 fattori (`MomentumFactor`,
`MeanReversionFactor`, `RealizedVolatilityFactor`, `ParkinsonVolatilityFactor`,
`RelativeVolumeFactor`, `RsiFactor`, `MacdFactor`, `DistanceFromMaFactor`), tutti dietro
`IAlphaFactor` (contratto anti-look-ahead per costruzione: `value[i]` dipende solo da
`candles[0..i]`), con `FactorEvaluator` che già calcola IC (Spearman), IR, quantile spread e
decay dell'IC. **La fondazione è quella giusta** — il gap è solo la *numerosità e varietà* del
catalogo, non l'infrastruttura di valutazione (già equivalente all'Alphalens di Qlib).

**Perché Alpha158 di Qlib non è "158 idee diverse"**: è ~15-18 operatori rolling (ROC, MA, STD,
BETA/pendenza, MAX, MIN, quantili, rank, count-positivi/negativi, correlazione prezzo-volume,
VWMA...) applicati su ~5-6 orizzonti (5/10/20/30/60) più un pugno di fattori "shape della
candela" (KMID, KLEN, KUP, KLOW) a orizzonte singolo. Non sono 158 classi scritte a mano: sono
**pochi operatori × molti orizzonti**.

**Architettura proposta** — `Services/Alpha/Alpha158/` (sotto-namespace, additivo):

```csharp
// Operatori rolling causali, riusano/estendono FactorMath esistente.
public static class RollingOps
{
    public static decimal?[] Roc(decimal[] closes, int horizon);          // rendimento a n periodi
    public static decimal?[] MaRatio(decimal[] closes, int horizon);      // close / SMA - 1
    public static decimal?[] StdRatio(decimal[] closes, int horizon);     // STD(close, n) / close
    public static decimal?[] Beta(decimal[] closes, int horizon);         // pendenza OLS su n barre (MathNet)
    public static decimal?[] Rsq(decimal[] closes, int horizon);          // R² della stessa regressione
    public static decimal?[] RollingRank(decimal[] series, int horizon);  // percentile causale (riusa CausalPercentile di SignalCatalog)
    public static decimal?[] Qtlu(decimal[] closes, int horizon, decimal q);
    public static decimal?[] Corr(decimal[] a, decimal[] b, int horizon); // price-volume correlation (CORR/CORD)
    public static decimal?[] CountPos(decimal[] returns, int horizon);    // CNTP/CNTN/CNTD
    public static decimal?[] Wvma(decimal[] closes, decimal[] volumes, int horizon);
    // ... IMAX/IMIN (posizione dell'estremo), SUMP/SUMN, VSTD, VSUMP/VSUMN ...
}

// Genera N x M istanze IAlphaFactor invece di scriverle a mano una per una.
public sealed class Alpha158FactorSet
{
    public static readonly int[] DefaultHorizons = [5, 10, 20, 30, 60];

    public IReadOnlyList<IAlphaFactor> BuildCatalog(IEnumerable<int>? horizons = null);
    // -> KBAR shape (5, horizon-indipendenti) + per ogni orizzonte: Roc, MaRatio, StdRatio,
    //    Beta, Rsq, Max, Min, Qtlu, Qtld, Rank, Rsv, Imax, Imin, Corr, Cord, Cntp, Cntn,
    //    Sump, Sumn, Vma, Vstd, Wvma  ~= 22 op x 5 orizzonti + 5 KBAR = 115, estendibile a
    //    158 aggiungendo un 6° orizzonte o varianti (skip, come già fa MomentumFactor).
}
```

Ogni fattore generato implementa **la stessa interfaccia `IAlphaFactor` già esistente** — zero
modifiche a `FactorEvaluator`, `DatasetBuilder`, `MlStrategy`, `AlphaFactorFactory`: si
registrano semplicemente in più. Questo è il punto chiave: l'architettura attuale è già pronta
per 158 fattori quanto lo è per 8, perché non assume un numero fisso.

**Test anti-look-ahead a costo marginale zero**: oggi ogni fattore ha un test di troncamento
scritto a mano. Con 150 fattori generati serve un singolo `[Theory]` xUnit che itera
`Alpha158FactorSet.BuildCatalog()` e verifica l'invariante di troncamento per ciascuno
automaticamente — non 150 test scritti a mano, un test parametrico che li copre tutti.

**Effort**: medio (2-3 giorni-persona equivalenti). **Rischio**: basso — additivo, nessuna
interfaccia esistente tocca. **Dipendenze nuove**: nessuna (MathNet.Numerics già presente per
`Beta`/regressione).

---

### 1.2 — Nested Decision Execution ⭐⭐⭐ (P1, alto impatto su PnL reale)

**Verificato**: `IExchangeClient.PlaceOrderAsync` è un singolo metodo, un ordine — non esiste
alcun layer tra "la strategia decide Long" e "l'ordine parte sull'exchange". Confermato anche in
`TradingEngine`: nessuna nozione di slicing, TWAP, VWAP o iceberg. **Il gap è reale**, non
parzialmente coperto da nulla di esistente.

**Architettura proposta** — nuovo namespace `Services/Execution/`, layer che si inserisce **tra**
la decisione (oggi: `IStrategy.EvaluateSignal` → `Signal` a fine candela) e l'esecuzione (oggi:
una chiamata diretta a `PlaceOrderAsync`):

```csharp
public interface IExecutionAlgorithm
{
    string Name { get; } // "Immediate" | "Twap" | "Vwap" | "Iceberg"

    /// <summary>
    /// Dato un ordine "intenzione" (symbol, side, notional totale) deciso al timeframe di
    /// decisione (es. 4h), produce un piano di child-order sul timeframe di esecuzione (es. 5m).
    /// </summary>
    ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles);
}

public sealed class TwapExecutionAlgorithm : IExecutionAlgorithm { /* N fette uguali */ }
public sealed class VwapExecutionAlgorithm : IExecutionAlgorithm { /* pesate su profilo volume storico intraday */ }
public sealed class IcebergExecutionAlgorithm : IExecutionAlgorithm { /* clip fisso, refill */ }
public sealed class ImmediateExecutionAlgorithm : IExecutionAlgorithm { /* comportamento ODIERNO: un solo ordine — default, backward-compatible */ }
```

Due punti di aggancio distinti, entrambi additivi:

1. **Backtest** — nuovo `ExecutionSimulator` che, dato un `ExecutionPlan` e le candele fini
   (es. 5m dentro una barra 4h), simula i fill con slippage/parziali invece di assumere fill
   istantaneo a chiusura candela (assunzione odierna di `BacktestEngine`). Attivabile per
   `IStrategy` che dichiarano un `ExecutionAlgorithm` diverso da `Immediate`; il default resta
   il comportamento attuale — **nessuna regressione sui backtest esistenti**.
2. **Live** — nuovo `ExecutionWorker` (stesso pattern di `TradingWorker`/`PromotionWorker`:
   `BackgroundService` scoped) che, quando `TradingEngine` decide di aprire/chiudere una
   posizione, invece di chiamare `PlaceOrderAsync` direttamente delega a `IExecutionAlgorithm`
   (scelto per gamba, nuovo campo opzionale `ExecutionAlgorithmName` su `EnsembleStrategy`/
   `SavedStrategy`, default `"Immediate"`) che schedula gli ordini figli nel tempo.

**Perché conviene per crypto**: fee+slippage su un singolo fill di size significativa possono
costare più dello spread stesso; TWAP/VWAP riducono l'impatto di mercato distribuendo l'ordine.
Stima onesta (non verificabile senza dati reali di order book, quindi da trattare come ipotesi
da validare in Paper, non come fatto): il 10-20% di miglioramento citato nella proposta originale
è plausibile per size grandi, marginale per size piccole — **da misurare con l'Experiment
Tracker (§1.3)**, non da assumere.

**Effort**: alto (algoritmi + simulator + worker + UI). **Rischio**: medio — tocca
`TradingEngine`, va fatto con un default 100% retrocompatibile. **Dipendenze nuove**: nessuna.

---

### 1.3 — Experiment Tracking ⭐⭐ (P1, abilita la misura di tutto il resto)

**Verificato**: `Services/Pipeline/PipelineEntities.cs` ha già `PipelineRun` (Id, ConfigurationId,
Status, ContextSnapshotJson, StageSummariesJson, RecommendationJson) + `PipelineArtifact`
(RunId, Kind, PayloadJson) — **è, di fatto, un piccolo MLflow**, ma **cablato solo al Pipeline a
15 stadi**. Un training manuale in `/ml`, uno sweep in `/optimization`, una campagna di
`StrategyHunter` non producono nessun record confrontabile: si vedono solo nella UI del momento,
poi si perdono. Il gap non è "non abbiamo tracking", è "il tracking che abbiamo è intrappolato in
un solo consumatore".

**Architettura proposta** — generalizzare, non duplicare: `Services/Experiments/` con
un'astrazione più leggera del `PipelineRun` (che resta invariato — il suo checkpoint/resume
per-stadio è un bisogno diverso da quello di un log sperimentale):

```csharp
public interface IExperimentTracker
{
    Task<Guid> StartRunAsync(string kind, string name, object parameters, CancellationToken ct = default);
    // kind: "Backtest" | "Optimization" | "MlTraining" | "Discovery" | "Pipeline" | "AlphaMining"
    Task LogMetricsAsync(Guid runId, IReadOnlyDictionary<string, decimal> metrics, CancellationToken ct = default);
    Task LogArtifactAsync(Guid runId, string kindTag, object payload, CancellationToken ct = default);
    Task CompleteAsync(Guid runId, string status, CancellationToken ct = default); // "Completed" | "Failed"
}
```

Nuove entità EF `ExperimentRun`/`ExperimentArtifact` (stesso pattern JSON-column di
`PipelineRun`/`PipelineArtifact` — coerenza deliberata). `BacktestEngine`, `OptimizationEngine`,
`MlLab` (training) e `StrategyDiscoveryEngine` diventano ciascuno un thin wrapper che apre un run
a inizio esecuzione e lo chiude a fine — **nessuna modifica al loro comportamento**, solo
osservabilità in più. Il `PipelineEngine` può *in aggiunta* scrivere un `ExperimentRun` di kind
`"Pipeline"` accanto al suo `PipelineRun` esistente (comporre, non sostituire), così un run di
pipeline compare nella stessa tabella comparativa degli altri.

**UI `/experiments`**: tabella di tutti i run (filtro per kind/symbol/data), **confronto
side-by-side** di due run (diff parametri + diff metriche) — risponde direttamente alla domanda
dell'utente ("LightGBM con 50 fattori vs con Alpha158"). Versioning "git-like" delle config:
hash di `ParametersJson` per rilevare run con configurazione identica — nota bene: **non** un
vero content-addressable store stile git (fuori scope, complessità non giustificata qui), scelta
dichiarata esplicitamente come nelle altre "deviazioni" della roadmap.

**Effort**: medio. **Rischio**: basso (additivo, wrapper). **Perché è P1 nonostante la stella
sia ⭐⭐**: senza tracking, ogni altra proposta (Alpha158, drift detection, stacking, alpha
mining) produce risultati che non si possono confrontare rigorosamente nel tempo — è
un'infrastruttura abilitante, va prima delle idee "vistose".

---

### 1.4 — Modelli Transformer/attention (TRA-like) ⭐⭎ (P3, decisione tecnologica esplicita)

**Verificato**: nessun modello attention-based; `MlpReturnPredictor` (cap. 17 di Jansen, già
completato) è un MLP feed-forward puro C# — precedente diretto e riuso naturale del pattern.
`TorchSharp` **non è nel progetto** — decisione esplicita già presa e documentata in
`ROADMAP-ML4T.md §2`: "da introdurre solo quando la fase relativa inizia" (cap. 18-22 deep
learning sequenziale sono rimasti esclusi proprio per questo).

**Questo è un bivio, non un'implementazione**, esattamente come lo furono GARCH/TorchSharp in
passato — vanno presentate le due strade, non scelta una in autonomia:

- **(a) Attention "a mano" in C# puro**: `AttentionReturnPredictor` — self-attention a 1-2 teste
  su una finestra di T timestep × F fattori (i fattori Alpha158 di §1.1 come input naturale),
  positional encoding, feed-forward, training con backprop manuale (o autodiff inverso scritto a
  mano, come già impostato concettualmente in `MlpReturnPredictor`). Effort **alto**: attention +
  backprop attraverso softmax/matmul in C# puro è un ordine di grandezza più complesso di un MLP
  a uno strato. Nessuna nuova dipendenza nativa.
- **(b) Adottare TorchSharp ora**: implementazione di un TRA (o anche solo un Transformer
  encoder minimale) drasticamente più semplice grazie all'autodiff di libtorch, ma introduce il
  runtime nativo (centinaia di MB) che finora si è deliberatamente evitato.

**Raccomandazione**: rimandare a dopo che Alpha158 (§1.1) e stacking (§1.8) sono in produzione —
è il "prossimo salto di qualità ML" come dice l'utente stesso, non un blocco per il resto della
roadmap. Quando si arriva qui, la scelta (a) vs (b) va fatta insieme, come per GARCH.

---

### 1.5 — Concept Drift Detection statistica ⭐⭐ (P1/P2, affianca — non sostituisce)

**Verificato**: `Services/Monitoring/StrategyDecayMonitor.cs` esiste ed è **esattamente reattivo**
come descritto dall'utente — confronta Sharpe realizzato (da `TradeRecord`) vs atteso (dal
backtest/holdout), con soglie e alert. Nessun test statistico sulla distribuzione delle feature
in ingresso esiste oggi. Il gap è reale e la richiesta dell'utente di **affiancare, non
sostituire**, è corretta architetturalmente: lo Sharpe realizzato resta il giudice finale (misura
il PnL, non un proxy), la distribuzione delle feature è un **segnale anticipatore**.

**Architettura proposta** — `Services/Monitoring/Drift/` (accanto, non dentro,
`StrategyDecayMonitor`):

```csharp
public interface IFeatureDriftDetector
{
    string Name { get; } // "Psi" | "Ks" | "PageHinkley"
    DriftResult Detect(IReadOnlyList<decimal> referenceValues, IReadOnlyList<decimal> currentValues);
}

public sealed class PsiDriftDetector : IFeatureDriftDetector    // Population Stability Index a bin
public sealed class KsDriftDetector : IFeatureDriftDetector     // Kolmogorov-Smirnov 2-sample
public sealed class PageHinkleyDetector : IFeatureDriftDetector // change-point online/causale, sullo STREAM (non due campioni statici)

public interface IFeatureDriftMonitor
{
    // Per ogni fattore usato da un SavedMlModel/gamba attiva: reference = finestra di training,
    // current = ultime N candele. Soglie standard: PSI>0.2 warning, >0.25 alert; KS p<0.05.
    Task<IReadOnlyList<FactorDriftReport>> EvaluateAsync(SavedMlModel model, IReadOnlyList<OhlcvData> recentCandles, CancellationToken ct);
}
```

Wiring: nuovo `FeatureDriftWorker` (stesso pattern di `PromotionWorker`), pannello "Drift
fattori" in `/ensemble` **accanto** al pannello "Monitor decadimento" già esistente (stessa
pagina, stessa filosofia di alert, fonte diversa: distribuzione vs performance).

**Effort**: medio. **Rischio**: basso. **Dipendenze nuove**: nessuna (MathNet.Numerics copre KS;
PSI e Page-Hinkley sono aritmetica diretta).

---

### 1.6 — AutoML / Bayesian Optimization ⭐ (P3, opportunistico)

**Verificato**: `OptimizationEngine` fa grid search walk-forward esaustivo (28.440 combinazioni
nella caccia di luglio, 62.568 nella v2 con leva — numeri reali dai report). Funziona, è
"solido" (parola usata nella roadmap stessa), ma costoso quando lo spazio dei parametri cresce.

**Architettura proposta** — `Services/Optimization/Bayesian/`:

```csharp
public interface IHyperparameterOptimizer
{
    Task<ParameterCombo> SuggestNextAsync(IReadOnlyList<EvaluatedCombo> history, ParameterSpace space, CancellationToken ct);
}

public sealed class BayesianOptimizationEngine : IHyperparameterOptimizer
{
    // Surrogato: Gaussian Process con kernel RBF, media/varianza posteriore in forma chiusa
    // via MathNet.Numerics (niente libreria GP dedicata: kernel + K^-1 + predizione sono poche
    // decine di righe di algebra lineare). Acquisition: Expected Improvement.
}
```

Si affianca (non sostituisce) `OptimizationEngine`: uno switch "Grid" vs "Bayesiano" nella UI
`/optimization`, stesso `IBacktestEngine` come funzione di valutazione. **Priorità bassa come
correttamente valutato dall'utente**: ha senso solo quando lo spazio dei parametri di uno sweep
diventa proibitivo per il grid (es. >5-6 parametri continui contemporaneamente) — oggi gli sweep
reali (2-4 parametri per strategia) sono ancora nell'ordine gestibile dal grid. Da fare quando
(e se) `StrategyHunter` inizierà a sweepare spazi molto più grandi.

---

### 1.7 — Alpha Factor Mining automatico (genetic programming) — "il vero gioiello" ⭐⭐ (P2)

**Verificato**: `Services/Discovery/StrategyComposer.cs` + `Backtesting/CompositeSignalStrategy.cs`
+ `SignalCatalog.cs` (9 segnali normalizzati 0-100) — è, come dice l'utente, **combinatoria**:
AND/OR fino a 3+2 condizioni su un catalogo *fisso* di segnali. Non genera formule nuove, sceglie
combinazioni di formule esistenti. Il gap verso "formulaic alpha mining" è quindi non la
capacità di cercare, ma la capacità di **inventare l'espressione matematica stessa**.

**Architettura proposta** — `Services/AlphaMining/`, esplicitamente costruito **sopra** §1.1 (più
operatori rolling = spazio di ricerca più ricco) e riusando `FactorEvaluator` (§0) come fitness
già pronta:

```csharp
// Albero di espressione: nodi = operatori rolling (Ref, Delta, Mean, Std, Corr, Rank...) +
// operatori aritmetici (Add, Sub, Mul, Div, Abs, Log) + foglie (Close, High, Low, Volume, Open,
// costanti). Ogni nodo compila a un delegate compatibile con IAlphaFactor.Compute.
public abstract class AlphaExpressionNode
{
    public abstract IReadOnlyList<decimal?> Evaluate(IReadOnlyList<OhlcvData> candles);
}

public sealed class GeneticAlphaMiner
{
    // GeneticSharp (nuovo pacchetto NuGet, puro managed .NET — nessun runtime nativo):
    // popolazione di AlphaExpressionNode, fitness = |IC| via FactorEvaluator ESISTENTE
    // (misurato SOLO sul periodo di selezione, mai su holdout — stessa disciplina già
    // applicata in ogni campagna StrategyHunter), crossover di sottoalberi, mutazione puntuale,
    // selezione a torneo, penalità di complessità (profondità albero) contro l'overfitting.
    public Task<IReadOnlyList<MinedFactor>> MineAsync(MiningConfig config, CancellationToken ct);
}
```

Le formule sopravvissute (IC + decay + turnover accettabili sulla selezione) vanno sottoposte
allo **stesso verdetto su holdout mai visto** già usato per le strategie (§ metodo
`REPORT-CACCIA-STRATEGIE`) prima di essere fidate — coerenza totale con la disciplina
anti-overfitting già in uso, non un percorso parallelo con standard più bassi. Persistenza:
nuova entità `SavedFactor` (espressione JSON + diagnostica IC), riusabile ovunque un
`IAlphaFactor` è consumato oggi (dataset ML, `MlStrategy`, candidati per
`CompositeSignalStrategy`).

**Effort**: alto. **Dipendenze nuove**: `GeneticSharp` (NuGet, gestita, nessun runtime nativo —
compatibile col principio "C# puro" della piattaforma). **Perché dopo §1.1**: l'espressività
della ricerca è limitata dagli operatori disponibili; con soli 8 fattori attuali lo spazio di
ricerca sarebbe povero.

---

### 1.8 — Ensemble di modelli predittivi (stacking) ⭐⭐ (P2)

**Verificato**: `EnsembleManager`/`EnsembleAllocator` combinano **strategie** (allocazione di
capitale tra gambe, rolling-Sharpe, regime-aware) — è un ensemble a livello di *segnale
finale/PnL*, non a livello di *predizione*. Oggi non esiste un modo per combinare le predizioni
numeriche di più `IReturnPredictor` sullo stesso symbol/timeframe prima che diventino un segnale.

**Architettura proposta** — la scelta più elegante è **non** creare un concetto nuovo ma
un'ulteriore implementazione di `IReturnPredictor` (l'interfaccia esiste già, Fit/Predict/
Save/Load):

```csharp
public sealed class StackedReturnPredictor : IReturnPredictor
{
    private readonly IReadOnlyList<IReturnPredictor> _baseModels; // Linear + RF + LightGBM + Mlp...

    // Fit: addestra ogni modello base sulle stesse righe di IDatasetBuilder, poi un
    // meta-learner (ridge regression, leggero) su predizioni OUT-OF-FOLD ottenute con
    // IPurgedTimeSeriesCv ESISTENTE — stacking corretto (niente leakage: nessun modello base
    // vede le proprie predizioni di training nel meta-training).
    // Alternative più semplici (peso ∝ 1/RMSE di validazione, media semplice) selezionabili
    // via un parametro StackingMode.
}
```

Poiché implementa `IReturnPredictor`, si inserisce **automaticamente** in tutto ciò che oggi
consuma quell'interfaccia: `MlStrategy`, `SavedMlModel` (nuovo `ModelType="Stacked"`),
`/ml`, `/optimization`, `/ensemble` — **zero modifiche** a quei consumatori, esattamente lo
stesso pattern con cui `MlpReturnPredictor` si è inserito senza toccare nulla. Estensione
naturale suggerita dall'utente stesso (peso modulato dal regime): il `RegimeDetector` esistente
può alimentare una feature/peso aggiuntivo nel meta-learner (es. one-hot del cluster di regime
corrente) — anch'esso già disponibile system-wide, zero nuove dipendenze.

**Effort**: medio. **Rischio**: basso (additivo, pattern già collaudato tre volte per i modelli
precedenti).

---

### 1.9 — RL per l'esecuzione (menzionato, non numerato dall'utente) — P3, dipendente da §1.2/§1.4

Naturale evoluzione di §1.2 una volta che gli algoritmi TWAP/VWAP/Iceberg sono in produzione e
generano dati di fill reali: un agente (DQN/PPO) che minimizza l'implementation shortfall al
posto di uno scheduling fisso. Richiede TorchSharp (stessa decisione di §1.4) **e** uno storico
di fill/impatto di mercato che oggi non esiste (si costruisce proprio eseguendo §1.2 in Paper per
un periodo). Va trattato come Fase 5, non prima.

---

## 2. Tabella riassuntiva priorità/effort/dipendenze

| # | Proposta | Priorità utente | Stato reale oggi | Effort | Nuove dipendenze | Rischio regressione |
|---|---|---|---|---|---|---|
| 1.1 | Alpha158CSharp | ⭐⭐⭐ | 8 fattori, infrastruttura IC pronta | Medio | Nessuna | Basso |
| 1.2 | Nested Decision Execution | ⭐⭐⭐ | Assente (1 ordine, 0 layer) | Alto | Nessuna | Medio |
| 1.3 | Experiment Tracking | ⭐⭐ | Parziale (solo Pipeline) | Medio | Nessuna | Basso |
| 1.4 | Transformer/TRA | ⭐⭐ | Assente (MLP puro C# come precedente) | Alto | TorchSharp *(decisione)* | Basso |
| 1.5 | Concept Drift Detection | ⭐⭐ | Assente (decay monitor è reattivo) | Medio | Nessuna | Basso |
| 1.6 | AutoML/Bayesian | ⭐ | Grid search esaustivo, funzionante | Medio | Nessuna | Basso |
| 1.7 | Alpha mining genetico | ⭐⭐ | Solo combinatoria AND/OR | Alto | GeneticSharp | Basso |
| 1.8 | Ensemble di modelli (stacking) | ⭐⭐ | Ensemble solo a livello strategia | Medio | Nessuna | Basso |
| 1.9 | RL esecuzione | (non numerata) | Assente | Alto | TorchSharp *(condivisa con 1.4)* | Basso (a valle) |

---

## 3. Piano a fasi

Criterio di ordinamento: ROI dichiarato dall'utente, dipendenze reali tra le proposte (non
solo priorità nominale), e principio già seguito nel resto della piattaforma di non introdurre
dipendenze pesanti (TorchSharp) finché la fase che le richiede non è decisa esplicitamente.

**Fase QLIB-1 — Fondamenta di misura (nessuna nuova dipendenza)**
§1.1 Alpha158CSharp + §1.3 Experiment Tracking generalizzato. Motivo di stare insieme: appena il
catalogo fattori si allarga, serve **subito** un modo rigoroso per confrontare "modello con 8
fattori" vs "modello con 150 fattori" — costruire l'uno senza l'altro produrrebbe solo
un'impressione soggettiva di miglioramento, non una misura.

**Fase QLIB-2 — PnL reale e robustezza (nessuna nuova dipendenza)**
§1.2 Nested Decision Execution (TWAP/VWAP/Iceberg + simulator di backtest) + §1.5 Concept Drift
Detection. Entrambe incidono direttamente su denaro vero: l'esecuzione sul lato guadagno,
il drift detection sul lato protezione. Nessuna dipende dalla Fase 1, possono partire in
parallelo con essa se le risorse lo permettono.

**Fase QLIB-3 — Ensemble e ricerca avanzata (dipende da QLIB-1)**
§1.8 Stacking di modelli + §1.7 Alpha mining genetico (richiede il catalogo ampliato di §1.1 per
avere uno spazio di ricerca non banale, e il tracker di §1.3 per loggare ogni generazione).
Dipendenza nuova: `GeneticSharp` (gestita, nessun runtime nativo).

**Fase QLIB-4 — Bivio tecnologico esplicito**
§1.4 Transformer/TRA — richiede una decisione utente (attention pura in C# vs adozione
TorchSharp), da prendere con lo stesso processo già usato per altre scelte di dipendenza.
§1.6 AutoML/Bayesian — solo se/quando gli sweep di Optimization diventano proibitivi per il
grid search (non è un blocco, è opportunistico).

**Fase QLIB-5 — RL esecuzione (lungo termine)**
§1.9, dipende da QLIB-2 (dati di fill reali) e dall'esito di QLIB-4 (TorchSharp sì/no).

**Ordine consigliato**: QLIB-1 → QLIB-2 (parallelizzabile con QLIB-1) → QLIB-3 → QLIB-4 → QLIB-5.

---

## 4. Principi trasversali (invarianti, da rispettare in ogni fase)

- **Anti-look-ahead per costruzione**: ogni nuovo `IAlphaFactor`/`AlphaExpressionNode` usa solo
  `candles[0..i]`, verificato da test di troncamento (parametrico per Alpha158, §1.1).
- **Disciplina selezione/holdout**: ogni formula minata (§1.7) o combo Bayesiana (§1.6) è
  scelta SOLO sul periodo di selezione; il verdetto è sempre sull'holdout mai visto — stessa
  regola già applicata a strategie e modelli.
- **Additività architetturale**: ogni proposta si innesta su un'interfaccia esistente
  (`IAlphaFactor`, `IReturnPredictor`, `IStrategy`) invece di crearne una parallela — pattern
  già dimostrato con `MlpReturnPredictor`, `RiskParityOptimizer`, `RollingPairsSpreadAnalyzer`.
  Il criterio per capire se una nuova idea deve essere una nuova implementazione di
  un'interfaccia esistente o un modulo davvero nuovo: **se consuma o produce lo stesso tipo di
  dato di qualcosa che già esiste, implementa l'interfaccia esistente; se introduce un tipo di
  dato/decisione nuovo (es. "quando/come eseguire un ordine"), è un modulo nuovo**.
- **Nessuna nuova dipendenza nativa senza decisione esplicita**: TorchSharp resta un bivio
  discusso (§1.4/§1.9), non un'aggiunta silenziosa.
- **Tracciabilità**: ogni run di ogni tipo (backtest, sweep, training, mining) passa
  dall'Experiment Tracker (§1.3) non appena esiste, per rendere ogni altra proposta misurabile
  nel tempo, non solo "sembra funzionare meglio".

---

## 5. Prossimo passo operativo

Fase QLIB-1 è il punto di partenza naturale: **Alpha158CSharp** (`Services/Alpha/Alpha158/`,
operatori rolling + generatore di catalogo, ~110-150 fattori a orizzonti multipli) insieme
all'**Experiment Tracker** generalizzato (`Services/Experiments/`, entità EF + wrapper su
Backtest/Optimization/MlLab/Discovery + UI `/experiments`). Nessuna decisione tecnologica
pendente, nessuna nuova dipendenza nativa, rischio di regressione basso su entrambe. Su tua
conferma si parte da qui, con lo stesso standard di test (xUnit, anti-look-ahead, determinismo)
già in uso in tutta la piattaforma.
