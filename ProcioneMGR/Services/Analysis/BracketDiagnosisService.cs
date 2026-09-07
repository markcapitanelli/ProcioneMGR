using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Trading;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// [2026-09-07] <b>Perché una corsia chiude in stop: il previsto, l'osservato, e lo scarto fra i due.</b>
///
/// <para><b>La domanda.</b> «Molte operazioni si chiudono con uno stop loss invece che con un take
/// profit, dovrei capire perché — se è il server andato giù, se l'investimento non era corretto, o
/// se gli stop e i take sono stati messi male.» Le tre ipotesi sono verificabili separatamente, e
/// questo servizio le separa: <b>prima calcola quanti stop quel bracket produce da solo</b>, poi
/// conta quelli veri, e mette i due numeri accanto. Senza il primo, «troppi stop» non è
/// falsificabile.</para>
///
/// <para><b>Ipotesi già escluse per misura</b> (2026-09-06, 60 giorni di storia): zero stop su 82
/// entro un'ora da un riavvio o da un rilascio, contro un 9,9% atteso per puro caso; zero su 119 con
/// un buco di candele nelle tre barre precedenti; prezzo d'uscita dentro il minimo-massimo della
/// barra 119 volte su 119. <b>Il server andato giù non c'entra.</b> Il controllo resta qui come
/// invariante ricalcolato, non come conclusione scritta una volta.</para>
///
/// <para><b>Le quattro risposte possibili, e nessuna imputazione.</b> Il verdetto per corsia è uno di
/// <see cref="VerdettoBracket"/>. «Non giudicabile» è un esito di prima classe e non un errore: con
/// meno di <see cref="BracketDiagnosisOptions.MinBarrierExits"/> operazioni vive il confronto non ha potere statistico, e
/// riempire il buco con una stima sarebbe fabbricare una risposta. La piattaforma ha già pagato
/// questo errore altrove.</para>
///
/// <para><b>Advisory-only, sempre.</b> Non scrive niente: né la configurazione delle corsie né i
/// parametri di rischio. La scansione delle geometrie alternative
/// (<see cref="BracketRaceAnalyzer.Scan"/>) produce numeri da guardare, non da applicare. È la stessa
/// regola del layer AI — pareri e veti, mai esecuzione — e vale a maggior ragione qui, dove ciò che
/// si toccherebbe è la protezione delle posizioni.</para>
/// </summary>
public sealed class BracketDiagnosisService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    IOptionsMonitor<BracketDiagnosisOptions> options,
    ILogger<BracketDiagnosisService> logger)
{
    /// <summary>Le soglie in vigore adesso. Rilette a ogni misura: sono manopole calde.</summary>
    public BracketDiagnosisOptions Soglie => options.CurrentValue;

    /// <summary>Diagnosi di tutte le corsie configurate.</summary>
    public async Task<IReadOnlyList<DiagnosiCorsia>> DiagnoseAllAsync(CancellationToken ct = default)
    {
        var esiti = new List<DiagnosiCorsia>();
        for (var laneId = 0; laneId < TradingLanes.Count; laneId++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                esiti.Add(await DiagnoseAsync(laneId, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Fail-open sulla diagnostica: una corsia che non si legge non deve nascondere le altre.
                logger.LogWarning(ex, "Diagnosi del bracket fallita per la corsia {LaneId}.", laneId);
                esiti.Add(DiagnosiCorsia.NonMisurabile(laneId, $"lettura fallita: {ex.Message}"));
            }
        }
        return esiti;
    }

    /// <summary>Diagnosi di una corsia: geometria schierata, corsa nominale, uscite vere, verdetto.</summary>
    public async Task<DiagnosiCorsia> DiagnoseAsync(int laneId, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var stato = await db.TradingEngineStates.AsNoTracking()
            .Where(s => s.LaneId == laneId).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);

        var config = await serviceProvider.GetRequiredKeyedService<IEnsembleManager>(laneId)
            .GetConfigurationAsync(ct);

        // Il simbolo VERO è quello con cui il motore è partito; la configurazione può essere già
        // stata riscritta sotto una corsia in corsa (l'auto-apply lo fa) e mentirebbe sul passato.
        var symbol = !string.IsNullOrWhiteSpace(stato?.Symbol) ? stato!.Symbol : config.Symbol;
        var timeframe = !string.IsNullOrWhiteSpace(stato?.Timeframe) ? stato!.Timeframe : config.Timeframe;

        // ── La geometria schierata ────────────────────────────────────────────────────────────────
        // Le gambe attive con protezioni. Se ne hanno di diverse si media, come fa AutoBracket: il
        // bracket è per POSIZIONE e le posizioni della corsia condividono la stessa serie.
        var gambe = config.Strategies.Where(s => s.IsActive).ToList();
        var conStop = gambe.Where(s => s.StopLossPercent is > 0m && s.TakeProfitPercent is > 0m).ToList();

        if (conStop.Count == 0)
        {
            return new DiagnosiCorsia(laneId, symbol, timeframe, stato?.IsRunning ?? false,
                VerdettoBracket.NonMisurabile,
                gambe.Count == 0
                    ? "nessuna gamba attiva sulla corsia"
                    : $"nessuna delle {gambe.Count} gambe attive ha stop e target impostati",
                Nominale: null, Osservato: null, Alternative: []);
        }

        var stop = Math.Round(conStop.Average(s => s.StopLossPercent!.Value), 4);
        var take = Math.Round(conStop.Average(s => s.TakeProfitPercent!.Value), 4);

        // ── La corsa nominale sulle candele vere della corsia ─────────────────────────────────────
        var candele = await db.OhlcvData.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
            .OrderByDescending(c => c.TimestampUtc)
            .Take(Math.Clamp(opts.MaxCandles, 100, 200_000))
            .ToListAsync(ct);
        candele.Reverse();

        // ── Le uscite vere, con tutti gli scarti dichiarati ───────────────────────────────────────
        // Si leggono PRIMA della corsa, perché è da qui che esce l'orizzonte su cui misurarla.
        var (osservato, durataMediana) = await ContaUsciteAsync(db, laneId, symbol, timeframe, ct);

        var orizzonte = OrizzonteBarre(timeframe, durataMediana);
        var nominale = BracketRaceAnalyzer.Run(candele, stop, take, orizzonte, opts.RoundTripCostPercent);
        var alternative = nominale.Misurabile
            ? BracketRaceAnalyzer.Scan(candele, stop, BracketRaceAnalyzer.DefaultMultipliers, orizzonte, opts.RoundTripCostPercent)
            : [];

        // ── Il verdetto ───────────────────────────────────────────────────────────────────────────
        if (!nominale.Misurabile)
        {
            return new DiagnosiCorsia(laneId, symbol, timeframe, stato?.IsRunning ?? false,
                VerdettoBracket.NonMisurabile, nominale.Motivo ?? "corsa non misurabile",
                nominale, osservato, alternative);
        }

        var minime = Math.Max(1, opts.MinBarrierExits);
        if (osservato.UsciteABarriera < minime)
        {
            var mancanti = minime - osservato.UsciteABarriera;
            var attesi = conStop.Sum(s => s.ExpectedTradesPerMonth ?? 0m);
            var quando = attesi > 0m
                ? $"al ritmo dichiarato dalle gambe ({attesi:0.#} operazioni al mese) servono circa {mancanti / attesi:0.#} mesi"
                : "le gambe non dichiarano un ritmo atteso, quindi non si può dire quando arriveranno";

            // Due cause diverse per lo stesso verdetto, e vanno distinte: «non ne sono ancora
            // arrivate abbastanza» è un'attesa che finirà; «ce ne sono, ma nessuna si può datare» è
            // un difetto della STORIA, e nessuna attesa lo risolve — servono uscite nuove.
            var perNonDatabili = osservato.UsciteABarrieraNonDatabili > osservato.UsciteABarriera
                ? $" In tabella ci sono altre {osservato.UsciteABarrieraNonDatabili} uscite a barriera, ma "
                  + "sono precedenti all'ora di parete e non si può sapere se siano avvenute: aspettare non le "
                  + "renderà databili, servono uscite nuove."
                : string.Empty;

            return new DiagnosiCorsia(laneId, symbol, timeframe, stato?.IsRunning ?? false,
                VerdettoBracket.NonGiudicabile,
                $"{osservato.UsciteABarriera} uscite a barriera vive su {minime} necessarie; {quando}.{perNonDatabili}",
                nominale, osservato, alternative);
        }

        var ammesso = Math.Max(0m, opts.AllowedDeviationPoints);
        var scarto = osservato.StopSharePercent - nominale.StopSharePercent;
        var verdetto = Math.Abs(scarto) <= ammesso
            ? VerdettoBracket.ComePrevisto
            : VerdettoBracket.ScartoDalPrevisto;

        var racconto = verdetto == VerdettoBracket.ComePrevisto
            ? $"osservato {osservato.StopSharePercent:0.#}% di stop contro {nominale.StopSharePercent:0.#}% previsti dalla "
              + $"sola geometria del bracket: lo scarto è {scarto:+0.#;-0.#;0}pp, dentro il rumore. "
              + "Non c'è niente da correggere qui: gli stop sono il numero nominale di questo rapporto stop/target."
            : $"osservato {osservato.StopSharePercent:0.#}% di stop contro {nominale.StopSharePercent:0.#}% previsti: "
              + $"scarto {scarto:+0.#;-0.#;0}pp, oltre i {ammesso:0}pp ammessi. "
              + (scarto > 0
                  ? "La corsia chiude in stop PIÙ spesso di quanto la geometria giustifichi: il segnale sta entrando male."
                  : "La corsia chiude in stop MENO spesso del nominale: il segnale sta aggiungendo qualcosa.");

        return new DiagnosiCorsia(laneId, symbol, timeframe, stato?.IsRunning ?? false,
            verdetto, racconto, nominale, osservato, alternative);
    }

    /// <summary>
    /// L'orizzonte su cui misurare la corsa: <b>la durata mediana vera delle posizioni chiuse</b> di
    /// questa corsia, convertita in barre. Un tasso di tocco senza orizzonte non significa niente, e
    /// l'orizzonte giusto è quello su cui la corsia opera davvero.
    ///
    /// <para><b>Perché non un parametro delle gambe.</b> Una prima stesura leggeva
    /// <c>Parameters["HoldingBars"]</c>. Verificato sul database il 2026-09-07: <b>quella chiave non
    /// esiste</b> su nessuna delle nove gambe attive — le venticinque chiavi vere sono soglie e
    /// periodi di indicatori. Sarebbe stato un ramo morto travestito da adattività, che ricadeva
    /// sempre sul default facendo credere il contrario.</para>
    ///
    /// <para><b>C'è una circolarità, ed è accettata a occhi aperti.</b> L'orizzonte esce dalle stesse
    /// uscite che poi si confrontano col nominale. La si tollera perché la quota di stop è poco
    /// sensibile all'orizzonte — allungandolo crescono entrambi i conteggi — mentre un numero fisso
    /// scelto da noi confronterebbe la corsia con una durata che non è la sua. L'alternativa senza
    /// circolarità sarebbe la durata ATTESA dichiarata dalle gambe, che però non esiste in
    /// configurazione.</para>
    ///
    /// <para>Senza un campione si usa il default dichiarato, e vale la pena saperlo: dieci barre.</para>
    /// </summary>
    private static int OrizzonteBarre(string timeframe, TimeSpan? durataMediana)
    {
        if (durataMediana is not TimeSpan d || d <= TimeSpan.Zero) return BracketRaceAnalyzer.DefaultHorizonBars;

        var ppy = Statistics.PeriodsPerYear(timeframe);
        if (ppy <= 0) return BracketRaceAnalyzer.DefaultHorizonBars;

        var barra = TimeSpan.FromDays(365.0 / ppy);
        var barre = (int)Math.Round(d / barra);
        return Math.Clamp(barre, 1, 500);
    }

    /// <summary>
    /// Le uscite vere della corsia, con OGNI scarto dichiarato. Tre filtri in cascata, e ognuno
    /// lascia il proprio conteggio: repliche dello stesso trade, righe fabbricate dal replay di
    /// candele storiche, righe senza ora di parete che non si possono giudicare né in un senso né
    /// nell'altro. Un totale più basso senza spiegazione si legge come un guasto.
    /// </summary>
    private static async Task<(UsciteOsservate Uscite, TimeSpan? DurataMediana)> ContaUsciteAsync(
        ApplicationDbContext db, int laneId, string symbol, string timeframe, CancellationToken ct)
    {
        var righe = await db.TradeRecords.AsNoTracking()
            .Where(t => t.LaneId == laneId && t.Symbol == symbol)
            .ToListAsync(ct);

        var distinti = TradeDeduplication.Distinti(righe);
        var repliche = TradeDeduplication.Repliche(righe, distinti);

        var vivi = TradeDeduplication.Vivi(distinti, timeframe);
        var replay = TradeDeduplication.Replay(distinti, vivi);

        // [Il punto più delicato di tutto il servizio.] Le righe senza ora di parete sopravvivono a
        // Vivi() per una scelta corretta ma DIVERSA dalla nostra: quel filtro non vuole cancellare la
        // storia delle corsie d'impronta, e «non giudicabile» per lui significa «non la butto via».
        //
        // Qui il verdetto riguarda il comportamento DAL VIVO, e una riga che non si può datare non lo
        // sostiene: non si sa se sia avvenuta o se l'abbia fabbricata un riavvio rigiocando candele
        // vecchie. Misurato sul database il 2026-09-07: sulle corsie 2, 3, 4 e 5 le righe senza
        // orologio sono il 100% del campione, e contarle produrrebbe una percentuale precisa calcolata
        // su un insieme di cui non si sa niente. È lo stesso errore che faceva risultare 27 trade di
        // forward test dove ne era avvenuto uno.
        //
        // Restano CONTATE e mostrate, perché sparire sarebbe peggio: chi legge deve vedere quanto
        // grande è la parte che non entra nel giudizio.
        static bool Datata(TradeRecord t) => t.RecordedAtUtc is not null;
        static bool E(TradeRecord t, string motivo) => string.Equals(t.ExitReason, motivo, StringComparison.OrdinalIgnoreCase);

        var databili = vivi.Where(Datata).ToList();
        var nonDatabili = vivi.Count - databili.Count;

        var stop = databili.Count(t => E(t, "StopLoss"));
        var take = databili.Count(t => E(t, "TakeProfit"));
        var altro = databili.Count - stop - take;

        var stopNonDatabili = vivi.Count(t => !Datata(t) && E(t, "StopLoss"));
        var takeNonDatabili = vivi.Count(t => !Datata(t) && E(t, "TakeProfit"));

        // La durata mediana delle uscite a barriera databili: è l'orizzonte su cui la corsa va
        // misurata. Solo quelle databili, per la stessa ragione del verdetto — una durata calcolata
        // su righe che potrebbero essere replay descriverebbe il replay.
        var durate = databili
            .Where(t => E(t, "StopLoss") || E(t, "TakeProfit"))
            .Select(t => t.Duration)
            .Where(x => x > TimeSpan.Zero)
            .OrderBy(x => x)
            .ToList();
        TimeSpan? mediana = durate.Count > 0 ? durate[durate.Count / 2] : null;

        return (new UsciteOsservate(righe.Count, repliche, replay, nonDatabili,
            stop, take, altro, stopNonDatabili, takeNonDatabili), mediana);
    }
}

