namespace ProcioneMGR.Data;

/// <summary>
/// [J8, PRD autonomia-operativa 2026-08-25] <b>L'orologio dell'osservazione di una corsia di
/// flotta, che non si azzera al riavvio.</b>
///
/// Prima l'osservazione era <c>now − TradingEngineStates.StartedAtUtc</c>, che riparte da zero a
/// ogni riavvio del motore: con <c>RetireMinWeeks=3</c> servivano 21 giorni di uptime ININTERROTTO,
/// e la finestra continua più lunga mai raggiunta in tutta la vita della flotta è stata
/// <b>20 giorni e 3 ore</b> (corsie 4 e 6, 2026-08-03 → 2026-08-23). Il criterio di ritiro per
/// Sharpe non ha mai potuto esprimersi — non ha giudicato e assolto: non ha potuto guardare.
///
/// <para>Qui si accumula il tempo di osservazione VISTO dai tick del guscio (una riga per corsia),
/// e si azzera solo quando cambia l'<see cref="Identity"/> — cioè quando sulla corsia arriva un
/// altro esperimento. <see cref="FirstSeenUtc"/> è l'ancora per contare i trade: dal primo
/// avvistamento dell'identità, non dall'ultimo riavvio del motore.</para>
/// </summary>
public class FleetLaneObservation
{
    /// <summary>Chiave: una riga per corsia.</summary>
    public int LaneId { get; set; }

    /// <summary>
    /// L'identità dell'esperimento in corsia: simbolo, timeframe e gambe attive (ordinate).
    /// Cambia ⇒ l'osservazione riparte: 20 giorni su GridMeanReversion UNI non dicono nulla sul
    /// Composite DOT che l'ha sostituita.
    /// </summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Primo avvistamento di QUESTA identità: l'ancora per contare trade e performance.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>
    /// Secondi di osservazione accumulati. Si accreditano solo i tick con la corsia IN CORSA, e i
    /// buchi lunghi (guscio spento) si accreditano al massimo per il tetto: il conteggio può solo
    /// SOTTOSTIMARE l'osservazione vera, mai gonfiarla — l'errore è nella direzione che ritarda un
    /// ritiro, non in quella che lo inventa.
    /// </summary>
    public long ObservedSeconds { get; set; }

    /// <summary>L'ultimo tick che ha guardato questa corsia (il riferimento del prossimo delta).</summary>
    public DateTime LastTickUtc { get; set; }
}
