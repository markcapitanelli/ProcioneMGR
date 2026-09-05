using System.Text.Json;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I11] Il denominatore condiviso: quanti trade ci si aspetta, e fra quanto si potrà giudicare.
///
/// Esiste come componente a sé perché lo consumano in <b>due</b> — il ritiro per inedia (I12) e il
/// freno per gamba (I13). Se ognuno se lo calcolasse per conto proprio avremmo due regole che
/// rispondono alla stessa domanda, e prima o poi due verdetti sulla stessa corsia: è il difetto già
/// pagato in D2 e con <c>SeriesFreshness</c>, e trovato <b>quattro volte</b> nella Fase 2 di questa
/// stessa ondata.
///
/// <para>La ragione operativa: la regola di ritiro esistente pretende ≥20 trade, e al 2026-08-19 le
/// corsie di flotta 3-7 avevano chiuso <b>da uno a sei trade ciascuna sul simbolo attuale (5, 1, 5, 6, 3) in 6-16 giorni</b>. Non sarebbero
/// state ritirabili mai, e una corsia che non si libera mai blocca la flotta e a monte il comitato.</para>
/// </summary>
public class TradeFrequencyTests
{
    // --- La frequenza attesa -------------------------------------------------------------------

    [Fact]
    public void FrequenzaAttesa_ETradeDivisoMesi()
        => Assert.Equal(10m, TradeFrequency.PerMonth(trades: 30, months: 3m));

    /// <summary>
    /// <b>Il caso-trappola.</b> Finestra non positiva ⇒ <c>null</c>, non zero: senza finestra la
    /// frequenza è un'illusione, e restituire zero la farebbe passare per «rarissima» invece che per
    /// «non derivabile» — cioè condannerebbe per inedia una gamba di cui non si sa nulla.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void FinestraNonPositiva_NonDerivabile_NonZero(int months)
        => Assert.Null(TradeFrequency.PerMonth(trades: 30, months: months));

    // --- Il tempo al verdetto ------------------------------------------------------------------

    /// <summary>
    /// <b>Il numero che nessuno dichiarava</b> (criterio AF2c-3): a 2 trade/mese servono DIECI mesi
    /// per i 20 trade che la regola di ritiro pretende. Non è un divieto — il giornaliero resta
    /// schierabile — ma va detto allo schieramento, non scoperto dopo mesi.
    /// </summary>
    [Fact]
    public void DueTradeAlMese_RichiedonoDieciMesiPerVentiTrade()
        => Assert.Equal(10m, TradeFrequency.MonthsToVerdict(2m, requiredTrades: 20));

    /// <summary>
    /// Frequenza zero ⇒ <c>null</c>, e <b>zero non è «subito»</b>: è «mai». È l'errore di lettura
    /// che trasformerebbe l'impossibile in un numero piccolo.
    /// </summary>
    [Fact]
    public void FrequenzaZero_IlVerdettoNonArrivaMai()
    {
        Assert.Null(TradeFrequency.MonthsToVerdict(0m, 20));
        Assert.Null(TradeFrequency.MonthsToVerdict(null, 20));
        Assert.Contains("MAI", TradeFrequency.Describe(0m, 20), StringComparison.Ordinal);
    }

