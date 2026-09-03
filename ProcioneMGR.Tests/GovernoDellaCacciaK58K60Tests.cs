using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K58-K60, PRD autonomia-piena — Fase 4, 2026-09-03] <b>Il governo della caccia: che cosa si paga
/// e non si guarda, quanto costa in ore, e quale buco riempire per primo.</b>
///
/// <para><b>Il fatto che ha aperto il filone.</b> Al 2026-09-03 la piattaforma teneva aggiornate
/// <b>222 celle</b> (serie × timeframe) e ne cacciava <b>125</b>: novantasette si pagavano a ogni
/// giro dell'ingestione senza che nessuna caccia le guardasse. E la rotazione automatica conteneva
/// <b>4 configurazioni su 13</b> attive.</para>
///
/// <para><b>Il vincolo che governa il progetto</b>, e che i nulli difendono: il gate del DSR
/// deflaziona per i tentativi <i>del proprio run</i> e <b>non vede le altre cacce</b>. Aggiungerne
/// non rende il gate più severo, quindi nessun freno scatta da solo — la disciplina è il tetto in
/// ORE, e il controllo che scala col numero di cacce è K57, non il DSR.</para>
/// </summary>
public class GovernoDellaCacciaK58K60Tests
{
    // ------------------------------------------------------------------ K58 · copertura

    [Fact]
    public void LeCELLE_siLEGGONOdallUNIVERSO()
    {
        var celle = HuntCoverageReader.Leggi(
            """[{"Symbol":"BTC/USDT","Timeframe":"5m"},{"Symbol":"ETH/USDT","Timeframe":"5m"}]""");

        Assert.Equal(2, celle.Count);
        Assert.Contains(new CellaUniverso("BTC/USDT", "5m"), celle);
    }

    /// <summary>
    /// <b>Il nullo della lettura.</b> Un universo malformato non deve far cadere il conteggio: la
    /// configurazione illeggibile vale «zero celle cacciate», che dichiara PIÙ buchi, non meno. È il
    /// verso prudente perché i buchi si propongono, non si eseguono.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non è json")]
    [InlineData("{}")]
    [InlineData("""[{"Symbol":"BTC/USDT"}]""")]
    [InlineData("""[{"Timeframe":"5m"}]""")]
    public void ILNULLO_unUNIVERSOmalformato_nonESPLODEeNONinventa(string json)
        => Assert.Empty(HuntCoverageReader.Leggi(json));

    [Fact]
    public void ILBUCOpiuGRANDE_vienePRIMA()
    {
        var cop = new CoperturaCaccia(
            Seguite: [new("A", "5m"), new("B", "5m"), new("C", "1h")],
            Cacciate: [],
            Scoperte: [new("A", "5m"), new("B", "5m"), new("C", "1h")]);

        Assert.Equal("5m", cop.BuchiPerTimeframe[0].Timeframe);
        Assert.Equal(2, cop.BuchiPerTimeframe[0].Simboli.Count);
    }

    // ------------------------------------------------------------------ K59 · budget in ore

    /// <summary>I costi VERI misurati il 2026-09-03, dopo i tagli su 18 e 19.</summary>
    private static readonly CostoCaccia[] Reali =
    [
        new(19, MinutiPerRun: 43.8, OreAttualiAlMese: 11.0, ChiaviPerOra: 2.0, CadenzaOre: 48, Run: 18),
        new(18, MinutiPerRun: 15.3, OreAttualiAlMese: 7.6, ChiaviPerOra: 1.2, CadenzaOre: 48, Run: 61),
        new(20, MinutiPerRun: 9.2, OreAttualiAlMese: 5.7, ChiaviPerOra: 5.6, CadenzaOre: 0, Run: 16),
        new(17, MinutiPerRun: 3.7, OreAttualiAlMese: 3.8, ChiaviPerOra: 18.3, CadenzaOre: 0, Run: 60),
    ];

    [Fact]
    public void DENTROilTETTO_nonSiTOCCAniente()
        => Assert.Empty(HuntBudget.Riallinea(Reali, budgetOreMese: 40));

    /// <summary>
    /// <b>Il nullo che conta di più.</b> Senza tetto impostato non si rallenta nulla: un servizio
    /// che decide da solo quanto budget è «giusto» prenderebbe una decisione che nessuno gli ha
    /// chiesto, e su una risorsa che il proprietario paga.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ILNULLO_senzaTETTO_nonSiRALLENTAnessuno(double budget)
        => Assert.Empty(HuntBudget.Riallinea(Reali, budget));

