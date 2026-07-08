# Migrazione a PostgreSQL

Guida operativa per far girare ProcioneMGR su **PostgreSQL** (produzione) mantenendo **SQLite**
per sviluppo e test. La migrazione è **infrastrutturale**: nessun cambiamento alla logica di
business. Il codice è **dual-provider** — si sceglie il database da configurazione, senza fork.

> **Sequenza di sicurezza (leggere prima di tutto):**
> 1. **Backup verificato di `app.db`** con il tool `DbBackup` (o dalla pagina `/admin/backup`).
> 2. **Verifica integrità** del backup (`DbBackup verify`).
> 3. Solo **dopo** aver verificato il backup, procedere con la migrazione.
> 4. Se qualcosa va storto, il backup permette di ripristinare.

---

## 0. Backup sicuro di SQLite (prerequisito)

SQLite tiene le scritture recenti in un file WAL separato (`app.db-wal`). Copiare `app.db` a caldo
può catturare un file **corrotto**. Il tool esegue un **WAL checkpoint TRUNCATE** (svuota il WAL nel
file principale) e poi verifica il backup con `PRAGMA integrity_check`.

```bash
# dalla cartella tools/DbBackup
dotnet run -- backup        # checkpoint + copia verificata in backup/
dotnet run -- verify        # integrity_check sull'ultimo backup
dotnet run -- list          # elenco backup con dimensione/data
# restore è disponibile ma sovrascrive il DB attivo: usarlo solo ad app ferma
```

In alternativa: pagina **`/admin/backup`** (solo Admin) — pulsante "Crea backup ora", elenco backup,
verifica e ripristino con conferma.

---

## 1. Prerequisiti

- **PostgreSQL 14+** (testato con PostgreSQL 18).
- `psql` (client a riga di comando) oppure **pgAdmin**.
- .NET 10 SDK con lo strumento EF: `dotnet tool install --global dotnet-ef` (o `dotnet-ef` già presente).

### Installazione rapida

**Docker (il più veloce, se disponibile):**
```bash
docker run --name procione-pg -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:18
```

