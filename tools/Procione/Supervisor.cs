using System.Diagnostics;
using System.Text.Json;

namespace Procione;

/// <summary>
/// Il supervisore residente: la parte della plancia che <b>non</b> aspetta che tu la guardi.
///
/// PERCHE' ESISTE. Fino al 2026-08-23 le automazioni della piattaforma erano attivita' pianificate
/// di Windows, e ognuna apriva la sua finestra: «ProcioneMGR Watchdog» lanciava
/// <c>powershell.exe -File watchdog.ps1</c> ogni cinque minuti con <c>LogonType=Interactive</c>,
/// cioe' una finestra che saltava davanti a qualunque cosa si stesse facendo, 288 volte al giorno.
/// Peggio del fastidio: erano automazioni fuori dalla plancia, quindi il loro esito si poteva
/// leggere solo aprendo il Task Scheduler — ed e' esattamente cosi' che il backup notturno ha
/// fallito SEI notti di fila senza che nessuno se ne accorgesse (2026-08-17).
///
/// Qui gli stessi script girano dentro l'unico programma, con l'output CATTURATO: nessuna finestra
/// nasce mai, l'esito finisce nel quadro accanto a tutto il resto, e il log e' uno solo.
///
/// UN SOLO SCRITTORE, anche qui (regola 2). Due supervisori vivi significherebbero due pg_dump
/// nella stessa notte e due watchdog che si contendono la riparazione del tunnel: l'esclusione e'
/// un mutex di sessione, e chi non lo ottiene degrada a OSSERVATORE — legge lo stato dell'altro e
/// lo mostra, senza duplicare nulla.
/// </summary>
internal sealed class Supervisor : IDisposable
{
    // Namespace `Local\`: tutti i processi della plancia vivono nella sessione dell'utente, e
    // creare oggetti in `Global\` richiede un privilegio (SeCreateGlobalPrivilege) che un utente
    // interattivo non ha — la creazione fallirebbe, e con essa l'esclusione.
    private const string NomeMutex = @"Local\ProcioneMGR-Supervisore";
    private const string NomeStop = @"Local\ProcioneMGR-Supervisore-Stop";

    /// Ogni quanto il ciclo si sveglia per guardare le scadenze e per battere il cuore. Non e' la
    /// cadenza dei lavori: quella la decide <see cref="Schedule"/>.
    private static readonly TimeSpan Passo = TimeSpan.FromSeconds(10);

    /// Oltre questa eta' il battito non prova piu' nulla: il processo puo' esserci ancora ed essere
    /// piantato. 6 passi di margine, perche' un'esecuzione lunga (un pg_dump) tiene fermo il ciclo.
    public static readonly TimeSpan BattitoScaduto = TimeSpan.FromMinutes(65);

    private readonly Mutex _mutex;
    private readonly List<JobState> _stati = [];
    private readonly DateTimeOffset _avvio = DateTimeOffset.Now;
    private Dictionary<string, bool> _accesi = [];
    private bool _muto;

    /// <summary>Il lavoro in corso adesso, se ce n'e' uno: serve a chi deve aspettarlo sapendo cosa.</summary>
    public (string Nome, TimeSpan Tetto)? InCorso { get; private set; }

