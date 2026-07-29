using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Regime;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Services.Trading;

/// <summary>Una sezione di configurazione del motore, come il motore la vede adesso.</summary>
/// <param name="Path">Percorso della sezione (es. <c>Trading:Safety</c>).</param>
/// <param name="Json">Valori EFFETTIVI (file + variabili d'ambiente + default, già fusi).</param>
/// <param name="Writable">Se il guscio può riscriverla.</param>
/// <param name="Source">Provider che fornisce il valore, per spiegare perché salvare non basta.</param>
public sealed record EngineConfigSectionView(string Path, string Json, bool Writable, string Source);

/// <summary>Esito di una scrittura: la sezione riletta, più un eventuale avvertimento non bloccante.</summary>
public sealed record EngineConfigWriteResult(string AppliedJson, string? Warning);

/// <summary>
/// Legge e scrive le sezioni di configurazione OSPITATE DAL MOTORE. Vive nel progetto condiviso
/// perché la usano entrambi gli host: <c>ProcioneMGR.Trading</c> la espone via gRPC, e il monolite
/// la usa direttamente quando il motore gira in-process (stessa logica, nessun ramo speciale).
///
/// <para>Perché esiste, in una riga: quando il motore gira in un altro processo, il file che il
/// guscio scrive non è quello che il motore legge — verificato dal vivo il 2026-07-29 su un PVC
/// rimasto a <c>{}</c>. Da qui in poi il guscio non indovina più: chiede.</para>
///
/// <para>Tre garanzie, nell'ordine in cui contano:
/// <list type="number">
/// <item>si tocca solo ciò che è in <see cref="EngineConfigSections"/> — elenco chiuso;</item>
/// <item>si valida con le STESSE regole dei pannelli (<see cref="AdminConfigRules"/>), così un
/// valore rifiutato in UI non entra da un'altra porta;</item>
/// <item>si dice la verità sulla SORGENTE: in Kubernetes le variabili d'ambiente della ConfigMap
/// vincono su <c>appsettings.json</c>, quindi un salvataggio può riuscire e non cambiare nulla.
/// Tacerlo sarebbe la stessa bugia che questo lavoro sta correggendo.</item>
/// </list></para>
/// </summary>
public sealed class EngineConfigService(
    IConfiguration configuration,
    IAppConfigWriter writer,
    IHostEnvironment environment,
    ILogger<EngineConfigService> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Tipo di opzioni per sezione: serve a due cose che una lettura "grezza" non permetterebbe —
    /// restituire i DEFAULT del codice per le chiavi assenti dal file (che è ciò che il motore
    /// applica davvero), e validare con le stesse regole della UI.
    /// </summary>
    private static readonly Dictionary<string, Type> SectionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Trading:Safety"] = typeof(SafetyConfiguration),
        ["Trading:LiveExecution"] = typeof(LiveExecutionOptions),
        ["Trading:CorrelatedExposure"] = typeof(CorrelatedExposureOptions),
        ["Trading:RegimeRouting"] = typeof(RegimeRoutingOptions),
        ["Trading:LaneInvariants"] = typeof(LaneInvariantOptions),
        ["Trading:ProtectiveExitShadow"] = typeof(ProtectiveExitShadowOptions),
        ["MarketData:Realtime"] = typeof(RealtimeFeedOptions),
        ["Carry"] = typeof(CarryOptions),
        ["Notifications"] = typeof(ProcioneMGR.Services.Notifications.NotificationOptions),
    };

    /// <summary>Dove il motore scrive: lo stesso file che <see cref="AppConfigWriter"/> tocca.</summary>
    public string ConfigPath => Path.Combine(environment.ContentRootPath, "appsettings.json");

    /// <summary>
    /// Il motore può riscrivere la propria configurazione? Falso quando il file non esiste o è in
    /// sola lettura (es. montato da ConfigMap). Il pannello lo dice PRIMA, invece di far scoprire
    /// il rifiuto al primo salvataggio.
    /// </summary>
    public bool IsWritable()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            using var probe = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Configurazione del motore non scrivibile in {Path}.", ConfigPath);
            return false;
        }
    }

    /// <summary>
    /// Legge le sezioni richieste (vuoto = tutte quelle note). Le sconosciute o proibite vengono
    /// SALTATE in silenzio in lettura: un pannello che chiede più del dovuto non deve far fallire
    /// l'intera schermata — mentre in SCRITTURA il rifiuto è esplicito e rumoroso.
    /// </summary>
    public IReadOnlyList<EngineConfigSectionView> Read(IEnumerable<string>? sections = null)
    {
        var wanted = sections?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (wanted is null || wanted.Count == 0) wanted = [.. EngineConfigSections.AllReadable()];

        var views = new List<EngineConfigSectionView>();
        foreach (var section in wanted)
        {
            if (!EngineConfigSections.IsReadable(section)) continue;
            views.Add(new EngineConfigSectionView(
                section,
                SerializeSection(section),
                EngineConfigSections.IsWritable(section),
                DescribeSource(section)));
        }
        return views;
    }

    /// <summary>
    /// Sostituisce una sezione. Rifiuta con <see cref="InvalidOperationException"/> ciò che non è
    /// scrivibile o non passa la validazione: il chiamante gRPC lo traduce in un codice di stato,
    /// il chiamante in-process lo mostra all'operatore.
    /// </summary>
    public async Task<EngineConfigWriteResult> WriteAsync(string section, string json, CancellationToken ct = default)
    {
        if (!EngineConfigSections.IsWritable(section))
        {
            throw new InvalidOperationException(
                $"Sezione '{section}' non scrivibile da questo canale. Scrivibili: {string.Join(", ", EngineConfigSections.Writable)}.");
        }

        if (!SectionTypes.TryGetValue(section, out var type))
        {
            throw new InvalidOperationException($"Sezione '{section}' senza tipo di opzioni noto: impossibile validarla.");
        }

        object options;
        try
        {
            options = JsonSerializer.Deserialize(json, type, Json)
                      ?? throw new InvalidOperationException("payload vuoto");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON non valido per la sezione '{section}': {ex.Message}");
        }

        // STESSE regole dei pannelli: un valore rifiutato in UI non deve poter entrare da qui.
        var error = AdminConfigRules.Validate(options);
        if (error is not null) throw new InvalidOperationException(error);

        await writer.SaveSectionAsync(section, options, ct);

        // RILETTURA ESPLICITA, non "aspettiamo che il file watcher se ne accorga".
        //
        // Il disegno originale contava su reloadOnChange: si scrive il file, il provider JSON nota
        // la modifica e IOptionsMonitor propaga entro ~1s. Verificato dal vivo il 2026-07-29 che
        // DENTRO IL POD non succede: il file sta su un PVC, e la notifica di modifica (inotify) non
        // attraversa quel mount. Il file conteneva Realtime:Enabled=false e il motore continuava a
        // rispondere true — cioè la configurazione era scritta e non applicata, che dal punto di
        // vista di chi guarda il pannello è identico al non aver salvato affatto.
        //
        // Qui non serve alcun watcher: chi scrive SA di aver scritto. Reload() ricarica i provider
        // e fa scattare i change token, quindi IOptionsMonitor dei worker vede il valore nuovo
        // subito e in modo deterministico, su qualunque tipo di volume.
        if (configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
        else
        {
            logger.LogWarning(
                "Configurazione non ricaricabile (non è un IConfigurationRoot): la sezione '{Section}' è " +
                "sul disco ma potrebbe non essere applicata finché il processo non riparte.", section);
        }

        logger.LogInformation("Configurazione del motore aggiornata e ricaricata: sezione '{Section}'.", section);

        return new EngineConfigWriteResult(SerializeSection(section), OverrideWarning(section));
    }

    /// <summary>
    /// Serializza la sezione partendo dal POCO tipizzato: le chiavi assenti dal file compaiono col
    /// DEFAULT DEL CODICE, che è ciò che il motore applica davvero. Una lettura grezza mostrerebbe
    /// invece un buco, facendo credere che la funzione sia "non configurata" quando è configurata
    /// dal costruttore.
    /// </summary>
    private string SerializeSection(string section)
    {
        if (SectionTypes.TryGetValue(section, out var type))
        {
            var instance = Activator.CreateInstance(type)!;
            configuration.GetSection(section).Bind(instance);
            return JsonSerializer.Serialize(instance, type, Json);
        }

        // Sezioni di sola lettura scalari (LaneCount, UseRemoteTrading): il valore grezzo com'è.
        var raw = configuration[section];
        return raw is null ? "null" : JsonValue.Create(raw)!.ToJsonString();
    }

    /// <summary>
    /// Da quale provider arriva la sezione. Serve a spiegare l'unico modo in cui un salvataggio può
    /// riuscire e non cambiare nulla: in Kubernetes le env della ConfigMap hanno precedenza sul
    /// file, quindi la chiave resta quella della ConfigMap finché non si tocca il deploy.
    /// </summary>
    private string DescribeSource(string section)
    {
        if (configuration is not IConfigurationRoot root) return "sconosciuta";

        var keys = SectionTypes.TryGetValue(section, out var type)
            ? type.GetProperties().Select(p => $"{section}:{p.Name}").ToList()
            : [section];

        var winners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var winner = WinningProvider(root, key);
            if (winner is not null) winners.Add(FriendlySourceName(winner));
        }

        if (winners.Count == 0) return "default del codice";
        return string.Join(" + ", winners.Order());
    }

    /// <summary>
    /// Avviso non bloccante quando almeno una chiave della sezione è fornita da un provider che
    /// VINCE sul file JSON.
    ///
    /// <para>Il criterio non è "il provider si chiama Environment" — sarebbe un riconoscimento per
    /// nome, cieco a tutto il resto (riga di comando, provider remoti, sorgenti custom). Il criterio
    /// è quello che conta davvero: <b>chi ha l'ultima parola non è il file su cui abbiamo appena
    /// scritto</b>. Se è così, il salvataggio riesce e non cambia nulla, ed è esattamente il tipo di
    /// silenzio che questo lavoro esiste per togliere.</para>
    /// </summary>
    private string? OverrideWarning(string section)
    {
        if (configuration is not IConfigurationRoot root) return null;
        if (!SectionTypes.TryGetValue(section, out var type)) return null;

        var overridden = new List<string>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties())
        {
            var winner = WinningProvider(root, $"{section}:{property.Name}");
            if (winner is null || IsFileProvider(winner)) continue;
            overridden.Add(property.Name);
            sources.Add(FriendlySourceName(winner));
        }

        if (overridden.Count == 0) return null;
        return $"{string.Join(", ", overridden)} " +
               $"{(overridden.Count == 1 ? "arriva" : "arrivano")} da {string.Join(" / ", sources.Order())}, " +
               "che ha la precedenza sul file: il valore salvato non avrà effetto finché quella sorgente resta " +
               "(in Kubernetes: la ConfigMap del deployment).";
    }

    /// <summary>L'ULTIMO provider che possiede la chiave vince — è l'ordine di registrazione di .NET.</summary>
    private static IConfigurationProvider? WinningProvider(IConfigurationRoot root, string key)
    {
        IConfigurationProvider? winner = null;
        foreach (var provider in root.Providers)
        {
            if (provider.TryGet(key, out _)) winner = provider;
        }
        return winner;
    }

    private static bool IsFileProvider(IConfigurationProvider provider) =>
        provider is Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider;

    /// <summary>Nome comprensibile del provider: il tipo .NET non dice nulla a chi legge un pannello.</summary>
    private static string FriendlySourceName(IConfigurationProvider provider) => provider switch
    {
        Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider => "appsettings.json",
        Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider
            => "variabili d'ambiente",
        Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider => "riga di comando",
        _ => provider.GetType().Name,
    };
}