/// <summary>Il verdetto su una corsia. Nessuno dei quattro è un errore: sono quattro stati del mondo.</summary>
public enum VerdettoBracket
{
    /// <summary>La geometria non si può misurare: gamba senza protezioni, o serie troppo corta. Il motivo è scritto.</summary>
    NonMisurabile,

    /// <summary>Il previsto esiste, l'osservato non ha abbastanza campione. Non si imputa: si dice quanto manca.</summary>
    NonGiudicabile,

    /// <summary>L'osservato coincide col nominale: gli stop non hanno una causa oltre il rapporto stop/target.</summary>
    ComePrevisto,

    /// <summary>L'osservato si scosta dal nominale oltre il rumore: qui c'è qualcosa che il bracket non spiega.</summary>
    ScartoDalPrevisto,
}

/// <summary>Le uscite vere di una corsia, con tutti gli scarti dichiarati riga per riga.</summary>
public sealed record UsciteOsservate(
    int RigheTotali,
    int RepliqueScartate,
    int ReplayScartati,
    int SenzaOrologio,
    int Stop,
    int Take,
    int AltreUscite,
    int StopNonDatabili = 0,
    int TakeNonDatabili = 0)
{
    /// <summary>
    /// Uscite arrivate a una barriera <b>e databili</b>: sono le sole confrontabili con la corsa
    /// nominale, perché sono le sole di cui si sappia che sono davvero avvenute.
    /// </summary>
    public int UsciteABarriera => Stop + Take;

    /// <summary>Uscite a barriera che esistono in tabella ma non si possono datare. Si mostrano, non si giudicano.</summary>
    public int UsciteABarrieraNonDatabili => StopNonDatabili + TakeNonDatabili;

    public decimal StopSharePercent =>
        UsciteABarriera > 0 ? Math.Round(100m * Stop / UsciteABarriera, 1) : 0m;

    /// <summary>La riga da mostrare sotto il conteggio: dice sempre cosa è stato tolto e perché.</summary>
    public string Scarti =>
        $"{RigheTotali} righe in tabella − {RepliqueScartate} repliche − {ReplayScartati} da replay "
        + $"= {RigheTotali - RepliqueScartate - ReplayScartati} vive"
        + (SenzaOrologio > 0
            ? $", di cui {SenzaOrologio} senza ora di parete: restano fuori dal giudizio perché non si può "
              + $"sapere se siano avvenute o le abbia fabbricate un riavvio "
              + $"({StopNonDatabili} stop e {TakeNonDatabili} target che NON entrano nella percentuale)"
            : string.Empty);
}

