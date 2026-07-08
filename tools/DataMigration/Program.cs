using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;

// =============================================================================================
// DataMigration — copia i dati da SQLite (app.db) a PostgreSQL, tabella per tabella.
//
// PRINCIPIO: copia ADO-to-ADO "raw", NON tramite l'ORM. I metadati EF (nomi tabelle/colonne, tipi
// CLR, grafo delle foreign key) servono solo a sapere COSA e in CHE ORDINE copiare. I VALORI sono
// letti grezzi da SQLite e scritti grezzi su PostgreSQL. Conseguenze volute:
//   • le colonne cifrate (credenziali) sono trasferite come ciphertext byte-per-byte: nessuna master
//     key richiesta, nessuna decifratura/ricifratura (il runtime le decifrerà con la stessa chiave);
//   • i blob binari (SavedMlModel.ModelBytes) sono trasferiti byte-per-byte;
//   • gli ID interi sono PRESERVATI (inseriti esplicitamente), così le foreign key restano valide;
//     al termine i sequence identity vengono riallineati con setval.
//
// CONVERSIONI DI TIPO gestite (SQLite memorizza in modo "lasco"):
//   • decimal  : SQLite TEXT  -> numeric   (parse invariant-culture: NIENTE perdita di precisione)
//   • DateTime : SQLite TEXT  -> timestamp (Kind=Unspecified, coerente con timestamp without tz)
//   • bool     : SQLite 0/1   -> boolean
//   • enum     : SQLite TEXT  -> text       (il DbContext serializza gli enum come stringa)
//   • byte[]   : SQLite BLOB  -> bytea       (verbatim)
//
// USO:
//   DataMigration --sqlite <path app.db> --pg "<connstring>" [--truncate] [--skip-ohlcv] [--batch N]
//
// Se PostgreSQL non è ancora popolato con lo schema, applicare prima le migrazioni:
//   dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR.Migrations.Postgres
//
// NOTA: strumento verificato in compilazione; la sua esecuzione end-to-end va validata contro
// l'istanza PostgreSQL reale (vedi docs/POSTGRES_MIGRATION.md).
// =============================================================================================

var opts = ParseArgs(args);
if (opts is null) return 1;

Console.WriteLine($"[DataMigration] Sorgente SQLite : {opts.SqlitePath}");
Console.WriteLine($"[DataMigration] Target Postgres: {Redact(opts.PgConnString)}");
Console.WriteLine($"[DataMigration] Opzioni        : truncate={opts.Truncate}, skipOhlcv={opts.SkipOhlcv}, batch={opts.BatchSize}");

if (!File.Exists(opts.SqlitePath))
{
    Console.Error.WriteLine($"ERRORE: file SQLite non trovato: {opts.SqlitePath}");
    return 1;
}

// --- Metadati del modello EF (solo per struttura, non per i dati) ---
var services = new ServiceCollection();
services.AddSingleton<IEncryptionService, PassthroughEncryption>();
services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite($"Data Source={opts.SqlitePath}"));
await using var sp = services.BuildServiceProvider();
using var metaCtx = sp.GetRequiredService<ApplicationDbContext>();
var model = metaCtx.Model;

var tables = BuildTablePlan(model);
if (opts.OnlyOhlcv)
{
    // Migra SOLO OhlcvData (le altre tabelle sono già state migrate in una passata precedente).
    tables = tables.Where(t => t.Table.Equals("OhlcvData", StringComparison.OrdinalIgnoreCase)).ToList();
}
Console.WriteLine($"[DataMigration] Tabelle da migrare (ordine dipendenze): {tables.Count}");

// --- Connessioni ---
await using var pg = new NpgsqlConnection(opts.PgConnString);
try
{
    await pg.OpenAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERRORE: impossibile connettersi a PostgreSQL: {ex.Message}");
    Console.Error.WriteLine("Verifica che il DB esista e che lo schema sia stato applicato (dotnet ef database update).");
    return 1;
}

