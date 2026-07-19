using System.Security.Claims;
using ProcioneMGR.Data;

namespace ProcioneMGR.Components.Layout;

/// <summary>
/// Una singola voce di menu.
/// </summary>
/// <param name="Href">URL relativo, senza slash iniziale (es. "market/watchlist"). Stringa vuota = Home.</param>
/// <param name="Label">Etichetta mostrata in italiano.</param>
/// <param name="Icon">Classe Bootstrap Icons (es. "bi-house-door-fill").</param>
/// <param name="Description">Frase breve usata come tooltip in sidebar e come testo secondario nella ricerca globale (Ctrl+K).</param>
/// <param name="Roles">
/// Ruoli abilitati a vedere la voce. <c>null</c> = qualsiasi utente autenticato.
/// Rispecchia 1:1 il gating <c>AuthorizeView</c> della vecchia NavMenu.
/// </param>
/// <param name="Match">true = match esatto della route (usato solo per Home).</param>
public sealed record NavItem(
    string Href,
    string Label,
    string Icon,
    string Description,
    string[]? Roles = null,
    bool Match = false);

/// <summary>
/// Un blocco della sidebar (Overview / Dati / Ricerca / Trading / Avanzati / Configurazione).
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
/// <c>NavMenu</c> (sidebar), <c>Breadcrumb</c> (percorso contestuale) e
/// <c>CommandPalette</c> (ricerca globale Ctrl+K).
/// Organizzazione per workflow utente: Overview → Dati → Ricerca &amp; Sviluppo →
/// Trading → Strumenti Avanzati → Configurazione. Href e ruoli invariati.
/// </summary>
public static class NavModel
{
    private static readonly string[] ManagerAndAdmin = [AppRoles.Manager, AppRoles.Admin];
    private static readonly string[] AdminOnly = [AppRoles.Admin];

