using System.Text.RegularExpressions;

namespace ProcioneMGR.Tests;

/// <summary>
/// CHI PUÒ APRIRE COSA, RESO VERIFICABILE (R21, 2026-09-06).
///
/// <para>La protezione delle pagine dipende <b>interamente</b> dall'attributo
/// <c>@attribute [Authorize]</c> scritto a mano su ogni <c>.razor</c> con una rotta: in
/// <c>Program.cs</c> non esiste nessuna <c>FallbackPolicy</c> (rilievo R4, ancora aperto). Una
/// pagina nuova che dimentichi l'attributo nasce <b>pubblica</b>, e una che lo metta <b>nudo</b>
/// nasce aperta a qualunque utente autenticato — in nessuno dei due casi protesta qualcuno:
/// né il compilatore, né la CI, né la revisione, che vede una riga in mezzo a trenta uguali.</para>
///
/// <para><b>Il difetto che ha motivato il guardiano.</b> Cinque pagine avevano <c>[Authorize]</c>
/// nudo. Su quattro era una scelta (sono le pagine del ruolo <c>User</c>); sulla quinta,
/// <c>/settings/exchanges</c>, no: quella pagina alimenta il pool di credenziali con cui il MOTORE
/// firma gli ordini, e <c>ExchangeCredentialReader.FindForTradingAsync</c> sceglie la riga per
/// <c>(exchange, testnet)</c> <b>senza guardare l'utente</b>. Il filtro per <c>UserId</c> c'era in
/// lettura e cancellazione — cioè nella vetrina — e non nel percorso di firma: un isolamento
/// apparente, la classe di difetto che questo progetto chiama «controlli che rassicurano a
/// prescindere dalla realtà». Con la registrazione aperta (R22) bastava crearsi un account.</para>
///
/// <para>Da qui in poi ogni pagina con rotta sta in uno di tre stati <b>dichiarati</b>: ha un
/// ruolo, oppure è nell'inventario delle autenticate-senza-ruolo con la sua ragione, oppure è
/// nell'inventario delle pubbliche con la sua ragione. Un quarto stato non esiste, e una voce
/// morta (pagina che nel frattempo ha preso un ruolo, o che non c'è più) fa fallire la suite:
/// un permesso che protegge qualcosa che non esiste più è peggio di nessun permesso, perché la
/// prossima pagina con quel nome lo eredita in silenzio.</para>
/// </summary>
public sealed class AutorizzazioneDellePagineTests
{
    /// <summary>
    /// Pagine deliberatamente PUBBLICHE (nessun <c>[Authorize]</c>), con la ragione.
    /// </summary>
    private static readonly Dictionary<string, string> PubblicheDichiarate = new(StringComparer.Ordinal)
    {
        ["Components/Pages/Home.razor"] =
            "vetrina: agli anonimi mostra solo presentazione e i due pulsanti di accesso; il ramo "
            + "autenticato è dentro <AuthorizeView>, quindi i dati non escono da lì.",
        ["Components/Pages/Error.razor"] =
            "pagina d'errore del framework: deve poter comparire anche quando l'autenticazione è "
            + "proprio ciò che ha fallito.",
        ["Components/Pages/NotFound.razor"] =
            "404: rispondere «non esiste» non rivela nulla, e richiedere il login per vederlo "
            + "trasformerebbe ogni refuso in un redirect al login.",
    };

    /// <summary>
    /// Pagine autenticate SENZA ruolo — aperte a qualunque account, ruolo <c>User</c> compreso —
    /// con la ragione di ciascuna. È l'inventario che la revisione del 2026-09-06 ha ridotto da
    /// cinque voci a tre: <c>Dashboard</c> è salita a Manager (scrive sulle serie condivise) e
    /// <c>ExchangeSettings</c> ad Admin (alimenta il pool di firma).
    /// </summary>
    private static readonly Dictionary<string, string> AutorizzateSenzaRuolo = new(StringComparer.Ordinal)
    {
        ["Components/Pages/Backtest.razor"] =
            "simula sul passato e salva le strategie dell'utente (filtrate per UserId): non tocca "
            + "corsie né dati condivisi. È il senso stesso del ruolo User.",
        ["Components/Pages/MarketAnalysis.razor"] =
            "analisi in sola lettura sulle serie già scaricate: calcola e mostra, non scrive.",
        ["Components/Pages/Strategies.razor"] =
            "archivio personale: elenco e cancellazione sono filtrati per UserId, qui il confine "
            + "per utente è reale e sta nella query, non solo nella vetrina.",
    };

