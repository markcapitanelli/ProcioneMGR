using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prove della revisione della plancia del 2026-09-06.
///
/// Ogni test qui sotto corrisponde a un difetto TROVATO nel codice che girava, non a un requisito
/// immaginato. Sono tutti della stessa famiglia — la piu' pericolosa di questo progetto: una
/// domanda a cui non si e' potuto rispondere che viene letta come una risposta rassicurante.
/// </summary>
public class PlanciaGovernoTests
{
    /// <summary>
    /// L'albero da cui questa suite e' stata compilata.
    ///
    /// NON <c>Platform.MainRepoRoot</c>, che dai worktree risale al repository principale: i test
    /// devono confrontare il codice con la documentazione che gli sta ACCANTO, altrimenti da un
    /// worktree misurerebbero i lavori di qui contro il documento di la' — e sarebbero verdi o
    /// rossi per il motivo sbagliato.
    /// </summary>
    private static string Radice => Platform.RepoRoot;

    // =============================================================================================
    //  «Non lo so» non e' «e' chiuso»
    // =============================================================================================

    [Fact]
    public void ChiusuraTunnel_porte_libere_e_un_esito_riuscito()
    {
        var (riuscito, messaggio, nota) = Verdicts.ChiusuraTunnel([], []);

        Assert.True(riuscito);
        Assert.Contains("libere", messaggio);
        Assert.Null(nota);
    }

    [Fact]
    public void ChiusuraTunnel_riuscita_anche_se_il_proprietario_non_si_leggeva()
    {
        // Il PERCORSO non conta, conta il risultato: se le porte sono libere il lavoro e' fatto,
        // anche se la mappa porta -> PID non si era potuta leggere. Dichiararlo fallito qui
        // manderebbe a ripetere un'operazione gia' riuscita.
        var (riuscito, _, nota) = Verdicts.ChiusuraTunnel([], [18092, 18093]);

        Assert.True(riuscito);
        Assert.Null(nota);
    }

    [Fact]
    public void ChiusuraTunnel_porte_ancora_vive_non_si_dichiarano_chiuse()
    {
        var (riuscito, messaggio, _) = Verdicts.ChiusuraTunnel([18092], []);

        Assert.False(riuscito);
        Assert.Contains("18092", messaggio);
    }

    [Fact]
    public void ChiusuraTunnel_distingue_NON_CHIUSA_da_NON_MISURATA()
    {
        // Le due cose mandano a fare cose diverse: «non l'ho chiusa» vuol dire riprovare, «non so
        // se e' chiusa» vuol dire andare a guardare. Confonderle e' il difetto del 2026-09-05, che
        // e' costato un rilascio annunciato e mai avvenuto.
        var (riuscito, messaggio, nota) = Verdicts.ChiusuraTunnel([18092, 18093], [18093]);

        Assert.False(riuscito);
        Assert.Contains("18092", messaggio);
        Assert.NotNull(nota);
        Assert.Contains("non ho misurato", nota);
    }

    // =============================================================================================
    //  Preferenze illeggibili: mai ricadere sui default
    // =============================================================================================

    [Fact]
    public void IsEnabled_la_preferenza_salvata_vince_sul_default_della_tabella()
    {
        var veglia = Jobs.All.Single(j => j.Name == "veglia");
        Assert.True(veglia.EnabledByDefault);   // premessa del test, non un'opinione

        Assert.False(Prefs.IsEnabled(veglia, new Dictionary<string, bool> { ["veglia"] = false }));
        Assert.True(Prefs.IsEnabled(veglia, new Dictionary<string, bool>()));
    }

    [Fact]
    public void Job_senza_preferenze_leggibili_crede_al_SUPERVISORE_non_al_default()
    {
        // IL difetto. `Prefs.Read()` puo' restituire null (file occupato da un'altra scrittura), e
        // il vecchio `IsEnabled` in quel caso ripiegava sul default della tabella — che per la
        // veglia e' ACCESO. Risultato: un lavoro spento a mano risultava acceso nel quadro, in
        // silenzio, e il rimedio suggerito era il comando che l'operatore aveva gia' eseguito.
        //
        // Ora chi non ha una mappa buona passa `acceso: null`, e il verdetto crede allo stato che
        // il supervisore dichiara — che e' la fonte giusta, perche' e' lui a eseguirli.
        var veglia = Jobs.All.Single(j => j.Name == "veglia");
        var statoSupervisore = new JobState { Name = "veglia", Enabled = false, LastRun = DateTimeOffset.Now };

        var c = Verdicts.Job(veglia, statoSupervisore, supervisoreVivo: true, DateTimeOffset.Now, acceso: null);

        Assert.Equal(Level.NotApplicable, c.Level);
        Assert.Contains("spento", c.Detail);
    }

