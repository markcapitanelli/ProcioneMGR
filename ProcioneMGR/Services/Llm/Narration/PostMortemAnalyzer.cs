using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Llm.Narration;

/// <summary>
/// [G4] Il MENÙ CHIUSO delle cause di un'operazione andata male. L'AI può scegliere solo qui
/// dentro: fuori menù, JSON rotto, timeout o assenza ⇒ <see cref="Inconclusive"/>, esattamente
/// come il comitato AF3 ricade sul suo default.
/// </summary>
public static class PostMortemCauses
{
    /// <summary>Il mercato era in un regime diverso da quello in cui la strategia è stata validata.</summary>
    public const string AdverseRegime = "RegimeAvverso";

    /// <summary>Lo stop è stato colpito e poi il prezzo è tornato a favore: protezione troppo stretta.</summary>
    public const string TightStop = "StopStretto";

    /// <summary>Le entrate non hanno più il margine che avevano in holdout.</summary>
    public const string DegradedSignal = "SegnaleDegradato";

    /// <summary>Il lordo era positivo, i costi l'hanno mangiato. CALCOLABILE: non serve l'AI.</summary>
    public const string CostsDominate = "CostiDominanti";

    /// <summary>Dentro la variabilità attesa: non c'è niente da spiegare.</summary>
    public const string NormalNoise = "RumoreNormale";

    /// <summary>Chiusura forzata dall'exchange. CALCOLABILE: non serve l'AI.</summary>
    public const string Liquidation = "Liquidazione";

    /// <summary>Il default deterministico: nessuno ha saputo dire di più.</summary>
    public const string Inconclusive = "Inconcludente";

    /// <summary>Le voci che l'AI può scegliere (le calcolabili restano al codice).</summary>
    public static readonly IReadOnlyList<string> AiSelectable =
        [AdverseRegime, TightStop, DegradedSignal, NormalNoise, Inconclusive];

    /// <summary>Tutte le voci ammesse in <see cref="Data.TradePostMortem.Cause"/>.</summary>
    public static readonly IReadOnlyList<string> All =
        [AdverseRegime, TightStop, DegradedSignal, CostsDominate, NormalNoise, Liquidation, Inconclusive];

    public static bool IsValid(string? cause) =>
        cause is not null && All.Contains(cause, StringComparer.Ordinal);

    public static string Label(string cause) => cause switch
    {
        AdverseRegime => "regime di mercato avverso",
        TightStop => "stop troppo stretto",
        DegradedSignal => "segnale degradato",
        CostsDominate => "costi che hanno mangiato il lordo",
        NormalNoise => "rumore dentro la variabilità attesa",
        Liquidation => "liquidazione forzata",
        _ => "inconcludente",
    };
}

/// <summary>I fatti oggettivi di un'operazione: tutto da <c>TradeRecord</c>, niente di interpretato.</summary>
public sealed record TradeFacts(
    int TradeRecordId,
    int LaneId,
    string Symbol,
    string StrategyId,
    string Side,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal PnlPercent,
    decimal GrossPnlPercent,
    decimal FeePercentEstimate,
    TimeSpan Duration,
    string ExitReason,
    bool WasLiquidated,
    string Mode);

/// <summary>
/// [G4] La parte DETERMINISTICA del post-mortem: ricava i fatti da un trade e, dove la causa è
/// aritmetica, la stabilisce senza interpellare nessuna AI.
///
/// <para>È lo stesso principio di G6: ciò che il codice sa calcolare, lo calcola il codice. L'AI
/// serve solo dove serve davvero un'interpretazione — e anche lì sceglie dentro un menù.</para>
/// </summary>
public static class PostMortemAnalyzer
{
    /// <summary>
    /// Estrae i fatti. Il lordo si stima aggiungendo al netto il costo di andata e ritorno
    /// (<paramref name="feePercent"/> per gamba): serve solo a distinguere «il segnale era buono
    /// ma i costi l'hanno mangiato» da «il segnale era sbagliato», e la stima è dichiarata come
    /// tale nel nome del campo.
    /// </summary>
    public static TradeFacts Extract(TradeRecord trade, decimal feePercent)
    {
        ArgumentNullException.ThrowIfNull(trade);
        var roundTripCost = Math.Max(0m, feePercent) * 2m;
        return new TradeFacts(
            trade.Id,
            trade.LaneId,
            trade.Symbol,
            trade.StrategyId,
            trade.Side.ToString(),
            trade.EntryPrice,
            trade.ExitPrice,
            trade.PnlPercent,
            trade.PnlPercent + roundTripCost,
            roundTripCost,
            trade.Duration,
            string.IsNullOrWhiteSpace(trade.ExitReason) ? "(non dichiarato)" : trade.ExitReason,
            trade.WasLiquidated,
            trade.Mode.ToString());
    }

    /// <summary>
    /// La causa che il CODICE sa stabilire da solo, o <c>null</c> se serve un'interpretazione.
    /// Quando restituisce una causa, l'AI non viene interpellata affatto: è aritmetica, e pagare
    /// un LLM per confermarla sarebbe spreco (oltre che un modo per farsi contraddire dai numeri).
    /// </summary>
    public static string? DeterministicCause(TradeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.WasLiquidated) return PostMortemCauses.Liquidation;

        // Lordo positivo e netto negativo: i costi hanno mangiato il segnale. Niente da interpretare.
        if (facts.GrossPnlPercent > 0m && facts.PnlPercent < 0m) return PostMortemCauses.CostsDominate;

        return null;
    }
}
