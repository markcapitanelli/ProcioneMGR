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
    /// [J1, PRD autonomia-operativa 2026-08-25] Ore di silenzio (dall'ultimo run della campagna)
    /// dopo cui una campagna in <c>WaitingForTrigger</c> torna in rotazione DA SOLA.
    /// <c>0</c> = mai (comportamento storico: si esce solo con un trigger contestuale o a mano).
    ///
    /// <para>Perché esiste: da <c>WaitingForTrigger</c> il planner non usciva a tempo — l'unica
    /// uscita era un cambio di regime rilevato da <c>RegimeChangeDetector</c>, o l'operatore. Il
    /// 2026-08-23 la rotazione si è esaurita e la ricerca è rimasta FERMA 43+ ore senza che nessuna
    /// superficie lo dicesse; e il detector era stato per un mese la sorgente di sveglie spurie
    /// (bug di unità del log-HAR, corretto il 2026-08-20), quindi un detector guasto può sia
    /// svegliare a vuoto sia non svegliare mai. Il riarmo a tempo è la sorgente INDIPENDENTE dal
    /// trigger: un guasto del detector non può più fermare la ricerca per sempre.</para>
    ///
    /// <para>La fermata originale restava un'idea giusta («non macinare la stessa rotazione in un
    /// regime invariato») ed è per questo che il default non è zero ore ma UN GIORNO: più lungo del
    /// backoff per-config (12h), così la pausa contemplativa c'è comunque — solo, non è più
    /// eterna.</para>
    /// </summary>
    public int RearmHours { get; set; } = 24;

    /// <summary>
    /// [J3] Ore senza un run completato (e senza run in corso) oltre cui la sonda della ricerca in
    /// Home dichiara la macchina FERMA. È una soglia di LETTURA, non un gate: non ferma e non avvia
    /// nulla. Più corta di <see cref="RearmHours"/> di proposito: prima si vede il fermo, poi (se
    /// il riarmo è acceso) la piattaforma riparte da sola — la card racconta entrambe le cose.
    /// </summary>
    public int StallAlertHours { get; set; } = 12;

    /// <summary>
    /// [K59, 2026-09-03] <b>Il tetto di ore di caccia al mese.</b> <c>0</c> = nessun tetto, e allora
    /// non si propone nulla — il consumo resta non governato, ma dichiarato.
    ///
    /// <para><b>Perché in ore e non in numero di cacce.</b> La mediana per run va da <b>0,6 minuti</b>
    /// (cfg 9, 1d su 10 serie) a <b>43,8</b> (cfg 19, 5m su 10 serie): settanta volte. Contare le
    /// cacce tratterebbe come uguali due cose che non lo sono — è lo stesso errore per cui K54b ha
    /// dovuto mettere il costo accanto alla resa.</para>
    ///
    /// <para><b>Perché serve un tetto esplicito.</b> Il gate del DSR deflaziona per i tentativi del
    /// PROPRIO run e non vede le altre cacce: aggiungerne non lo rende più severo, quindi
    /// <b>nessun freno scatta da solo</b>.</para>
    ///
    /// <para>Riferimento misurato il 2026-09-03: dopo i tagli su 18 e 19 e l'ingresso di cinque
    /// configurazioni, il consumo previsto è di circa <b>32 ore al mese</b> su nove cacce (otto con
    /// un costo misurato).</para>
    /// </summary>
    public double MonthlyHourBudget { get; set; }

    /// <summary>
    /// [K59] Le proposte di rallentamento si <b>applicano da sole</b>. Default <c>false</c>, ed è
    /// deliberato: riscrivere la cadenza di una caccia è una decisione di budget del proprietario,
    /// e la stessa scelta è già stata fatta per <c>GreyAutoDeploy</c> e per il sonno di una caccia
    /// (K50: «nessuna azione automatica»).
    ///
    /// <para>Con <c>false</c> il worker misura, scrive il verdetto e — se serve — notifica. È
    /// esattamente ciò che serve per guardarlo girare prima di dargli il potere di scrivere.</para>
    /// </summary>
    public bool BudgetAutoApply { get; set; }

    /// <summary>
    /// [K59] Ogni quanti minuti guardare il budget. Non è una decisione urgente — le ore si
    /// accumulano lentamente — e un giro ogni ora è già abbondante. <c>0</c> = spento.
    /// </summary>
    public int BudgetTickMinutes { get; set; } = 60;

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
