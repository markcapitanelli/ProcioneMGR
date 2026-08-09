# 28 — GRAFO, RAGGIUNGIBILITÀ E FLUSSI: il livello che i passaggi precedenti non potevano vedere

> **Quinto passaggio, 2026-08-08.** I primi quattro hanno guardato **cosa esiste** (file, tipi,
> membri, opzioni, test). Questo guarda **cosa è connesso a cosa**: grafo delle dipendenze,
> raggiungibilità dai punti di ingresso reali, cicli, registrazioni DI mai risolte, catene di dati
> che si interrompono a metà.
>
> È il metodo che trova ciò che nessuna lettura file-per-file può trovare, perché il difetto **non
> sta dentro un file**: sta nello spazio fra due file che dovrebbero parlarsi e non lo fanno.

---

## Sommario dei reperti

| ID | Reperto | Tipo | Severità |
|---|---|---|---|
| **E-01** | **Il funding storico è raccolto, riempito all'indietro e mai letto**: `FundingHistory` è popolato solo da un test | catena interrotta | 🔴 **High** |
| **E-02** | `IFundingHistoryProvider` registrato in DI e **mai iniettato** da nessuno | dead DI | 🟠 Medium (causa di E-01) |
| **E-03** | `BayesianSearch` registrato Singleton, ma il solo consumatore lo costruisce a mano — e **non potrebbe usare il singleton** | dead DI / fuorviante | 🟡 Medium |
| **E-04** | 7 pagine ripetono `SELECT DISTINCT Symbol` su `OhlcvData` (~12M righe) quando `TrackedSeries` (~221) ha la risposta | prestazioni / duplicazione | 🟡 Medium |
| **E-05** | `appsettings.json.example` non documenta 2 sezioni che il codice legge (`Database`, `PostMortem`) | config incompleta | 🔵 Low |
| **E-06** | Il layer `Services/` è **un'unica componente fortemente connessa da 35 moduli** | architettura | 🔵 Info (per progetto) |
| — | **Zero file di produzione irraggiungibili** — verificato, i 4 candidati sono falsi positivi | ✅ esito positivo | — |

---

## 1. Metodo

Costruito un grafo delle dipendenze **fra tipi**, ricavato dai riferimenti testuali reali, con i
doc-comment **esclusi** (citare un tipo in un `<see cref>` non è usarlo — distinzione che cambia il
risultato).

I **punti di ingresso** non sono arbitrari: sono quelli che il runtime attiva davvero.

| Classe di ingresso | Come viene attivata | Conteggio |
|---|---|---|
| `Program.cs` dei 4 host | processo | 4 |
| Componenti `.razor` | routing Blazor | 89 |
| `*ServiceCollectionExtensions` | composizione DI | ~7 |
| `BackgroundService` / `IHostedService` | host generico | ~22 |
| **Totale punti di ingresso** | | **104** |

Da lì, chiusura transitiva sui riferimenti.

| | |
|---|---:|
| File di produzione | 523 |
| Raggiungibili | **519** |
| Non raggiungibili | 4 → **tutti falsi positivi, verificati** |

### I 4 falsi positivi, e perché lo sono

Sono tutte classi di **extension method**: il grafo non li vede perché il call site nomina il
**metodo**, non la classe. Verificati uno per uno:

| Classe | Metodo | Chiamato da |
|---|---|---|
| `IdentityComponentsEndpointRouteBuilderExtensions` | `MapAdditionalIdentityEndpoints()` | `Program.cs` |
| `ExperimentTrackerExtensions` | `SafeStartRunAsync()` ecc. | **7 punti** |
| `ObservabilityExtensions` | `AddProcioneObservability()` | `Program.cs:376` |
| `DataProtectionSetup` | `AddProcioneDataProtection()` | `Program.cs:72` |

> **Esito: nessun file di produzione è codice morto.** È un risultato forte, e va detto con la
> stessa enfasi dei difetti. Gli orfani già noti (`JumpModel`, Microstructure) non compaiono qui
> perché *sono raggiungibili come tipi* — semplicemente nessuno li invoca. Sono due domande diverse,
> ed è la seconda che li ha trovati (documento 06).

---

## 2. 🔴 E-01 — Il funding storico: raccolto, riempito all'indietro, mai letto

**È il reperto più importante di questo passaggio, e nessuno dei quattro precedenti poteva trovarlo:
ogni singolo pezzo della catena esiste, è corretto e ben documentato. Manca solo l'ultimo anello.**

### La catena, pezzo per pezzo

