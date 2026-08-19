using ProcioneMGR.Data;
using ProcioneMGR.Services.Monitoring.Drift;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I6] <b>La capacità del monitor di deriva di dire di no.</b>
///
/// Il difetto che copre è la classe più punita del progetto: un controllo che dice la cosa
/// rassicurante indipendentemente dalla realtà. Tre condizioni — candele insufficienti, modello
/// senza feature dichiarate, finestra corrente sovrapposta al periodo di training — producevano
/// tutte <c>Overall = None</c>, cioè il badge verde, indistinguibile da «ho guardato e va tutto
/// bene». Il commento sul campo <c>TotalFeatures</c> diceva «0 = check saltato», ma nessuna
/// superficie lo leggeva così: la distinzione esisteva nella testa di chi aveva scritto il campo,
/// non nel prodotto.
///
/// <para>La terza condizione è la più insidiosa perché non sembra un guasto: confrontare la finestra
/// recente con la distribuzione di training quando la prima è <i>contenuta</i> nella seconda è
/// confrontare un campione con la popolazione che lo contiene. Non può quasi mai allarmare, e quel
/// silenzio si legge come stabilità.</para>
/// </summary>
public class DriftSkipReasonTests
{
    private static readonly DateTime TrainTo = new(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

    private static SavedMlModel Model(string factorsJson = """["Rsi","Atr"]""", DateTime? trainingTo = null) => new()
    {
        Id = 1,
        Name = "m",
        Symbol = "BTC/USDT",
        Timeframe = "1h",
        FactorsJson = factorsJson,
        TrainingDataFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        TrainingDataTo = trainingTo ?? TrainTo,
    };

    /// <summary>Candele a partire da <paramref name="start"/>, una all'ora.</summary>
    private static List<OhlcvData> Candles(int count, DateTime start) =>
        Enumerable.Range(0, count)
            .Select(i => new OhlcvData { Symbol = "BTC/USDT", Timeframe = "1h", TimestampUtc = start.AddHours(i), Close = 100 + i })
            .ToList();

    private static DriftMonitorOptions Opt(int recentCandles = 200) => new() { RecentCandles = recentCandles };

    /// <summary>Finestra tutta DOPO il training, abbastanza lunga: è il caso in cui si giudica davvero.</summary>
    [Fact]
    public void FinestraSanaEDopoIlTraining_NonSiSalta()
    {
        var skip = FeatureDriftWorker.DescribeSkip(Model(), Candles(200, TrainTo.AddDays(1)), Opt());

        Assert.Null(skip);
    }

    /// <summary>
    /// Caso (a). Prima produceva <c>Overall=None</c> con zero feature, cioè il verde.
    /// </summary>
    [Fact]
    public void CandeleInsufficienti_SiSaltaELoDice()
    {
        var skip = FeatureDriftWorker.DescribeSkip(Model(), Candles(50, TrainTo.AddDays(1)), Opt(recentCandles: 200));

        Assert.NotNull(skip);
        Assert.Contains("insufficienti", skip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("50", skip, StringComparison.Ordinal);
        Assert.Contains("200", skip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Il pavimento è lo stesso dell'invio (<c>Math.Max(20, RecentCandles)</c>): due regole per la
    /// stessa soglia darebbero due verdetti sullo stesso modello — il difetto già pagato in D2 e con
    /// <c>SeriesFreshness</c>.
    /// </summary>
    [Fact]
    public void IlPavimentoNonScendeMaiSottoVenti()
    {
        // RecentCandles=5 non autorizza un check su 5 candele: il minimo legale resta 20.
        var skip = FeatureDriftWorker.DescribeSkip(Model(), Candles(10, TrainTo.AddDays(1)), Opt(recentCandles: 5));

        Assert.NotNull(skip);
        Assert.Contains("20", skip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Caso (b): «il modello ha feature valutabili?» NON si decide qui. La risposta la dà il monitor,
    /// e leggere <c>FactorsJson</c> per conto proprio sarebbe una seconda regola sulla stessa
    /// domanda — due regole che possono divergere sullo stesso modello, il difetto già pagato in D2
    /// e con <c>SeriesFreshness</c>. Il salto per report vuoto si dichiara dopo la valutazione.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("   ")]
    public void FactorsJsonVuoto_NonEDecisoQui(string factorsJson)
    {
        var skip = FeatureDriftWorker.DescribeSkip(Model(factorsJson), Candles(200, TrainTo.AddDays(1)), Opt());

        Assert.Null(skip);
    }

    /// <summary>
    /// <b>Caso (c), il più insidioso.</b> La finestra recente cade INTERAMENTE dentro il periodo di
    /// training: il confronto sarebbe fra un campione e la popolazione che lo contiene, quindi non
    /// potrebbe quasi mai allarmare — e quel silenzio si leggeva come stabilità.
    /// </summary>
    [Fact]
    public void FinestraDentroIlPeriodoDiTraining_SiSaltaEDichiaraLaPercentuale()
    {
        var dentro = Candles(200, TrainTo.AddDays(-30)); // 200 ore prima della fine del training

        var skip = FeatureDriftWorker.DescribeSkip(Model(), dentro, Opt());

        Assert.NotNull(skip);
        Assert.Contains("si sovrappone al periodo di training", skip, StringComparison.Ordinal);
        Assert.Contains("100%", skip, StringComparison.Ordinal);
        Assert.Contains("popolazione che contiene il campione", skip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sovrapposizione PARZIALE: la finestra comincia prima della fine del training e prosegue dopo.
    /// Va comunque saltata — un confronto per metà contaminato non è un verdetto a metà, è un
    /// verdetto inaffidabile — e la percentuale dichiarata dev'essere quella vera, non 100.
    /// </summary>
    [Fact]
    public void SovrapposizioneParziale_SiSaltaConLaPercentualeVera()
    {
        // 200 candele orarie che iniziano 50 ore prima della fine del training: 51 dentro (inclusa
        // quella esattamente sul confine), 149 fuori.
        var skip = FeatureDriftWorker.DescribeSkip(Model(), Candles(200, TrainTo.AddHours(-50)), Opt());

        Assert.NotNull(skip);
        Assert.Contains("si sovrappone", skip, StringComparison.Ordinal);
        Assert.Contains("26%", skip, StringComparison.Ordinal); // 51/200 arrotondato
        Assert.DoesNotContain("100%", skip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il controllo sul rumore, nella direzione opposta.</b> Un modello senza data di training
    /// dichiarata (<c>default</c>) NON deve far saltare il check: sarebbe un rifiuto costruito
    /// sull'ignoranza, e un monitor che si rifiuta di guardare è inutile quanto uno che dice sempre
    /// verde. Un salto di troppo è un guasto come un salto di meno.
    /// </summary>
    [Fact]
    public void ModelloSenzaDataDiTraining_NonFaSaltareIlCheck()
    {
        var senzaData = Model(trainingTo: default);

        var skip = FeatureDriftWorker.DescribeSkip(senzaData, Candles(200, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)), Opt());

        Assert.Null(skip);
    }

    /// <summary>
    /// L'ordine dei controlli è parte del contratto: con troppe poche candele si dice QUELLO, non la
    /// sovrapposizione — il motivo più prossimo è quello che l'operatore può rimediare.
    /// </summary>
    [Fact]
    public void CandelePocheEFinestraSovrapposta_VinceIlMotivoPiuProssimo()
    {
        var skip = FeatureDriftWorker.DescribeSkip(Model(), Candles(30, TrainTo.AddDays(-30)), Opt(recentCandles: 200));

        Assert.NotNull(skip);
        Assert.Contains("insufficienti", skip, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// La riga distingue un rinvio da un giudizio, e la distinzione è leggibile senza interpretare
    /// <c>Overall</c>: è ciò che permette alla UI di non colorare di verde un check mai eseguito.
    /// </summary>
    [Fact]
    public void UnaRigaSaltata_NonSiDichiaraUnVerdetto()
    {
        var saltata = new DriftCheckResult { SkipReason = "candele recenti insufficienti: 10 su 200 richieste" };
        var giudizio = new DriftCheckResult { Overall = DriftSeverity.None, TotalFeatures = 12 };

        Assert.False(saltata.IsVerdict);
        Assert.True(giudizio.IsVerdict);
    }

    // ---------------------------------------------------------------------------------------
    // IL QUARTO MODO DI DIRE VERDE
    //
    // Trovato dalla revisione avversaria del 2026-08-18 DENTRO la correzione che ne eliminava tre.
    // I tre rilevatori (PSI, KS, Page-Hinkley) restituiscono `DriftSeverity.None` anche quando NON
    // hanno potuto misurare: quando le osservazioni valide — dopo il warm-up del fattore e dopo lo
    // scarto dei null — sono sotto MinObservations, rispondono «Dati insufficienti». Il worker
    // vedeva `reports.Count > 0` e salvava un GIUDIZIO verde costruito su rilevatori che avevano
    // dichiarato di non aver guardato.
    //
    // È dormiente con la configurazione di fabbrica, e si apre alla prima taratura delle soglie che
    // I6 ha appena reso amministrabili: basta alzare «Osservazioni min» sopra le osservazioni
    // disponibili perché OGNI riga diventi un falso «pulito».
    // ---------------------------------------------------------------------------------------

    private static FactorDriftReport Report(string name, int refCount, int curCount, DriftSeverity severity = DriftSeverity.None) => new()
    {
        FeatureName = name,
        ReferenceCount = refCount,
        CurrentCount = curCount,
        Results = [new DriftResult("Psi", 0.01, null, severity, "—")],
    };

    /// <summary>Osservazioni sufficienti da entrambi i lati: la feature è stata davvero misurata.</summary>
    [Fact]
    public void FeatureConOsservazioniSufficienti_EMisurata()
        => Assert.True(FeatureDriftWorker.IsMeasured(Report("rsi", 500, 180), new DriftThresholds { MinObservations = 20 }));

    /// <summary>
    /// <b>Il caso del quarto verde.</b> Sotto il pavimento da UNO SOLO dei due lati il rilevatore ha
    /// risposto «dati insufficienti»: non è una misura, e contarla come «nessuna deriva» è il verde
    /// falso. Entrambi i lati vanno controllati — un riferimento profondo non salva una finestra
    /// corrente vuota, né viceversa.
    /// </summary>
    [Theory]
    [InlineData(500, 3)]    // finestra corrente troppo corta (warm-up del fattore che si mangia le barre)
    [InlineData(3, 180)]    // riferimento troppo corto
    [InlineData(0, 0)]      // nulla da nessuna parte
    public void FeatureSottoIlPavimento_NonEMisurata(int refCount, int curCount)
        => Assert.False(FeatureDriftWorker.IsMeasured(Report("rsi", refCount, curCount), new DriftThresholds { MinObservations = 20 }));

    /// <summary>
    /// <b>Il caso che rende il difetto raggiungibile</b>, e che nessuna configurazione di fabbrica
    /// produce: le osservazioni bastano per il pavimento di default (20) ma non per una soglia
    /// alzata dal pannello. È esattamente la taratura che la card di I6 invita a fare.
    /// </summary>
    [Fact]
    public void SogliaAlzataDalPannello_RendeNonMisurataUnaFeatureCheLoEraColDefault()
    {
        var report = Report("rsi", 500, 150);

        Assert.True(FeatureDriftWorker.IsMeasured(report, new DriftThresholds { MinObservations = 20 }));
        Assert.False(FeatureDriftWorker.IsMeasured(report, new DriftThresholds { MinObservations = 300 }));
    }

    /// <summary>
    /// Un verdetto diverso da <c>None</c> è una misura <b>per definizione</b>: i rilevatori
    /// rispondono <c>None</c> quando non hanno potuto guardare, quindi un Warning o un Alert può
    /// venire solo da un confronto realmente eseguito. La regola vale anche con conteggi bassi —
    /// non allarga mai il silenzio, aggiunge solo casi misurati.
    /// </summary>
    [Theory]
    [InlineData(DriftSeverity.Warning)]
    [InlineData(DriftSeverity.Alert)]
    public void UnAllarmeEUnaMisuraPerDefinizione(DriftSeverity severity)
        => Assert.True(FeatureDriftWorker.IsMeasured(
            Report("rsi", refCount: 0, curCount: 0, severity), new DriftThresholds { MinObservations = 300 }));

    /// <summary>
    /// Un report SENZA risultati non è una misura: è il caso «fattore non più costruibile», che il
    /// monitor impacchetta comunque in un report per non perdere traccia del fattore. Conterlo come
    /// misurato lo trasformerebbe in un «nessuna deriva» su una feature che non esiste più.
    /// </summary>
    [Fact]
    public void ReportSenzaRisultati_NonEMisurato()
    {
        var vuoto = new FactorDriftReport { FeatureName = "sparito", ReferenceCount = 500, CurrentCount = 200, Results = [] };

        Assert.False(FeatureDriftWorker.IsMeasured(vuoto, new DriftThresholds { MinObservations = 20 }));
    }

    /// <summary>
    /// <b>Il controllo sul rumore, nella direzione opposta</b>: il pavimento non può scendere sotto
    /// 1, altrimenti una configurazione con <c>MinObservations = 0</c> dichiarerebbe «misurata» una
    /// feature senza una sola osservazione — un monitor che promette di aver guardato il nulla.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void PavimentoNonPositivo_NonRendeMisurabileIlNulla(int minObservations)
        => Assert.False(FeatureDriftWorker.IsMeasured(Report("x", 0, 0), new DriftThresholds { MinObservations = minObservations }));

    /// <summary>
    /// La soglia usata è la STESSA che usano i rilevatori, presa dalle stesse opzioni: due pavimenti
    /// per la stessa domanda darebbero due verdetti sulla stessa feature — il difetto già pagato in
    /// D2 e con <c>SeriesFreshness</c>. Qui si pinna che il confronto sia proprio contro
    /// <c>MinObservations</c> e non contro una costante locale.
    /// </summary>
    [Fact]
    public void IlPavimentoEQuelloDeiRilevatori_NonUnaCostanteLocale()
    {
        var report = Report("rsi", 25, 25);

        Assert.True(FeatureDriftWorker.IsMeasured(report, new DriftThresholds { MinObservations = 25 }));
        Assert.False(FeatureDriftWorker.IsMeasured(report, new DriftThresholds { MinObservations = 26 }));
    }
}
