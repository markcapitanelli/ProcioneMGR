namespace ProcioneMGR.Services.Pipeline;

/// <summary>Quanto costa e quanto rende una caccia, per decidere quante ore darle.</summary>
/// <param name="ConfigurationId">La configurazione.</param>
/// <param name="MinutiPerRun">Durata mediana misurata. 0 = mai girata, quindi costo ignoto.</param>
/// <param name="OreAttualiAlMese">Al ritmo corrente, contando la cadenza propria.</param>
/// <param name="ChiaviPerOra">La resa che regge il confronto (K54b): 0 quando non ha ancora prodotto.</param>
/// <param name="CadenzaOre">La cadenza propria attuale (<c>MinHoursBetweenRuns</c>), 0 = nessun limite.</param>
public sealed record CostoCaccia(
    int ConfigurationId, double MinutiPerRun, double OreAttualiAlMese, double ChiaviPerOra, int CadenzaOre);

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
            .OrderBy(c => c.ChiaviPerOra)
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
                $"resa {c.ChiaviPerOra:F2} chiavi/ora — la più bassa fra quelle misurate; "
                + $"da {c.OreAttualiAlMese:F1} a {oreDopo:F1} ore al mese"));
            daTagliare -= risparmio;
        }

        return proposte;
    }

    /// <summary>
    /// Con cadenza propria a zero la caccia gira al ritmo della rotazione: lo si ricava dalle ore
    /// che consuma e da quanto dura un run, invece di assumerne uno.
    /// </summary>
    private static int StimaCadenzaImplicita(CostoCaccia c)
    {
        if (c.MinutiPerRun <= 0 || c.OreAttualiAlMese <= 0) return 0;
        var runAlMese = c.OreAttualiAlMese * 60 / c.MinutiPerRun;
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
