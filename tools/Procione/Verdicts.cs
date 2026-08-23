namespace Procione;

/// <summary>
/// I verdetti che richiedono un ragionamento, separati dalle sonde che raccolgono i dati.
///
/// Sono funzioni pure: stessi ingressi, stesso esito, nessuna rete. Cosi' si possono puntare contro
/// casi noti — compreso il caso «tutto sano», che deve produrre silenzio. Uno strumento diagnostico
/// che si accende sul normale e' peggio di nessuno strumento: insegna a ignorarlo.
/// </summary>
internal static class Verdicts
{
    /// <summary>
    /// Un tunnel e' buono solo se TUTTE le sue porte sono in ascolto E il pod che stava servendo e'
    /// ancora quello vivo adesso.
    ///
    /// «La porta e' in ascolto» NON basta, ed e' l'errore che questa piattaforma ha gia' pagato:
    /// quando il pod viene sostituito (deploy, OOM-kill, rollout) kubectl resta in ascolto sulla
    /// porta locale mentre il tunnel punta a un pod che non esiste piu'. E nemmeno una sonda di rete
    /// aiuta: una connessione TCP verso un forward morto viene ACCETTATA in locale e muore dopo.
    /// L'unico verdetto deterministico e' il confronto fra il pod annotato nel marcatore e quello
    /// vivo adesso — nessuna inferenza sul comportamento della rete.
    /// </summary>
    /// <param name="marcatore">Contenuto del file marcatore, gia' normalizzato (<see cref="Parsing.Marker"/>).</param>
    public static Check Tunnel(string nome, IReadOnlyList<int> porte, string? marcatore, Pod? podVivo,
                               ISet<int> inAscolto, bool clusterSu, string serve)
    {
        var quante = porte.Count(inAscolto.Contains);
        var elenco = string.Join("+", porte);

        if (!clusterSu)
            return quante > 0
                ? new Check("tunnel", nome, Level.Warn,
                    $"porte {elenco} in ascolto ma il cluster e' giu': il tunnel non porta da nessuna parte",
                    "`procione ferma tunnel`, poi riaprilo quando il cluster torna")
                : new Check("tunnel", nome, Level.NotApplicable, "cluster giu': nessun tunnel possibile");

        if (quante == 0)
            return new Check("tunnel", nome, Level.Down,
                $"porte {elenco} non in ascolto — {serve} non funziona", "`procione ripara tunnel`");

        if (quante < porte.Count)
            // Tunnel a meta': succede col tunnel aperto da una versione precedente dello script, o
            // con un kubectl morente. Si rifa' intero.
            return new Check("tunnel", nome, Level.Warn,
                $"tunnel incompleto: {quante} porte su {porte.Count} ({elenco})", "`procione ripara tunnel`");

        if (podVivo is null)
            return new Check("tunnel", nome, Level.Warn,
                $"porte {elenco} in ascolto ma nessun pod Running a cui puntare",
                "`procione ripara tunnel` dopo che il pod e' tornato su");

        if (marcatore is null)
            return new Check("tunnel", nome, Level.Warn,
                $"porte {elenco} in ascolto, pod servito SCONOSCIUTO (marcatore assente)",
                "`procione ripara tunnel`, cosi' il marcatore torna a dire la verita'");

        if (marcatore != podVivo.Identity)
            return new Check("tunnel", nome, Level.Down,
                $"STANTIO: serviva {marcatore}, ora c'e' {podVivo.Identity}", "`procione ripara tunnel`");

        return new Check("tunnel", nome, Level.Ok, $"{elenco} → {podVivo.Name} (riavvii {podVivo.Restarts})");
    }

    /// <summary>
    /// Il pod a cui un tunnel punta davvero.
    ///
    /// Deve coincidere con la scelta di <c>ensure-trading-portforward.ps1</c>, che prende
    /// <c>.items[0]</c> fra i pod Running del componente — cioe' il PRIMO in ordine di nome, che e'
    /// l'ordine in cui l'API server restituisce le liste. Durante un rollout ne esistono due, e se
    /// la plancia ne scegliesse un altro griderebbe STANTIO su un tunnel sano.
    ///
    /// Era gia' cosi', ma per caso: dipendeva dal fatto che <c>FirstOrDefault</c> incontrasse le
    /// righe nell'ordine di kubectl. Qui l'ordinamento e' scritto, cosi' il legame con lo script
    /// e' visibile a chi lo tocchera'.
    /// </summary>
    public static Pod? TunnelPod(IEnumerable<Pod> pods, string ns) =>
        pods.Where(p => p.Ns == ns && p.Phase == "Running")
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// Quale assetto sta girando. «Tutti e due» non e' una configurazione: e' la violazione della
    /// regola 2 (un solo scrittore) in attesa di manifestarsi, e va detta in rosso.
    /// </summary>
    public static Layout Which(bool dockerVivo, bool nodoKindSu, bool guscioComposeSu) => (nodoKindSu, guscioComposeSu) switch
    {
        (true, true) => Layout.Both,
        (true, false) => Layout.Kind,
        (false, true) => Layout.Compose,
        _ => dockerVivo ? Layout.None : Layout.Unknown,
    };

