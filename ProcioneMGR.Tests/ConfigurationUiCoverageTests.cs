using System.Text.RegularExpressions;

namespace ProcioneMGR.Tests;

/// <summary>
/// LA REGOLA D'ORO DELLA PIATTAFORMA, resa verificabile: nessuna funzione backend può esistere
/// senza essere controllabile dall'interfaccia web.
///
/// <para>L'audit backend↔frontend del 2026-07-29 ha trovato quattordici sezioni di configurazione
/// che governavano funzioni vive — il feed real-time e il suo potere di chiudere posizioni, il
/// limite di esposizione correlata, il router di regime, il watchdog delle corsie, il canale
/// Telegram, il forward test del carry — e che si potevano toccare SOLO editando
/// <c>appsettings.json</c> a mano. Nessuna era rotta: erano invisibili, che per una manopola di
/// sicurezza è lo stesso guasto della <c>ConfigurationBindingTests</c> vista da un altro lato.</para>
///
/// <para>Questo test impedisce che il buco si riapra. Scandisce i sorgenti alla ricerca delle
/// sezioni lette dal codice e pretende che ognuna compaia nell'inventario qui sotto: o con la
/// pagina che la espone (e allora quella pagina deve nominarla davvero), o con la ragione
/// esplicita per cui non ha UI. Aggiungere una sezione nuova senza decidere in quale dei due casi
/// stia fa fallire la suite — che è esattamente il momento giusto per accorgersene.</para>
/// </summary>
public sealed class ConfigurationUiCoverageTests
{
    /// <summary>Sezione esposta da una pagina: il file razor DEVE contenerne il nome.</summary>
    private static readonly Dictionary<string, string> ExposedBy = new()
    {
        // --- /admin/autonomy: gli automatismi ---
        ["Trading:LiveExecution"] = "Components/Pages/Admin/Autonomy.razor",
        ["AutoReapply"] = "Components/Pages/Admin/Autonomy.razor",
        ["PromotionEvaluator"] = "Components/Pages/Admin/Autonomy.razor",
        ["Llm"] = "Components/Pages/Admin/Autonomy.razor",
        ["PipelineSupervisor"] = "Components/Pages/Admin/Autonomy.razor",
        ["Sentiment"] = "Components/Pages/Admin/Autonomy.razor",
        ["Drift"] = "Components/Pages/Admin/Autonomy.razor",
        ["Campaign"] = "Components/Pages/Admin/Autonomy.razor",
        ["RegimeTrigger"] = "Components/Pages/Admin/Autonomy.razor",
        ["EnsembleComparator"] = "Components/Pages/Admin/Autonomy.razor",
        ["Registry"] = "Components/Pages/Admin/Autonomy.razor",
        ["Carry"] = "Components/Pages/Admin/Autonomy.razor",
        ["Liquidations"] = "Components/Pages/Admin/Autonomy.razor",
        ["Notifications"] = "Components/Pages/Admin/Autonomy.razor",
        ["Ml"] = "Components/Pages/Admin/Autonomy.razor",
        ["Observability:Enabled"] = "Components/Pages/Admin/Autonomy.razor",
        ["Observability:OtlpEndpoint"] = "Components/Pages/Admin/Autonomy.razor",

        // --- /admin/protections: ciò che filtra o ferma un'operazione ---
        ["MarketData:Realtime"] = "Components/Pages/Admin/Protections.razor",
        ["Trading:ProtectiveExitShadow"] = "Components/Pages/Admin/Protections.razor",
        ["Trading:CorrelatedExposure"] = "Components/Pages/Admin/Protections.razor",
        ["Trading:RegimeRouting"] = "Components/Pages/Admin/Protections.razor",
        ["Trading:LaneInvariants"] = "Components/Pages/Admin/Protections.razor",

        // --- pagine operative ---
        ["Trading:Safety"] = "Components/Pages/Trading.razor",
        ["Execution"] = "Components/Pages/ExecutionLab.razor",
    };