using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = opts.SqlitePath,
    Mode = SqliteOpenMode.ReadOnly,
    Pooling = false,
}.ToString());
sqlite.Open();

// --- Guardia: il target deve essere vuoto, oppure --truncate ---
if (opts.Truncate)
{
    var truncList = string.Join(", ", tables.AsEnumerable().Reverse().Select(t => PgQuoteIdent(t.Table)));
    Console.WriteLine("[DataMigration] TRUNCATE di tutte le tabelle (RESTART IDENTITY CASCADE)…");
    await using var trunc = pg.CreateCommand();
    trunc.CommandText = $"TRUNCATE {truncList} RESTART IDENTITY CASCADE;";
    await trunc.ExecuteNonQueryAsync();
}
else
{
    var nonEmpty = new List<string>();
    foreach (var t in tables)
    {
        await using var cnt = pg.CreateCommand();
        cnt.CommandText = $"SELECT COUNT(*) FROM {PgQuoteIdent(t.Table)};";
        var n = Convert.ToInt64(await cnt.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        if (n > 0) nonEmpty.Add($"{t.Table} ({n})");
    }
    if (nonEmpty.Count > 0)
    {
        Console.Error.WriteLine("ERRORE: il target PostgreSQL non è vuoto. Tabelle con dati: " + string.Join(", ", nonEmpty));
        Console.Error.WriteLine("Rilancia con --truncate per svuotarle prima della migrazione (ATTENZIONE: cancella i dati esistenti).");
        return 1;
    }
}

// --- Copia tabella per tabella ---
var totals = new Dictionary<string, long>();
foreach (var t in tables)
{
    if (opts.SkipOhlcv && t.Table.Equals("OhlcvData", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[DataMigration] SALTO {t.Table} (--skip-ohlcv): dati di mercato ri-scaricabili via tools/PlatformExpand.");
        continue;
    }

    // OhlcvData ha milioni di righe: usa il COPY binario di Npgsql (ordini di grandezza più veloce
    // dell'INSERT riga-per-riga). Le tabelle piccole restano su INSERT parametrizzato (percorso già
    // provato). Entrambi preservano gli ID espliciti.
    var copied = t.Table.Equals("OhlcvData", StringComparison.OrdinalIgnoreCase)
        ? await CopyTableViaBinaryCopyAsync(sqlite, pg, t)
        : await CopyTableAsync(sqlite, pg, t, opts.BatchSize);
    totals[t.Table] = copied;
    Console.WriteLine($"[DataMigration]   {t.Table}: {copied} righe");
}

// --- Riallinea le sequence identity (dopo insert con ID espliciti) ---
Console.WriteLine("[DataMigration] Riallineamento sequence identity…");
foreach (var t in tables)
{
    if (t.IdentityColumn is null) continue;
    if (opts.SkipOhlcv && t.Table.Equals("OhlcvData", StringComparison.OrdinalIgnoreCase)) continue;
    await using var sv = pg.CreateCommand();
    // pg_get_serial_sequence: 1° arg regclass (quotato per preservare il case Pascal), 2° arg attname.
    sv.CommandText =
        $"SELECT setval(pg_get_serial_sequence('{'"'}{t.Table}{'"'}', '{t.IdentityColumn}'), " +
        $"GREATEST((SELECT COALESCE(MAX({PgQuoteIdent(t.IdentityColumn)}), 0) FROM {PgQuoteIdent(t.Table)}), 1));";
    try { await sv.ExecuteScalarAsync(); }
    catch (Exception ex) { Console.WriteLine($"   (setval {t.Table} saltato: {ex.Message})"); }
}

Console.WriteLine();
Console.WriteLine("[DataMigration] COMPLETATO. Riepilogo:");
foreach (var kv in totals.OrderByDescending(k => k.Value))
    Console.WriteLine($"   {kv.Key,-32} {kv.Value,12:N0}");
Console.WriteLine($"   {"TOTALE",-32} {totals.Values.Sum(),12:N0}");
return 0;

// =============================================================================================
// Helpers
// =============================================================================================

static async Task<long> CopyTableAsync(SqliteConnection sqlite, NpgsqlConnection pg, TablePlan t, int batchSize)
{
    var colList = string.Join(", ", t.Columns.Select(c => PgQuoteIdent(c.Column)));
    var paramList = string.Join(", ", t.Columns.Select((_, i) => $"@p{i}"));
    var insertSql = $"INSERT INTO {PgQuoteIdent(t.Table)} ({colList}) VALUES ({paramList});";

    await using var read = sqlite.CreateCommand();
    read.CommandText =
        $"SELECT {string.Join(", ", t.Columns.Select(c => SqliteQuoteIdent(c.Column)))} FROM {SqliteQuoteIdent(t.Table)};";
    await using var rdr = await read.ExecuteReaderAsync();

    long copied = 0;
    NpgsqlTransaction? tx = await pg.BeginTransactionAsync();
    await using var ins = pg.CreateCommand();
    ins.Transaction = tx;
    ins.CommandText = insertSql;
    // Pre-crea i parametri (riusati riga per riga).
    var pars = new NpgsqlParameter[t.Columns.Count];
    for (var i = 0; i < t.Columns.Count; i++)
    {
        pars[i] = new NpgsqlParameter($"p{i}", NpgsqlTypeFor(t.Columns[i].ClrType));
        ins.Parameters.Add(pars[i]);
    }

    try
    {
        while (await rdr.ReadAsync())
        {
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var raw = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                pars[i].Value = Coerce(raw, t.Columns[i].ClrType) ?? DBNull.Value;
            }
            await ins.ExecuteNonQueryAsync();
            copied++;

            if (copied % batchSize == 0)
            {
                await tx.CommitAsync();
                await tx.DisposeAsync();
                tx = await pg.BeginTransactionAsync();
                ins.Transaction = tx;
                Console.Write($"\r[DataMigration]   {t.Table}: {copied:N0}…   ");
            }
        }
        await tx.CommitAsync();
    }
    finally
    {
        await tx.DisposeAsync();
    }
    if (copied >= batchSize) Console.WriteLine();
    return copied;
}

// Copia via COPY binario di Npgsql: streaming ad alte prestazioni per tabelle con milioni di righe
// (es. OhlcvData). Include tutte le colonne, ID compreso (preservazione ID). I vincoli (FK, unique)
// restano validati alla chiusura del COPY.
static async Task<long> CopyTableViaBinaryCopyAsync(SqliteConnection sqlite, NpgsqlConnection pg, TablePlan t)
{
    var colList = string.Join(", ", t.Columns.Select(c => PgQuoteIdent(c.Column)));
    var types = t.Columns.Select(c => NpgsqlTypeFor(c.ClrType)).ToArray();

    await using var read = sqlite.CreateCommand();
    read.CommandText =
        $"SELECT {string.Join(", ", t.Columns.Select(c => SqliteQuoteIdent(c.Column)))} FROM {SqliteQuoteIdent(t.Table)};";
    await using var rdr = await read.ExecuteReaderAsync();

    long copied = 0;
    await using (var writer = await pg.BeginBinaryImportAsync(
        $"COPY {PgQuoteIdent(t.Table)} ({colList}) FROM STDIN (FORMAT BINARY)"))
    {
        while (await rdr.ReadAsync())
        {
            await writer.StartRowAsync();
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var raw = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                var val = Coerce(raw, t.Columns[i].ClrType);
                if (val is null) await writer.WriteNullAsync();
                else await writer.WriteAsync(val, types[i]);
            }
            copied++;
            if (copied % 100000 == 0) Console.Write($"\r[DataMigration]   {t.Table}: {copied:N0}…   ");
        }
        await writer.CompleteAsync();
    }
    if (copied >= 100000) Console.WriteLine();
    return copied;
}

