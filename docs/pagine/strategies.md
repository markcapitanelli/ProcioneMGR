# Le mie Strategie — `/strategies`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Strategies.razor`](../../ProcioneMGR/Components/Pages/Strategies.razor) (~125 righe) |
| **Route** | `/strategies` |
| **Sezione navigazione** | Trading |
| **Accesso** | `[Authorize]` — qualsiasi utente autenticato (vede solo le proprie) |
| **Render mode** | `InteractiveServer` |

## A cosa serve

È l'**archivio personale delle configurazioni salvate**: ogni volta che in
[Backtest](backtest.md) si preme "Salva come strategia", in [Optimization](optimization.md)
"Save Best Configuration" o in [Discovery](discovery.md) si salva un risultato promettente,
la combinazione strategia+parametri finisce qui. Da questo archivio la si può ricaricare nel
Backtest o aggiungere come gamba di un [Ensemble](ensemble.md).

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 18–36 | Cos'è l'archivio e cosa fanno Carica/Elimina |
| Tabella | 43–78 | Nome, tipo (badge), parametri decodificati dal JSON, data di creazione, azioni |

## Come funziona (flusso del codice)

- **Caricamento** (righe 92–100): `SavedStrategies` filtrate per `UserId == utente corrente`
  — l'archivio è strettamente personale — ordinate per data decrescente.
- **Carica** (riga 102): naviga a `backtest?load={id}`; è il Backtest a caricare la strategia
  (vedi `LoadStrategyAsync` in quella pagina).
- **Elimina** (righe 104–111): `ExecuteDeleteAsync` con doppio filtro `Id + UserId` (nessuno
  può cancellare strategie altrui). Non influisce sui backtest già eseguiti.
- **FormatParams** (righe 113–124): decodifica difensiva del JSON parametri in `k=v` leggibili;
  se il JSON è illeggibile mostra la stringa grezza.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IDbContextFactory<ApplicationDbContext>` | CRUD su `SavedStrategies` | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `NavigationManager` | Handoff verso Backtest (`?load=`) | (framework) |
| `SavedStrategy` (entità) | Nome, `StrategyName`, `ParametersJson`, `UserId`, eventuale Sharpe di ottimizzazione | [`Data/`](../../ProcioneMGR/Data) |

## Dati letti / scritti

- **Legge**: `SavedStrategies` dell'utente corrente.
- **Scrive**: eliminazione di righe proprie.

## Collegamenti con le altre pagine

- **In ingresso**: Backtest / Optimization / Discovery (salvataggi).
- **In uscita**: Backtest (`?load=`), Ensemble (le salvate compaiono nel menu "Aggiungi salvata").
