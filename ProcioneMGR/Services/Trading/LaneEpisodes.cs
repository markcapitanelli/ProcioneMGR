using System.Text.Json;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Da dove viene ciò che si sa di un episodio. La distinzione non è pedanteria: gli episodi
/// PRIMA del 2026-08-06 hanno una voce di `StartEngine` che non registrava simbolo né strategie,
/// quindi il simbolo si ricava dagli ordini stessi e le strategie non si sanno. Dirlo è la
/// differenza fra un'informazione e una ricostruzione.
/// </summary>
public enum LaneEpisodeSource
{
    /// <summary>Simbolo e strategie letti dalla voce di avvio: è ciò che il motore ha dichiarato.</summary>
    Declared,

    /// <summary>Simbolo dedotto dagli ordini dell'episodio; le strategie non sono recuperabili.</summary>
    InferredFromOrders,

    /// <summary>Nessun ordine e nessun dettaglio: dell'episodio si sa solo che è esistito.</summary>
    Unknown,
}

/// <summary>
/// Un tratto di vita di una corsia: da un avvio del motore al successivo. È l'unità che mancava —
/// senza, lo storico di una corsia è un mucchio indistinto di esperimenti diversi.
/// </summary>
public sealed record LaneEpisode(
    int Index,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string Symbol,
    string Timeframe,
    IReadOnlyList<string> StrategyNames,
    string Mode,
    int OrderCount,
    LaneEpisodeSource Source,
    IReadOnlyList<int> SavedStrategyIds)
{
    /// <summary>L'episodio in corso: nessun avvio successivo lo ha chiuso.</summary>
    public bool IsCurrent => EndedAtUtc is null;

    /// <summary>Etichetta breve per la UI: «Composite DOT/USDT 15m» oppure «DOT/USDT (strategia non registrata)».</summary>
    public string Title
    {
        get
        {
            var strumento = string.IsNullOrEmpty(Symbol) ? "simbolo ignoto" : Symbol;
            if (!string.IsNullOrEmpty(Timeframe)) strumento += " " + Timeframe;
            return StrategyNames.Count > 0
                ? $"{string.Join(" + ", StrategyNames)} {strumento}"
                : $"{strumento} (strategia non registrata)";
        }
    }
}

