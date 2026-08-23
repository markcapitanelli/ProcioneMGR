using System.Text;

namespace Procione;

/// <summary>
/// Le automazioni di Windows: leggerle, e ridurle a UNA.
///
/// Fino al 2026-08-23 la piattaforma si faceva avviare e sorvegliare da TRE meccanismi distinti,
/// tutti fuori dalla plancia e ognuno con la sua finestra:
///
///   · «ProcioneMGR Watchdog»  — Task Scheduler, ogni 5 minuti, LogonType=Interactive: una console
///                                PowerShell che saltava davanti all'utente 288 volte al giorno;
///   · «ProcioneMGR Backup DB» — Task Scheduler, 03:30, stessa forma;
///   · Startup\ProcioneMGR-BringUp.cmd — il ripiego non elevato di bringup.ps1 -Register, che
///                                apre una PowerShell minimizzata a ogni logon.
///
/// Dopo la migrazione ne resta uno solo, «ProcioneMGR Plancia», che avvia il supervisore di questa
/// stessa applicazione. Gli script non cambiano di una riga: cambia chi li chiama e come — con
/// l'output catturato, quindi senza far nascere nessuna finestra.
///
/// NOTA sulla regola «non riscrivere gli script». Qui si scrive PowerShell nuovo, ed e' voluto:
/// non esiste nessuno script di <c>scripts/</c> che registri la plancia: il lavoro nasce con lei.
/// Quello che NON si riscrive e' la registrazione dei task vecchi — quella si cancella e basta.
/// </summary>
internal static class Tasks
{
    /// <summary>
    /// Stato ed esito delle attivita' che ci interessano, e — separatamente — se la lettura e'
    /// RIUSCITA.
    ///
    /// I due fatti vanno tenuti distinti, e non e' pedanteria: se PowerShell non parte o sfora il
    /// tempo, l'output e' vuoto — esattamente come quando non esiste nessuna attivita'. Confonderli
    /// significa che la migrazione annuncia «nessuna automazione vecchia da togliere» e chiude
    /// dicendo «da adesso l'unica cosa che parte da sola e' questa plancia», mentre il watchdog
    /// vecchio continua a girare con la sua finestra. Meglio non toccare niente che raccontarlo.
    /// </summary>
    internal readonly record struct TaskRead(bool Ok, List<TaskInfo> Tasks)
    {
        public static TaskRead Fallita => new(false, []);
    }

    public static async Task<TaskRead> ReadAsync(IEnumerable<string> nomi)
    {
        var elenco = nomi.ToList();
        var r = await QueryRawAsync(elenco);
        var lette = Parsing.ScheduledTasks(r.Out);

        // Una lettura riuscita emette UNA riga per nome: un conteggio corto e' gia' il segnale che
        // qualcosa e' andato storto, anche quando il codice d'uscita non lo dice.
        return new TaskRead(r.Code == 0 && lette.Count == elenco.Count, lette);
    }

    /// <summary>Comodita' per chi il fallimento lo gestisce a monte: la sola lista.</summary>
    public static async Task<List<TaskInfo>> QueryAsync(IEnumerable<string> nomi)
        => (await ReadAsync(nomi)).Tasks;

