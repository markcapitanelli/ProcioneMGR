using ProcioneMGR.Services.Microstructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D3] L'OFI vero (Cont-Kukanov-Stoikov) verificato caso per caso contro il valore calcolato A MANO
/// dalla formula pubblicata, che è l'unico riferimento indipendente possibile per una definizione:
/// non esiste una seconda implementazione da confrontare, esistono i sei casi elementari (bid fermo,
/// migliorato, ritirato; e i tre simmetrici sull'ask) e il loro segno.
///
/// Perché tanta cura su quattro righe di aritmetica: un OFI col segno sbagliato produce un IC
/// perfettamente plausibile — semplicemente col segno opposto a quello vero — e nessun controllo
/// statistico a valle lo smaschererebbe.
/// </summary>
public class OrderFlowImbalanceTests
{
    private static readonly DateTime T0 = new(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);

    private static BestQuote Quote(decimal bid, decimal bidSize, decimal ask, decimal askSize, int seconds = 0) =>
        new(T0.AddSeconds(seconds), bid, bidSize, ask, askSize);

    [Fact]
    public void PricesUnchanged_TheOfiIsTheNetChangeInSizes()
    {
        // bid 100 (10 → 14) = +4 ; ask 101 (8 → 5) = −(5−8) = +3 ; totale +7.
        var previous = Quote(100m, 10m, 101m, 8m);
        var current = Quote(100m, 14m, 101m, 5m, 1);

        Assert.Equal(7m, OrderFlowImbalance.TopOfBookOfi(previous, current));
    }

    [Fact]
    public void BidImproves_TheWholeNewBidSizeCountsAsBuyingPressure()
    {
        // Bid da 100 a 100,5: qualcuno si è messo davanti in acquisto. Il termine bid vale +qᵇ nuovo
        // (3), non la differenza con la coda precedente — quella coda sta ora un livello sotto.
        // Ask fermo: −(4 − 4) = 0.
        var previous = Quote(100m, 10m, 101m, 4m);
        var current = Quote(100.5m, 3m, 101m, 4m, 1);

        Assert.Equal(3m, OrderFlowImbalance.TopOfBookOfi(previous, current));
    }

    [Fact]
    public void BidIsPulled_TheOldSizeCountsNegative()
    {
        // Bid da 100 a 99,5: la domanda a 100 è sparita → −qᵇ vecchio (−10). Ask fermo → 0.
        var previous = Quote(100m, 10m, 101m, 4m);
        var current = Quote(99.5m, 6m, 101m, 4m, 1);

        Assert.Equal(-10m, OrderFlowImbalance.TopOfBookOfi(previous, current));
    }

    [Fact]
    public void AskImproves_ItIsSellingPressureWithTheOppositeSign()
    {
        // Ask da 101 a 100,5 (venditore aggressivo): −qᵃ nuovo (−7). Bid fermo → 0.
        var previous = Quote(100m, 10m, 101m, 4m);
        var current = Quote(100m, 10m, 100.5m, 7m, 1);

        Assert.Equal(-7m, OrderFlowImbalance.TopOfBookOfi(previous, current));
    }

    [Fact]
    public void AskIsPulled_TheOldAskSizeCountsPositive()
    {
        // Ask da 101 a 101,5: l'offerta a 101 è sparita → +qᵃ vecchio (+4).
        var previous = Quote(100m, 10m, 101m, 4m);
        var current = Quote(100m, 10m, 101.5m, 9m, 1);

        Assert.Equal(4m, OrderFlowImbalance.TopOfBookOfi(previous, current));
    }