    private Supervisor(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Da quando la macchina e' accesa. Serve al lavoro «all'avvio»: dev'essere fatto una volta per
    /// SESSIONE, non una volta per processo — altrimenti riaprire la plancia farebbe ripartire un
    /// bring-up da venti minuti, e la si imparerebbe a non aprire.
    /// </summary>
    private static DateTimeOffset Accensione => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    // =============================================================================================
    //  Esclusione
    // =============================================================================================

    /// <summary>
    /// Prova a diventare IL supervisore. Restituisce <c>null</c> se ce n'e' gia' uno vivo: in quel
    /// caso non si duplica nulla e si resta a guardare.
    /// </summary>
    public static Supervisor? TryAcquire()
    {
        Mutex? m = null;
        try
        {
            m = new Mutex(initiallyOwned: false, NomeMutex);
            // WaitOne(0): o e' libero adesso, o c'e' gia' un supervisore. Non si mette in coda —
            // un secondo supervisore che parte «appena l'altro finisce» non lo vuole nessuno.
            if (!m.WaitOne(0))
            {
                m.Dispose();
                return null;
            }
            return new Supervisor(m);
        }
        catch (AbandonedMutexException)
        {
            // Il supervisore precedente e' morto senza rilasciare (kill, crash, spegnimento
            // brutale). L'eccezione arriva DOPO che la proprieta' e' stata concessa: il mutex e'
            // gia' nostro, e va tenuto QUESTO — costruirne un altro non riacquisirebbe niente.
            return m is null ? null : new Supervisor(m);
        }
        catch (Exception ex)
        {
            m?.Dispose();
            Ui.Warn($"esclusione non disponibile ({ex.Message.Trim()}): il supervisore non parte.");
            return null;
        }
    }

    /// <summary>Chiede al supervisore residente di fermarsi. Vero se qualcuno stava ascoltando.</summary>
    public static bool RequestStop()
    {
        // Gli oggetti di sincronizzazione CON NOME esistono solo su Windows: la plancia gira solo
        // li' (powershell, Get-NetTCPConnection, Task Scheduler), ma la guardia tiene il compilatore
        // tranquillo senza inventare un supporto che non c'e'.
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(NomeStop, out var evento)) return false;
            using (evento) evento.Set();
            return true;
        }
        catch { return false; }
    }

    // =============================================================================================
    //  Ciclo
    // =============================================================================================

    public async Task RunAsync(bool muto, CancellationToken esterno = default)
    {
        _muto = muto;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(esterno);
        var ct = cts.Token;

        // Il segnale di arresto e' un evento con nome, non l'uccisione del processo: cosi'
        // `procione servizio ferma` (e `procione.cmd --ricompila`, che deve liberare l'eseguibile)
        // ottengono una chiusura pulita, con lo stato salvato.
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, NomeStop);
        stop.Reset();
        using var registrazione = new ThreadPoolWait(stop, cts);

        Carica();
        Log($"supervisore avviato (pid {Environment.ProcessId}) su {Platform.RepoRoot}");
        foreach (var j in Jobs.All)
            Log($"  · {j.Name,-8} {j.When.Describe(),-24} {(Stato(j).Enabled ? j.What : "SPENTO — " + j.What)}");
        Battito();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Le preferenze si rileggono a ogni giro, non solo all'avvio: cosi' `procione
                // lavoro <nome> spegni` ha effetto subito, senza far ripartire il supervisore.
                // Se in questo istante il file non e' leggibile si TIENE l'ultima mappa buona:
                // ricadere sui default riaccenderebbe, in silenzio, cio' che era stato spento.
                _accesi = Prefs.Read() ?? _accesi;
                var accesi = _accesi;

                foreach (var job in Jobs.All)
                {
                    if (ct.IsCancellationRequested) break;
                    var st = Stato(job);
                    st.Enabled = Prefs.IsEnabled(job, accesi);
                    if (!st.Enabled) continue;
                    if (!job.When.IsDue(st.LastRun, DateTimeOffset.Now)) continue;
                    await EseguiAsync(job, st, ct);
                }

                Battito();
                await Task.Delay(Passo, ct);
            }
        }
        catch (OperationCanceledException) { /* arresto richiesto: e' la via normale di uscita */ }
        finally
        {
            Log("supervisore fermato.");
            // Il battito si azzera USCENDO: un file di stato lasciato con l'ultimo battito fresco
            // farebbe credere per un'ora che le automazioni stiano ancora girando.
            Battito(spento: true);
        }
    }

    private async Task<bool> EseguiAsync(Job job, JobState st, CancellationToken ct)
    {
        var percorso = Platform.Script(job.Script);
        var inizio = DateTimeOffset.Now;

        if (!File.Exists(percorso))
        {
            // Un'azione che punta a uno script sparito e' precisamente il modo in cui il backup
            // notturno e' morto in silenzio: qui e' un fallimento registrato, non un nulla di fatto.
            Registra(st, inizio, inizio, Proc.Failed, $"script assente: {percorso}");
            Log($"✖ {job.Name}: script assente ({percorso})", ConsoleColor.Red);
            return false;
        }

        Log($"→ {job.Name}: {job.Script} {string.Join(' ', job.Args)}".TrimEnd());

        // Si dichiara PRIMA di partire. Se il supervisore muore qui in mezzo, il giro successivo
        // trovera' questo campo valorizzato e sapra' che l'esecuzione e' rimasta a meta' — invece
        // di dedurre dal disco che era andata bene.
        st.RunningSince = inizio;
        Salva();

        var argv = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", percorso };
        argv.AddRange(job.Args);

        InCorso = (job.Name, job.Timeout);
        ExecResult r;
        try { r = await Proc.CaptureAsync("powershell", argv, (int)job.Timeout.TotalMilliseconds, Ambiente()); }
        finally { InCorso = null; }
        var fine = DateTimeOffset.Now;

        // Il testo completo va SEMPRE nel log, riuscita o no: la sintesi sta nel quadro, ma quando
        // si va a capire perche' un backup e' fallito serve tutto quello che ha detto.
        foreach (var riga in (r.Out + "\n" + r.Err).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            LogSolaTraccia($"    {riga.TrimEnd()}");

        Registra(st, inizio, fine, r.Code, Sintesi(r), job);

        if (r.Ok) Log($"● {job.Name}: riuscito in {Ui.Age(fine - inizio)} — {st.LastSummary}", ConsoleColor.Green);
        else Log($"✖ {job.Name}: {Diagnosi(r.Code)} dopo {Ui.Age(fine - inizio)} — {st.LastSummary}", ConsoleColor.Red);

        if (ct.IsCancellationRequested) return r.Ok;
        return r.Ok;
    }

    private void Registra(JobState st, DateTimeOffset inizio, DateTimeOffset fine, int codice,
                          string sintesi, Job? job = null)
    {
        // L'intervallo si conta dall'INIZIO, come fa il Task Scheduler. Ma se l'esecuzione ha
        // sforato la propria cadenza si riparte dalla FINE: altrimenti un lavoro cronicamente
        // lento ripartirebbe di fila all'infinito, senza mai una pausa.
        st.LastRun = job is { When.Kind: Cadence.Interval } && fine - inizio >= job.When.Every ? fine : inizio;
        st.LastElapsedSeconds = (fine - inizio).TotalSeconds;
        st.LastCode = codice;
        st.LastSummary = sintesi;
        st.ConsecutiveFailures = codice == 0 ? 0 : st.ConsecutiveFailures + 1;
        st.RunningSince = null;
        Salva();
    }

    /// Esecuzione rimasta a meta': il supervisore e' morto mentre lo script girava. Non e' «uscita
    /// diversa da zero» — lo script potrebbe anche aver finito bene — ed e' l'unico stato in cui
    /// cio' che si trova sul disco non prova nulla.
    public const int Interrotto = -9;

    /// <summary>L'ultima riga che dice qualcosa. Sul fallimento vince stderr, dove sta la ragione.</summary>
    private static string Sintesi(ExecResult r)
    {
        var fonte = !r.Ok && r.Err.Length > 0 ? r.Err : r.Text;
        var riga = fonte.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .LastOrDefault(x => x.Length > 0)
                   ?? (r.Ok ? "nessun output" : Diagnosi(r.Code));
        return riga.Length > 160 ? riga[..157] + "..." : riga;
    }

    public static string Diagnosi(int codice) => codice switch
    {
        0 => "riuscito",
        Proc.TimedOut => "TEMPO SCADUTO (processo ucciso)",
        Proc.Failed => "non eseguibile",
        Proc.NotStarted => "non avviato",
        Interrotto => "INTERROTTO a meta' (il supervisore e' morto mentre girava)",
        _ => $"uscita {codice}",
    };

    /// <summary>
    /// L'ambiente dei figli. Si eredita quello del supervisore e si colma un solo buco: il token
    /// Telegram. Il watchdog notifica leggendo <c>TELEGRAM_BOT_TOKEN</c> dall'ambiente, e se manca
    /// si limita a loggare — cioe' scopre il guasto e non riesce a dirlo. Il token sta gia' nel
    /// file che usano il guscio e .claude/launch.json: si passa di li'.
    /// </summary>
    private static Dictionary<string, string>? Ambiente()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"))) return null;
        try
        {
            if (!File.Exists(Platform.TelegramTokenFile)) return null;
            var token = File.ReadAllText(Platform.TelegramTokenFile).Trim();
            return token.Length == 0 ? null : new Dictionary<string, string> { ["TELEGRAM_BOT_TOKEN"] = token };
        }
        catch { return null; }
    }

    // =============================================================================================
    //  Stato condiviso
    // =============================================================================================

    private JobState Stato(Job job) => Stato(job.Name);

    private JobState Stato(string nome)
    {
        var st = _stati.FirstOrDefault(s => s.Name == nome);
        if (st is not null) return st;
        var job = Jobs.Find(nome);
        st = new JobState { Name = nome, Enabled = job is null || Prefs.IsEnabled(job) };
        _stati.Add(st);
        return st;
    }

    private void Carica()
    {
        var precedente = ReadState();
        var accesi = Prefs.Read();
        foreach (var job in Jobs.All)
        {
            var salvato = precedente?.Jobs.FirstOrDefault(j => j.Name == job.Name);
            var st = new JobState
            {
                Name = job.Name,
                // Una chiave nata DOPO il file di stato non deve leggersi come «era spento» ne'
                // come «era gia' fallito»: e' la lezione del watchdog, che con un campo nuovo letto
                // $null sparava una falsa notifica di ripristino.
                Enabled = Prefs.IsEnabled(job, accesi),
                LastRun = salvato?.LastRun,
                LastElapsedSeconds = salvato?.LastElapsedSeconds ?? 0,
                LastCode = salvato?.LastCode ?? 0,
                LastSummary = salvato?.LastSummary ?? "",
                ConsecutiveFailures = salvato?.ConsecutiveFailures ?? 0,
            };

            // Un'esecuzione rimasta a meta': il supervisore precedente e' morto mentre lo script
            // girava. Si registra come tale — e NON si tocca LastRun, che resta l'ultima esecuzione
            // davvero conclusa: cosi' la scadenza persa resta persa, e il recupero riparte.
            var interrotto = salvato?.RunningSince is not null;
            if (interrotto)
            {
                st.LastCode = Interrotto;
                st.LastSummary = $"partito {salvato!.RunningSince:yyyy-MM-dd HH:mm}, mai concluso";
                st.ConsecutiveFailures = (salvato.ConsecutiveFailures) + 1;
            }

            // Il backup si fa credere al DISCO, non alla nostra memoria: il dump piu' recente e' il
            // dato osservabile: copre il backup fatto a mano, quello fatto dalla vecchia attivita'
            // pianificata prima della migrazione, e la macchina che ha cambiato repository.
            // «Nessun dump» diventa una data infinitamente vecchia, cioe' «fallo appena puoi».
            //
            // Ma NON dopo un'interruzione: li' il file piu' recente e' con ogni probabilita' il
            // dump troncato che pg_dump stava scrivendo quando e' stato ucciso, e prenderlo per
            // buono trasformerebbe il recupero in un salto silenzioso.
            if (job.SeedFromBackupDir && !interrotto)
            {
                var daDisco = UltimoBackup();
                if (daDisco is not null && (st.LastRun is null || daDisco > st.LastRun))
                    st.LastRun = daDisco;
                else if (st.LastRun is null)
                    st.LastRun = DateTimeOffset.MinValue;
            }
            else if (job.SeedFromBackupDir && st.LastRun is null)
            {
                st.LastRun = DateTimeOffset.MinValue;
            }

            // «All'avvio» significa una volta per SESSIONE, non una volta per processo: un'esecuzione
            // precedente all'accensione della macchina non conta, una successiva si'. Senza questa
            // riga il bring-up si farebbe una volta sola nella storia della macchina; senza la
            // condizione, si rifarebbe ogni volta che si apre la plancia.
            if (job.When.Kind == Cadence.AtStart && st.LastRun < Accensione)
                st.LastRun = null;

            _stati.Add(st);
        }
    }

    /// <summary>
    /// Data dell'ultimo dump PLAUSIBILE sul disco, oppure <c>null</c> se non ce n'e' nessuno.
    ///
    /// «Plausibile» e non «esistente»: i file sotto il megabyte si scartano, che e' la stessa
    /// soglia con cui <c>db-backup.ps1</c> dichiara sospetto un dump (il database vero ne pesa
    /// 350). Un file esiguo non e' un backup, e prenderlo per tale farebbe saltare il giro
    /// successivo — cioe' il contrario di quello che serve.
    /// </summary>
    public static DateTimeOffset? UltimoBackup()
    {
        try
        {
            if (!Directory.Exists(Platform.BackupDir)) return null;
            var ultimo = new DirectoryInfo(Platform.BackupDir)
                .GetFiles("procionemgr-*.dump")
                .Where(f => f.Length >= 1L << 20)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return ultimo is null ? null : new DateTimeOffset(ultimo.LastWriteTime);
        }
        catch { return null; }
    }

    private void Battito(bool spento = false) => Salva(spento);

    private void Salva(bool spento = false)
    {
        var stato = new SupervisorState
        {
            Pid = Environment.ProcessId,
            Started = _avvio,
            Heartbeat = spento ? DateTimeOffset.MinValue : DateTimeOffset.Now,
            Repo = Platform.RepoRoot,
            Jobs = _stati,
        };
        // Se non si riesce a scrivere, il supervisore lavora lo stesso: e' stato di macchina, non
        // una parte del lavoro.
        Files.WriteAtomic(Platform.SupervisorState, JsonSerializer.Serialize(stato, Json));
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Lo stato del supervisore residente, quale che sia il processo che lo esegue.</summary>
    public static SupervisorState? ReadState()
    {
        try
        {
            var testo = Files.ReadShared(Platform.SupervisorState);
            return testo is null ? null : JsonSerializer.Deserialize<SupervisorState>(testo, Json);
        }
        catch { return null; }
    }

    /// <summary>
    /// C'e' davvero un supervisore vivo? Il battito e' la prova; il PID da solo non lo e' (si
    /// riusa, e un processo puo' essere vivo e piantato). Quando il battito e' fresco si controlla
    /// anche che il processo esista: un file lasciato da uno spegnimento brutale non deve valere
    /// per un'ora come «tutto sotto controllo».
    /// </summary>
    public static bool IsAlive(SupervisorState? s, DateTimeOffset adesso)
    {
        if (s is null || s.Pid <= 0) return false;
        if (!Verdicts.HeartbeatFresh(s.Heartbeat, adesso, BattitoScaduto)) return false;
        if (s.Pid == Environment.ProcessId) return true;
        try { using var p = Process.GetProcessById(s.Pid); return !p.HasExited; }
        catch { return false; }
    }

    // =============================================================================================
    //  Log
    // =============================================================================================

    private const long TettoLog = 4L << 20;

    private void Log(string riga, ConsoleColor colore = ConsoleColor.DarkGray)
    {
        if (!_muto) Ui.Line("  " + riga, colore);
        LogSolaTraccia(riga);
    }

    private static void LogSolaTraccia(string riga)
    {
        try
        {
            Ruota();
            File.AppendAllText(Platform.SupervisorLog,
                               $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {riga}{Environment.NewLine}");
        }
        catch { /* il log non deve poter fermare il supervisore */ }
    }

    private static void Ruota()
    {
        try
        {
            var f = new FileInfo(Platform.SupervisorLog);
            if (!f.Exists || f.Length < TettoLog) return;
            File.Move(Platform.SupervisorLog, Platform.SupervisorLog + ".1", overwrite: true);
        }
        catch { }
    }

    /// <summary>
    /// Rilascia l'esclusione.
    ///
    /// <c>ReleaseMutex</c> puo' lanciare quando la chiusura avviene su un thread diverso da quello
    /// che ha acquisito (i mutex di Windows hanno affinita' di thread, e il finally di un metodo
    /// asincrono puo' girare ovunque): l'eccezione si ingoia perche' non cambia nulla in pratica —
    /// la plancia termina subito dopo, e il sistema rilascia comunque il mutex alla morte del
    /// processo. Chi lo prendera' dopo vedra' AbandonedMutexException, che e' gestita.
    /// </summary>
    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }

    /// <summary>
    /// Collega un evento con nome a una richiesta di annullamento, senza bloccare un thread in
    /// attesa: <c>RegisterWaitForSingleObject</c> usa un thread di attesa condiviso del pool.
    /// </summary>
    private sealed class ThreadPoolWait : IDisposable
    {
        private readonly RegisteredWaitHandle _handle;

        public ThreadPoolWait(WaitHandle evento, CancellationTokenSource cts) =>
            _handle = ThreadPool.RegisterWaitForSingleObject(
                evento, (_, _) => { try { cts.Cancel(); } catch { } }, null, -1, executeOnlyOnce: true);

        public void Dispose() => _handle.Unregister(null);
    }
}
