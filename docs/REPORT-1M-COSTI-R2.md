# R2 — Dati 1m e verifica dell'edge al netto dei costi

Data: 2026-07-20 · Branch `claude/procione-trading-bot-roadmap-faa381` · Suite **1317/1317**

**Esito in una riga:** il costo è funzione del **turnover**, non della risoluzione dei dati — e il
solo vantaggio genuino del guardare il mercato più spesso (reagire agli stop) lo dà già il feed a
tick di R1, gratis.

## Perché questa fase

R1 ha reso reattive le uscite protettive. La domanda successiva della roadmap era se scendere a 1m
avesse senso. La risposta non poteva essere un'assunzione: andava misurata con la macchina di
validazione già presente (walk-forward, Deflated Sharpe, PBO).

Preparando la misura è emerso che **la macchina non era pronta a rispondere onestamente**, e la
correzione di quel difetto è diventata la parte più importante di R2.

## Il difetto trovato: la selezione girava senza attrito

`OptimizationConfiguration` non aveva un campo per lo slippage, e
`OptimizationEngine.BuildBacktestConfig` non lo impostava. Poiché `StrategyDiscoveryEngine` passa da
lì, **tutto il percorso di selezione dei candidati girava a sole commissioni**, mentre la successiva
validazione holdout della pipeline applicava i costi pieni (`PipelineCosts`: fee + slippage + funding).

Non è un problema di contabilità, è un problema di **selezione**. Ottimizzando senza attrito si
premiano i parametri ad alto turnover, il cui vantaggio apparente è esattamente il costo che non si
sta pagando. Sui timeframe lenti l'ottimismo è modesto e il gate onesto a valle lo assorbe. A 1m,
dove lo slippage pesa quanto la commissione, la classifica dei candidati si riempirebbe di strategie
perdenti **prima** che il gate onesto le veda: il top-N sarebbe già inquinato.

Misurare 1m con questo difetto in piedi avrebbe prodotto un risultato senza valore.

### Correzione

| File | Cambiamento |
|---|---|
| `Services/Optimization/OptimizationModels.cs` | Nuovo `SlippagePercent`, default **onesto** (`PipelineCosts.DefaultSlippagePercent`), non zero |
| `Services/Optimization/OptimizationEngine.cs` | `BuildBacktestConfig` lo propaga a ogni backtest |
| `Services/Discovery/StrategyDiscoveryModels.cs` | Idem su `StrategyDiscoveryConfiguration` |
| `Services/Discovery/StrategyDiscoveryEngine.cs` | Lo passa all'ottimizzatore |
| `Services/Pipeline/Stages/ModelStages.cs` | Lo stage Discovery dichiara i `PipelineCosts` e li usa — prima li dichiaravano solo gli stage di validazione |

Il default è deliberatamente l'attrito realistico e non zero: chi vuole il comportamento ottimista
deve chiederlo, non ottenerlo per dimenticanza.

## Contabilità dei costi nel backtest

`BacktestResult` ora espone `TotalFeesPaid`, `TotalSlippagePaid`, `TotalFundingPaid`, e i derivati
`TotalCosts`, `CostDragPercent` (costi in % del capitale iniziale) e `GrossReturnPercent`.

**È puramente diagnostico.** Fee e slippage erano già dentro il PnL (le prime nel `Portfolio`, il
secondo nei prezzi di fill): esporli non sposta di un centesimo nessun risultato preesistente. Un
test blocca esattamente questa proprietà, perché il modo più facile di sbagliare qui sarebbe
"sistemare" la contabilità sottraendo i costi una seconda volta.

## Ingestione 1m — deliberatamente limitata

**6 coppie** (BTC, ETH, SOL, BNB, XRP, DOGE) × **12 mesi**, non tutte e 30 le coppie della watchlist.

