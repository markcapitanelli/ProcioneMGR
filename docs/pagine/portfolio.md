# Portafoglio — `/portfolio`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/PortfolioOptimization.razor`](../../ProcioneMGR/Components/Pages/PortfolioOptimization.razor) (~430 righe) |
| **Route** | `/portfolio` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IAsyncDisposable` |

## A cosa serve

Risponde alla domanda "**come dividere il capitale tra più asset?**", con la premessa che
comprare più crypto non basta a diversificare (si muovono spesso insieme a BTC). La pagina
allinea i rendimenti storici dei simboli scelti per timestamp e calcola **quattro
allocazioni a confronto**, ognuna con una filosofia diversa. Nessuna viene applicata
automaticamente al trading: è uno strumento di analisi.

| Metodo | Filosofia | Caveat |
|---|---|---|
| MV — Max Sharpe | Markowitz: massimo rendimento per unità di rischio storico | La più sensibile agli errori di stima; scetticismo su storici brevi |
| MV — Min Varianza | Minima volatilità possibile; usa solo le covarianze | Spesso il punto di partenza più sensato |
| ERC — Equal Risk Contribution | Ogni asset contribuisce ugualmente al rischio totale | Verificabile nel grafico dei contributi |
| HRP — Hierarchical Risk Parity | Clustering gerarchico sulla correlazione, rischio ripartito tra gruppi e poi dentro i gruppi | Non inverte la covarianza: regge panieri numerosi/storici corti |

In più la **PCA dei fattori di rischio**: se la prima componente spiega >70–80% della
varianza, il paniere si muove come un solo asset e la diversificazione è illusoria.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 24–47 | Le quattro filosofie + vincoli + PCA spiegati |
| Configurazione | 49–103 | Multi-select simboli (Ctrl+click), timeframe, lookback (50–20.000), componenti PCA, peso min/max %, risk-free annuo |
| KPI | 107–113 | Simboli, barre allineate, periodo, **shrinkage Ledoit-Wolf δ**, varianza spiegata PCA |
| Confronto pesi | 115–156 | Tabella simbolo × metodo con footer vol/rendimento/Sharpe annualizzati e il caveat "sono IN-SAMPLE" |
| Donut per metodo | 158–169 | 4 grafici a ciambella dei pesi |
| Contributi rischio ERC | 171–183 | Verifica: devono essere ~uguali; se no, i vincoli min/max stanno forzando la soluzione |
| PCA | 184–196 | Varianza spiegata per componente |

## Come funziona (flusso del codice)

### Validazioni (righe 239–249)
Almeno 2 simboli; fattibilità dei vincoli (`maxWeight × n ≥ 100%`, altrimenti messaggio
esplicito "vincoli insoddisfacibili").

### Caricamento e allineamento (righe 251–272, 288–294)
Ultime N candele per simbolo (stesso timeframe), poi `ReturnMatrixBuilder.BuildAlignedReturns`
tiene **solo i timestamp presenti per tutti** i simboli. Guardia: minimo 30 periodi condivisi,
con messaggio che invita a controllare la sovrapposizione degli storici.

### Calcolo (righe 286–315, in `Task.Run`)
1. I 4 ottimizzatori girano sulla **stessa matrice di rendimenti allineati** con la stessa
   configurazione (min/max, risk-free, periodi/anno derivati dal timeframe).
2. La covarianza usata è **Ledoit-Wolf** (shrinkage δ mostrato tra i KPI): stima più stabile
   della covarianza campionaria su storici corti.
3. Contributi di rischio dell'ERC ricalcolati sulla stessa covarianza LW
   (`PortfolioMath.RiskContributions`) per la verifica visiva.
4. `IRiskFactorPca.Compute` sulla matrice per la varianza spiegata per componente.

### Statistiche in-sample — `Build` (righe 346–366)
Rendimento del portafoglio pesato periodo per periodo → media/varianza → annualizzazione →
Sharpe con risk-free. Il footer della tabella ricorda che sono numeri **in-sample**: utili a
confrontare i metodi tra loro, non come promessa out-of-sample.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `MeanVarianceOptimizer` | Max Sharpe e Min Varianza (obiettivo parametrico) | [`Services/Portfolio/MeanVarianceOptimizer.cs`](../../ProcioneMGR/Services/Portfolio/MeanVarianceOptimizer.cs) |
| `RiskParityOptimizer` | ERC | [`Services/Portfolio/RiskParityOptimizer.cs`](../../ProcioneMGR/Services/Portfolio/RiskParityOptimizer.cs) |
| `HierarchicalRiskParityOptimizer` | HRP (clustering gerarchico) | [`Services/Portfolio/HierarchicalRiskParityOptimizer.cs`](../../ProcioneMGR/Services/Portfolio/HierarchicalRiskParityOptimizer.cs) |
| `PortfolioMath` | Matrici, Ledoit-Wolf, contributi di rischio | [`Services/Portfolio/PortfolioMath.cs`](../../ProcioneMGR/Services/Portfolio/PortfolioMath.cs) |
| `ReturnMatrixBuilder` | Allineamento rendimenti per timestamp | [`Services/Portfolio/ReturnMatrixBuilder.cs`](../../ProcioneMGR/Services/Portfolio/ReturnMatrixBuilder.cs) |
| `IRiskFactorPca` | PCA sulla correlazione del paniere | [`Services/ML/RiskFactorPca.cs`](../../ProcioneMGR/Services/ML/RiskFactorPca.cs) |
| `HierarchicalClustering` (via HRP) | Linkage per l'albero HRP | [`Services/ML/HierarchicalClustering.cs`](../../ProcioneMGR/Services/ML/HierarchicalClustering.cs) |
| `wwwroot/js/charts.js` | Donut, barh, bar | [`wwwroot/js/charts.js`](../../ProcioneMGR/wwwroot/js/charts.js) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (ultime N candele per ciascun simbolo selezionato).
- **Scrive**: nulla — analisi pura, i pesi non vengono applicati al trading.

## Collegamenti con le altre pagine

- [Ensemble](ensemble.md) — il "cugino operativo": lì l'allocazione è tra strategie (e viene
  eseguita), qui è tra asset (e resta analitica).
- [Analisi Serie](market-analysis.md) — i messaggi d'errore rimandano lì per ingerire dati.

## Note di design

- Tutti i metodi condividono covarianza (Ledoit-Wolf) e vincoli: il confronto è ad armi pari.
- Il caveat in-sample è ripetuto in tabella per l'utente frettoloso: il Max Sharpe è "per
  costruzione il più bello sul passato".
- Calcolo interamente in `Task.Run`: covarianze/ottimizzazioni/PCA non bloccano il circuito.
