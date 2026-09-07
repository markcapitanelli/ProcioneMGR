using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Trading.Internal;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// [2026-09-07] <b>La corsa a primo tocco: quanto spesso un bracket schierato DEVE chiudersi in stop,
/// su entrate qualunque.</b>
///
/// <para><b>Da dove nasce.</b> Il proprietario: «molte operazioni si chiudono con uno stop loss invece
/// che con un take profit, dovrei capire perché». In esercizio il rapporto è 119 stop contro 21
/// target — l'85%. La domanda sembra riguardare le strategie, e invece ha una risposta che non ha
/// bisogno di conoscerle: con un take profit 2,7-4,2 volte più lontano dello stop, su entrate prese a
/// ogni chiusura di barra e su entrambi i lati, le otto corsie schierate producono fra l'85% e il 94%
/// di uscite in stop. L'osservato è dentro quella banda. <b>Non c'è niente da spiegare: è il numero
/// nominale del bracket.</b></para>
///
/// <para><b>Perché serve un misuratore e non un rapporto.</b> Questa piattaforma ha già cinque
/// meccanismi che misurano e tacciono, e una diagnosi scritta in un documento invecchia in silenzio.
/// Qui il numero si ricalcola dalle candele in secondi, ogni volta che qualcuno guarda, e vale come
/// DENOMINATORE: senza, «troppi stop» è una lamentela infalsificabile; con, è uno scarto dal previsto.
/// Ed è l'unico segnale disponibile su scala settimanale — il forward test vero chiede da 7 a 18 mesi
/// per corsia, al ritmo dichiarato dalle gambe.</para>
///
/// <para><b>Riusa la regola del motore, non una sua copia.</b> La decisione di uscita è quella vera,
/// <see cref="ProtectiveExitEvaluator.EvaluateStopAndTarget"/>: stop valutato prima del target sulla
/// stessa barra, riempimento al livello oppure all'apertura se la barra ha aperto oltre. Una seconda
/// regola scritta per l'occasione misurerebbe un sosia del motore, ed è esattamente il modo in cui
/// nascono i numeri che sembrano misure.</para>
///
/// <para><b>Misura il bracket FISSO, non il trailing.</b> La posizione fittizia non porta
/// <c>TrailingStopPercent</c>, quindi <see cref="ProtectiveExitEvaluator.EffectiveStop"/> ricade
/// sullo stop dichiarato. È fedele a ciò che è schierato: verificato sul database il 2026-09-07,
/// <b>nessuna</b> delle nove gambe attive sulle otto corsie ha un trailing impostato. Se un giorno
/// ne comparisse uno, questa corsa lo ignorerebbe e il numero andrebbe letto come un limite
/// superiore alla quota di stop — un trailing può solo avvicinare lo stop, mai allontanarlo.</para>
///
/// <para><b>Che cosa NON dice.</b> Non dice se la strategia è buona: le entrate qui sono prese a ogni
/// barra, cioè deliberatamente prive di segnale. Serve a separare ciò che il bracket produce da solo
/// da ciò che il segnale aggiunge — e finché l'osservato coincide col nominale, il segnale non sta
/// aggiungendo niente di misurabile su questa metrica. E non dice quanto si guadagna: il valore atteso
/// per operazione (<see cref="BracketRace.ExpectedValuePercent"/>) va letto sapendo che comprende le
/// operazioni che non toccano nessuna barriera — che sono il 51-90% del totale, e scartarle è la
/// trappola da sopravvivenza che trasforma un valore atteso di -0,2% in uno di -1,4%.</para>
/// </summary>
public static class BracketRaceAnalyzer
{
    /// <summary>Orizzonte in barre entro cui si considera la corsa: oltre, l'esito è «nessuna barriera».</summary>
    public const int DefaultHorizonBars = 10;

    /// <summary>
    /// Corre il bracket su tutta la serie, entrando a ogni chiusura di barra e su ENTRAMBI i lati —
    /// perché il bracket schierato è simmetrico (<c>AutoBracket</c> media long e short) e misurarlo su
    /// un lato solo darebbe il numero di quel lato, non quello della gamba.
    /// </summary>
    /// <param name="candles">Serie in ordine cronologico crescente.</param>
    /// <param name="stopPercent">Distanza dello stop in % dal prezzo d'ingresso. Deve essere &gt; 0.</param>
    /// <param name="takePercent">Distanza del target in % dal prezzo d'ingresso. Deve essere &gt; 0.</param>
    /// <param name="horizonBars">Barre entro cui si osserva la corsa.</param>
    /// <param name="roundTripCostPercent">Costo di andata e ritorno in punti percentuali (fee + slippage).</param>
    public static BracketRace Run(
        IReadOnlyList<OhlcvData> candles,
        decimal stopPercent,
        decimal takePercent,
        int horizonBars = DefaultHorizonBars,
        decimal roundTripCostPercent = 0.20m)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (horizonBars < 1) throw new ArgumentOutOfRangeException(nameof(horizonBars));
        if (stopPercent <= 0m || takePercent <= 0m)
        {
            // Una gamba senza protezioni non ha una corsa da correre: non si inventa un numero.
            return BracketRace.NonMisurabile("stop o target non impostati sulla gamba");
        }
        if (candles.Count < horizonBars + 2)
        {
            return BracketRace.NonMisurabile($"servono almeno {horizonBars + 2} candele, presenti {candles.Count}");
        }

