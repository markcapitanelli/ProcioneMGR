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

    /// <summary>
    /// [I7] Minuti di PAUSA della campagna dopo che un umano ha annullato un run. <c>0</c> = nessuna
    /// pausa (comportamento storico).
    ///
    /// <para>Un annullamento è un ordine, non un esito: prima finiva nello stesso ramo di un
    /// fallimento e la rotazione ripartiva entro un tick da 60 secondi, cioè il contrario di ciò che
    /// chi annulla voleva. Il default di 60 minuti è una finestra in cui si può guardare cos'è
    /// successo senza che la piattaforma riparta da sola — non è un numero misurato, ed è
    /// amministrabile apposta.</para>
    /// </summary>
    public int CancelPauseMinutes { get; set; } = 60;

    /// <summary>
    /// [I7] Il percorso campagna rispetta <c>AutoReapply:Enabled</c>. Default <c>true</c>.
    ///
    /// <para>Prima il planner chiamava l'applier senza consultare quel gate, che è letto solo dallo
    /// scheduler: con la ri-applica automatica SPENTA, la campagna schierava lo stesso. Un
    /// interruttore che chiude una porta e ne lascia aperta un'altra è la stessa forma dei pannelli
    /// che scrivevano sul processo sbagliato. Con questo a <c>true</c> e la ri-applica spenta, i
    /// sopravvissuti vengono registrati e notificati ma NON schierati.</para>
    /// </summary>
    public bool RespectAutoReapplyGate { get; set; } = true;
}