    [Fact]
    public void Job_con_preferenza_esplicita_la_preferenza_comanda()
    {
        // Il complemento: quando la mappa SI legge, e' lei ad avere ragione — altrimenti
        // `procione lavoro <nome> accendi` sembrerebbe non aver fatto niente finche' il
        // supervisore non riparte.
        var veglia = Jobs.All.Single(j => j.Name == "veglia");
        var statoSupervisore = new JobState { Name = "veglia", Enabled = false, LastRun = DateTimeOffset.Now };

        var c = Verdicts.Job(veglia, statoSupervisore, supervisoreVivo: true, DateTimeOffset.Now, acceso: true);

        Assert.NotEqual(Level.NotApplicable, c.Level);
    }

    // =============================================================================================
    //  Il fumetto dell'icona: silenzio sul normale
    // =============================================================================================

    [Fact]
    public void Fumetto_uno_stato_che_non_cambia_non_annuncia_niente()
    {
        // Il caso che conta di piu'. Un guasto che dura un'ora produce sessanta rilevazioni: se
        // ognuna facesse comparire un fumetto, l'utente spegnerebbe le notifiche — e con esse
        // anche quelle che contano.
        Assert.Null(Verdicts.Fumetto(Level.Down, Level.Down));
        Assert.Null(Verdicts.Fumetto(Level.Ok, Level.Ok));
        Assert.Null(Verdicts.Fumetto(Level.Warn, Level.Warn));
    }

    [Fact]
    public void Fumetto_il_guasto_si_annuncia()
    {
        var a = Verdicts.Fumetto(Level.Ok, Level.Down);
        Assert.NotNull(a);
        Assert.Contains("guasto", a!.Value.Titolo);
        Assert.True(a.Value.ConDettagli);

        // Anche arrivandoci da un avviso, e anche alla prima rilevazione in assoluto.
        Assert.NotNull(Verdicts.Fumetto(Level.Warn, Level.Down));
        Assert.NotNull(Verdicts.Fumetto(null, Level.Down));
    }

    [Fact]
    public void Fumetto_anche_il_rientro_si_annuncia()
    {
        var a = Verdicts.Fumetto(Level.Down, Level.Ok);
        Assert.NotNull(a);
        Assert.Contains("rientrat", a!.Value.Titolo);
        Assert.False(a.Value.ConDettagli);   // niente da elencare: e' tornato tutto a posto

        // Rientro parziale: si dice, e si dice COSA resta.
        var parziale = Verdicts.Fumetto(Level.Down, Level.Warn);
        Assert.NotNull(parziale);
        Assert.True(parziale!.Value.ConDettagli);
    }

    [Fact]
    public void Fumetto_il_passaggio_fra_verde_e_giallo_non_disturba_nessuno()
    {
        // Lo dice il COLORE dell'icona, che sta li' apposta. Un fumetto per ogni avviso che va e
        // viene — un backup di 37 ore, un tunnel che si rifa' — sarebbe rumore continuo.
        Assert.Null(Verdicts.Fumetto(Level.Ok, Level.Warn));
        Assert.Null(Verdicts.Fumetto(Level.Warn, Level.Ok));
    }

    // =============================================================================================
    //  La tabella dei lavori e la documentazione
    // =============================================================================================

    [Fact]
    public void Ogni_lavoro_punta_a_uno_script_che_esiste_davvero()
    {
        // Un lavoro che punta a uno script sparito fallisce a ogni giro, per sempre. E' l'incidente
        // del 2026-08-17 nella sua forma piu' semplice: un'automazione che non puo' funzionare, e
        // che nessuno guarda finche' non serve.
        var scripts = Path.Combine(Radice, "scripts");
        Assert.True(Directory.Exists(scripts), $"cartella scripts non trovata: {scripts}");

        foreach (var job in Jobs.All)
            Assert.True(File.Exists(Path.Combine(scripts, job.Script)),
                        $"il lavoro «{job.Name}» punta a scripts/{job.Script}, che non esiste.");
    }

    [Fact]
    public void I_lavori_hanno_nomi_distinti_e_un_tetto_di_tempo()
    {
        // Il tetto non e' un dettaglio: uno script appeso senza tetto blocca il supervisore per
        // sempre, e un supervisore fermo e' indistinguibile — da fuori — da uno che non trova
        // niente di rotto.
        Assert.Equal(Jobs.All.Count, Jobs.All.Select(j => j.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Jobs.All, j => Assert.True(j.Timeout > TimeSpan.Zero, $"«{j.Name}» senza tetto di tempo"));
    }

    [Fact]
    public void La_documentazione_elenca_TUTTI_i_lavori()
    {
        // La revisione ha trovato il documento fermo a tre lavori mentre il codice ne aveva cinque:
        // mancavano `piani` e `deploy`, cioe' proprio i due che aggiornano il repository e
        // schierano codice da soli. Un elenco incompleto delle automazioni e' peggio di nessun
        // elenco, perche' si legge come completo.
        var doc = Path.Combine(Radice, "docs", "PLANCIA-CONSOLE.md");
        Assert.True(File.Exists(doc), $"documento non trovato: {doc}");

        var testo = File.ReadAllText(doc);
        foreach (var job in Jobs.All)
            Assert.True(testo.Contains($"`{job.Name}`", StringComparison.Ordinal),
                        $"il lavoro «{job.Name}» non e' documentato in docs/PLANCIA-CONSOLE.md.");
    }
}
