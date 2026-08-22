using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Difetto C, 2026-08-22] <b>La gamba schierata non era quella cliccata.</b>
///
/// <para><c>GreyDeployer.DeployAsync</c> risolveva il candidato con <c>FirstOrDefault</c> sulla terna
/// (strategia, simbolo, timeframe), e il form metteva proprio quella terna nel <c>value</c>
/// dell'<c>option</c>. Ma la terna <b>non è una chiave</b>: <c>CreativeDiscoveryStage</c> conferma più
/// specifiche distinte della stessa meta-strategia sulla stessa serie — è esattamente il motivo per
/// cui <see cref="PipelineCandidateKey"/> esiste e appende l'impronta dei parametri.</para>
///
/// <para>Le due liste divergevano: <c>ListGreyAsync</c> ordina per Sharpe holdout decrescente, la
/// risoluzione seguiva l'ordine dell'artifact. Misurato sugli artifact il 2026-08-22: <b>12 terne
/// ambigue distinte</b> su 1.414 righe grigie in 146 run, e su <b>2 dei 10 run raggiungibili dal
/// menù</b> l'ambiguità cade sulla riga <b>preselezionata</b> — l'operatore apre, non tocca niente,
/// preme Schiera e ottiene un'altra strategia.</para>
///
/// <para>La fixture di questi test è il caso reale del run <c>b49a4c8c</c> (2026-08-21): due
/// <c>Composite XLM/USDT 4h</c>, una regola a 3 condizioni con Sharpe 1,2915 su 8 trade e una a 2
/// condizioni con 0,5289 su 3. Parametri copiati dall'artifact.</para>
/// </summary>
public class GreyDeployerResolutionTests
{
    /// <summary>La regola a 3 condizioni: è quella che la UI mostra in cima (Sharpe 1,2915).</summary>
    private static ValidatedCandidate XlmTreCondizioni() => new()
    {
        StrategyName = "Composite",
        Symbol = "XLM/USDT",
        Timeframe = "4h",
        Parameters = new()
        {
            ["Logic"] = 0, ["ExitOp1"] = 0, ["EntryOp1"] = 1, ["EntryOp2"] = 1, ["EntryOp3"] = 1,
            ["ExitSig1"] = 1, ["ExitThr1"] = 20, ["Direction"] = 0,
            ["EntrySig1"] = 1, ["EntrySig2"] = 8, ["EntrySig3"] = 3,
            ["EntryThr1"] = 80, ["EntryThr2"] = 65, ["EntryThr3"] = 50,
            ["ExitCount"] = 1, ["EntryCount"] = 3,
        },
        HoldoutSharpe = 1.2915m,
        HoldoutTrades = 8,
        HoldoutProfitFactor = 4.474m,
        HoldoutMaxDrawdown = 3.835m,
        SelectionSharpe = 1.0181m,
        Survived = false,
        RejectReason = "Solo 8 trade in holdout (< 20)",
    };

    /// <summary>La regola a 2 condizioni: viene PRIMA nell'artifact (indice 135 contro 137).</summary>
    private static ValidatedCandidate XlmDueCondizioni() => new()
    {
        StrategyName = "Composite",
        Symbol = "XLM/USDT",
        Timeframe = "4h",
        Parameters = new()
        {
            ["Logic"] = 0, ["ExitOp1"] = 0, ["EntryOp1"] = 1, ["EntryOp2"] = 0,
            ["ExitSig1"] = 1, ["ExitThr1"] = 20, ["Direction"] = 0,
            ["EntrySig1"] = 1, ["EntrySig2"] = 5,
            ["EntryThr1"] = 80, ["EntryThr2"] = 20,
            ["ExitCount"] = 1, ["EntryCount"] = 2,
        },
        HoldoutSharpe = 0.5289m,
        HoldoutTrades = 3,
        HoldoutProfitFactor = 11.054m,
        HoldoutMaxDrawdown = 3.212m,
        SelectionSharpe = 1.2303m,
        Survived = false,
        RejectReason = "Solo 3 trade in holdout (< 20)",
    };

    // ------------------------------------------------------------------ l'identità deve discriminare

    /// <summary>
    /// È la porta da cui il difetto rientrerebbe <b>senza toccare <c>ResolveGrey</c></b>: se qualcuno
    /// accorciasse <see cref="PipelineCandidateKey"/> togliendo l'impronta dei parametri, le due
    /// chiavi tornerebbero identiche e la risoluzione per identità ridiventerebbe una risoluzione
    /// per terna.
    /// </summary>
    [Fact]
    public void StessaTerna_ParametriDiversi_ProduconoChiaviDIVERSE()
    {
        var a = XlmTreCondizioni();
        var b = XlmDueCondizioni();

        Assert.Equal("Composite", a.StrategyName);
        Assert.Equal(a.StrategyName, b.StrategyName);
        Assert.Equal(a.Symbol, b.Symbol);
        Assert.Equal(a.Timeframe, b.Timeframe);   // la terna è la stessa...
        Assert.NotEqual(a.Key, b.Key);            // ...l'identità no
    }