Il motivo è aritmetico: a 1m un anno vale ~525.000 candele per coppia. Su 30 coppie sarebbero ~15,8
milioni di righe contro i ~7,7 milioni dell'**intero** database attuale — più che raddoppiarlo per
rispondere a una domanda che si risponde su sei coppie.

La scelta delle sei non è casuale: sono le più liquide, cioè quelle dove lo slippage reale è più
basso e dove quindi 1m ha la **migliore** probabilità di funzionare. Se l'edge netto non sopravvive
qui, non sopravvive altrove, e la domanda è chiusa senza scaricare altri venti milioni di righe.

Le serie sono registrate in watchlist **disabilitate**: sei serie 1m nel ciclo di sincronizzazione
periodico sono sei richieste REST ogni 5 minuti per dati che nessuno consuma finché la misura non ha
detto se 1m è operabile.

Comando: `dotnet run --project tools/PlatformExpand -- ingest1m`

## Metodo di misura

`dotnet run --project tools/PlatformExpand -- costprofile`

Due parti, in ordine di forza dell'argomento.

**1. Il tetto strutturale — indipendente da qualunque strategia.** Confronta l'ampiezza tipica di una
candela (mediana di |rendimento| barra su barra) con il costo di un giro completo
(`2 × (fee + slippage)` = 0,30% con i default). Il rapporto dice **quante barre tipiche** un trade
deve catturare solo per andare in pari. Questa misura non è attaccabile con "dipende dai parametri":
non ne usa.

**2. Il profilo di costo delle strategie reali.** Stesse strategie, stesse coppie, stessa finestra di
calendario, su 1m / 5m / 15m / 1h con i costi onesti. Riporta mediane di turnover, rendimento lordo,
cost drag e rendimento netto, più il conteggio di quante combinazioni restano profittevoli **al
netto** rispetto a quante lo erano al lordo.

Parametri di default, di proposito: la domanda non è "quali parametri vanno bene a 1m" ma "a 1m resta
qualcosa da ottimizzare, dopo i costi?". Se il cost drag divora il lordo a parametri ragionevoli,
ottimizzare significa solo cercare più a fondo nel rumore.

## Bug incontrato eseguendo

Nessun tool in `tools/` impostava `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`,
che l'app imposta in `ProcioneMGR/Program.cs`. Le colonne sono `timestamp without time zone` e il
codice usa `DateTime` con `Kind=Utc`: senza lo switch Npgsql rifiuta la scrittura e ogni fase che
tocca l'OHLCV muore. Corretto in `PlatformExpand`; gli altri tool che accedono al DB via EF hanno lo
stesso difetto latente ed è stato aperto un task a parte.

## Risultati

Finestra: ultimi 6 mesi (2026-01-20 → 2026-07-20), 6 coppie, 13 strategie a parametri di default,
costi fee 0,1%/lato + slippage 0,05%/fill ⇒ **round-turn 0,30%**.

### 1. Il tetto strutturale

| TF | barre | \|mossa\| mediana per barra | barre da catturare per pareggiare |
|---|---|---|---|
| 1m | 1.563.846 | 0,0335% | **8,9** |
| 5m | 312.651 | 0,0747% | 4,0 |
| 15m | 104.220 | 0,1338% | 2,2 |
| 1h | 26.058 | 0,2659% | 1,1 |

A 1h un trade deve catturare poco più di una candela tipica per coprire i costi. **A 1m ne deve
catturare quasi nove**, cioè deve avere ragione sulla direzione per ~9 minuti consecutivi con
tempismo quasi perfetto, ripetutamente. Il dato non dipende da alcun parametro né da alcuna
strategia: è la geometria del mercato contro il listino prezzi dell'exchange.

### 2. Il meccanismo: la cadenza è per BARRA, non per tempo

| TF | trade mediani (6 mesi) | barre per trade |
|---|---|---|
| 1m | 5.140 | 50,7 |
| 5m | 940 | 55,4 |
| 15m | 312 | 55,6 |
| 1h | 104 | 41,8 |

