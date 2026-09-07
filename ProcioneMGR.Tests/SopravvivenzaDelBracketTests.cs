using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-07] <b>Perché le operazioni si chiudono in stop e non in take: la prova, eseguibile.</b>
///
/// <para>Il proprietario: «molte operazioni si chiudono con uno stop loss invece che con un take
/// profit, dovrei capire perché». In esercizio il rapporto è 119 stop contro 21 target. La risposta
/// non è la sfortuna, non è il segnale e non è l'infrastruttura (misurato: 0 stop su 82 non-replay
/// entro un'ora da un crash o da un rilascio, 0 su 119 con un buco di candele nelle tre barre
/// precedenti, il prezzo d'uscita dentro la barra 119 volte su 119). È <b>aritmetica</b>, e nasce da
/// una riga di <see cref="ExcursionAnalyzer"/>.</para>
///
/// <para><b>L'identità che lo dimostra.</b> Per un long l'escursione avversa è il drawdown e la
/// favorevole è il runup; per uno short i due ruoli si scambiano. Quindi, sulle stesse candele, la
/// distribuzione della MAE dei long <b>è</b> quella della MFE degli short e viceversa. Siccome
/// <c>AutoBracket.ComputeAsync</c> media i due lati, senza il filtro sui vincenti stop e take
/// pescano dalla stessa coppia di code e devono venire <b>uguali</b>. Col filtro le due popolazioni
/// divergono — fra i vincitori il runup è grande per costruzione e il drawdown è piccolo — e allo
/// stesso percentile il take esce sistematicamente più lontano.</para>
///
/// <para><b>Perché un test e non una nota in un documento.</b> Questa piattaforma ha cinque
/// meccanismi di auto-correzione che misurano e tacciono: una diagnosi scritta in un rapporto marcisce
/// in silenzio. Se qualcuno un giorno toccasse <c>Aggregate</c>, questi test cadrebbero e direbbero
/// perché. La causa smette di essere un'opinione e diventa un invariante.</para>
///
/// <para><b>Quello che questi test NON dicono.</b> Che togliere il filtro farebbe guadagnare la
/// piattaforma. Calcolato onestamente — contando anche il 51-90% di operazioni che non toccano
/// nessuna barriera — il valore atteso per operazione su entrate qualunque vale meno la commissione e
/// nient'altro, con o senza filtro. La geometria del bracket cambia la FORMA dell'esito (quante volte
/// si vince contro quanto si vince), non la media. Per questo il default resta invariato.</para>
/// </summary>
public sealed class SopravvivenzaDelBracketTests
{
    /// <summary>
    /// Una serie deterministica e senza deriva: passeggiata a zig-zag con ampiezza variabile, così
    /// che le escursioni esistano ma nessun lato sia favorito. Nessun <c>Random</c> (i test devono
    /// dare lo stesso numero a ogni corsa, ed è anche la regola dei workflow di questo repo).
    /// </summary>
    private static List<OhlcvData> SerieSenzaDeriva(int n = 800)
    {
        var candele = new List<OhlcvData>(n);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var prezzo = 100m;
        for (var i = 0; i < n; i++)
        {
            // Passo alternato con ampiezza che varia in modo periodico ma non banale: la somma su un
            // ciclo completo è nulla, quindi la serie non ha deriva e nessun lato è avvantaggiato.
            var ampiezza = 0.4m + (i % 7) * 0.15m + (i % 13) * 0.05m;
            var passo = i % 2 == 0 ? ampiezza : -ampiezza;
            var open = prezzo;
            var close = prezzo + passo;
            var alto = Math.Max(open, close) + ampiezza * 0.6m;
            var basso = Math.Min(open, close) - ampiezza * 0.6m;
            candele.Add(new OhlcvData
            {
                Symbol = "TEST/USDT",
                Timeframe = "1h",
                TimestampUtc = t0.AddHours(i),
                Open = open,
                High = alto,
                Low = basso,
                Close = close,
                Volume = 1_000m,
            });
            prezzo = close;
        }
        return candele;
    }

    /// <summary>Come <c>AutoBracket.ComputeAsync</c>: la media fra i due lati, sui valori positivi.</summary>
    private static decimal Media(decimal a, decimal b)
    {
        var v = new[] { a, b }.Where(x => x > 0m).ToList();
        return v.Count > 0 ? Math.Round(v.Average(), 2) : 0m;
    }

