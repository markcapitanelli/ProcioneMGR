namespace Procione;

/// <summary>Come si ripete un lavoro del supervisore.</summary>
internal enum Cadence
{
    /// Ogni N (dall'ULTIMA esecuzione, non da un'origine fissa).
    Interval,

    /// Una volta al giorno, a un'ora precisa.
    Daily,

    /// Una volta sola, all'avvio del supervisore.
    AtStart,
}

/// <summary>
/// Quando tocca a un lavoro. E' una funzione pura del passato — <b>nessun orologio letto qui
/// dentro</b>, l'istante arriva sempre come parametro — perche' questa e' la meta' del supervisore
/// che puo' sbagliare in silenzio: se il calcolo scivola, un backup salta e nessuno se ne accorge
/// finche' non serve. Con l'orologio fuori, la si punta contro casi noti.
///
/// La regola del RECUPERO e' quella del Task Scheduler di Windows (-StartWhenAvailable), e non per
/// imitazione: un'occorrenza persa perche' il PC era spento non e' un'occorrenza da saltare. Ma si
/// recupera <b>una volta sola</b>: si tiene l'ultima esecuzione, non l'elenco delle mancate, quindi
/// dopo sei notti spente parte un backup, non sei.
/// </summary>
internal sealed record Schedule(Cadence Kind, TimeSpan Every, TimeOnly At)
{
    public static Schedule Ogni(TimeSpan quanto) => new(Cadence.Interval, quanto, default);

    public static Schedule Alle(int ora, int minuto) => new(Cadence.Daily, TimeSpan.Zero, new TimeOnly(ora, minuto));

    public static Schedule SoloAllAvvio() => new(Cadence.AtStart, TimeSpan.Zero, default);

    /// <summary>
    /// Quanto ritardo si tollera prima di dirlo.
    ///
    /// Non e' zero perche' il supervisore ha un solo ciclo: mentre un pg_dump dura un minuto, la
    /// veglia aspetta il suo turno — e chiamarlo «in ritardo» sarebbe un allarme sul funzionamento
    /// normale. Un giro intero perso, invece, e' l'unico segno esterno di un ciclo incastrato.
    /// </summary>
    public TimeSpan Grace => Kind switch
    {
        Cadence.Interval => Every,
        Cadence.Daily => TimeSpan.FromHours(1),
        _ => TimeSpan.MaxValue,
    };

    /// <summary>Come si scrive nel quadro: «ogni 5 minuti», «ogni giorno alle 03:30».</summary>
    public string Describe() => Kind switch
    {
        Cadence.Interval => $"ogni {Ui.Age(Every)}",
        Cadence.Daily => $"ogni giorno alle {At:HH\\:mm}",
        _ => "all'avvio",
    };

    /// <summary>
    /// Quando tocca la PROSSIMA volta, sapendo quando e' toccato l'ultima.
    /// </summary>
    /// <param name="ultima">
    /// Ultima esecuzione nota, oppure <c>null</c> se non se ne sa nulla.
    ///
    /// «Non se ne sa nulla» e «non e' mai stata fatta» sono due cose diverse e vanno distinte dal
    /// chiamante: per il backup l'ultima esecuzione si deduce dal DUMP PIU' RECENTE sul disco (il
    /// dato osservabile, non lo stato dichiarato), e l'assenza totale di dump si passa qui come
    /// <see cref="DateTimeOffset.MinValue"/> — cioe' «infinitamente vecchia», quindi da fare
    /// subito. Passare <c>null</c> significherebbe invece aspettare le 03:30, e una piattaforma
    /// senza nemmeno un backup non deve aspettare la notte.
    /// </param>
    /// <param name="fuso">
    /// Il fuso in cui leggere «le 03:30». E' un parametro, e non <c>TimeZoneInfo.Local</c> letto
    /// qui dentro, per lo stesso motivo per cui l'istante e' un parametro: cosi' il cambio dell'ora
    /// legale si puo' PROVARE, invece di scoprirlo la notte del 25 ottobre.
    /// </param>
    public DateTimeOffset Next(DateTimeOffset? ultima, DateTimeOffset adesso, TimeZoneInfo? fuso = null) => Kind switch
    {
        // All'avvio e basta: fatto una volta, non tocca mai piu'.
        Cadence.AtStart => ultima is null ? adesso : DateTimeOffset.MaxValue,

        // Mai eseguito: subito. Un supervisore che parte e aspetta cinque minuti prima di guardare
        // se la piattaforma e' viva e' un supervisore che arriva tardi proprio al riavvio, che e'
        // quando serve.
        Cadence.Interval => ultima is null ? adesso : ultima.Value + Every,

        _ => ProssimaGiornaliera(ultima, adesso, fuso ?? TimeZoneInfo.Local),
    };

