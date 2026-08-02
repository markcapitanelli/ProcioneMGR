namespace ProcioneMGR.Services.Trading;

/// <summary>
/// L'elenco CHIUSO delle sezioni di configurazione che il guscio può leggere e riscrivere sul
/// motore via gRPC (<c>GetEngineConfig</c>/<c>SetEngineConfig</c>).
///
/// <para>Questa classe è il confine di sicurezza di quel canale, e vive in un file suo perché sia
/// difficile allargarla per distrazione. <c>SetEngineConfig</c> scrive su un processo che firma
/// ordini veri: senza un elenco chiuso sarebbe un'API di configurazione generica su una superficie
/// che comprende la connection string del database, la master key con cui si decifrano le
/// credenziali exchange, il segreto condiviso che autorizza il canale stesso e i toggle che
/// decidono quale processo esegue gli ordini. Nessuno di questi è raggiungibile da qui — non
/// perché il chiamante sia gentile, ma perché il server rifiuta tutto ciò che non è in
/// <see cref="Writable"/>.</para>
///
/// <para>Il criterio per entrare: la sezione governa un COMPORTAMENTO OPERATIVO ospitato dal
/// motore, che un operatore deve poter cambiare senza un deploy. Il criterio per restare fuori:
/// tutto ciò che riguarda identità, segreti, o la TOPOLOGIA (chi ospita cosa) — quello cambia
/// insieme al deployment, e cambiarlo a caldo dal browser produce due esecutori sulla stessa
/// corsia, o nessuno.</para>
/// </summary>
public static class EngineConfigSections
{
    /// <summary>
    /// Sezioni leggibili E scrivibili dal pannello. Tutte governano componenti che vivono
    /// nell'host del motore: cambiarle sul guscio non avrebbe alcun effetto.
    /// </summary>
    public static readonly IReadOnlyList<string> Writable =
    [
        // Le soglie pre-ordine: è il motore ad applicarle, ordine per ordine.
        "Trading:Safety",
        // Esecuzione a fette (TWAP/VWAP/Iceberg) delle aperture Testnet/Live.
        "Trading:LiveExecution",
        // Limite trasversale sull'esposizione correlata fra corsie.
        "Trading:CorrelatedExposure",
        // Router di regime: quali strategie possono operare, e in quale regime.
        "Trading:RegimeRouting",
        // Watchdog degli invarianti contabili (quarantena corsie).
        "Trading:LaneInvariants",
        // Sentinella d'ombra delle uscite protettive.
        "Trading:ProtectiveExitShadow",
        // Feed di prezzo real-time, incluso se i tick possono chiudere posizioni.
        "MarketData:Realtime",
        // Forward test del carry delta-neutro.
        "Carry",
        // [2026-07-29] Canale di notifica DEL MOTORE. Aggiunto dopo aver scoperto che il canale del
        // guscio era muto da due giorni senza che nessuno potesse accorgersene: il motore ha un suo
        // producer — il watchdog che mette una corsia in QUARANTENA, cioè l'allarme più importante
        // che la piattaforma possa emettere — e lasciarlo non configurabile e non verificabile
        // avrebbe replicato lo stesso punto cieco un processo più in là.
        //
        // Il TOKEN resta fuori: si legge solo da TELEGRAM_BOT_TOKEN, non è in questa sezione e non
        // passa da questo canale. Qui viaggiano interruttore, provider, ChatId (non un segreto) e
        // rate-limit.
        "Notifications",
        // [E3, 2026-07-31] Dual-read ML osservativo: il confronto lo fa TradingEngine, cioè il
        // motore. Il pannello scriveva la sezione del guscio, che il motore remoto non legge — e il
        // Trading host non faceva nemmeno il binding, quindi il toggle non era collegabile da
        // nessuna strada. Niente segreti qui: toggle, URL del servizio ML in-cluster e timeout.
        "Ml",
        // [AF5.1] Heartbeat incrociato: il worker del MOTORE (che scrive il proprio battito e
        // sorveglia quello del guscio) legge la sezione del proprio processo — accenderla dal
        // browser deve raggiungere anche lui, e reloadOnChange non attraversa un mount PVC.
        "Heartbeat",
    ];

    /// <summary>
    /// Sezioni che il pannello può LEGGERE per mostrare il contesto, ma mai riscrivere. Non sono
    /// segreti — sono fatti sulla topologia che l'operatore ha diritto di vedere e che deve
    /// cambiare dal deploy.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnly =
    [
        "Trading:LaneCount",
        "Trading:UseRemoteTrading",
    ];

    /// <summary>
    /// Prefissi il cui contenuto non esce MAI da questo processo, nemmeno in lettura. Elencati
    /// esplicitamente invece di affidarsi al fatto che non siano fra i leggibili: un domani
    /// qualcuno potrebbe allargare la lettura, e questo controllo resta.
    /// </summary>
    private static readonly string[] NeverExposed =
    [
        "ConnectionStrings",
        "Security",
        "Trading:GrpcSharedSecret",
        "Trading:RemoteUrl",
        "Llm",          // niente chiavi API di terzi, nemmeno indirettamente
    ];

    /// <summary>Vero se la sezione può essere riscritta dal guscio.</summary>
    public static bool IsWritable(string section) =>
        !string.IsNullOrWhiteSpace(section)
        && !IsForbidden(section)
        && Writable.Contains(section, StringComparer.OrdinalIgnoreCase);

    /// <summary>Vero se la sezione può essere letta dal guscio (i scrivibili sono anche leggibili).</summary>
    public static bool IsReadable(string section) =>
        !string.IsNullOrWhiteSpace(section)
        && !IsForbidden(section)
        && (Writable.Contains(section, StringComparer.OrdinalIgnoreCase)
            || ReadOnly.Contains(section, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Vero se la sezione tocca un prefisso proibito. Il confronto è per SEGMENTO, non per
    /// sottostringa: "Securities" non deve essere scambiata per "Security", e "ConnectionStrings"
    /// deve bloccare anche "ConnectionStrings:PostgresConnection".
    /// </summary>
    private static bool IsForbidden(string section)
    {
        foreach (var forbidden in NeverExposed)
        {
            if (section.Equals(forbidden, StringComparison.OrdinalIgnoreCase)) return true;
            if (section.StartsWith(forbidden + ":", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Tutte le sezioni leggibili, nell'ordine in cui hanno senso per un pannello.</summary>
    public static IEnumerable<string> AllReadable() => Writable.Concat(ReadOnly);
}