**Windows:** installer EDB (https://www.postgresql.org/download/windows/) — installa anche il
servizio `postgresql-x64-18` e `psql`.

**Linux (apt):** `sudo apt install postgresql-18`
**macOS (Homebrew):** `brew install postgresql@18 && brew services start postgresql@18`

---

## 2. Creazione database e utente

Con accesso da superuser (`postgres`), esegui (adatta la password):

```sql
CREATE DATABASE procionemgr;
CREATE USER procione WITH PASSWORD 'Procione2026Pg_secure';
GRANT ALL PRIVILEGES ON DATABASE procionemgr TO procione;

-- PostgreSQL 15+: il ruolo deve poter creare oggetti nello schema public
\connect procionemgr
GRANT ALL ON SCHEMA public TO procione;
ALTER DATABASE procionemgr OWNER TO procione;
```

Da riga di comando Windows (servizio locale):

```powershell
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -c "CREATE DATABASE procionemgr;"
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -c "CREATE USER procione WITH PASSWORD 'Procione2026Pg_secure';"
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -c "GRANT ALL PRIVILEGES ON DATABASE procionemgr TO procione;"
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U postgres -d procionemgr -c "GRANT ALL ON SCHEMA public TO procione; ALTER DATABASE procionemgr OWNER TO procione;"
```

> **Credenziali di default usate dal progetto** (modificabili): database `procionemgr`, utente
> `procione`, password `Procione2026Pg_secure`. In produzione **cambia la password** e NON tenerla
> in `appsettings.json`: usa User Secrets (dev) o la variabile d'ambiente `ConnectionStrings__PostgresConnection`.

---

## 3. Configurazione dual-provider

Il provider si sceglie da `appsettings.json` con la chiave `Database:Provider`
(`SQLite` — default — oppure `PostgreSQL`). Le connection string vivono in `ConnectionStrings`:

```jsonc
{
  "Database": { "Provider": "SQLite" },           // default: comportamento storico invariato
  "ConnectionStrings": {
    "DefaultConnection": "DataSource=Data/app.db;Cache=Shared",
    "PostgresConnection": "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure"
  }
}
```

`appsettings.Production.json` imposta già `Database:Provider = PostgreSQL`. Puoi anche sovrascrivere
da ambiente senza toccare i file:

```powershell
$env:Database__Provider = "PostgreSQL"
$env:ConnectionStrings__PostgresConnection = "Host=...;Database=procionemgr;Username=procione;Password=***"
```

**Come funziona nel codice** (`Program.cs`): a seconda del provider si chiama `UseNpgsql` o
`UseSqlite`. Le migrazioni sono **provider-specifiche**: quelle SQLite stanno nell'assembly
`ProcioneMGR`, quelle PostgreSQL nel progetto dedicato `ProcioneMGR.Migrations.Postgres`.

---

## 4. Applicazione dello schema (migrazioni PostgreSQL)

Le migrazioni PostgreSQL sono nel progetto `ProcioneMGR.Migrations.Postgres`, che contiene anche una
`IDesignTimeDbContextFactory` per applicarle. La connection string è letta dall'ambiente
`PostgresConnection` (o dal default locale).

```powershell
# opzionale: punta a un DB diverso dal default
$env:PostgresConnection = "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure"

dotnet ef database update `
  --project ProcioneMGR.Migrations.Postgres `
  --startup-project ProcioneMGR.Migrations.Postgres
```

Verifica che le tabelle siano state create:

```sql
\connect procionemgr
\dt
-- attese: OhlcvData, OpenPositions, SavedMlModels, PipelineRuns, AspNetUsers, ...
```

> **Nota sui tipi** (gestita automaticamente dalle migrazioni PostgreSQL generate):
> - `byte[]` (es. `SavedMlModel.ModelBytes`) → **`bytea`**
> - `decimal` → **`numeric(p,s)`** con precision/scale **per-colonna** (prezzi 18,8; volume 28,8;
>   sentiment 5,4; Sharpe 18,6) — la precisione dei prezzi crypto è preservata.
> - stringhe JSON (`ContextSnapshotJson`, `FactorsJson`, …) → **`text`**
> - `DateTime` → **`timestamp without time zone`** (coerente con i valori "naive UTC" di SQLite;
>   evita l'eccezione di Npgsql su `Kind=Unspecified`).
> - chiavi intere → **`GENERATED BY DEFAULT AS IDENTITY`** (consente insert con ID espliciti in
>   fase di migrazione dati).

### 4.1 — Generare NUOVE migrazioni PostgreSQL

Il modello è la fonte di verità in `ProcioneMGR` (host), non nella factory di design-time (che
costruisce un context "nudo" con lo schema Identity di default). Quindi le nuove migrazioni si
generano con **`--startup-project ProcioneMGR`**. Poiché `ProcioneMGR` NON referenzia il progetto
delle migrazioni (per non creare un ciclo), la sua DLL va resa disponibile all'host prima:

```powershell
$env:Database__Provider = "PostgreSQL"
$env:ConnectionStrings__PostgresConnection = "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=..."

# 1) build del progetto migrazioni e copia della DLL nel bin dell'host
dotnet build ProcioneMGR.Migrations.Postgres -c Debug
Copy-Item ProcioneMGR.Migrations.Postgres\bin\Debug\net10.0\ProcioneMGR.Migrations.Postgres.dll `
          ProcioneMGR\bin\Debug\net10.0\ -Force

# 2) genera la migrazione (host = modello corretto, incl. Identity Version3)
dotnet ef migrations add <Nome> `
  --project ProcioneMGR.Migrations.Postgres `
  --startup-project ProcioneMGR --no-build
```

> **Perché serviva questa nota (bug risolto)**: il differ di `migrations add` andava in
> `NullReferenceException` per un **drift di UNA riga** nello snapshot. `IdentityUser.LockoutEnd` è
> un `DateTimeOffset?`: a design-time Npgsql 10 lo mappa a `timestamp with time zone`, ma lo snapshot
> lo riportava come `without` (residuo storico). Il differ tentava un ALTER su quella colonna e
> crashava. Fix: allineare la riga nello snapshot a `timestamp with time zone` (nessuna migrazione,
> nessun tocco al DB — il DB reale resta `without`, mismatch dormiente e innocuo come già oggi a
> runtime, perché `LockoutEnd` non viene scritto). Da allora `migrations add` funziona e produce
> migrazioni corrette senza doverle scrivere a mano.

---

## 5. Migrazione dei dati da SQLite a PostgreSQL

Tool: `tools/DataMigration`. Copia i dati **tabella per tabella** con una strategia **ADO-to-ADO
grezza** (non passa dall'ORM):

- le colonne **cifrate** (credenziali) sono trasferite come ciphertext **byte-per-byte** — nessuna
  master key richiesta, il runtime le decifrerà con la stessa chiave;
- i **blob** binari (modelli ML) sono trasferiti byte-per-byte;
- gli **ID interi sono preservati** (insert esplicito) e i sequence identity vengono riallineati con
  `setval` al termine;
- conversioni gestite: `decimal` TEXT→`numeric` (parse invariant-culture, nessuna perdita di
  precisione), `DateTime` TEXT→`timestamp`, `bool` 0/1→`boolean`, enum-come-stringa→`text`.

**Prerequisiti:** lo schema PostgreSQL dev'essere già applicato (passo 4) e il DB deve essere
**vuoto** (oppure usa `--truncate`).

```powershell
$env:PostgresConnection = "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure"

# dalla cartella tools/DataMigration
dotnet run -- --sqlite "..\..\ProcioneMGR\Data\app.db" --pg $env:PostgresConnection

# opzioni utili:
#   --truncate      svuota le tabelle target prima di migrare (RESTART IDENTITY CASCADE)
#   --skip-ohlcv    salta OhlcvData (dati di mercato ri-scaricabili via tools/PlatformExpand; è la
#                   tabella di gran lunga più grande — ~7,45M righe)
#   --batch N       righe per transazione (default 5000)
```

> **Suggerimento pratico:** i dati davvero irrecuperabili sono strategie/modelli/posizioni/pipeline
> run, **non** le candele OHLCV (ri-scaricabili dagli exchange). Per una prima migrazione rapida usa
> `--skip-ohlcv` e poi ri-ingesta i dati di mercato con `tools/PlatformExpand`.

---

## 6. Avvio dell'app su PostgreSQL

```powershell
$env:Database__Provider = "PostgreSQL"
dotnet run --project ProcioneMGR
```

Oppure usa l'ambiente Production (che ha già `Provider = PostgreSQL`):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet run --project ProcioneMGR
```

Verifiche consigliate in browser:
1. Tutte le pagine caricano senza errori.
2. Crea un ensemble / avvia paper trading → le posizioni compaiono in `SELECT * FROM "OpenPositions";`.
3. Salva un modello in `/ml` → il blob è in `SavedMlModels.ModelBytes` (`bytea`).
4. Lancia un run pipeline → `PipelineRuns` popolato.
5. Riavvia l'app → i dati persistono.

---

## 7. Backup e restore di PostgreSQL

SQLite usa `DbBackup`; PostgreSQL ha i suoi strumenti nativi:

```bash
# backup (custom format, comprimibile e selettivo in restore)
pg_dump -U procione -h localhost -Fc procionemgr -f procionemgr.dump

# restore su DB vuoto
pg_restore -U procione -h localhost -d procionemgr --clean --if-exists procionemgr.dump
```

---

## 8. Test

- I **466 test xUnit** girano su **SQLite in-memory** e continuano a passare invariati.
- `ProviderCompatibilityTests` verifica il round-trip di blob/decimal/JSON: sempre su SQLite,
  **anche su PostgreSQL** se imposti `RUN_POSTGRES_TESTS=true` (con `POSTGRES_TEST_CONNECTION`
  opzionale verso un **DB usa-e-getta**, perché il test fa `EnsureDeleted`/`EnsureCreated`).

```powershell
$env:RUN_POSTGRES_TESTS = "true"
$env:POSTGRES_TEST_CONNECTION = "Host=localhost;Port=5432;Database=procionemgr_test;Username=procione;Password=Procione2026Pg_secure"
dotnet test ProcioneMGR.Tests
```

---

## 9. Troubleshooting

| Sintomo | Causa / rimedio |
|---|---|
| `password authentication failed for user "procione"` | Password errata in connection string, o `pg_hba.conf` non consente scram per quell'utente. |
| `permission denied for schema public` | PostgreSQL 15+: manca `GRANT ALL ON SCHEMA public TO procione` (vedi §2). |
| `relation "OpenPositions" does not exist` | Schema non applicato: esegui `dotnet ef database update` (§4). Ricorda che i nomi sono **case-sensitive** e quotati (PascalCase). |
| `Cannot write DateTime with Kind=UTC to PostgreSQL type 'timestamp without time zone'` | Non dovrebbe capitare: il mapping è `timestamp without time zone` e i valori sono `Unspecified`. Se accade, verifica di non aver forzato `Kind=Utc` a monte. |
| DataMigration: "il target non è vuoto" | Usa `--truncate` (svuota) oppure parti da un DB appena creato. |
| Migrazione OHLCV lentissima | Normale (milioni di righe via INSERT). Usa `--skip-ohlcv` e ri-ingesta con `tools/PlatformExpand`. |

---

## 10. Riepilogo file coinvolti

- `Program.cs` — selezione provider (SQLite/PostgreSQL) da `Database:Provider`.
- `appsettings.json` / `appsettings.Production.json` — config provider + connection string.
- `Data/ApplicationDbContext.cs` — config `DateTime`→`timestamp without time zone` per Npgsql.
- `ProcioneMGR.Migrations.Postgres/` — migrazioni PostgreSQL + design-time factory.
- `tools/DbBackup/` — backup sicuro SQLite (checkpoint WAL + integrity_check).
- `Services/Admin/DatabaseBackupHelper.cs` + `DatabaseBackupService.cs` — logica backup condivisa.
- `Components/Pages/Admin/Backup.razor` — UI backup (`/admin/backup`).
- `tools/DataMigration/` — copia dati SQLite → PostgreSQL.
- `ProcioneMGR.Tests/ProviderCompatibilityTests.cs` — round-trip blob/decimal/JSON sui due provider.
