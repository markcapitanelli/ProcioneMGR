# Home — `/`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Home.razor`](../../ProcioneMGR/Components/Pages/Home.razor) (~200 righe) |
| **Route** | `/` |
| **Sezione navigazione** | Overview |
| **Accesso** | Doppia vista: utente autenticato (home operativa) / anonimo (landing con Login/Registrati) |
| **Render mode** | `InteractiveServer` |

## A cosa serve

La Home è il **punto di ingresso e orientamento** della piattaforma. Non esegue operazioni:
mostra dove sei ("a che punto sei") e suggerisce dove andare, con tre blocchi:

1. **KPI di stato** — quattro card riassuntive: serie tracciate, candele in archivio,
   strategie salvate dall'utente, stato del trading engine.
2. **Alert operativi** — avvisi che richiedono attenzione subito (decadimento gambe ensemble,
   assenza di dati scaricati).
3. **Percorso guidato** — il workflow naturale della piattaforma in 5 passi
   (Dati → Backtest → Ottimizza → Combina → Vai live) più le card degli strumenti avanzati.

Per l'utente **non autenticato** la pagina è una landing minimale con logo, tagline e i
bottoni Login / Registrati (righe 86–97).

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| Hero compatto | 16–22 | Saluto personalizzato con il nome utente (`_displayName`) |
| Quick actions | 24–29 | 4 scorciatoie: Nuovo Backtest, Aggiorna Dati, Avvia Trading, Apri Dashboard |
| Card statistiche | 31–38 | 4 `StatCard` renderizzate da `_stats` (record `HomeStats`) |
| Alert "nessun dato" | 40–46 | Mostrato solo se `TrackedSeries == 0`: invita ad andare in Watchlist |
| Alert decadimento | 48–63 | Se `_decayAlerts` non è vuoto: elenco gambe ensemble in alert con Sharpe realizzato vs atteso e link a `ensemble#decay-monitor` |
| Percorso piattaforma | 66–74 | 5 `WorkflowStep` numerati, ognuno linka la pagina corrispondente |
| Strumenti avanzati | 76–84 | 6 `ToolCard`: Discovery, Regimi, ML Lab, Pairs, Volatilità, Sentiment |

## Come funziona (flusso del codice)

Tutto avviene in `OnInitializedAsync` (righe 107–132):

1. Recupera lo stato di autenticazione da `AuthenticationStateProvider`; se l'utente non è
   autenticato esce subito (la vista anonima non ha bisogno di dati).
2. Apre un `ApplicationDbContext` via factory e calcola i 4 KPI con query dirette:
   - `TrackedSeries.CountAsync(s => s.Enabled)` — serie con aggiornamento automatico attivo;
   - `OhlcvData.LongCountAsync()` — totale candele in archivio;
   - `SavedStrategies.CountAsync(s => s.UserId == userId)` — strategie **dell'utente corrente**;
   - `TradingEngineStates.FirstOrDefaultAsync()` — da cui deriva l'etichetta
     `"Mai avviato"` / `"{Mode} attivo"` / `"Fermo"` (riga 124).
3. Chiama `IEnsembleManager.GetDecayReportsAsync()` e filtra i soli report con `IsAlert == true`.
   La chiamata è dentro un `try/catch` vuoto: il widget è **non bloccante** — se il decay monitor
   fallisce la Home resta comunque utilizzabile (riga 131).

I tre helper `StatCard`, `WorkflowStep` e `ToolCard` (righe 134–197) sono `RenderFragment`
costruiti a mano: piccole factory di markup che tengono il template della pagina compatto.

## Servizi e classi coinvolte

| Dipendenza iniettata | Ruolo nella pagina | File |
|---|---|---|
| `IDbContextFactory<ApplicationDbContext>` | Query dirette dei KPI (conteggi serie/candele/strategie, stato engine) | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `AuthenticationStateProvider` | Nome utente e userId per personalizzare saluto e conteggio strategie | (framework ASP.NET Identity) |
| `IEnsembleManager` | `GetDecayReportsAsync()` per l'alert di decadimento delle gambe ensemble | [`Services/Ensemble/EnsembleManager.cs`](../../ProcioneMGR/Services/Ensemble/EnsembleManager.cs) |
| `DecayReport` (modello) | Sharpe realizzato vs atteso per gamba, flag `IsAlert` | [`Services/Monitoring/StrategyDecayMonitor.cs`](../../ProcioneMGR/Services/Monitoring/StrategyDecayMonitor.cs) |

## Dati letti / scritti

- **Legge**: `TrackedSeries`, `OhlcvData`, `SavedStrategies`, `TradingEngineStates` (solo conteggi/stato).
- **Scrive**: nulla. La Home è interamente read-only.

## Collegamenti con le altre pagine

La Home è l'hub: linka Watchlist, Dashboard, Backtest, Optimization, Ensemble, Trading,
Discovery, Regimes, ML Lab, Pairs Trading, Volatility e Sentiment. L'alert di decadimento
porta direttamente all'ancora `#decay-monitor` della pagina [Ensemble](ensemble.md).

## Note di design

- I KPI sono calcolati a ogni caricamento pagina, senza cache: sono 4 query leggere.
- Il pattern "widget best-effort in try/catch vuoto" (decay alert) è deliberato: un guasto in
  un sottosistema secondario non deve mai rompere la pagina d'ingresso.
