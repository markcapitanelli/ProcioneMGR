using ProcioneMGR.Data;

namespace ProcioneMGR.Components.Layout;

/// <summary>
/// Una singola voce di menu.
/// </summary>
/// <param name="Href">URL relativo, senza slash iniziale (es. "market/watchlist"). Stringa vuota = Home.</param>
/// <param name="Label">Etichetta mostrata in italiano.</param>
/// <param name="Icon">Classe Bootstrap Icons (es. "bi-house-door-fill").</param>
/// <param name="Roles">
/// Ruoli abilitati a vedere la voce. <c>null</c> = qualsiasi utente autenticato.
/// Rispecchia 1:1 il gating <c>AuthorizeView</c> della vecchia NavMenu.
/// </param>
/// <param name="Match">true = match esatto della route (usato solo per Home).</param>
public sealed record NavItem(
    string Href,
    string Label,
    string Icon,
    string[]? Roles = null,
    bool Match = false);

/// <summary>
/// Un blocco della sidebar (Fondamenta / Analisi / Specialistico / Account).
/// </summary>
/// <param name="Key">Chiave stabile usata per persistere lo stato aperto/chiuso in localStorage.</param>
/// <param name="Title">Titolo del blocco.</param>
/// <param name="Accent">Colore CSS del pallino (tono pastello sul tema scuro).</param>
/// <param name="Collapsible">true = il blocco può essere collassato dall'utente.</param>
/// <param name="Items">Voci contenute.</param>
public sealed record NavSection(
    string Key,
    string Title,
    string Accent,
    bool Collapsible,
    IReadOnlyList<NavItem> Items);

/// <summary>
/// Modello di navigazione centralizzato: unica fonte di verità condivisa da
/// <c>NavMenu</c> (rendering della sidebar) e <c>Breadcrumb</c> (percorso contestuale).
/// Riorganizza in 3 blocchi le vecchie voci piatte SENZA cambiare href né ruoli.
/// </summary>
public static class NavModel
{
    private static readonly string[] ManagerAndAdmin = [AppRoles.Manager, AppRoles.Admin];
    private static readonly string[] AdminOnly = [AppRoles.Admin];

    /// <summary>Blocchi operativi (Fondamenta + Analisi + Specialistico). Account è a parte.</summary>
    public static readonly IReadOnlyList<NavSection> Sections =
    [
        // 🟢 FONDAMENTA — percorso minimo obbligato, sempre aperto e non collassabile.
        new NavSection("fondamenta", "Fondamenta", "#4ade80", Collapsible: false,
        [
            new NavItem("", "Home", "bi-house-door-fill", Match: true),
            new NavItem("dashboard", "Dashboard", "bi-bar-chart-line-fill"),
            new NavItem("market-analysis", "Analisi serie", "bi-clipboard-data"),
            new NavItem("backtest", "Backtest", "bi-graph-up-arrow"),
            new NavItem("strategies", "Le mie strategie", "bi-collection-fill"),
            new NavItem("trading", "Trading", "bi-currency-exchange", ManagerAndAdmin),
            new NavItem("market/watchlist", "Watchlist", "bi-eye-fill", ManagerAndAdmin),
            new NavItem("settings/exchanges", "Credenziali Exchange", "bi-key-fill"),
        ]),

        // 🟡 ANALISI — strumenti per migliorare strategie esistenti, collassabile.
        new NavSection("analisi", "Analisi", "#fbbf24", Collapsible: true,
        [
            new NavItem("optimization", "Optimization", "bi-sliders", ManagerAndAdmin),
            new NavItem("ensemble", "Ensemble", "bi-diagram-3-fill", ManagerAndAdmin),
            new NavItem("ml", "ML Lab", "bi-cpu-fill", ManagerAndAdmin),
            new NavItem("feature-selection", "Selezione feature (IC)", "bi-funnel", ManagerAndAdmin),
            new NavItem("portfolio", "Portafoglio", "bi-pie-chart-fill", ManagerAndAdmin),
            new NavItem("registry", "Registry modelli", "bi-award", ManagerAndAdmin),
            new NavItem("experiments", "Esperimenti", "bi-journals", ManagerAndAdmin),
            new NavItem("regimes", "Regimes", "bi-grid-3x3-gap-fill", ManagerAndAdmin),
            new NavItem("metrics", "Metriche", "bi-speedometer2", ManagerAndAdmin),
        ]),

        // 🔴 SPECIALISTICO — strumenti di nicchia e amministrazione, collassabile.
        new NavSection("specialistico", "Specialistico", "#f87171", Collapsible: true,
        [
            new NavItem("discovery", "Discovery", "bi-search", ManagerAndAdmin),
            new NavItem("pipeline", "Pipeline", "bi-robot", ManagerAndAdmin),
            new NavItem("execution", "Execution Lab", "bi-stack", ManagerAndAdmin),
            new NavItem("alpha-mining", "Alpha Mining", "bi-diagram-2", ManagerAndAdmin),
            new NavItem("pairs-trading", "Pairs Trading", "bi-arrow-left-right", ManagerAndAdmin),
            new NavItem("market/bars", "Barre informative", "bi-bar-chart-steps", ManagerAndAdmin),
            new NavItem("volatility", "Volatilità", "bi-activity", ManagerAndAdmin),
            new NavItem("sentiment", "Sentiment", "bi-newspaper", ManagerAndAdmin),
            new NavItem("admin/ai-supervisor", "Supervisione AI", "bi-robot", ManagerAndAdmin),
            new NavItem("admin/autonomy", "Autonomia", "bi-toggles", AdminOnly),
            new NavItem("admin/users", "Gestione Utenti", "bi-people-fill", AdminOnly),
            new NavItem("admin/backup", "Backup Database", "bi-database-fill-down", AdminOnly),
        ]),
    ];

    /// <summary>Chiavi dei soli blocchi collassabili (usate per lo stato persistente).</summary>
    public static IEnumerable<string> CollapsibleKeys =>
        Sections.Where(s => s.Collapsible).Select(s => s.Key);

    // Lookup route (normalizzata) -> (titolo sezione, etichetta voce) per il breadcrumb.
    private static readonly Dictionary<string, (string Section, string Page)> Lookup =
        BuildLookup();

    private static Dictionary<string, (string, string)> BuildLookup()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in Sections)
        {
            foreach (var item in section.Items)
            {
                map[Normalize(item.Href)] = (section.Title, item.Label);
            }
        }
        return map;
    }

    /// <summary>
    /// Restituisce il breadcrumb (sezione, pagina) per una route relativa, oppure
    /// <c>null</c> se la route non è mappata (es. Home o pagine Account/Identity).
    /// </summary>
    public static (string Section, string Page)? Resolve(string relativePath)
    {
        var key = Normalize(relativePath);
        if (key.Length == 0) return null; // Home: nessun breadcrumb.
        return Lookup.TryGetValue(key, out var crumb) ? crumb : null;
    }

    /// <summary>Chiave del blocco che contiene la route, o <c>null</c>.</summary>
    public static string? SectionKeyOf(string relativePath)
    {
        var key = Normalize(relativePath);
        if (key.Length == 0) return null;
        foreach (var section in Sections)
        {
            if (section.Items.Any(i => Normalize(i.Href) == key))
            {
                return section.Key;
            }
        }
        return null;
    }

    // Toglie query string, fragment e slash iniziali/finali; case-insensitive a valle.
    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0) path = path[..cut];
        return path.Trim('/');
    }
}
