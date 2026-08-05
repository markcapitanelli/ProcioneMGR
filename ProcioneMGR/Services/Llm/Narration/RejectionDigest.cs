using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Llm.Narration;

/// <summary>
/// [G6] Le classi di bocciatura di un candidato, ricavate dal <see cref="ValidatedCandidate.RejectReason"/>
/// che il motore scrive. Sono ETICHETTE per raggruppare, non un giudizio nuovo: il verdetto resta
/// quello del gate che ha respinto.
/// </summary>
public static class RejectionCauses
{
    public const string SharpeHoldout = "SharpeHoldout";
    public const string ContoTrade = "ContoTrade";
    public const string DeflatedSharpe = "DeflatedSharpe";
    public const string PanelPbo = "PanelPbo";
    public const string Permutation = "Permutation";
    public const string NullTwin = "NullTwin";
    public const string MonteCarlo = "MonteCarlo";
    public const string BacktestFailed = "BacktestFailed";
    public const string NoTrades = "NoTrades";

    /// <summary>Bocciato senza motivo dichiarato: si dice, non si inventa una causa.</summary>
    public const string Undeclared = "Undeclared";

    /// <summary>
    /// Motivo presente ma non riconosciuto dal classificatore. Esiste perché il classificatore
    /// legge stringhe scritte altrove: se il motore cambia un messaggio, i candidati finiscono qui
    /// e la UI lo dice — invece di essere silenziosamente contati sotto l'etichetta sbagliata.
    /// </summary>
    public const string Other = "Other";

    /// <summary>Etichetta leggibile in italiano, per la UI e per il prompt.</summary>
    public static string Label(string cause) => cause switch
    {
        SharpeHoldout => "Sharpe holdout sotto la soglia",
        ContoTrade => "troppo pochi trade in holdout",
        DeflatedSharpe => "Deflated Sharpe sotto la soglia (sospetto overfitting da selezione)",
        PanelPbo => "PBO di pannello troppo alto (selezione inaffidabile)",
        Permutation => "Sharpe compatibile col rumore (test di permutazione)",
        NullTwin => "battuto dal gemello sintetico",
        MonteCarlo => "rischio Monte Carlo oltre il tetto",
        BacktestFailed => "backtest fallito",
        NoTrades => "nessun trade nel range di selezione",
        Undeclared => "bocciato senza motivo dichiarato",
        _ => "motivo non riconosciuto dal classificatore",
    };
}

/// <summary>Quanti candidati sono caduti su una data causa.</summary>
public sealed record RejectionGroup(string Cause, string Label, int Count);

/// <summary>
/// Un candidato bocciato con i suoi numeri VERI. Nessun campo derivato dall'AI: questi valori
/// vengono dal verdetto del motore e sono ciò contro cui una prosa sbagliata si smaschera da sola.
/// </summary>
public sealed record RejectedCandidateFacts(
    string Key,
    string StrategyName,
    string Symbol,
    string Timeframe,
    decimal HoldoutSharpe,
    int HoldoutTrades,
    decimal HoldoutReturn,
    double? DeflatedSharpe,
    double? PanelPbo,
    double? NullTwinPercentile,
    string Cause,
    string CauseLabel,
    string RejectReason,
    bool IsGrey);

/// <summary>
/// [G6] Il ritratto DETERMINISTICO delle bocciature di un run: quanti candidati, quanti
/// sopravvissuti, per quale causa sono caduti gli altri, e i migliori fra i bocciati coi loro
/// numeri.
///
/// <para><b>Il punto di questa classe</b>: è calcolata in C# da dati già presenti, costa zero e
/// NON richiede l'AI. La spiegazione in prosa (<see cref="IRejectionNarrator"/>) si appoggia a
/// questo, non lo sostituisce — così la funzione ha valore anche col layer AI spento, e la prosa
/// viene sempre mostrata ACCANTO ai numeri veri: se l'AI scrive un numero sbagliato, si vede.</para>
/// </summary>
public sealed record RunRejectionDigest(
    int Evaluated,
    int Survived,
    int Rejected,
    int GreyCount,
    IReadOnlyList<RejectionGroup> Groups,
    IReadOnlyList<RejectedCandidateFacts> TopRejected)
{
    public static readonly RunRejectionDigest Empty =
        new(0, 0, 0, 0, [], []);

    /// <summary>True se c'è qualcosa da raccontare (almeno un bocciato).</summary>
    public bool HasContent => Rejected > 0;
}

