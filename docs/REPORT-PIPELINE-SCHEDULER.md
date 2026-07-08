# Report — Schedulazione Automatica delle Cacce Pipeline

## Obiettivo

Automatizzare l'esecuzione periodica delle `PipelineConfiguration`: un worker in background legge
il campo `Schedule` (già esistente ma mai consumato), calcola quando il prossimo run è dovuto, e
lo lancia da solo con `Trigger="Scheduled"` — senza che il portafoglio di strategie invecchi in
attesa di un clic manuale su `/pipeline`.

## File modificati/creati

- `ProcioneMGR.csproj` — nuovo pacchetto **Cronos 0.13.0**.
- `Services/Pipeline/PipelineEntities.cs` — `PipelineConfiguration` +2 campi
  (`ScheduleEnabled`, `NextRunAt`); commento su `Schedule` aggiornato per riflettere l'uso reale.
- `Data/Migrations/20260704025023_AddPipelineScheduling.cs` (nuova, applicata e verificata contro
  il DB reale).
- **`Services/Pipeline/PipelineSchedulerWorker.cs`** (nuovo) — il worker vero e proprio.
- `Services/Pipeline/PipelineEngine.cs` — **fix di un bug di concorrenza reale preesistente**
  (vedi §Decisioni, punto 3): la guardia "un run è già in corso" ora gira PRIMA di scrivere il
  `PipelineRun` sul DB, in `StartRunAsync` e `ResumeRunAsync`.
- `Program.cs` — `AddHostedService<PipelineSchedulerWorker>()`.
- `Components/Pages/Pipeline.razor` — editor (toggle + input cron + anteprima + avviso Live),
  colonna "Schedulazione" nella lista config, colonna/filtro "Trigger" nello storico run,
  `GuidaPanel` aggiornato; **fix di un bug reale preesistente**: `SaveConfigAsync` leggeva
  `Schedule` in `EditConfig` ma non lo scriveva mai indietro nel DB (persa a ogni salvataggio).
- `ProcioneMGR.Tests/PipelineSchedulerWorkerTests.cs` (nuovo) — 14 test (5 unitari puri + 9
  integrazione con DB reale e motore scriptato).
- `ProcioneMGR.Tests/PipelineEngineConcurrencyTests.cs` (nuovo) — 1 test di regressione
  deterministico per il bug di concorrenza corretto in `PipelineEngine`.

## Decisioni architetturali

### 1. Cronos invece di un parser fatto in casa

Scelta raccomandata dal prompt e confermata: `Cronos` è una libreria minuscola (zero
dipendenze), lo standard de-facto per il cron in .NET (usata da Hangfire), e l'utente può
copiare espressioni direttamente da crontab.guru invece di imparare una sintassi inventata
("6 hours", "1 day at 03:00"...) che avrei dovuto testare io stesso in tutti i casi limite
(fusi orari, mesi, giorni della settimana). Reuse-first: scrivere un parser di ricorrenze
calendariali corrette è un problema genuinamente non banale, non il tipico caso in cui una
libreria esterna sia eccessiva.

### 2. Nessun campo `LastRunAt`/`LastRunStatus`/`LastRunError` duplicato su `PipelineConfiguration`

Deviazione deliberata dal prompt. Motivo: **`PipelineRun` ha già `StartedAt`, `Status`,
`ErrorLog`**, interrogabile per `ConfigurationId`. Duplicare queste informazioni su
`PipelineConfiguration` avrebbe introdotto uno stato che può disallinearsi dalla fonte di
verità — ed è successo ANCHE nel comportamento letterale suggerito dal prompt: il pseudocodice
marca `LastRunStatus="Completed"` subito dopo aver chiamato `StartRunAsync`, ma **quella
chiamata ritorna immediatamente dopo aver solo AVVIATO il run in background** (verificato
leggendo `PipelineEngine.StartRunAsync`: lancia un `Task.Run` fire-and-forget e ritorna il
`Guid` subito, non attende le 15 fasi) — marcare "Completed" a quel punto sarebbe stato
semplicemente FALSO. Ho preferito derivare "ultimo run" leggendo `PipelineRuns` (già caricato
in `Pipeline.razor` per lo storico) invece di introdurre un secondo posto dove la stessa
informazione può risultare disallineata.

### 3. Bug di concorrenza reale scoperto e corretto in `PipelineEngine`

