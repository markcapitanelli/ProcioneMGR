# Analisi Serie — `/market-analysis`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/MarketAnalysis.razor`](../../ProcioneMGR/Components/Pages/MarketAnalysis.razor) (~600 righe) |
| **Route** | `/market-analysis` |
| **Sezione navigazione** | Dati & Monitoraggio |
| **Accesso** | `[Authorize]` — qualsiasi utente autenticato |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Applica alla serie storica le **analisi "a priori" del metodo quantitativo** (impostazione
Trombetta/McAllen): prima di scrivere una strategia si misura se lo strumento ha
comportamenti ripetitivi sfruttabili. Solo se emerge un'inefficienza statisticamente
consistente vale la pena codificarla in un trading system.

Le analisi prodotte (una card per ciascuna):

| Analisi | Cosa misura | Timeframe consigliato |
|---|---|---|
| Gap & Lap | Frequenza/entità di gap e lap, % di ricolmatura, esito della barra → vocazione mean-reverting vs trend-following | Alto (1d) |
| Stop loss suggerito | 95°/99° percentile del ritracciamento massimo delle barre che poi chiudono positive (e simmetrico short) | qualsiasi |
| Effetto memoria | Autocorrelazione delle variazioni % (lag 1..10) + probabilità di continuazione dopo barra positiva | qualsiasi |
| Activity Factor / bias orario | Volume medio e spinta media per ora UTC, con % di occorrenze concordi | 1h |
| Supporti/Resistenze & trend | Livelli per numero di tocchi, trend da swing, ritracciamento con soglie 33/50/66% e allerta oltre il 66% | qualsiasi |
| Pattern candlestick & grafici | Candele di inversione rilevate solo dopo un trend; doppi massimi/minimi e testa-spalle con conferma neckline | qualsiasi |
| Classificazione gap | Breakaway / runaway / exhaustion con volume, ricolmatura e island reversal | 1d |
| Conferma volumetrica | Volume medio barre up vs down nella finestra recente, con warning di divergenza prezzo/volume | qualsiasi |
| Bias giorno della settimana | Contributo medio intraday e overnight per giorno, con % concordi | 1d |

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 27–87 | Spiegazione dettagliata di ogni analisi (vero e proprio glossario metodologico) |
| Form serie | 89–143 | Symbol (datalist dei simboli in archivio), timeframe, intervallo, bottone Analizza |
| `ConfigPresets` | 92–93 | Memoria dell'ultima configurazione usata (modalità silenziosa, `PageKey="market-analysis"`) |
| `DataAvailability` | 134–136 | Mostra la copertura dati disponibile e fa **gating** del bottone Analizza (`_hasData`) |
| Card risultati | 145–454 | Le 9 card di analisi, renderizzate solo se il rispettivo report esiste |

## Come funziona (flusso del codice)

### Avvio (righe 505–511)
Carica l'elenco dei simboli distinti presenti in `OhlcvData` per l'autocompletamento, e
lo userId per i preset di configurazione.

### Analizza — `RunAsync` (righe 513–591)
1. Valida symbol e intervallo; salva la configurazione come "ultima usata" via
   `ConfigPresets.SaveLastUsedAsync()` (best-effort).
2. Azzera tutti i report precedenti (righe 528–539).
3. Carica le candele dal DB per symbol/timeframe/intervallo; se sono **meno di 30** si ferma
   con un messaggio che invita a scaricare più dati.