/// <summary>[G6] Costruttore puro del <see cref="RunRejectionDigest"/>. Nessuna dipendenza, nessun I/O.</summary>
public static class RejectionDigestBuilder
{
    /// <summary>Quanti candidati bocciati riportare per esteso. Oltre, si contano soltanto.</summary>
    public const int DefaultTopN = 5;

    /// <summary>
    /// Classifica un <see cref="ValidatedCandidate.RejectReason"/> nella sua causa.
    ///
    /// <para>Legge PREFISSI delle stringhe scritte da <c>ModelStages</c> e
    /// <c>NullTwinValidationStage</c>. È un accoppiamento che va dichiarato invece che nascosto:
    /// se un messaggio del motore cambia, il candidato finisce in <see cref="RejectionCauses.Other"/>
    /// e la UI lo mostra come «motivo non riconosciuto» — mai contato sotto l'etichetta sbagliata.
    /// Il test <c>RejectionDigestTests.Classify_RiconosceIMessaggiRealiDelMotore</c> tiene le due
    /// parti allineate: usa le stringhe vere e fallisce se divergono.</para>
    /// </summary>
    public static string Classify(string? rejectReason)
    {
        if (string.IsNullOrWhiteSpace(rejectReason)) return RejectionCauses.Undeclared;

        var r = rejectReason.TrimStart();
        if (r.StartsWith("Sharpe holdout", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.SharpeHoldout;
        if (r.StartsWith("Solo ", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.ContoTrade;
        if (r.StartsWith("DSR ", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.DeflatedSharpe;
        if (r.StartsWith("PBO", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.PanelPbo;
        if (r.StartsWith("permutation", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.Permutation;
        if (r.StartsWith("Gemello nullo", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.NullTwin;
        if (r.StartsWith("MC RiskFactor", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.MonteCarlo;
        if (r.StartsWith("Backtest fallito", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.BacktestFailed;
        if (r.StartsWith("Nessun trade", StringComparison.OrdinalIgnoreCase)) return RejectionCauses.NoTrades;
        return RejectionCauses.Other;
    }

    /// <summary>
    /// Costruisce il ritratto. <paramref name="topN"/> limita SOLO quanti bocciati vengono
    /// riportati per esteso: i conteggi per causa coprono sempre tutti.
    ///
    /// <para>L'ordine dei «migliori fra i bocciati» è lo Sharpe holdout decrescente, e si chiama
    /// così per onestà: NON è una distanza dal passaggio. Le soglie dei gate sono eterogenee (un
    /// DSR a 0,94 e un conteggio trade a 18 non stanno sulla stessa scala) e una distanza unica
    /// sarebbe un numero inventato.</para>
    /// </summary>
    public static RunRejectionDigest Build(IReadOnlyList<ValidatedCandidate>? candidates, int topN = DefaultTopN)
    {
        if (candidates is null || candidates.Count == 0) return RunRejectionDigest.Empty;

        var survived = candidates.Count(c => c.Survived);
        var rejected = candidates.Where(c => !c.Survived).ToList();
        if (rejected.Count == 0)
        {
            return new RunRejectionDigest(candidates.Count, survived, 0, 0, [], []);
        }

        var groups = rejected
            .GroupBy(c => Classify(c.RejectReason))
            .Select(g => new RejectionGroup(g.Key, RejectionCauses.Label(g.Key), g.Count()))
            // Prima le cause più frequenti; a parità, ordine alfabetico stabile (i test contano su
            // un ordine deterministico, e un ordine che balla fra due run identici è rumore in UI).
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Cause, StringComparer.Ordinal)
            .ToList();

        var top = rejected
            .OrderByDescending(c => c.HoldoutSharpe)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, topN))
            .Select(ToFacts)
            .ToList();

        var grey = rejected.Count(Fleet.FleetStateReader.IsGrey);

        return new RunRejectionDigest(candidates.Count, survived, rejected.Count, grey, groups, top);
    }

    private static RejectedCandidateFacts ToFacts(ValidatedCandidate c)
    {
        var cause = Classify(c.RejectReason);
        return new RejectedCandidateFacts(
            c.Key,
            c.StrategyName,
            c.Symbol,
            c.Timeframe,
            c.HoldoutSharpe,
            c.HoldoutTrades,
            c.HoldoutReturn,
            c.DeflatedSharpe,
            c.PanelPbo,
            c.NullTwinPercentile,
            cause,
            RejectionCauses.Label(cause),
            c.RejectReason ?? "(non dichiarato)",
            Fleet.FleetStateReader.IsGrey(c));
    }
}