    // =============================================================================================
    //  Supervisore e lavori
    // =============================================================================================

    /// <summary>
    /// Il battito e' fresco? E' l'unica prova che un supervisore sta davvero girando: il PID si
    /// riusa, e un processo puo' essere vivo e piantato.
    /// </summary>
    public static bool HeartbeatFresh(DateTimeOffset battito, DateTimeOffset adesso, TimeSpan tetto)
        => battito > DateTimeOffset.MinValue && adesso - battito <= tetto && battito - adesso <= tetto;

    /// <summary>
    /// Il supervisore c'e' o no, e cosa cambia se non c'e'.
    /// </summary>
    /// <param name="lavoriCopertiDaTask">
    /// I lavori che, in assenza del supervisore, stanno comunque girando dalle vecchie attivita' di
    /// Windows.
    ///
    /// Senza questa distinzione il verdetto MENTIREBBE: su una macchina non ancora migrata,
    /// «veglia e backup NON stanno girando» e' semplicemente falso — girano, con la loro finestra
    /// PowerShell davanti all'utente, ed e' l'unica cosa che sorveglia la piattaforma. Un rosso su
    /// una cosa che funziona e' il modo piu' rapido di rendere inutile un quadro.
    /// </param>
    public static Check Supervisore(SupervisorState? stato, bool vivo, bool inQuestoProcesso,
                                    bool attivitaRegistrata, IReadOnlyCollection<string> lavoriCopertiDaTask,
                                    DateTimeOffset adesso)
    {
        if (vivo && stato is not null)
        {
            var quanti = stato.Jobs.Count(j => j.Enabled);
            var dove = inQuestoProcesso ? "in questa finestra" : $"pid {stato.Pid}";
            var eta = Ui.Age(adesso - stato.Started);
            // Il repository conta: un supervisore che comanda un worktree legge un appsettings.json
            // fotografato alla nascita di quel worktree e mai piu' aggiornato — sei notti di backup
            // perse, 2026-08-17.
            var repo = stato.Repo == Platform.RepoRoot ? "" : $"  ⚠ su {stato.Repo}";
            return new Check("supervisore", "Supervisore", Level.Ok,
                             $"attivo da {eta} ({dove}) — {quanti} lavori accesi{repo}");
        }

        // Il battito azzerato e' la FIRMA di un'uscita ordinata: il supervisore lo mette a
        // DateTimeOffset.MinValue proprio uscendo. Un battito vero e vecchio significa invece che
        // e' morto senza passare dal finally. Sono due cose diverse, e mandare a cercare un crash
        // che non c'e' stato — ogni volta che si ferma il servizio di proposito — e' un modo di
        // affermare cio' che non si e' misurato.
        var morto = stato is null
            ? "non attivo"
            : stato.Heartbeat > DateTimeOffset.MinValue
                ? $"fermo — l'ultimo (pid {stato.Pid}) e' morto senza chiudere"
                : $"fermo — l'ultimo (pid {stato.Pid}) e' stato fermato";

        // Le automazioni ci sono ancora, solo fuori di qui: e' un avviso, non un guasto.
        if (lavoriCopertiDaTask.Count > 0)
            return new Check("supervisore", "Supervisore", Level.Warn,
                $"{morto} — {string.Join(" e ", lavoriCopertiDaTask)} girano ancora dal Task Scheduler, con le loro finestre",
                "`procione attivita migra` porta tutto qui dentro, una volta per tutte");

        return new Check("supervisore", "Supervisore", Level.Down,
            $"{morto}: NESSUNO sta vegliando sulla piattaforma",
            attivitaRegistrata
                ? "`procione servizio` (l'attivita' al logon c'e' gia': basta rientrare)"
                : "`procione attivita migra` — porta le automazioni dentro la plancia, una volta per tutte");
    }