// Converte un valore letto grezzo da SQLite nel tipo atteso da PostgreSQL.
static object? Coerce(object? raw, Type clrType)
{
    if (raw is null || raw is DBNull) return null;
    var t = Nullable.GetUnderlyingType(clrType) ?? clrType;

    if (t.IsEnum) return raw is string es ? es : raw.ToString();            // enum-as-string
    if (t == typeof(decimal))
        return raw is string ds ? decimal.Parse(ds, NumberStyles.Any, CultureInfo.InvariantCulture)
                                : Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
    if (t == typeof(DateTime))
    {
        var dt = raw is string dts ? DateTime.Parse(dts, CultureInfo.InvariantCulture, DateTimeStyles.None)
                                   : Convert.ToDateTime(raw, CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);          // timestamp without tz
    }
    if (t == typeof(DateTimeOffset))
        return raw is string dos ? DateTimeOffset.Parse(dos, CultureInfo.InvariantCulture) : raw;
    if (t == typeof(TimeSpan))
        return raw is string tss ? TimeSpan.Parse(tss, CultureInfo.InvariantCulture) : raw;
    if (t == typeof(bool)) return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
    if (t == typeof(Guid)) return raw is string gs ? Guid.Parse(gs) : raw;
    if (t == typeof(int)) return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
    if (t == typeof(long)) return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
    if (t == typeof(double)) return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
    if (t == typeof(float)) return Convert.ToSingle(raw, CultureInfo.InvariantCulture);
    if (t == typeof(byte[])) return raw;
    if (t == typeof(string)) return raw is string s ? s : raw.ToString();
    return raw;
}

