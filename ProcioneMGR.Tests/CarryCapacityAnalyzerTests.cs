using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Carry;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I16 ≡ F12] La capacità del carry: a che taglia l'edge si annulla, e a che soglia conviene
/// entrare. Sono la stessa domanda vista da due lati, perché entrambe passano dal COSTO.
/// </summary>
public class CarryCapacityAnalyzerTests
{
    private static CarryConfiguration Config() => new()
    {
        InitialCapital = 10_000m, PositionSizePercent = 50m,
        EnterAnnualFundingPercent = 5m, ExitAnnualFundingPercent = 2m,
        TrailingFundingEvents = 9, FundingEventsPerDay = 3,
        SpotFeePercent = 0.1m, PerpFeePercent = 0.05m, SlippagePercent = 0.03m,
    };

    /// <summary>Serie di funding costante, per isolare l'effetto della taglia dal caso.</summary>
    private static List<FundingRatePoint> Costante(decimal annualPercent, int giorni)
    {
        var per8h = annualPercent / (3m * 365m);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, giorni * 3)
            .Select(i => new FundingRatePoint(t0.AddHours(i * 8), per8h))
            .ToList();
    }

    // --- L'impatto √ ------------------------------------------------------------------------------

    /// <summary>
    /// <b>La legge è concava, ed è il punto.</b> Quadruplicare la taglia raddoppia l'impatto, non lo
    /// quadruplica: è l'evidenza empirica di Almgren, e distingue una curva di capacità da una
    /// proporzione. Con la legge lineare la capacità sarebbe molto più bassa di quanto è.
    /// </summary>
    [Fact]
    public void LImpattoCresceComeLaRadice_NonComeLaTaglia()
    {
        var adv = 1_000_000_000m;

        var a = CarryCapacityAnalyzer.ImpactPercent(1_000_000m, adv, 0.1m)!.Value;
        var b = CarryCapacityAnalyzer.ImpactPercent(4_000_000m, adv, 0.1m)!.Value;

        Assert.True(Math.Abs(b - 2m * a) < 0.0001m, $"atteso il doppio ({2 * a}), ottenuto {b}");
    }

    /// <summary>
    /// <b>Senza scala di liquidità non c'è curva.</b> ADV assente ⇒ <c>null</c>, mai zero: uno zero
    /// direbbe che una taglia qualunque è gratis, ed è esattamente il modo in cui una curva di
    /// capacità diventa rassicurante a prescindere dalla realtà.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SenzaAdv_NessunImpattoCalcolabile_MaiZero(int adv)
        => Assert.Null(CarryCapacityAnalyzer.ImpactPercent(1_000_000m, adv, 0.1m));

    /// <summary>E una riga senza ADV non entra nella curva: non vale zero, non esiste.</summary>
    [Fact]
    public void SenzaAdv_LaCurvaEVuota()
    {
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(20m, 400), Config(), averageDailyVolumeUsd: 0m, [1_000_000m], [5m]);

        Assert.Empty(curva);
        Assert.Contains("manca la scala di liquidità", CarryCapacityAnalyzer.Verdict(curva, 5m), StringComparison.Ordinal);
    }

    // --- Il pareggio: il numero che governa tutto -----------------------------------------------

    /// <summary>
    /// <b>Il conto che decide se una soglia ha senso.</b> Coi costi di riferimento un round trip
    /// costa 2·(0,1+0,03) + 2·(0,05+0,03) = 0,42% del nozionale. A 5% annualizzato si incassano
    /// 5/365 = 0,0137% al giorno, quindi servono <b>~31 giorni</b> in posizione solo per pareggiare.
    ///
    /// <para>È il numero da confrontare con la durata mediana degli episodi: se gli episodi durano
    /// meno del pareggio, la soglia è troppo bassa <i>a prescindere</i> da quanto rende in media.</para>
    /// </summary>
    [Fact]
    public void IlPareggioAllaSogliaInVigore_ECircaUnMese()
    {
        var giorni = CarryCapacityAnalyzer.BreakEvenDays(5m, Config());

        Assert.NotNull(giorni);
        Assert.InRange(giorni!.Value, 29m, 33m);
    }

    /// <summary>Al doppio del premio il pareggio arriva in metà tempo: la relazione è quella attesa.</summary>
    [Fact]
    public void RaddoppiandoIlPremio_IlPareggioDimezza()
    {
        var a = CarryCapacityAnalyzer.BreakEvenDays(5m, Config())!.Value;
        var b = CarryCapacityAnalyzer.BreakEvenDays(10m, Config())!.Value;

        Assert.True(Math.Abs(a / 2m - b) < 0.2m, $"atteso ~{a / 2m}, ottenuto {b}");
    }

    /// <summary>
    /// Premio non positivo ⇒ <c>null</c>, e <b>null non è «tardi»</b>: è «mai». Restituire un numero
    /// grande farebbe passare l'impossibile per il lontano.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PremioNonPositivo_NessunPareggio(int premio)
        => Assert.Null(CarryCapacityAnalyzer.BreakEvenDays(premio, Config()));

    // --- La curva di capacità ---------------------------------------------------------------------

    /// <summary>
    /// <b>Il netto scende al crescere della taglia, ed è la definizione di capacità.</b> Su un
    /// premio costante e generoso, l'unica cosa che cambia fra i punti è lo slippage da impatto:
    /// se il netto non scendesse, la curva non starebbe misurando niente.
    /// </summary>
    [Fact]
    public void IlNettoScendeAlCrescereDellaTaglia()
    {
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(30m, 400), Config(), averageDailyVolumeUsd: 100_000_000m,
            [1_000_000m, 10_000_000m, 50_000_000m], [5m]);

        Assert.Equal(3, curva.Count);
        Assert.True(curva[0].NetAnnualizedPercent > curva[1].NetAnnualizedPercent);
        Assert.True(curva[1].NetAnnualizedPercent > curva[2].NetAnnualizedPercent);
        Assert.True(curva[0].SlippagePercent < curva[2].SlippagePercent);
    }

    /// <summary>
    /// La partecipazione all'ADV è riportata: è il numero che rende leggibile la curva. Un milione
    /// su cento di ADV è l'1%, e sotto quella soglia l'impatto è trascurabile — sopra, no.
    /// </summary>
    [Fact]
    public void LaPartecipazioneAllAdvEDichiarata()
    {
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(30m, 400), Config(), averageDailyVolumeUsd: 100_000_000m, [1_000_000m], [5m]);

        Assert.Equal(1m, Assert.Single(curva).ParticipationPercent);
    }

    /// <summary>
    /// <b>L'isteresi si conserva in PROPORZIONE quando la soglia si muove.</b> Tenerla fissa in
    /// valore assoluto la renderebbe via via più larga al salire della soglia, e la curva
    /// misurerebbe due cose che cambiano insieme invece di una: il classico confronto che sembra
    /// dire qualcosa sulla soglia e in realtà dice qualcosa sull'isteresi.
    /// </summary>
    [Fact]
    public void LIsteresiSiConservaInProporzione()
    {
        // Config di riferimento: uscita 2 su entrata 5 = 40%.
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(30m, 400), Config(), averageDailyVolumeUsd: 1_000_000_000m,
            [1_000_000m], [5m, 10m, 20m]);

        // Non si può leggere l'uscita dal punto, ma si può leggerne l'EFFETTO: con l'isteresi
        // proporzionale il numero di episodi su un premio COSTANTE resta uno a ogni soglia sotto il
        // premio — non si apre e chiude di più solo perché la soglia è salita.
        Assert.All(curva.Where(p => p.EnterThresholdPercent < 30m), p => Assert.Equal(1, p.Episodes));
    }

    // --- Trade/mese e durata mediana: la preferenza operativa del proprietario -------------------

    /// <summary>
    /// <b>Ogni punto della curva dichiara trade/mese e durata mediana.</b> Non è contorno: è il
    /// meccanismo — un carry che apre spesso e chiude presto paga il round trip più volte di quante
    /// riesca a ripagarlo. La regola della piattaforma è che nessuna misura si presenta senza questi
    /// due numeri.
    /// </summary>
    [Fact]
    public void OgniPuntoDichiaraFrequenzaEDurata()
    {
        // Premio che oscilla sopra e sotto la soglia: episodi multipli e misurabili.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var serie = Enumerable.Range(0, 3 * 365)
            .Select(i => new FundingRatePoint(t0.AddHours(i * 8),
                (i / 90) % 2 == 0 ? 20m / (3m * 365m) : -5m / (3m * 365m)))
            .ToList();

        var punto = Assert.Single(CarryCapacityAnalyzer.Sweep(
            serie, Config(), averageDailyVolumeUsd: 1_000_000_000m, [1_000_000m], [5m]));

        Assert.True(punto.Episodes > 1, "il seme deve produrre piu' episodi, altrimenti la mediana non significa nulla");
        Assert.True(punto.TradesPerMonth > 0m);
        Assert.True(punto.MedianHoldDays > 0m);
        Assert.InRange(punto.TimeInPositionPercent, 0m, 100m);
    }

    /// <summary>
    /// La durata è la MEDIANA e non la media, perché la distribuzione ha una coda lunga: un episodio
    /// da sei mesi sposterebbe la media e racconterebbe una strategia diversa da quella osservata.
    /// </summary>
    [Fact]
    public void LaDurataEMediana_NonMedia()
    {
        var episodi = new List<CarryEpisode>
        {
            new(new(2026, 1, 1), new(2026, 1, 3), 6, 1m, 0.4m, 0.6m),     // 2 giorni
            new(new(2026, 2, 1), new(2026, 2, 4), 9, 1m, 0.4m, 0.6m),     // 3 giorni
            new(new(2026, 3, 1), new(2026, 9, 1), 550, 40m, 0.4m, 39.6m), // 184 giorni: la coda
        };
        var esito = new CarryBacktestResult { EpisodeList = episodi };
        var serie = Costante(10m, 365);

        var (_, mediana) = CarryCapacityAnalyzer.FrequenzaEDurata(esito, serie);

        Assert.Equal(3m, mediana);   // la media sarebbe 63: un'altra strategia
    }

    // --- Il verdetto ------------------------------------------------------------------------------

    /// <summary>
    /// <b>Il verdetto si scrive anche quando dice che va già bene.</b> È ciò che il gate dell'item
    /// pretende, e la ragione è che un'analisi senza conclusione si legge come «non abbiamo trovato
    /// niente» invece che «abbiamo guardato, e la risposta è questa».
    /// </summary>
    [Fact]
    public void SeLaSogliaInVigoreEGiaLaMigliore_IlVerdettoLoDiceLoStesso()
    {
        // Premio 6%: la soglia a 5 apre, quelle a 10 e 20 non aprono mai ⇒ la migliore e' 5.
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(6m, 700), Config(), averageDailyVolumeUsd: 1_000_000_000m,
            [1_000_000m], [5m, 10m, 20m]);

        var verdetto = CarryCapacityAnalyzer.Verdict(curva, sogliaAttuale: 5m);

        Assert.Contains("È GIÀ la soglia in vigore", verdetto, StringComparison.Ordinal);
        Assert.Contains("questo è il verdetto, non l'assenza di uno", verdetto, StringComparison.Ordinal);
    }

    /// <summary>Il verdetto porta sempre trade/mese e durata mediana: la regola vale anche per la frase.</summary>
    [Fact]
    public void IlVerdettoPortaFrequenzaEDurata()
    {
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(20m, 700), Config(), averageDailyVolumeUsd: 1_000_000_000m, [1_000_000m], [5m]);

        var verdetto = CarryCapacityAnalyzer.Verdict(curva, 5m);

        Assert.Contains("trade/mese", verdetto, StringComparison.Ordinal);
        Assert.Contains("durata mediana", verdetto, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>«Non apre mai» e «apre e perde» sono due risposte diverse</b>, e il verdetto le distingue.
    /// Qui il premio (1% annuo) sta sotto la soglia più bassa della griglia: nessun episodio, mai.
    /// La risposta non è «la soglia migliore è X» ma «il premio non c'è».
    /// </summary>
    [Fact]
    public void SeNessunaSogliaApreMai_IlVerdettoDiceCheIlPremioNonCE()
    {
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(1m, 700), Config(), averageDailyVolumeUsd: 100_000_000m,
            [1_000_000m, 20_000_000m], [5m]);

        var verdetto = CarryCapacityAnalyzer.Verdict(curva, 5m);

        Assert.Contains("NESSUNA delle soglie provate apre mai", verdetto, StringComparison.Ordinal);
        Assert.Contains("il premio non c'e'", verdetto, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il difetto che la misura sui dati veri ha smascherato.</b> Su BTC negli ultimi 365 giorni
    /// il carry perde a ogni soglia che apre; una soglia alta che <i>non apre mai</i> rende 0,00%, e
    /// zero batte una perdita. La prima versione del verdetto presentava quindi «soglia migliore:
    /// 12%» — cioè <b>non fare niente</b> travestito da ottimo.
    ///
    /// <para>È la classe «controllo che rassicura a prescindere dalla realtà» applicata a un
    /// verdetto: la risposta giusta è che nessuna soglia funziona, e va detta con quelle parole.
    /// Qui si costruisce esattamente quello scenario — una soglia che apre e perde, una che non apre
    /// — e si pretende che l'astenersi non venga incoronato.</para>
    /// </summary>
    [Fact]
    public void UnaSogliaCheNonApreMai_NonPuoEssereLaMigliore()
    {
        // Premio 4%: la soglia a 3 apre (e con costi 0,42% a round trip perde), quella a 20 mai.
        var curva = CarryCapacityAnalyzer.Sweep(
            Costante(4m, 200), Config(), averageDailyVolumeUsd: 100_000_000m,
            [5_000_000m], [3m, 20m]);

        var apre = curva.Single(p => p.EnterThresholdPercent == 3m);
        var nonApre = curva.Single(p => p.EnterThresholdPercent == 20m);
        Assert.True(apre.Episodes > 0, "il seme deve far aprire la soglia bassa");
        Assert.Equal(0, nonApre.Episodes);
        Assert.True(apre.NetAnnualizedPercent < 0m, "e la soglia bassa deve perdere, altrimenti il caso non e' quello");

        var verdetto = CarryCapacityAnalyzer.Verdict(curva, 3m);

        Assert.DoesNotContain("Soglia migliore fra quelle che aprono: 20", verdetto, StringComparison.Ordinal);
        Assert.Contains("NESSUNA soglia che apra e' profittevole", verdetto, StringComparison.Ordinal);
    }
}