    /// <summary>Vero se il lavoro e' dovuto adesso.</summary>
    public bool IsDue(DateTimeOffset? ultima, DateTimeOffset adesso, TimeZoneInfo? fuso = null)
        => adesso >= Next(ultima, adesso, fuso);

    /// <summary>
    /// La prossima occorrenza giornaliera. Si calcola in un colpo solo (giorno di riferimento +
    /// l'ora, eventualmente +1 giorno) e non a forza di somme: con un'ultima esecuzione
    /// «infinitamente vecchia» un ciclo girerebbe per duemila anni di giorni.
    ///
    /// L'offset del CANDIDATO si chiede al fuso, non si eredita dal riferimento. Ereditarlo sembra
    /// innocuo e non lo e': l'ultimo backup del 24 ottobre porta +02:00, e «il 25 alle 03:30+02:00»
    /// e' in realta' le 02:30 dell'orologio a muro — il dump parte un'ora prima, e l'esecuzione
    /// successiva, registrata con l'offset nuovo, fa ricadere la scadenza dentro la stessa notte:
    /// DUE pg_dump, che e' precisamente cio' che questo supervisore esiste per non fare.
    /// </summary>
    private DateTimeOffset ProssimaGiornaliera(DateTimeOffset? ultima, DateTimeOffset adesso, TimeZoneInfo fuso)
    {
        // Senza storia non si recupera nulla: non sapendo se ieri e' stato fatto, inventarsi che
        // NON lo e' stato significherebbe lanciare un pg_dump ogni volta che si apre la plancia.
        var riferimento = ultima ?? adesso;

        // Un riferimento «infinitamente vecchio» (nessun dump mai esistito) non ha bisogno di
        // aritmetica: e' dovuto, e basta. Il calcolo su DateTimeOffset.MinValue puo' anche uscire
        // dall'intervallo rappresentabile quando il fuso e' a est di Greenwich.
        if (riferimento.Year <= 1) return riferimento;

        var giorno = TimeZoneInfo.ConvertTime(riferimento, fuso).Date;
        var candidato = AMuro(giorno, fuso);
        return candidato <= riferimento ? AMuro(giorno.AddDays(1), fuso) : candidato;
    }

    /// <summary>L'istante in cui, in quel giorno e in quel fuso, l'orologio a muro segna <see cref="At"/>.</summary>
    private DateTimeOffset AMuro(DateTime giorno, TimeZoneInfo fuso)
    {
        var locale = DateTime.SpecifyKind(giorno.Date + At.ToTimeSpan(), DateTimeKind.Unspecified);

        // La notte in cui l'ora avanza, l'orario dichiarato puo' NON ESISTERE (in Italia le 02:30
        // del 29 marzo): si sposta avanti di un'ora, come fa il Task Scheduler. L'occorrenza
        // slitta, non sparisce — saltarla sarebbe una notte senza backup una volta l'anno.
        if (fuso.IsInvalidTime(locale)) locale = locale.AddHours(1);

        // La notte in cui l'ora torna indietro, l'orario esiste DUE volte: si prende la PRIMA
        // (l'offset piu' grande, quello estivo), cosi' il lavoro si fa una volta sola.
        var offset = fuso.IsAmbiguousTime(locale)
            ? fuso.GetAmbiguousTimeOffsets(locale).Max()
            : fuso.GetUtcOffset(locale);

        return new DateTimeOffset(locale, offset);
    }
}
