using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K50, PRD autonomia-piena — Fase 4, 2026-09-02] <b>Quale caccia produce, e quale consuma budget
/// per niente.</b>
///
/// <para>Il PRD lo elenca come primo dei «cinque che nessuno fa»: <i>config 17 e 18 hanno consumato
/// 57 tentativi ciascuna e prodotto zero gambe su 119 run, e nessuno le mette in sonno</i>.</para>
///
/// <para><b>Ma il criterio ovvio è una trappola, e la misura lo dimostra.</b> Sui trenta giorni al
/// 2026-09-02, <c>ensembleLegs</c> è vuoto in <b>173 run su 173</b>, su tutte e cinque le
/// configurazioni attive: «zero gambe assemblate» non distingue una caccia morta da una viva — le
/// addormenterebbe <b>tutte</b>, perché il collo di bottiglia è il gate, non la caccia.</para>
///
/// <para>Ciò che discrimina è la <b>fascia grigia</b>, e di molto: 1,32 chiavi distinte per run
/// (config 17) contro 0,06 (config 8), un fattore <b>ventidue</b>.</para>
/// </summary>
public class ResaDellaCacciaK50Tests
{
    /// <summary>Le cinque configurazioni attive, misurate il 2026-09-02 sui trenta giorni.</summary>
    // [K54b, 2026-09-02] Le ore sono entrate nel giudizio: uno spreco e' resa bassa MOLTIPLICATA
    // per costo reale, e senza il costo si condannava chi non consuma nulla. Qui i costi VERI
    // misurati sulla stessa finestra, cosi' questi test continuano a provare cio' che provavano —
    // la logica della resa — invece di fermarsi al cancello nuovo.
    //
    // NOTA: alla config 8 restano 0 ore perche' e' FERMA dal 20 agosto, ed e' esattamente il caso
    // che ha imposto la correzione: era dichiarata «sterile» senza consumare niente.
    private static readonly (int Cfg, int Runs, int GreyKeys, double Ore, double MinMediani)[] Reale =
    [
        (17, 62, 82, 4.49, 3.8), (18, 63, 26, 21.88, 15.5), (20, 15, 13, 2.32, 8.7),
        (19, 16, 5, 14.09, 43.7), (8, 17, 1, 0.0, 7.4),
    ];

    /// <summary>Costo sopra il cancello di K54b, per i casi sintetici che provano la sola resa.</summary>
    private const double OreCheContano = 5.0;

    /// <summary>
    /// <b>[Rettifica K54b, 2026-09-02]</b> Questo test affermava: «sui dati veri la caccia sterile
    /// viene trovata, ed è la 8». Era la conclusione sbagliata, e il test la teneva in piedi.
    ///
    /// <para>La config 8 è <b>ferma dal 20 agosto</b>: consuma <b>0 ore su 48,7</b> al mese, quindi
    /// non c'è niente da liberare mettendola in sonno. E non l'aveva spenta un giudizio: l'aveva
    /// dimenticata un gate (commit <c>932eb21</c>) introdotto col commento «le campagne reali sono
    /// già tutte a timeframe singolo, quindi nessun run esistente cambia» — falso, 28 run su 29
    /// della config 8 sono a timeframe misti.</para>
    ///
    /// <para>Sui dati veri il verdetto corretto è che <b>nessuna configurazione attiva è sterile</b>,
    /// e la config 8 è <c>Dormiente</c>. Dove stanno davvero le ore lo dice il costo, non la resa:
    /// la config 19 consuma 14,09 ore con una mediana di 43,7 minuti a run — undici volte la 17 —
    /// e non ha schierato una gamba.</para>
    /// </summary>
    [Fact]
    public void SuiDATIveri_nessunaATTIVAesterile_eLA8eFERMA()
    {
        var righe = HuntYield.Judge(Reale);

        Assert.Empty(righe.Where(r => r.Verdict == HuntYieldVerdict.Sterile));
        Assert.Equal(HuntYieldVerdict.Dormiente, righe.Single(r => r.ConfigurationId == 8).Verdict);
        // Le attive restano produttive: un giudizio che condanna metà della flotta di caccia non è
        // un giudizio, è una soglia sbagliata.
        Assert.All(righe.Where(r => r.ConfigurationId != 8),
            r => Assert.Equal(HuntYieldVerdict.Produttiva, r.Verdict));

        // E il costo racconta una storia diversa dalla resa: la più cara NON è la peggiore per resa.
        var piuCara = righe.OrderByDescending(r => r.HoursSpent).First();
        Assert.Equal(18, piuCara.ConfigurationId);
        Assert.Equal(HuntYieldVerdict.Produttiva, piuCara.Verdict);
    }