    [Fact]
    public void TheFormulaIsAntisymmetric_MirroringBidAndAskFlipsTheSign()
    {
        // Controllo strutturale, non aritmetico: se si scambiano i due lati del book (specchiando i
        // prezzi attorno al mid) l'OFI deve cambiare solo segno. Una asimmetria qui vorrebbe dire
        // che la formula tratta compratori e venditori con due pesi diversi.
        var previous = Quote(100m, 10m, 102m, 8m);
        var current = Quote(101m, 3m, 102m, 5m, 1);
        var direct = OrderFlowImbalance.TopOfBookOfi(previous, current);

        // Specchio: bid↔ask con i prezzi riflessi attorno a 101 (il mid iniziale).
        var mPrevious = Quote(100m, 8m, 102m, 10m);
        var mCurrent = Quote(100m, 5m, 101m, 3m, 1);
        var mirrored = OrderFlowImbalance.TopOfBookOfi(mPrevious, mCurrent);

        Assert.Equal(direct, -mirrored);
    }

    [Fact]
    public void OverASequence_TheOfiIsTheSumOfTheEvents()
    {
        var quotes = new List<BestQuote>
        {
            Quote(100m, 10m, 101m, 10m, 0),
            Quote(100m, 12m, 101m, 10m, 1),   // +2
            Quote(100m, 12m, 101m, 7m, 2),    // +3
            Quote(99.5m, 5m, 101m, 7m, 3),    // −12
        };

        var total = OrderFlowImbalance.TopOfBookOfi(quotes);

        Assert.Equal(-7m, total);
        // La somma per pezzi deve coincidere con quella complessiva: se non lo facesse, l'accumulo
        // starebbe perdendo o contando due volte un evento.
        var byHand = OrderFlowImbalance.TopOfBookOfi(quotes[0], quotes[1])
                     + OrderFlowImbalance.TopOfBookOfi(quotes[1], quotes[2])
                     + OrderFlowImbalance.TopOfBookOfi(quotes[2], quotes[3]);
        Assert.Equal(byHand, total);
    }

    [Fact]
    public void ASingleQuote_HasNoEventsSoTheOfiIsZero()
    {
        Assert.Equal(0m, OrderFlowImbalance.TopOfBookOfi([Quote(100m, 1m, 101m, 1m)]));
        Assert.Equal(0m, OrderFlowImbalance.TopOfBookOfi([]));
    }

    // --- La variante su bande di profondità (quella misurabile storicamente) ---------------------

    private static BookDepthSnapshot Depth(decimal bid, decimal ask, int seconds) =>
        new(T0.AddSeconds(seconds), new Dictionary<decimal, decimal> { [-0.2m] = bid, [0.2m] = ask });

    [Fact]
    public void DepthBandOfi_IsSignedTowardsTheSideThatGrew_AndNormalized()
    {
        // Bid 1000 → 1200 (+200), ask 1000 → 900 (−100): pressione netta in acquisto +300.
        // Scala = (1000+1000+1200+900)/2 = 2050 → 300/2050 = 0,14634…
        var previous = Depth(1000m, 1000m, 0);
        var current = Depth(1200m, 900m, 30);

        var ofi = OrderFlowImbalance.DepthBandOfi(previous, current, 0.2m);

        Assert.NotNull(ofi);
        Assert.Equal(300m / 2050m, ofi!.Value, 10);
        // Scambiando i due snapshot il segno si capovolge: il tempo ha una direzione.
        Assert.Equal(-ofi.Value, OrderFlowImbalance.DepthBandOfi(current, previous, 0.2m)!.Value, 10);
    }

    [Fact]
    public void DepthBandOfi_WithoutTheBand_IsNullInsteadOfZero()
    {
        var previous = new BookDepthSnapshot(T0, new Dictionary<decimal, decimal> { [-0.2m] = 100m });
        var current = Depth(120m, 90m, 30);

        Assert.Null(OrderFlowImbalance.DepthBandOfi(previous, current, 0.2m));
        // Una banda diversa da quelle presenti non è "zero flusso": è assenza di dato.
        Assert.Null(OrderFlowImbalance.DepthBandOfi(current, current, 5m));
    }

    [Fact]
    public void DepthBandOfi_OnAnEmptyBook_IsNull()
    {
        var flat = Depth(0m, 0m, 0);

        Assert.Null(OrderFlowImbalance.DepthBandOfi(flat, Depth(0m, 0m, 30), 0.2m));
    }
}
