using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Tests;

/// <summary>
/// I difetti trovati dalla <b>revisione avversaria</b> del 2026-08-19 sul codice delle Fasi 3-5,
/// ognuno inchiodato dal test che lo avrebbe visto. Sono cinque lenti indipendenti che hanno cercato
/// difetti, e per ogni ritrovamento tre scettici incaricati di demolirlo: qui sotto restano quelli
/// sopravvissuti alla confutazione.
///
/// <para>Il filo comune è quello di tutta l'ondata: <b>una superficie che dice una cosa
/// rassicurante quando la realtà è diversa</b>. Tre di questi difetti li avevo introdotti io negli
/// item che esistono proprio per eliminare quella classe.</para>
/// </summary>
public class RevisioneFase5Tests
{
    // --- 1. Il numeratore e il denominatore devono venire dalla stessa fotografia ---------------

    /// <summary>
    /// <b>Il difetto peggiore della revisione.</b> Il ritiro per inedia conta i trade dal MOTORE e
    /// somma il ritmo atteso dalla CONFIGURAZIONE — e I13(a), scritto lo stesso giorno, stabilisce
    /// che le due possono divergere: il motore fotografa le gambe attive all'<i>avvio</i>, quindi
    /// una gamba aggiunta e salvata senza riavviare la corsia gonfia l'atteso senza produrre un solo
    /// trade.
    ///
    /// <para>Bastava aggiungere una gamba da 30 trade/mese a una corsia sana per farle emettere
    /// «Corsia in INEDIA» al tick dopo — e con la corsia in <c>Fleet:ExecutionLanes</c> e il dry-run
    /// spento, per fermarla davvero dopo la conferma dell'isteresi.</para>
    /// </summary>
    [Fact]
    public void ConfigurazioneEMotoreCheDivergono_SonoUnaDivergenza()
        => Assert.True(FleetStateReader.Diverge(["a", "b"], ["a"]));

    /// <summary>
    /// <b>Ma il silenzio non è divergenza.</b> Un motore che non risponde, o con un'immagine
    /// precedente al campo del contratto che porta le gambe in esecuzione, dà una lista VUOTA.
    /// Trattarla come divergenza avrebbe spento il ritiro per inedia su ogni motore non aggiornato,
    /// in silenzio: la contromisura sarebbe diventata un secondo difetto.
    /// </summary>
    [Theory]
    [InlineData(new[] { "a" }, new string[0])]
    [InlineData(new string[0], new[] { "a" })]
    public void ListaVuota_NonEUnaDivergenza(string[] configurate, string[] inEsecuzione)
        => Assert.False(FleetStateReader.Diverge(configurate, inEsecuzione));

    [Fact]
    public void ListaSconosciuta_NonEUnaDivergenza()
    {
        Assert.False(FleetStateReader.Diverge(null, ["a"]));
        Assert.False(FleetStateReader.Diverge(["a"], null));
    }

    /// <summary>Lo stesso insieme in ordine diverso NON è una divergenza: si confrontano insiemi, non sequenze.</summary>
    [Fact]
    public void StessoInsiemeInOrdineDiverso_NonEUnaDivergenza()
        => Assert.False(FleetStateReader.Diverge(["b", "a"], ["a", "b"]));

    // --- 2. Una sola definizione di «mese», ai due lati della stessa disuguaglianza -------------

