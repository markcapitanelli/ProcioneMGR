# Dashboard — `/dashboard`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Dashboard.razor`](../../ProcioneMGR/Components/Pages/Dashboard.razor) (~385 righe) |
| **Route** | `/dashboard` |
| **Sezione navigazione** | Overview |
| **Accesso** | `[Authorize]` — qualsiasi utente autenticato; il banner promozioni è solo Admin/Manager |
| **Render mode** | `InteractiveServer`, implementa `IDisposable` |

## A cosa serve

La Dashboard fa due cose, spiegate anche dal `GuidaPanel` in cima alla pagina (righe 23–41):

1. **Vedere** un grafico a candele (OHLCV) di un simbolo con indicatori tecnici sovrapposti.
2. **Scaricare al volo** dati storici che non sono ancora in archivio (fetch *una tantum*).

La differenza chiave con [Watchlist](watchlist.md): la Watchlist configura cosa tenere
**automaticamente aggiornato in background**; la Dashboard fa un download **manuale ed
esplorativo** — utile per una prima occhiata a un simbolo prima di decidere se tracciarlo
stabilmente.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 23–41 | Spiegazione della pagina e glossario dei bottoni |
| Banner promozioni corsie | 43–74 | Solo Admin/Manager: corsie pronte per Testnet o da retrocedere, con link a Trading |
| Form dati di mercato | 78–155 | Exchange, Symbol (con `datalist` di autocompletamento), Timeframe, intervallo Da/A, bottoni Scarica/Carica simboli/Annulla, progress bar |
| Card grafico | 157–189 | Titolo con conteggio candele, checkbox indicatori (EMA 20/50, Bollinger, RSI, MACD), componente `OhlcvChart` |

## Come funziona (flusso del codice)

### Avvio — banner promozioni (righe 220–230)
`OnInitializedAsync` chiama `IPromotionEvaluator.EvaluateAllLanesAsync()` e tiene solo le
decisioni delle corsie **in esecuzione** che siano `ReadyForTestnet` (in modalità Paper) o
`ShouldDemote`. È un widget best-effort in try/catch: se la valutazione fallisce la
Dashboard resta valida.

### Scarica dati — `FetchAsync` (righe 232–284)
1. Validazioni: symbol non vuoto, intervallo `A > Da`.
2. Crea un `CancellationTokenSource` e un `Progress<IngestionProgress>` che aggiorna la
   progress bar via `InvokeAsync(StateHasChanged)`.
3. Chiama `IOhlcvIngestionService.IngestHistoricalDataAsync(exchange, symbol, timeframe, from,
   toInclusive, progress, token)` — il servizio scarica **solo le candele mancanti** (se già
   in database non le riscarica) e le persiste.
4. Al termine ricarica le candele dal DB (`LoadCandlesAsync`), ricalcola gli indicatori e
   mostra l'esito.
5. Se l'utente preme **Annulla**, l'`OperationCanceledException` viene gestita mostrando
   comunque le candele già salvate fino a quel punto (righe 268–273).

### Caricamento candele — `LoadCandlesAsync` (righe 286–301)
Query su `OhlcvData` filtrata per symbol/timeframe/intervallo, ordinata per timestamp, con
**tetto di sicurezza `Take(50_000)`** per non gonfiare il payload del grafico.

### Indicatori — `RefreshIndicatorsAsync` (righe 321–366)
Ricalcola **lato server** solo gli indicatori selezionati, sulle candele già in memoria
(nessun nuovo fetch): EMA 20/50, Bollinger (20, 2σ), RSI 14, MACD (12/26/9). Ogni serie
diventa una `IndicatorSeries` con colore e scala (`osc` = pannello oscillatore separato per
RSI/MACD) e viene passata a `OhlcvChart`. I toggle usano `@bind:after="RefreshIndicatorsAsync"`,
quindi il ricalcolo è immediato al click.

### Ciclo di vita — `Dispose` (righe 375–378)
Se l'utente naviga via a metà fetch, il token viene cancellato: senza questo, il loop di
download proseguirebbe orfano fino a fine intervallo tenendo occupate connessioni DB.

## Servizi e classi coinvolte

| Dipendenza iniettata | Ruolo nella pagina | File |
|---|---|---|
| `IOhlcvIngestionService` | Download incrementale delle candele storiche con progress e cancellazione | [`Services/Ingestion/OhlcvIngestionService.cs`](../../ProcioneMGR/Services/Ingestion/OhlcvIngestionService.cs) |
| `IExchangeClientFactory` | `Create(exchange).GetSymbolsAsync()` per l'autocompletamento dei simboli | [`Services/Exchanges/ExchangeClientFactory.cs`](../../ProcioneMGR/Services/Exchanges/ExchangeClientFactory.cs) |
| `IDbContextFactory<ApplicationDbContext>` | Lettura candele per il grafico | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `ITechnicalIndicatorsService` | Calcolo EMA / Bollinger / RSI / MACD | [`Services/Indicators/TechnicalIndicatorsService.cs`](../../ProcioneMGR/Services/Indicators/TechnicalIndicatorsService.cs) |
| `IPromotionEvaluator` | Banner "Promozioni corsie" (pronta per Testnet / retrocessione consigliata) | [`Services/Trading/PromotionEvaluator.cs`](../../ProcioneMGR/Services/Trading/PromotionEvaluator.cs) |
| `OhlcvChart` (componente) | Rendering del grafico candele + indicatori (Plotly via `wwwroot/js/charts.js`) | [`Components/Pages/OhlcvChart.razor`](../../ProcioneMGR/Components/Pages/OhlcvChart.razor) |
| `Timeframes.Supported` | Elenco dei timeframe validi nel select | [`Services/Exchanges/Timeframes.cs`](../../ProcioneMGR/Services/Exchanges/Timeframes.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (candele per il grafico); stato corsie via `IPromotionEvaluator`.
- **Scrive**: `OhlcvData` **indirettamente** tramite il servizio di ingestione (le candele
  scaricate vengono persistite e restano disponibili a tutta la piattaforma).

## Collegamenti con le altre pagine

- [Watchlist](watchlist.md) — per trasformare un'esplorazione una tantum in tracking automatico.
- [Trading](trading.md) — link "Dettagli in Trading" dal banner promozioni corsie.

## Note di design

- Il fetch è idempotente: rilanciare lo stesso intervallo non duplica candele (il servizio
  di ingestione salta quelle già presenti).
- Gli indicatori sono ricalcolati on-toggle senza toccare il DB: separazione netta tra
  "dati" (fetch) e "viste sui dati" (indicatori).
- Il limite di 50k candele protegge il circuito SignalR di Blazor Server da payload enormi.
