# Esperimenti — `/experiments`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Experiments.razor`](../../ProcioneMGR/Components/Pages/Experiments.razor) (~335 righe) |
| **Route** | `/experiments` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

È il **registro degli esperimenti** (in stile MLflow): ogni backtest, ottimizzazione,
training ML, campagna di discovery e run di pipeline lascia una **riga confrontabile** con
parametri, metriche finali e durata. Serve a rispondere con una misura — non con
un'impressione — a domande come *"il modello con 8 fattori rende più o meno di quello con
Alpha158?"*.

Funzione chiave: selezionare **due run** con «Confronta» per vederne affiancati metriche
(con Δ colorato) e parametri (righe diverse evidenziate in giallo). L'**hash** dei parametri
è un'impronta "git-like": due run con hash identico hanno la stessa configurazione.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 16–28 | Scopo del registro e uso del confronto |
| Filtri | 30–59 | Tipo (kind), symbol, ricerca sul nome, ricarica; conteggio filtrati/totali |
| Pannello confronto | 61–126 | Visibile con 2 run selezionati: header A/B con hash, tabella metriche con Δ (B−A), tabella parametri con differenze evidenziate |
| Tabella run | 128–176 | Bottone Confronta (max 2), tipo (badge colorato per kind), nome, symbol, TF, stato, avvio, durata, riassunto metriche (prime 4), hash corto |

## Come funziona (flusso del codice)

### Caricamento — `ReloadAsync` (righe 195–205)
Ultimi 1000 `ExperimentRuns` ordinati per avvio decrescente; i kind distinti popolano il
filtro. I filtri (righe 207–216) sono applicati in memoria con `@bind:after` — reattivi a
ogni modifica senza bottone.

### Confronto (righe 218–250)
`ToggleSelect` mantiene al massimo 2 selezioni; `BuildComparison` deserializza
`MetricsJson` (dizionario nome→decimal) e appiattisce `ParametersJson` di primo livello a
stringhe leggibili (`FlattenParams`, righe 263–286 — difensivo: JSON illeggibile produce
tabella vuota, non un crash). Le chiavi mostrate sono l'**unione** di A e B, così si vedono
anche i parametri presenti in uno solo dei due run.

### Formattazioni
`Duration` sceglie h/min/s; `FmtNum` adatta i decimali all'ordine di grandezza; `Short`
tronca l'hash a 8 caratteri; badge per kind (Backtest blu, Optimization azzurro, MlTraining
verde, Discovery giallo, Pipeline scuro, AlphaMining grigio) e per stato.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IDbContextFactory<ApplicationDbContext>` | Lettura `ExperimentRuns` | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `ExperimentRun` (entità) | Riga del registro: kind, nome, symbol/TF, stato, tempi, `MetricsJson`, `ParametersJson`, `ParametersHash` | [`Services/Experiments/ExperimentEntities.cs`](../../ProcioneMGR/Services/Experiments/ExperimentEntities.cs) |
| `IExperimentTracker` (chi scrive) | API usata da Backtest/Optimization/ML/Discovery/Pipeline per registrare i run (merge JSONB atomico contro i lost-update) | [`Services/Experiments/ExperimentTracker.cs`](../../ProcioneMGR/Services/Experiments/ExperimentTracker.cs) |

## Dati letti / scritti

- **Legge**: `ExperimentRuns` (ultimi 1000).
- **Scrive**: nulla — la pagina è read-only; a scrivere sono i motori tramite
  `IExperimentTracker`.

## Collegamenti con le altre pagine

- Tutte le pagine di ricerca ([Backtest](backtest.md), [Optimization](optimization.md),
  [ML Lab](ml.md), [Discovery](discovery.md), [Alpha Mining](alpha-mining.md),
  [Pipeline](pipeline.md)) generano righe qui.
- [Registry](registry.md) — il GuidaPanel del registry rimanda qui per la genealogia dei modelli.

## Note di design

- Confronto limitato a 2 run: la UI disabilita il bottone Confronta quando 2 sono già
  selezionati, evitando stati ambigui.
- Parsing JSON sempre difensivo: il registro deve restare consultabile anche con run
  vecchi/malformati.