Le strategie girano a una cadenza **quasi costante in barre** su tutti i timeframe — inevitabile,
visto che i lookback degli indicatori si contano in barre. Conseguenza: passare da 1h a 1m non rende
la stessa strategia "più reattiva", la trasforma in una strategia **diversa e a orizzonte 60 volte
più corto**, che nello stesso periodo di calendario fa ~50 volte più operazioni.

### 3. Cosa ne segue sul conto (size 10%, realistica)

| TF | trade | lordo% | costi% | netto% | costi/lordo |
|---|---|---|---|---|---|
| 1m | 5.140 | −0,62 | **76,89** | −79,44 | 2723% |
| 5m | 941 | −0,68 | 24,15 | −26,25 | 2299% |
| 15m | 313 | +0,04 | 8,93 | −10,37 | 670% |
| 1h | 104 | −0,33 | 3,43 | −4,14 | 388% |

Sopravvissuti al netto: **0/72 a 1m, 5m e 15m; 1/72 a 1h.**

Il cost drag va da 3,4% a 76,9% — **22 volte** — muovendosi in proporzione al turnover.

> **Onestà su cosa questa tabella NON dimostra.** Il rendimento LORDO è ~0 su tutti i timeframe
> (−0,62 / −0,68 / +0,04 / −0,33): a parametri di default queste strategie non hanno edge da
> nessuna parte. Quindi la tabella non mostra "1m distrugge un edge reale", mostra "senza edge
> lordo, più si gira più si perde, in proporzione al turnover". Il peso della conclusione sta sul
> tetto strutturale del punto 1, che non ha questa debolezza.
>
> Una prima versione girava a `PositionSizePercent = 100`: tutte le mediane saturavano a −100% e i
> timeframe non erano più distinguibili. Rifatta al 10%.

## Decisione su 1m

**Il costo è funzione del TURNOVER, non della risoluzione dei dati.** Questa è la conclusione, e ha
tre conseguenze pratiche.

**1. Le serie 1m restano DISABILITATE nel ciclo di sincronizzazione.** I dati restano (3,15M candele
già scaricate, utili come materia prima: barre a volume/controvalore via `BarBuilder`, granularità
più fine per l'analisi delle escursioni). Ma non alimentano trading.

**2. Non si fa uno sweep di discovery a 1m aspettandosi vincitori ad alto turnover.** Con lordo ~0 e
un tetto di 8,9 barre, sarebbe cercare più a fondo nel rumore — e a 525k barre per coppia
costerebbe ore di calcolo per un risultato che il punto 1 rende già improbabile. Resta possibile su
richiesta, ma con questa aspettativa dichiarata.

**3. Il vero beneficio della risoluzione fine ce l'abbiamo già, e non costa nulla.** Reagire prima
agli stop è l'unico vantaggio genuino del guardare il mercato più spesso — ed è esattamente ciò che
il feed a tick di R1 fornisce, senza aggiungere un solo trade. **I tick per il RISCHIO comprano
sicurezza; le barre 1m per le DECISIONI comprano turnover.** Sono due cose opposte, ed è facile
confonderle.

### Conseguenza per R3 (Modalità Semplice)

Il PDF proponeva un profilo di rischio **"Scalping"** fra le opzioni offerte all'utente finale.
Questa misura dice che sarebbe **vendere all'utente il modo più caro di perdere denaro**: 22 volte
il cost drag di un profilo lento, per un tetto strutturale 8 volte più alto da superare. Il profilo
"Scalping" va escluso dalla Modalità Semplice, e ora non per opinione ma per misura.

## Riproducibilità

```
dotnet run --project tools/PlatformExpand -- ingest1m      # ~48 min, 3,15M candele
dotnet run --project tools/PlatformExpand -- costprofile   # ~10 min
```

Dettaglio per singolo backtest in `tools/PlatformExpand/bin/Debug/net10.0/cost-profile.json`.