| # | Componente | Stato |
|---|---|---|
| 1 | `SentimentSyncWorker` raccoglie i funding rate → `SentimentMetricPoint` (`Metric = "FundingRate"`, firmati) | ✅ gira |
| 2 | `tools/PlatformExpand` **riempie all'indietro la storia profonda** dei funding | ✅ esiste |
| 3 | `IFundingHistoryProvider` legge la serie e la prepara per il backtest | ✅ implementato, registrato |
| 4 | `BacktestConfiguration.FundingHistory` è il campo che la riceve | ✅ esiste |
| 5 | `FundingRateLookup.BuildOrNull(config.FundingHistory)` (`BacktestEngine.cs:209`) | ✅ implementato |
| 6 | `fundingLookup?.RateFracAt(ts, fundingFrac) ?? fundingFrac` (`BacktestEngine.cs:347`) | ✅ implementato |
| **7** | **qualcuno che assegni `config.FundingHistory`** | ❌ **NESSUNO** |

### L'evidenza

`FundingHistory` è assegnato in **un solo punto dell'intero repository**:

```
ProcioneMGR.Tests/FundingHistoryTests.cs:88        FundingHistory = history,
```

In produzione: zero. Quindi `fundingLookup` è **sempre null**, e la riga 347 ricade **sempre** sulla
costante.

### Cosa si usa al suo posto

Non zero — la costante è valorizzata ovunque conti:

| Percorso | Valore |
|---|---|
| `BacktestPageService.cs:251` | dalla UI (`cfg.FundingPercent`) |
| `PipelineModels.cs:90` | `DefaultFundingRatePercentPer8h = 0.01m` — «funding "neutro" storico dei perpetual» |
| `StrategyComposer.cs:53` | `0.01m` |
| `tools/PlatformExpand` | `0.01m` |

### Perché conta

Il funding reale è **firmato e volatile**. Una costante positiva di 0,01%:

- **addebita ai long** un costo che in periodi di funding negativo non avrebbero pagato;
- **addebita agli short** un costo che avrebbero invece **incassato** — ed è esattamente l'errore che
  il commento a `BacktestEngine.cs:342` dichiara di aver corretto: *«La vecchia costante senza segno
  addebitava il funding anche agli short — che nella realtà lo avrebbero ricevuto»*. La correzione
  del **segno** è stata fatta; l'aggancio alla **serie reale** no;
- cancella del tutto i regimi di funding negativo, che sono precisamente quelli in cui una posizione
  long viene pagata per esistere.

### La conseguenza che pesa di più

**`CarryWorker` dal vivo usa il funding REALE** (`CarryWorker.cs:135-136`, legge
`SentimentMetricPoints`). **Il backtest generale usa la costante.**

Quindi il motore che *decide* e il motore che *valida* usano due modelli di funding diversi — ed è
proprio la classe di divergenza che questa piattaforma altrove combatte con cura: il
`VolatilityScaler` è condiviso **verbatim** fra backtest e live *«così backtest e live non possono
divergere»* (`BacktestEngine.cs:232-234`). Qui quella disciplina non è stata applicata.

> Il carry è **l'unico edge che la piattaforma ha misurato come positivo**. Vale la pena sapere con
> quale modello di funding è stato misurato ogni numero.

### Un precedente identico, già documentato dal progetto

Il doc-comment di `FundingHistoryProvider` descrive la serie come *«quella che il sync del sentiment
raccoglie e **che finora nessun motore consumava**»*. T0.2 è nato per chiudere quel buco. Ha
costruito il provider — e non ha collegato l'ultimo anello.

È la stessa forma del difetto di `SeriesFreshness` (MKR/USDT ferma dieci mesi con `/watchlist` che
diceva «Abilitata»): **il dato c'è, chi lo legge no**. Il progetto ha già imparato questa lezione una
volta e l'ha scritta nei propri commenti.

### Proposta

1. Iniettare `IFundingHistoryProvider` in `BacktestPageService` e negli stage di pipeline che
   costruiscono una `BacktestConfiguration` per mercati futures.
2. Popolare `FundingHistory` quando il mercato è Futures e la serie copre il periodo; **dichiarare
   in UI** quale dei due modelli è in uso — la costante resta un fallback legittimo quando la storia
   non copre l'intervallo.
3. Rimisurare il carry con il funding reale e **confrontare** col numero storico. Se cambia, è
   un'informazione preziosa, non un fastidio.
4. Test di regressione: un backtest futures con `FundingHistory` popolata deve dare un PnL **diverso**
   da quello con la sola costante, su un periodo con funding negativo.

---

## 3. 🟡 E-03 — `BayesianSearch`: registrazione morta e fuorviante

`Program.cs:172` registra `BayesianSearch` come **Singleton**. L'unico consumatore
(`OptimizationEngine.cs:580`) lo costruisce a mano:

