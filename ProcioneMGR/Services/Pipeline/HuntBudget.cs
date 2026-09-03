namespace ProcioneMGR.Services.Pipeline;

/// <summary>Quanto costa e quanto rende una caccia, per decidere quante ore darle.</summary>
/// <param name="ConfigurationId">La configurazione.</param>
/// <param name="MinutiPerRun">Durata mediana misurata. 0 = mai girata, quindi costo ignoto.</param>
/// <param name="OreAttualiAlMese">Al ritmo corrente, contando la cadenza propria.</param>
/// <param name="ChiaviPerOra">La resa che regge il confronto (K54b): 0 quando non ha ancora prodotto.</param>
/// <param name="CadenzaOre">La cadenza propria attuale (<c>MinHoursBetweenRuns</c>), 0 = nessun limite.</param>
/// <param name="Run">
/// Run completati nella finestra. Serve a distinguere <b>«rende zero»</b> da <b>«non ha ancora
/// avuto modo di rendere»</b>: sotto <see cref="HuntYield.MinRunsForVerdict"/> la resa non è un
/// giudizio, e chi non è giudicabile non va in cima alla fila dei tagli.
/// </param>
/// <param name="RunAlMese">
/// [Revisione 2026-09-03] Il ritmo con cui la caccia gira DAVVERO, in run al mese, misurato dal
/// conteggio dei run sull'età della finestra (fino a oggi). 0 = non noto: si ricava da ore e durata.
/// </param>
public sealed record CostoCaccia(
    int ConfigurationId, double MinutiPerRun, double OreAttualiAlMese, double ChiaviPerOra, int CadenzaOre,
    int Run = int.MaxValue, double RunAlMese = 0)
{
    /// <summary>
    /// [K59, corretto dal test] Vero = ci sono abbastanza run perché la resa voglia dire qualcosa.
    ///
    /// <para><b>Il difetto che l'ha imposto.</b> Le configurazioni entrate in rotazione il
    /// 2026-09-03 (9, 11, 14, 16) hanno resa <c>0</c> — non perché siano sterili, ma perché non
    /// hanno ancora girato. Ordinando per resa crescente finivano <b>prime</b> nella fila dei
    /// rallentamenti: la caccia appena aggiunta sarebbe stata la prima a essere frenata, e non
    /// avrebbe mai avuto modo di dimostrare nulla. È la regola dell'ignoranza che non condanna,
    /// violata al primo giro.</para>
    /// </summary>
    public bool ResaGiudicabile => Run >= HuntYield.MinRunsForVerdict;
};

/// <summary>Una modifica di cadenza proposta, con la ragione e il risparmio.</summary>
public sealed record ProposteCadenza(int ConfigurationId, int CadenzaAttuale, int CadenzaProposta, double OreRisparmiate, string Perche);

/// <summary>
/// [K59, PRD autonomia-piena — Fase 4, 2026-09-03] <b>Il tetto della caccia si misura in ORE, non in
/// numero di configurazioni.</b>
///
/// <para><b>Perché in ore.</b> Contare le cacce non dice niente: al 2026-09-03 la mediana per run va
/// da <b>0,6 minuti</b> (cfg 9, 1d su 10 serie) a <b>43,8</b> (cfg 19, 5m su 10 serie) — settanta
/// volte. Un tetto «al massimo N cacce» tratterebbe come uguali due cose che non lo sono, ed è lo
/// stesso errore per cui K54b ha dovuto mettere il costo accanto alla resa: senza, si condanna chi
/// non consuma nulla e si assolve chi consuma tutto.</para>
///
/// <para><b>Perché il numero di cacce NON si autolimita.</b> Il gate del DSR deflaziona per i
/// tentativi <i>di quel run</i> (<c>trialsExplored</c>): non vede le altre cacce. Aggiungerne non
/// rende il gate più severo, quindi <b>nessun freno scatta da solo</b> e la disciplina dev'essere
/// esplicita. Il controllo che invece <i>scala</i> col numero di cacce è K57 — «sopravvive alla
/// rimisurazione?» — che guarda FRA i run invece che dentro uno. I due sono complementari, e senza
/// K57 aggiungere cacce sarebbe solo comprare più occasioni di essere fortunati.</para>
///
/// <para><b>Che cosa si taglia per primo.</b> La resa per ORA, non per run: il numero di run al
/// denominatore è una scelta di pianificazione, non una proprietà della caccia (misurato: stessa
/// config, stesso motore, resa da 0,477 a 7,250 col tasso di grigi piatto). E chi non ha ancora
/// una misura <b>non si tocca</b>: l'ignoranza non condanna, ed è la regola che nel filone K è già
/// stata pagata quattro volte.</para>
///
/// Puro e statico: si prova senza database e senza orologio.
/// </summary>
public static class HuntBudget
{
    /// <summary>
    /// Cadenza massima proponibile. Oltre le due settimane una caccia smette di essere una caccia:
    /// il suo campione non cresce abbastanza da poterla giudicare (K50 chiede 12 run per un
    /// verdetto), e si finirebbe a tenerla accesa senza mai poterne dire niente.
    /// </summary>
    public const int MaxCadenzaOre = 336;