    /// <summary>
    /// L'atteso nasce dividendo i giorni di holdout per 30,44; il tempo trascorso con cui
    /// quell'atteso veniva riproporzionato divideva per 30,0. Stessa unità, due aritmetiche, ai
    /// due lati della <i>stessa</i> disuguaglianza — e lo scarto cresceva dell'1,5% per ogni mese di
    /// osservazione, spostando il confine dell'inedia contro la corsia.
    ///
    /// <para>Il caso costruito apposta: 20 trade su un holdout di esattamente due mesi ⇒ 10/mese.
    /// Osservazione di esattamente un mese ⇒ attesi 10, soglia al 20% = 2. Con due trade la corsia è
    /// <b>al limite</b>, quindi NON affamata. Con le due aritmetiche diverse la soglia diventava
    /// 2,029 e due trade la condannavano.</para>
    /// </summary>
    [Fact]
    public void UnaSolaAritmeticaDelMese_AiDueLatiDelConfronto()
    {
        var ranges = new PipelineDateRanges
        {
            SelectionFrom = new DateTime(2026, 1, 1), SelectionTo = new DateTime(2026, 5, 1),
            HoldoutFrom = new DateTime(2026, 5, 1),
            HoldoutTo = new DateTime(2026, 5, 1).AddDays((double)(2m * TradeFrequency.DaysPerMonth)),
        };
        var perMese = TradeFrequency.PerMonth(20, ranges.HoldoutMonths()!.Value);
        Assert.Equal(10m, perMese!.Value, 2);

        var unMese = TimeSpan.FromDays((double)TradeFrequency.DaysPerMonth);
        Assert.False(TradeFrequency.IsStarving(perMese, observed: 2, unMese, 0.2m, TimeSpan.FromDays(7)),
            "due trade su dieci attesi sono esattamente il 20%: al limite, non sotto");
        Assert.True(TradeFrequency.IsStarving(perMese, observed: 1, unMese, 0.2m, TimeSpan.FromDays(7)));
    }

    // --- 3. «Non te lo so dire» non è «non sto eseguendo nulla» --------------------------------

    /// <summary>
    /// In proto3 un campo <c>repeated</c> assente si deserializza <b>vuoto</b>, mai null: un motore
    /// con un'immagine precedente al campo delle gambe in esecuzione risponde con una lista vuota
    /// <i>mentre esegue</i>. Leggerla come fatto produceva la bugia peggiore dei due versi — nessun
    /// avviso sulle gambe spente ma ancora operate, <b>e in più</b> l'affermazione falsa che tutte
    /// le gambe attive «non sono eseguite».
    ///
    /// <para>Qui si fissa la regola pura: con una corsia in corsa e nessuna gamba nota, il confronto
    /// non si fa in NESSUNO dei due versi.</para>
    /// </summary>
    [Fact]
    public void MotoreInCorsaCheNonNominaGambe_NessunConfrontoInNessunVerso()
    {
        var cfg = new EnsembleConfiguration
        {
            Symbol = "ADA/USDT", Timeframe = "4h",
            Strategies =
            [
                new EnsembleStrategy { StrategyId = "a", IsActive = true },
                new EnsembleStrategy { StrategyId = "b", IsActive = false },
            ],
        };

        // null = "non determinabile": è ciò che il servizio scrive quando la corsia gira e la lista
        // arriva vuota. Nessuna accusa, in nessuna direzione.
        Assert.Empty(EnsemblePageService.LegsStillRunningWhileDisabled(cfg, null, true));
        Assert.Empty(EnsemblePageService.LegsEnabledButNotRunning(cfg, null, true));
    }

    // --- 4. Il corpus notizie: il giudice condiviso resta condiviso -----------------------------

    /// <summary>
    /// [I15-rev] Il predicato che la purge usa per RISPARMIARE e quello che il guardiano usa per
    /// CONTARE devono restare lo stesso. Se divergessero, il guardiano misurerebbe la profondità di
    /// un insieme diverso da quello protetto e direbbe «tutto a posto» di righe che il worker sta
    /// cancellando — una protezione che non si vede fallire.
    ///
    /// <para>Nota di realtà, misurata sul database vero il 2026-08-19: <b>22.777 notizie, 22.777
    /// con punteggio, zero grezze</b>. L'esenzione «mirata» è di fatto totale, e
    /// <c>NewsRetentionDays</c> non limita più quella tabella — il commento che diceva il contrario
    /// era vero nel codice e falso nei fatti.</para>
    /// </summary>
    [Fact]
    public void IlGiudiceDelCorpusEUnoSolo_ELeDueEspressioniRestanoComplementari()
    {
        var scored = NewsCorpus.Scored.Compile();
        var notScored = NewsCorpus.NotScored.Compile();

        foreach (decimal? s in new decimal?[] { null, 0m, 0.5m, -1m })
        {
            var punto = new AltDataPoint
            {
                TimestampUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                Source = "F", Title = "t", DedupeKey = "k", SentimentScore = s,
            };
            Assert.True(scored(punto) ^ notScored(punto));
        }
    }

