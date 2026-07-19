namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Opzioni del Campaign Planner (Fase 1, PRD Autonomia Operativa §4), sezione <c>Campaign</c>.
/// </summary>
public sealed class CampaignOptions
{
    /// <summary>
    /// Gate GLOBALE del planner. DEFAULT false (è IL cambio di natura da strumento ad agente:
    /// l'attivazione è una decisione esplicita dell'operatore, come da PRD §4). Hot-reload.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Cadenza del tick del worker (letta all'avvio; cambiarla richiede riavvio).</summary>
    public int TickSeconds { get; set; } = 60;
}