    [Fact]
    public void SOPRAilTETTO_siRALLENTApartendoDaCHIrendeMENO()
    {
        var proposte = HuntBudget.Riallinea(Reali, budgetOreMese: 20);

        Assert.NotEmpty(proposte);
        // La 18 rende 1,2 chiavi/ora: è la più bassa fra quelle misurate, e tocca a lei per prima.
        Assert.Equal(18, proposte[0].ConfigurationId);
        Assert.True(proposte[0].CadenzaProposta > 48);
        Assert.Contains("chiavi/ora", proposte[0].Perche, StringComparison.Ordinal);
    }

    /// <summary>
    /// Chi non ha una durata misurata NON si tocca: rallentare una caccia di cui non si conosce il
    /// prezzo non è una decisione, è un tiro a indovinare. È la stessa regola dell'ignoranza che
    /// non condanna, già pagata quattro volte in questo filone.
    /// </summary>
    [Fact]
    public void CHInonHAunCOSTOmisurato_nonSiTOCCA()
    {
        CostoCaccia[] cacce =
        [
            new(11, MinutiPerRun: 0, OreAttualiAlMese: 0, ChiaviPerOra: 0, CadenzaOre: 48),
            new(19, MinutiPerRun: 43.8, OreAttualiAlMese: 30, ChiaviPerOra: 2.0, CadenzaOre: 24),
        ];

        var proposte = HuntBudget.Riallinea(cacce, budgetOreMese: 10);

        Assert.DoesNotContain(proposte, p => p.ConfigurationId == 11);
    }

    /// <summary>Non si rallenta oltre le due settimane: una caccia che non si può giudicare non serve.</summary>
    [Fact]
    public void LaCADENZA_nonSUPERAilTETTOdelleDUEsettimane()
    {
        var proposte = HuntBudget.Riallinea(
            [new(19, 43.8, OreAttualiAlMese: 300, ChiaviPerOra: 0.1, CadenzaOre: 12)], budgetOreMese: 1);

        Assert.All(proposte, p => Assert.True(p.CadenzaProposta <= HuntBudget.MaxCadenzaOre));
    }

    // ------------------------------------------------------------------ K59 · la proiezione

    /// <summary>
    /// [Revisione 2026-09-03] <b>Le ore al mese seguono la cadenza IN VIGORE.</b> Il difetto: la
    /// proiezione dalle sole ore osservate non vedeva una cadenza appena riscritta, quindi lo sforo
    /// veniva riproposto e la stessa caccia raddoppiata a ogni giro (48 → 96 → 192 → 336 in tre ore).
    /// </summary>
    [Fact]
    public void LaPROIEZIONE_seguelaCADENZAinVIGORE_nonLeOREosservate()
    {
        // cfg 18: durata media 15,3 min, osservata a 30 run/mese. A 48h il ritmo è 15 run/mese = 3,8 h.
        Assert.Equal(15.3 / 60 * 15, HuntBudget.ProiettaOreAlMese(15.3, runAlMese: 30, cadenzaOre: 48), 3);
        // Riscritta a 96h: il ritmo osservato è lo STESSO, la proiezione si dimezza.
        Assert.Equal(15.3 / 60 * 7.5, HuntBudget.ProiettaOreAlMese(15.3, runAlMese: 30, cadenzaOre: 96), 3);
        // Senza cadenza propria si proietta dal ritmo osservato.
        Assert.Equal(15.3 / 60 * 30, HuntBudget.ProiettaOreAlMese(15.3, runAlMese: 30, cadenzaOre: 0), 3);
    }

    /// <summary>
    /// <b>Una cadenza è un minimo, non una schedulazione.</b> Una caccia con cadenza 48h che gira
    /// DAVVERO 3 volte al mese (fuori rotazione, a cron rado, o lanciata a mano) costa 3 run, non 15.
    /// </summary>
    [Fact]
    public void LaCADENZA_nonINVENTAunRITMOcheLaCACCIAnonHA()
    {
        Assert.Equal(40.0 / 60 * 3, HuntBudget.ProiettaOreAlMese(40, runAlMese: 3, cadenzaOre: 48), 3);
    }

    /// <summary>Il nullo: mai girata ⇒ costo zero, con o senza cadenza (non si inventa un prezzo).</summary>
    [Fact]
    public void ILNULLO_maiGIRATA_costaZERO()
    {
        Assert.Equal(0d, HuntBudget.ProiettaOreAlMese(0, 0, cadenzaOre: 48));
        Assert.Equal(0d, HuntBudget.ProiettaOreAlMese(15, 0, cadenzaOre: 0));
    }

