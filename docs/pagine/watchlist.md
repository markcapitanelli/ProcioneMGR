# Watchlist — `/market/watchlist`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Watchlist.razor`](../../ProcioneMGR/Components/Pages/Watchlist.razor) (~260 righe) |
| **Route** | `/market/watchlist` |
| **Sezione navigazione** | Dati & Monitoraggio |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

È il punto dove si dichiara **quali serie di mercato la piattaforma deve scaricare e tenere
aggiornate da sola**. Ogni riga della watchlist è una tripla *(Exchange, Symbol, Timeframe)*:
una volta aggiunta, un worker in background la sincronizza periodicamente senza intervento
manuale. Tutti i dati usati da backtest, ottimizzazioni, ML e analisi provengono da qui (o
dai fetch una tantum della [Dashboard](dashboard.md)).

Concetti chiave spiegati nel `GuidaPanel` (righe 17–42):
- **Abilitata/Disabilitata** — solo le serie abilitate vengono aggiornate in automatico;
  disabilitare non cancella lo storico già scaricato.
- **Elimina** — rimuove la serie dalla watchlist ma **non tocca le candele** già in archivio.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 17–42 | Spiegazione di exchange/symbol/timeframe/stato/candele/azioni |
| Form "Aggiungi serie" | 44–75 | Select exchange (enum `ExchangeName`), input symbol, select timeframe (`Timeframes.Supported`) |
| Alert di stato | 77–80 | Esito dell'ultima azione (verde/rosso) |
| Tabella "Serie tracciate" | 82–140 | Exchange, Symbol, TF, Stato + `LastSyncStatus`, conteggio candele, ultima sync, azioni per riga (Sync now / Abilita-Disabilita / Elimina) |

## Come funziona (flusso del codice)

### Caricamento — `LoadAsync` (righe 156–174)
Legge tutte le `TrackedSeries` ordinate e calcola il numero di candele per serie. Il
conteggio usa **una sola query aggregata** (`GroupBy(Symbol, Timeframe)` →
dizionario) invece di una `CountAsync` per riga: il commento alle righe 163–165 spiega che
con molte serie intraday (5m ≈ 150k candele l'una) il pattern N+1 era diventato un collo di
bottiglia a ogni caricamento pagina.

### Aggiungi — `AddAsync` (righe 176–212)
Normalizza il symbol (`Trim().ToUpperInvariant()`), verifica che la tripla non sia già
tracciata (chiave logica exchange+symbol+timeframe), poi inserisce una `TrackedSeries` con
`Enabled = true`. Il primo download vero e proprio avverrà al prossimo giro del worker in
background (o subito con "Sync now").

### Sync now — `SyncNowAsync` (righe 214–231)
Chiama `IMarketDataSyncService.SyncSeriesAsync(id)`: forza la sincronizzazione immediata
della singola serie e riporta quante candele sono state processate. È lo stesso codice usato
dal worker automatico, invocato on-demand.

### Abilita/Disabilita — `ToggleAsync` (righe 233–243)
Flip del flag `Enabled` sulla riga. Le serie disabilitate vengono saltate dal worker.

### Elimina — `DeleteAsync` (righe 245–251)
`ExecuteDeleteAsync` sulla `TrackedSeries`. Il messaggio di conferma esplicita il contratto:
*"i dati OHLCV restano nel DB"* — l'eliminazione ferma gli aggiornamenti, non distrugge lo storico.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IDbContextFactory<ApplicationDbContext>` | CRUD su `TrackedSeries` + conteggi `OhlcvData` | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `IMarketDataSyncService` | Sync on-demand di una serie (`SyncSeriesAsync`) | [`Services/Ingestion/MarketDataSyncService.cs`](../../ProcioneMGR/Services/Ingestion/MarketDataSyncService.cs) |
| `MarketDataSyncWorker` (indiretto) | Il background service che aggiorna periodicamente tutte le serie abilitate | [`Services/Ingestion/MarketDataSyncWorker.cs`](../../ProcioneMGR/Services/Ingestion/MarketDataSyncWorker.cs) |
| `RemoteMarketDataSyncService` (indiretto) | Variante remota dietro il flag `MarketData:UseRemoteIngestion` (microservizio Ingestion) | [`Services/Ingestion/RemoteMarketDataSyncService.cs`](../../ProcioneMGR/Services/Ingestion/RemoteMarketDataSyncService.cs) |
| `Timeframes.Supported` | Timeframe validi (5m/15m/1h/4h/1d) | [`Services/Exchanges/Timeframes.cs`](../../ProcioneMGR/Services/Exchanges/Timeframes.cs) |

Nota architetturale: `IMarketDataSyncService` ha due implementazioni registrate in base alla
configurazione (vedi [`Services/Ingestion/IngestionServiceCollectionExtensions.cs`](../../ProcioneMGR/Services/Ingestion/IngestionServiceCollectionExtensions.cs)):
quella in-process e quella remota che delega al microservizio `ProcioneMGR.Ingestion`
(`POST /sync/{id}`). La pagina non sa quale delle due sta usando.

## Dati letti / scritti

- **Legge**: `TrackedSeries`, `OhlcvData` (solo conteggi aggregati).
- **Scrive**: `TrackedSeries` (insert/update/delete); indirettamente `OhlcvData` via sync.

## Collegamenti con le altre pagine

- [Dashboard](dashboard.md) — fetch esplorativo una tantum; la Watchlist è il tracking stabile.
- Tutte le pagine di analisi (Backtest, Optimization, ML, …) dipendono dai dati scaricati da qui.