    /// <summary>
    /// Il caso reale di <c>b49a4c8c</c>, riprodotto alla lettera: la gamba cliccata è quella in cima
    /// alla lista (Sharpe 1,29 su 8 trade), ma nell'artifact viene <b>dopo</b> l'altra. Con la
    /// risoluzione sulla terna, <c>FirstOrDefault</c> restituiva la regola a 2 condizioni.
    /// </summary>
    [Fact]
    public void DueGrigiStessaTerna_RisolveLaGambaCLICCATA_NonLaPrimaDellArtifact()
    {
        // Ordine dell'artifact: prima la peggiore (idx 135), poi quella mostrata in cima (idx 137).
        var artifact = new List<ValidatedCandidate> { XlmDueCondizioni(), XlmTreCondizioni() };
        var cliccata = XlmTreCondizioni();

        var (risolto, errore) = GreyDeployer.ResolveGrey(artifact, cliccata.Key);

        Assert.Null(errore);
        Assert.NotNull(risolto);
        Assert.Equal(8, risolto!.HoldoutTrades);            // <- con la terna erano 3
        Assert.Equal(1.2915m, risolto.HoldoutSharpe);       // <- con la terna era 0,5289
    }

    /// <summary>
    /// La risoluzione per terna è <b>per costruzione</b> dipendente dall'ordine della lista: in una
    /// delle due permutazioni restituisce l'altra gamba. Quella per identità no.
    /// </summary>
    [Fact]
    public void LaRisoluzioneNONDipendeDallOrdineDellaLista()
    {
        var chiave = XlmTreCondizioni().Key;

        var (avanti, _) = GreyDeployer.ResolveGrey([XlmDueCondizioni(), XlmTreCondizioni()], chiave);
        var (indietro, _) = GreyDeployer.ResolveGrey([XlmTreCondizioni(), XlmDueCondizioni()], chiave);

        Assert.Equal(avanti!.HoldoutTrades, indietro!.HoldoutTrades);
        Assert.Equal(8, avanti.HoldoutTrades);
    }

    // ------------------------------------------------------------------------------- fail-closed

    /// <summary>
    /// <b>Guardia di contratto</b>, non riproduzione di un caso reale: la chiave degenera nella terna
    /// solo con zero parametri, e nessuno dei produttori di candidati ne emette senza (verificato
    /// 2026-08-22: 0 righe su 14.492). Ma se l'identità smettesse di discriminare, non si tira a
    /// sorte su un'azione che scrive su una corsia. Qualunque ritorno a <c>FirstOrDefault</c> o
    /// <c>First()</c> restituirebbe il primo invece di rifiutare.
    /// </summary>
    [Fact]
    public void ChiaveAMBIGUA_RIFIUTA_InveceDiSceglierePerPrima()
    {
        var uno = new ValidatedCandidate { StrategyName = "X", Symbol = "BTC/USDT", Timeframe = "1h", HoldoutSharpe = 1m, HoldoutTrades = 5 };
        var due = new ValidatedCandidate { StrategyName = "X", Symbol = "BTC/USDT", Timeframe = "1h", HoldoutSharpe = 2m, HoldoutTrades = 9 };
        Assert.Equal(uno.Key, due.Key);   // parametri vuoti => la chiave degenera nella terna

        var (risolto, errore) = GreyDeployer.ResolveGrey([uno, due], uno.Key);

        Assert.Null(risolto);
        Assert.NotNull(errore);
        Assert.Contains("AMBIGUA", errore, StringComparison.Ordinal);
    }

    /// <summary>Se il messaggio torna generico, l'operatore non capisce quale gamba manchi.</summary>
    [Fact]
    public void ChiaveAssente_RifiutaEDICEQuale()
    {
        var (risolto, errore) = GreyDeployer.ResolveGrey([XlmDueCondizioni()], "Composite XLM/USDT 4h #deadbeef");

        Assert.Null(risolto);
        Assert.Contains("#deadbeef", errore!, StringComparison.Ordinal);
    }

    [Fact]
    public void ChiaveVuota_RifiutaSenzaEsplodere()
    {
        foreach (var chiave in new[] { "", "   " })
        {
            var (risolto, errore) = GreyDeployer.ResolveGrey([XlmDueCondizioni()], chiave);
            Assert.Null(risolto);
            Assert.NotNull(errore);
        }
    }

    [Fact]
    public void ListaVuota_RifiutaInveceDiEsplodere()
    {
        var (risolto, errore) = GreyDeployer.ResolveGrey([], XlmTreCondizioni().Key);

        Assert.Null(risolto);
        Assert.NotNull(errore);
    }
}