/// <summary>
/// [2026-08-06] Ricostruisce gli episodi di una corsia dagli avvii del motore.
///
/// <para><b>Perché non serviva una tabella nuova</b>: i confini erano già a database. Ogni
/// `StartEngine` nel registro di audit apre un tratto, il successivo lo chiude — la corsia 0 ne
/// aveva 11, mai usati. Mancava solo COSA girasse in ciascuno, ed è un buco di tre campi nel
/// payload, colmato dal 2026-08-06 in poi.</para>
///
/// <para><b>Il compromesso onesto sul passato</b>: per gli episodi vecchi il payload non ha
/// simbolo né strategie. Il simbolo si ricava dagli ordini caduti dentro l'intervallo — è una
/// deduzione solida (un episodio opera su un simbolo solo) ma resta una deduzione, e
/// <see cref="LaneEpisodeSource"/> la dichiara. Le strategie di allora non sono recuperabili: si
/// dice «non registrata», non si inventa.</para>
///
/// <para>Puro e senza I/O: riceve le voci di avvio e gli ordini già letti.</para>
/// </summary>
public static class LaneEpisodeBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// <paramref name="startEvents"/> = le voci `StartEngine` della corsia, in qualunque ordine.
    /// <paramref name="orders"/> = gli ordini della corsia. Gli episodi tornano dal più RECENTE.
    /// </summary>
    public static IReadOnlyList<LaneEpisode> Build(
        IEnumerable<TradingAuditLog> startEvents,
        IEnumerable<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(startEvents);
        ArgumentNullException.ThrowIfNull(orders);

        var starts = startEvents
            .Where(e => string.Equals(e.Action, "StartEngine", StringComparison.Ordinal))
            .OrderBy(e => e.TimestampUtc)
            .ToList();
        if (starts.Count == 0) return [];

        var ordini = orders.OrderBy(o => o.CreatedAtUtc).ToList();
        var episodi = new List<LaneEpisode>(starts.Count);

        for (var i = 0; i < starts.Count; i++)
        {
            var inizio = starts[i].TimestampUtc;
            // L'episodio finisce dove comincia il successivo. L'ultimo resta aperto: uno StopEngine
            // ferma il motore ma NON chiude l'episodio — la corsia resta configurata su quella
            // strategia, e un riavvio senza cambi è lo stesso esperimento che riprende.
            DateTime? fine = i + 1 < starts.Count ? starts[i + 1].TimestampUtc : null;

            var dentro = ordini
                .Where(o => o.CreatedAtUtc >= inizio && (fine is null || o.CreatedAtUtc < fine))
                .ToList();

            var (simbolo, timeframe, strategie, modo, salvate, dichiarato) = ReadPayload(starts[i]);

            if (!dichiarato)
            {
                // Il simbolo si deduce dagli ordini: se ce n'è più d'uno (non dovrebbe) si prende
                // il più frequente, che è la lettura meno arbitraria.
                simbolo = dentro
                    .Where(o => !string.IsNullOrEmpty(o.Symbol))
                    .GroupBy(o => o.Symbol)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => g.Key)
                    .FirstOrDefault() ?? string.Empty;
            }

            var fonte = dichiarato
                ? LaneEpisodeSource.Declared
                : string.IsNullOrEmpty(simbolo) ? LaneEpisodeSource.Unknown : LaneEpisodeSource.InferredFromOrders;

            episodi.Add(new LaneEpisode(
                Index: i + 1,
                StartedAtUtc: inizio,
                EndedAtUtc: fine,
                Symbol: simbolo,
                Timeframe: timeframe,
                StrategyNames: strategie,
                Mode: modo,
                OrderCount: dentro.Count,
                Source: fonte,
                SavedStrategyIds: salvate));
        }

        episodi.Reverse();   // il più recente per primo: è quello che si guarda
        return episodi;
    }

    /// <summary>
    /// Gli ordini più vecchi del primo avvio annotato: nessun episodio li contiene. Non è un caso
    /// di scuola — sulla corsia 0 sono 474 su 500, perché il registro di audit ha cominciato ad
    /// annotare <c>StartEngine</c> il 30/06 mentre gli ordini partono dal 1º giugno. Mostrarli
    /// insieme agli altri li farebbe sembrare parte dell'ultimo episodio; nasconderli sarebbe
    /// peggio. Restano a parte, dichiarati per quello che sono.
    /// </summary>
    public static IReadOnlyList<Order> OrphanOrders(
        IReadOnlyList<LaneEpisode> episodes,
        IEnumerable<Order> orders)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(orders);

        // Nessun episodio: non c'è un "prima", ci sono solo ordini. Il chiamante li mostra piatti.
        if (episodes.Count == 0) return [];

        var primoAvvio = episodes.Min(e => e.StartedAtUtc);
        return [.. orders.Where(o => o.CreatedAtUtc < primoAvvio)];
    }

    /// <summary>
    /// Legge il payload dell'avvio. <c>dichiarato</c> è vero solo se contiene DAVVERO il simbolo —
    /// il campo esiste dal 2026-08-06, e la sua assenza è il modo per riconoscere un episodio
    /// vecchio senza doverne indovinare la data.
    /// </summary>
    private static (string Symbol, string Timeframe, IReadOnlyList<string> Strategies, string Mode,
                    IReadOnlyList<int> SavedStrategyIds, bool Declared)
        ReadPayload(TradingAuditLog entry)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<StartPayload>(entry.Details, JsonOpts);
            if (dto is null) return ("", "", [], entry.Mode.ToString(), [], false);

            var dichiarato = !string.IsNullOrWhiteSpace(dto.Symbol);
            return (
                dto.Symbol ?? "",
                dto.Timeframe ?? "",
                dto.StrategyNames ?? [],
                dto.Mode ?? entry.Mode.ToString(),
                dto.SavedStrategyIds ?? [],
                dichiarato);
        }
        catch (JsonException)
        {
            // Payload illeggibile: si degrada a episodio dedotto, non si perde il confine.
            return ("", "", [], entry.Mode.ToString(), [], false);
        }
    }

    private sealed class StartPayload
    {
        public string? Mode { get; set; }
        public string? Symbol { get; set; }
        public string? Timeframe { get; set; }
        public List<string>? StrategyNames { get; set; }

        /// <summary>Gli id delle strategie SALVATE, il ponte verso /strategies. Vuoto sugli episodi vecchi.</summary>
        public List<int>? SavedStrategyIds { get; set; }
    }
}