    /// <summary>
    /// <b>L'IDENTITÀ.</b> Senza il filtro sui vincenti, e mediando i due lati come fa
    /// <c>AutoBracket</c>, stop e take escono uguali. Non «simili»: uguali, perché sono percentili
    /// della stessa coppia di distribuzioni scambiate di ruolo.
    /// </summary>
    [Fact]
    public void SenzaIlFiltroSuiVincenti_StopETakeSonoLoSTESSO_NUMERO()
    {
        var a = new ExcursionAnalyzer();
        var candele = SerieSenzaDeriva();

        var lungo = a.SuggestAdaptiveBracket(candele, OrderSide.Buy, winnersOnly: false);
        var corto = a.SuggestAdaptiveBracket(candele, OrderSide.Sell, winnersOnly: false);

        var stop = Media(lungo.StopLossPercent, corto.StopLossPercent);
        var take = Media(lungo.TakeProfitPercent, corto.TakeProfitPercent);

        Assert.True(stop > 0m, "la serie di prova deve produrre escursioni misurabili");
        Assert.Equal(stop, take);   // al centesimo: è la stessa distribuzione, non un'approssimazione
    }

    /// <summary>
    /// <b>LA CAUSA.</b> Con il filtro (il comportamento schierato) il take si allontana dallo stop di
    /// un fattore ben oltre 1, sulla STESSA serie senza deriva — cioè senza che il mercato abbia
    /// fatto nulla per meritarlo. È qui che nasce il rapporto 1:2,5-1:4,2 dei bracket in esercizio.
    /// </summary>
    [Fact]
    public void ConIlFiltroSuiVincenti_IlTakeSiALLONTANA_SenzaCheIlMercatoLoGiustifichi()
    {
        var a = new ExcursionAnalyzer();
        var candele = SerieSenzaDeriva();

        var lungo = a.SuggestAdaptiveBracket(candele, OrderSide.Buy);    // default: winnersOnly = true
        var corto = a.SuggestAdaptiveBracket(candele, OrderSide.Sell);

        var stop = Media(lungo.StopLossPercent, corto.StopLossPercent);
        var take = Media(lungo.TakeProfitPercent, corto.TakeProfitPercent);

        Assert.True(stop > 0m && take > 0m, "la serie di prova deve produrre un bracket");
        Assert.True(take > stop,
            $"col filtro sui vincenti il take deve risultare più lontano dello stop: stop {stop}, take {take}");
    }

    /// <summary>
    /// <b>LA CONSEGUENZA, in probabilità.</b> Un take k volte più lontano dello stop viene toccato
    /// molto meno spesso nello stesso orizzonte. Qui la si misura sul percorso vero delle candele con
    /// la semantica del motore (lo stop ha la precedenza sul pareggio di barra, come in
    /// <c>ProtectiveExitEvaluator</c>): il conteggio degli stop-primo deve dominare quello dei
    /// take-primo. È la lettura NOMINALE del 119:21 — il numero atteso, non un allarme.
    /// </summary>
    [Fact]
    public void UnTakePiuLontano_VieneToccatoMoltoMenoSpesso_SuEntrateQualunque()
    {
        var candele = SerieSenzaDeriva();
        const decimal stop = 1.0m;
        const decimal take = 3.0m;   // il rapporto 1:3 dei bracket schierati
        const int orizzonte = 10;

        var stopPrimo = 0;
        var takePrimo = 0;
        for (var i = 0; i + orizzonte < candele.Count; i++)
        {
            var ingresso = candele[i].Close;
            var livelloStop = ingresso * (1m - stop / 100m);
            var livelloTake = ingresso * (1m + take / 100m);
            for (var j = i + 1; j <= i + orizzonte; j++)
            {
                var c = candele[j];
                // Precedenza allo stop quando la stessa barra tocca entrambi: è la regola del motore.
                if (c.Low <= livelloStop) { stopPrimo++; break; }
                if (c.High >= livelloTake) { takePrimo++; break; }
            }
        }

        Assert.True(stopPrimo > 0 && takePrimo >= 0);
        Assert.True(stopPrimo > takePrimo * 2,
            $"con un take tre volte più lontano lo stop deve arrivare primo molto più spesso: stop {stopPrimo}, take {takePrimo}");
    }

    /// <summary>
    /// Il default non è cambiato: <c>winnersOnly</c> nasce <c>true</c>, quindi il bracket che la
    /// piattaforma schiera oggi è identico a quello di prima di questa diagnosi. Il parametro esiste
    /// per misurare, non per correggere di nascosto i parametri di rischio delle corsie.
    /// </summary>
    [Fact]
    public void IlDefaultNonECambiato_IlBracketSchieratoEQuelloDiPrima()
    {
        var a = new ExcursionAnalyzer();
        var candele = SerieSenzaDeriva();

        var conDefault = a.SuggestAdaptiveBracket(candele, OrderSide.Buy);
        var esplicito = a.SuggestAdaptiveBracket(candele, OrderSide.Buy, winnersOnly: true);

        Assert.Equal(esplicito.StopLossPercent, conDefault.StopLossPercent);
        Assert.Equal(esplicito.TakeProfitPercent, conDefault.TakeProfitPercent);
    }
}
