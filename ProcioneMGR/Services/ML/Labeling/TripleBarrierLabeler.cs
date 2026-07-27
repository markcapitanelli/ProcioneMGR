using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.ML.Labeling;

// =============================================================================================
//  [C4 roadmap — M1 della roadmap intraday archiviata] TRIPLE-BARRIER LABELING (López de Prado,
//  AFML cap. 3).
//
//  Il problema che risolve: etichettare una barra col rendimento a orizzonte FISSO ("quanto rende
//  fra 10 barre") ignora il percorso. Una posizione che scende del 5% e poi risale a +1% viene
//  etichettata come vincente, ma nella realtà lo stop l'avrebbe chiusa in perdita. Il triple-barrier
//  etichetta con la barriera che si tocca PER PRIMA — profitto, stop o tempo — che è ciò che
//  succede davvero a un ordine con bracket.
//
//  DUE ONESTÀ STRUTTURALI, entrambe verificate dai test:
//
//  1. AMBIGUITÀ INTRA-BARRA. Se dentro la stessa barra il massimo tocca il take-profit E il minimo
//     tocca lo stop, l'OHLC NON dice quale sia arrivato prima. Qui si assume sempre lo STOP: è la
//     scelta pessimistica, l'unica che non produce un backtest più bello della realtà. È la stessa
//     asimmetria già annotata nell'audit di luglio fra stop intra-barra nel backtest e stop
//     sul-close nel vivo — qui viene dichiarata invece che subita.
//
//  2. CODA NON ETICHETTABILE. Le ultime barre non hanno abbastanza futuro per risolvere la
//     barriera verticale: restano SENZA etichetta invece di riceverne una troncata. Un'etichetta
//     costruita su un orizzonte più corto degli altri sarebbe un dato diverso spacciato per uguale.
// =============================================================================================

/// <summary>Quale barriera è stata toccata per prima.</summary>
public enum TripleBarrierOutcome
{
    /// <summary>Stop loss: la barriera avversa.</summary>
    Stop = -1,

    /// <summary>Barriera verticale (tempo scaduto) senza toccare né profitto né stop.</summary>
    Vertical = 0,

    /// <summary>Take profit: la barriera favorevole.</summary>
    Profit = 1,
}

/// <summary>
/// Etichetta di una singola barra di ingresso: quale barriera, quando, con che rendimento.
/// </summary>
public sealed record TripleBarrierLabel(
    int EntryIndex,
    DateTime EntryUtc,
    int ExitIndex,
    DateTime ExitUtc,
    TripleBarrierOutcome Outcome,
    decimal ReturnPercent,
    decimal EntryPrice,
    decimal ExitPrice)
{
    /// <summary>Barre di detenzione: l'ampiezza su cui questa etichetta "occupa" la serie.</summary>
    public int BarsHeld => ExitIndex - EntryIndex;
}

/// <summary>Parametri delle tre barriere. Le percentuali sono positive e misurate dall'ingresso.</summary>
public sealed class TripleBarrierConfig
{
    /// <summary>Barriera favorevole, in % dall'ingresso (es. 2 = +2% per un long). Zero o meno = disattivata.</summary>
    public decimal ProfitTakePercent { get; set; } = 2m;

    /// <summary>Barriera avversa, in % dall'ingresso (valore POSITIVO, es. 1 = −1% per un long). Zero o meno = disattivata.</summary>
    public decimal StopLossPercent { get; set; } = 1m;

    /// <summary>Barriera verticale: massimo numero di barre di detenzione.</summary>
    public int VerticalBarrierBars { get; set; } = 10;

    /// <summary>Lato dell'ipotetico ingresso.</summary>
    public OrderSide Side { get; set; } = OrderSide.Buy;
}

/// <summary>
/// Etichettatura triple-barrier e pesi di campione per etichette sovrapposte. Puro e
/// deterministico: nessun DB, nessun orologio, nessuno stato.
/// </summary>
public interface ITripleBarrierLabeler
{
    /// <summary>Etichetta ogni barra di ingresso risolvibile. Le barre in coda restano senza etichetta.</summary>
    IReadOnlyList<TripleBarrierLabel> Label(IReadOnlyList<OhlcvData> candles, TripleBarrierConfig config);

    /// <summary>
    /// Configurazione con barriere derivate dai percentili di escursione della serie
    /// (<see cref="ExcursionAnalyzer"/>), invece che da numeri scelti a mano.
    /// </summary>
    TripleBarrierConfig SuggestConfig(IReadOnlyList<OhlcvData> candles, OrderSide side, int verticalBarrierBars);

    /// <summary>
    /// Peso di ciascuna etichetta per UNICITÀ MEDIA (AFML §4.3): quanto poco la sua finestra di vita
    /// è condivisa con le altre. Allineato per indice a <paramref name="labels"/>.
    /// </summary>
    IReadOnlyList<double> AverageUniqueness(IReadOnlyList<TripleBarrierLabel> labels, int barCount);
}

/// <inheritdoc cref="ITripleBarrierLabeler"/>
public sealed class TripleBarrierLabeler : ITripleBarrierLabeler
{
    private readonly ExcursionAnalyzer _excursion = new();

