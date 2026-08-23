using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prove del supervisore della plancia (`procione servizio`), cioe' della parte che ha preso il
/// posto delle attivita' pianificate di Windows.
///
/// Si prova SOLO cio' che e' puro: il calcolo delle scadenze e i verdetti. E' il punto in cui
/// questo cambiamento puo' peggiorare le cose invece di migliorarle — un errore di aritmetica
/// nella cadenza non si vede, non lancia, non lascia traccia: semplicemente il backup non parte,
/// e ce ne si accorge il giorno in cui serve. E' gia' successo, in una forma diversa: il dump
/// notturno ha fallito SEI notti di fila (2026-08-17) perche' nessuno leggeva il codice d'uscita
/// del task.
///
/// L'orologio non viene mai letto qui dentro ne' dentro <see cref="Schedule"/>: l'istante e'
/// sempre un parametro. E' quello che rende provabile «la macchina e' stata spenta sei notti».
/// </summary>
public class ProcioneSupervisorTests
{
    private static DateTimeOffset T(int giorno, int ora, int minuto = 0)
        => new(new DateTime(2026, 8, giorno, ora, minuto, 0), TimeSpan.FromHours(2));

    // =============================================================================================
    //  Cadenza a intervallo — la veglia, ogni 5 minuti
    // =============================================================================================

    [Fact]
    public void Intervallo_mai_eseguito_e_dovuto_SUBITO()
    {
        // Un supervisore che parte e aspetta cinque minuti prima di guardare se la piattaforma e'
        // viva arriva tardi proprio al riavvio, che e' quando serve.
        var s = Schedule.Ogni(TimeSpan.FromMinutes(5));

        Assert.Equal(T(20, 9), s.Next(null, T(20, 9)));
        Assert.True(s.IsDue(null, T(20, 9)));
    }

    [Fact]
    public void Intervallo_appena_eseguito_non_e_dovuto()
    {
        var s = Schedule.Ogni(TimeSpan.FromMinutes(5));

        Assert.Equal(T(20, 9, 5), s.Next(T(20, 9), T(20, 9, 1)));
        Assert.False(s.IsDue(T(20, 9), T(20, 9, 1)));
        Assert.False(s.IsDue(T(20, 9), T(20, 9, 4)));
        Assert.True(s.IsDue(T(20, 9), T(20, 9, 5)));
    }

    [Fact]
    public void Intervallo_dopo_una_lunga_assenza_recupera_UNA_volta_sola()
    {
        // Il PC e' stato spento cinque ore. Senza questa proprieta' il supervisore, al ritorno,
        // sparerebbe sessanta giri di veglia di fila per «recuperare» le occorrenze perse.
        var s = Schedule.Ogni(TimeSpan.FromMinutes(5));
        var ultima = T(20, 4);

        Assert.True(s.IsDue(ultima, T(20, 9)));

        // Un giro, e si e' di nuovo in pari: si tiene l'ULTIMA esecuzione, non l'elenco delle
        // mancate.
        Assert.False(s.IsDue(T(20, 9), T(20, 9, 1)));
    }

    // =============================================================================================
    //  Cadenza giornaliera — il backup, alle 03:30
    // =============================================================================================

    // Il fuso si passa SEMPRE esplicitamente nei test della cadenza giornaliera. Non e' pignoleria:
    // «le 03:30» sono un orario a muro, quindi dipendono dal fuso, e T() costruisce istanti a
    // +02:00 (l'ora italiana d'agosto). Lasciando decidere a TimeZoneInfo.Local questi test
    // passerebbero su questa macchina e cadrebbero sulla CI, che gira in UTC — cosa puntualmente
    // successa alla prima esecuzione. Un test che dipende dal fuso della macchina prova il fuso,
    // non il codice.

    [Fact]
    public void Giornaliera_dopo_l_esecuzione_di_stanotte_tocca_domani()
    {
        // Il caso normale, quello che deve restare MUTO: stanotte alle 03:31 il dump c'e' stato.
        var s = Schedule.Alle(3, 30);

        Assert.Equal(T(21, 3, 30), s.Next(T(20, 3, 31), T(20, 10), Roma));
        Assert.False(s.IsDue(T(20, 3, 31), T(20, 10), Roma));
    }

    [Fact]
    public void Giornaliera_con_l_occorrenza_di_stanotte_SALTATA_e_dovuta_adesso()
    {
        // L'ultima e' di ieri, le 03:30 di stanotte sono passate e non e' successo niente: e'
        // esattamente il caso delle sei notti perse, e il recupero e' quello che il Task Scheduler
        // fa con -StartWhenAvailable. Aspettare la notte dopo raddoppierebbe il buco.
        var s = Schedule.Alle(3, 30);

        Assert.True(s.IsDue(T(19, 3, 31), T(20, 10), Roma));
    }