    /// <summary>
    /// Cadenza minima che la riallocazione può imporre: sotto, non sta rallentando, sta solo
    /// facendo rumore su un numero che era già basso.
    /// </summary>
    public const int MinCadenzaProponibile = 12;

    /// <summary>
    /// Che cosa rallentare, e di quanto, per stare dentro <paramref name="budgetOreMese"/>.
    /// Restituisce lista VUOTA quando il budget è rispettato o non è impostato: non si tocca ciò
    /// che non serve toccare.
    /// </summary>
    public static IReadOnlyList<ProposteCadenza> Riallinea(
        IReadOnlyList<CostoCaccia> cacce, double budgetOreMese)
    {
        ArgumentNullException.ThrowIfNull(cacce);
        if (budgetOreMese <= 0 || cacce.Count == 0) return [];

        var totale = cacce.Sum(c => c.OreAttualiAlMese);
        if (totale <= budgetOreMese) return [];

        // Si rallenta partendo da chi rende MENO per ora. Chi non ha ancora una misura di costo
        // (mai girata) resta fuori: rallentare una caccia di cui non si conosce il prezzo non è
        // una decisione, è un tiro a indovinare.
        var candidate = cacce
            .Where(c => c.MinutiPerRun > 0 && c.OreAttualiAlMese > 0)
            // Chi non ha ancora abbastanza run va IN FONDO alla fila, non in cima: la sua resa a
            // zero non è un giudizio. Si tocca solo se rallentare le giudicabili non basta.
            .OrderBy(c => c.ResaGiudicabile ? 0 : 1)
            .ThenBy(c => c.ChiaviPerOra)
            .ThenByDescending(c => c.OreAttualiAlMese)
            .ToList();

        var proposte = new List<ProposteCadenza>();
        var daTagliare = totale - budgetOreMese;

        foreach (var c in candidate)
        {
            if (daTagliare <= 0.01) break;

            // Il minimo raddoppio della cadenza dimezza le ore. Si cerca la cadenza più PICCOLA che
            // basta: rallentare più del necessario è una perdita di informazione che nessuno ha
            // chiesto.
            var attuale = c.CadenzaOre > 0 ? c.CadenzaOre : StimaCadenzaImplicita(c);
            if (attuale <= 0) continue;

            var oreDopo = c.OreAttualiAlMese;
            var proposta = attuale;
            while (proposta < MaxCadenzaOre && c.OreAttualiAlMese - oreDopo < daTagliare)
            {
                proposta = Math.Min(MaxCadenzaOre, proposta * 2);
                oreDopo = c.OreAttualiAlMese * attuale / proposta;
            }

            proposta = Math.Max(MinCadenzaProponibile, proposta);
            if (proposta <= attuale) continue;

            var risparmio = c.OreAttualiAlMese - oreDopo;
            proposte.Add(new ProposteCadenza(c.ConfigurationId, c.CadenzaOre, proposta, risparmio,
                (c.ResaGiudicabile
                    ? $"resa {c.ChiaviPerOra:F2} chiavi/ora su {c.Run} run — fra le più basse misurate"
                    : $"solo {c.Run} run: la resa non è ancora giudicabile, ma rallentare le altre non bastava")
                + $"; da {c.OreAttualiAlMese:F1} a {oreDopo:F1} ore al mese"));
            daTagliare -= risparmio;
        }

        return proposte;
    }

