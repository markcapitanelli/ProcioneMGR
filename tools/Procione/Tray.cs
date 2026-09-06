using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Procione;

/// <summary>
/// L'icona nell'area di notifica: la plancia che c'e' anche quando non la stai guardando.
///
/// PERCHE' ESISTE (2026-09-06, richiesta del proprietario: «deve essere sempre visibile e
/// gestibile»). Il supervisore residente e' headless per costruzione — l'output dei lavori e'
/// catturato apposta, perche' fino al 2026-08-23 le automazioni aprivano una finestra PowerShell
/// davanti all'utente 288 volte al giorno. Quella scelta era giusta e resta, ma ha un prezzo che
/// si e' visto col tempo: l'amministratore non ha piu' NESSUNA presenza permanente. Per sapere se
/// il server e' in piedi deve ricordarsi di aprire una console.
///
/// Questa icona chiude quel buco senza riaprire il vecchio: sta vicino all'orologio, cambia colore
/// col verdetto peggiore, e apre un menu con i comandi. Non ruba mai il fuoco e non fa nascere
/// finestre da sola — le finestre le apre solo quando sei TU a scegliere una voce, che e' l'esatto
/// contrario di quello che dava fastidio.
///
/// NIENTE WinForms, di proposito. Aggiungere <c>UseWindowsForms</c> porterebbe il progetto a
/// <c>net10.0-windows</c>, e la suite di test che ne prova la logica pura e' <c>net10.0</c>: la
/// plancia diventerebbe non provabile per un'icona. Qui si usano le stesse P/Invoke che la plancia
/// gia' usa per <c>--muto</c>, e il target framework non cambia.
/// </summary>
internal sealed class Tray : IDisposable
{
    // =============================================================================================
    //  Win32
    // =============================================================================================

    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmCommand = 0x0111;
    private const int WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;

    /// Messaggio che l'area di notifica ci rimanda quando l'utente tocca l'icona.
    private const int WmIcona = 0x0400 + 1;    // WM_APP + 1