    [Fact]
    public void Giornaliera_senza_alcuna_storia_NON_recupera()
    {
        // Non sapendo se ieri e' stato fatto, inventarsi che non lo e' stato significherebbe
        // lanciare un pg_dump ogni volta che si apre la plancia.
        var s = Schedule.Alle(3, 30);

        Assert.False(s.IsDue(null, T(20, 10), Roma));
        Assert.Equal(T(21, 3, 30), s.Next(null, T(20, 10), Roma));

        // Prima dell'ora del giorno: la prossima e' oggi, non domani.
        Assert.Equal(T(20, 3, 30), s.Next(null, T(20, 1), Roma));
    }

    [Fact]
    public void Giornaliera_senza_NESSUN_dump_mai_e_dovuta_subito_e_non_esplode()
    {
        // Il supervisore passa DateTimeOffset.MinValue quando la cartella dei backup e' vuota:
        // «infinitamente vecchio», cioe' fallo appena puoi. Il calcolo dev'essere immediato e non
        // deve uscire dall'intervallo rappresentabile (l'aritmetica su MinValue con un fuso a est
        // di Greenwich lancerebbe).
        var s = Schedule.Alle(3, 30);

        var prossimo = s.Next(DateTimeOffset.MinValue, T(20, 10), Roma);

        Assert.True(prossimo < T(20, 10));
        Assert.True(s.IsDue(DateTimeOffset.MinValue, T(20, 10), Roma));
    }

    [Fact]
    public void Giornaliera_e_puntuale_al_minuto()
    {
        var s = Schedule.Alle(3, 30);

        Assert.False(s.IsDue(T(19, 3, 30), T(20, 3, 29), Roma));
        Assert.True(s.IsDue(T(19, 3, 30), T(20, 3, 30), Roma));
    }

    // =============================================================================================
    //  Il cambio dell'ora — la notte in cui un backup diventa due
    // =============================================================================================

