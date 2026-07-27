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
| **Deriva nel tempo** | 172–260 | **[D2]** IC finestra per finestra con sparkline, riferimento vs recente, verdetto per fattore |

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

### Deriva nel tempo — `AnalyzeDriftAsync` — **[D2]**

L'IC full-sample della classifica qui sopra è **una media su tutto il periodo**: un fattore che
informava bene nel 2024 e ha smesso nel 2026 può ancora mostrare un |IC| dignitoso. Questo pannello
guarda la stessa misura **finestra per finestra**, e dà un verdetto: stabile / si è spento / segno
invertito / dati insufficienti.

**Il pavimento di rumore è il concetto centrale.** L'errore standard di una correlazione attorno a
zero vale circa `1,96/√n`: su 250 osservazioni è 0,124, su 2500 è 0,039. Confrontare l'IC con la
soglia economica 0,02 senza tenerne conto significa promuovere il caso a segnale — e produrre
allarmi, comprese finte "inversioni di segno", ogni volta che il rumore gira storto. La soglia
operativa è quindi `max(0,02, pavimento)`, mostrata in chiaro accanto al bottone e in una colonna
della tabella.

**Ampiezza della finestra**: proposta automaticamente perché produca ~10 finestre sui dati
disponibili (`SuggestDriftWindow`). È un baratto dichiarato — finestre larghe vedono segnali più
deboli ma danno meno punti nel tempo — e resta modificabile.

**Finestre non sovrapposte**: pochi punti indipendenti invece di molti punti correlati per
costruzione, coerente con la lezione già pagata dalla piattaforma sulla significatività fabbricata.

**Nessuna persistenza e nessuna azione automatica**: l'IC storico è una funzione deterministica
delle candele, quindi si ricalcola invece di essere salvato (nessuna migrazione, nessuno stato da
tenere allineato); e il pannello segnala soltanto, come `StrategyDecayMonitor`.

Misura dal vivo (BTC/USDT 1h, 26.929 candele, finestra 2500): **MeanReversion** 0,050 → 0,027 e
**RSI** −0,049 → −0,029 sono scesi sotto il pavimento; gli altri otto fattori non hanno mai
superato la soglia in nessun periodo.

### Preset (righe 219–247)
`PageConfig` serializza l'intero form; `ApplyConfigJson` è difensivo (enum/timeframe
validati, JSON malformato ignorato).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IIcFeatureSelector` | Rank/Select dei fattori per IC con IR e consistenza | [`Services/ML/IcFeatureSelector.cs`](../../ProcioneMGR/Services/ML/IcFeatureSelector.cs) |
| `IFactorDriftAnalyzer` | **[D2]** IC rolling, pavimento di rumore, verdetto di deriva | [`Services/Alpha/FactorDriftAnalyzer.cs`](../../ProcioneMGR/Services/Alpha/FactorDriftAnalyzer.cs) |
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
