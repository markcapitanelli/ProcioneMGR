namespace Procione;

/// <summary>
/// La plancia interattiva: quello che si vede digitando <c>procione</c> senza argomenti.
///
/// Il quadro si ridisegna da solo e i comandi si danno con UN tasto. Non e' vezzo: le cose che si
/// fanno qui si fanno mentre qualcosa non va, e in quel momento ricordarsi il nome esatto di uno
/// script fra i diciannove di <c>scripts/</c> e' esattamente il passaggio che fa perdere tempo.
/// </summary>
internal static class Dashboard
{
    private const int SecondiFraAggiornamenti = 12;

    /// <param name="soloLettura">true = comando `guarda`: si osserva e basta, nessuna azione.</param>
    public static async Task<int> RunAsync(bool soloLettura, int? ogniSecondi = null)
    {
        // Senza una console vera (pipe, task pianificato, CI) la plancia degrada a una stampa
        // secca: meglio un'uscita utilizzabile che un ciclo interattivo che nessuno puo' guidare.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            var uno = await Probes.RunAsync();
            Ui.Print(Ui.Render(uno, conRimedi: true));
            return uno.ExitCode;
        }

        var intervallo = TimeSpan.FromSeconds(Math.Max(2, ogniSecondi ?? SecondiFraAggiornamenti));
        var dipinte = 0;
        Snapshot? quadro = null;
        var prossimo = DateTime.MinValue;

        // La plancia OSPITA il supervisore: aprirla accende le automazioni, chiuderla le spegne —
        // a meno che non stiano gia' girando altrove (residente dal logon), nel qual caso non si
        // duplica nulla e ci si limita a mostrarle. `guarda` non lo accende mai: e' sola lettura.
        //
        // Muto di proposito: il quadro si ridisegna in posizione fissa, e un supervisore che
        // stampa mentre si dipinge lo sfregerebbe. Quello che ha fatto si vede nel quadro, e per
        // esteso in `procione log supervisore`.
        var supervisore = soloLettura ? null : Supervisor.TryAcquire();
        using var cts = new CancellationTokenSource();
        var ciclo = supervisore?.RunAsync(muto: true, cts.Token);

