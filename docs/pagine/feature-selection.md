# Feature Selection (IC) — `/feature-selection`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/FeatureSelection.razor`](../../ProcioneMGR/Components/Pages/FeatureSelection.razor) (~350 righe) |
| **Route** | `/feature-selection` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer`, implementa `IAsyncDisposable` |

## A cosa serve

Prima di addestrare un modello ML, misura **quali fattori (indicatori) hanno davvero un
legame con i rendimenti futuri** e quali sono solo rumore. Il criterio è
l'**Information Coefficient (IC)**: la correlazione di Spearman fra il valore del fattore
oggi e il rendimento nei periodi successivi. Il segno non conta (un segnale vale sia
dritto che invertito), quindi la classifica è per **|IC|**.

Metriche complementari (dal `GuidaPanel`, righe 28–46):
- **Information Ratio** — stabilità dell'IC su finestre mobili (IC medio ÷ std): un IC alto
  ma instabile vale meno di uno più basso ma costante.
- **Consistenza segno** — quota di finestre in cui l'IC ha lo stesso segno del full-sample
  (≥ 0.5 = segno affidabile).
- **Filtri** — |IC| minimo, IR minimo, "solo segno consistente", Top N: i sopravvissuti sono
  i fattori da passare al modello ML.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 28–46 | Definizione di IC/IR/consistenza e senso dei filtri |
| Form serie | 53–85 | Exchange, symbol, timeframe, periodo + checkbox **Includi catalogo Alpha158** (con conteggio fattori e avviso "più lento") |
| `DataAvailability` | 87–89 | Gating del bottone sui dati disponibili |
| `AdvancedPanel` | 92–117 | Orizzonte forward, Top N, |IC| minimo, IR minimo, solo segno consistente |
| Bottone + conteggio | 119–126 | "Valuta fattori" + numero di candidati correnti |
| Grafico |IC| | 136–142 | Bar chart orizzontale dei top 25 fattori (verde = IC positivo, rosso = negativo) |
| Classifica | 144–169 | Tabella completa: IC, |IC|, Info Ratio, consistenza, osservazioni, flag "Tenuto" (riga verde) |

## Come funziona (flusso del codice)

### Candidati (righe 249–256)
I fattori candidati vengono dai prototipi di `IAlphaFactorFactory` con i **parametri di
default**. Il catalogo **Alpha158** (fattori in stile Qlib, riconosciuti via
`Alpha158Catalog.TryCreate`) è opzionale perché moltiplica i tempi di calcolo; il conteggio
dei candidati è mostrato sotto il bottone.

### Valutazione — `RunAsync` (righe 258–308)
1. Carica le candele (minimo 100, altrimenti messaggio d'errore).
2. Costruisce `IcFeatureSelectionConfig` con orizzonte e filtri.
3. In `Task.Run` (CPU-bound, fuori dal thread del circuito Blazor):
   - `Selector.Rank(candidates, candles, config)` — classifica completa;
   - `Selector.Select(candidates, candles, config)` — applica i filtri e restituisce i tenuti.
4. Aggiorna tabella, set `_kept` e grafico (`_chartPending` → `OnAfterRenderAsync` →
   `barh` di charts.js).

### Preset (righe 219–247)
`PageConfig` serializza l'intero form; `ApplyConfigJson` è difensivo (enum/timeframe
validati, JSON malformato ignorato).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IIcFeatureSelector` | Rank/Select dei fattori per IC con IR e consistenza | [`Services/ML/IcFeatureSelector.cs`](../../ProcioneMGR/Services/ML/IcFeatureSelector.cs) |
| `IAlphaFactorFactory` | Prototipi dei fattori alpha (nome, parametri, default) | [`Services/Alpha/AlphaFactorFactory.cs`](../../ProcioneMGR/Services/Alpha/AlphaFactorFactory.cs) |
| `Alpha158Catalog` | Catalogo esteso di fattori in stile Qlib Alpha158 | [`Services/Alpha/Alpha158/Alpha158Catalog.cs`](../../ProcioneMGR/Services/Alpha/Alpha158/Alpha158Catalog.cs) |
| `FactorEvaluator` (via selector) | Calcolo IC/IR per singolo fattore | [`Services/Alpha/FactorEvaluator.cs`](../../ProcioneMGR/Services/Alpha/FactorEvaluator.cs) |
| `wwwroot/js/charts.js` | Bar chart orizzontale | [`wwwroot/js/charts.js`](../../ProcioneMGR/wwwroot/js/charts.js) |
| `ConfigPresets` / `DataAvailability` / `AdvancedPanel` | Componenti condivisi | [`Components/Shared/`](../../ProcioneMGR/Components/Shared) |

## Dati letti / scritti

- **Legge**: `OhlcvData` (candele della serie).
- **Scrive**: `UserPageConfigs` (preset/ultima configurazione). I risultati della selezione
  **non vengono persistiti**: la lista dei fattori tenuti va usata a mano in ML Lab.

## Collegamenti con le altre pagine

- [ML Lab](ml.md) — il passo successivo: addestrare un modello usando i fattori sopravvissuti.
- [Alpha Mining](alpha-mining.md) — genera **nuovi** fattori candidati (via genetic miner)
  che possono poi essere valutati qui.

## Note di design

- La coppia `Rank`+`Select` è ridondante di proposito: la tabella mostra tutti i fattori
  (anche gli scartati) per far capire *perché* un fattore non è passato.
- Il grafico mostra |IC| ma colora per segno: si vede a colpo d'occhio se un fattore informa
  in direzione "normale" o contrarian.