    private static async Task<ExecResult> QueryRawAsync(IEnumerable<string> nomi)
    {
        // Nessuna virgoletta DOPPIA nel frammento: `powershell -Command` lo riceve gia' racchiuso
        // fra virgolette, e le doppie annidate si perdono per strada.
        var elenco = string.Join(",", nomi.Select(n => $"'{n.Replace("'", "''")}'"));
        var ps =
            $"foreach($n in @({elenco})){{ " +
            "$t = Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue; " +
            "if(-not $t){ $n + '|ASSENTE||'; continue }; " +
            "$i = Get-ScheduledTaskInfo -TaskName $n -ErrorAction SilentlyContinue; " +
            "$lr = ''; if($i -and $i.LastRunTime -and $i.LastRunTime.Year -gt 1900){ $lr = $i.LastRunTime.ToString('yyyy-MM-dd HH:mm') }; " +
            "$rc = ''; if($i){ $rc = [string]$i.LastTaskResult }; " +
            "$n + '|' + $t.State + '|' + $lr + '|' + $rc }";

        return await Proc.PsAsync(ps, 25000);
    }

    /// <summary>L'attivita' esiste (in qualunque stato, anche disabilitata).</summary>
    public static bool Exists(IEnumerable<TaskInfo> lette, string nome) =>
        lette.Any(t => t.Name == nome && t.State != "ASSENTE");

    /// <summary>
    /// L'attivita' esiste ED e' in grado di partire.
    ///
    /// «Esiste» e «sta girando» sono due domande diverse, e disabilitare un'attivita' dal Task
    /// Scheduler — invece di cancellarla — e' il gesto naturale di chi vuole fermarla. Contarla
    /// come coperta significherebbe dire in giallo «veglia e backup girano ancora dal Task
    /// Scheduler» mentre non gira nulla: un fatto mai misurato, e un guasto declassato ad avviso.
    /// Nel dubbio (stato sconosciuto) si dichiara SCOPERTO, non coperto.
    /// </summary>
    public static bool Active(IEnumerable<TaskInfo> lette, string nome) =>
        lette.Any(t => t.Name == nome && t.State is "Ready" or "Running" or "Queued");

    // =============================================================================================
    //  Registrazione
    // =============================================================================================

    /// <summary>
    /// Registra l'unica attivita' prevista: al logon parte <c>procione servizio --muto</c>.
    /// </summary>
    /// <returns>0 riuscito (task o ripiego), 2 fallito.</returns>
    public static int RegisterSupervisor()
    {
        var exe = Platform.SelfExe;
        if (exe is null || !Path.GetFileName(exe).StartsWith("procione", StringComparison.OrdinalIgnoreCase))
        {
            // Girando da `dotnet run` l'eseguibile del processo e' dotnet.exe: registrarlo
            // significherebbe un'attivita' che al logon prova a ricompilare il progetto.
            Ui.Error("la plancia non sta girando dal proprio eseguibile (probabilmente `dotnet run`).");
            Ui.Info("compila prima: `procione.cmd --ricompila`, poi rilancia il comando dall'eseguibile.");
            return 2;
        }

        // --- mai un worktree in un'attivita' che deve vivere per mesi ------------------------------
        // E' la lezione del 2026-08-17, e vale per gli eseguibili quanto per gli script: un worktree
        // sparisce con `git worktree remove`, e da quel logon il task fallisce con 0x8007002E —
        // nessun supervisore, nessuna veglia, nessun backup, e nessuno che se ne accorga.
        if (Platform.InWorktree(exe))
        {
            var principale = Path.Combine(Platform.MainRepoRoot, "tools", "Procione", "bin", "Release",
                                          "net10.0", "procione.exe");
            if (!File.Exists(principale))
            {
                Ui.Error("stai lanciando la plancia da un WORKTREE, e nel repository principale non c'e'.");
                Ui.Info($"worktree   : {Path.GetDirectoryName(exe)}");
                Ui.Info($"principale : {principale} (assente)");
                Ui.Info("un'attivita' che punta a un worktree muore con `git worktree remove`, in silenzio:");
                Ui.Info("e' l'incidente del 2026-08-17, sei notti di backup perse. Non la registro.");
                Ui.Info($"compila prima nel repository principale: cd {Platform.MainRepoRoot} && procione.cmd --ricompila");
                return 2;
            }
            Ui.Warn("lanciata da un worktree: registro l'eseguibile del repository PRINCIPALE.");
            exe = principale;
        }

        Ui.Info($"eseguibile : {exe}");
        Ui.Info($"repository : {Platform.MainRepoRoot}");

        // $$""" e non $""": lo script PowerShell contiene graffe sue (`{ exit 0 }`), e con una sola
        // il compilatore le leggerebbe come interpolazioni. Qui le interpolazioni sono {{...}}.
        var script = $$"""
        $ErrorActionPreference = 'Stop'
        $azione = New-ScheduledTaskAction -Execute '{{Escape(exe)}}' -Argument 'servizio --muto' -WorkingDirectory '{{Escape(Platform.MainRepoRoot)}}'
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User '{{Escape(Utente)}}'
        # ExecutionTimeLimit a ZERO significa "nessun tetto": senza, Windows ucciderebbe il
        # supervisore dopo 72 ore, e lo farebbe in silenzio.
        $impostazioni = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -StartWhenAvailable -MultipleInstances IgnoreNew `
            -ExecutionTimeLimit (New-TimeSpan -Seconds 0) `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5)
        Register-ScheduledTask -TaskName '{{Escape(Platform.SupervisorTask)}}' -Action $azione -Trigger $trigger `
            -Settings $impostazioni -Force `
            -Description 'Supervisore della plancia ProcioneMGR: veglia ogni 5 minuti e backup notturno, dentro un solo programma e senza finestre.' | Out-Null
        if (Get-ScheduledTask -TaskName '{{Escape(Platform.SupervisorTask)}}' -ErrorAction SilentlyContinue) { exit 0 }
        exit 1
        """;

        var esito = RunPs(script, "registrazione dell'attivita'");
        if (esito == 0)
        {
            Ui.Good($"attivita' «{Platform.SupervisorTask}» registrata e VERIFICATA (al logon).");
            return 0;
        }

        // Ripiego senza privilegi, lo stesso di bringup.ps1: la cartella Esecuzione automatica
        // ottiene lo stesso effetto e non chiede niente a nessuno.
        Ui.Warn("registrazione non riuscita (di norma: serve una shell elevata). Ripiego su Esecuzione automatica.");
        return WriteStartupShortcut(exe);
    }

    private static int WriteStartupShortcut(string exe)
    {
        try
        {
            var cartella = Path.GetDirectoryName(StartupPlancia)!;
            Directory.CreateDirectory(cartella);
            // /min piu' `--muto`: la finestra viene creata minimizzata da cmd e poi NASCOSTA dalla
            // plancia stessa. Senza `--muto` resterebbe una console nella barra delle applicazioni.
            File.WriteAllText(StartupPlancia,
                "@echo off\r\n" +
                "REM Avvia il supervisore della plancia ProcioneMGR (nessuna finestra: --muto la nasconde).\r\n" +
                $"start \"\" /min \"{exe}\" servizio --muto\r\n",
                Encoding.ASCII);
            Ui.Good($"ripiego scritto: {StartupPlancia}");
            Ui.Info("parte al prossimo logon. Adesso: `procione servizio` (oppure apri la plancia).");
            return 0;
        }
        catch (Exception ex)
        {
            Ui.Error($"anche il ripiego e' fallito: {ex.Message.Trim()}");
            return 2;
        }
    }

    // =============================================================================================
    //  Rimozione e migrazione
    // =============================================================================================

    public static bool Remove(string nome)
    {
        var r = Proc.Ps($"Unregister-ScheduledTask -TaskName '{nome.Replace("'", "''")}' -Confirm:$false " +
                        "-ErrorAction SilentlyContinue; " +
                        $"if (Get-ScheduledTask -TaskName '{nome.Replace("'", "''")}' -ErrorAction SilentlyContinue) " +
                        "{ exit 1 } else { exit 0 }", 25000);
        return r.Ok;
    }

    /// <summary>
    /// La migrazione: da tre meccanismi a uno.
    ///
    /// L'ordine conta ed e' quello sicuro: prima si registra il nuovo, poi si tolgono i vecchi.
    /// Al contrario, un fallimento a meta' lascerebbe la piattaforma senza NESSUNA sorveglianza —
    /// che e' il modo in cui un'unificazione peggiora le cose invece di migliorarle.
    /// </summary>
    public static async Task<int> MigrateAsync()
    {
        Ui.Title("Migrazione delle automazioni dentro la plancia");

        var lettura = await ReadAsync(Jobs.Legacy.Select(l => l.Task).Append(Platform.SupervisorTask));
        if (!lettura.Ok)
        {
            // Fail-closed: senza sapere cosa c'e', registrare il nuovo e dichiarare finito
            // lascerebbe vivi i doppioni annunciando il contrario.
            Ui.Error("non riesco a leggere le attivita' pianificate di Windows: non tocco niente.");
            Ui.Info("riprova; se insiste, `Get-ScheduledTask` da PowerShell dice cosa non va.");
            return 2;
        }

        var lette = lettura.Tasks;
        var daTogliere = Jobs.Legacy.Where(l => Exists(lette, l.Task)).ToList();
        var startupBringUp = File.Exists(Platform.StartupShortcut);
        // Il bring-up al logon esiste in DUE forme: il task (percorso primario di bringup.ps1
        // -Register) e il .cmd in Esecuzione automatica (il suo ripiego senza privilegi). Guardarne
        // una sola significherebbe togliere l'altra e non accendere niente al suo posto.
        var bringUpAlLogon = startupBringUp || daTogliere.Any(l => l.Job == "avvio");

        Ui.Info("cosa succede:");
        Ui.Line($"    · registro «{Platform.SupervisorTask}»: al logon parte il supervisore di questa plancia,",
                ConsoleColor.Gray);
        Ui.Line("      che esegue gli STESSI script con l'output catturato — nessuna finestra, mai.", ConsoleColor.Gray);
        foreach (var (task, job, era) in daTogliere)
            Ui.Line($"    · tolgo «{task}» ({era}) → diventa il lavoro «{job}»" +
                    (job == "avvio" ? ", che ACCENDO" : ""), ConsoleColor.Gray);
        if (startupBringUp)
            Ui.Line($"    · tolgo {Platform.StartupShortcut} → diventa il lavoro «avvio», che ACCENDO", ConsoleColor.Gray);
        if (daTogliere.Count == 0 && !startupBringUp)
            Ui.Line("    · nessuna automazione vecchia da togliere: c'e' solo da registrare la nuova.", ConsoleColor.Gray);

        if (!Ui.Confirm("Procedere?"))
        {
            Ui.Info("annullato: nulla e' stato toccato.");
            return 1;
        }

        Ui.Title("1. Registrazione del supervisore");
        if (RegisterSupervisor() != 0)
        {
            Ui.Error("il nuovo meccanismo non e' stato registrato: i vecchi restano dov'erano.");
            Ui.Info("meglio tre finestre che nessuna sorveglianza.");
            return 2;
        }

        var esito = 0;
        if (daTogliere.Count > 0) Ui.Title("2. Ritiro delle automazioni vecchie");
        foreach (var (task, _, _) in daTogliere)
        {
            if (Remove(task)) Ui.Good($"«{task}» rimossa (verificato).");
            else { Ui.Error($"«{task}» NON rimossa: probabilmente serve una shell elevata."); esito = 1; }
        }

        if (bringUpAlLogon)
        {
            // Il bring-up al logon c'era — come task, come .cmd, o entrambi: toglierlo senza
            // accendere il lavoro equivalente significherebbe spegnere in silenzio una cosa che
            // funzionava, e mostrarla nel quadro come «spento», cioe' come una scelta.
            if (Prefs.Set("avvio", true))
                Ui.Good("lavoro «avvio» ACCESO: il bring-up al logon continua a esserci, dentro la plancia.");
            else
            {
                Ui.Error("lavoro «avvio» NON acceso: il bring-up al logon andrebbe perso.");
                Ui.Info("rimedio: `procione lavoro avvio accendi`.");
                esito = 1;
            }
        }

        if (startupBringUp)
        {
            try
            {
                File.Delete(Platform.StartupShortcut);
                Ui.Good($"«{Path.GetFileName(Platform.StartupShortcut)}» rimosso da Esecuzione automatica.");
            }
            catch (Exception ex) { Ui.Error($"non rimosso: {ex.Message.Trim()}"); esito = 1; }
        }

        Ui.Title("Fatto");
        Ui.Info("da adesso l'unica cosa che parte da sola e' questa plancia. `procione stato` lo mostra.");
        Ui.Info("il supervisore parte al prossimo logon; per accenderlo subito: `procione servizio`.");
        return esito;
    }

    /// <summary>Toglie tutto: si torna a lanciare ogni cosa a mano.</summary>
    public static async Task<int> RemoveAllAsync()
    {
        var nomi = Jobs.Legacy.Select(l => l.Task).Append(Platform.SupervisorTask).ToList();
        var lette = await QueryAsync(nomi);
        var presenti = nomi.Where(n => Exists(lette, n)).ToList();
        var startup = new[] { Platform.StartupShortcut, StartupPlancia }.Where(File.Exists).ToList();

        if (presenti.Count == 0 && startup.Count == 0)
        {
            Ui.Info("non c'e' nessuna automazione registrata: niente da togliere.");
            return 0;
        }
        foreach (var n in presenti) Ui.Line($"    · attivita' «{n}»", ConsoleColor.Gray);
        foreach (var s in startup) Ui.Line($"    · {s}", ConsoleColor.Gray);
        if (!Ui.Confirm("Togliere tutto? Da quel momento nulla parte piu' da solo."))
        {
            Ui.Info("annullato.");
            return 1;
        }

        var esito = 0;
        foreach (var n in presenti)
        {
            if (Remove(n)) Ui.Good($"«{n}» rimossa.");
            else { Ui.Error($"«{n}» NON rimossa (shell elevata?)."); esito = 1; }
        }
        foreach (var s in startup)
        {
            try { File.Delete(s); Ui.Good($"{Path.GetFileName(s)} rimosso."); }
            catch (Exception ex) { Ui.Error($"{s}: {ex.Message.Trim()}"); esito = 1; }
        }
        return esito;
    }

    // =============================================================================================
    //  Utilita'
    // =============================================================================================

    public static string StartupPlancia => Path.Combine(
        Path.GetDirectoryName(Platform.StartupShortcut)!, "ProcioneMGR-Plancia.cmd");

    private static string Utente => $"{Environment.UserDomainName}\\{Environment.UserName}";

    private static string Escape(string s) => s.Replace("'", "''");

    /// <summary>
    /// Esegue uno script PowerShell scritto qui. Passa da un FILE e non da <c>-Command</c>: uno
    /// script di dieci righe con virgolette e apici, infilato in un solo argomento, e' il modo
    /// classico di ottenere un errore di sintassi che si manifesta solo sulla macchina dell'utente.
    /// </summary>
    private static int RunPs(string script, string cosa)
    {
        var file = Path.Combine(Path.GetTempPath(), $"procione-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var r = Proc.Capture("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], 60000);
            if (!r.Ok && r.Text.Length > 0) Ui.Info($"{cosa}: {r.FirstLine}");
            return r.Ok ? 0 : 2;
        }
        catch (Exception ex)
        {
            Ui.Error($"{cosa} fallita: {ex.Message.Trim()}");
            return 2;
        }
        finally { try { File.Delete(file); } catch { } }
    }
}