```csharp
var search = new BayesianSearch(new BayesianOptimizationEngine(new BayesianOptions { Seed = config.BayesianSeed }));
```

La registrazione non è solo inutilizzata: **è incompatibile col disegno**. Il seed è per-run
(`config.BayesianSeed`), mentre un Singleton avrebbe un seed fissato al boot — condividerlo
romperebbe la riproducibilità per esperimento. Il codice fa la cosa giusta; **la registrazione
suggerisce il contrario** a chi legge il composition root.

**Proposta:** rimuovere la registrazione, oppure sostituirla con una factory
(`Func<int, BayesianSearch>`) che renda esplicito che il seed è per-run.

---

## 4. 🟡 E-04 — Sette scansioni indipendenti sulla tabella più grande

Sette pagine eseguono, ciascuna per conto proprio, la stessa query su `OhlcvData` (~12M righe):

```csharp
_knownSymbols = await db.OhlcvData.Select(c => c.Symbol).Distinct().OrderBy(s => s).ToListAsync();
```

`Discovery.razor:246` · `InformationBars.razor:173` · `MarketAnalysis.razor:602` ·
`PairsTrading.razor:305` · `PortfolioOptimization.razor:231` · `Regimes.razor:319` ·
`Sentiment.razor:788` · `Volatility.razor:195`

L'indice su `OhlcvData(Symbol, Timeframe, TimestampUtc)` ha `Symbol` come colonna guida, quindi
PostgreSQL può fare una scansione **solo-indice** — ma comunque su tutte le righe: non esiste skip
scan. Il risultato sono ~30 simboli distinti.

`TrackedSeries` (≈221 righe, indice su `Exchange, Symbol, Timeframe`) ha già la risposta.

> **Sfumatura da non ignorare:** i due insiemi **non coincidono**. `TrackedSeries` elenca le serie
> *attualmente tracciate*; `OhlcvData` può contenere simboli tracciati in passato e poi rimossi. Una
> sostituzione ingenua farebbe **sparire dati storici dai menu a tendina**. Il rimedio corretto è un
> servizio condiviso (`ISymbolCatalog`) che decida la politica una volta sola e la dichiari — non
> sette copie che decidono implicitamente ciascuna la propria.

Contesto: il Filone H (prestazioni, 2026-08-05) ha lavorato sulla configurazione del DB
(`random_page_cost` 4→1,1 su NVMe, 15×). Questo pattern è rimasto.

---

## 5. 🔵 E-05 — Il template di configurazione è indietro rispetto al codice

Confronto fra le sezioni lette dal codice e quelle presenti in `appsettings.json.example`:

| | Sezioni |
|---|---|
| Lette dal codice ma **assenti** dall'example | `Database` (migrate-on-startup, 2026-08-05) · `PostMortem` (post-mortem AI) |
| Nell'example ma non lette come sezione radice | `DataProtection` · `MarketRegime` — lette con chiave completa (`MarketRegime:Enabled`), quindi **non è un difetto** |

Chi parte dall'example non sa che `Database` e `PostMortem` esistono. Sono le due sezioni più
recenti: il template non è stato aggiornato con loro.

---

## 6. 🔵 E-06 — Il layer `Services/` è una sola componente fortemente connessa

L'analisi dei cicli (Tarjan sul grafo dei moduli) restituisce **una SCC da 35 moduli**: praticamente
tutto `Services/` più `Data`. Le coppie mutuamente dipendenti sono **43**.

Le più significative:

| Coppia | Natura |
|---|---|
| `Data` ↔ 7 moduli di servizio | **per progetto**: le entità vivono accanto al servizio che le possiede (`TradingEntities.cs` in `Services/Trading/`), e `ApplicationDbContext` le referenzia tutte |
| `Backtesting` ↔ `Trading` | il backtest usa i tipi del trading e il motore usa le strategie del backtest |
| `Backtesting` ↔ `Validation`, `Optimization`, `Regime`, `Risk`, `ML` | il motore di backtest è il crocevia della ricerca |
| `Pipeline` ↔ 8 moduli | l'orchestratore per definizione tocca tutto |
| `Ensemble` ↔ `Trading` | **da leggere con attenzione — vedi sotto** |

### La verifica che vale la pena raccontare

Il codice **dichiara** un'invariante: `TradingServiceCollectionExtensions.cs:187` spiega che la fee
viva è passata come `Func<decimal>` *«perché Ensemble non importa Trading»*. Il grafo dice il
contrario. Chi ha ragione?

**Entrambi, su due oggetti diversi.** Verificato aprendo i file:

- `EnsembleManager.cs` (il **core**) importa `Data`, `Backtesting`, `Monitoring`, `Optimization`,
  `Regime` — **non `Trading`**. L'invariante regge dove conta.
- `EnsemblePageService.cs:10` importa `Trading`. È il **page service**, cioè orchestrazione di UI,
  non il core.

> L'invariante architetturale è rispettata nel nucleo; il confine di modulo lo attraversa lo strato
> di presentazione. Non è una violazione — ma chi legge solo il grafo concluderebbe di sì, e chi
> legge solo il commento concluderebbe che il modulo è pulito. **Serviva guardare entrambi.**

### `ProcioneMGR.Ingestion` ↔ `ProcioneMGR.Ml`: falso positivo istruttivo

Il grafo segnala un ciclo fra due **progetti** — impossibile in C#. Causa: entrambi definiscono una
classe con lo stesso nome, `NoOpEncryptionService`, e l'indice per nome le confonde.

Ma la verifica ha rivelato qualcosa che vale più del falso positivo: **non è copia-incolla**. Sono
due implementazioni parallele dello stesso pattern di sicurezza, ciascuna con la motivazione del
proprio host:

- Ingestion: *«il path di ingestione OHLCV non tocca ExchangeCredentials»*
- Ml: *«il path di inferenza ML legge solo SavedMlModels, sola lettura»*

Entrambe **lanciano** invece di fare passthrough, con questo ragionamento:

> *«un passthrough scriverebbe credenziali IN CHIARO su colonne che tutto il resto del sistema tratta
> come cifrate — fallimento silenzioso e pericoloso. Lanciare trasforma quello scenario in un crash
> immediato. **Conseguenza: a questo host non va distribuita NESSUNA master key.**»*

**Rilevante per C-01:** i due host satellite non hanno mai avuto la master key **per costruzione**.
Il raggio d'azione dell'esposizione è più stretto di quanto il solo `.gitignore` lasciasse temere.

---

## 7. Riepilogo delle registrazioni DI

| | |
|---|---:|
| Registrazioni totali trovate | 206 |
| Servizi distinti | 171 |
| Candidati «registrati e mai risolti» | 20 |
| → falsi positivi (hosted service risolti dall'host) | 15 |
| → falsi positivi (`DelegatingHandler` via `IHttpClientFactory`) | 1 |
| → falsi positivi (parametri opzionali con prefisso namespace: `IAiCommittee`, `IDigestNarrator`) | 2 |
| **→ reperti genuini** | **2** — `IFundingHistoryProvider` (E-02), `BayesianSearch` (E-03) |

> Due registrazioni morte su 171 servizi è un tasso molto basso. Ma **una delle due è la causa
> diretta di E-01**, che è il reperto più consequenziale di questo passaggio: una registrazione DI
> mai risolta non è un difetto estetico quando quel servizio era l'unico anello che chiudeva una
> catena di dati.

---

## 8. Priorità aggiornate dopo il quinto passaggio

| Ordine | Intervento | Perché |
|---|---|---|
| 1 | **C-01** rimuovere e ruotare i segreti (con la procedura di **D-06**) | invariato |
| 2 | **D-01** conteggio reale dei tentativi nel gate DSR | il gate che protegge dall'illusione è il più permissivo dei tre conteggi |
| 3 | **E-01** collegare il funding storico al backtest | backtest e live usano modelli di funding diversi, e il carry è l'unico edge positivo misurato |
| 4 | **D-02** unità omogenee nell'esposizione Futures | un limite che non vincola ciò che dichiara |
| 5 | **C-02** `DriveProtectiveExits` default a `false` | invariato |
| 6 | **E-03**, **E-04**, **E-05** | igiene: registrazione fuorviante, sette scansioni, template incompleto |

---

## 9. Cosa resta fuori, e quale metodo lo troverebbe

Per onestà della mappa, dopo cinque passaggi:

| Non coperto | Metodo che lo troverebbe |
|---|---|
| Comportamento a runtime (stato reale delle corsie, freschezza dei dati, latenze) | app viva su `localhost:5199` + verifica di livello 4 |
| Correttezza numerica sotto input estremi | property-based testing / fuzzing degli invarianti |
| Concorrenza reale fra worker e motore | test di stress con orologio simulato |
| Performance effettiva delle query | `EXPLAIN ANALYZE` sul DB reale |
| Deriva fra documentazione e codice nel tempo | rigenerare i documenti 21-28 periodicamente e diffarli |

L'ultima riga è la più utile: **questi documenti sono generati meccanicamente**, quindi rigenerarli
fra un mese e confrontare produce l'elenco esatto di ciò che è cambiato — inclusi i cablaggi che si
sono rotti nel frattempo.