        var stopFirst = 0;
        var takeFirst = 0;
        var neither = 0;
        decimal sum = 0m;
        var barreAlloStop = new List<int>();
        var barreAlTake = new List<int>();
        var pareggi = 0;

        for (var i = 0; i + horizonBars < candles.Count; i++)
        {
            var entry = candles[i].Close;
            if (entry <= 0m) continue;

            foreach (var side in (ReadOnlySpan<OrderSide>)[OrderSide.Buy, OrderSide.Sell])
            {
                var esito = Corri(candles, i, horizonBars, entry, side, stopPercent, takePercent);
                switch (esito.Kind)
                {
                    case ProtectiveExitKind.StopLoss:
                        stopFirst++;
                        barreAlloStop.Add(esito.Bars);
                        if (esito.Ambigua) pareggi++;
                        break;
                    case ProtectiveExitKind.TakeProfit: takeFirst++; barreAlTake.Add(esito.Bars); break;
                    default: neither++; break;
                }
                sum += esito.ReturnPercent - roundTripCostPercent;
            }
        }

        var total = stopFirst + takeFirst + neither;
        if (total == 0) return BracketRace.NonMisurabile("nessuna finestra utilizzabile nella serie");

        return new BracketRace(
            Misurabile: true,
            Motivo: null,
            StopFirst: stopFirst,
            TakeFirst: takeFirst,
            Neither: neither,
            HorizonBars: horizonBars,
            StopPercent: stopPercent,
            TakePercent: takePercent,
            RoundTripCostPercent: roundTripCostPercent,
            ExpectedValuePercent: sum / total,
            MedianBarsToStop: Mediana(barreAlloStop),
            MedianBarsToTake: Mediana(barreAlTake),
            AmbiguousBars: pareggi);
    }

    /// <summary>Mediana intera, o null se la barriera non è mai stata toccata: uno zero direbbe «subito».</summary>
    private static int? Mediana(List<int> valori)
    {
        if (valori.Count == 0) return null;
        valori.Sort();
        return valori[valori.Count / 2];
    }

    /// <summary>
    /// <b>La scansione delle geometrie: la parte «come si migliora».</b> Tiene fermo lo stop della
    /// gamba e fa variare SOLO la distanza del target, perché è l'unico dei due estremi che la
    /// calibrazione decide con un percentile e non con un vincolo di rischio.
    ///
    /// <para>Il risultato atteso, e che va letto come un risultato e non come un fallimento: su
    /// entrate senza segnale il valore atteso è praticamente lo stesso per ogni geometria, e vale
    /// meno la commissione. Cambia la FORMA (quante volte si vince contro quanto si vince), non la
    /// media. Serve a togliere dal tavolo l'ipotesi «i bracket sono messi male»: se la banda dei
    /// valori attesi è stretta quanto il costo, il bracket <b>non è la leva</b>, e continuare a
    /// spostarlo è lavoro che non produce soldi.</para>
    /// </summary>
    public static IReadOnlyList<BracketRace> Scan(
        IReadOnlyList<OhlcvData> candles,
        decimal stopPercent,
        IReadOnlyList<decimal> takeMultipliers,
        int horizonBars = DefaultHorizonBars,
        decimal roundTripCostPercent = 0.20m)
    {
        ArgumentNullException.ThrowIfNull(takeMultipliers);
        return takeMultipliers
            .Where(m => m > 0m)
            .Select(m => Run(candles, stopPercent, stopPercent * m, horizonBars, roundTripCostPercent))
            .ToList();
    }

    /// <summary>I rapporti target/stop provati di default: da simmetrico a molto asimmetrico.</summary>
    public static readonly IReadOnlyList<decimal> DefaultMultipliers = [0.5m, 1m, 1.5m, 2m, 2.5m, 3m, 4m, 5m];

    /// <summary>
    /// Una singola finestra. Costruisce una posizione fittizia e usa la decisione VERA del motore,
    /// barra per barra. Il ritorno di «nessuna barriera» è la variazione all'orizzonte: scartare quelle
    /// finestre gonfierebbe la perdita attesa di un ordine di grandezza.
    /// </summary>
    private static (ProtectiveExitKind Kind, decimal ReturnPercent, int Bars, bool Ambigua) Corri(
        IReadOnlyList<OhlcvData> candles, int i, int horizon, decimal entry, OrderSide side,
        decimal stopPercent, decimal takePercent)
    {
        var segno = side == OrderSide.Buy ? 1m : -1m;
        var pos = new OpenPosition
        {
            Symbol = candles[i].Symbol,
            Side = side,
            EntryPrice = entry,
            Quantity = 1m,
            StopLoss = entry * (1m - segno * stopPercent / 100m),
            TakeProfit = entry * (1m + segno * takePercent / 100m),
        };

        for (var j = i + 1; j <= i + horizon; j++)
        {
            var c = candles[j];
            var uscita = ProtectiveExitEvaluator.EvaluateStopAndTarget(pos, c.Open, c.High, c.Low);
            if (!uscita.ShouldClose) continue;
            var reso = segno * (uscita.FillPrice - entry) / entry * 100m;

            // Pareggio di barra: la barra che ha risolto ha toccato ANCHE l'altro livello. Il motore
            // assegna sempre lo stop, che è l'esito peggiore. La scelta è corretta - dentro la barra
            // l'ordine dei tocchi non è osservabile - ma va DICHIARATA: dove i pareggi sono tanti, il
            // valore atteso misurato qui è sistematicamente più pessimista del vero.
            var ambigua = uscita.Kind == ProtectiveExitKind.StopLoss
                && pos.TakeProfit is decimal tp
                && (segno > 0m ? c.High >= tp : c.Low <= tp);
            return (uscita.Kind, reso, j - i, ambigua);
        }

        var finale = candles[i + horizon].Close;
        return (ProtectiveExitKind.None, segno * (finale - entry) / entry * 100m, horizon, false);
    }
}

