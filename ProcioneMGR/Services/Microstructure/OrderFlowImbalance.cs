namespace ProcioneMGR.Services.Microstructure;

// =============================================================================================
//  [D3] L'OFI VERO, e la sua versione misurabile sui dati storici.
//
//  DUE FORMULE, DUE STATURE DIVERSE — e la differenza va detta prima di guardare qualunque numero.
//
//  1. TopOfBookOfi: l'OFI di **Cont-Kukanov-Stoikov (2014)**, esatto, sul miglior bid/ask. È quello
//     che D3 nomina. Richiede prezzi E quantità al top-of-book, che i dump storici NON contengono
//     (i file bookTicker non esistono su data.binance.vision: verificato, 404 su tutte le date
//     provate). È implementato e verificato qui perché è la formula che consumerebbe il collettore
//     dal vivo: `{sym}@bookTicker` porta già bid/ask CON le size, e BinanceStreamMapper oggi le
//     scarta. Se il pilota si accenderà, non c'è nulla da inventare.
//
//  2. DepthBandOfi: la stessa idea applicata alla PROFONDITÀ A BANDE, che è la forma in cui il book
//     esiste storicamente (snapshot ogni 30 secondi, bande ±0,20% … ±5%). Variazione netta del
//     notional disponibile sul lato bid meno quella sul lato ask.
//
//  DOVE LA SECONDA È PIÙ DEBOLE DELLA PRIMA, detto senza giri:
//  - le bande sono relative al MID, quindi se il mid si muove la banda si sposta con lui: parte
//    della variazione di notional è movimento di prezzo, non aggiunta o ritiro di liquidità;
//  - a 30 secondi si vede il NETTO, non la sequenza: un book riempito e svuotato dieci volte
//    dentro l'intervallo appare identico a uno immobile;
//  - la convenzione di segno di CKS sul cambio di prezzo del miglior livello non ha equivalente:
//    qui non esiste "il miglior bid è salito", esiste solo "dentro lo 0,20% c'è più roba".
//
//  Quindi un esito negativo su (2) NON chiude in eterno la domanda su (1): dice che il book
//  osservabile a 30 secondi, alla granularità storicamente disponibile, non aggiunge nulla al proxy
//  trade-flow. È un'informazione che costa zero raccolta e che riordina le priorità — non una
//  dimostrazione di impossibilità.
// =============================================================================================

/// <summary>Order flow imbalance: la formula di Cont-Kukanov-Stoikov e la sua variante su bande di profondità.</summary>
public static class OrderFlowImbalance
{
    /// <summary>
    /// OFI di UN evento di book (Cont-Kukanov-Stoikov 2014, eq. 2):
    /// <code>
    /// e = 1{Pᵇₙ ≥ Pᵇₙ₋₁}·qᵇₙ − 1{Pᵇₙ ≤ Pᵇₙ₋₁}·qᵇₙ₋₁ − 1{Pᵃₙ ≤ Pᵃₙ₋₁}·qᵃₙ + 1{Pᵃₙ ≥ Pᵃₙ₋₁}·qᵃₙ₋₁
    /// </code>
    /// Letta per casi, è esattamente la pressione netta al top-of-book:
    /// <list type="bullet">
    /// <item>bid fermo → +Δ size al bid (liquidità aggiunta in acquisto = positiva);</item>
    /// <item>bid migliorato → +qᵇ nuovo intero (qualcuno si è messo davanti in acquisto);</item>
    /// <item>bid ritirato → −qᵇ vecchio intero (quella domanda è sparita);</item>
    /// <item>e simmetricamente sull'ask, col segno opposto.</item>
    /// </list>
    /// </summary>
    public static decimal TopOfBookOfi(BestQuote previous, BestQuote current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var bidTerm =
            (current.BidPrice >= previous.BidPrice ? current.BidSize : 0m)
            - (current.BidPrice <= previous.BidPrice ? previous.BidSize : 0m);

        var askTerm =
            -(current.AskPrice <= previous.AskPrice ? current.AskSize : 0m)
            + (current.AskPrice >= previous.AskPrice ? previous.AskSize : 0m);

        return bidTerm + askTerm;
    }

    /// <summary>
    /// OFI accumulato su una sequenza di quote: la somma degli eventi, che è la definizione della
    /// variabile con cui CKS regredisce le variazioni di prezzo. Con meno di due quote non esiste
    /// alcun evento, quindi il risultato è zero e non "nessun dato": una somma vuota vale zero.
    /// </summary>
    public static decimal TopOfBookOfi(IReadOnlyList<BestQuote> quotes)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        var sum = 0m;
        for (var i = 1; i < quotes.Count; i++) sum += TopOfBookOfi(quotes[i - 1], quotes[i]);
        return sum;
    }

    /// <summary>
    /// OFI sulle bande di profondità fra due snapshot: <c>(Δnotional bid − Δnotional ask)</c>,
    /// normalizzato sul notional medio totale della banda perché il numero sia confrontabile fra
    /// simboli e nel tempo (senza normalizzare, BTC dominerebbe qualunque confronto per pura scala).
    /// Null se una delle due bande manca in uno dei due snapshot.
    /// </summary>
    public static decimal? DepthBandOfi(BookDepthSnapshot previous, BookDepthSnapshot current, decimal band)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (previous.BidNotional(band) is not decimal prevBid || previous.AskNotional(band) is not decimal prevAsk
            || current.BidNotional(band) is not decimal curBid || current.AskNotional(band) is not decimal curAsk)
        {
            return null;
        }

        var scale = (prevBid + prevAsk + curBid + curAsk) / 2m;
        if (scale <= 0m) return null;

        return ((curBid - prevBid) - (curAsk - prevAsk)) / scale;
    }
}