        try
        {
            Console.CursorVisible = false;
            Console.Clear();

            while (true)
            {
                if (DateTime.UtcNow >= prossimo)
                {
                    if (quadro is not null) Disegna(quadro, ref dipinte, soloLettura, "rilevo...");
                    var fresco = await Probes.RunAsync();
                    quadro = fresco;
                    prossimo = DateTime.UtcNow.Add(intervallo);
                    Disegna(fresco, ref dipinte, soloLettura, null);
                }

                // KeyAvailable puo' lanciare su console che non espongono l'input (host insoliti):
                // la plancia deve continuare ad aggiornare il quadro, non morire.
                bool tastoPronto;
                try { tastoPronto = Console.KeyAvailable; } catch { tastoPronto = false; }

                if (tastoPronto)
                {
                    var tasto = Console.ReadKey(intercept: true);
                    if (tasto.Key is ConsoleKey.Q or ConsoleKey.Escape) return quadro?.ExitCode ?? 0;

                    if (tasto.Key == ConsoleKey.R) { prossimo = DateTime.MinValue; continue; }

                    if (soloLettura) continue;

                    if (Esegui(tasto.KeyChar, quadro))
                    {
                        // Dopo un'azione il quadro precedente e' carta straccia: si rilegge subito.
                        Console.CursorVisible = false;
                        Console.Clear();
                        dipinte = 0;
                        prossimo = DateTime.MinValue;
                    }
                }

                await Task.Delay(120);

                // Il ciclo ospitato puo' finire prima della plancia: `procione servizio ferma` da
                // un'altra finestra lo annulla, oppure e' morto per un'eccezione. Da quel momento
                // l'esclusione va LIBERATA subito — tenerla renderebbe impossibile far ripartire
                // qualunque supervisore, in qualunque processo, e la plancia continuerebbe a
                // mostrarsi come se stesse vegliando.
                if (ciclo is { IsCompleted: true } && supervisore is not null)
                {
                    supervisore.Dispose();
                    supervisore = null;
                    ciclo = null;
                    prossimo = DateTime.MinValue;
                }
            }
        }
        finally
        {
            cts.Cancel();

            // Si aspetta che il ciclo chiuda davvero: e' lui a scrivere il file di stato con il
            // battito azzerato. Uscire senza aspettarlo lascerebbe un battito fresco, e per un'ora
            // `procione stato` direbbe che le automazioni girano mentre non gira piu' niente.
            //
            // L'attesa pero' va DETTA. L'annullamento non raggiunge il processo figlio (uno script
            // lanciato non si interrompe a meta'), quindi se in quel momento sta girando un
            // pg_dump si puo' restare qui minuti: senza una riga, la plancia sembrerebbe piantata
            // sull'ultimo fotogramma, e la si ucciderebbe a meta' backup.
            if (ciclo is not null)
            {
                Console.CursorVisible = true;
                try { Console.SetCursorPosition(0, Math.Max(0, dipinte)); } catch { }
                if (!ciclo.IsCompleted)
                {
                    var lavoro = supervisore?.InCorso;
                    Ui.Line(lavoro is null
                        ? "\n  chiudo il supervisore..."
                        : $"\n  attendo la fine di «{lavoro.Value.Nome}» (tetto {Ui.Age(lavoro.Value.Tetto)}): " +
                          "interromperlo adesso lascerebbe il lavoro a meta'. Ctrl+C per non aspettare.",
                        ConsoleColor.Yellow);
                }
                try { await ciclo; } catch { }
                dipinte = 0;
            }

            supervisore?.Dispose();
            Console.CursorVisible = true;
            try { Console.SetCursorPosition(0, Math.Max(0, dipinte)); } catch { }
            Console.WriteLine();
        }
    }

    private static void Disegna(Snapshot q, ref int dipinte, bool soloLettura, string? nota)
    {
        var righe = Ui.Render(q, conRimedi: true);
        righe.Add(ScreenLine.Plain(""));
        righe.Add(ScreenLine.Plain(soloLettura
            ? "  [r] rileva ora     [q] esci"
            : "  [r] rileva ora   [a] avvia tutto   [g] guscio   [t] ripara tunnel   [p] ripara proxy",
            ConsoleColor.DarkCyan));
        if (!soloLettura)
        {
            righe.Add(ScreenLine.Plain(
                "  [m] riavvia motore   [l] log   [b] backup   [v] veglia ora   [j] lavori",
                ConsoleColor.DarkCyan));
            righe.Add(ScreenLine.Plain(
                "  [d] dottore   [o] apri UI   [q] esci",
                ConsoleColor.DarkCyan));
        }
        if (nota is not null)
            righe.Add(ScreenLine.Plain("  " + nota, ConsoleColor.DarkGray));

        Ui.Paint(righe, ref dipinte);
    }

    /// <returns>true se e' stata eseguita un'azione (e quindi il quadro va rifatto).</returns>
    private static bool Esegui(char tasto, Snapshot? quadro)
    {
        Action? azione = char.ToLowerInvariant(tasto) switch
        {
            'a' => () => Actions.UpAll(forza: false),
            'g' => () => GuscioSuOGiu(quadro),
            't' => () => Actions.RepairTunnels(),
            'p' => () => Actions.RepairProxy(),
            'm' => () => Actions.Restart("motore", confermato: false),
            'b' => () => Actions.Backup(verifica: false),
            'v' => () => Actions.Watchdog(),
            'j' => MenuLavori,
            'd' => () => Actions.Doctor(),
            'o' => () => Actions.Open(null),
            'l' => MenuLog,
            _ => null,
        };
        if (azione is null) return false;

        Console.CursorVisible = true;
        Console.Clear();
        try { azione(); }
        catch (Exception ex) { Ui.Error(ex.Message.Trim()); }
        Ui.Line("\n  — premi INVIO per tornare alla plancia —", ConsoleColor.DarkGray);
        Console.ReadLine();
        return true;
    }

    /// <summary>Il tasto [g] fa la cosa sensata: accende se e' spento, spegne se e' acceso.</summary>
    private static void GuscioSuOGiu(Snapshot? quadro)
    {
        var acceso = Probes.ListeningPorts().Contains(Platform.ShellPort);
        if (!acceso) { Actions.UpShell(forza: false); return; }

        Ui.Warn($"il guscio e' acceso su :{Platform.ShellPort}.");
        if (Ui.Confirm("Fermarlo?")) Actions.DownShell();
        else Ui.Info("lasciato acceso.");
    }

    /// <summary>I lavori del supervisore: guardarli, accenderli, spegnerli, farne partire uno.</summary>
    private static void MenuLavori()
    {
        Actions.Lavoro(null, null).GetAwaiter().GetResult();

        Ui.Write("\n  quale lavoro? (INVIO per tornare): ", ConsoleColor.Yellow);
        var nome = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(nome)) { Ui.Info("annullato."); return; }
        if (Jobs.Find(nome) is null) { Ui.Error($"lavoro sconosciuto: '{nome}'."); return; }

        Ui.Write("  cosa fare? [ora | accendi | spegni] ", ConsoleColor.Yellow);
        var cosa = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cosa)) { Ui.Info("annullato."); return; }

        Console.Clear();
        Actions.Lavoro(nome, cosa).GetAwaiter().GetResult();
    }

    private static void MenuLog()
    {
        Ui.Title("Quale log?");
        var voci = new[] { "supervisore", "guscio", "motore", "ingestion", "ml", "bringup", "watchdog" };
        for (var i = 0; i < voci.Length; i++) Ui.Info($"[{i + 1}] {voci[i]}");
        Ui.Write("\n  scegli (INVIO per annullare): ", ConsoleColor.Yellow);
        var scelta = Console.ReadLine();
        if (!int.TryParse(scelta, out var n) || n < 1 || n > voci.Length) { Ui.Info("annullato."); return; }
        Console.Clear();
        Actions.Logs(voci[n - 1], righe: 120, segui: false);
    }
}