    /// <summary>Blocchi operativi. Account è a parte (renderizzato da NavMenu).</summary>
    public static readonly IReadOnlyList<NavSection> Sections =
    [
        // 🏠 OVERVIEW — punto di partenza, sempre visibile e non collassabile.
        new NavSection("overview", "Overview", "#58a6ff", Collapsible: false,
        [
            new NavItem("", "Home", "bi-house-door-fill",
                "Punto di partenza: statistiche, alert e workflow guidato.", Match: true),
            new NavItem("dashboard", "Dashboard", "bi-bar-chart-line-fill",
                "Grafici OHLCV e indicatori tecnici one-off."),
        ]),

        // 📊 DATI & MONITORAGGIO — la materia prima e il suo stato di salute.
        new NavSection("dati", "Dati & Monitoraggio", "#4ade80", Collapsible: false,
        [
            new NavItem("market/watchlist", "Watchlist", "bi-eye-fill",
                "Serie tracciate e aggiornate automaticamente in background.", ManagerAndAdmin),
            new NavItem("market-analysis", "Analisi Serie", "bi-clipboard-data",
                "Esplorazione statistica dei dati storici scaricati."),
            new NavItem("market/bars", "Barre informative", "bi-bar-chart-steps",
                "Barre a volume/dollaro vs barre a tempo, con confronto statistico.", ManagerAndAdmin),
            new NavItem("metrics", "Metriche", "bi-speedometer2",
                "KPI di piattaforma e performance tracking.", ManagerAndAdmin),
        ]),

        // 🔬 RICERCA & SVILUPPO — il workflow di creazione strategie, in ordine naturale.
        new NavSection("ricerca", "Ricerca & Sviluppo", "#fbbf24", Collapsible: false,
        [
            new NavItem("backtest", "Backtest", "bi-graph-up-arrow",
                "Simula una strategia sui dati storici, senza rischiare nulla."),
            new NavItem("optimization", "Optimization", "bi-sliders2",
                "Ottimizzazione parametri: Grid search o Bayesian.", ManagerAndAdmin),
            new NavItem("feature-selection", "Feature Selection (IC)", "bi-funnel-fill",
                "Selezione feature per Information Coefficient.", ManagerAndAdmin),
            new NavItem("ml", "ML Lab", "bi-cpu-fill",
                "Training modelli ML: Linear, Random Forest, LightGBM, MLP.", ManagerAndAdmin),
            new NavItem("ensemble", "Ensemble", "bi-diagram-3-fill",
                "Combinazione di strategie multi-lane in un portafoglio.", ManagerAndAdmin),
            new NavItem("portfolio", "Portafoglio", "bi-pie-chart-fill",
                "Confronto di allocazioni: Max Sharpe, Min Var, ERC, HRP.", ManagerAndAdmin),
            new NavItem("registry", "Registry Modelli", "bi-award-fill",
                "Champion models validati e promuovibili al trading.", ManagerAndAdmin),
            new NavItem("experiments", "Esperimenti", "bi-journals",
                "Tracking degli esperimenti ML: run, parametri, risultati.", ManagerAndAdmin),
        ]),

        // 🚀 TRADING — l'operatività vera e propria.
        new NavSection("trading", "Trading", "#f97316", Collapsible: false,
        [
            new NavItem("trading", "Trading", "bi-currency-exchange",
                "Control center: Paper, Testnet e Live.", ManagerAndAdmin),
            new NavItem("strategies", "Le mie Strategie", "bi-collection-fill",
                "Le strategie che hai salvato, pronte da riusare."),
            new NavItem("execution", "Execution Lab", "bi-stack",
                "Ordini avanzati: TWAP, VWAP, Iceberg, Adaptive.", ManagerAndAdmin),
        ]),

        // 🧠 STRUMENTI AVANZATI — automazione e analisi di nicchia, collassabile.
        new NavSection("avanzati", "Strumenti Avanzati", "#a78bfa", Collapsible: true,
        [
            new NavItem("discovery", "Discovery", "bi-search",
                "Ricerca automatica delle combinazioni più promettenti.", ManagerAndAdmin),
            new NavItem("pipeline", "Pipeline", "bi-robot",
                "Automazione end-to-end: da dati a strategia applicata.", ManagerAndAdmin),
            new NavItem("campaign", "Campagne", "bi-bullseye",
                "Rotazione automatica delle cacce: cosa fare dopo un run.", ManagerAndAdmin),
            new NavItem("alpha-mining", "Alpha Mining", "bi-gem",
                "Generazione creativa di alpha factors (genetic miner).", ManagerAndAdmin),
            new NavItem("regimes", "Regimes", "bi-grid-3x3-gap-fill",
                "Classificazione dei regimi di mercato (trend/laterale).", ManagerAndAdmin),
            new NavItem("pairs-trading", "Pairs Trading", "bi-arrow-left-right",
                "Trading sulle relazioni fra asset, non sulla direzione.", ManagerAndAdmin),
            new NavItem("volatility", "Volatilità", "bi-activity",
                "Stima della volatilità futura (GARCH).", ManagerAndAdmin),
            new NavItem("sentiment", "Sentiment", "bi-newspaper",
                "Notizie e sentiment, con verifica dell'impatto sul prezzo.", ManagerAndAdmin),
        ]),

        // ⚙️ CONFIGURAZIONE — impostazioni e amministrazione, collassabile e role-based.
        new NavSection("config", "Configurazione", "#94a3b8", Collapsible: true,
        [
            new NavItem("settings/exchanges", "Credenziali Exchange", "bi-key-fill",
                "API key degli exchange, salvate crittate."),
            new NavItem("admin/ai-supervisor", "Supervisione AI", "bi-robot",
                "Advisory layer Claude sulla pipeline (solo consultivo).", ManagerAndAdmin),
            new NavItem("admin/autonomy", "Autonomia", "bi-toggles",
                "Controlli del livello di autonomia: auto-reapply, promozioni, drift.", AdminOnly),
            new NavItem("admin/users", "Gestione Utenti", "bi-people-fill",
                "Utenti e ruoli della piattaforma.", AdminOnly),
            new NavItem("admin/backup", "Backup Database", "bi-database-fill-down",
                "Backup e restore PostgreSQL (pg_dump/pg_restore).", AdminOnly),
        ]),
    ];

    /// <summary>Chiavi dei soli blocchi collassabili (usate per lo stato persistente).</summary>
    public static IEnumerable<string> CollapsibleKeys =>
        Sections.Where(s => s.Collapsible).Select(s => s.Key);

    /// <summary>
    /// True se la voce è visibile all'utente indicato (stessa semantica del vecchio
    /// gating AuthorizeView). Condiviso fra NavMenu e CommandPalette.
    /// </summary>
    public static bool IsVisible(NavItem item, ClaimsPrincipal user)
    {
        if (item.Roles is null)
        {
            return true; // null = qualsiasi utente autenticato.
        }

        foreach (var role in item.Roles)
        {
            if (user.IsInRole(role))
            {
                return true;
            }
        }
        return false;
    }

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
        if (key.Length == 0) return "overview"; // Home appartiene a Overview.
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
