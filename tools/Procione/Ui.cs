using System.Runtime.InteropServices;
using System.Text;

namespace Procione;

/// <summary>Riga gia' pronta per essere dipinta: simbolo colorato + testo.</summary>
internal readonly record struct ScreenLine(string Glyph, ConsoleColor GlyphColor, string Text, ConsoleColor TextColor)
{
    public static ScreenLine Plain(string text, ConsoleColor color = ConsoleColor.Gray)
        => new("", ConsoleColor.Gray, text, color);
}

/// <summary>
/// Tutto cio' che si stampa. Colori con <see cref="Console.ForegroundColor"/> e non con sequenze
/// ANSI: funziona anche fuori da Windows Terminal (console classica, output di un task pianificato,
/// riquadro di un IDE) senza riempire il testo di caratteri di escape quando i colori non ci sono.
/// </summary>
internal static class Ui
{
    public static bool Colors { get; set; } = true;

    public static void Init()
    {
        // I simboli di stato sono UTF-8. Se la console non li accetta si prosegue in ASCII invece
        // di lanciare: una plancia che non parte perche' non sa disegnare un pallino e' assurda.
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { Ascii = true; }
        if (Console.IsOutputRedirected) Colors = false;
    }

    private static bool Ascii;

    // --- la finestra che non deve esserci ----------------------------------------------------

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] elenco, uint quanti);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr finestra, int comando);

    private const int SwHide = 0;

    /// <summary>
    /// Nasconde la propria finestra di console.
    ///
    /// E' il punto in cui questa unificazione mantiene la sua promessa. Un'applicazione console
    /// avviata dal Task Scheduler o dalla cartella Esecuzione automatica riceve una console
    /// dall'HOST, e non c'e' nessun flag di avvio che la sopprima: l'unico modo e' che il processo
    /// la nasconda da se', appena parte. Il lampo che resta dura qualche millisecondo, UNA volta al
    /// logon — al posto di una finestra PowerShell ogni cinque minuti, per sempre.
    ///
    /// Si nasconde SOLO una console propria — cioe' una a cui non e' attaccato nessun altro
    /// processo. La distinzione e' l'intera differenza fra utile e disastroso: lanciato a mano da
    /// una finestra cmd, il processo EREDITA quella console, e nasconderla farebbe sparire la
    /// finestra dell'utente con tutto il suo scrollback, recuperabile solo da Gestione attivita'.
    ///
    /// Falso se non c'era una console propria da nascondere: non e' un errore, e non deve impedire
    /// al supervisore di partire.
    /// </summary>
    public static bool HideConsoleWindow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var finestra = GetConsoleWindow();
            if (finestra == IntPtr.Zero) return false;

            // GetConsoleProcessList restituisce QUANTI processi sono attaccati alla console: 1
            // significa «solo io», cioe' me l'ha creata l'host (Task Scheduler, `start`) e posso
            // farne quel che voglio.
            var elenco = new uint[8];
            var quanti = GetConsoleProcessList(elenco, (uint)elenco.Length);
            if (quanti != 1) return false;

            return ShowWindow(finestra, SwHide);
        }
        catch { return false; }
    }

    public static string Glyph(Level l) => (Ascii, l) switch
    {
        (true, Level.Ok) => "ok",
        (true, Level.Warn) => "!!",
        (true, Level.Down) => "XX",
        (true, _) => "--",
        (false, Level.Ok) => "●",
        (false, Level.Warn) => "▲",
        (false, Level.Down) => "✖",
        (false, _) => "·",
    };

    public static ConsoleColor Color(Level l) => l switch
    {
        Level.Ok => ConsoleColor.Green,
        Level.Warn => ConsoleColor.Yellow,
        Level.Down => ConsoleColor.Red,
        _ => ConsoleColor.DarkGray,
    };

    // --- stampa semplice ------------------------------------------------------------------------

    public static void Write(string text, ConsoleColor color)
    {
        if (!Colors) { Console.Write(text); return; }
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = old;
    }

    public static void Line(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        Write(text, color);
        Console.WriteLine();
    }

    public static void Title(string text) => Line($"\n{text}", ConsoleColor.Cyan);
    public static void Error(string text) => Line($"  ✖ {text}", ConsoleColor.Red);
    public static void Warn(string text) => Line($"  ▲ {text}", ConsoleColor.Yellow);
    public static void Good(string text) => Line($"  ● {text}", ConsoleColor.Green);
    public static void Info(string text) => Line($"    {text}", ConsoleColor.DarkGray);

    // --- rendering del quadro -------------------------------------------------------------------

    /// <summary>
    /// Trasforma una rilevazione in righe di schermo. Una sola funzione per il comando `stato` e
    /// per la plancia interattiva: cosi' le due viste non possono divergere.
    /// </summary>
    public static List<ScreenLine> Render(Snapshot s, bool conRimedi)
    {
        var righe = new List<ScreenLine>();
        var intestazione = $"ProcioneMGR — plancia    {s.Taken:HH:mm:ss}    assetto: {Describe(s.Layout)}";
        righe.Add(new ScreenLine("", ConsoleColor.Gray, intestazione, ConsoleColor.Cyan));
        righe.Add(ScreenLine.Plain(new string('─', Math.Min(78, Math.Max(40, intestazione.Length + 2))), ConsoleColor.DarkGray));

        string? gruppo = null;
        var largh = s.Checks.Count == 0 ? 10 : s.Checks.Max(c => c.Name.Length);
        foreach (var c in s.Checks)
        {
            if (c.Group != gruppo)
            {
                gruppo = c.Group;
                righe.Add(ScreenLine.Plain(""));
                righe.Add(ScreenLine.Plain("  " + gruppo.ToUpperInvariant(), ConsoleColor.DarkCyan));
            }
            righe.Add(new ScreenLine(
                "  " + Glyph(c.Level) + " ", Color(c.Level),
                c.Name.PadRight(largh) + "  " + c.Detail,
                c.Level == Level.NotApplicable ? ConsoleColor.DarkGray : ConsoleColor.Gray));

            if (conRimedi && c.Fix is { Length: > 0 } && c.Level is Level.Warn or Level.Down)
                righe.Add(ScreenLine.Plain(new string(' ', largh + 6) + "→ " + c.Fix, ConsoleColor.DarkYellow));
        }

        righe.Add(ScreenLine.Plain(""));
        var ko = s.Count(Level.Down);
        var wa = s.Count(Level.Warn);
        var ok = s.Count(Level.Ok);
        var riassunto = ko > 0
            ? $"  {ko} guasti, {wa} avvisi, {ok} in ordine."
            : wa > 0 ? $"  nessun guasto, {wa} avvisi, {ok} in ordine."
                     : $"  tutto in ordine ({ok} controlli).";
        righe.Add(ScreenLine.Plain(riassunto, ko > 0 ? ConsoleColor.Red : wa > 0 ? ConsoleColor.Yellow : ConsoleColor.Green));
        return righe;
    }

    public static string Describe(Layout l) => l switch
    {
        Layout.Kind => "kind (cluster locale)",
        Layout.Compose => "Docker Compose",
        Layout.Both => "kind + Compose INSIEME",
        Layout.None => "nessuno (piattaforma spenta)",
        _ => "sconosciuto",
    };

    public static void Print(IEnumerable<ScreenLine> righe)
    {
        foreach (var r in righe)
        {
            if (r.Glyph.Length > 0) Write(r.Glyph, r.GlyphColor);
            Line(r.Text, r.TextColor);
        }
    }

    // --- pittura a schermo fisso (plancia interattiva) --------------------------------------------

    /// <summary>
    /// Ridisegna dall'origine senza <c>Console.Clear()</c>: pulire e riscrivere fa sfarfallare.
    /// Ogni riga viene riempita di spazi fino a bordo schermo cosi' la cornice precedente sparisce.
    /// </summary>
    public static void Paint(IReadOnlyList<ScreenLine> righe, ref int dipinteInPrecedenza)
    {
        int larghezza;
        try { larghezza = Math.Max(20, Console.WindowWidth - 1); } catch { larghezza = 100; }

        var dipinte = 0;
        for (var i = 0; i < righe.Count; i++)
        {
            var r = righe[i];
            // Finestra piu' corta del quadro: SetCursorPosition lancia. Si smette di disegnare
            // invece di lasciar scorrere lo schermo, e si annota quante righe si e' arrivati a
            // dipingere davvero — altrimenti la pulizia del giro dopo partirebbe dal posto
            // sbagliato e lascerebbe residui in mezzo al quadro.
            try { Console.SetCursorPosition(0, i); } catch { break; }

            var testo = r.Glyph + r.Text;
            if (testo.Length > larghezza) testo = testo[..larghezza];
            var lungGlifo = Math.Min(r.Glyph.Length, testo.Length);
            if (lungGlifo > 0)
            {
                Write(testo[..lungGlifo], r.GlyphColor);
                Write(testo[lungGlifo..].PadRight(larghezza - lungGlifo), r.TextColor);
            }
            else
            {
                Write(testo.PadRight(larghezza), r.TextColor);
            }
            dipinte++;
        }

        // Cancella le righe rimaste dal disegno precedente (il quadro puo' accorciarsi).
        for (var i = dipinte; i < dipinteInPrecedenza; i++)
        {
            try { Console.SetCursorPosition(0, i); Console.Write(new string(' ', larghezza)); } catch { break; }
        }
        dipinteInPrecedenza = dipinte;
    }

    // --- interazione --------------------------------------------------------------------------

    /// <summary>
    /// Conferma esplicita per le azioni che si sentono nel mondo reale (riavviare il motore,
    /// distruggere il cluster). Il default e' NO: un INVIO distratto non deve mai bastare.
    /// </summary>
    public static bool Confirm(string domanda)
    {
        if (Console.IsInputRedirected)
        {
            Ui.Error("serve una conferma ma lo standard input non e' una console: rilancia con --si.");
            return false;
        }
        Write($"  {domanda} [s/N] ", ConsoleColor.Yellow);
        var risposta = Console.ReadLine();
        return risposta is not null && risposta.Trim().StartsWith('s');
    }

    /// <summary>Conferma piu' forte: si deve digitare esattamente <paramref name="parola"/>.</summary>
    public static bool ConfirmWord(string domanda, string parola)
    {
        if (Console.IsInputRedirected) { Ui.Error("serve una conferma digitata: esegui da una console interattiva."); return false; }
        Write($"  {domanda}\n  digita «{parola}» per procedere: ", ConsoleColor.Red);
        return string.Equals(Console.ReadLine()?.Trim(), parola, StringComparison.Ordinal);
    }

    // --- formattazione ---------------------------------------------------------------------------

    public static string Age(TimeSpan t) => t switch
    {
        { TotalDays: >= 1 } => $"{(int)t.TotalDays}g {t.Hours}h",
        { TotalHours: >= 1 } => $"{(int)t.TotalHours}h {t.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)t.TotalMinutes}m",
        _ => $"{Math.Max(0, (int)t.TotalSeconds)}s",
    };

    public static string Size(long byteCount) => byteCount switch
    {
        >= 1L << 30 => $"{byteCount / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{byteCount / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{byteCount / (double)(1L << 10):0.0} KiB",
        _ => $"{byteCount} B",
    };
}