    [Fact]
    public void IlNULLO_diK50_seNESSUNOrende_nonSiCONDANNAnessuno()
    {
        // È il caso che rende inutilizzabile il criterio «zero gambe»: quando il collo di bottiglia
        // è il gate, tutte le configurazioni rendono zero. Condannarne una sarebbe scegliere un
        // capro espiatorio, e spegnerla non farebbe passare niente di più.
        var righe = HuntYield.Judge([(1, 30, 0, OreCheContano, 10), (2, 30, 0, OreCheContano, 10), (3, 30, 0, OreCheContano, 10)]);

        Assert.All(righe, r => Assert.Equal(HuntYieldVerdict.Produttiva, r.Verdict));
    }

    [Fact]
    public void SottoIRUNminimi_nonSiGIUDICA()
    {
        // Con pochi run una resa di zero è ordinaria anche per una caccia sana: l'ignoranza non
        // condanna, come per il ritmo atteso e per la provenienza.
        var righe = HuntYield.Judge([(1, 40, 40, OreCheContano, 10), (2, 3, 0, OreCheContano, 10)]);

        Assert.Equal(HuntYieldVerdict.TroppoPresto, righe.Single(r => r.ConfigurationId == 2).Verdict);
    }

    [Fact]
    public void LaMEDIANAnonSIfaSPOSTAREdaCHInonEgiudicabile()
    {
        // Il metro non deve dipendere da chi non si sta giudicando: se una configurazione con tre
        // run entrasse nella mediana, potrebbe far condannare una collega sana — o assolverne una
        // morta. Qui la sola giudicabile è la 1, quindi la mediana è la sua e nessuno è sterile.
        var righe = HuntYield.Judge([(1, 40, 4, OreCheContano, 10), (2, 2, 0, OreCheContano, 10), (3, 2, 0, OreCheContano, 10), (4, 2, 0, OreCheContano, 10)]);

        Assert.Equal(HuntYieldVerdict.Produttiva, righe.Single(r => r.ConfigurationId == 1).Verdict);
        Assert.All(righe.Where(r => r.ConfigurationId != 1),
            r => Assert.Equal(HuntYieldVerdict.TroppoPresto, r.Verdict));
    }

    [Fact]
    public void UnaCACCIAappenaSOTTOlaSOGLIA_nonEsterile()
    {
        // La soglia è larga apposta: deve cogliere i casi fuori scala (il fattore 22 misurato), non
        // arbitrare fra vicini. Con mediana 1,00 e frazione 0,25, una resa di 0,30 resta produttiva.
        var righe = HuntYield.Judge([(1, 20, 20, OreCheContano, 10), (2, 20, 20, OreCheContano, 10), (3, 20, 6, OreCheContano, 10)]);

        Assert.Equal(HuntYieldVerdict.Produttiva, righe.Single(r => r.ConfigurationId == 3).Verdict);
    }

    [Fact]
    public void LaFRASEdellaCACCIAsterile_PORTAilNUMEROcheLAsostiene()
    {
        var righe = HuntYield.Judge(Reale);
        var otto = righe.Single(r => r.ConfigurationId == 8);

        var testo = HuntYield.Describe(otto, medianaPerRun: 0.41);

        // [K54b] La frase non parla piu' di resa, perche' la resa non e' la ragione: parla di ore.
        // E indirizza alla domanda giusta — non «rende poco», ma «perche' ha smesso di girare».
        Assert.Contains("17 run per 0,0 ore", testo, StringComparison.Ordinal);
        Assert.Contains("PERCHE' ha smesso di essere invocata", testo, StringComparison.Ordinal);

        // La frase della caccia davvero sterile, invece, porta la resa E il costo.
        var sterile = HuntYield.Judge([(1, 40, 40, 10.0, 15.0), (2, 40, 1, 10.0, 15.0)])
            .Single(r => r.ConfigurationId == 2);
        var testoSterile = HuntYield.Describe(sterile, medianaPerRun: 1.0);
        Assert.Contains("contro una mediana di", testoSterile, StringComparison.Ordinal);
        Assert.Contains("COSTA 10,0 ore", testoSterile, StringComparison.Ordinal);
    }
}