    /// <summary>
    /// Le pagine su cui il ruolo è una <b>decisione</b> e non una convenzione: se qualcuno le
    /// riapre, il test deve dire perché non si fa.
    /// </summary>
    private static readonly Dictionary<string, (string[] Ruoli, string Perche)> RuoloDeciso = new(StringComparer.Ordinal)
    {
        ["Components/Pages/ExchangeSettings.razor"] = (["Admin"],
            "R21: il pool di credenziali è UNO per progetto e FindForTradingAsync non filtra per "
            + "utente — chi scrive qui decide con quali chiavi il motore firma, e la casella "
            + "Testnet è libera (non spuntarla salva una credenziale LIVE). Anche «Ri-cifra ora» "
            + "agisce su tutte le righe della tabella. Manager NON basta."),
        ["Components/Pages/Dashboard.razor"] = (["Admin", "Manager"],
            "R21: «scarica storico» chiama IngestHistoricalDataAsync, che scrive sulle serie OHLCV "
            + "condivise da tutte le corsie e consuma il rate-limit dell'exchange."),
    };

    // --- Strumenti ---------------------------------------------------------------------------

    private static readonly Regex DirettivaPage = new(@"^\s*@page\s+""", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AttributoAuthorize = new(
        @"@attribute\s*\[\s*(?:Microsoft\.AspNetCore\.Authorization\.)?Authorize\s*(?<args>\([^\]]*\))?\s*\]",
        RegexOptions.Compiled);

    private static readonly Regex RuoloAppRoles = new(@"AppRoles\.(?<r>[A-Za-z]+)", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static readonly Regex Rotta = new(@"@page\s+""(?<r>[^""]+)""", RegexOptions.Compiled);

    private sealed record Pagina(string Percorso, bool HaAuthorize, IReadOnlySet<string> Ruoli, IReadOnlyList<string> Rotte)
    {
        public bool ENuda => HaAuthorize && Ruoli.Count == 0;
        public bool EPubblica => !HaAuthorize;
    }

    /// <summary>Classifica il markup di una pagina. Pura: la stessa funzione la esercitano i test di rumore.</summary>
    private static Pagina Classifica(string percorso, string markup)
    {
        var rotte = Rotta.Matches(markup).Select(x => x.Groups["r"].Value.TrimStart('/')).ToList();

        var m = AttributoAuthorize.Match(markup);
        if (!m.Success) return new Pagina(percorso, HaAuthorize: false, new HashSet<string>(StringComparer.Ordinal), rotte);

        var ruoli = RuoloAppRoles.Matches(m.Groups["args"].Value)
            .Select(x => x.Groups["r"].Value)
            .ToHashSet(StringComparer.Ordinal);
        return new Pagina(percorso, HaAuthorize: true, ruoli, rotte);
    }

    /// <summary>
    /// Le pagine con rotta sotto <c>Components/Pages</c>. Fuori resta <c>Components/Account/Pages</c>:
    /// è il flusso di Identity (login, password dimenticata, conferma email), anonimo per
    /// costruzione — la sua parte protetta è <c>Manage/</c>, che ha un test suo qui sotto.
    /// </summary>
    private static IReadOnlyList<Pagina> PagineConRotta()
    {
        var root = Path.Combine(RepoRoot(), "ProcioneMGR");
        var dir = Path.Combine(root, "Components", "Pages");

        var pagine = new List<Pagina>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            // File.ReadAllText toglie il BOM: metà di questi file ce l'ha (arrivano dal template),
            // e con il BOM davanti un "^@page" non aggancerebbe — la pagina sparirebbe dal
            // censimento invece di far fallire il test. Un guardiano che non vede è peggio di niente.
            var markup = File.ReadAllText(file);
            if (!DirettivaPage.IsMatch(markup)) continue;

            var percorso = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            pagine.Add(Classifica(percorso, markup));
        }
        return pagine;
    }

    // --- Test --------------------------------------------------------------------------------

    /// <summary>Il censimento deve vedere la UI vera: se ne trova quattro, sta guardando altrove.</summary>
    [Fact]
    public void IlCensimento_VedeLePagineVere()
    {
        Assert.True(PagineConRotta().Count > 25,
            $"solo {PagineConRotta().Count} pagine con rotta trovate: il guardiano non sta guardando la UI");
    }

    [Fact]
    public void OgniPaginaConRotta_HaUnRuolo_OStaInUnInventarioConLaSuaRagione()
    {
        var scoperte = new List<string>();

        foreach (var p in PagineConRotta())
        {
            if (p.EPubblica && !PubblicheDichiarate.ContainsKey(p.Percorso))
            {
                scoperte.Add($"{p.Percorso}: NESSUN [Authorize] — la pagina è pubblica");
            }
            else if (p.ENuda && !AutorizzateSenzaRuolo.ContainsKey(p.Percorso))
            {
                scoperte.Add($"{p.Percorso}: [Authorize] NUDO — aperta a qualunque utente autenticato, ruolo User compreso");
            }
        }

        Assert.True(scoperte.Count == 0,
            $"{scoperte.Count} pagine senza una decisione dichiarata sull'accesso:\n  "
            + string.Join("\n  ", scoperte)
            + "\n\nDa' loro un ruolo — [Authorize(Roles = AppRoles.Admin + \",\" + AppRoles.Manager)] — "
            + "oppure aggiungile all'inventario giusto qui sopra SCRIVENDO PERCHÉ. La domanda da farsi "
            + "è quella di R21: questa pagina scrive su qualcosa di condiviso, o alimenta il percorso "
            + "con cui il motore firma gli ordini?");
    }

    [Fact]
    public void LInventarioDelleAutorizzateSenzaRuolo_NonHaVociMorte()
    {
        var pagine = PagineConRotta().ToDictionary(p => p.Percorso, StringComparer.Ordinal);
        var morte = new List<string>();

        foreach (var (percorso, _) in AutorizzateSenzaRuolo)
        {
            if (!pagine.TryGetValue(percorso, out var p)) { morte.Add($"{percorso} (pagina inesistente o senza rotta)"); continue; }
            if (!p.ENuda) morte.Add($"{percorso} (ora ha un ruolo: {string.Join('+', p.Ruoli)} — togliela dall'inventario)");
        }

        Assert.True(morte.Count == 0,
            "Voci morte fra le «autorizzate senza ruolo»: " + string.Join("; ", morte)
            + ". Una voce che non corrisponde più alla realtà fa credere decisa una cosa che nessuno ha deciso.");
    }

    [Fact]
    public void LInventarioDellePubbliche_NonHaVociMorte()
    {
        var pagine = PagineConRotta().ToDictionary(p => p.Percorso, StringComparer.Ordinal);
        var morte = new List<string>();

        foreach (var (percorso, _) in PubblicheDichiarate)
        {
            if (!pagine.TryGetValue(percorso, out var p)) { morte.Add($"{percorso} (pagina inesistente o senza rotta)"); continue; }
            if (!p.EPubblica) morte.Add($"{percorso} (ora è protetta — togliela dall'inventario)");
        }

        Assert.True(morte.Count == 0, "Voci morte fra le pubbliche dichiarate: " + string.Join("; ", morte));
    }

    /// <summary>
    /// [R21] Il chiodo: <c>/settings/exchanges</c> è Admin e SOLO Admin, e la ragione è scritta
    /// accanto al codice sia nella pagina sia in <c>ExchangeCredentialReader</c>.
    /// </summary>
    [Fact]
    public void LePagineConRuoloDeciso_LoHannoAncora()
    {
        var pagine = PagineConRotta().ToDictionary(p => p.Percorso, StringComparer.Ordinal);
        var rotte = new List<string>();

        foreach (var (percorso, atteso) in RuoloDeciso)
        {
            if (!pagine.TryGetValue(percorso, out var p)) { rotte.Add($"{percorso}: pagina assente"); continue; }

            var attesi = atteso.Ruoli.ToHashSet(StringComparer.Ordinal);
            if (!p.Ruoli.SetEquals(attesi))
            {
                rotte.Add($"{percorso}: attesi [{string.Join(',', attesi.OrderBy(x => x, StringComparer.Ordinal))}], "
                          + $"trovati [{string.Join(',', p.Ruoli.OrderBy(x => x, StringComparer.Ordinal))}] — {atteso.Perche}");
            }
        }

        Assert.True(rotte.Count == 0, "Ruoli decisi e poi cambiati senza rivedere la decisione:\n  " + string.Join("\n  ", rotte));
    }

    /// <summary>Ogni pagina sotto <c>Pages/Admin/</c> richiede almeno il ruolo Admin: è la cartella che lo promette.</summary>
    [Fact]
    public void LePagineDellaCartellaAdmin_RichiedonoAdmin()
    {
        var fuori = PagineConRotta()
            .Where(p => p.Percorso.StartsWith("Components/Pages/Admin/", StringComparison.Ordinal))
            .Where(p => !p.Ruoli.Contains("Admin"))
            .Select(p => p.Percorso)
            .ToList();

        Assert.True(fuori.Count == 0, "Pagine in Pages/Admin senza il ruolo Admin: " + string.Join(", ", fuori));
    }

    /// <summary>
    /// La gestione dell'account (password, 2FA, dati personali) resta autenticata: la protegge un
    /// <c>_Imports.razor</c> di cartella, che è facile da perdere in un merge senza accorgersene.
    /// </summary>
    [Fact]
    public void LaGestioneDellAccount_RestaDietroAutenticazione()
    {
        var file = Path.Combine(RepoRoot(), "ProcioneMGR", "Components", "Account", "Pages", "Manage", "_Imports.razor");
        Assert.True(File.Exists(file), "manca Components/Account/Pages/Manage/_Imports.razor");
        Assert.Matches(AttributoAuthorize, File.ReadAllText(file));
    }

    /// <summary>
    /// [R22] Le due sole porte che creano un account passano dal cancello della registrazione.
    /// Il controllo è sul sorgente e non a rendering perché <c>Register.razor</c> inietta
    /// <c>UserManager</c> e <c>SignInManager</c>, classi concrete che bUnit non sostituisce: la
    /// tabella di verità la esercita <see cref="RegistrazioneChiusaTests"/> sulla funzione pura.
    /// </summary>
    [Fact]
    public void LeDuePorteCheCreanoUnAccount_PassanoDalCancello()
    {
        var root = Path.Combine(RepoRoot(), "ProcioneMGR", "Components", "Account", "Pages");
        foreach (var nome in new[] { "Register.razor", "ExternalLogin.razor" })
        {
            var markup = File.ReadAllText(Path.Combine(root, nome));
            Assert.Contains("IRegistrationGate", markup, StringComparison.Ordinal);
            Assert.Contains("RegistrationGate.EvaluateAsync()", markup, StringComparison.Ordinal);
            Assert.Contains("UserManager.CreateAsync", markup, StringComparison.Ordinal); // la porta è ancora lì
        }
    }

    /// <summary>
    /// [R21] IL MENÙ DICE LA VERITÀ SU CHI PUÒ ENTRARE. <c>NavModel</c> tiene i ruoli di ogni voce
    /// in una lista <b>parallela</b> agli attributi delle pagine — «rispecchia 1:1 il gating
    /// AuthorizeView», dice il suo commento — e due liste parallele divergono al primo cambio.
    ///
    /// <para>È successo esattamente qui: alzando <c>/settings/exchanges</c> ad Admin e
    /// <c>/dashboard</c> a Manager, le voci di menù sarebbero rimaste visibili a tutti, portando
    /// un utente <c>User</c> dritto a un rifiuto. Un collegamento che invita dove non si può
    /// entrare è la stessa bugia di un valore vecchio mostrato come attuale.</para>
    /// </summary>
    [Fact]
    public void IlMenu_HaGliStessiRuoliDellePagine()
    {
        var perRotta = new Dictionary<string, Pagina>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in PagineConRotta())
        {
            foreach (var r in p.Rotte) perRotta[r] = p;
        }

        var divergenze = new List<string>();
        foreach (var sezione in ProcioneMGR.Components.Layout.NavModel.Sections)
        {
            foreach (var voce in sezione.Items)
            {
                // Le voci che non puntano a una pagina di questo censimento (link esterni, aree
                // Identity) non hanno un attributo con cui confrontarsi.
                if (!perRotta.TryGetValue(voce.Href, out var pagina)) continue;

                var nelMenu = (voce.Roles ?? []).ToHashSet(StringComparer.Ordinal);
                if (!nelMenu.SetEquals(pagina.Ruoli))
                {
                    divergenze.Add(
                        $"«{voce.Label}» → /{voce.Href}: menù [{string.Join(',', nelMenu.OrderBy(x => x, StringComparer.Ordinal))}] "
                        + $"≠ pagina [{string.Join(',', pagina.Ruoli.OrderBy(x => x, StringComparer.Ordinal))}] ({pagina.Percorso})");
                }
            }
        }

        Assert.True(divergenze.Count == 0,
            "Il menù promette un accesso diverso da quello che la pagina concede:\n  " + string.Join("\n  ", divergenze));
    }

