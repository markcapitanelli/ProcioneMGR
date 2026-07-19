# Backup Database — `/admin/backup`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Admin/Backup.razor`](../../ProcioneMGR/Components/Pages/Admin/Backup.razor) (~200 righe) |
| **Route** | `/admin/backup` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize(Roles = Admin)]` — solo Admin |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Backup e restore del **database PostgreSQL** (che contiene tutto lo stato: strategie,
modelli ML, posizioni, run pipeline, credenziali cifrate) con gli strumenti nativi
`pg_dump`/`pg_restore` (devono essere installati e nel `PATH`).

Garanzie dichiarate nel `GuidaPanel`:
- il backup è un **archivio custom compresso** (`pg_dump -Fc`): snapshot
  **transazionalmente consistente** prodotto dal server — non serve fermare l'app per un
  backup a caldo;
- subito dopo la creazione l'archivio è **verificato** con `pg_restore --list`: se non è
  leggibile viene **eliminato** e segnalato — mai conservato un backup corrotto;
- il **ripristino sovrascrive il database attivo** (`pg_restore --clean --if-exists`): da
  usare solo dopo aver fermato app/trading.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 14–41 | Cosa contiene il DB, garanzie di consistenza/verifica, avvertenza sul restore |
| Azioni globali | 43–50 | "Crea backup ora", "Aggiorna elenco", nome del DB attivo |
| Tabella backup | 67–99 | File, data, dimensione, azioni: **Verifica** e **Ripristina** con doppia conferma inline ("Conferma ripristino" / "Annulla") |

## Come funziona (flusso del codice)

- **Crea** (righe 112–143): `BackupService.CreateBackup()` in `Task.Run` (I/O-bound, su DB
  grandi richiede secondi). L'esito include dimensione e verifica; in caso di fallimento
  il messaggio riporta l'errore di dump o di integrità.
- **Verifica** (righe 145–166): `VerifyBackup(path)` riesegue `pg_restore --list`.
- **Ripristina** (righe 168–189): dopo la doppia conferma inline, `Restore(path)`; il
  messaggio finale invita a **riavviare l'app** per usare il DB ripristinato.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `DatabaseBackupService` | Orchestrazione pg_dump/pg_restore, elenco backup, verifica | [`Services/Admin/DatabaseBackupService.cs`](../../ProcioneMGR/Services/Admin/DatabaseBackupService.cs) |
| `DatabaseBackupHelper` | Invocazione dei processi esterni e parsing esiti | [`Services/Admin/DatabaseBackupHelper.cs`](../../ProcioneMGR/Services/Admin/DatabaseBackupHelper.cs) |

Nota infrastruttura: in K8s la stessa logica gira come CronJob (tool `DbBackup`
containerizzato, vedi [`tools/`](../../tools) e i manifest in [`infra/`](../../infra)).

## Dati letti / scritti

- **Legge**: elenco file in `backup/`.
- **Scrive**: file `.dump` (creazione), **intero database** (ripristino).

## Note di design

- Le credenziali exchange nel dump restano **cifrate** (il ciphertext è nel DB): il backup
  non degrada la sicurezza, ma resta legato alla master key per la decifratura.
- La verifica automatica post-dump è nata dalla riscrittura del backup dopo la migrazione
  a PostgreSQL (2026-07-09), collaudata dal vivo.
