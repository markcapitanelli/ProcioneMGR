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
        new(19, MinutiPerRun: 43.8, OreAttualiAlMese: 11.0, ChiaviPerOra: 2.0, CadenzaOre: 48),
        new(18, MinutiPerRun: 15.3, OreAttualiAlMese: 7.6, ChiaviPerOra: 1.2, CadenzaOre: 48),
        new(20, MinutiPerRun: 9.2, OreAttualiAlMese: 5.7, ChiaviPerOra: 5.6, CadenzaOre: 0),
        new(17, MinutiPerRun: 3.7, OreAttualiAlMese: 3.8, ChiaviPerOra: 18.3, CadenzaOre: 0),
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