static NpgsqlDbType NpgsqlTypeFor(Type clrType)
{
    var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (t.IsEnum) return NpgsqlDbType.Text;
    if (t == typeof(decimal)) return NpgsqlDbType.Numeric;
    if (t == typeof(DateTime)) return NpgsqlDbType.Timestamp;
    if (t == typeof(DateTimeOffset)) return NpgsqlDbType.TimestampTz;
    if (t == typeof(TimeSpan)) return NpgsqlDbType.Interval;
    if (t == typeof(bool)) return NpgsqlDbType.Boolean;
    if (t == typeof(Guid)) return NpgsqlDbType.Uuid;
    if (t == typeof(int)) return NpgsqlDbType.Integer;
    if (t == typeof(long)) return NpgsqlDbType.Bigint;
    if (t == typeof(double)) return NpgsqlDbType.Double;
    if (t == typeof(float)) return NpgsqlDbType.Real;
    if (t == typeof(byte[])) return NpgsqlDbType.Bytea;
    return NpgsqlDbType.Text;
}

// Costruisce l'elenco ordinato delle tabelle (topologico per foreign key) con colonne, tipi e PK identity.
static List<TablePlan> BuildTablePlan(IModel model)
{
    // Raggruppa gli entity type per tabella (gestisce eventuali TPH/owned sulla stessa tabella).
    var byTable = new Dictionary<string, TablePlan>(StringComparer.Ordinal);
    var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

    foreach (var et in model.GetEntityTypes())
    {
        var table = et.GetTableName();
        if (table is null) continue;
        var sid = StoreObjectIdentifier.Create(et, StoreObjectType.Table);
        if (sid is null) continue;

        if (!byTable.TryGetValue(table, out var plan))
        {
            plan = new TablePlan(table);
            byTable[table] = plan;
            deps[table] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var p in et.GetProperties())
        {
            var col = p.GetColumnName(sid.Value);
            if (col is null) continue;
            if (plan.Columns.All(c => c.Column != col))
                plan.Columns.Add(new ColumnPlan(col, p.ClrType));
        }

        // PK identity a colonna singola (int/long, value-generated on add) -> per setval + insert esplicito.
        var pk = et.FindPrimaryKey();
        if (pk is { Properties.Count: 1 })
        {
            var kp = pk.Properties[0];
            if ((kp.ClrType == typeof(int) || kp.ClrType == typeof(long))
                && kp.ValueGenerated == ValueGenerated.OnAdd)
            {
                plan.IdentityColumn = kp.GetColumnName(sid.Value);
            }
        }

        foreach (var fk in et.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType.GetTableName();
            if (principal is not null && principal != table)
                deps[table].Add(principal);
        }
    }

    // Kahn's topological sort: le tabelle principali (dipendenze) prima delle dipendenti.
    var ordered = new List<TablePlan>();
    var remaining = new HashSet<string>(byTable.Keys, StringComparer.Ordinal);
    while (remaining.Count > 0)
    {
        var ready = remaining.Where(t => deps[t].All(d => !remaining.Contains(d))).OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (ready.Count == 0)
        {
            // Ciclo (es. self-reference o FK circolari): aggiungi il resto in ordine alfabetico.
            ready = remaining.OrderBy(t => t, StringComparer.Ordinal).ToList();
        }
        foreach (var t in ready)
        {
            ordered.Add(byTable[t]);
            remaining.Remove(t);
        }
    }
    return ordered;
}