    /// <summary>
    /// Il fuso in cui vive la piattaforma. Il nome Windows prima, quello IANA come ripiego: la CI
    /// gira su Linux, e un test che conosce un solo nome di fuso non e' portabile.
    /// </summary>
    private static TimeZoneInfo Roma
    {
        get
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"); }
        }
    }

    [Fact]
    public void La_notte_in_cui_l_ora_torna_indietro_il_backup_si_fa_UNA_volta_sola()
    {
        // 2026-10-25: alle 03:00 CEST l'orologio torna alle 02:00 CET, e la giornata dura 25 ore.
        // Col candidato costruito ereditando l'offset dell'ULTIMA esecuzione (+02:00) succedeva
        // questo: la scadenza «25 alle 03:30+02:00» cade alle 02:30 dell'orologio a muro, il dump
        // parte un'ora prima, viene registrato con l'offset nuovo (+01:00), e la scadenza ricade
        // DENTRO la stessa notte. Due pg_dump, che e' precisamente cio' che il supervisore esiste
        // per non fare.
        var s = Schedule.Alle(3, 30);
        var ultima = new DateTimeOffset(2026, 10, 24, 3, 30, 0, TimeSpan.FromHours(2));

        var prossima = s.Next(ultima, ultima.AddHours(1), Roma);

        // L'orologio a muro deve segnare 03:30, non 02:30: quindi offset invernale.
        Assert.Equal(TimeSpan.FromHours(1), prossima.Offset);
        Assert.Equal(new DateTime(2026, 10, 25, 3, 30, 0), prossima.DateTime);

        // E dopo averla eseguita, la successiva e' il GIORNO DOPO, non ancora quella notte.
        var dopo = s.Next(prossima, prossima.AddMinutes(1), Roma);
        Assert.Equal(new DateTime(2026, 10, 26, 3, 30, 0), dopo.DateTime);
    }

    [Fact]
    public void Un_giro_di_ventiquattro_ore_intorno_al_cambio_d_ora_produce_UN_solo_backup()
    {
        // Il controllo sul comportamento, non sulla formula: si fa girare il ciclo del supervisore
        // minuto per minuto attraverso la notte del cambio, e si contano le esecuzioni.
        var s = Schedule.Alle(3, 30);
        DateTimeOffset? ultima = new DateTimeOffset(2026, 10, 24, 3, 30, 0, TimeSpan.FromHours(2));
        var esecuzioni = new List<DateTimeOffset>();

        var t = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2));
        var fine = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(1));
        while (t < fine)
        {
            if (s.IsDue(ultima, t, Roma)) { esecuzioni.Add(t); ultima = t; }
            t = t.AddMinutes(1);
        }

        // Due giorni, due backup: quello del 25 e quello del 26. Mai due nella stessa notte.
        Assert.Equal(2, esecuzioni.Count);
        Assert.Equal(25, TimeZoneInfo.ConvertTime(esecuzioni[0], Roma).Day);
        Assert.Equal(26, TimeZoneInfo.ConvertTime(esecuzioni[1], Roma).Day);
        Assert.All(esecuzioni, e => Assert.Equal(new TimeOnly(3, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(e, Roma).DateTime)));
    }

    [Fact]
    public void La_notte_in_cui_l_ora_avanza_l_occorrenza_slitta_ma_non_sparisce()
    {
        // 2026-03-29: alle 02:00 l'orologio salta alle 03:00, quindi le 02:30 NON ESISTONO. Un
        // lavoro programmato li' non deve svanire per un anno: slitta, come fa il Task Scheduler.
        var s = Schedule.Alle(2, 30);
        var ultima = new DateTimeOffset(2026, 3, 28, 2, 30, 0, TimeSpan.FromHours(1));

        var prossima = s.Next(ultima, ultima.AddHours(1), Roma);

        Assert.Equal(new DateTime(2026, 3, 29, 3, 30, 0), prossima.DateTime);
        Assert.Equal(TimeSpan.FromHours(2), prossima.Offset);
    }

    [Fact]
    public void AllAvvio_si_fa_una_volta_e_poi_mai_piu()
    {
        var s = Schedule.SoloAllAvvio();

        Assert.True(s.IsDue(null, T(20, 9)));
        Assert.False(s.IsDue(T(20, 9), T(20, 23)));
        Assert.Equal(DateTimeOffset.MaxValue, s.Next(T(20, 9), T(21, 9)));
    }

    // =============================================================================================
    //  La tabella dei lavori — guardiano contro le cadenze che cambiano di nascosto
    // =============================================================================================

    [Fact]
    public void I_lavori_hanno_la_stessa_cadenza_delle_attivita_che_sostituiscono()
    {
        // Questa unificazione ha una promessa precisa: cambia CHI chiama gli script, non quando ne'
        // con quali argomenti. Le cadenze sono copiate dalle definizioni dei task veri
        // (Watchdog: trigger PT5M; Backup DB: giornaliero 03:30, -KeepDays 14).
        var veglia = Jobs.Find("veglia")!;
        Assert.Equal("watchdog.ps1", veglia.Script);
        Assert.Empty(veglia.Args);
        Assert.Equal(TimeSpan.FromMinutes(5), veglia.When.Every);
        Assert.True(veglia.EnabledByDefault);

        var backup = Jobs.Find("backup")!;
        Assert.Equal("db-backup.ps1", backup.Script);
        Assert.Contains("-KeepDays", backup.Args);
        Assert.Contains("14", backup.Args);
        Assert.Equal(new TimeOnly(3, 30), backup.When.At);

        // Il bring-up nasce SPENTO: dura minuti e tocca cluster e tunnel, e non e' cio' che ci si
        // aspetta aprendo una console per guardare uno stato. La migrazione lo accende solo se
        // toglie un bring-up al logon che c'era gia'.
        Assert.False(Jobs.Find("avvio")!.EnabledByDefault);

        // Nessun lavoro senza tetto di attesa: uno script appeso bloccherebbe il ciclo per sempre,
        // e un supervisore fermo e' indistinguibile da uno che non trova nulla di rotto.
        Assert.All(Jobs.All, j => Assert.True(j.Timeout > TimeSpan.Zero));
    }

    // =============================================================================================
    //  Il battito — l'unica prova che il supervisore stia davvero girando
    // =============================================================================================

    [Fact]
    public void Il_battito_prova_la_vita_solo_finche_e_fresco()
    {
        var tetto = TimeSpan.FromMinutes(65);

        Assert.True(Verdicts.HeartbeatFresh(T(20, 9, 30), T(20, 10), tetto));
        Assert.False(Verdicts.HeartbeatFresh(T(20, 8), T(20, 10), tetto));

        // Battito azzerato: e' cosi' che il supervisore dichiara di essersi fermato uscendo. Un
        // file lasciato con l'ultimo battito fresco farebbe credere per un'ora che le automazioni
        // stiano girando.
        Assert.False(Verdicts.HeartbeatFresh(DateTimeOffset.MinValue, T(20, 10), tetto));

        // Battito nel FUTURO oltre il tetto: orologio spostato all'indietro, oppure un file di
        // un'altra macchina. Non e' una prova di vita, e' un'anomalia.
        Assert.False(Verdicts.HeartbeatFresh(T(20, 20), T(20, 10), tetto));
    }

    // =============================================================================================
    //  Verdetti sui lavori
    // =============================================================================================

    private static JobState Andato(DateTimeOffset quando, int codice = 0, int diFila = 0) => new()
    {
        Name = "veglia",
        LastRun = quando,
        LastElapsedSeconds = 3,
        LastCode = codice,
        LastSummary = codice == 0 ? "Watchdog : backup   OK" : "pg_dump: autenticazione fallita",
        ConsecutiveFailures = diFila,
        Enabled = true,
    };

    private static Job Veglia => Jobs.Find("veglia")!;

    [Fact]
    public void Un_lavoro_in_orario_e_riuscito_non_accende_niente()
    {
        var c = Verdicts.Job(Veglia, Andato(T(20, 9, 58)), supervisoreVivo: true, T(20, 10));

        Assert.Equal("Ok", c.Level.ToString());
        Assert.Null(c.Fix);
    }

    [Fact]
    public void Senza_supervisore_i_lavori_non_accendono_un_rosso_a_testa()
    {
        // Tre lavori fermi per UNA ragione sola, gia' detta nella riga del supervisore. Tre rossi
        // per un guasto solo e' il modo di insegnare a non guardare i rossi.
        foreach (var job in Jobs.All)
        {
            var c = Verdicts.Job(job, Andato(T(20, 4)), supervisoreVivo: false, T(20, 10));
            Assert.Equal("NotApplicable", c.Level.ToString());
        }
    }

    [Fact]
    public void Un_lavoro_che_gira_ancora_dal_Task_Scheduler_non_e_FERMO()
    {
        // Su una macchina non ancora migrata la veglia gira eccome — con la sua finestra davanti
        // all'utente. Dirla «ferma» sarebbe falso, ed e' precisamente la classe di difetto che
        // questa plancia esiste per non avere: un controllo che afferma cio' che non ha misurato.
        var c = Verdicts.Job(Veglia, Andato(T(20, 4)), supervisoreVivo: false, T(20, 10), copertoDaTask: true);

        Assert.Equal("NotApplicable", c.Level.ToString());
        Assert.Contains("Task Scheduler", c.Detail);
        Assert.DoesNotContain("fermo", c.Detail);
    }

    [Fact]
    public void Senza_supervisore_ma_coi_task_vecchi_vivi_il_verdetto_e_un_AVVISO_non_un_guasto()
    {
        var conTask = Verdicts.Supervisore(null, vivo: false, inQuestoProcesso: false,
                                           attivitaRegistrata: false, ["veglia", "backup"], T(20, 10));
        Assert.Equal("Warn", conTask.Level.ToString());
        Assert.Contains("Task Scheduler", conTask.Detail);

        // Nessun supervisore E nessun task: allora si', nessuno sta vegliando.
        var nulla = Verdicts.Supervisore(null, vivo: false, inQuestoProcesso: false,
                                         attivitaRegistrata: false, [], T(20, 10));
        Assert.Equal("Down", nulla.Level.ToString());
        Assert.Contains("NESSUNO", nulla.Detail);
    }

    [Fact]
    public void Un_lavoro_spento_e_dichiarato_spento_non_guasto()
    {
        var spento = Andato(T(20, 9, 58));
        spento.Enabled = false;

        var c = Verdicts.Job(Veglia, spento, supervisoreVivo: true, T(20, 10));

        Assert.Equal("NotApplicable", c.Level.ToString());
        Assert.Contains("spento", c.Detail);
    }

    [Theory]
    [InlineData(1, "Warn")]   // capita: il cluster stava ripartendo
    [InlineData(2, "Warn")]
    [InlineData(3, "Down")]   // tre di fila non e' piu' un caso
    [InlineData(9, "Down")]
    public void I_fallimenti_diventano_un_guasto_solo_quando_insistono(int diFila, string atteso)
    {
        var c = Verdicts.Job(Veglia, Andato(T(20, 9, 58), codice: 1, diFila), supervisoreVivo: true, T(20, 10));

        Assert.Equal(atteso, c.Level.ToString());
        // Il rimedio dev'essere il comando da battere, non un consiglio.
        Assert.Contains("procione log supervisore", c.Fix);
    }

    [Fact]
    public void Il_tempo_scaduto_si_distingue_da_un_errore_dello_script()
    {
        // Sono due guasti diversi: uno e' lo script che si lamenta, l'altro e' lo script che non
        // torna piu'. Confonderli manda a cercare nel posto sbagliato.
        var c = Verdicts.Job(Veglia, Andato(T(20, 9, 58), codice: -2), supervisoreVivo: true, T(20, 10));

        Assert.Contains("TEMPO SCADUTO", c.Detail);
    }

    [Fact]
    public void Un_lavoro_in_forte_ritardo_dice_di_esserlo()
    {
        // Il supervisore batte, il lavoro e' acceso, ma la scadenza e' passata da mezz'ora: il
        // ciclo e' occupato o incastrato. E' l'unico modo di accorgersene da fuori.
        var c = Verdicts.Job(Veglia, Andato(T(20, 9)), supervisoreVivo: true, T(20, 9, 40));

        Assert.Equal("Warn", c.Level.ToString());
        Assert.Contains("IN RITARDO", c.Detail);
    }

    [Fact]
    public void Un_ritardo_entro_la_grazia_resta_MUTO()
    {
        // Il supervisore ha un ciclo solo: mentre un pg_dump dura un minuto, la veglia aspetta il
        // suo turno. Chiamarlo «in ritardo» sarebbe un allarme sul funzionamento normale.
        var c = Verdicts.Job(Veglia, Andato(T(20, 9)), supervisoreVivo: true, T(20, 9, 9));

        Assert.Equal("Ok", c.Level.ToString());
        Assert.DoesNotContain("RITARDO", c.Detail);
    }

    [Fact]
    public void Un_lavoro_MAI_eseguito_non_e_in_ritardo_di_settecentomila_giorni()
    {
        // «Nessun dump e' mai esistito» il supervisore lo dice con DateTimeOffset.MinValue, che e'
        // una data e non un'esecuzione: letta come tale produceva «ultimo 739870g fa» e «IN RITARDO
        // di 739870g — il supervisore e' occupato o incastrato», mentre il primo dump stava
        // partendo regolarmente. Un numero assurdo in un quadro e' un quadro che si smette di
        // leggere.
        var backup = Jobs.Find("backup")!;
        var mai = new JobState { Name = "backup", LastRun = DateTimeOffset.MinValue, Enabled = true };

        var c = Verdicts.Job(backup, mai, supervisoreVivo: true, T(20, 10));

        Assert.Equal("Ok", c.Level.ToString());
        Assert.Contains("mai eseguito", c.Detail);
        Assert.DoesNotContain("RITARDO", c.Detail);
        Assert.DoesNotContain("739", c.Detail);
    }

    [Fact]
    public void Un_lavoro_rimasto_a_META_si_distingue_da_uno_fallito()
    {
        // Il supervisore ucciso durante un pg_dump non passa mai da Registra. Senza questo stato,
        // il giro dopo troverebbe solo un dump troncato sul disco — piu' recente di ogni altro,
        // quindi preso per buono — e il recupero dell'occorrenza persa sparirebbe in silenzio.
        var interrotto = new JobState
        {
            Name = "backup",
            LastRun = T(19, 3, 31),
            LastCode = Supervisor.Interrotto,
            LastSummary = "partito 2026-08-20 03:30, mai concluso",
            ConsecutiveFailures = 1,
            Enabled = true,
        };

        var c = Verdicts.Job(Jobs.Find("backup")!, interrotto, supervisoreVivo: true, T(20, 10));

        Assert.Equal("Warn", c.Level.ToString());
        Assert.Contains("INTERROTTO", c.Detail);
        Assert.NotEqual(Supervisor.Diagnosi(0), Supervisor.Diagnosi(Supervisor.Interrotto));
    }

    [Fact]
    public void Un_lavoro_che_sta_girando_ADESSO_lo_dice()
    {
        // Il bring-up dura minuti. Per tutti quei minuti il quadro diceva «mai eseguito, in
        // partenza adesso»: taceva sull'unica cosa che stava succedendo davvero, pur avendone il
        // dato in mano (RunningSince serve gia' a riconoscere le esecuzioni interrotte).
        var inCorso = new JobState { Name = "avvio", Enabled = true, RunningSince = T(20, 9, 55) };

        var c = Verdicts.Job(Jobs.Find("avvio")!, inCorso, supervisoreVivo: true, T(20, 10));

        Assert.Equal("Ok", c.Level.ToString());
        Assert.Contains("IN CORSO da 5m", c.Detail);
        Assert.DoesNotContain("mai eseguito", c.Detail);
    }

    [Fact]
    public void Un_lavoro_in_corso_SENZA_supervisore_non_e_in_corso()
    {
        // Il file di stato conserva RunningSince finche' un supervisore non riparte e lo legge come
        // «interrotto». Nel frattempo, dire «in corso» sarebbe affermare che qualcosa sta girando
        // mentre non gira niente.
        var inCorso = new JobState { Name = "avvio", Enabled = true, RunningSince = T(20, 9, 55) };

        var c = Verdicts.Job(Jobs.Find("avvio")!, inCorso, supervisoreVivo: false, T(20, 10));

        Assert.Equal("NotApplicable", c.Level.ToString());
        Assert.DoesNotContain("IN CORSO", c.Detail);
    }

    [Fact]
    public void Acceso_e_spento_si_leggono_dalle_PREFERENZE_non_dalla_fotografia()
    {
        // `procione lavoro avvio accendi` scrive le preferenze, ma lo stato del supervisore resta
        // com'era finche' un supervisore non riparte. Leggendo di li', il quadro rispondeva «spento
        // — rimedio: `procione lavoro avvio accendi`», cioe' il comando appena eseguito.
        var fotografiaVecchia = new JobState { Name = "avvio", Enabled = false, LastRun = T(20, 9) };

        var conPreferenza = Verdicts.Job(Jobs.Find("avvio")!, fotografiaVecchia,
                                         supervisoreVivo: true, T(20, 10), acceso: true);
        Assert.NotEqual("NotApplicable", conPreferenza.Level.ToString());
        Assert.DoesNotContain("spento", conPreferenza.Detail);

        // E il contrario: preferenza spenta, fotografia accesa.
        var fotografiaAccesa = new JobState { Name = "avvio", Enabled = true, LastRun = T(20, 9) };
        var conPreferenzaSpenta = Verdicts.Job(Jobs.Find("avvio")!, fotografiaAccesa,
                                               supervisoreVivo: true, T(20, 10), acceso: false);
        Assert.Equal("NotApplicable", conPreferenzaSpenta.Level.ToString());
        Assert.Contains("spento", conPreferenzaSpenta.Detail);
    }

    [Fact]
    public void Un_arresto_PULITO_non_si_racconta_come_una_morte()
    {
        // Il battito azzerato e' la firma di un'uscita ordinata — lo scrive il supervisore stesso
        // uscendo. Chiamarla «morto senza chiudere» manderebbe a cercare un crash che non c'e'
        // stato, ogni volta che si ferma il servizio di proposito (per esempio per ricompilare).
        var fermato = new SupervisorState { Pid = 1234, Heartbeat = DateTimeOffset.MinValue };
        var pulito = Verdicts.Supervisore(fermato, vivo: false, inQuestoProcesso: false,
                                          attivitaRegistrata: true, [], T(20, 10));
        Assert.Contains("e' stato fermato", pulito.Detail);
        Assert.DoesNotContain("senza chiudere", pulito.Detail);

        var morto = new SupervisorState { Pid = 1234, Heartbeat = T(20, 4) };
        var brutale = Verdicts.Supervisore(morto, vivo: false, inQuestoProcesso: false,
                                           attivitaRegistrata: true, [], T(20, 10));
        Assert.Contains("senza chiudere", brutale.Detail);
    }

    // =============================================================================================
    //  Le automazioni vecchie: doppione o unica sorveglianza?
    // =============================================================================================

    // =============================================================================================
    //  Worktree: uno scratch, non una fonte di verita'
    // =============================================================================================

    [Theory]
    [InlineData(@"C:\Users\proci\Desktop\ProgettoP", false)]
    [InlineData(@"C:\Users\proci\Desktop\ProgettoP\.claude\worktrees\sleepy-lovelace-286898", true)]
    // Il confronto e' insensibile alle maiuscole: i percorsi Windows arrivano come capita.
    [InlineData(@"C:\Users\proci\Desktop\ProgettoP\.Claude\Worktrees\x", true)]
    [InlineData(@"C:\qualcosa\senza\niente", false)]
    [InlineData(null, false)]
    public void InWorktree_riconosce_una_cartella_usa_e_getta(string? percorso, bool atteso)
        => Assert.Equal(atteso, Platform.InWorktree(percorso));

    [Fact]
    public void MainRepoRoot_taglia_il_worktree_e_restituisce_il_repo_vero()
    {
        // La stessa funzione di Get-MainRepoRoot in db-backup.ps1, e per la stessa ragione: un
        // worktree e' uno scratch. Ci si registra un'attivita' pianificata e muore col `git
        // worktree remove`; ci si avvia il guscio e legge un appsettings.json fermo al giorno in
        // cui il worktree e' nato.
        //
        // Non si prova il valore assoluto di MainRepoRoot (dipende da dove gira la suite), ma
        // l'INVARIANTE che conta: qualunque cosa sia, non e' dentro un worktree.
        Assert.False(Platform.InWorktree(Platform.MainRepoRoot));
    }

    [Fact]
    public void Un_task_DISABILITATO_non_conta_come_sorveglianza()
    {
        // Disabilitare un'attivita' dal Task Scheduler, invece di cancellarla, e' il gesto naturale
        // di chi vuole fermarla. Contarla come «sta girando» declasserebbe un guasto ad avviso e
        // affermerebbe un fatto mai misurato: «veglia e backup girano ancora dal Task Scheduler»
        // mentre non gira nulla.
        List<TaskInfo> lette =
        [
            new("ProcioneMGR Watchdog", "Disabled", "2026-08-20 09:55", "0"),
            new("ProcioneMGR Backup DB", "Ready", "2026-08-20 03:30", "0"),
        ];

        Assert.True(Tasks.Exists(lette, "ProcioneMGR Watchdog"));
        Assert.False(Tasks.Active(lette, "ProcioneMGR Watchdog"));
        Assert.True(Tasks.Active(lette, "ProcioneMGR Backup DB"));

        // Stato sconosciuto: nel dubbio si dichiara SCOPERTO, non coperto.
        Assert.False(Tasks.Active([new("X", "Unknown", "", "")], "X"));
    }

    [Fact]
    public void Fra_la_migrazione_e_il_logon_il_task_vecchio_NON_e_un_doppione()
    {
        // La «ProcioneMGR Plancia» e' registrata ma il supervisore parte al prossimo logon: in
        // quella finestra il task vecchio e' l'unica cosa che veglia sulla piattaforma, e
        // consigliare di toglierlo contraddirebbe, nella stessa schermata, la riga del supervisore.
        var c = Verdicts.LegacyTask("ProcioneMGR Watchdog", "watchdog.ps1 ogni 5 minuti",
                                    "Ready", "ultima 2026-08-20 09:55", esitoBuono: true, "0x00000000",
                                    supervisorePrende: false, planciaRegistrata: true);

        Assert.Equal("Warn", c.Level.ToString());
        Assert.DoesNotContain("DOPPIONE", c.Detail);
        Assert.Contains("prossimo logon", c.Detail);
    }

    [Fact]
    public void Un_task_vecchio_col_supervisore_acceso_e_un_DOPPIONE()
    {
        // Due watchdog che si contendono la riparazione del tunnel, due pg_dump nella stessa
        // notte, e la finestra PowerShell che torna a saltare davanti all'utente.
        var c = Verdicts.LegacyTask("ProcioneMGR Watchdog", "watchdog.ps1 ogni 5 minuti",
                                    "Ready", "ultima 2026-08-20 09:55", esitoBuono: true, "0x00000000",
                                    supervisorePrende: true);

        Assert.Equal("Warn", c.Level.ToString());
        Assert.Contains("DOPPIONE", c.Detail);
        Assert.Contains("attivita migra", c.Fix);
    }

    [Fact]
    public void Un_task_vecchio_SENZA_supervisore_non_va_sgridato()
    {
        // Finche' il supervisore non c'e', quel task e' l'unica cosa che veglia sulla piattaforma.
        // Segnarlo giallo insegnerebbe a ignorare il giallo proprio mentre sta facendo il suo
        // lavoro.
        var c = Verdicts.LegacyTask("ProcioneMGR Backup DB", "db-backup.ps1 alle 03:30",
                                    "Ready", "ultima 2026-08-20 03:30", esitoBuono: true, "0x00000000",
                                    supervisorePrende: false);

        Assert.Equal("Ok", c.Level.ToString());
    }

    [Fact]
    public void Un_task_vecchio_FALLITO_resta_un_avviso()
    {
        var c = Verdicts.LegacyTask("ProcioneMGR Backup DB", "db-backup.ps1 alle 03:30",
                                    "Ready", "ultima 2026-08-20 03:30", esitoBuono: false, "0x00000001",
                                    supervisorePrende: false);

        Assert.Equal("Warn", c.Level.ToString());
        Assert.Contains("0x00000001", c.Detail);
    }

    // =============================================================================================
    //  Backup: il verdetto sul DATO OSSERVABILE
    // =============================================================================================

    [Fact]
    public void Il_backup_si_giudica_sui_file_non_su_cio_che_l_automazione_dichiara()
    {
        var soglia = TimeSpan.FromHours(36);

        var fresco = Verdicts.Backup(true, 14, T(20, 3, 30), 350L << 20, T(20, 10), soglia);
        Assert.Equal("Ok", fresco.Level.ToString());
        Assert.Contains("14 dump", fresco.Detail);

        // Oltre 36 ore almeno un giro notturno e' saltato: e' la soglia gia' usata dal watchdog,
        // cosi' i due strumenti non possono dare verdetti diversi sullo stesso fatto.
        var saltato = Verdicts.Backup(true, 14, T(18, 3, 30), 350L << 20, T(20, 10), soglia);
        Assert.Equal("Warn", saltato.Level.ToString());
        Assert.Contains("saltato", saltato.Detail);

        Assert.Equal("Warn", Verdicts.Backup(true, 0, null, 0, T(20, 10), soglia).Level.ToString());
        Assert.Equal("Warn", Verdicts.Backup(false, 0, null, 0, T(20, 10), soglia).Level.ToString());
    }

    // =============================================================================================
    //  Il pod di un tunnel — la stessa scelta dello script, scritta invece che casuale
    // =============================================================================================

    [Fact]
    public void Il_pod_del_tunnel_e_il_PRIMO_per_nome_come_items_0_dello_script()
    {
        // Durante un rollout ce ne sono due Running. ensure-trading-portforward.ps1 prende
        // `.items[0]`, cioe' il primo in ordine di nome (l'ordine in cui l'API server restituisce
        // le liste). Se la plancia ne scegliesse un altro griderebbe STANTIO su un tunnel sano.
        var vecchio = new Pod("procionemgr-trading", "procionemgr-trading-6cf8c78dff-aaaaa", "Running", 9, true, T(19, 8));
        var nuovo = new Pod("procionemgr-trading", "procionemgr-trading-7ab91c22ee-zzzzz", "Running", 0, true, T(20, 9));
        var altro = new Pod("procionemgr-ml", "procionemgr-ml-000", "Running", 0, true, T(20, 9));
        var morente = new Pod("procionemgr-trading", "procionemgr-trading-0000000000-aaaaa", "Terminating", 9, false, T(19, 8));

        var scelto = Verdicts.TunnelPod([altro, nuovo, morente, vecchio], "procionemgr-trading");

        Assert.Equal(vecchio.Name, scelto!.Name);
        Assert.Null(Verdicts.TunnelPod([altro], "procionemgr-trading"));
    }

    // =============================================================================================
    //  Lettura delle attivita' pianificate (era senza rete: e' la sezione gia' morta in silenzio)
    // =============================================================================================

    [Fact]
    public void ScheduledTasks_legge_le_righe_vere_del_frammento_PowerShell()
    {
        // Copiate dall'esecuzione reale su questa macchina: i nomi hanno spazi, l'attivita'
        // inesistente arriva come ASSENTE con due campi vuoti, e le righe finiscono con CRLF.
        const string uscita =
            "ProcioneMGR Watchdog|Ready|2026-08-23 11:53|267014\r\n" +
            "ProcioneMGR Backup DB|Ready|2026-08-23 03:30|0\r\n" +
            "ProcioneMGR BringUp|ASSENTE||\r\n";

        var lette = Parsing.ScheduledTasks(uscita);

        Assert.Equal(3, lette.Count);
        Assert.Equal("ProcioneMGR Watchdog", lette[0].Name);
        Assert.Equal("Ready", lette[0].State);
        Assert.Equal("2026-08-23 11:53", lette[0].LastRun);
        Assert.Equal("267014", lette[0].LastResult);
        Assert.Equal("ASSENTE", lette[2].State);
        Assert.Equal("", lette[2].LastRun);
    }

    [Theory]
    [InlineData("")]
    [InlineData("|Ready|2026-08-23 03:30|0")]   // nome vuoto: non e' un'attivita'
    [InlineData("SoloIlNome")]                  // meno di due campi
    public void ScheduledTasks_scarta_le_righe_che_non_sono_attivita(string uscita)
        => Assert.Empty(Parsing.ScheduledTasks(uscita));

    [Fact]
    public void KubeServer_non_muore_su_un_kubeconfig_di_forma_sbagliata()
    {
        // Radice che non e' un oggetto: TryGetProperty LANCIA InvalidOperationException, e senza
        // la cattura `procione stato` moriva con uscita 3 invece di dire «contesto assente» —
        // cioe' la plancia taceva proprio sul guasto che deve raccontare.
        Assert.Null(Parsing.KubeServer("[]", "kind-procionemgr-dev"));
        Assert.Null(Parsing.KubeServer("null", "kind-procionemgr-dev"));
        Assert.Null(Parsing.KubeServer("42", "kind-procionemgr-dev"));
        Assert.Null(Parsing.KubeServer("\"stringa\"", "kind-procionemgr-dev"));
    }
}