    /// <summary>
    /// Il caso della cfg 19 al primo giro: UN run da 44 minuti (finestra di un giorno → 30 run/mese
    /// osservati) valeva 21,9 ore/mese; con la cadenza propria a 48h il ritmo è tagliato a 15 e il
    /// costo è 11 ore, cioè il numero vero. Senza cadenza resta la stima dall'osservato, che decade
    /// man mano che la finestra invecchia (il denominatore è l'età fino a oggi).
    /// </summary>
    [Fact]
    public void UNsoloRUN_conCADENZA_nonVIENEproiettatoCOMEquotidiano()
    {
        var conCadenza = HuntBudget.ProiettaOreAlMese(43.8, runAlMese: 30, cadenzaOre: 48);
        Assert.InRange(conCadenza, 10.5, 11.5);
        // Lo stesso run visto dieci giorni dopo: 3 run/mese osservati, 2,2 ore.
        Assert.InRange(HuntBudget.ProiettaOreAlMese(43.8, runAlMese: 3, cadenzaOre: 0), 2.0, 2.4);
    }

    /// <summary>
    /// [Revisione 2026-09-03] Il raddoppio 12→24→48→96→192 saltava 336: una caccia che entra SOLO a
    /// due settimane veniva scartata mentre Riallinea quella cadenza la propone.
    /// </summary>
    [Fact]
    public void LaCADENZAmassima_vienePROVATAprimaDiRINUNCIARE()
    {
        // 40 min/run: a 192h costa 2,5 h/mese (non entra in 1,6), a 336h costa 1,43 h (entra).
        Assert.Equal(HuntBudget.MaxCadenzaOre, HuntProposer.CadenzaCheEntra(minutiPerRun: 40, oreResidueAlMese: 1.6));
        Assert.Equal(0, HuntProposer.CadenzaCheEntra(minutiPerRun: 40, oreResidueAlMese: 1.0));
    }

    [Fact]
    public void ILRACCONTO_diceIlNUMEROanchesENONcSTETTO()
    {
        Assert.Contains("non è governato", HuntBudget.Racconta(Reali, 0), StringComparison.Ordinal);
        Assert.Contains("dentro", HuntBudget.Racconta(Reali, 100), StringComparison.Ordinal);
        Assert.Contains("di troppo", HuntBudget.Racconta(Reali, 5), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ K60 · la proposta

    [Fact]
    public void LaCADENZA_eLaPIUfittaCHEentraNELbudget()
    {
        // 10 minuti a run: a 12h di cadenza fanno 10 h/mese, a 24h ne fanno 5.
        Assert.Equal(12, HuntProposer.CadenzaCheEntra(minutiPerRun: 10, oreResidueAlMese: 20));
        Assert.Equal(24, HuntProposer.CadenzaCheEntra(minutiPerRun: 10, oreResidueAlMese: 6));
    }

    /// <summary>
    /// <b>Il nullo della proposta.</b> Se non entra nemmeno al ritmo più lento, la proposta NON si
    /// fa: farla e poi sforare sarebbe promettere ore che non ci sono.
    /// </summary>
    [Theory]
    [InlineData(1000, 0.1)]
    [InlineData(10, 0)]
    [InlineData(0, 50)]
    public void ILNULLO_seNONentra_nienteCADENZA(double minuti, double oreResidue)
        => Assert.Equal(0, HuntProposer.CadenzaCheEntra(minuti, oreResidue));

    [Fact]
    public void ILPREZZOdellaPROPOSTA_ePARTEdellETICHETTA()
    {
        var p = new CacciaProposta("15m", ["A/USDT", "B/USDT"], ModelloId: 20, MinutiStimati: 6, CadenzaOre: 24);

        Assert.Contains("15m", p.Etichetta, StringComparison.Ordinal);
        Assert.Contains("mai cacciate", p.Etichetta, StringComparison.Ordinal);
        Assert.Contains("h/mese", p.Etichetta, StringComparison.Ordinal);
        Assert.Equal(3.0, p.OreAlMese, 1);   // 6 min × 30 run/mese = 3 ore
    }

    /// <summary>
    /// L'universo di una proposta resta PICCOLO di proposito: un universo grande moltiplica i
    /// tentativi DENTRO il run, cioè alza SR* — l'unico posto dove la molteplicità è davvero
    /// contata. È il contrario dell'istinto «più serie, più possibilità».
    /// </summary>
    [Fact]
    public void LUNIVERSOproposto_restaPICCOLO()
        => Assert.InRange(HuntProposer.SeriePerProposta, 5, 12);
}