    // --- 5. Le manopole nuove non passano più senza controllo ----------------------------------

    /// <summary>
    /// [I12-rev] La frazione di inedia è l'unica manopola della sezione che può fare danno restando
    /// «valida» per il binder: sopra 1 condanna <b>ogni</b> corsia, compresa quella che opera
    /// esattamente quanto promesso — e col braccio armato le ferma sul serio, una per tick.
    /// Zero resta ammesso ed è il modo dichiarato di spegnere il criterio.
    /// </summary>
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.2, true)]
    [InlineData(1.0, true)]
    [InlineData(1.5, false)]
    [InlineData(-0.1, false)]
    public void FrazioneDiInedia_FuoriBanda_ERifiutata(double frazione, bool valida)
    {
        var opt = new FleetOptions { StarvationFraction = (decimal)frazione };

        Assert.Equal(valida, ProcioneMGR.Services.Config.AdminConfigRules.Validate(opt) is null);
    }

    /// <summary>Le altre tre manopole nuove: osservazione minima, azioni per tick, corsie autorizzate.</summary>
    [Fact]
    public void LeAltreManopoleNuove_SonoValidate()
    {
        Assert.NotNull(ProcioneMGR.Services.Config.AdminConfigRules.Validate(new FleetOptions { StarvationMinDays = 0 }));
        Assert.NotNull(ProcioneMGR.Services.Config.AdminConfigRules.Validate(new FleetOptions { MaxExecutionsPerTick = 0 }));
        Assert.NotNull(ProcioneMGR.Services.Config.AdminConfigRules.Validate(new FleetOptions { ExecutionLanes = [-1] }));
        Assert.Null(ProcioneMGR.Services.Config.AdminConfigRules.Validate(new FleetOptions { ExecutionLanes = [0, 5] }));
    }

    // --- 6. La diagnosi non promette un ritiro che il dry-run impedisce ------------------------

    /// <summary>
    /// [I12-rev] «Il prossimo tick le ritira e libera il posto» era una promessa fatta da una
    /// funzione pura che non conosce né <c>DryRun</c> né <c>ExecutionLanes</c>. In dry-run — cioè nel
    /// default della piattaforma — l'operatore avrebbe aspettato un ritiro che non sarebbe mai
    /// arrivato. Ora la frase dice ciò che è vero in ogni assetto (il verdetto c'è) e rimanda a dove
    /// si legge se verrà eseguito.
    /// </summary>
    [Fact]
    public void LaDiagnosiNonPromettePiuUnRitiroCheIlDryRunImpedisce()
    {
        var state = new FleetState
        {
            Lanes =
            [
                new FleetLaneState(3, true, "Paper", true, false, false, false, 0.5m, 0, TimeSpan.FromDays(20), "ADA/USDT", "4h", 30m),
            ],
            Candidates =
            [
                new(Guid.NewGuid(), DateTime.UtcNow.AddDays(-2), "pass", 10m, "1h", "c1", false),
                new(Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), "pass", 10m, "1h", "c2", false),
            ],
            FootprintLanes = 3, ExposureGuardEnabled = true, NowUtc = DateTime.UtcNow,
        };

        var silence = FleetOrchestrator.Explain(state, new FleetOptions { StarvationFraction = 0.2m, StarvationMinDays = 10 });

        Assert.Equal(1, silence.StarvingLanes);
        Assert.Contains("INEDIA", silence.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("il prossimo tick le ritira", silence.Reason, StringComparison.Ordinal);
        Assert.Contains("DryRun", silence.Reason, StringComparison.Ordinal);
    }
}