    /// Messaggio che ci mandiamo da soli per aggiornare l'icona dal thread giusto.
    private const int WmAggiorna = 0x0400 + 2; // WM_APP + 2

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;

    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private const int MfString = 0x00000000;
    private const int MfSeparator = 0x00000800;
    private const int MfGrayed = 0x00000001;
    private const int MfPopup = 0x00000010;

    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    [StructLayout(LayoutKind.Sequential)]
    private struct Punto { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Messaggio
    {
        public IntPtr Hwnd;
        public uint Codice;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Tempo;
        public Punto Punto;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ClasseFinestra
    {
        public uint CbSize;
        public uint Stile;
        public IntPtr Proc;
        public int ExtraClasse;
        public int ExtraFinestra;
        public IntPtr Istanza;
        public IntPtr Icona;
        public IntPtr Cursore;
        public IntPtr Sfondo;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)] public string Nome;
        public IntPtr IconaPiccola;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DatiIcona
    {
        public int CbSize;
        public IntPtr Finestra;
        public uint Id;
        public uint Flag;
        public uint Callback;
        public IntPtr Icona;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint Stato;
        public uint MascheraStato;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOVersione;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string TitoloInfo;
        public uint FlagInfo;
        public Guid Guid;
        public IntPtr IconaFumetto;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref ClasseFinestra classe);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStile, string classe, string nome, int stile,
                                                 int x, int y, int larghezza, int altezza,
                                                 IntPtr genitore, IntPtr menu, IntPtr istanza, IntPtr param);

    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Messaggio msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Messaggio msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Messaggio msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int codice);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Punto punto);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, int flag, IntPtr id, string? testo);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flag, int x, int y, IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int indice);
    [DllImport("user32.dll")]
    private static extern IntPtr CreateIcon(IntPtr istanza, int larghezza, int altezza,
                                            byte piani, byte bitPerPixel, byte[] maschera, byte[] colore);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icona);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string nome);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int messaggio, ref DatiIcona dati);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? nome);

    // =============================================================================================
    //  Stato
    // =============================================================================================

    private const string NomeClasse = "ProcioneMGRPlanciaTray";

    /// Il delegato DEVE restare vivo quanto la finestra: passarlo a RegisterClassEx non crea una
    /// radice per il GC, e una volta raccolto la prima callback di Windows salterebbe nel vuoto.
    private readonly WndProc _proc;

    private readonly Thread _thread;
    private readonly Func<Snapshot?> _quadro;
    private readonly Action _fermaSupervisore;
    private readonly uint _taskbarCreata;
    private readonly ManualResetEventSlim _pronto = new(false);

    private IntPtr _finestra;
    private IntPtr _icona;
    private Level _livello = Level.NotApplicable;
    private Level? _livelloAnnunciato;
    private string _tip = "ProcioneMGR — rilevazione in corso";
    private string? _fumettoTitolo;
    private string? _fumettoTesto;
    private volatile bool _chiuso;

    private Tray(Func<Snapshot?> quadro, Action fermaSupervisore)
    {
        _quadro = quadro;
        _fermaSupervisore = fermaSupervisore;
        _proc = Procedura;
        _taskbarCreata = RegisterWindowMessageW("TaskbarCreated");
        _thread = new Thread(Ciclo) { IsBackground = true, Name = "plancia-tray" };
        // STA e' la scelta convenzionale per un thread che possiede una finestra e apre menu della
        // shell. La guardia e' per l'analizzatore, che non vede il controllo gia' fatto in Start.
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>
    /// Accende l'icona. <c>null</c> se non e' possibile — sessione senza desktop, area di notifica
    /// assente: non e' un errore che debba impedire al supervisore di vegliare, che resta il suo
    /// lavoro vero.
    /// </summary>
    public static Tray? Start(Func<Snapshot?> quadro, Action fermaSupervisore)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var tray = new Tray(quadro, fermaSupervisore);
            tray._thread.Start();
            // Si aspetta che la finestra esista davvero prima di restituirla: chi la riceve la usa
            // subito per aggiornare l'icona, e un handle nullo la farebbe fallire in silenzio.
            return tray._pronto.Wait(TimeSpan.FromSeconds(5)) && tray._finestra != IntPtr.Zero ? tray : null;
        }
        catch { return null; }
    }

    // =============================================================================================
    //  Ciclo dei messaggi
    // =============================================================================================

    private void Ciclo()
    {
        try
        {
            var istanza = GetModuleHandleW(null);
            var classe = new ClasseFinestra
            {
                CbSize = (uint)Marshal.SizeOf<ClasseFinestra>(),
                Proc = Marshal.GetFunctionPointerForDelegate(_proc),
                Istanza = istanza,
                Nome = NomeClasse,
            };
            // Se la classe c'e' gia' (un'altra istanza nello stesso processo) RegisterClassEx
            // fallisce, e va bene: serve solo che esista.
            RegisterClassExW(ref classe);

            // Finestra vera ma mai mostrata. NON message-only (HWND_MESSAGE): un menu contestuale
            // ha bisogno di una finestra che possa diventare quella in primo piano, altrimenti
            // resta aperto anche dopo un clic altrove — il difetto classico dei menu da tray.
            _finestra = CreateWindowExW(0, NomeClasse, "ProcioneMGR", 0, 0, 0, 0, 0,
                                        IntPtr.Zero, IntPtr.Zero, istanza, IntPtr.Zero);
            // Si segnala «pronto» appena la finestra esiste, PRIMA di attaccare l'icona: attaccarla
            // puo' richiedere fino a dieci secondi (al logon l'area di notifica non c'e' ancora),
            // e far aspettare tanto chi ci ha creati lo porterebbe a concludere che l'icona non si
            // puo' avere — proprio nel caso in cui basta avere pazienza.
            _pronto.Set();
            if (_finestra == IntPtr.Zero) return;

            Attaccata = AggiungiIcona();

            while (!_chiuso)
            {
                var esito = GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (esito <= 0) break;          // 0 = WM_QUIT, -1 = errore
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch { /* l'icona e' un di piu': non deve poter far cadere il supervisore */ }
        finally
        {
            // try/catch anche qui, e non e' zelo: se Dispose ha gia' liberato il semaforo — succede
            // quando la Join scade e il thread e' ancora vivo — questa Set lancerebbe
            // ObjectDisposedException su un thread di background, cioe' TERMINEREBBE IL PROCESSO.
            // Il supervisore morirebbe per colpa del suo indicatore.
            try { _pronto.Set(); } catch { }
            TogliIcona();
        }
    }

    private IntPtr Procedura(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // Explorer e' ripartito (crash, o aggiornamento): l'area di notifica e' nuova e la
            // nostra icona non c'e' piu'. Senza questo, «sempre visibile» durerebbe fino al primo
            // riavvio della shell — e nessuno collegherebbe le due cose.
            if (msg == _taskbarCreata) { Attaccata = AggiungiIcona(); return IntPtr.Zero; }

            switch (msg)
            {
                case WmIcona:
                    var evento = (int)(lParam.ToInt64() & 0xFFFF);
                    if (evento is WmRButtonUp or WmLButtonUp) ApriMenu();
                    else if (evento == WmLButtonDblClk) Lancia("");
                    return IntPtr.Zero;

                case WmAggiorna:
                    ScriviIcona(NimModify);
                    return IntPtr.Zero;

                case WmCommand:
                    Comanda((int)(wParam.ToInt64() & 0xFFFF));
                    return IntPtr.Zero;

                case WmClose:
                case WmDestroy:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch { /* mai lasciar uscire un'eccezione verso Windows */ }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // =============================================================================================
    //  L'icona
    // =============================================================================================

    /// <summary>
    /// Vero se la shell ha ACCETTATO l'icona. Non e' cosmetica: e' l'unica prova che «sempre
    /// visibile» sia vero, e va poter essere letta nel log invece che dedotta dall'assenza di
    /// errori.
    /// </summary>
    public bool Attaccata { get; private set; }

    /// <summary>Vero se il pallino e' stato davvero disegnato (<c>CreateIcon</c> ha reso un handle).</summary>
    public bool DisegnoRiuscito { get; private set; }

    private bool AggiungiIcona()
    {
        // Al logon l'attivita' pianificata puo' partire PRIMA che Explorer abbia creato l'area di
        // notifica: NIM_ADD fallisce, e senza ritentare l'icona non comparirebbe fino al riavvio.
        for (var i = 0; i < 10; i++)
        {
            if (ScriviIcona(NimAdd)) { Attaccata = true; return true; }
            Thread.Sleep(1000);
        }
        return false;
    }

    private bool ScriviIcona(int operazione)
    {
        if (_finestra == IntPtr.Zero) return false;

        var nuova = CreaIcona(_livello);
        // Un handle nullo si dichiara: senza, l'icona finirebbe nell'area di notifica SENZA
        // immagine — visibile ma muta sul verdetto, cioe' meta' dello scopo — e la diagnosi direbbe
        // «attiva». Si prosegue lo stesso (menu e descrizione servono comunque), ma lo si sa.
        DisegnoRiuscito = nuova != IntPtr.Zero;

        var dati = new DatiIcona
        {
            CbSize = Marshal.SizeOf<DatiIcona>(),
            Finestra = _finestra,
            Id = 1,
            Flag = NifMessage | NifTip | (DisegnoRiuscito ? NifIcon : 0u),
            Callback = WmIcona,
            Icona = nuova,
            Tip = Taglia(_tip, 127),
            Info = "",
            TitoloInfo = "",
        };

        string? titolo, testo;
        lock (this) { titolo = _fumettoTitolo; testo = _fumettoTesto; _fumettoTitolo = _fumettoTesto = null; }
        if (operazione == NimModify && testo is not null)
        {
            dati.Flag |= NifInfo;
            dati.Info = Taglia(testo, 255);
            dati.TitoloInfo = Taglia(titolo ?? "ProcioneMGR", 63);
            dati.FlagInfo = _livello == Level.Down ? 3u : 2u;   // NIIF_ERROR : NIIF_WARNING
        }

        var ok = Shell_NotifyIconW(operazione, ref dati);

        // L'icona VECCHIA si distrugge solo dopo che la nuova e' stata consegnata alla shell:
        // liberarla prima lascerebbe l'area di notifica con un handle non valido. Un handle per
        // aggiornamento che non venisse mai liberato sarebbe una perdita lenta ma sicura — questo
        // processo vive per settimane.
        if (ok)
        {
            if (_icona != IntPtr.Zero) DestroyIcon(_icona);
            _icona = nuova;
        }
        else if (nuova != IntPtr.Zero) DestroyIcon(nuova);

        return ok;
    }

    private readonly object _chiusura = new();

    private void TogliIcona()
    {
        // Puo' essere chiamata da due thread: dal ciclo che finisce e da Dispose se la Join scade.
        // Senza esclusione si arriverebbe a distruggere due volte gli stessi handle.
        lock (_chiusura)
        try
        {
            if (_finestra != IntPtr.Zero)
            {
                var dati = new DatiIcona { CbSize = Marshal.SizeOf<DatiIcona>(), Finestra = _finestra, Id = 1,
                                           Tip = "", Info = "", TitoloInfo = "" };
                Shell_NotifyIconW(NimDelete, ref dati);
                DestroyWindow(_finestra);
                _finestra = IntPtr.Zero;
            }
            if (_icona != IntPtr.Zero) { DestroyIcon(_icona); _icona = IntPtr.Zero; }
        }
        catch { }
    }

    /// <summary>
    /// Disegna un pallino pieno del colore del verdetto.
    ///
    /// Si costruisce a mano invece di caricare un file: la plancia non ha risorse incorporate, e
    /// aggiungerle per un cerchio significherebbe una pipeline di build in piu' per sempre. Il
    /// cerchio e' anche simmetrico rispetto all'orizzontale, il che rende irrilevante l'unica
    /// ambiguita' vera di <c>CreateIcon</c> — se le righe vadano dall'alto o dal basso.
    /// </summary>
    private static IntPtr CreaIcona(Level livello)
    {
        var lato = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var altezza = Math.Max(16, GetSystemMetrics(SmCySmIcon));
        lato = altezza = Math.Min(lato, altezza);

        var (b, g, r) = livello switch
        {
            Level.Ok => ((byte)0x3C, (byte)0xC4, (byte)0x4A),     // verde
            Level.Warn => ((byte)0x1E, (byte)0xB0, (byte)0xE8),   // ambra
            Level.Down => ((byte)0x3A, (byte)0x44, (byte)0xE0),   // rosso
            _ => ((byte)0x90, (byte)0x90, (byte)0x90),            // grigio: non misurato
        };

        var colore = new byte[lato * altezza * 4];
        // La maschera AND e' a 1 bit per pixel, con le righe allineate a 2 byte.
        var byteRiga = (lato + 15) / 16 * 2;
        var maschera = new byte[byteRiga * altezza];

        var centro = (lato - 1) / 2.0;
        var raggio = lato / 2.0 - 0.8;

        for (var y = 0; y < altezza; y++)
        {
            for (var x = 0; x < lato; x++)
            {
                var dx = x - centro;
                var dy = y - centro;
                var dentro = dx * dx + dy * dy <= raggio * raggio;
                var i = (y * lato + x) * 4;

                if (dentro)
                {
                    // Il bordo leggermente piu' scuro: su uno sfondo chiaro un pallino piatto
                    // sparisce, e un'icona che non si distingue non e' «sempre visibile».
                    var bordo = dx * dx + dy * dy > (raggio - 1.2) * (raggio - 1.2);
                    colore[i + 0] = bordo ? (byte)(b * 0.65) : b;
                    colore[i + 1] = bordo ? (byte)(g * 0.65) : g;
                    colore[i + 2] = bordo ? (byte)(r * 0.65) : r;
                    colore[i + 3] = 255;
                }
                else
                {
                    // Fuori dal cerchio: bit della maschera a 1 = pixel trasparente.
                    maschera[y * byteRiga + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        return CreateIcon(GetModuleHandleW(null), lato, altezza, 1, 32, maschera, colore);
    }

    private static string Taglia(string testo, int massimo) =>
        testo.Length <= massimo ? testo : testo[..massimo];

    // =============================================================================================
    //  Aggiornamento dall'esterno
    // =============================================================================================

    /// <summary>
    /// Porta nell'icona l'ultima rilevazione: colore, descrizione, e — solo sulle TRANSIZIONI — un
    /// fumetto.
    ///
    /// L'anti-spam e' la stessa disciplina di <c>watchdog.ps1</c>: si avvisa quando lo stato
    /// CAMBIA, mai a ripetizione. Un fumetto ogni minuto su un guasto che dura da un'ora e' il modo
    /// piu' rapido di far disattivare le notifiche, e con esse anche quelle che contano.
    /// </summary>
    public void Update(Snapshot? quadro)
    {
        if (_finestra == IntPtr.Zero) return;

        var livello = quadro?.Worst ?? Level.NotApplicable;
        _tip = quadro is null
            ? "ProcioneMGR — nessuna rilevazione"
            : $"ProcioneMGR — {Riassunto(quadro)}\n{quadro.Taken:HH:mm:ss}  ({Ui.Describe(quadro.Layout)})";

        if (quadro is not null && livello != _livelloAnnunciato)
        {
            // La decisione «annunciare o no» sta in Verdicts, dove si puo' provare contro il caso
            // che conta di piu': lo stato che NON cambia, e che deve restare muto.
            var fumetto = Verdicts.Fumetto(_livelloAnnunciato, livello);
            if (fumetto is not null)
            {
                var (titolo, dettagli) = fumetto.Value;
                Annuncia(titolo, dettagli
                    ? PrimeRighe(quadro, livello == Level.Down ? Level.Down : Level.Warn)
                    : "tutti i controlli sono tornati in ordine.");
            }
            _livelloAnnunciato = livello;
        }

        _livello = livello;
        PostMessageW(_finestra, WmAggiorna, IntPtr.Zero, IntPtr.Zero);
    }

    private void Annuncia(string titolo, string testo)
    {
        lock (this) { _fumettoTitolo = titolo; _fumettoTesto = testo; }
    }

    private static string Riassunto(Snapshot q)
    {
        var ko = q.Count(Level.Down);
        var wa = q.Count(Level.Warn);
        return ko > 0 ? $"{ko} guasti, {wa} avvisi" : wa > 0 ? $"{wa} avvisi" : "tutto in ordine";
    }

    private static string PrimeRighe(Snapshot q, Level livello)
    {
        var righe = q.Checks.Where(c => c.Level == livello).Take(3).Select(c => $"· {c.Name}: {c.Detail}");
        var testo = string.Join("\n", righe);
        return testo.Length == 0 ? "nessun dettaglio" : testo;
    }

    // =============================================================================================
    //  Il menu
    // =============================================================================================

    private const int CmdPlancia = 1001;
    private const int CmdStato = 1002;
    private const int CmdPorte = 1003;
    private const int CmdAvviaTutto = 1010;
    private const int CmdRipara = 1011;
    private const int CmdRiparaTunnel = 1012;
    private const int CmdRiparaProxy = 1013;
    private const int CmdGuscioSu = 1020;
    private const int CmdGuscioGiu = 1021;
    private const int CmdGuscioRiavvia = 1022;
    private const int CmdApriUi = 1030;
    private const int CmdLogSupervisore = 1031;
    private const int CmdDottore = 1032;
    private const int CmdEsci = 1090;
    private const int CmdLavoroBase = 1100;   // + indice del lavoro

    private void ApriMenu()
    {
        var quadro = _quadro();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            // La prima voce e' il verdetto, disabilitata: e' un'etichetta, non un comando. Cosi'
            // un clic destro risponde alla domanda «come sta?» senza aprire niente.
            AppendMenuW(menu, MfString | MfGrayed, 0,
                quadro is null ? "rilevazione non ancora fatta" : $"{Glifo(quadro.Worst)}  {Riassunto(quadro)}");
            AppendMenuW(menu, MfSeparator, 0, null);

            AppendMenuW(menu, MfString, CmdPlancia, "Apri la plancia\tdoppio clic");
            AppendMenuW(menu, MfString, CmdStato, "Stato completo");
            AppendMenuW(menu, MfString, CmdPorte, "Porte e chi le occupa");
            AppendMenuW(menu, MfSeparator, 0, null);

            var guscioSu = Net.ListeningPorts().Contains(Platform.ShellPort);
            AppendMenuW(menu, MfString, guscioSu ? CmdGuscioGiu : CmdGuscioSu,
                        guscioSu ? "Ferma il guscio" : "Avvia il guscio");
            if (guscioSu) AppendMenuW(menu, MfString, CmdGuscioRiavvia, "Riavvia il guscio");
            AppendMenuW(menu, MfString, CmdApriUi, "Apri la UI nel browser");
            AppendMenuW(menu, MfSeparator, 0, null);

            AppendMenuW(menu, MfString, CmdAvviaTutto, "Avvia tutto (bring-up)");
            AppendMenuW(menu, MfString, CmdRipara, "Ripara tutto");
            AppendMenuW(menu, MfString, CmdRiparaTunnel, "Ripara i tunnel");
            AppendMenuW(menu, MfString, CmdRiparaProxy, "Ripara il proxy dell'API");
            AppendMenuW(menu, MfSeparator, 0, null);

            var lavori = CreatePopupMenu();
            for (var i = 0; i < Jobs.All.Count; i++)
            {
                var job = Jobs.All[i];
                AppendMenuW(lavori, MfString, CmdLavoroBase + i, $"{job.Name} — esegui adesso");
            }
            AppendMenuW(menu, MfPopup, lavori, "Lavori del supervisore");
            AppendMenuW(menu, MfString, CmdLogSupervisore, "Log del supervisore");
            AppendMenuW(menu, MfString, CmdDottore, "Dottore (prerequisiti)");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CmdEsci, "Ferma il supervisore ed esci");

            GetCursorPos(out var punto);
            // SetForegroundWindow PRIMA: senza, il menu resta aperto anche dopo un clic altrove.
            // E' il difetto documentato da Microsoft (KB135788), e si vede subito.
            SetForegroundWindow(_finestra);
            var scelta = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, punto.X, punto.Y, _finestra, IntPtr.Zero);
            PostMessageW(_finestra, WmNull, IntPtr.Zero, IntPtr.Zero);

            // Il sottomenu NON si distrugge a parte: DestroyMenu sul padre distrugge anche i figli,
            // e liberarlo prima lascerebbe nel padre un handle gia' morto da ridistruggere.
            if (scelta != 0) Comanda(scelta);
        }
        finally { DestroyMenu(menu); }
    }

    private static string Glifo(Level l) => l switch
    {
        Level.Ok => "●",
        Level.Warn => "▲",
        Level.Down => "✖",
        _ => "·",
    };

    private void Comanda(int id)
    {
        if (id >= CmdLavoroBase && id < CmdLavoroBase + Jobs.All.Count)
        {
            Lancia($"lavoro {Jobs.All[id - CmdLavoroBase].Name} ora");
            return;
        }

        switch (id)
        {
            case CmdPlancia: Lancia(""); break;
            case CmdStato: Lancia("stato"); break;
            case CmdPorte: Lancia("porte"); break;
            case CmdAvviaTutto: Lancia("avvia"); break;
            case CmdRipara: Lancia("ripara"); break;
            case CmdRiparaTunnel: Lancia("ripara tunnel"); break;
            case CmdRiparaProxy: Lancia("ripara proxy"); break;
            case CmdGuscioSu: Lancia("avvia guscio"); break;
            case CmdGuscioGiu: Lancia("ferma guscio"); break;
            case CmdGuscioRiavvia: Lancia("riavvia guscio"); break;
            case CmdApriUi: Lancia("apri"); break;
            case CmdLogSupervisore: Lancia("log supervisore -n 200 -f"); break;
            case CmdDottore: Lancia("dottore"); break;
            case CmdEsci: _fermaSupervisore(); break;
        }
    }

    /// <summary>
    /// Esegue un comando della plancia in una finestra NUOVA, che resta aperta.
    ///
    /// Le finestre le apre solo l'utente scegliendo una voce — mai il programma da solo: e' la
    /// differenza fra questa icona e le attivita' pianificate che aprivano una console ogni cinque
    /// minuti. E resta aperta (<c>cmd /k</c>) perche' meta' di questi comandi serve proprio a
    /// LEGGERE quello che dicono; una finestra che si chiude da sola porterebbe via la risposta.
    /// </summary>
    private static void Lancia(string argomenti)
    {
        var exe = Platform.SelfExe;
        if (exe is null) return;
        try
        {
            // Le virgolette di cmd: `cmd /k ""percorso" argomenti"`. Le doppie esterne fanno si'
            // che cmd non spezzi il percorso sugli spazi di «Program Files» e simili.
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/k \"\"{exe}\" {argomenti}\"",
                UseShellExecute = true,
                WorkingDirectory = Platform.MainRepoRoot,
            });
        }
        catch { /* niente da fare: l'icona resta, il supervisore continua */ }
    }

    // =============================================================================================

    public void Dispose()
    {
        if (_chiuso) return;
        _chiuso = true;
        try
        {
            if (_finestra != IntPtr.Zero) PostMessageW(_finestra, WmClose, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        catch { }
        TogliIcona();
        _pronto.Dispose();
    }
}