static string PgQuoteIdent(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
static string SqliteQuoteIdent(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

static string Redact(string conn)
{
    // Nasconde la password nella stampa.
    return string.Join(";", conn.Split(';').Select(p =>
        p.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase) ? "Password=***" : p));
}

static Options? ParseArgs(string[] args)
{
    string? sqlite = null, pg = null;
    var truncate = false;
    var skipOhlcv = false;
    var onlyOhlcv = false;
    var batch = 5000;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--sqlite": sqlite = args[++i]; break;
            case "--pg": pg = args[++i]; break;
            case "--truncate": truncate = true; break;
            case "--skip-ohlcv": skipOhlcv = true; break;
            case "--only-ohlcv": onlyOhlcv = true; break;
            case "--batch": batch = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "-h" or "--help":
                PrintUsage();
                return null;
        }
    }

    // Default ragionevoli: app.db del progetto e connection string da env PostgresConnection.
    sqlite ??= Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ProcioneMGR", "Data", "app.db");
    sqlite = Path.GetFullPath(sqlite);
    pg ??= Environment.GetEnvironmentVariable("PostgresConnection")
           ?? "Host=localhost;Port=5432;Database=procionemgr;Username=procione;Password=Procione2026Pg_secure";

    if (batch < 1) batch = 5000;
    return new Options(sqlite, pg, truncate, skipOhlcv, onlyOhlcv, batch);
}

static void PrintUsage()
{
    Console.WriteLine("Uso: DataMigration --sqlite <app.db> --pg \"<connstring>\" [--truncate] [--skip-ohlcv] [--batch N]");
    Console.WriteLine("  --sqlite      Path del file app.db sorgente (default: ProcioneMGR/Data/app.db).");
    Console.WriteLine("  --pg          Connection string PostgreSQL (default: env PostgresConnection).");
    Console.WriteLine("  --truncate    Svuota le tabelle target prima di migrare (RESTART IDENTITY CASCADE).");
    Console.WriteLine("  --skip-ohlcv  Salta OhlcvData (dati di mercato ri-scaricabili, tabella più grande).");
    Console.WriteLine("  --only-ohlcv  Migra SOLO OhlcvData via COPY binario (per completare dopo un --skip-ohlcv).");
    Console.WriteLine("  --batch N     Righe per transazione per le tabelle piccole (default 5000).");
}

sealed record Options(string SqlitePath, string PgConnString, bool Truncate, bool SkipOhlcv, bool OnlyOhlcv, int BatchSize);

sealed class TablePlan(string table)
{
    public string Table { get; } = table;
    public List<ColumnPlan> Columns { get; } = [];
    public string? IdentityColumn { get; set; }
}

sealed record ColumnPlan(string Column, Type ClrType);

sealed class PassthroughEncryption : IEncryptionService
{
    public string Encrypt(string plaintext) => plaintext;
    public string Decrypt(string ciphertext) => ciphertext;
}