Leggendo `StartRunAsync` per capire come evitare doppi lanci (punto esplicitamente richiesto
dal prompt), ho trovato che il `PipelineRun` con `Status="Running"` viene **persistito sul DB
PRIMA** del controllo "un run è già in corso" (quel controllo vive dentro `LaunchBackground`,
chiamato dopo). Con un solo utente che clicca a mano la race è quasi impossibile da osservare;
con lo scheduler (che introduce chiamate concorrenti reali — due config dovute nello stesso
tick, o lo scheduler che corre insieme a un clic manuale) diventa un caso concreto: il secondo
`StartRunAsync` concorrente scriveva comunque la sua riga "Running" nel DB e SOLO DOPO falliva
nel lancio — lasciando per sempre una riga orfana che nessun task in background avrebbe mai
completato. **Fix**: la guardia anticipata (`_live is not null` → throw) ora gira prima di
qualunque scrittura, sia in `StartRunAsync` che in `ResumeRunAsync` (stesso bug, stesso schema).
Bloccato da un test deterministico (`PipelineEngineConcurrencyTests`, usa una fase finta che
resta bloccata finché il test non la libera, per non dipendere dai tempi macchina).
Non ho toccato altro della logica di pipeline/discovery/ottimizzazione, solo questa guardia di
concorrenza — coerente con "non toccare la logica già funzionante": qui la logica di ricerca
non è stata sfiorata, solo l'orchestrazione del lancio.

### 4. Nessun lock per-config; niente check DB "Status=Running" per la resilienza al riavvio

Il prompt suggeriva un `ConcurrentDictionary<int, SemaphoreSlim>` per-config PIÙ un controllo
sul DB per la resilienza a un riavvio a metà run. Entrambi superflui una volta chiarito che
`PipelineEngine` è già **a slot singolo globale** (`_live` è un singolo campo nullable, non un
dizionario — un solo run gira in tutta la piattaforma, non uno per config). La concorrenza è
quindi già gestita centralmente dal motore (ora corretta, vedi punto 3): lo scheduler si limita
a chiamare `StartRunAsync` e a gestire l'eccezione se lo slot è occupato. Sulla resilienza al
riavvio: **limite preesistente e condiviso con i run manuali**, non specifico allo scheduler —
dopo un crash, `_live` si azzera in memoria (comportamento invariato) e sia un clic manuale che
un tick schedulato potrebbero avviare un nuovo run mentre una vecchia riga resta "Running" per
sempre nel DB (nessun completamento automatico di righe orfane esisteva già prima di questo
lavoro). Non l'ho corretto: è un problema del motore stesso (già esisteva per l'uso manuale),
fuori dal perimetro "non toccare la logica di pipeline" — segnalato come prossimo passo.

### 5. Safety Live: **salta**, non declassa silenziosamente