    /// <summary>
    /// Come sta andando un lavoro. Le tre domande, in ordine: e' acceso, l'ultimo giro e' andato
    /// bene, e il prossimo e' in orario.
    /// </summary>
    /// <param name="supervisoreVivo">
    /// Se non c'e' nessun supervisore, tutti i lavori sono fermi per UNA ragione sola, gia' detta
    /// nella riga sopra. Ripeterla su ogni lavoro accenderebbe tre rossi per un guasto solo — ed e'
    /// cosi' che si insegna a non guardare i rossi.
    /// </param>
    /// <param name="copertoDaTask">
    /// Questo lavoro sta girando dalla vecchia attivita' di Windows. Senza supervisore non e'
    /// «fermo»: e' altrove, e dirlo fermo sarebbe falso.
    /// </param>
    /// <param name="acceso">
    /// Se il lavoro e' acceso ADESSO, secondo le preferenze. Non si legge dallo stato del
    /// supervisore: quello e' una fotografia, e resta fermo finche' un supervisore non riparte —
    /// cosi' `procione lavoro avvio accendi` sembrerebbe non aver fatto niente, e il rimedio
    /// suggerito sarebbe il comando appena eseguito.
    /// </param>
    public static Check Job(Job job, JobState? st, bool supervisoreVivo, DateTimeOffset adesso,
                            bool copertoDaTask = false, bool? acceso = null)
    {
        var ultimo = st?.LastRun;
        // «Infinitamente vecchio» e' il valore con cui il supervisore dice «nessun dump e' mai
        // esistito»: e' una data, non un'esecuzione, e trattarla come tale produce «ultimo 739870g
        // fa» e «IN RITARDO di 739870g».
        var mai = ultimo is null or { Year: <= 1 };
        var quando = mai
            ? "mai eseguito"
            : $"ultimo {Ui.Age(adesso - ultimo!.Value)} fa in {Ui.Age(TimeSpan.FromSeconds(st!.LastElapsedSeconds))}";

        if (!(acceso ?? st?.Enabled ?? job.EnabledByDefault))
            return new Check("supervisore", job.Name, Level.NotApplicable,
                             $"spento — {job.What}", $"`procione lavoro {job.Name} accendi`");

        if (!supervisoreVivo)
            return new Check("supervisore", job.Name, Level.NotApplicable,
                copertoDaTask
                    ? $"{job.When.Describe()} — gira ancora dal Task Scheduler, fuori dalla plancia"
                    : $"{job.When.Describe()} — fermo, {quando}");

        var prossimo = job.When.Next(ultimo, adesso);
        var fra = prossimo == DateTimeOffset.MaxValue
            ? "non si ripete"
            : prossimo <= adesso ? "adesso" : $"fra {Ui.Age(prossimo - adesso)}";

        // Fallito, oppure rimasto a meta'. Un fallimento isolato capita (il cluster stava
        // ripartendo); tre di fila sono un guasto, e la differenza deve vedersi — un giallo che non
        // diventa mai rosso non sposta nessuno.
        if (st is not null && st.LastCode != 0)
            return new Check("supervisore", job.Name,
                st.ConsecutiveFailures >= 3 ? Level.Down : Level.Warn,
                $"{Supervisor.Diagnosi(st.LastCode)} " +
                $"({st.ConsecutiveFailures} di fila) — {st.LastSummary}",
                $"`procione log supervisore` per il testo intero, `procione lavoro {job.Name} ora` per riprovare");

        // Mai eseguito, ma dovuto adesso: e' il primo giro, non un ritardo. Dirlo «IN RITARDO di
        // 739870 giorni» sarebbe vero solo nell'aritmetica.
        if (mai)
            return new Check("supervisore", job.Name, Level.Ok,
                $"{job.When.Describe()} — mai eseguito, {(prossimo <= adesso ? "in partenza adesso" : $"il primo fra {Ui.Age(prossimo - adesso)}")}");

        // In ritardo: il lavoro e' acceso, il supervisore batte, ma la scadenza e' passata da un
        // pezzo. Vuol dire che il ciclo e' occupato o incastrato, ed e' l'unico modo di accorgersene
        // dall'esterno.
        if (prossimo != DateTimeOffset.MaxValue && adesso - prossimo > job.When.Grace)
            return new Check("supervisore", job.Name, Level.Warn,
                $"IN RITARDO di {Ui.Age(adesso - prossimo)} — {job.When.Describe()}, {quando}",
                "il supervisore e' occupato o incastrato: `procione log supervisore`");

        return new Check("supervisore", job.Name, Level.Ok,
                         $"{job.When.Describe()} — {quando}, prossimo {fra}");
    }

