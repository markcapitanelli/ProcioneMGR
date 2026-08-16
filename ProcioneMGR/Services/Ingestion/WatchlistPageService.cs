using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// Orchestrazione della pagina /market/watchlist — stessa divisione di responsabilità di
/// TradingPageService: qui vivono query, stato derivato e azioni; il componente .razor tiene solo
/// rendering, ciclo di vita e PollingTimer. Scoped: uno scope Blazor Server = un circuito = una
/// sessione utente, quindi lo stato (righe, timbro del sync, memoria per il rilevamento del
/// recupero) è per-utente e muore col circuito.
///
/// <para>Nato dalla revisione post-incidente 2026-08-15 (122 serie ferme per 6 ore in silenzio,
/// worker di sync morto alle 22:44 del giorno prima). I tre difetti della pagina precedente:
/// (1) nessuna risposta alla domanda vera «il sync sta girando ADESSO?»; (2) fotografia statica
/// che restava rossa anche a guasto rientrato; (3) una GROUP BY sull'intera OhlcvData (~12,6M
/// righe, 15 s misurati) pagata a OGNI caricamento. Ora: timbro del ciclo in testa, freschezza
/// per-serie via MAX sull'indice (pochi ms l'una), conteggi fuori dal percorso critico.</para>
/// </summary>
public sealed class WatchlistPageService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IMarketDataSyncService syncService,
    IExchangeClientFactory exchangeFactory,
    ProcioneMGR.Services.MarketData.ISymbolCatalog symbolCatalog,
    IConfiguration configuration,
    ILogger<WatchlistPageService> logger)
{
    /// <summary>Una riga della tabella. Mutabile: conteggi e stato exchange arrivano DOPO il primo paint.</summary>
    public sealed class Row
    {
        public required TrackedSeries Series { get; set; }

        /// <summary>Candele a DB; null = non ancora contate (i conteggi sono fuori dal percorso critico).</summary>
        public int? CandleCount { get; set; }

        public DateTime? LastCandleUtc { get; set; }

        /// <summary>
        /// Ferma ma in RECUPERO: l'ultima candela è avanzata dall'osservazione precedente e il sync
        /// gira. Dopo un blocco l'arretrato si drena in qualche minuto: dirlo evita di leggere il
        /// drenaggio come guasto in corso (e di disabilitare serie sane, l'errore dell'incidente STX).
        /// </summary>
        public bool IsRecovering { get; set; }

        /// <summary>Stato grezzo del simbolo su exchange (es. "TRADING", "BREAK"); null = non verificato.</summary>
        public string? ExchangeStatus { get; set; }
    }

    public sealed record Lane(int LaneId, string Symbol, string Timeframe, string Mode);

    /// <summary>Il timbro del ciclo di sync, come lo mostra la pagina.</summary>
    public sealed record Pulse(
        DateTime? LastCycleUtc, string? Outcome, bool Stalled, bool Estimated, bool Disabled, TimeSpan Interval);

    /// <summary>Esito della verifica su exchange: senza i fallimenti, «0 sospese» rassicura anche quando non è stato verificato NULLA.</summary>
    public sealed record StatusCheck(int Suspended, int Checked, IReadOnlyList<string> FailedExchanges)
    {
        public bool AllFailed => Checked == 0 && FailedExchanges.Count > 0;
    }

    public IReadOnlyList<Row>? Rows { get; private set; }
    public IReadOnlyList<Lane> RunningLanes { get; private set; } = [];
    public Pulse? SyncPulse { get; private set; }

    /// <summary>Quando è stata scattata/aggiornata la fotografia mostrata (UTC).</summary>
    public DateTime? LoadedAtUtc { get; private set; }

    /// <summary>Quando sono stati verificati gli stati su exchange (UTC); null = mai in questa sessione.</summary>
    public DateTime? StatusesCheckedAtUtc { get; private set; }

    /// <summary>Memoria dell'ultima candela vista per serie: serve al rilevamento del recupero.</summary>
    private readonly Dictionary<int, DateTime?> _previousLastCandle = [];

    /// <summary>Osservazioni consecutive senza progresso, per serie: oltre la soglia il recupero decade.</summary>
    private readonly Dictionary<int, int> _observationsWithoutProgress = [];

    /// <summary>
    /// Quante osservazioni senza progresso il badge «in recupero» sopravvive. Due, non zero: un
    /// drenaggio reale non porta una barra nuova a ogni tick da 60 s (su una serie 1h può passare
    /// qualche minuto fra due barre recuperate) e il badge lampeggerebbe fra «in recupero» e
    /// «FERMA» senza che nulla sia cambiato (review 2026-08-15). Non infinite: un drenaggio che si
    /// pianta davvero deve tornare a dichiararsi FERMA.
    /// </summary>
    private const int RecoveryGraceObservations = 2;

    /// <summary>
    /// Serializza le letture che riscrivono <see cref="Rows"/>. Senza, il tick del polling partito
    /// PRIMA di un'azione dell'utente può riscrivere in coda i valori pre-azione: la riga appena
    /// sincronizzata tornerebbe «FERMA» per un minuto subito dopo il messaggio di successo
    /// (lost update, review 2026-08-15). Uno scope Blazor = un circuito: la contesa è fra il timer
    /// e i click dello stesso utente, quindi il gate è corto e non contende fra sessioni.
    /// </summary>
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    /// <summary>Progressivo delle letture: un refresh più vecchio non sovrascrive uno più recente.</summary>
    private long _readSequence;

    private TimeSpan ConfiguredInterval =>
        TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("MarketData:SyncIntervalMinutes", 5)));

    /// <summary>
    /// Caricamento pieno: serie, freschezza, corsie e timbro del sync. NIENTE conteggi qui — la
    /// GROUP BY su tutta OhlcvData costava 15 s misurati a ogni apertura di pagina; i conteggi
    /// arrivano da <see cref="LoadCountsAsync"/> dopo il primo paint, e i valori già noti si
    /// conservano per non far lampeggiare la colonna.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _readSequence);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var series = await db.TrackedSeries
            .OrderBy(s => s.Exchange).ThenBy(s => s.Symbol).ThenBy(s => s.Timeframe)
            .ToListAsync(ct);
        var lastBySeries = await ReadLastCandlesAsync(db, series, ct);
        var lanes = await ReadLanesAsync(db, ct);
        var pulse = await ReadPulseAsync(db, ct);

        await _stateGate.WaitAsync(ct);
        try
        {
            if (seq < Volatile.Read(ref _readSequence)) return; // una lettura più recente ha già scritto

            var previousCounts = Rows?.ToDictionary(r => r.Series.Id, r => r.CandleCount);
            var previousStatuses = Rows?.ToDictionary(r => r.Series.Id, r => r.ExchangeStatus);

            Rows = series.Select(s => new Row
            {
                Series = s,
                LastCandleUtc = lastBySeries.GetValueOrDefault((s.Symbol, s.Timeframe)),
                CandleCount = previousCounts?.GetValueOrDefault(s.Id),
                ExchangeStatus = previousStatuses?.GetValueOrDefault(s.Id),
            }).ToList();

            RunningLanes = lanes;
            SyncPulse = pulse;
            UpdateRecoveryFlags();
            LoadedAtUtc = DateTime.UtcNow;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Refresh per il polling: freschezza (MAX per serie sull'indice), stato di sync PER SERIE
    /// (<c>LastSyncUtc</c>/<c>LastSyncStatus</c>/<c>Enabled</c>, che il worker riscrive ogni ciclo
    /// — senza rileggerli le colonne restavano congelate all'apertura della pagina), corsie e
    /// timbro. Mai i conteggi: il tick periodico deve costare millisecondi, non secondi.
    /// </summary>
    public async Task RefreshFreshnessAsync(CancellationToken ct = default)
    {
        if (Rows is null)
        {
            await LoadAsync(ct);
            return;
        }

        var seq = Interlocked.Increment(ref _readSequence);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var series = await db.TrackedSeries.AsNoTracking().ToListAsync(ct);
        var lastBySeries = await ReadLastCandlesAsync(db, series, ct);
        var lanes = await ReadLanesAsync(db, ct);
        var pulse = await ReadPulseAsync(db, ct);

        await _stateGate.WaitAsync(ct);
        try
        {
            if (seq < Volatile.Read(ref _readSequence) || Rows is null) return;

            var byId = series.ToDictionary(s => s.Id);
            // Se nel frattempo sono state aggiunte/rimosse serie, l'insieme è cambiato: rifare la
            // lista qui (con i conteggi noti conservati) invece di mostrarne una stantia.
            if (Rows.Count != series.Count || Rows.Any(r => !byId.ContainsKey(r.Series.Id)))
            {
                var counts = Rows.ToDictionary(r => r.Series.Id, r => r.CandleCount);
                var statuses = Rows.ToDictionary(r => r.Series.Id, r => r.ExchangeStatus);
                Rows = series
                    .OrderBy(s => s.Exchange).ThenBy(s => s.Symbol).ThenBy(s => s.Timeframe)
                    .Select(s => new Row
                    {
                        Series = s,
                        LastCandleUtc = lastBySeries.GetValueOrDefault((s.Symbol, s.Timeframe)),
                        CandleCount = counts.GetValueOrDefault(s.Id),
                        ExchangeStatus = statuses.GetValueOrDefault(s.Id),
                    }).ToList();
            }
            else
            {
                foreach (var row in Rows)
                {
                    row.Series = byId[row.Series.Id]; // stato di sync fresco, non quello dell'apertura
                    row.LastCandleUtc = lastBySeries.GetValueOrDefault((row.Series.Symbol, row.Series.Timeframe));
                }
            }

            RunningLanes = lanes;
            SyncPulse = pulse;
            UpdateRecoveryFlags();
            LoadedAtUtc = DateTime.UtcNow;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Conteggi per serie, FUORI dal percorso critico. Un Count per serie sull'indice
    /// (Symbol, Timeframe, TimestampUtc) al posto della GROUP BY sull'intera tabella: la storia di
    /// questa query è istruttiva — prima N+1 CountAsync (collo di bottiglia), poi GROUP BY unica
    /// (fix del collo… diventato seq scan da 15 s a 12,6M righe), ora di nuovo per-serie ma
    /// sull'indice e in background, dove il costo non blocca nessuno. Il token è OBBLIGATORIO nei
    /// fatti: senza, una passata continuava a scandire l'indice dopo che l'utente aveva navigato via.
    /// </summary>
    public async Task LoadCountsAsync(CancellationToken ct = default)
    {
        var snapshot = Rows;
        if (snapshot is null) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var countByKey = new Dictionary<(string, string), int>();
        foreach (var row in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var key = (row.Series.Symbol, row.Series.Timeframe);
            if (!countByKey.ContainsKey(key))
            {
                countByKey[key] = await db.OhlcvData
                    .Where(c => c.Symbol == row.Series.Symbol && c.Timeframe == row.Series.Timeframe)
                    .CountAsync(ct);
            }
        }

        await _stateGate.WaitAsync(ct);
        try
        {
            foreach (var row in Rows ?? [])
            {
                if (countByKey.TryGetValue((row.Series.Symbol, row.Series.Timeframe), out var n))
                {
                    row.CandleCount = n;
                }
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>
    /// Verifica su exchange lo stato dei simboli: UNA chiamata pubblica per exchange copre
    /// l'intero listino. L'esito dichiara anche COSA NON è stato verificato: «0 sospese» quando
    /// tutte le chiamate sono fallite rassicurava a vuoto — la classe di difetto «controlli che
    /// rassicurano a prescindere dalla realtà» (Filone E). Gli exchange falliti perdono i badge
    /// vecchi: un BREAK di ieri non deve comparire sotto il timestamp della verifica di oggi.
    /// </summary>
    public async Task<StatusCheck> CheckExchangeStatusesAsync(CancellationToken ct = default)
    {
        var snapshot = Rows;
        if (snapshot is null)
        {
            return new StatusCheck(0, 0, []);
        }

        var fresh = new Dictionary<int, string>();
        var failed = new List<string>();
        var okExchanges = 0;

        foreach (var exchange in snapshot.Select(r => r.Series.Exchange).Distinct())
        {
            try
            {
                var client = exchangeFactory.Create(exchange);
                var statuses = await client.GetSymbolStatusesAsync(ct);
                if (statuses.Count == 0)
                {
                    // Client senza capacità di status (o listino vuoto): meglio nessun verdetto che
                    // un verdetto inventato — e va detto, non taciuto.
                    failed.Add($"{exchange} (nessuno stato disponibile)");
                    continue;
                }

                okExchanges++;
                foreach (var row in snapshot.Where(r => r.Series.Exchange == exchange))
                {
                    fresh[row.Series.Id] = statuses.TryGetValue(row.Series.Symbol, out var st) ? st : "non quotato";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Il filtro è sul TOKEN, non sul tipo: un timeout HTTP è una TaskCanceledException
                // con Token=None (la terza sorgente di OCE di questo stesso PR) ed è il modo di
                // guasto più comune di una chiamata pubblica da qualche MB. Isolare l'exchange
                // guasto è proprio ciò per cui questo catch esiste.
                logger.LogWarning(ex, "Verifica stato simboli su {Exchange} fallita.", exchange);
                failed.Add(exchange.ToString());
            }
        }

        var now = DateTime.UtcNow;
        await _stateGate.WaitAsync(ct);
        try
        {
            foreach (var row in Rows ?? [])
            {
                // Verificato ora → valore nuovo; exchange fallito → nessun badge (mai un badge
                // vecchio sotto un timestamp nuovo: Regola 5).
                row.ExchangeStatus = fresh.TryGetValue(row.Series.Id, out var st) ? st : null;
            }

            StatusesCheckedAtUtc = okExchanges > 0 ? now : null;

            var suspended = (Rows ?? []).Count(r => r.Series.Enabled
                && SeriesFreshness.IsStale(r.Series.Timeframe, r.LastCandleUtc, now)
                && r.ExchangeStatus is not null
                && !IsTradable(r.Series.Exchange, r.ExchangeStatus));
            return new StatusCheck(suspended, okExchanges, failed);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Negoziabile secondo il vocabolario dell'exchange (Binance "TRADING", Bitget "online").</summary>
    public static bool IsTradable(ExchangeName exchange, string? rawStatus) => exchange switch
    {
        ExchangeName.Binance => string.Equals(rawStatus, "TRADING", StringComparison.OrdinalIgnoreCase),
        ExchangeName.Bitget => string.Equals(rawStatus, "online", StringComparison.OrdinalIgnoreCase),
        _ => true, // exchange senza vocabolario noto: non inventare sospensioni
    };

    // ------------------------------------------------------------------ azioni

    public async Task<(string Message, bool IsError)> AddAsync(
        ExchangeName exchange, string? symbolRaw, string timeframe, CancellationToken ct = default)
    {
        var symbol = symbolRaw?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return ("Inserisci un symbol (es. BTC/USDT).", true);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exists = await db.TrackedSeries.AnyAsync(s =>
            s.Exchange == exchange && s.Symbol == symbol && s.Timeframe == timeframe, ct);
        if (exists)
        {
            return ("Questa serie è già tracciata.", true);
        }

        db.TrackedSeries.Add(new TrackedSeries
        {
            Exchange = exchange,
            Symbol = symbol,
            Timeframe = timeframe,
            Enabled = true,
        });
        await db.SaveChangesAsync(ct);
        symbolCatalog.Invalidate(); // [E-04] i menu delle altre pagine vedono subito la serie nuova/rimossa
        return ($"Aggiunta {exchange} {symbol} {timeframe}.", false);
    }

    public async Task<(string Message, bool IsError)> SyncNowAsync(int id, CancellationToken ct = default)
    {
        try
        {
            var n = await syncService.SyncSeriesAsync(id, ct);
            return ($"Sync completata: {n} candele processate.", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Filtro sul TOKEN, non sul tipo: in assetto remoto il timeout dell'HttpClient verso il
            // pod ingestion arriva come TaskCanceledException con Token=None e, sfuggendo a un
            // filtro per tipo, risalirebbe fino all'event handler Blazor uccidendo il circuito —
            // proprio mentre l'utente prova a rimediare a un sync lento (review 2026-08-15).
            return ($"Errore sync: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Flip di Enabled. Due avvertenze integrate: (a) disabilitare una serie operata da una corsia
    /// NON ferma il trading (incidente STX 2026-08-13) — si dice nel momento in cui l'equivoco
    /// nasce; (b) se la serie risulta sospesa su exchange, l'annotazione «disabilitata … riporta
    /// stato X» si scrive da sola in LastSyncStatus, come già si faceva a mano per MKR/TON.
    /// </summary>
    public async Task<(string Message, bool IsError)> ToggleAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.TrackedSeries.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
        {
            return ("Serie non trovata.", true);
        }

        s.Enabled = !s.Enabled;

        string message;
        var isError = false;
        if (!s.Enabled)
        {
            var status = Rows?.FirstOrDefault(r => r.Series.Id == id)?.ExchangeStatus;
            if (status is not null && !IsTradable(s.Exchange, status))
            {
                s.LastSyncStatus = $"disabilitata {DateTime.UtcNow:yyyy-MM-dd} — {s.Exchange} riporta stato {status}";
            }

            var lanes = await db.TradingEngineStates.AsNoTracking()
                .Where(t => t.IsRunning && t.Symbol == s.Symbol && t.Timeframe == s.Timeframe)
                .Select(t => new { t.LaneId, t.Mode })
                .ToListAsync(ct);
            if (lanes.Count > 0)
            {
                message = $"Serie disabilitata, ma {string.Join(" e ", lanes.Select(l => $"la corsia {l.LaneId} ({l.Mode})"))} "
                        + "la sta operando: qui si ferma solo l'aggiornamento delle candele, NON il trading. "
                        + "La corsia continuerà a decidere su dati che non si aggiornano più — fermala in /trading "
                        + "oppure riabilita la serie.";
                isError = true;
            }
            else
            {
                message = $"Serie {s.Symbol} {s.Timeframe} disabilitata.";
            }
        }
        else
        {
            message = $"Serie {s.Symbol} {s.Timeframe} riabilitata.";
        }

        await db.SaveChangesAsync(ct);
        symbolCatalog.Invalidate(); // [E-04]
        return (message, isError);
    }

    public async Task<(string Message, bool IsError)> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.TrackedSeries.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        symbolCatalog.Invalidate(); // [E-04]
        return ("Serie rimossa dalla watchlist (i dati OHLCV restano nel DB).", false);
    }

    // ------------------------------------------------------------------ derivati per il markup

    /// <summary>[E7] Serie ABILITATE la cui ultima candela è più indietro della tolleranza.</summary>
    public int StaleEnabledCount()
    {
        if (Rows is null) return 0;
        var now = DateTime.UtcNow;
        return Rows.Count(r => r.Series.Enabled
            && SeriesFreshness.IsStale(r.Series.Timeframe, r.LastCandleUtc, now));
    }

    /// <summary>
    /// [2026-08-13] Corsie che operano una serie DISABILITATA in watchlist: la combinazione
    /// peggiore (corsia viva su dati che nessuno aggiorna), resa visibile in cima alla pagina.
    /// </summary>
    public List<(Lane Lane, TrackedSeries Series)> LaneOnDisabledSeries()
    {
        if (Rows is null) return [];
        return RunningLanes
            .Select(l => (Lane: l, Series: Rows.FirstOrDefault(r =>
                !r.Series.Enabled
                && string.Equals(r.Series.Symbol, l.Symbol, StringComparison.OrdinalIgnoreCase)
                && r.Series.Timeframe == l.Timeframe)?.Series))
            .Where(x => x.Series is not null)
            .Select(x => (x.Lane, x.Series!))
            .ToList();
    }

    // ------------------------------------------------------------------ interni

    /// <summary>
    /// [F1-F3] MAX per-serie sull'indice (Symbol, Timeframe, TimestampUtc), MAI la GROUP BY
    /// sull'intera tabella: quella era un seq scan da 15 secondi misurati su 12,6M righe. Il costo
    /// scala col numero di serie (~222 lookup da pochi ms), non con la storia accumulata.
    /// </summary>
    private static async Task<Dictionary<(string, string), DateTime?>> ReadLastCandlesAsync(
        ApplicationDbContext db, IEnumerable<TrackedSeries> series, CancellationToken ct)
    {
        var last = new Dictionary<(string, string), DateTime?>();
        foreach (var s in series)
        {
            if (last.ContainsKey((s.Symbol, s.Timeframe))) continue;
            last[(s.Symbol, s.Timeframe)] = await db.OhlcvData
                .Where(c => c.Symbol == s.Symbol && c.Timeframe == s.Timeframe)
                .MaxAsync(c => (DateTime?)c.TimestampUtc, ct);
        }
        return last;
    }

    private static async Task<List<Lane>> ReadLanesAsync(ApplicationDbContext db, CancellationToken ct) =>
        await db.TradingEngineStates.AsNoTracking()
            .Where(t => t.IsRunning)
            .Select(t => new Lane(t.LaneId, t.Symbol, t.Timeframe, t.Mode.ToString()))
            .ToListAsync(ct);

    private async Task<Pulse> ReadPulseAsync(ApplicationDbContext db, CancellationToken ct)
    {
        // Il timbro del ciclo (HostHeartbeats, ruolo ingestion-sync). Se il worker non timbra
        // ancora (immagine del pod non aggiornata), si stima dal MAX(LastSyncUtc) delle serie
        // abilitate: meno preciso, ma «stimato» dichiarato è meglio di «nessun dato».
        var stamp = await db.HostHeartbeats.AsNoTracking()
            .Where(h => h.Host == HostHeartbeat.IngestionSyncRole)
            .Select(h => new { h.LastUtc, h.Version })
            .FirstOrDefaultAsync(ct);

        DateTime? lastCycle = stamp?.LastUtc;
        var outcome = stamp?.Version;
        var estimated = false;
        if (lastCycle is null)
        {
            lastCycle = await db.TrackedSeries.AsNoTracking()
                .Where(s => s.Enabled)
                .MaxAsync(s => s.LastSyncUtc, ct);
            estimated = lastCycle is not null;
        }

        // L'intervallo VERO è quello del processo che timbra (il pod ingestion ha il suo
        // appsettings, indipendente da quello del guscio): se il timbro lo dichiara, vince sul
        // nostro — una soglia calcolata sulla cadenza sbagliata giudica male in entrambe le
        // direzioni (review 2026-08-15).
        var interval = ProcioneMGR.Services.Ingestion.SyncPulse.TryParseStampedInterval(outcome) ?? ConfiguredInterval;
        var disabled = ProcioneMGR.Services.Ingestion.SyncPulse.IsDisabledOutcome(outcome);

        return new Pulse(
            lastCycle, outcome,
            Stalled: !disabled && ProcioneMGR.Services.Ingestion.SyncPulse.IsStalled(lastCycle, DateTime.UtcNow, interval),
            Estimated: estimated,
            Disabled: disabled,
            Interval: interval);
    }

    private void UpdateRecoveryFlags()
    {
        if (Rows is null) return;
        var now = DateTime.UtcNow;
        foreach (var row in Rows)
        {
            var stale = SeriesFreshness.IsStale(row.Series.Timeframe, row.LastCandleUtc, now);
            var known = _previousLastCandle.TryGetValue(row.Series.Id, out var prev);
            var advanced = known
                && row.LastCandleUtc is DateTime current
                && (prev is not DateTime p || current > p);
            var syncAlive = SyncPulse is { Stalled: false, Disabled: false };

            if (!stale || !syncAlive)
            {
                // Fresca (o sync fermo): nessun recupero da dichiarare, e il conteggio si riarma.
                row.IsRecovering = false;
                _observationsWithoutProgress.Remove(row.Series.Id);
            }
            else if (advanced)
            {
                row.IsRecovering = true;
                _observationsWithoutProgress[row.Series.Id] = 0;
            }
            else if (row.IsRecovering)
            {
                // Nessun progresso in questa osservazione: il badge resiste per un po' (vedi
                // RecoveryGraceObservations), poi la serie torna a dichiararsi FERMA.
                var missed = _observationsWithoutProgress.GetValueOrDefault(row.Series.Id) + 1;
                _observationsWithoutProgress[row.Series.Id] = missed;
                row.IsRecovering = missed <= RecoveryGraceObservations;
            }

            _previousLastCandle[row.Series.Id] = row.LastCandleUtc;
        }
    }
}
