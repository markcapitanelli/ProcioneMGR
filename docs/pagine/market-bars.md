# Barre informative — `/market/bars`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/InformationBars.razor`](../../ProcioneMGR/Components/Pages/InformationBars.razor) (~320 righe) |
| **Route** | `/market/bars` |
| **Sezione navigazione** | Dati & Monitoraggio |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IAsyncDisposable` |

## A cosa serve

Costruisce e confronta le **barre informative** (ML4T cap. 2): invece di chiudere una candela
ogni intervallo di tempo fisso, la barra si chiude quando è transitato un ammontare costante
di *attività* — volume scambiato (**volume bars**) o controvalore in USDT (**dollar bars**).

Il razionale, spiegato nel `GuidaPanel` (righe 18–39): le candele a tempo campionano il
mercato in modo disomogeneo (di notte poca informazione per candela, nei momenti concitati
troppa). Campionando per attività si ottiene una serie di rendimenti più vicina alla normalità
— meno code grasse, varianza più stabile — quindi input migliori per i modelli ML.

La pagina è anche un **banco di verifica**: confronta la curtosi in eccesso dei rendimenti
delle barre informative con quella delle candele temporali. Se sulla serie ricampionata la
curtosi scende, il campionamento sta funzionando.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 18–39 | Teoria del campionamento per attività + glossario (soglia, curtosi, coda scartata) |
| Configurazione | 41–93 | Symbol, timeframe base, lookback (100–100.000 candele), tipo barra (Volume/Dollar), soglia, target barre con bottone **Suggerisci** |
| Card metriche | 97–102 | Barre costruite, candele sorgente, media candele/barra, periodo coperto |
| Tabella confronto | 104–137 | N. rendimenti, std dev e curtosi in eccesso: barre informative vs candele temporali, con nota di lettura |
| Grafico | 139–142 | Serie dei close delle barre informative (spaziatura temporale variabile), Plotly nel div `ib_ts` |

## Come funziona (flusso del codice)

### Caricamento candele — `LoadCandlesAsync` (righe 176–187)
Prende le **ultime N candele** (lookback, clampato 100–100.000) del symbol/timeframe scelto,
ordinate poi in senso cronologico. L'aggregazione parte dalle candele temporali già in
piattaforma (non dai tick): per la granularità migliore si usa il timeframe più fine (5m,
default).

### Suggerisci soglia — `SuggestAsync` (righe 189–208)
Chiama `BarBuilder.SuggestVolumeThreshold` / `SuggestDollarThreshold` sull'intervallo
caricato: la soglia è calcolata come attività totale ÷ numero di barre desiderate
(`_targetBars`, default 200), così il run produce circa quel numero di barre.

### Costruisci barre — `RunAsync` (righe 210–256)
1. Guardie: almeno 100 candele, soglia positiva.
2. In `Task.Run` (per non bloccare il circuito Blazor): `BuildVolumeBars` o
   `BuildDollarBars` sulla serie, poi `ReturnStats` sia sui close delle barre costruite sia
   su quelli delle candele originali.
3. Guardia finale: se le barre prodotte sono meno di 10 la statistica non è sensata e viene
   chiesto di abbassare la soglia.
4. `_chartPending = true` → il grafico viene disegnato in `OnAfterRenderAsync`.

### Statistiche — `ReturnStats` (righe 259–277)
Funzione statica interna (testabile): log-return della serie di close, std dev e **curtosi in
eccesso** con momenti campionari semplici (m4/m2² − 3). Il footer della tabella avverte che
le std non sono confrontabili in valore assoluto (orizzonti per barra diversi): conta la forma
della distribuzione.

### Grafico e ciclo di vita (righe 279–294, 310–321)
Il modulo JS `./js/charts.js` (Plotly) è importato lazy alla prima render utile e riusato;
`DisposeAsync` smonta il grafico e rilascia il modulo, ignorando `JSDisconnectedException`
se il circuito è già caduto.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `BarBuilder` | Costruzione volume/dollar bars e suggerimento soglia (istanziato localmente, riga 168) | [`Services/Ingestion/BarBuilder.cs`](../../ProcioneMGR/Services/Ingestion/BarBuilder.cs) |
| `IDbContextFactory<ApplicationDbContext>` | Lettura candele sorgente e simboli noti | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `IJSRuntime` + `wwwroot/js/charts.js` | Grafico timeseries Plotly riusabile | [`wwwroot/js/charts.js`](../../ProcioneMGR/wwwroot/js/charts.js) |
| `AggregatedBar` (modello) | La barra costruita (OHLC, volume, EndUtc) | [`Services/Ingestion/BarBuilder.cs`](../../ProcioneMGR/Services/Ingestion/BarBuilder.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (ultime N candele del symbol/timeframe).
- **Scrive**: nulla — le barre costruite vivono solo in memoria di pagina; l'ultima barra
  incompleta viene scartata per non produrre una barra non confrontabile.

## Collegamenti con le altre pagine

- [Analisi Serie](market-analysis.md) — i messaggi d'errore rimandano lì per ingerire più dati.
- [ML Lab](ml.md) — il beneficiario concettuale: rendimenti più gaussiani = feature migliori.

## Note di design

- Il lavoro pesante è in `Task.Run`: la UI resta reattiva durante la costruzione.
- La pagina è volutamente **senza persistenza**: è uno strumento di studio comparativo, non
  una pipeline di dati.
