using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K59, PRD autonomia-piena — Fase 4, 2026-09-03] <b>Il guardiano del budget misura, propone, e
/// scrive solo se glielo si dice.</b>
///
/// <para><b>La proprietà difesa</b> è che il report non menta mai sullo stato: dev'essere possibile
/// distinguere «sto dentro il tetto», «sforo e propongo», «sforo, propongo e ho scritto» e «non ho
/// mai guardato». L'ultimo è il caso che questo filone ha già pagato quattro volte — un controllo
/// che non ha guardato non deve poter sembrare un controllo che ha detto «va bene».</para>
/// </summary>
public class GuardianoDelBudgetK59Tests
{
    /// <summary>I costi VERI misurati il 2026-09-03, dopo i tagli e l'ingresso delle cinque config.</summary>
    private static readonly CostoCaccia[] Reali =
    [
        new(19, MinutiPerRun: 43.8, OreAttualiAlMese: 11.0, ChiaviPerOra: 2.0, CadenzaOre: 48, Run: 18),
        new(18, MinutiPerRun: 15.3, OreAttualiAlMese: 7.6, ChiaviPerOra: 1.2, CadenzaOre: 48, Run: 61),
        new(20, MinutiPerRun: 9.2, OreAttualiAlMese: 5.7, ChiaviPerOra: 5.6, CadenzaOre: 0, Run: 16),
        new(17, MinutiPerRun: 3.7, OreAttualiAlMese: 3.8, ChiaviPerOra: 18.3, CadenzaOre: 0, Run: 60),
        // Le quattro entrate in rotazione il 2026-09-03: hanno una durata storica ma quasi nessun
        // run recente, quindi la loro resa NON e' giudicabile.
        new(14, MinutiPerRun: 5.0, OreAttualiAlMese: 2.5, ChiaviPerOra: 0, CadenzaOre: 24, Run: 1),
        new(16, MinutiPerRun: 2.1, OreAttualiAlMese: 1.1, ChiaviPerOra: 0, CadenzaOre: 24, Run: 1),
        new(9, MinutiPerRun: 0.6, OreAttualiAlMese: 0.3, ChiaviPerOra: 0, CadenzaOre: 24, Run: 2),
        new(11, MinutiPerRun: 0, OreAttualiAlMese: 0, ChiaviPerOra: 0, CadenzaOre: 48, Run: 0),
    ];

