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
    private static readonly (int Cfg, int Runs, int GreyKeys)[] Reale =
    [
        (17, 62, 82), (18, 63, 26), (20, 15, 13), (19, 16, 5), (8, 17, 1),
    ];

    [Fact]
    public void SuiDATIveri_laCACCIAsterileVIENEtrovata_eSOLOquella()
    {
        var righe = HuntYield.Judge(Reale);

        var sterili = righe.Where(r => r.Verdict == HuntYieldVerdict.Sterile).Select(r => r.ConfigurationId).ToList();
        Assert.Equal([8], sterili);
        // ...e le altre restano produttive: un giudizio che condanna metà della flotta di caccia
        // non è un giudizio, è una soglia sbagliata.
        Assert.All(righe.Where(r => r.ConfigurationId != 8),
            r => Assert.Equal(HuntYieldVerdict.Produttiva, r.Verdict));
    }

    [Fact]
    public void IlNULLO_diK50_seNESSUNOrende_nonSiCONDANNAnessuno()
    {
        // È il caso che rende inutilizzabile il criterio «zero gambe»: quando il collo di bottiglia
        // è il gate, tutte le configurazioni rendono zero. Condannarne una sarebbe scegliere un
        // capro espiatorio, e spegnerla non farebbe passare niente di più.
        var righe = HuntYield.Judge([(1, 30, 0), (2, 30, 0), (3, 30, 0)]);

        Assert.All(righe, r => Assert.Equal(HuntYieldVerdict.Produttiva, r.Verdict));
    }

    [Fact]
    public void SottoIRUNminimi_nonSiGIUDICA()
    {
        // Con pochi run una resa di zero è ordinaria anche per una caccia sana: l'ignoranza non
        // condanna, come per il ritmo atteso e per la provenienza.
        var righe = HuntYield.Judge([(1, 40, 40), (2, 3, 0)]);

        Assert.Equal(HuntYieldVerdict.TroppoPresto, righe.Single(r => r.ConfigurationId == 2).Verdict);
    }

    [Fact]
    public void LaMEDIANAnonSIfaSPOSTAREdaCHInonEgiudicabile()
    {
        // Il metro non deve dipendere da chi non si sta giudicando: se una configurazione con tre
        // run entrasse nella mediana, potrebbe far condannare una collega sana — o assolverne una
        // morta. Qui la sola giudicabile è la 1, quindi la mediana è la sua e nessuno è sterile.
        var righe = HuntYield.Judge([(1, 40, 4), (2, 2, 0), (3, 2, 0), (4, 2, 0)]);

        Assert.Equal(HuntYieldVerdict.Produttiva, righe.Single(r => r.ConfigurationId == 1).Verdict);
        Assert.All(righe.Where(r => r.ConfigurationId != 1),
            r => Assert.Equal(HuntYieldVerdict.TroppoPresto, r.Verdict));
    }

    [Fact]
    public void UnaCACCIAappenaSOTTOlaSOGLIA_nonEsterile()
    {
        // La soglia è larga apposta: deve cogliere i casi fuori scala (il fattore 22 misurato), non
        // arbitrare fra vicini. Con mediana 1,00 e frazione 0,25, una resa di 0,30 resta produttiva.
        var righe = HuntYield.Judge([(1, 20, 20), (2, 20, 20), (3, 20, 6)]);

        Assert.Equal(HuntYieldVerdict.Produttiva, righe.Single(r => r.ConfigurationId == 3).Verdict);
    }

    [Fact]
    public void LaFRASEdellaCACCIAsterile_PORTAilNUMEROcheLAsostiene()
    {
        var righe = HuntYield.Judge(Reale);
        var otto = righe.Single(r => r.ConfigurationId == 8);

        var testo = HuntYield.Describe(otto, medianaPerRun: 0.41);

        Assert.Contains("1 candidati grigi distinti in 17 run", testo, StringComparison.Ordinal);
        Assert.Contains("contro una mediana di", testo, StringComparison.Ordinal);
    }
}