    // --- Rumore (livello 2): lo strumento distingue davvero i tre stati ------------------------

    /// <summary>
    /// Se il classificatore smettesse di distinguere «nudo» da «con ruolo», tutti i test qui sopra
    /// continuerebbero a passare — verdi e ciechi. Qui lo si esercita su markup sintetico, incluse
    /// le tre forme in cui il repo scrive i ruoli e un commento che NOMINA l'attributo senza esserlo.
    /// </summary>
    [Theory]
    [InlineData("@page \"/x\"\n", false, "")]                                                     // pubblica
    [InlineData("@page \"/x\"\n@attribute [Authorize]\n", true, "")]                              // nuda
    [InlineData("@page \"/x\"\n@attribute [Authorize(Roles = AppRoles.Admin)]\n", true, "Admin")] // un ruolo
    [InlineData("@page \"/x\"\n@attribute [Authorize(Roles = AppRoles.Admin + \",\" + AppRoles.Manager)]\n", true, "Admin,Manager")]
    [InlineData("@page \"/x\"\n@attribute [Authorize(Roles = $\"{AppRoles.Admin},{AppRoles.Manager}\")]\n", true, "Admin,Manager")]
    [InlineData("@page \"/x\"\n@* con [Authorize] nudo era aperta *@\n@attribute [Authorize(Roles = AppRoles.Admin)]\n", true, "Admin")]
    public void IlClassificatore_DistingueLeTreForme(string markup, bool haAuthorize, string ruoliAttesi)
    {
        var p = Classifica("x.razor", markup);

        Assert.Equal(haAuthorize, p.HaAuthorize);
        Assert.Equal(
            ruoliAttesi.Length == 0 ? Array.Empty<string>() : ruoliAttesi.Split(','),
            p.Ruoli.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(!haAuthorize, p.EPubblica);
        Assert.Equal(haAuthorize && ruoliAttesi.Length == 0, p.ENuda);
    }
}