    /// <summary>
    /// [Revisione 2026-09-03] <b>Le ore al mese AL RITMO IN VIGORE.</b> Con una cadenza propria
    /// (<c>MinHoursBetweenRuns &gt; 0</c>) la proiezione è <i>durata mediana × run al mese a quella
    /// cadenza</i>; solo senza cadenza si proietta dall'osservato.
    ///
    /// <para><b>Il difetto che l'ha imposta.</b> La prima versione proiettava sempre dalle ore
    /// OSSERVATE negli ultimi 30 giorni, e <see cref="Riallinea"/> assumeva che quelle ore
    /// corrispondessero alla cadenza attuale. Dopo una riscrittura le ore osservate non cambiano per
    /// settimane: lo sforo veniva «visto» di nuovo al giro dopo e la stessa configurazione
    /// raddoppiata ancora — 48 → 96 → 192 → 336 ore in tre giri, poi la successiva. E un solo run
    /// osservato (span zero → un giorno) valeva 21,9 ore/mese per la cfg 19.</para>
    /// </summary>
    /// <para><b>Un solo stimatore, rivisto la sera stessa.</b> La prima versione proiettava con la
    /// cadenza dalla durata <i>mediana</i> e senza cadenza dalla <i>somma</i> delle ore: con durate
    /// asimmetriche i due numeri divergevano, e il solo atto di scrivere una cadenza faceva
    /// «rientrare» lo sforo senza che il consumo cambiasse. Ora il costo è sempre
    /// <c>durata media × ritmo</c>, dove il ritmo è il MINORE fra quello della cadenza (720/ore) e
    /// quello osservato (run al mese sull'età della finestra): una cadenza è un minimo, non una
    /// schedulazione, e una configurazione fuori rotazione o lanciata una volta a mano non gira a
    /// 720/cadenza run al mese solo perché ha una cadenza scritta.</para>
    /// </summary>
    /// <param name="minutiMedi">Durata media di un run (ore consumate / run), 0 = mai misurata.</param>
    /// <param name="runAlMese">Ritmo osservato: run al mese sull'età della finestra (fino a oggi).</param>
    /// <param name="cadenzaOre">La cadenza propria in vigore, 0 = nessuna.</param>
    public static double ProiettaOreAlMese(double minutiMedi, double runAlMese, int cadenzaOre)
    {
        if (minutiMedi <= 0 || runAlMese <= 0) return 0;
        var ritmo = cadenzaOre > 0 ? Math.Min(30.0 * 24.0 / cadenzaOre, runAlMese) : runAlMese;
        return minutiMedi / 60.0 * ritmo;
    }

    /// <summary>
    /// Con cadenza propria a zero la caccia gira al ritmo della rotazione: lo si ricava dalle ore
    /// che consuma e da quanto dura un run, invece di assumerne uno.
    /// </summary>
    private static int StimaCadenzaImplicita(CostoCaccia c)
    {
        // [Revisione 2026-09-03] Il ritmo misurato dal CONTEGGIO, quando c'è: è lo stesso numero
        // con cui è stata proiettata OreAttualiAlMese, quindi la cadenza implicita e il costo
        // parlano della stessa caccia. Il ricavo da ore/durata resta il ripiego per i fixture.
        var runAlMese = c.RunAlMese > 0
            ? c.RunAlMese
            : (c.MinutiPerRun <= 0 || c.OreAttualiAlMese <= 0 ? 0 : c.OreAttualiAlMese * 60 / c.MinutiPerRun);
        return runAlMese <= 0 ? 0 : Math.Max(1, (int)Math.Round(30 * 24 / runAlMese));
    }

    /// <summary>La frase per il pannello e per il journal, col numero che la sostiene.</summary>
    public static string Racconta(IReadOnlyList<CostoCaccia> cacce, double budgetOreMese)
    {
        ArgumentNullException.ThrowIfNull(cacce);
        var totale = cacce.Sum(c => c.OreAttualiAlMese);
        if (budgetOreMese <= 0)
        {
            return $"{totale:F1} ore al mese di caccia al ritmo attuale su {cacce.Count} configurazioni. "
                 + "Nessun tetto impostato: il consumo non è governato da niente.";
        }
        return totale <= budgetOreMese
            ? $"{totale:F1} ore al mese su un tetto di {budgetOreMese:F0}: dentro, non c'è niente da rallentare."
            : $"{totale:F1} ore al mese contro un tetto di {budgetOreMese:F0}: {totale - budgetOreMese:F1} di troppo.";
    }
}
