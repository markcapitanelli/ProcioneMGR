namespace Procione;

/// <summary>Gravita' di un controllo.</summary>
internal enum Level
{
    /// In ordine.
    Ok,

    /// Funziona ma non come dovrebbe, oppure sta per smettere.
    Warn,

    /// Non funziona.
    Down,

    /// Non previsto in questo assetto (es. i tunnel kubectl quando si gira su Docker Compose).
    /// NON e' un guasto e non conta nel verdetto: dirlo "rosso" insegnerebbe a ignorare il rosso.
    NotApplicable,
}

/// <summary>
/// Un controllo e il suo esito. <paramref name="Fix"/> e' il comando da digitare, non un consiglio
/// generico: la plancia serve a rimettere in piedi le cose, e la distanza fra "sai cos'e' rotto" e
/// "sai cosa battere" e' dove si perdono le ore.
/// </summary>
internal sealed record Check(string Group, string Name, Level Level, string Detail, string? Fix = null);

/// <summary>Quale delle due configurazioni previste sta girando.</summary>
internal enum Layout
{
    Unknown,

    /// Nessuna: la piattaforma e' spenta.
    None,

    /// Windows + cluster kind (l'assetto di sviluppo di questa macchina).
    Kind,

    /// docker compose up -d (l'assetto portatile, con il suo Postgres isolato).
    Compose,

    /// Tutti e due insieme. Non e' una configurazione: e' la violazione della regola 2
    /// (un solo scrittore) in attesa di manifestarsi.
    Both,
}

/// <summary>Un container Docker, come lo racconta <c>docker ps -a</c>.</summary>
internal sealed record Container(string Name, string State, string Status, string Project, string Service);

/// <summary>Un pod, come lo racconta kubectl.</summary>
internal sealed record Pod(string Ns, string Name, string Phase, int Restarts, bool Ready, DateTimeOffset Created)
{
    /// <summary>
    /// L'identita' che conta per un tunnel: NOME + CONTEGGIO RIAVVII.
    ///
    /// Il solo nome e' cieco al riavvio del container DENTRO lo stesso pod (OOM-kill, crash,
    /// liveness fallita): il pod mantiene nome e identita', il port-forward di kubectl muore lo
    /// stesso, e la porta locale resta in ascolto. Successo davvero: tunnel morto da 8 ore verso un
    /// pod con RESTARTS 2, /trading a zero corsie mentre il motore operava (2026-08-05).
    /// </summary>
    public string Identity => $"{Name}#{Restarts}";
}

/// <summary>Un'attivita' pianificata di Windows.</summary>
internal sealed record TaskInfo(string Name, string State, string LastRun, string LastResult);

/// <summary>Una fotografia della piattaforma.</summary>
internal sealed class Snapshot
{
    public required DateTimeOffset Taken { get; init; }
    public required Layout Layout { get; init; }
    public required IReadOnlyList<Check> Checks { get; init; }

    public int Count(Level l) => Checks.Count(c => c.Level == l);

    public Level Worst =>
        Checks.Any(c => c.Level == Level.Down) ? Level.Down :
        Checks.Any(c => c.Level == Level.Warn) ? Level.Warn : Level.Ok;

    /// 0 tutto bene, 1 avvisi, 2 guasti. Cosi' `procione stato` si puo' mettere in uno script.
    public int ExitCode => Worst switch { Level.Down => 2, Level.Warn => 1, _ => 0 };
}