    /// <summary>
    /// Sezioni deliberatamente SENZA controllo UI, con la ragione. Non sono eccezioni di comodo: o
    /// scelgono la TOPOLOGIA del processo (chi ospita cosa, deciso una volta a startup e vincolato
    /// al deploy), o non cambiano alcun comportamento osservabile.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyNotExposed = new()
    {
        ["MarketData:UseRemoteIngestion"] =
            "Topologia: decide se la sync gira in-process o nel servizio Ingestion. Cambiarlo dalla UI " +
            "senza il deploy corrispondente lascerebbe la watchlist senza aggiornamenti, o con due " +
            "scrittori. Il commento in appsettings dice esplicitamente NON esporre in /admin/autonomy.",
        ["MarketData:RemoteIngestionUrl"] = "Coppia del precedente: stesso vincolo di deploy.",
        ["Trading:UseRemoteTrading"] =
            "Topologia, ed è il caso più pericoloso: col valore sbagliato si hanno DUE esecutori sulla " +
            "stessa corsia, o nessuno. Deve cambiare insieme al Deployment, non da un browser.",
        ["Ml:RemoteUrl"] =
            "Il canale gRPC si crea una volta sola a startup: il campo esiste comunque nel pannello " +
            "Diagnostica di /admin/autonomy (sezione Ml), marcato come 'vale dal riavvio'.",
        ["Http:DisableHttpsRedirection"] =
            "Proprietà dell'ambiente di hosting (reverse proxy che parla in chiaro), non una scelta " +
            "dell'operatore: sbagliarla dalla UI significa perdere l'accesso alla UI stessa.",
        ["FactorCache"] =
            "Solo memoria e prestazioni: la cache è un memoizzatore con invariante 'cache == ricalcolo', " +
            "quindi nessun valore prodotto dalla piattaforma cambia al variare di questa sezione.",
        ["Trading:Bitget:SpotMarketBuyVerified"] =
            "Attestazione di una verifica fatta a mano contro l'exchange reale (semantica del campo " +
            "quantity sugli ordini market spot): è un fatto sul mondo, non una preferenza da cambiare.",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Le sezioni lette dal codice. Il pattern copre le quattro forme in uso:
    /// <c>GetSection("X")</c>, <c>GetValue&lt;T&gt;("X")</c>, <c>Configuration["X"]</c> e la
    /// costante <c>SectionName = "X"</c>.
    /// </summary>
    private static readonly Regex SectionPattern = new(
        "(?:GetSection|GetRequiredSection|GetValue<[^>]*>)\\(\"(?<s>[A-Za-z][A-Za-z0-9:]*)\""
        + "|Configuration\\[\"(?<s2>[A-Za-z][A-Za-z0-9:]*)\"\\]"
        + "|SectionName\\s*=\\s*\"(?<s3>[A-Za-z][A-Za-z0-9:]*)\"",
        RegexOptions.Compiled);

    /// <summary>Sezioni infrastrutturali del framework: non sono funzioni della piattaforma.</summary>
    private static readonly HashSet<string> FrameworkSections =
        ["Logging", "AllowedHosts", "ConnectionStrings", "Security", "DataProtection", "Kestrel", "Urls"];

    private static IReadOnlyList<string> ScanConfiguredSections()
    {
        var root = RepoRoot();
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var project in new[] { "ProcioneMGR", "ProcioneMGR.Trading" })
        {
            var dir = Path.Combine(root, project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // bin/obj contengono copie generate: gonfierebbero il risultato senza aggiungerci nulla.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                foreach (Match m in SectionPattern.Matches(File.ReadAllText(file)))
                {
                    var section = new[] { m.Groups["s"], m.Groups["s2"], m.Groups["s3"] }
                        .First(g => g.Success).Value;
                    if (!FrameworkSections.Contains(section.Split(':')[0])) found.Add(section);
                }
            }
        }

        return [.. found];
    }

    [Fact]
    public void EveryConfiguredSection_IsEitherExposedInTheUi_OrDeliberatelyNot()
    {
        var orphans = ScanConfiguredSections()
            .Where(s => !ExposedBy.ContainsKey(s) && !DeliberatelyNotExposed.ContainsKey(s))
            .ToList();

        Assert.True(orphans.Count == 0,
            "Sezioni di configurazione senza controllo UI e senza una ragione dichiarata: " +
            string.Join(", ", orphans) +
            ". Aggiungi un pannello che le esponga, oppure inseriscile in DeliberatelyNotExposed " +
            "spiegando perché non devono essere toccabili dall'interfaccia.");
    }

    [Fact]
    public void EveryClaimedOwnerPage_ActuallyNamesTheSection()
    {
        // Una mappa che dichiara un proprietario senza che quel proprietario esista è peggio di
        // nessuna mappa: dice che la copertura c'è quando non c'è.
        var root = RepoRoot();
        var broken = new List<string>();

        foreach (var (section, relativePath) in ExposedBy)
        {
            var path = Path.Combine(root, "ProcioneMGR", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { broken.Add($"{section} → {relativePath} (file assente)"); continue; }

            var markup = File.ReadAllText(path);
            // Le sezioni annidate compaiono nel razor col path completo oppure con la costante
            // SectionName del loro POCO: si accetta anche l'ultimo segmento come prova di presenza.
            var leaf = section.Split(':')[^1];
            if (!markup.Contains(section, StringComparison.Ordinal)
                && !markup.Contains(leaf, StringComparison.Ordinal))
            {
                broken.Add($"{section} → {relativePath} (la pagina non la nomina)");
            }
        }

        Assert.True(broken.Count == 0, "Proprietari dichiarati ma non reali: " + string.Join("; ", broken));
    }

    [Fact]
    public void TheInventoryHasNoStaleEntries()
    {
        // Il rovescio: una sezione rimossa dal codice e rimasta nell'inventario farebbe credere
        // coperta una funzione che non esiste più.
        var configured = ScanConfiguredSections().ToHashSet(StringComparer.Ordinal);
        var stale = ExposedBy.Keys.Concat(DeliberatelyNotExposed.Keys)
            .Where(s => !configured.Contains(s))
            .ToList();

        Assert.True(stale.Count == 0,
            "Sezioni nell'inventario che il codice non legge più: " + string.Join(", ", stale));
    }
}