Verificato leggendo `ExecutionPlanStage` che `ExecutionMode` non ha ALCUN effetto
sull'esecuzione reale (influenza solo il testo di una nota nel piano finale — il pipeline non
piazza mai ordini veri, a prescindere dal valore). Nonostante questo, ho scelto di **saltare**
(non eseguire) un run schedulato su una config in Live, invece di declassarla silenziosamente
a Paper come suggerito dal prompt: mutare la configurazione SALVATA dell'utente senza che se ne
accorga (anche solo temporaneamente, con ripristino a fine run) introduce un rischio di
race/corruzione se l'utente sta modificando la stessa config nell'editor in quel momento, per
un beneficio pressoché nullo (dato che ExecutionMode non cambia nulla nell'esecuzione). Skip +
warning esplicito è più onesto e altrettanto sicuro. Documentato nel codice e nella UI.

### 6. `IsDue`/`ComputeNextRun` resi `public` invece di `internal`

Per farli testare direttamente da `ProcioneMGR.Tests` senza `InternalsVisibleTo` — stessa
convenzione già usata altrove nel progetto (es. `StrategyComposer.BuildOosWindows`).

## Test

- **5 test unitari puri** (`PipelineSchedulerWorkerStaticTests`): `IsDue` con NextRunAt
  null/passato/futuro; `ComputeNextRun` su espressione giornaliera (verificata contro un valore
  atteso calcolato a mano) e su espressione non valida; determinismo (stesso input → stesso
  output).
- **9 test di integrazione** (`PipelineSchedulerWorkerIntegrationTests`, DB SQLite reale su file
  temp, motore `IPipelineEngine` scriptato): config dovuta e abilitata → lancia con
  `Trigger="Scheduled"`; config non dovuta → non lancia; schedulazione disabilitata → non
  lancia; **Live → salta ma avanza comunque `NextRunAt`** (non resta bloccata a martellare ogni
  tick); motore occupato (`InvalidOperationException` "già in corso") → non avanza
  `NextRunAt` (ritenta al tick successivo) e non propaga l'eccezione; altro errore → non
  propaga, avanza `NextRunAt` (non martella su un errore permanente); espressione cron non
  valida → non lancia, non crasha; più config dovute nello stesso tick → valutate
  indipendentemente.
- **1 test di regressione deterministico** (`PipelineEngineConcurrencyTests`) per il bug di
  concorrenza corretto in `PipelineEngine` (§Decisioni, punto 3).
- **Suite completa**: 460/460 verdi (444 preesistenti dopo il lavoro sul decay monitor + 16
  nuovi), 0 regressioni.
- **Build**: 0 errori, 0 warning nuovi (stesse vulnerabilità NU1903 preesistenti, non introdotte
  da questo lavoro).

## Verifica browser

- Log di avvio confermano: `PipelineSchedulerWorker avviato (check ogni 00:05:00)`.
- Migrazione `AddPipelineScheduling` applicata con successo contro il DB reale.
- Editor: toggle "Schedulazione automatica" + input cron + anteprima in tempo reale
  (`ComputeNextRun` mostrato correttamente: "Prossima esecuzione: 2026-07-05 03:00 UTC" per
  `0 3 * * *`); avviso "I run schedulati vengono saltati quando la modalità è Live" verificato
  apparire/scomparire correttamente al cambio di `ExecutionMode`.
- **Bug reale trovato e corretto durante la verifica stessa**: il primo tentativo di salvataggio
  end-to-end ha rivelato che `Schedule`/`ScheduleEnabled` non venivano scritti nel DB — non un
  problema del codice nuovo, ma un bug PREESISTENTE nell'editor (`SaveConfigAsync` non
  copiava mai `Schedule` da `_editing` alla riga DB, nonostante `EditConfig` lo leggesse
  correttamente al caricamento). Corretto (vedi §File modificati) e riverificato: dopo il fix,
  creare una config di test, impostare `*/5 * * * *` + abilitare, salvare, ricaricare la pagina
  → badge "Attiva" con tooltip "Cron: */5 * * * * — mai eseguito" confermano la persistenza
  corretta.
- Lista configurazioni: colonna "Schedulazione" presente e popolata correttamente per tutte le
  7 config reali esistenti dell'utente (tutte "Non configurata", come atteso — nessuna aveva
  mai avuto uno schedule).
- Storico run: colonna "Trigger" + filtro (Tutti/Manuali/Schedulati) verificato funzionante
  (filtrando su "Scheduled" compare solo la riga con l'icona 🕒).
- **Avvio automatico reale osservato end-to-end** (non solo simulato nei test): creata una
  configurazione di test con `Schedule="*/5 * * * *"` + `ScheduleEnabled=true`, salvata, e senza
  alcun intervento manuale il worker l'ha lanciata da sola al tick successivo:
  ```
  info: ProcioneMGR.Services.Pipeline.PipelineSchedulerWorker[0]
        Run schedulato avviato: config 8 'Nuovo pipeline' -> run 4f88b2e4-22ee-498d-afe0-60c312e5b00a.
  ```
  Dopo il run: badge "Prossima: 07-04 07:40 UTC" (avanzato correttamente di 5 minuti), tooltip
  "Cron: */5 * * * * — ultimo: 2026-07-04 07:35 (Completed)" (derivato da `PipelineRuns`, non da
  un campo duplicato — conferma la decisione del punto 2), storico con riga
  `🕒 | 00:00:12 | Completed`. Configurazione di test poi eliminata per non lasciare residui
  nell'elenco reale dell'utente.

## Prossimi passi consigliati

Suggerimento emerso da questo lavoro: il limite di resilienza al riavvio descritto al punto 4
delle decisioni (righe "Running" potenzialmente orfane dopo un crash) è preesistente e non
specifico allo scheduler, ma diventa più rilevante ora che l'automazione gira senza supervisione
umana continua. Una soluzione futura ragionevole: all'avvio dell'app, un controllo una tantum
che marca "Failed" (con un `ErrorLog` esplicativo) ogni `PipelineRun` rimasto "Running" da prima
del riavvio — coerente con `PipelineEngine.ResumeRunAsync` già esistente, che permetterebbe
all'utente di riprenderlo dal checkpoint se lo desidera, invece di lasciarlo bloccato per sempre
in uno stato ambiguo.
