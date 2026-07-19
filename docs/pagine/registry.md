# Registry Modelli — `/registry`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Registry.razor`](../../ProcioneMGR/Components/Pages/Registry.razor) (~165 righe) |
| **Route** | `/registry` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Governa il **ciclo di vita dei modelli ML**: ogni modello salvato vive in uno stadio
`Staging → Challenger → Champion`, con uscita a `Retired`. Regole cardine (dal `GuidaPanel`,
righe 16–27):

- C'è **un solo Champion per (coppia, timeframe)**.
- La promozione a Champion passa dal **gate del Deflated Sharpe**: un modello con DSR
  inferiore **non sostituisce mai** quello in carica.
- Se un Champion degrada (drift), il monitor lo **ritira e accoda un retrain** — mai
  un'azione diretta sul trading Live.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 16–27 | Le regole del ciclo di vita |
| Card per gruppo | 39–92 | Un card per (Symbol, Timeframe) con badge del Champion corrente (nome + DSR) o "Nessun Champion attivo" |
| Tabella modelli | 56–90 | Modello, tipo, stadio (badge colorato), DSR, versione, note (motivo ritiro, "retrain accodato"), azioni |

Azioni per riga, condizionate allo stadio:
- `Staging` → **→ Challenger**
- `Staging` o `Challenger` → **→ Champion** (passa dal gate DSR)
- Qualsiasi stadio tranne `Retired` → **Ritira**

## Come funziona (flusso del codice)

### Caricamento — `LoadAsync` (righe 102–119)
Legge tutti i `SavedMlModels` e li raggruppa per (Symbol, Timeframe); dentro ogni gruppo
l'ordinamento mette prima il Champion, in fondo i Retired, e nel mezzo ordina per DSR
decrescente.

### Promozioni (righe 121–150)
- `ToChallenger` → `IModelRegistry.PromoteToChallengerAsync(id)`.
- `ToChampion` → `IModelRegistry.TryPromoteToChampionAsync(id)`: restituisce un **outcome
  con motivazione** (`Promoted` + `Reason`) — se il DSR non batte il Champion in carica, la
  promozione è rifiutata e la UI mostra il perché. La logica di confronto sta tutta nel
  registry, la pagina si limita a riportare l'esito.
- `Retire` → `RetireAsync(id, motivo, requestRetrain: false)`: il ritiro manuale non accoda
  retrain (a differenza del ritiro automatico da drift).

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IModelRegistry` / `ModelRegistry` | Transizioni di stadio, gate DSR, ritiri e retrain | [`Services/Registry/ModelRegistry.cs`](../../ProcioneMGR/Services/Registry/ModelRegistry.cs) |
| `IDbContextFactory<ApplicationDbContext>` | Lettura `SavedMlModels` per la vista | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `FeatureDriftWorker` (indiretto) | Il ritiro automatico + retrain quando un Champion degrada | [`Services/Monitoring/Drift/FeatureDriftWorker.cs`](../../ProcioneMGR/Services/Monitoring/Drift/FeatureDriftWorker.cs) |

## Dati letti / scritti

- **Legge**: `SavedMlModels` (stadio, DSR, versione, note).
- **Scrive**: `SavedMlModels` (transizioni di stadio via registry).

## Collegamenti con le altre pagine

- [ML Lab](ml.md) — dove nascono i modelli (arrivano qui in Staging).
- [Esperimenti](experiments.md) — il registro dei run che li ha prodotti (link nel GuidaPanel).
- [Ensemble](ensemble.md) — il Champion è agganciabile come gamba con la sentinella
  `MlChampion` (auto-aggiornante, mai Live).
- [Trading](trading.md) — il motore risolve la sentinella Champion a runtime via
  `MlModelLoader`; il vincolo "Champion mai in Live" è imposto dal motore.

## Note di design

- La pagina non contiene logica di promozione: `TryPromoteToChampionAsync` incapsula il
  gate e restituisce la motivazione, così la regola è testabile e unica.
- Il DSR è mostrato in percentuale (probabilità che lo Sharpe osservato non sia frutto del
  caso dopo la correzione per test multipli).