/// <summary>La diagnosi di una corsia.</summary>
public sealed record DiagnosiCorsia(
    int LaneId,
    string Symbol,
    string Timeframe,
    bool IsRunning,
    VerdettoBracket Verdetto,
    string Racconto,
    BracketRace? Nominale,
    UsciteOsservate? Osservato,
    IReadOnlyList<BracketRace> Alternative)
{
    public static DiagnosiCorsia NonMisurabile(int laneId, string motivo) =>
        new(laneId, string.Empty, string.Empty, false, VerdettoBracket.NonMisurabile, motivo, null, null, []);

    /// <summary>
    /// La conclusione sulla scansione delle geometrie. Se il valore atteso di ogni alternativa sta
    /// dentro una banda stretta quanto il costo di andata e ritorno, spostare il bracket non è la
    /// leva — e dirlo è più utile che suggerire una geometria «migliore» che migliore non è.
    /// </summary>
    public string? Leva
    {
        get
        {
            var utili = Alternative.Where(a => a.Misurabile).ToList();
            if (utili.Count < 2 || Nominale is null) return null;

            var migliore = utili.MaxBy(a => a.ExpectedValuePercent)!;
            var peggiore = utili.MinBy(a => a.ExpectedValuePercent)!;
            var banda = migliore.ExpectedValuePercent - peggiore.ExpectedValuePercent;
            var costo = Nominale.RoundTripCostPercent;

            return banda <= costo
                ? $"Fra il rapporto {peggiore.RewardToRisk:0.##} e il {migliore.RewardToRisk:0.##} il valore atteso per "
                  + $"operazione varia di {banda:0.###} punti, meno del costo di andata e ritorno ({costo:0.##}). "
                  + "La geometria del bracket cambia la FORMA dell'esito, non la media: spostarla non è la leva del profitto."
                : $"Il rapporto {migliore.RewardToRisk:0.##} rende {migliore.ExpectedValuePercent:0.###}% per operazione contro "
                  + $"{Nominale.ExpectedValuePercent:0.###}% dell'attuale ({Nominale.RewardToRisk:0.##}), su entrate senza segnale. "
                  + "Da verificare con un forward test prima di toccare qualcosa: qui le entrate sono prese a ogni barra.";
        }
    }
}
