namespace ProcioneMGR.Services.Admin;

/// <summary>
/// Configurazione del backup del database: la cartella dei dump <b>notturni</b>, la loro
/// conservazione, la soglia oltre la quale il backup è dichiarato fermo e il nome dell'operazione
/// pianificata che lo esegue.
///
/// <para><b>PERCHÉ ESISTE (2026-08-23).</b> <c>/admin/backup</c> elencava solo la cartella
/// <c>backup/</c> della content root, dove l'ultimo file era del <b>2026-07-09</b>. Il backup vero
/// lo fa da mesi <c>scripts/db-backup.ps1</c>, ogni notte alle 03:30, in
/// <c>%USERPROFILE%\ProcioneMGR-Backup</c> — e lì i dump sono giornalieri e sani. La pagina
/// mostrava quindi una data vecchia di un mese e mezzo mentre il backup funzionava: un controllo
/// che rassicura (o allarma) <i>a prescindere dalla realtà</i>, la classe di difetto che questo
/// progetto tratta come grave.</para>
///
/// <para><b>PERCHÉ UNA SEZIONE E NON UNA COSTANTE.</b> La destinazione dello script è
/// <b>parametrica</b> (<c>-Destination</c>): ricopiarla in C# creerebbe due verità che divergono al
/// primo cambio, e la pagina tornerebbe a mentire — solo con un'altra data. La fonte unica è questa
/// sezione. <c>db-backup.ps1</c> la legge dall'<c>appsettings.json</c> del <b>repo principale</b>,
/// lo stesso file da cui già prende la connection string; i suoi parametri restano solo come
/// override esplicito per un'esecuzione una tantum, e <c>-Register</c> non li scrive più dentro il
/// task (un argomento congelato nel task è la stessa doppia verità, spostata di un metro).</para>
///
/// <para>Nessuna proprietà calcolata qui: <c>SaveSectionAsync</c> serializza il POCO intero, quindi
/// una get-only diventerebbe una chiave inventata in <c>appsettings.json</c>. La risoluzione dei
/// default vive in <see cref="DatabaseBackupService"/>.</para>
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// Cartella dei dump <b>notturni</b>. Vuota = <c>%USERPROFILE%\ProcioneMGR-Backup</c>, cioè il
    /// default storico del parametro <c>-Destination</c> dello script. Deve stare <b>fuori dal
    /// repository</b>: un dump contiene la master key cifrata, le credenziali exchange e tutto lo
    /// storico. Percorso assoluto: uno relativo si risolverebbe contro la directory di lavoro del
    /// processo, che non è la stessa per l'app e per il Task Scheduler.
    /// </summary>
    public string NightlyDirectory { get; set; } = "";

    /// <summary>
    /// Giorni di conservazione dei dump notturni. La usa <b>lo script</b>, non questa app: la
    /// rotazione avviene solo dopo un backup riuscito e l'ultimo file non si cancella mai,
    /// qualunque età abbia.
    /// </summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Oltre queste ore senza un dump notturno il backup è dichiarato <b>fermo</b> — in questa
    /// pagina e in <c>db-backup.ps1 -Verify</c>, che ne esce con codice 1. 48 e non 24: una notte
    /// saltata per un host spento non è un guasto, due lo sono.
    /// </summary>
    public int StaleAfterHours { get; set; } = 48;

    /// <summary>
    /// Nome dell'operazione pianificata di Windows che esegue il backup notturno. La pagina la
    /// interroga per sapere <i>se esiste, quando ha girato e con che esito</i>: senza questo, la
    /// presenza di un file recente sarebbe l'unico indizio, e un task cancellato resterebbe
    /// invisibile finché i dump non invecchiano.
    /// </summary>
    public string ScheduledTaskName { get; set; } = "ProcioneMGR Backup DB";
}
