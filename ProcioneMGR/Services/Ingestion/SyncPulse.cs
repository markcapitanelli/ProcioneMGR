namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// [2026-08-15] Regola UNICA per dire se il CICLO di sync sta girando, dal timbro che
/// <see cref="MarketDataSyncWorker"/> scrive su HostHeartbeats (ruolo "ingestion-sync") a fine
/// ciclo. Nasce dall'incidente del 2026-08-14: 122 serie ferme e un banner che consigliava di
/// «verificare BREAK su exchange» — ma l'imputato era il sync morto alle 22:44, un'informazione
/// che nessuna pagina sapeva dare. La diagnosi «è il sync o sono i simboli?» parte da qui.
///
/// Regola pura e statica come <see cref="SeriesFreshness"/>: due chiamanti (pagina watchlist e
/// guardia di freschezza) devono dare lo stesso verdetto sullo stesso timbro.
/// </summary>
public static class SyncPulse
{
    /// <summary>
    /// Formato del timbro (campo Version di HostHeartbeats): «esito · intervallo Nm». L'intervallo
    /// viaggia COL timbro perché chi lo giudica (guscio) e chi lo scrive (pod ingestion) hanno
    /// appsettings indipendenti: una soglia calcolata sull'intervallo del processo sbagliato
    /// giudicherebbe male in entrambe le direzioni (review 2026-08-15).
    /// </summary>
    public static string ComposeOutcome(string esito, TimeSpan syncInterval) =>
        $"{esito} · intervallo {(int)Math.Max(1, syncInterval.TotalMinutes)}m";

    /// <summary>L'intervallo dichiarato nel timbro; null se il timbro manca o non lo dichiara (formato vecchio).</summary>
    public static TimeSpan? TryParseStampedInterval(string? outcome)
    {
        var m = outcome is null
            ? null
            : System.Text.RegularExpressions.Regex.Match(outcome, @"intervallo (\d+)m");
        return m is { Success: true } ? TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) : null;
    }

    /// <summary>Il worker è SPENTO da configurazione (MarketData:Enabled=false): non è un guasto.</summary>
    public static bool IsDisabledOutcome(string? outcome) =>
        outcome?.StartsWith("spento", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Oltre quanto silenzio il ciclo si dichiara fermo. Il gap legittimo più lungo fra due timbri
    /// è un ciclo che consuma l'intero budget (2× intervallo) partito un intervallo dopo il timbro
    /// precedente: 3× intervallo. Il margine assorbe l'orologio e il costo del timbro stesso.
    /// Un ciclo ABBANDONATO dal backstop (fino a 4× intervallo) qui risulta fermo per qualche
    /// minuto: è già una patologia, e dirla è il compito di questa regola, non un falso allarme.
    /// </summary>
    public static TimeSpan StalledAfter(TimeSpan syncInterval) =>
        syncInterval * 3 + TimeSpan.FromMinutes(2);

    /// <summary>
    /// Fermo = nessun timbro mai scritto, oppure timbro più vecchio di <see cref="StalledAfter"/>.
    /// Il null NON vale «sano» (stessa trappola del null di SeriesFreshness): distinguere
    /// «mai visto un giro» da «fermo da N minuti» è compito del chiamante, non di questa regola.
    /// </summary>
    public static bool IsStalled(DateTime? lastCycleUtc, DateTime nowUtc, TimeSpan syncInterval) =>
        lastCycleUtc is not DateTime last || nowUtc - last > StalledAfter(syncInterval);

    /// <summary>
    /// La riga di diagnosi per la notifica di serie ferme: dice DOVE guardare. Con il sync fermo
    /// l'imputato è il sync (pod ingestion / worker), non i simboli; con il sync vivo e poche serie
    /// ferme l'ipotesi giusta è la sospensione del simbolo (BREAK); con il sync vivo e molte serie
    /// ferme insieme può essere l'exchange. Prima questa riga non esisteva e il consiglio era
    /// sempre «verifica BREAK» — sbagliato proprio nell'incidente più grave.
    /// </summary>
    public static string DescribeCause(int staleCount, DateTime? lastCycleUtc, DateTime nowUtc, TimeSpan syncInterval,
        string? outcome = null)
    {
        if (IsDisabledOutcome(outcome))
        {
            return "Il worker di sync è SPENTO da configurazione (MarketData:Enabled=false): "
                 + "le serie invecchiano perché nessuno le aggiorna. Riaccendilo da /admin/autonomy.";
        }

        if (lastCycleUtc is not DateTime last)
        {
            return "Nessun giro di sync risulta mai completato (nessun timbro a DB): "
                 + "verifica che il worker di sync sia in esecuzione (pod ingestion in assetto kind).";
        }

        if (IsStalled(lastCycleUtc, nowUtc, syncInterval))
        {
            var age = nowUtc - last;
            return $"L'ultimo giro di sync è delle {last:HH:mm} UTC ({(int)age.TotalMinutes} min fa): "
                 + "è il SYNC a essere fermo (worker/pod ingestion), non i simboli. "
                 + "Al suo ritorno l'arretrato si drena da solo.";
        }

        return staleCount >= 3
            ? $"Il sync gira (ultimo giro {last:HH:mm} UTC) ma più serie non avanzano insieme: "
              + "possibile guasto lato exchange o sospensioni multiple — verifica in /market/watchlist."
            : $"Il sync gira (ultimo giro {last:HH:mm} UTC): probabile sospensione del simbolo "
              + "(stato BREAK) — verifica su exchange e valuta se disabilitare la serie in /market/watchlist.";
    }
}
