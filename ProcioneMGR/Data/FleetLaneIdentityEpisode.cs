namespace ProcioneMGR.Data;

/// <summary>
/// [K47, PRD autonomia-piena — Fase 3, 2026-09-02] <b>La storia delle identità di corsia.</b>
/// Append-only: una riga per ogni esperimento CHIUSO.
///
/// <para><b>Perché esiste, e perché è il numero più richiesto di tutto il filone.</b>
/// <c>FleetLaneObservations</c> tiene <b>una riga per corsia</b> e la sovrascrive: a ogni cambio di
/// identità <c>FirstSeenUtc</c> e <c>ObservedSeconds</c> ripartono da zero e ciò che c'era prima
/// sparisce. Ogni criterio di ritiro — inedia, Sharpe, danno, ritmo — è ancorato a quel primo
/// avvistamento, quindi <b>ogni soglia è denominata in una grandezza di cui non esiste la
/// distribuzione</b>.</para>
///
/// <para>Le due ondate di misure del 2026-08-31 e 2026-09-01 hanno prodotto sette avversari
/// indipendenti, e <b>tutti e sette hanno chiesto lo stesso numero mancante</b>: quanto vive
/// un'identità. Ricostruirlo a mano dal journal ha dato 27,0 giorni di mediana su quattro episodi —
/// e ha mostrato che <b>alzare le soglie di ritiro a 27 o 41 giorni non le rende severe, le
/// spegne</b>, perché nessuna identità realmente vissuta ci sarebbe arrivata. Un numero che decide
/// così tanto non può stare in una ricostruzione manuale.</para>
///
/// <para><b>Solo episodi chiusi.</b> L'episodio in corso vive dov'è sempre vissuto
/// (<c>FleetLaneObservations</c>): duplicarlo qui creerebbe due verità sullo stesso oggetto, che è
/// il difetto che questo filone passa il tempo a togliere. Questa tabella risponde a «quanto vivono
/// le identità»; quella risponde a «da quanto vive questa».</para>
///
/// <para><b>Il motivo della chiusura non è un campo di questa tabella</b>, ed è deliberato: il
/// ledger vede il cambio <i>dopo</i> che è avvenuto e conosce le due identità, non la ragione. La
/// ragione si ricava incrociando <c>OrchestratorDecisions</c> per corsia e istante — e dove il
/// journal tace, il risultato è <b>«non registrato»</b>, che non è un buco ma <b>la misura della
/// completezza del journal</b>. Al 2026-08-31 valeva 2 schieramenti su 4.</para>
/// </summary>
public class FleetLaneIdentityEpisode
{
    public int Id { get; set; }

    public int LaneId { get; set; }

    /// <summary>L'identità dell'esperimento chiuso: <c>simbolo|timeframe|gambe ordinate</c>.</summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Primo avvistamento: l'ancora con cui questo esperimento è stato giudicato.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>
    /// Quando il ledger si è accorto che l'identità era cambiata. <b>Non</b> l'istante del cambio:
    /// l'orchestratore guarda ogni <c>TickMinutes</c>, quindi il ritardo è al più un tick. Va detto
    /// perché una durata sistematicamente lunga di un tick è un bias, piccolo ma dichiarabile.
    /// </summary>
    public DateTime ClosedUtc { get; set; }

    /// <summary>
    /// Osservazione ACCREDITATA all'esperimento: solo i tick con la corsia in corsa, col tetto per
    /// buco. È la grandezza in cui sono denominate le soglie di ritiro — non il calendario.
    /// </summary>
    public long ObservedSeconds { get; set; }

    /// <summary>
    /// L'identità che ha preso il posto di questa. Serve a distinguere una <b>sostituzione</b> (una
    /// corsia riassegnata a un'altra ipotesi) da una <b>riconfigurazione</b> della stessa idea, e a
    /// riattaccare gli episodi in catena quando si guarda una corsia nel tempo.
    /// </summary>
    public string NextIdentity { get; set; } = string.Empty;
}