/// <summary>
/// L'esito della corsa. <see cref="Misurabile"/> falso = non si è potuto misurare, e
/// <see cref="Motivo"/> dice perché: chi legge deve poter distinguere «non misurato» da uno zero.
/// </summary>
public sealed record BracketRace(
    bool Misurabile,
    string? Motivo,
    int StopFirst,
    int TakeFirst,
    int Neither,
    int HorizonBars,
    decimal StopPercent,
    decimal TakePercent,
    decimal RoundTripCostPercent,
    decimal ExpectedValuePercent,
    int? MedianBarsToStop = null,
    int? MedianBarsToTake = null,
    int AmbiguousBars = 0)
{
    public static BracketRace NonMisurabile(string motivo) =>
        new(false, motivo, 0, 0, 0, 0, 0m, 0m, 0m, 0m);

    /// <summary>Rapporto fra il take e lo stop: è il numero che genera tutto il resto.</summary>
    public decimal RewardToRisk => StopPercent > 0m ? Math.Round(TakePercent / StopPercent, 2) : 0m;

    /// <summary>Quante uscite a barriera finiscono in stop, in percentuale. È il numero da confrontare con l'osservato.</summary>
    public decimal StopSharePercent =>
        StopFirst + TakeFirst > 0 ? Math.Round(100m * StopFirst / (StopFirst + TakeFirst), 1) : 0m;

    /// <summary>Quante volte lo stop arriva primo per ogni volta che arriva primo il target.</summary>
    public decimal StopToTakeRatio =>
        TakeFirst > 0 ? Math.Round((decimal)StopFirst / TakeFirst, 1) : 0m;

    /// <summary>
    /// Quota degli stop nati da un pareggio di barra, cioè da una barra che aveva toccato anche il
    /// target. Dove è alta il valore atteso misurato è più pessimista del vero, e va detto.
    /// </summary>
    public decimal AmbiguousSharePercent =>
        StopFirst > 0 ? Math.Round(100m * AmbiguousBars / StopFirst, 1) : 0m;

    /// <summary>Quota delle finestre in cui nessuna barriera viene toccata entro l'orizzonte.</summary>
    public decimal NeitherSharePercent
    {
        get
        {
            var total = StopFirst + TakeFirst + Neither;
            return total > 0 ? Math.Round(100m * Neither / total, 1) : 0m;
        }
    }

    /// <summary>
    /// La frase da mostrare accanto al conteggio vero delle uscite. Dichiara sempre l'orizzonte:
    /// un tasso di tocco senza l'orizzonte su cui è misurato non significa niente.
    /// </summary>
    public string Racconto => !Misurabile
        ? $"corsa non misurabile: {Motivo}"
        : $"con stop {StopPercent:0.##}% e target {TakePercent:0.##}% (rapporto {RewardToRisk:0.##}), su entrate prese a "
          + $"ogni barra e su entrambi i lati, entro {HorizonBars} barre lo stop arriva primo {StopToTakeRatio:0.#} volte "
          + $"per ogni target: {StopSharePercent:0.#}% delle uscite a barriera. "
          + $"Il {NeitherSharePercent:0.#}% delle finestre non tocca nulla. "
          + (AmbiguousBars > 0
              ? $"{AmbiguousBars} di questi stop ({AmbiguousSharePercent:0.#}%) vengono da barre che avevano toccato "
                + "anche il target: dentro la barra l'ordine non è osservabile e il motore assegna l'esito peggiore. "
              : string.Empty)
          + $"Valore atteso per operazione, costi compresi: {ExpectedValuePercent:0.###}%.";
}