    public IReadOnlyList<TripleBarrierLabel> Label(IReadOnlyList<OhlcvData> candles, TripleBarrierConfig config)
    {
        ArgumentNullException.ThrowIfNull(candles);
        config ??= new TripleBarrierConfig();

        var horizon = Math.Max(1, config.VerticalBarrierBars);
        var isLong = config.Side == OrderSide.Buy;
        var labels = new List<TripleBarrierLabel>();

        // L'ingresso è al close della barra i; le barriere si valutano da i+1 in poi. Le ultime
        // `horizon` barre non hanno futuro sufficiente e restano fuori (vedi nota 2 in testa).
        for (var i = 0; i + horizon < candles.Count; i++)
        {
            var entry = candles[i].Close;
            if (entry <= 0m) continue;

            // Livelli assoluti delle due barriere, orientati dal lato.
            decimal? profitLevel = config.ProfitTakePercent > 0m
                ? (isLong ? entry * (1m + config.ProfitTakePercent / 100m) : entry * (1m - config.ProfitTakePercent / 100m))
                : null;
            decimal? stopLevel = config.StopLossPercent > 0m
                ? (isLong ? entry * (1m - config.StopLossPercent / 100m) : entry * (1m + config.StopLossPercent / 100m))
                : null;

            var resolved = false;
            for (var j = i + 1; j <= i + horizon && !resolved; j++)
            {
                var bar = candles[j];
                var hitProfit = profitLevel is { } p && (isLong ? bar.High >= p : bar.Low <= p);
                var hitStop = stopLevel is { } s && (isLong ? bar.Low <= s : bar.High >= s);

                if (!hitProfit && !hitStop) continue;

                // AMBIGUITÀ INTRA-BARRA: se entrambe sono toccate nella stessa barra l'OHLC non dice
                // quale sia venuta prima. Si sceglie SEMPRE lo stop — la lettura pessimistica.
                if (hitStop)
                {
                    labels.Add(Build(candles, i, j, TripleBarrierOutcome.Stop, entry, stopLevel!.Value, isLong));
                }
                else
                {
                    labels.Add(Build(candles, i, j, TripleBarrierOutcome.Profit, entry, profitLevel!.Value, isLong));
                }
                resolved = true;
            }

            if (!resolved)
            {
                // Barriera verticale: si esce al close dell'ultima barra dell'orizzonte.
                var exitIndex = i + horizon;
                labels.Add(Build(candles, i, exitIndex, TripleBarrierOutcome.Vertical, entry, candles[exitIndex].Close, isLong));
            }
        }

        return labels;
    }

    private static TripleBarrierLabel Build(
        IReadOnlyList<OhlcvData> candles, int entryIndex, int exitIndex,
        TripleBarrierOutcome outcome, decimal entry, decimal exitPrice, bool isLong)
    {
        // Il rendimento è quello del LATO: per uno short un prezzo che scende è un guadagno.
        var raw = (exitPrice - entry) / entry * 100m;
        var ret = isLong ? raw : -raw;
        return new TripleBarrierLabel(
            entryIndex, candles[entryIndex].TimestampUtc,
            exitIndex, candles[exitIndex].TimestampUtc,
            outcome, ret, entry, exitPrice);
    }

    public TripleBarrierConfig SuggestConfig(IReadOnlyList<OhlcvData> candles, OrderSide side, int verticalBarrierBars)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var horizon = Math.Max(1, verticalBarrierBars);

        // Le barriere vengono dalle escursioni REALI della serie all'orizzonte scelto (MAE→stop,
        // MFE→profitto), non da numeri tondi: è lo stesso motore che già alimenta il suggeritore
        // SL/TP della pagina Backtest, quindi le due cose non possono divergere.
        var bracket = _excursion.SuggestAdaptiveBracket(candles, side, horizon);
        return new TripleBarrierConfig
        {
            ProfitTakePercent = bracket.TakeProfitPercent,
            StopLossPercent = bracket.StopLossPercent,
            VerticalBarrierBars = horizon,
            Side = side,
        };
    }

    public IReadOnlyList<double> AverageUniqueness(IReadOnlyList<TripleBarrierLabel> labels, int barCount)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0) return [];

        // CONCORRENZA per barra: quante etichette sono "vive" su quella barra. Con etichette che si
        // sovrappongono (e col triple-barrier si sovrappongono quasi sempre) un addestramento che le
        // pesasse tutte uguali darebbe più voce ai periodi affollati, che non sono più informativi —
        // sono solo contati più volte. Da qui i pesi di AFML §4.3.
        var n = Math.Max(barCount, labels.Max(l => l.ExitIndex) + 1);
        var concurrency = new int[n];
        foreach (var l in labels)
        {
            for (var t = l.EntryIndex; t <= l.ExitIndex && t < n; t++) concurrency[t]++;
        }

        var weights = new double[labels.Count];
        for (var k = 0; k < labels.Count; k++)
        {
            var l = labels[k];
            double sum = 0;
            var span = 0;
            for (var t = l.EntryIndex; t <= l.ExitIndex && t < n; t++)
            {
                if (concurrency[t] > 0) sum += 1.0 / concurrency[t];
                span++;
            }
            weights[k] = span > 0 ? sum / span : 0;
        }
        return weights;
    }
}