    /// <summary>
    /// Un'automazione vecchia rimasta nel Task Scheduler.
    ///
    /// Il verdetto dipende da chi altro sta facendo lo stesso lavoro: finche' il supervisore non
    /// c'e', quel task e' l'unica cosa che veglia sulla piattaforma e va giudicato sul suo esito.
    /// Quando il supervisore c'e', lo stesso task e' un DOPPIONE — due pg_dump nella stessa notte,
    /// due watchdog che si contendono la riparazione del tunnel, e una finestra PowerShell che
    /// salta davanti all'utente ogni cinque minuti.
    /// </summary>
    /// <param name="supervisorePrende">
    /// Un supervisore sta girando ADESSO. Non basta che la sua attivita' sia registrata: fra la
    /// migrazione e il logon successivo il supervisore non c'e' ancora, e in quella finestra il
    /// task vecchio e' l'unica cosa che veglia sulla piattaforma. Chiamarlo doppione li' sarebbe
    /// consigliare di togliere l'unica sorveglianza rimasta — e contraddirebbe, nella stessa
    /// schermata, la riga del supervisore.
    /// </param>
    /// <param name="planciaRegistrata">
    /// L'attivita' della plancia c'e' gia'. Con il supervisore ancora fermo non e' un doppione, ma
    /// lo diventera' al prossimo logon: si dice, senza affermare che stia gia' succedendo.
    /// </param>
    public static Check LegacyTask(string task, string era, string stato, string quando,
                                   bool esitoBuono, string esito, bool supervisorePrende,
                                   bool planciaRegistrata = false)
    {
        var etichetta = task.Replace("ProcioneMGR ", "");

        if (supervisorePrende)
            return new Check("automazioni", etichetta, Level.Warn,
                $"DOPPIONE: {era} gira anche dal Task Scheduler, e apre la sua finestra",
                "`procione attivita migra` la toglie (il supervisore fa gia' lo stesso lavoro)");

        if (planciaRegistrata)
            return new Check("automazioni", etichetta, Level.Warn,
                $"ancora registrata: {era}. Diventera' un doppione al prossimo logon, quando partira' la plancia",
                "`procione attivita migra` (la migrazione e' rimasta a meta': serve una shell elevata)");

        if (!esitoBuono)
            return new Check("automazioni", etichetta, Level.Warn,
                $"{stato.ToLowerInvariant()}, {quando}, ultimo esito {esito}",
                "`procione attivita migra` porta il lavoro dentro la plancia, dove l'esito si vede");

        return new Check("automazioni", etichetta, stato == "Disabled" ? Level.Warn : Level.Ok,
            $"{stato.ToLowerInvariant()}, {quando} — fuori dalla plancia",
            "`procione attivita migra` per portarla dentro (una finestra in meno)");
    }

    // =============================================================================================
    //  Backup
    // =============================================================================================

    /// <summary>
    /// La freschezza dei dump, giudicata sul DATO OSSERVABILE — i file sul disco — e non sul fatto
    /// che un'automazione dica di averli fatti. E' la distinzione che il 2026-08-17 e' costata sei
    /// notti: il task usciva 1 ogni notte e nessuno leggeva quel codice, mentre la cartella lo
    /// diceva a chiunque la guardasse.
    /// </summary>
    /// <param name="soglia">Oltre questa eta' almeno un giro notturno e' saltato.</param>
    public static Check Backup(bool cartellaEsiste, int quanti, DateTimeOffset? ultimo, long dimensione,
                               DateTimeOffset adesso, TimeSpan soglia)
    {
        if (!cartellaEsiste)
            return new Check("automazioni", "Backup", Level.Warn,
                $"cartella {Platform.BackupDir} inesistente: nessun dump e' mai stato fatto", "`procione backup`");

        if (quanti == 0 || ultimo is null)
            return new Check("automazioni", "Backup", Level.Warn, "nessun dump presente", "`procione backup`");

        var eta = adesso - ultimo.Value;
        var dettaglio = $"{quanti} dump, l'ultimo {Ui.Age(eta)} fa ({Ui.Size(dimensione)})";
        return eta > soglia
            ? new Check("automazioni", "Backup", Level.Warn, dettaglio + " — un giro notturno e' saltato", "`procione backup`")
            : new Check("automazioni", "Backup", Level.Ok, dettaglio);
    }
}
