using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Discovery;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Difetto A, 2026-08-22] <b>Il walk-forward della scoperta creativa non era un walk-forward.</b>
///
/// <para>Il ciclo di conferma di <c>StrategyComposer</c> passava al motore l'INTERA lista di candele
/// precaricate, e quell'overload <b>ignora <c>config.From/To</c></b>: il contratto dice che filtrare
/// tocca al chiamante, e nel core di <c>BacktestEngine</c> quelle due proprietà non compaiono mai.
/// Le N finestre erano quindi <b>N esecuzioni identiche</b> sul range intero, e la loro media è per
/// costruzione lo Sharpe della selezione.</para>
///
/// <para>Misurato su <c>ResearchCandidates</c>: <b>9.665 righe su 9.665</b> prodotte da questa fase
/// avevano <c>WalkForwardOosSharpe = round(SelectionSharpe, 2)</c>, contro <b>zero</b> delle 3.833
/// della discovery classica — che affetta davvero. Confermato su run appena eseguiti il 2026-08-21:
/// 64 righe su 64 sul run 1h, 82 su 141 sul 4h.</para>
///
/// <para>Conseguenza che nessuno aveva visto: con <c>oosSharpe == screenSharpe</c> il gate di
/// conferma era una <b>tautologia</b> — tutte le campagne vive hanno
/// <c>minOosSharpe == minScreenSharpe</c> — quindi la fase «conferma walk-forward» <b>non ha mai
/// respinto nulla</b>.</para>
/// </summary>
public class SottoperiodiDiSelezioneTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Curva con un punto per ora, capitale dato da una funzione dell'indice.</summary>
    private static BacktestResult Corsa(int ore, Func<int, decimal> capitale, params DateTime[] ingressi)
    {
        var r = new BacktestResult();
        for (var i = 0; i < ore; i++)
        {
            r.EquityCurve.Add(new EquityPoint { Timestamp = T0.AddHours(i), Capital = capitale(i) });
        }
        foreach (var e in ingressi) r.Trades.Add(new BacktestTrade { EntryTime = e, EntryPrice = 1m });
        return r;
    }

    // ------------------------------------------------------- il difetto: N finestre, N numeri uguali

    /// <summary>
    /// Il cuore del difetto. Una curva a <b>due regimi</b> — prima metà in salita costante, seconda
    /// in discesa costante — deve produrre due Sharpe di segno opposto. Col vecchio codice le due
    /// finestre erano due esecuzioni sull'intero range: due numeri <b>identici</b>, e la loro media
    /// era lo Sharpe della selezione.
    /// </summary>
    [Fact]
    public void DueSottoperiodi_DannoSharpeDIVERSI_NonDueVolteQuelloDellIntero()
    {
        // 200 ore: le prime 100 salgono, le seconde 100 scendono, con un po' di rumore alternato
        // (senza varianza lo Sharpe non è definito).
        var corsa = Corsa(200, i => i < 100
            ? 1000m + i * 2m + (i % 2 == 0 ? 0.5m : -0.5m)
            : 1200m - (i - 100) * 2m + (i % 2 == 0 ? 0.5m : -0.5m));

        var (primo, _) = StrategyComposer.MeasureSubPeriod(corsa, T0, T0.AddHours(100), 8760);
        var (secondo, _) = StrategyComposer.MeasureSubPeriod(corsa, T0.AddHours(100), T0.AddHours(200), 8760);

        Assert.NotNull(primo);
        Assert.NotNull(secondo);
        Assert.True(primo > 0m, $"la prima metà sale, Sharpe atteso positivo: {primo}");
        Assert.True(secondo < 0m, $"la seconda metà scende, Sharpe atteso negativo: {secondo}");
        Assert.NotEqual(primo!.Value, secondo!.Value);
    }

    /// <summary>
    /// Una finestra che copre l'intera curva NON deve dare lo stesso numero di una sua metà: se lo
    /// desse, vorrebbe dire che la segmentazione non sta segmentando.
    /// </summary>
    [Fact]
    public void LaFinestraIntera_DifferisceDaOgniSuaMeta()
    {
        var corsa = Corsa(200, i => i < 100
            ? 1000m + i * 2m + (i % 2 == 0 ? 0.5m : -0.5m)
            : 1200m - (i - 100) * 2m + (i % 2 == 0 ? 0.5m : -0.5m));

        var (intera, _) = StrategyComposer.MeasureSubPeriod(corsa, T0, T0.AddHours(200), 8760);
        var (meta, _) = StrategyComposer.MeasureSubPeriod(corsa, T0, T0.AddHours(100), 8760);

        Assert.NotNull(intera);
        Assert.NotEqual(intera!.Value, meta!.Value);
    }

    // ------------------------------------------------------------------- lo zero non è neutro

    /// <summary>
    /// Una finestra a capitale costante avrebbe varianza nulla, e <c>Statistics.SharpeRatio</c>
    /// restituirebbe <c>0m</c>: uno zero <b>fabbricato</b>, indistinguibile da una misura, che
    /// diluirebbe la media verso il basso senza che nessuno lo veda. Non si media ciò che non si è
    /// misurato.
    /// </summary>
    [Fact]
    public void SottoperiodoSenzaAttivita_NonEntraComeZero_MaComeNULL()
    {
        // Prima metà attiva, seconda metà completamente piatta (nessuna posizione aperta).
        var corsa = Corsa(200, i => i < 100 ? 1000m + i * 2m + (i % 2 == 0 ? 0.5m : -0.5m) : 1200m);

        var (attiva, _) = StrategyComposer.MeasureSubPeriod(corsa, T0, T0.AddHours(100), 8760);
        var (piatta, _) = StrategyComposer.MeasureSubPeriod(corsa, T0.AddHours(101), T0.AddHours(200), 8760);

        Assert.NotNull(attiva);
        Assert.Null(piatta);   // <- con Statistics.SharpeRatio nudo sarebbe 0m
    }

    [Fact]
    public void FinestraSenzaAlcunPunto_E_NULL_NonZero()
    {
        var corsa = Corsa(50, i => 1000m + i);

        var (fuori, trade) = StrategyComposer.MeasureSubPeriod(corsa, T0.AddHours(100), T0.AddHours(200), 8760);

        Assert.Null(fuori);
        Assert.Equal(0, trade);
    }

    [Fact]
    public void FinestraConDueSoliPunti_E_NULL()
    {
        var corsa = Corsa(200, i => 1000m + i * 2m + (i % 2 == 0 ? 0.5m : -0.5m));

        // [T0+10, T0+12) contiene i punti 10 e 11: col precedente sono 3, ma la finestra ne ha 2.
        var (poco, _) = StrategyComposer.MeasureSubPeriod(corsa, T0.AddHours(10), T0.AddHours(11), 8760);

        Assert.Null(poco);
    }

    // ------------------------------------------------------------ attribuzione dei trade

    /// <summary>
    /// Un trade aperto in W1 e chiuso in W2 conta <b>1 in W1 e 0 in W2</b>: l'attribuzione è per
    /// ingresso. Fissa una convenzione che prima non era scritta da nessuna parte.
    /// </summary>
    [Fact]
    public void IlTrade_SiAttribuisceAllaFinestraDIINGRESSO()
    {
        var corsa = Corsa(200, i => 1000m + i * 2m + (i % 2 == 0 ? 0.5m : -0.5m), T0.AddHours(90));

        var (_, inW1) = StrategyComposer.MeasureSubPeriod(corsa, T0, T0.AddHours(100), 8760);
        var (_, inW2) = StrategyComposer.MeasureSubPeriod(corsa, T0.AddHours(100), T0.AddHours(200), 8760);

        Assert.Equal(1, inW1);
        Assert.Equal(0, inW2);
    }

    /// <summary>
    /// Le finestre di <c>BuildOosWindows</c> sono contigue: la candela di giunzione non deve essere
    /// contata due volte. Fallisce se l'estremo destro diventa inclusivo.
    /// </summary>
    [Fact]
    public void FinestreContigue_PartizionanoSenzaDoppioni()
    {
        var ingressi = Enumerable.Range(0, 20).Select(k => T0.AddHours(k * 10)).ToArray();
        var corsa = Corsa(200, i => 1000m + i * 2m + (i % 2 == 0 ? 0.5m : -0.5m), ingressi);

        var (_, a) = StrategyComposer.MeasureSubPeriod(corsa, T0, T0.AddHours(100), 8760);
        var (_, b) = StrategyComposer.MeasureSubPeriod(corsa, T0.AddHours(100), T0.AddHours(200), 8760);

        Assert.Equal(ingressi.Length, a + b);   // nessun doppione, nessuna perdita
    }

    // ------------------------------------------------------------------ la provenienza

    /// <summary>
    /// I candidati «Ml» non hanno alcuna misura di selezione: lo <c>0m</c> del default finiva in
    /// archivio come «0,00», indistinguibile da una misura. Sono 51 righe su 51.
    /// </summary>
    [Fact]
    public void CandidatoMl_NonHaSharpeDiSelezione_ENonUnoZero()
    {
        var v = new ValidatedCandidate
        {
            StrategyName = "Ml",
            Symbol = "BTC/USDT",
            Timeframe = "1h",
            WalkForwardOosSharpe = 0m,
            SelectionSharpe = 1.234m,
        };

        Assert.Null(ResearchCandidateIndexer.WalkForwardBonificato(v));
        Assert.Equal(DiscoveryCandidate.SourceNone, ResearchCandidateIndexer.SorgenteStorica(v));
    }

    /// <summary>
    /// L'impronta della copia: famiglia della scoperta creativa + due decimali esatti + uguale a
    /// <c>round(SelectionSharpe, 2)</c>. Nessuna informazione si perde — quel valore <i>è</i>
    /// l'arrotondamento, e <c>SelectionSharpe</c> resta a piena precisione nella stessa riga.
    /// </summary>
    [Fact]
    public void LoStoricoCOPIATO_DiventaNull_EDichiaraPerche()
    {
        var copiato = new ValidatedCandidate
        {
            StrategyName = "Composite",
            Symbol = "XLM/USDT",
            Timeframe = "1h",
            WalkForwardOosSharpe = 0.64m,
            SelectionSharpe = 0.6391m,
        };

        Assert.Null(ResearchCandidateIndexer.WalkForwardBonificato(copiato));
        Assert.Equal("CopiaSelezionePreFix", ResearchCandidateIndexer.SorgenteStorica(copiato));
        Assert.Contains("COPIA", ResearchCandidateIndexer.SpiegaSorgenteWalkForward("CopiaSelezionePreFix"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Il rovescio, che conta quanto il diritto: un walk-forward VERO della discovery classica
    /// (`GridMeanReversion ALGO/USDT`: wf 4,9812 contro sel 0,181) non deve essere toccato.
    /// </summary>
    [Fact]
    public void LoStoricoVERO_NonVieneToccato()
    {
        var vero = new ValidatedCandidate
        {
            StrategyName = "GridMeanReversion",
            Symbol = "ALGO/USDT",
            Timeframe = "4h",
            WalkForwardOosSharpe = 4.9812m,
            SelectionSharpe = 0.181m,
        };

        Assert.Equal(4.9812m, ResearchCandidateIndexer.WalkForwardBonificato(vero));
        Assert.Equal("SconosciutaPreFix", ResearchCandidateIndexer.SorgenteStorica(vero));
    }

    /// <summary>
    /// Una famiglia della scoperta creativa il cui numero NON è la copia (due decimali ma diverso da
    /// round(sel,2)) resta intatta: la bonifica non deve essere un'accetta sul nome della strategia.
    /// </summary>
    [Fact]
    public void FamigliaCreativa_MaNumeroDiverso_NonVieneBonificata()
    {
        var v = new ValidatedCandidate
        {
            StrategyName = "Composite",
            Symbol = "XLM/USDT",
            Timeframe = "1h",
            WalkForwardOosSharpe = 0.91m,
            SelectionSharpe = 0.6391m,
        };

        Assert.Equal(0.91m, ResearchCandidateIndexer.WalkForwardBonificato(v));
    }

    /// <summary>
    /// Chi dichiara la propria provenienza non viene mai bonificato: la bonifica vale solo per lo
    /// storico, e la sua condizione d'ingresso è proprio <c>WalkForwardSource is null</c>.
    /// </summary>
    [Fact]
    public void UnCandidatoNUOVO_CheDichiaraLaProvenienza_NonVieneBonificato()
    {
        var v = new ValidatedCandidate
        {
            StrategyName = "Composite",
            Symbol = "XLM/USDT",
            Timeframe = "1h",
            WalkForwardOosSharpe = 0.64m,
            SelectionSharpe = 0.6391m,   // sarebbe l'impronta esatta della copia...
            WalkForwardSource = DiscoveryCandidate.SourceSelectionSubPeriods,
        };

        Assert.Equal(0.64m, ResearchCandidateIndexer.WalkForwardBonificato(v));   // ...ma è dichiarato
    }

    /// <summary>Ogni provenienza ha una spiegazione leggibile: un trattino muto sarebbe metà del difetto.</summary>
    [Fact]
    public void OgniProvenienza_HaUnaSpiegazione()
    {
        foreach (var s in new[]
        {
            DiscoveryCandidate.SourceWalkForward,
            DiscoveryCandidate.SourceSelectionSubPeriods,
            DiscoveryCandidate.SourceNone,
            DiscoveryCandidate.SourceUndeclared,
            "CopiaSelezionePreFix",
            "SconosciutaPreFix",
        })
        {
            var testo = ResearchCandidateIndexer.SpiegaSorgenteWalkForward(s);
            Assert.False(string.IsNullOrWhiteSpace(testo), $"provenienza «{s}» senza spiegazione");
            Assert.DoesNotContain("Provenienza non registrata", testo, StringComparison.Ordinal);
        }
    }
}