4. Esegue in sequenza tutti gli analizzatori (righe 565–578) — sono calcoli in-memory,
   nessuno tocca più il DB:
   - `GapLap.Analyze` + `GapLap.ClassifyGaps`
   - `Excursion.SuggestStopLoss`, `Excursion.LaggedAutocorrelation(closes, 10)`, `Excursion.ContinuationProbability`
   - `Cyclical.ActivityFactor`, `Cyclical.HourlyPriceBias`, `Cyclical.DayOfWeekBias`
   - `SupportResistance.Analyze`
   - `CandleDetector.Detect`, `ChartPatterns.Detect`
   - `Volume.ConfirmTrend` (della serie si mostra solo l'ultima finestra)

### Preset di configurazione (righe 487–503)
La pagina persiste `(Symbol, Timeframe, From, To)` come JSON tramite il componente
`ConfigPresets` (con `ShowUi="false"`: solo memoria dell'ultima configurazione, senza UI di
preset nominati). `ApplyConfigJson` è difensivo: ignora JSON malformati e timeframe non
supportati.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `GapLapAnalyzer` | Statistiche gap/lap e classificazione dei gap | [`Services/Analysis/GapLapAnalyzer.cs`](../../ProcioneMGR/Services/Analysis/GapLapAnalyzer.cs) |
| `ExcursionAnalyzer` | Stop loss percentile, autocorrelazione, probabilità di continuazione | [`Services/Analysis/ExcursionAnalyzer.cs`](../../ProcioneMGR/Services/Analysis/ExcursionAnalyzer.cs) |
| `CyclicalAnalyzer` | Activity factor orario, bias orario e per giorno della settimana | [`Services/Analysis/CyclicalAnalyzer.cs`](../../ProcioneMGR/Services/Analysis/CyclicalAnalyzer.cs) |
| `SupportResistanceAnalyzer` | Livelli S/R, trend da swing, ritracciamenti, breakout | [`Services/Analysis/SupportResistanceAnalyzer.cs`](../../ProcioneMGR/Services/Analysis/SupportResistanceAnalyzer.cs) |
| `CandlestickPatternDetector` | Doji, hammer, engulfing, key reversal (solo dopo un trend) | [`Services/Analysis/CandlestickPatternDetector.cs`](../../ProcioneMGR/Services/Analysis/CandlestickPatternDetector.cs) |
| `ChartPatternDetector` | Doppi massimi/minimi, testa e spalle, conferma neckline | [`Services/Analysis/ChartPatternDetector.cs`](../../ProcioneMGR/Services/Analysis/ChartPatternDetector.cs) |
| `VolumeAnalyzer` | Conferma volumetrica del trend, divergenze | [`Services/Analysis/VolumeAnalyzer.cs`](../../ProcioneMGR/Services/Analysis/VolumeAnalyzer.cs) |
| `ConfigPresets` (componente) | Persistenza ultima configurazione per-utente | [`Components/Shared/ConfigPresets.razor`](../../ProcioneMGR/Components/Shared/ConfigPresets.razor) + [`Services/Preferences/PageConfigStore.cs`](../../ProcioneMGR/Services/Preferences/PageConfigStore.cs) |
| `DataAvailability` (componente) | Copertura dati e gating del bottone Analizza | [`Components/Shared/DataAvailability.razor`](../../ProcioneMGR/Components/Shared/DataAvailability.razor) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (candele della serie selezionata; elenco simboli distinti).
- **Scrive**: `UserPageConfigs` (ultima configurazione usata, via `ConfigPresets`).

## Collegamenti con le altre pagine

- [Watchlist](watchlist.md) / [Dashboard](dashboard.md) — da dove arrivano i dati; il tooltip
  del bottone Analizza rimanda a Watchlist se la serie non ha dati.
- [Backtest](backtest.md) — il passo successivo naturale: trasformare l'inefficienza trovata
  in una strategia da simulare. Lo stop loss percentile suggerito qui è lo stesso approccio
  usato dal bottone "Suggerisci SL/TP" del Backtest.

## Note di design

- Tutte le analisi sono **pure funzioni in-memory** sulle candele caricate una volta sola:
  un'unica query DB per run, poi solo CPU.
- La soglia minima di 30 candele evita report statisticamente privi di senso.
- Le didascalie citano esplicitamente i capitoli di riferimento (es. "McAllen cap. 15" per
  ritracciamento >66% e divergenze volume): la pagina è pensata come strumento didattico
  oltre che operativo.