    [Fact]
    public void LaFraseDichiaraSempreFrequenzaETempoInsieme()
    {
        var testo = TradeFrequency.Describe(2m, requiredTrades: 20);

        Assert.Contains("2 trade/mese", testo, StringComparison.Ordinal);
        Assert.Contains("10", testo, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaFrequenza_LaFraseLoDichiaraInveceDiTacere()
        => Assert.Contains("non derivabile", TradeFrequency.Describe(null, 20), StringComparison.Ordinal);

    // --- L'inedia --------------------------------------------------------------------------------

    /// <summary>
    /// <b>La coppia che dimostra perché il confronto è relativo e non un numero fisso.</b> Stessa
    /// osservazione esatta — 1 trade in 14 giorni — e due verdetti opposti, perché ciò che cambia è
    /// quanto ci si aspettava:
    /// <list type="bullet">
    /// <item>30/mese (giornaliero) ⇒ ne erano attesi ~14, ne è arrivato 1: <b>inedia</b>;</item>
    /// <item>2/mese ⇒ ne erano attesi ~0,93, ne è arrivato 1: <b>nella norma</b>.</item>
    /// </list>
    /// Un'unica soglia assoluta direbbe la cosa sbagliata su una delle due, e condannerebbe il
    /// giornaliero per il solo fatto di essere lento.
    /// </summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(2, false)]
    public void LInediaSiMisuraSullAtteso_NonSuUnNumeroFisso(int expectedPerMonth, bool starving)
        => Assert.Equal(starving, TradeFrequency.IsStarving(
            expectedPerMonth: expectedPerMonth, observed: 1, observation: TimeSpan.FromDays(14),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>
    /// <b>Il criterio è deliberatamente prudente</b>, e questo test lo inchioda: a 6 trade/mese
    /// attesi, 1 trade in 14 giorni è il 35% dell'atteso — sopra la soglia del 20%, quindi NON è
    /// inedia. Serve perché la tentazione, guardando «1 trade in due settimane», è di leggerlo come
    /// guasto a prescindere: il numero da guardare è il rapporto, non il conteggio.
    /// </summary>
    [Fact]
    public void SopraLaFrazione_NonEInedia_AnchePochiTrade()
        => Assert.False(TradeFrequency.IsStarving(
            expectedPerMonth: 6m, observed: 1, observation: TimeSpan.FromDays(14),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>
    /// <b>L'ignoranza non condanna.</b> Senza frequenza attesa — gambe configurate a mano, corsie
    /// 0-2 dell'impronta storica, ensemble creati prima del campo — nessun verdetto. È la stessa
    /// regola per cui la sonda degli agenti tiene «non determinabile» separato da «spento».
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void SenzaFrequenzaAttesa_NessunaCondanna(double? expected)
        => Assert.False(TradeFrequency.IsStarving(
            expectedPerMonth: expected is null ? null : (decimal)expected.Value,
            observed: 0, observation: TimeSpan.FromDays(60),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>
    /// Troppo presto per giudicare: sotto l'osservazione minima si tace, anche a zero trade. Senza
    /// questo, una corsia appena avviata verrebbe ritirata prima di aver avuto occasione di operare.
    /// </summary>
    [Fact]
    public void CorsiaAppenaAvviata_NonSiGiudica()
        => Assert.False(TradeFrequency.IsStarving(
            expectedPerMonth: 30m, observed: 0, observation: TimeSpan.FromDays(2),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>
    /// <b>Il controllo di livello 2</b>: frazione a zero = criterio DISATTIVATO, nessuna condanna
    /// possibile. È l'interruttore che rende l'item reversibile, e senza questo test «spento lascia
    /// il comportamento invariato» sarebbe una promessa.
    /// </summary>
    [Fact]
    public void FrazioneAZero_CriterioSpento_NessunaCondanna()
        => Assert.False(TradeFrequency.IsStarving(
            expectedPerMonth: 30m, observed: 0, observation: TimeSpan.FromDays(90),
            minFraction: 0m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>
    /// Il confine è calcolato sull'atteso NEL PERIODO, non sul mese: a 30/mese in 30 giorni gli
    /// attesi sono 30, quindi il 20% è 6 — 5 trade condannano, 7 no.
    /// </summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(7, false)]
    public void IlConfineSeguIlPeriodoOsservato(int observed, bool starving)
        => Assert.Equal(starving, TradeFrequency.IsStarving(
            expectedPerMonth: 30m, observed: observed, observation: TimeSpan.FromDays(30),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>Il verdetto porta con sé i numeri: un ritiro senza il suo perché è un ordine, non una decisione.</summary>
    [Fact]
    public void LaSpiegazioneRiportaOsservatiEAttesi()
    {
        var testo = TradeFrequency.DescribeStarvation(30m, observed: 1, observation: TimeSpan.FromDays(14), minFraction: 0.2m);

        Assert.Contains("1 trade in 14 giorni", testo, StringComparison.Ordinal);

        // [CI 2026-08-19] Il numero si formatta con la CULTURA DELL'HOST, e l'app non ne fissa
        // nessuna: sulla macchina di sviluppo (it-IT) esce «13,8», sui runner e nei pod Linux
        // «13.8». Inchiodare la forma italiana faceva passare il test qui e fallire in CI —
        // e il test non stava provando la lingua, stava provando il NUMERO.
        // Si costruisce quindi l'atteso con lo STESSO formato del codice sotto test.
        // Gli attesi NEL PERIODO, non al mese: 30 x (14 / 30,4375) = 13,8 con la costante condivisa.
        var attesiNelPeriodo = Math.Round(30m * (14m / TradeFrequency.DaysPerMonth), 1);
        Assert.Contains($"contro ~{attesiNelPeriodo:0.#} attesi", testo, StringComparison.Ordinal);
    }

    // --- La finestra di holdout: UNA aritmetica, tre chiamanti ---------------------------------

    private static PipelineDateRanges Ranges(string holdoutFrom, string holdoutTo) => new()
    {
        SelectionFrom = new DateTime(2025, 1, 1), SelectionTo = new DateTime(2025, 6, 1),
        HoldoutFrom = DateTime.Parse(holdoutFrom, System.Globalization.CultureInfo.InvariantCulture),
        HoldoutTo = DateTime.Parse(holdoutTo, System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// <b>L1 — il trade/mese ricostruito contro un riferimento indipendente.</b> Holdout
    /// 2026-06-01 → 2026-08-01 (61 giorni) con 30 trade: il riferimento, calcolato qui a mano e non
    /// dal codice sotto test, è <c>30 ÷ (61 ÷ 30,44) = 14,97</c>. Se qualcuno cambiasse il divisore
    /// del mese (30 invece di 30,44, o 4 settimane) questo test se ne accorgerebbe.
    /// </summary>
    [Fact]
    public void L1_IlTradeAlMese_CoincideColRiferimentoCalcolatoAMano()
    {
        var ranges = Ranges("2026-06-01", "2026-08-01");

        var atteso = 30m / (61m / 30.44m);
        var ottenuto = TradeFrequency.PerMonth(30, ranges.HoldoutMonths()!.Value);

        Assert.NotNull(ottenuto);
        Assert.True(Math.Abs(ottenuto!.Value - atteso) < 0.01m,
            $"atteso ~{atteso:0.###}/mese dal riferimento indipendente, ottenuto {ottenuto}");
    }

    /// <summary>
    /// Holdout sotto la settimana ⇒ nessuna finestra, quindi nessuna frequenza. Una finestra più
    /// corta non dà una frequenza, dà un aneddoto: meglio nessun numero che un numero costruito su
    /// sei giorni, per la stessa ragione per cui l'ignoranza non condanna.
    /// </summary>
    [Theory]
    [InlineData("2026-08-01", "2026-08-06")]   // 5 giorni
    [InlineData("2026-08-01", "2026-08-01")]   // finestra nulla
    public void FinestraTroppoCorta_NessunaFrequenza(string da, string a)
        => Assert.Null(Ranges(da, a).HoldoutMonths());

    /// <summary>
    /// <b>Il test che impedisce le due aritmetiche.</b> La stessa finestra letta dalle DUE strade —
    /// l'oggetto (applicatore della pipeline) e il JSON della configurazione (lettore della flotta,
    /// schieramento grigio) — deve dare lo stesso numero, alla cifra. Sono due porte, non due
    /// regole: se qualcuno ne cambiasse una sola, questo test diventa rosso.
    /// </summary>
    [Fact]
    public void LeDueStradeVersoLaFinestra_DannoLoStessoNumero()
    {
        var ranges = Ranges("2026-05-15", "2026-08-01");

        var daOggetto = ranges.HoldoutMonths();
        var daJson = FleetStateReader.HoldoutMonths(JsonSerializer.Serialize(ranges));

        Assert.NotNull(daOggetto);
        Assert.Equal(daOggetto, daJson);
    }

    /// <summary>JSON assente o illeggibile ⇒ null, mai un'eccezione: chi la chiama sta schierando.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{non json")]
    public void JsonIllegibile_NessunaFinestraENessunaEccezione(string? json)
        => Assert.Null(FleetStateReader.HoldoutMonths(json));

    // ------------------------------------------------------------------ [K16, 2026-09-05]

    /// <summary>
    /// <b>[K16] Il silenzio si condanna solo se e' improbabile sotto il proprio nullo.</b> Con la sola
    /// frazione, zero trade era inedia per QUALUNQUE ritmo atteso: a 1,65 trade/mese la corsia 4
    /// (Composite XLM/USDT 4h) sarebbe stata fermata al decimo giorno con il 58% di probabilita' di
    /// essere perfettamente nella norma. La coda di Poisson P(X ≤ osservati | attesi) sotto il 5% e'
    /// il criterio: a 10 giorni non si giudica, a 60 (λ = 3,25, P = 3,9%) si'; a 30/mese bastano i 10.
    /// </summary>
    [Theory]
    [InlineData(1.65, 10, false)]
    [InlineData(1.65, 60, true)]
    [InlineData(30, 10, true)]
    public void ZeroTrade_SiCondannaSoloQuandoIlSilenzioEImprobabile(double perMese, int giorni, bool inedia)
        => Assert.Equal(inedia, TradeFrequency.IsStarving(
            expectedPerMonth: (decimal)perMese, observed: 0, observation: TimeSpan.FromDays(giorni),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(10)));

    /// <summary>
    /// [K16] Con un trade la stessa regola: MacdTrend AAVE/USDT 4h promette 11,11 trade/mese; un solo
    /// trade in 20 giorni (λ = 7,3, P(X ≤ 1) = 0,56%) e' inedia, in 8 giorni (λ = 2,9, P = 22%) no —
    /// anche se in entrambi i casi e' sotto il 20% dell'atteso.
    /// </summary>
    [Theory]
    [InlineData(8, false)]
    [InlineData(20, true)]
    public void UnTrade_LaFrazioneNonBastaSenzaLaProbabilita(int giorni, bool inedia)
        => Assert.Equal(inedia, TradeFrequency.IsStarving(
            expectedPerMonth: 11.11m, observed: 1, observation: TimeSpan.FromDays(giorni),
            minFraction: 0.2m, minObservation: TimeSpan.FromDays(7)));

    /// <summary>Livello 1: la coda di Poisson contro valori calcolati a mano (e^-3 = 0,0498; e^-2·3 = 0,406; e^-0,5·1,625 = 0,9856).</summary>
    [Theory]
    [InlineData(0, 3.0, 0.0498)]
    [InlineData(1, 2.0, 0.4060)]
    [InlineData(2, 0.5, 0.9856)]
    [InlineData(0, 0.0, 1.0)]
    public void LaCodaDiPoisson_ControUnRiferimentoIndipendente(int osservati, double media, double atteso)
        => Assert.Equal(atteso, (double)TradeFrequency.PoissonLowerTail(osservati, (decimal)media), 3);

    /// <summary>La coda non trabocca con medie assurde (il fuzz della flotta ne passa): e' 0 quando la media e' enorme, 1 quando e' nulla.</summary>
    [Fact]
    public void LaCodaDiPoisson_NonTraboccaConMedieAssurde()
    {
        Assert.Equal(0m, TradeFrequency.PoissonLowerTail(3, 5_000m));
        Assert.Equal(1m, TradeFrequency.PoissonLowerTail(0, 0m));
        Assert.Equal(1m, TradeFrequency.PoissonLowerTail(500, 0.001m));
    }

    /// <summary>[K16] La spiegazione porta la probabilita' accanto al conteggio: «0 contro 0,5 attesi» e «0 contro 9,9» sono la stessa frazione e due verdetti opposti.</summary>
    [Fact]
    public void LaSpiegazioneRiportaLaProbabilitaDelSilenzio()
    {
        var testo = TradeFrequency.DescribeStarvation(30m, observed: 0, observation: TimeSpan.FromDays(10), minFraction: 0.2m);
        Assert.Contains("probabilita'", testo, StringComparison.Ordinal);
        Assert.Contains("0 trade in 10 giorni", testo, StringComparison.Ordinal);
    }
}
