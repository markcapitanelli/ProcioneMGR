# Backup Database — `/admin/backup`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Admin/Backup.razor`](../../ProcioneMGR/Components/Pages/Admin/Backup.razor) (~480 righe) |
| **Route** | `/admin/backup` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize(Roles = Admin)]` — solo Admin |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Mostrare e governare le **due** copie del database PostgreSQL (che contiene tutto lo stato:
strategie, modelli ML, posizioni, run pipeline, credenziali cifrate), con gli strumenti
nativi `pg_dump`/`pg_restore` (devono essere installati e nel `PATH`):

| Sorgente | Chi la produce | Dove |
|---|---|---|
| **notturno** | `scripts/db-backup.ps1`, ogni notte alle 03:30 da un'operazione pianificata di Windows | `Backup:NightlyDirectory` (default `%USERPROFILE%\ProcioneMGR-Backup`) |
| **manuale** | il pulsante «Crea backup ora» di questa pagina | `backup/` sotto la content root |

Garanzie dichiarate nel `GuidaPanel`:
- il backup è un **archivio custom compresso** (`pg_dump -Fc`): snapshot
  **transazionalmente consistente** prodotto dal server — non serve fermare l'app per un
  backup a caldo;
- subito dopo la creazione l'archivio è **verificato** con `pg_restore --list`: se non è
  leggibile viene **eliminato** e segnalato — mai conservato un backup corrotto;
- `pg_restore --list` legge l'**indice** dell'archivio, non i blocchi di dati: prova che il
  formato è valido, non che ogni byte lo sia. La prova piena resta il drill di restore su
  server vergine (l'ultimo il 2026-07-26);
- il **ripristino sovrascrive il database attivo** (`pg_restore --clean --if-exists`): da
  usare solo dopo aver fermato app/trading.

## Il difetto che questa pagina ha chiuso (2026-08-23)

Fino al 2026-08-23 la pagina elencava **solo** `backup/` sotto la content root, dove
l'ultimo file era del **2026-07-09**. Il backup notturno funzionava — un dump al giorno,
integro, centinaia di MB — ma stava altrove, e la pagina non lo sapeva. Il risultato era un
controllo che **dava una risposta a prescindere dalla realtà**: allarmante su un backup
sano, e altrettanto silenzioso se il notturno si fosse fermato davvero.

Il difetto era stato *documentato nella Guida* invece che risolto — cioè spiegato a chi
legge, e lasciato intatto per chi guarda. Ora:

- l'elenco copre **entrambe** le cartelle, con la **sorgente dichiarata** riga per riga
  (`manuale` / `notturno`, oppure `manuale/notturno` se le due cartelle coincidono: i nomi
  dei file hanno la stessa forma e attribuirli sarebbe indovinare);
- un pannello dedicato dà il **verdetto sul notturno** — `SANO` / `FERMO` / `MAI ESEGUITO`
  / `CARTELLA ASSENTE` / `NON DETERMINABILE` — con data, età, dimensione e conteggio;
- la pagina **interroga l'operazione pianificata** (`Get-ScheduledTask` +
  `Get-ScheduledTaskInfo` via PowerShell) e ne mostra stato, ultima esecuzione, **esito** e
  prossima esecuzione. Senza questo, un task cancellato o fallito resterebbe invisibile
  finché i dump non invecchiano abbastanza da far scattare la soglia.

### Una fonte sola per la destinazione

La destinazione dello script è **parametrica** (`-Destination`): ricopiarla in C# avrebbe
creato due verità che divergono al primo cambio, e la pagina sarebbe tornata a mentire —
solo con un'altra data. La fonte unica è la sezione `Backup` di `appsettings.json`, che
leggono **sia l'app sia lo script** (dal file del repo *principale*, lo stesso da cui
`db-backup.ps1` già prende la connection string).

Per la stessa ragione `-Register` **non congela più** `-Destination`/`-KeepDays` dentro gli
argomenti del task: un argomento congelato è la stessa doppia verità, spostata di un metro.
Chi li passa a mano ottiene comunque un task con argomenti fissi, ma lo script lo dice, e
la pagina lo segnala come divergenza.

### Cosa viene detto invece di essere risolto in silenzio

| Situazione | Perché è un avviso e non una correzione |
|---|---|
| Il task scrive in una cartella diversa da quella configurata | È il falso allarme originario, mirrorato: la pagina guarderebbe una cartella e il backup ne riempirebbe un'altra |
| Nessun task con quel nome | I file eventualmente presenti sono di un'altra epoca. L'avviso riporta il comando per registrarlo |
| Il task esegue una copia dello script dentro un **worktree** | È il guasto del 2026-08-17: sei notti di backup perse in silenzio, perché il worktree ha un `appsettings.json` proprio e stantio |
| Il task non è interrogabile (non siamo su Windows, PowerShell assente, timeout) | Tacere lascerebbe credere che il silenzio sia una conferma |
| Ultima esecuzione con codice ≠ 0 | `0x41301` (in esecuzione) e `0x41303` (mai partito) sono stati normali e **non** producono avviso |
| Task disabilitato | Esiste, ma non partirà |

## Struttura della pagina

| Blocco | Contenuto |
|---|---|
| GuidaPanel | Le due sorgenti, il difetto del 2026-08-23, le garanzie di consistenza/verifica, l'avvertenza sul restore |
| Card «Backup notturno» | Verdetto, destinazione, ultimo dump, archivio, operazione pianificata, avvisi |
| Card «Configurazione» | `NightlyDirectory`, `ScheduledTaskName`, `StaleAfterHours`, `RetentionDays` + Salva |
| Azioni globali | «Crea backup ora», «Aggiorna elenco», nome del DB attivo |
| Tabella backup | File, **Sorgente**, data, dimensione, azioni: **Verifica** e **Ripristina** con doppia conferma inline |

## Come funziona (flusso del codice)

- **Caricamento**: `ListBackups()` + `ReadNightlyStatus()`, entrambe in `Task.Run` — la
  seconda avvia PowerShell per interrogare il Task Scheduler e può prendersi qualche secondo.
- **Salva configurazione**: `AdminConfigRules.Validate` (che rifiuta un percorso *relativo*:
  si risolverebbe contro la directory di lavoro, diversa fra app e Task Scheduler) →
  `IAppConfigWriter.SaveSectionAsync("Backup", …)` → **attesa** che `IOptionsMonitor` abbia
  recepito il file, invece di ridisegnare con il valore precedente.
- **Crea**: `CreateBackup()` in `Task.Run`, scrive nella cartella **manuale**.
- **Verifica**: `VerifyBackup(path)` riesegue `pg_restore --list`, su qualunque sorgente.
- **Ripristina**: dopo la doppia conferma inline, `Restore(path)`; il messaggio finale
  invita a **riavviare l'app**.
- **Guardia sui percorsi**: `Verify`/`Restore` accettano solo file dentro le due cartelle
  note. La pagina passa già solo percorsi che ha elencato lei, ma `Restore` sovrascrive
  l'intero database e un metodo pubblico che accetta qualunque percorso è un'arma che
  aspetta il chiamante distratto.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `BackupOptions` | La sezione `Backup`: fonte unica di destinazione, conservazione, soglia, nome del task | [`Services/Admin/BackupOptions.cs`](../../ProcioneMGR/Services/Admin/BackupOptions.cs) |
| `DatabaseBackupService` | Orchestrazione pg_dump/pg_restore, elenco unificato, verdetto notturno, avvisi | [`Services/Admin/DatabaseBackupService.cs`](../../ProcioneMGR/Services/Admin/DatabaseBackupService.cs) |
| `DatabaseBackupHelper` | Invocazione dei processi esterni e parsing esiti | [`Services/Admin/DatabaseBackupHelper.cs`](../../ProcioneMGR/Services/Admin/DatabaseBackupHelper.cs) |
| `ScheduledTaskProbe` | Stato reale dell'operazione pianificata (PowerShell, fail-open) | [`Services/Admin/ScheduledTaskProbe.cs`](../../ProcioneMGR/Services/Admin/ScheduledTaskProbe.cs) |
| `IAppConfigWriter` | Scrittura della sezione in `appsettings.json` | [`Services/Config/AppConfigWriter.cs`](../../ProcioneMGR/Services/Config/AppConfigWriter.cs) |

Nota infrastruttura: in K8s la stessa logica esiste come CronJob (tool `DbBackup`
containerizzato, vedi [`tools/`](../../tools) e i manifest in [`infra/`](../../infra)), ma
`dbbackup-nightly` è **sospeso di proposito**: con `emptyDir` i backup verrebbero creati e
persi alla terminazione del pod. Resta spento finché non gli si dà un PVC — ed è la ragione
per cui il backup vero passa dall'host.

## Dati letti / scritti

- **Legge**: file `.dump` nelle due cartelle; sezione `Backup` di `appsettings.json`; stato
  dell'operazione pianificata di Windows.
- **Scrive**: file `.dump` (creazione manuale), sezione `Backup` di `appsettings.json`
  (pannello), **intero database** (ripristino).

## Perché `ScheduledTaskProbe` usa PowerShell e non `schtasks`

L'output di `schtasks /Query /V` è **localizzato**: su un Windows italiano le intestazioni
sono in italiano, e un parser che le cerca in inglese fallisce in silenzio restituendo
«task assente» — un controllo che direbbe «backup non registrato» su un backup che gira.
`Get-ScheduledTask` + `ConvertTo-Json` danno nomi di proprietà stabili in ogni lingua. Il
nome del task passa per variabile d'ambiente, non concatenato nello script: arriva dalla
configurazione, e un apice ben piazzato sarebbe un'iniezione di comando.

## Note di design

- Le credenziali exchange nel dump restano **cifrate** (il ciphertext è nel DB): il backup
  non degrada la sicurezza, ma resta legato alla master key per la decifratura. Per la
  stessa ragione la destinazione dev'essere **fuori dal repository**.
- `BackupInfo.CreatedUtc` è l'**ultima scrittura**, non la creazione: è ciò che riporta
  `db-backup.ps1 -Verify` (`LastWriteTime`), e per un dump scritto in streaming è l'istante
  in cui il file è diventato un backup. Due misure diverse della stessa cosa sono due verità
  che prima o poi divergono.
- La verifica automatica post-dump è nata dalla riscrittura del backup dopo la migrazione
  a PostgreSQL (2026-07-09), collaudata dal vivo.