    private static BudgetReport Report(double budget, bool applicate = false)
    {
        var proposte = HuntBudget.Riallinea(Reali, budget);
        return new BudgetReport(new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc), Reali, budget, proposte, applicate);
    }

    [Fact]
    public void ILCONSUMOreale_staINTORNOalleTRENTADUEore()
    {
        // Dopo i tagli su 18 e 19 e l'ingresso di quattro config economiche: il numero che il
        // proprietario userà per scegliere il tetto.
        Assert.InRange(Report(0).OreTotali, 30, 34);
    }

    [Fact]
    public void DENTROilTETTO_ilRACCONTOloDICE_eNONcSonoPROPOSTE()
    {
        var r = Report(40);

        Assert.Empty(r.Proposte);
        Assert.Equal(0, r.OreRisparmiabili);
        Assert.Contains("dentro", r.Racconto, StringComparison.Ordinal);
    }

    [Fact]
    public void SOPRAilTETTO_siPROPONEeSiDICEquantoSiRISPARMIA()
    {
        var r = Report(15);

        Assert.NotEmpty(r.Proposte);
        Assert.True(r.OreRisparmiabili > 0);
        Assert.Contains("di troppo", r.Racconto, StringComparison.Ordinal);
        // Applicate resta FALSO: proporre e scrivere sono due cose diverse, e il report non deve
        // poterle confondere.
        Assert.False(r.Applicate);
    }

    /// <summary>
    /// <b>Il nullo che conta.</b> Senza tetto impostato non si propone nulla — ma il racconto NON
    /// dice «va bene»: dice che il consumo non è governato da niente. È la distinzione fra «ho
    /// guardato e sta dentro» e «non c'è niente contro cui guardare», che in questo filone è già
    /// stata pagata con K40 e con K52.
    /// </summary>
    [Fact]
    public void ILNULLO_senzaTETTO_ilRACCONTOnonDICEcheVAbene()
    {
        var r = Report(0);

        Assert.Empty(r.Proposte);
        Assert.Contains("non è governato", r.Racconto, StringComparison.Ordinal);
        Assert.DoesNotContain("dentro", r.Racconto, StringComparison.Ordinal);
    }

    /// <summary>
    /// La config 11 non ha mai girato: costo zero, resa zero. Non dev'essere MAI proposta per il
    /// rallentamento — rallentare una caccia di cui non si conosce il prezzo non è una decisione.
    /// Ed è anche la config con la resa più bassa, quindi senza questa regola sarebbe la prima a
    /// cadere.
    /// </summary>
    [Fact]
    public void LaCONFIGmaiGIRATA_nonVIENEmaiRALLENTATA()
    {
        var r = Report(1);   // tetto assurdo: tutto ciò che può essere rallentato lo è

        Assert.NotEmpty(r.Proposte);
        Assert.DoesNotContain(r.Proposte, p => p.ConfigurationId == 11);
    }

    /// <summary>
    /// Chi rende di più non si tocca finché c'è chi rende meno. La 17 fa 18,3 chiavi/ora — nove
    /// volte la 19 — e dev'essere l'ultima della fila.
    /// </summary>
    /// <summary>
    /// <b>Il difetto trovato da questo test, e la ragione per cui esiste.</b> Nella prima versione
    /// l'ordine era la sola resa crescente — e le configurazioni entrate in rotazione il 2026-09-03
    /// hanno resa <c>0</c> perché non hanno ancora girato, non perché siano sterili. Finivano
    /// <b>prime</b> nella fila dei rallentamenti: la caccia appena aggiunta veniva frenata prima di
    /// aver potuto dimostrare qualcosa.
    ///
    /// <para>Ora chi non è giudicabile va in fondo, e si tocca solo se rallentare le misurate non
    /// basta.</para>
    /// </summary>
    [Fact]
    public void SiRALLENTAinORDINEdiRESAcrescente_MAlaNONgiudicabileVAinFONDO()
    {
        var proposte = HuntBudget.Riallinea(Reali, budgetOreMese: 20);
        var ordine = proposte.Select(p => p.ConfigurationId).ToList();

        Assert.Equal(18, ordine[0]);                    // 1,2 chiavi/ora su 61 run: giudicabile e bassa
        Assert.DoesNotContain(17, ordine);              // 18,3: non serve toccarla
    }

    /// <summary>
    /// [Revisione 2026-09-03] La proprietà «la non giudicabile va IN FONDO» provata senza
    /// condizioni: con un tetto assurdo tutte le rallentabili vengono proposte, e OGNI non
    /// giudicabile deve stare dopo OGNI giudicabile. La prima versione la proteggeva con un
    /// <c>if</c> che, con budget 20, non entrava mai — un'asserzione che non poteva fallire.
    /// </summary>
    [Fact]
    public void LeNONgiudicabili_stannoTUTTEdopoLEgiudicabili()
    {
        var proposte = HuntBudget.Riallinea(Reali, budgetOreMese: 1);
        var ordine = proposte.Select(p => p.ConfigurationId).ToList();

        var giudicabili = ordine.Where(id => id is 17 or 18 or 19 or 20).Select(id => ordine.IndexOf(id)).ToList();
        var nonGiudicabili = ordine.Where(id => id is 14 or 16 or 9).Select(id => ordine.IndexOf(id)).ToList();

        Assert.NotEmpty(giudicabili);
        Assert.NotEmpty(nonGiudicabili);
        Assert.True(nonGiudicabili.Min() > giudicabili.Max(),
            $"ordine: {string.Join(",", ordine)} — una non giudicabile precede una giudicabile");
        Assert.All(proposte.Where(p => p.ConfigurationId is 14 or 16 or 9),
            p => Assert.Contains("non è ancora giudicabile", p.Perche, StringComparison.Ordinal));
    }

    /// <summary>
    /// Il caso esplicito: con un tetto che si può rispettare rallentando le sole giudicabili, le
    /// nuove non vengono toccate affatto.
    /// </summary>
    [Fact]
    public void LeCACCEappenaENTRATE_nonSiTOCCANOseNONserve()
    {
        var proposte = HuntBudget.Riallinea(Reali, budgetOreMese: 25);

        Assert.NotEmpty(proposte);
        Assert.DoesNotContain(proposte, p => p.ConfigurationId is 14 or 16 or 9);
    }

    [Fact]
    public void ILREPORT_distingueLoSCRITTOdalPROPOSTO()
    {
        Assert.False(Report(15, applicate: false).Applicate);
        Assert.True(Report(15, applicate: true).Applicate);
    }
}
