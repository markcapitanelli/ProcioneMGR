using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>
/// [AF2] Costruisce il <see cref="FleetState"/> in SOLA lettura: corsie (directory + quarantene +
/// possesso campagne + stato vivo dei motori) e coda candidati (run completati + verdetti di
/// validazione). Difensivo per corsia e per run: un guasto su una corsia la rende INTOCCABILE
/// (mai "libera per errore"), un run illeggibile esce dalla coda con un log — l'orchestratore
/// deve poter ragionare su ciò che sa, non inciampare su ciò che non sa.
/// </summary>
public interface IFleetStateReader
{
    Task<FleetState> ReadAsync(CancellationToken ct = default);
}

public sealed class FleetStateReader(
    ILaneDirectory laneDirectory,
    ILaneQuarantineStore quarantineStore,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    IPipelineApplier applier,
    IOptionsMonitor<Risk.CorrelatedExposureOptions> exposureOptions,
    IOptionsMonitor<FleetOptions> fleetOptions,
    // [J8] L'orologio cumulato dell'osservazione: vive QUI, nell'unico punto che assembla lo stato
    // delle corsie, così Decide e la sua spiegazione (Explain, il pannello) leggono lo STESSO
    // numero — due orologi darebbero un pannello che spiega un ritiro diverso da quello vero.
    ILaneObservationLedger observationLedger,
    ILogger<FleetStateReader> logger) : IFleetStateReader
{
    // Soglia F5 (GreyDsrFloor): trasferita in GreyZone.DsrFloor insieme al giudice — vedi IsGrey.

    public async Task<FleetState> ReadAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // --- Corsie -----------------------------------------------------------------------------
        var summaries = await laneDirectory.ListAsync(ct);
        var quarantined = (await quarantineStore.GetAllAsync(ct)).Select(q => q.LaneId).ToHashSet();

        int campaignPrefix;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Il possesso delle campagne è un PREFISSO 0..ObservedLanes-1 (non una lista): si
            // prende il massimo fra le campagne abilitate — conservativo con più campagne.
            campaignPrefix = await db.VettingCampaigns.AsNoTracking()
                .Where(c => c.Enabled)
                .Select(c => (int?)c.ObservedLanes)
                .MaxAsync(ct) ?? 0;
        }

        var lanes = new List<FleetLaneState>(summaries.Count);
        foreach (var s in summaries)
        {
            var quarantinedLane = quarantined.Contains(s.Id);
            var campaignOwned = s.Id < campaignPrefix;

            var running = s.IsRunning;
            var mode = s.Mode;
            var emergency = false;
            var unreadable = false;
            var sharpe = 0m;
            var trades = 0;
            var observation = TimeSpan.Zero;
            var expected = s.ExpectedTradesPerMonth;

            // Lo stato vivo serve solo alle corsie di flotta potenzialmente toccabili; per le
            // altre bastano directory e vincoli (meno chiamate, meno superfici di guasto).
            if (s.Id >= applier.LaneCount && !quarantinedLane && !campaignOwned)
            {
                try
                {
                    var engine = serviceProvider.GetRequiredKeyedService<ITradingEngine>(s.Id);
                    var status = await engine.GetStatusAsync(ct);
                    running = status.IsRunning;
                    mode = status.Mode.ToString();
                    emergency = status.IsEmergencyStopped;

                    // [J8] L'osservazione viene dal REGISTRO CUMULATO, non da now − StartedAtUtc:
                    // quella finestra riparte da zero a ogni riavvio del motore, e con
                    // RetireMinWeeks=3 il massimo continuo mai raggiunto in tutta la vita della
                    // flotta è stato 20g 3h contro i 21 richiesti — il criterio di ritiro non ha
                    // mai potuto esprimersi. Trade e Sharpe si ancorano allo stesso primo
                    // avvistamento dell'identità: numeratore e denominatore dalla stessa storia.
                    var identity = LaneObservationLedger.BuildIdentity(s.Symbol, s.Timeframe, s.ActiveStrategyIds);
                    var (observed, firstSeen) = await observationLedger.AccumulateAsync(s.Id, identity, running, now, ct);
                    observation = observed;
                    if (running)
                    {
                        var perf = await engine.GetPerformanceAsync(from: firstSeen, ct);
                        sharpe = perf.SharpeRatio;
                        trades = perf.TotalTrades;
                    }

                    // [I12-rev] IL NUMERATORE E IL DENOMINATORE DEVONO VENIRE DALLA STESSA
                    // FOTOGRAFIA. I trade li conta il MOTORE; il ritmo atteso lo somma la
                    // CONFIGURAZIONE — e I13(a), scritto nello stesso giorno, stabilisce che le due
                    // possono divergere: il motore fotografa le gambe attive all'AVVIO, quindi una
                    // gamba aggiunta e salvata senza riavviare la corsia gonfia l'atteso senza
                    // produrre un solo trade.
                    //
                    // Senza questo controllo bastava aggiungere una gamba da 30 trade/mese a una
                    // corsia sana per farle emettere «Corsia in INEDIA» al tick successivo — e col
                    // braccio armato, per fermarla davvero. Un verdetto costruito su due verita'
                    // diverse dello stesso oggetto e' peggio di nessun verdetto.
                    //
                    // Quando divergono non si ricalcola: si RINUNCIA. L'ignoranza non condanna.
                    if (running && expected is not null && Diverge(s.ActiveStrategyIds, status.RunningStrategyIds))
                    {
                        expected = null;
                        logger.LogInformation(
                            "Corsia {Lane}: configurazione e motore non concordano sulle gambe attive (riavvio in sospeso) — "
                            + "il ritmo atteso non e' confrontabile e il ritiro per inedia non si esprime.", s.Id);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Corsia illeggibile = corsia INTOCCABILE, mai "libera per errore".
                    logger.LogWarning(ex, "Stato corsia {Lane} non leggibile: la marco intoccabile per questo tick.", s.Id);
                    emergency = true;
                    // [K40] ...e SEPARATAMENTE illeggibile, perché «fermata per emergenza» e «non
                    // risponde» hanno rimedi opposti e finora producevano la stessa frase.
                    unreadable = true;
                }
            }

            lanes.Add(new FleetLaneState(
                s.Id, running, mode, s.IsConfigured, quarantinedLane, campaignOwned, emergency,
                sharpe, trades, observation, s.Symbol, s.Timeframe,
                // [I12] Il ritmo atteso arriva dalla directory, che gia' deserializza la config
                // della corsia: una seconda lettura qui sarebbe una seconda regola su cosa conta
                // come "gamba attiva". [I12-rev] ...e vale null se il motore sta eseguendo altro.
                expected,
                // [J14] Provenienza delle gambe, per il tetto MaxGreyLanes (stessa fonte: la directory).
                GreySourced: s.HasGreyLegs,
                // [K40] «Non risponde» separato da «fermata per emergenza»: rimedi opposti.
                Unreadable: unreadable));
        }

        // --- Candidati --------------------------------------------------------------------------
        var candidates = await ReadCandidatesAsync(now, ct);

        return new FleetState
        {
            Lanes = lanes,
            Candidates = candidates,
            FootprintLanes = applier.LaneCount,
            ExposureGuardEnabled = exposureOptions.CurrentValue.Enabled,
            NowUtc = now,
        };
    }

    private async Task<IReadOnlyList<FleetCandidate>> ReadCandidatesAsync(DateTime now, CancellationToken ct)
    {
        var opt = fleetOptions.CurrentValue;
        var minCompleted = now.AddDays(-Math.Max(1, opt.CandidateMaxAgeDays));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // [J4] I run a universo MISTO non producono candidati di flotta: PBO e DSR di quei run
        // erano calcolati su un pannello che mescola due ppy (per questo dal 2026-08-20 lo stage
        // li rifiuta), quindi né la banda «pass» né la grigia sono verdetti confrontabili. Si
        // esclude DICHIARANDO il conteggio: uno scarto silenzioso si legge come «non c'era nulla».
        var mixedExcluded = await db.PipelineRuns.AsNoTracking()
            .CountAsync(r => r.Status == "Completed" && r.CompletedAt >= minCompleted
                && r.MixedTimeframeUniverse
                && !string.IsNullOrEmpty(r.RecommendationJson) && r.RecommendationJson != "{}", ct);
        if (mixedExcluded > 0)
        {
            logger.LogInformation(
                "Coda candidati: esclusi {N} run a universo misto (verdetti non confrontabili, J4).", mixedExcluded);
        }
        var runs = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.CompletedAt >= minCompleted)
            .Where(r => !r.MixedTimeframeUniverse)
            .Where(r => !string.IsNullOrEmpty(r.RecommendationJson) && r.RecommendationJson != "{}")
            .Select(r => new { r.Id, r.CompletedAt, r.ConfigurationId, r.RecommendationJson })
            .ToListAsync(ct);
        if (runs.Count == 0) return [];

        var runIds = runs.Select(r => r.Id).ToList();

        // Verdetti di validazione per run (artifact "ValidatedCandidates").
        var validatedByRun = (await db.PipelineArtifacts.AsNoTracking()
                .Where(a => a.Kind == "ValidatedCandidates" && runIds.Contains(a.RunId))
                .Select(a => new { a.RunId, a.PayloadJson })
                .ToListAsync(ct))
            .ToDictionary(a => a.RunId, a => a.PayloadJson);

        // Run già gestiti: dall'auto-reapply (artifact) o da questo stesso journal.
        var handledByReapply = (await db.PipelineArtifacts.AsNoTracking()
                .Where(a => a.Kind == AutoReapplyArtifactKinds.Decision && runIds.Contains(a.RunId))
                .Select(a => a.RunId)
                .ToListAsync(ct))
            .ToHashSet();
        // [K14 2026-08-31] DUE insiemi, non uno. Prima "Assign" e "ProposeGrey" finivano insieme in
        // «gia' gestito», e con l'ereditarieta' per identita' una NOTIFICA a un umano toglieva il
        // candidato al braccio automatico — per sempre, dentro la finestra dei 30 giorni.
        var assignedByFleet = (await db.OrchestratorDecisions.AsNoTracking()
                .Where(d => d.RunId != null && runIds.Contains(d.RunId.Value) && d.Kind == "Assign")
                .Select(d => d.RunId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var proposedByFleet = (await db.OrchestratorDecisions.AsNoTracking()
                .Where(d => d.RunId != null && runIds.Contains(d.RunId.Value) && d.Kind == "ProposeGrey")
                .Select(d => d.RunId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        // Le finestre date per derivare i trade/mese (la config le porta come JSON).
        var configIds = runs.Select(r => r.ConfigurationId).Distinct().ToList();
        var rangesByConfig = (await db.PipelineConfigurations.AsNoTracking()
                .Where(c => configIds.Contains(c.Id))
                .Select(c => new { c.Id, c.DateRangesJson })
                .ToListAsync(ct))
            .ToDictionary(c => c.Id, c => c.DateRangesJson);

        var list = new List<FleetCandidate>();
        foreach (var run in runs)
        {
            try
            {
                var recommendation = RunApplyEvaluator.DeserializeRecommendation(run.RecommendationJson);
                if (recommendation is null) continue;

                List<ValidatedCandidate> validated = [];
                if (validatedByRun.TryGetValue(run.Id, out var payload))
                {
                    try { validated = JsonSerializer.Deserialize<List<ValidatedCandidate>>(payload) ?? []; }
                    catch (JsonException) { /* verdetti illeggibili: si classifica con quello che c'è */ }
                }

                var verdict = Evaluate(recommendation, validated, rangesByConfig.GetValueOrDefault(run.ConfigurationId));
                if (verdict is not { } v) continue;

                list.Add(new FleetCandidate(
                    run.Id, run.CompletedAt ?? now, v.Band, v.TradesPerMonth, v.Timeframe, v.Summary,
                    AlreadyHandled: handledByReapply.Contains(run.Id) || assignedByFleet.Contains(run.Id),
                    AlreadyProposed: proposedByFleet.Contains(run.Id),
                    Identity: v.Identity));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Run {Run} escluso dalla coda candidati (dati illeggibili).", run.Id);
            }
        }

        // [revisione algoritmi 2026-08-20] IL DEDUP DEI GRIGI VALE ANCHE FRA UN TICK E L'ALTRO.
        //
        // La prima versione (I12) accorpava le proposte con la stessa identita' DENTRO un tick, e
        // bastava a non mandare quaranta notifiche insieme. Ma «gia' gestito» era per RUN, non per
        // identita': la caccia rigira gli stessi parametri sugli stessi mercati, quindi il giorno
        // dopo un run NUOVO con la STESSA coppia strategia/serie/parametri tornava a proporsi come
        // se fosse la prima volta. E' precisamente il meccanismo che aveva prodotto le 91 proposte
        // per sei cose distinte misurate il 2026-08-19: il dedup dentro il tick non lo tocca.
        //
        // Qui l'identita' eredita lo stato: se ANCHE UNO dei run che la portano e' gia' stato
        // gestito, lo sono tutti. La finestra e' quella dei candidati (CandidateMaxAgeDays), quindi
        // oltre quella un'identita' puo' ripresentarsi — ed e' giusto: dopo un mese e' una proposta
        // nuova, non la stessa che ritorna.
        // [K14] L'eredita' vale per ENTRAMBI gli stati, ma SEPARATAMENTE: «schierato» si eredita
        // perche' non si schiera due volte la stessa cosa; «proposto» si eredita perche' non si
        // notifica due volte la stessa cosa. Fonderli era il difetto.
        var identitaSchierate = list
            .Where(c => c.AlreadyHandled && !string.IsNullOrEmpty(c.Identity))
            .Select(c => c.Identity!)
            .ToHashSet(StringComparer.Ordinal);
        var identitaProposte = list
            .Where(c => c.AlreadyProposed && !string.IsNullOrEmpty(c.Identity))
            .Select(c => c.Identity!)
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (string.IsNullOrEmpty(c.Identity)) continue;
            var schierata = c.AlreadyHandled || identitaSchierate.Contains(c.Identity);
            var proposta = c.AlreadyProposed || identitaProposte.Contains(c.Identity);
            if (schierata != c.AlreadyHandled || proposta != c.AlreadyProposed)
            {
                list[i] = c with { AlreadyHandled = schierata, AlreadyProposed = proposta };
            }
        }

        return list;
    }

    internal readonly record struct CandidateVerdict(
        string Band, decimal TradesPerMonth, string Timeframe, string Summary, string? Identity = null);

    /// <summary>
    /// Il verdetto di un run come candidato di flotta.
    /// "pass" = almeno un sopravvissuto E gambe schierabili: frequenza dalla gamba più RADA (min).
    /// "grey" (F5) = zero sopravvissuti ma bocciature per SOLA finestra corta — classe ContoTrade
    /// ("Solo N trade in holdout") o DSR in [0.80, 0.95) — CON Sharpe holdout positivo: un grigio
    /// che perde non è grigio, è bocciato nel merito. La frequenza del grigio viene dal SUO
    /// HoldoutTrades, non dalle gambe della raccomandazione: con zero sopravvissuti le gambe non
    /// esistono, ed è proprio il caso per cui la fascia grigia esiste (primo journal vuoto del
    /// 2026-08-03: i grigi non entravano MAI in coda — il lettore chiedeva la frequenza a una
    /// lista vuota).
    /// null = non candidato (bocciato nel merito, o finestra/frequenza non derivabili).
    /// </summary>
    internal static CandidateVerdict? Evaluate(
        PipelineRecommendation recommendation, List<ValidatedCandidate> validated, string? dateRangesJson)
    {
        if (HoldoutMonths(dateRangesJson) is not decimal months) return null;

        var survivors = validated.Count(v => v.Survived);
        if (survivors > 0 && recommendation.EnsembleLegs.Count > 0)
        {
            var minTrades = recommendation.EnsembleLegs.Min(l => l.HoldoutTrades);
            // [I11] La frequenza attesa passa dal denominatore CONDIVISO: lo stesso numero che
            // leggeranno il ritiro per inedia e il freno per gamba. Calcolarlo qui a mano darebbe
            // due regole per la stessa domanda.
            return new CandidateVerdict("pass",
                TradeFrequency.PerMonth(minTrades, months) ?? 0m,
                recommendation.EnsembleLegs[0].Timeframe,
                $"{recommendation.BestCandidate} ({survivors} sopravvissuti su {recommendation.CandidatesEvaluated})",
                // [J13] L'identità del candidato SOLO quando la raccomandazione è a gamba singola:
                // è il caso che il braccio di assegnazione sa eseguire (una corsia = un simbolo).
                // Un ensemble multi-gamba resta senza chiave, e il worker lo dichiara ineseguibile.
                Identity: recommendation.EnsembleLegs.Count == 1 ? recommendation.EnsembleLegs[0].Key : null);
        }

        var grey = validated
            .Where(IsGrey)
            .OrderByDescending(candidate => candidate.HoldoutSharpe)
            .ToList();
        if (grey.Count == 0) return null;

        var best = grey[0];
        return new CandidateVerdict("grey",
            TradeFrequency.PerMonth(best.HoldoutTrades, months) ?? 0m,
            best.Timeframe,
            // [Difetto C, 2026-08-22] La proposta nomina la CHIAVE, come il menù e il journal —
            // `Identity: best.Key` era già due righe sotto. Lasciare la terna qui significava avere
            // metà catena che parla per identità e metà per terna, cioè la doppia verità che
            // GreyZone dichiara di voler evitare: due Composite XLM/USDT 4h dello stesso run
            // producevano la stessa identica riga di proposta.
            $"{best.Key}: Sharpe holdout {best.HoldoutSharpe:F2} su {best.HoldoutTrades} trade"
            + (grey.Count > 1 ? $" (+{grey.Count - 1} altri in fascia grigia)" : ""),
            // [I12] L'identità del grigio PROPOSTO (non del run): due run che ritrovano gli stessi
            // parametri sulla stessa serie sono una proposta sola, e vanno mostrati come tale.
            Identity: best.Key);
    }

    /// <summary>
    /// Il filtro della fascia grigia. La DEFINIZIONE vive in <see cref="GreyZone.IsGrey"/> —
    /// promossa lì il 2026-08-14 quando i consumatori sono diventati tre (questo lettore, il
    /// GreyDeployer e l'archivio candidati/assemblaggio ensemble); questo alias resta per i
    /// chiamanti interni della flotta. Mai duplicare la soglia qui.
    /// </summary>
    internal static bool IsGrey(ValidatedCandidate candidate) => GreyZone.IsGrey(candidate);

    /// <summary>
    /// Mesi della finestra holdout della config. Null se non derivabile (e allora il run non è un
    /// candidato: senza finestra la frequenza è un'illusione). La durata mediana delle posizioni
    /// invece NON esiste a livello di run (trade list non persistita): la misura il forward test.
    /// </summary>
    /// <summary>
    /// [I12-rev] Le gambe che la configurazione dichiara attive sono le stesse che il motore sta
    /// eseguendo?
    ///
    /// <para>Vero (divergono) solo quando entrambe le liste sono NOTE e diverse. Un motore che non
    /// risponde, o precedente al campo del contratto che porta le gambe in esecuzione, restituisce
    /// una lista VUOTA: non si sa nulla, e non sapere non e' un motivo per rinunciare al criterio —
    /// e' lo stato in cui il criterio si comportava gia' come prima. Rinunciare anche li' avrebbe
    /// spento il ritiro per inedia su ogni motore non aggiornato, silenziosamente.</para>
    /// </summary>
    internal static bool Diverge(IReadOnlyList<string>? configurate, IReadOnlyList<string>? inEsecuzione)
    {
        if (configurate is null || inEsecuzione is null) return false;
        if (configurate.Count == 0 || inEsecuzione.Count == 0) return false;
        return !configurate.ToHashSet(StringComparer.Ordinal)
            .SetEquals(inEsecuzione);
    }

    /// <summary>
    /// [I11] Solo la LETTURA del JSON: il calcolo vive su <see cref="PipelineDateRanges.HoldoutMonths"/>,
    /// perché lo condividono in tre (questo lettore, lo schieramento manuale della fascia grigia e
    /// lo stage che compone la raccomandazione). Internal e non più private per la stessa ragione.
    /// </summary>
    internal static decimal? HoldoutMonths(string? dateRangesJson)
    {
        if (string.IsNullOrWhiteSpace(dateRangesJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<PipelineDateRanges>(dateRangesJson)?.HoldoutMonths();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
