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
    ILogger<FleetStateReader> logger,
    // [K61] La stabilità K57 dei candidati, per il braccio AUTOMATICO. Opzionale: senza di essa
    // nessun candidato risulta giudicabile e la sostituzione non parte — fail-closed.
    Research.IStabilitaReader? stabilitaReader = null) : IFleetStateReader
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
            decimal? sharpePerTrade = null;
            var trades = 0;
            var observation = TimeSpan.Zero;
            var expected = s.ExpectedTradesPerMonth;
            // [K61] I due dati che la sostituzione richiede: quando la corsia ha chiuso l'ultima
            // operazione, e se ha una posizione viva addosso.
            DateTime? lastTrade = null;
            var openPositions = 0;

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
                    // [K61] Dal motore, non dal database: è lo stesso numero che il SafetyChecker usa
                    // per MaxOpenPositions, e una sostituzione sopra una posizione viva la
                    // cancellerebbe senza scrivere alcun TradeRecord (danno K36).
                    openPositions = status.OpenPositionCount;

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
                        // [K44] Zero campioni = NON DISPONIBILE, non «Sharpe zero». Un motore con
                        // un'immagine precedente al campo risponde 0 su entrambi, e uno zero letto
                        // come verdetto contro una soglia a zero sarebbe una condanna emessa da
                        // un'assenza.
                        sharpePerTrade = perf.SharpePerTradeSamples >= 2 ? perf.SharpePerTrade : null;

                        // [K61] L'ultima operazione CHIUSA, dalla stessa lista già deduplicata e
                        // ripulita dal replay (K41) da cui esce TotalTrades. Un MAX(ClosedAtUtc)
                        // scritto a mano leggerebbe l'ultima riga di REPLAY come ultima operazione,
                        // e una corsia muta da settimane sembrerebbe attiva di ieri.
                        lastTrade = perf.Trades.Count > 0
                            ? perf.Trades.Max(t => t.ClosedAtUtc)
                            : null;
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
                Unreadable: unreadable,
                // [K44] Il numero su cui una soglia unica e' davvero unica.
                RealizedSharpePerTrade: sharpePerTrade,
                // [K61] I due dati della sostituzione: l'ultima operazione chiusa (dalla lista gia'
                // ripulita dal replay) e le posizioni vive, che la vietano.
                LastTradeUtc: lastTrade,
                OpenPositions: openPositions));
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

    /// <summary>
    /// [Revisione 2026-09-03/04] <b>Una riga «Assign» conta come «candidato gestito» solo se ha
    /// toccato (o sta toccando) una corsia</b>: Applied, Intended, Unknown con un errore o applicato.
    /// I Failed e i Refused a dry-run SPENTO contano per <see cref="FinestraRifiuti"/>: abbastanza per
    /// far avanzare la coda oltre un candidato che il braccio non può eseguire (ensemble multi-gamba,
    /// corsia non autorizzata), non abbastanza per perderlo per due settimane. In dry-run nulla
    /// brucia: il rifiuto è la modalità, non una proprietà del candidato. Puro: si prova senza DB.
    /// </summary>
    internal static readonly TimeSpan FinestraRifiuti = TimeSpan.FromHours(24);

    internal static bool ContaComeGestito(string outcome, bool dryRun, bool applied, string? error, DateTime atUtc, DateTime now)
        => outcome switch
        {
            DecisionOutcome.Applied => true,
            DecisionOutcome.Intended => true,
            DecisionOutcome.Unknown => applied || error is not null,
            DecisionOutcome.Failed => atUtc >= now - FinestraRifiuti,
            DecisionOutcome.Refused => !dryRun && atUtc >= now - FinestraRifiuti,
            _ => false,
        };

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
        // [Revisione 2026-09-03] Solo le assegnazioni che hanno TOCCATO (o stanno toccando) una
        // corsia contano come «gestito»: Applied, Intended, Unknown. Un «Assign» RIFIUTATO dal gate
        // (dry-run, corsia non autorizzata, budget del tick) prima finiva qui uguale, e bruciava il
        // candidato — e per ereditarietà di identità tutti i run della stessa chiave — per i 14
        // giorni di CandidateMaxAgeDays: in dry-run la coda si svuotava in poche ore e il no-op
        // diceva «tutti già schierati in passato». Un rifiuto per regola non è uno schieramento.
        // I Failed contano per 24 ore: abbastanza per non ritentare a ogni tick uno schieramento
        // che fallisce per una ragione stabile (bracket non derivabile, motore muto), non
        // abbastanza per perdere il candidato per due settimane.
        //
        // I Refused a dry-run SPENTO contano anch'essi per 24 ore — ed è ciò che fa AVANZARE la
        // coda: un candidato che il braccio non può eseguire per una ragione stabile (ensemble
        // multi-gamba, corsia non autorizzata) altrimenti resterebbe in testa alla FIFO e
        // occuperebbe l'unico slot del tick per quattordici giorni, senza che i candidati dietro di
        // lui vengano mai proposti. In DRY-RUN invece nulla brucia: il rifiuto è la modalità, non
        // una proprietà del candidato, e chi spegne il dry-run dopo giorni di osservazione deve
        // trovare la coda intera.
        //
        // Unknown conta solo se porta un errore (un intento chiuso dalla riconciliazione) o se è
        // applicato: una riga senza esito scritta da un binario che non conosce la colonna, nella
        // finestra fra migrazione e rilascio, non è uno schieramento.
        var assignedByFleet = (await db.OrchestratorDecisions.AsNoTracking()
                .Where(d => d.RunId != null && runIds.Contains(d.RunId.Value) && d.Kind == "Assign")
                .Select(d => new { RunId = d.RunId!.Value, d.Outcome, d.DryRun, d.Applied, d.Error, d.AtUtc })
                .ToListAsync(ct))
            .Where(d => ContaComeGestito(d.Outcome, d.DryRun, d.Applied, d.Error, d.AtUtc, now))
            .Select(d => d.RunId)
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

        // [K61, 2026-09-04] LA STABILITÀ ARRIVA FIN QUI, non si ferma alla lista che legge un umano.
        //
        // Finora K57 viveva solo in /admin/autonomy, accanto alla tendina del clic umano: il braccio
        // AUTOMATICO ordinava per data e non sapeva distinguere una mediana di 3,98 su ventaglio 0,21
        // da una di 2,79 su ventaglio 3,26. Due superfici, due criteri, la stessa domanda.
        //
        // Il lettore restituisce SOLO le chiavi giudicabili (>= StabilitaIpotesi.MinMisurePerGiudicare
        // rimisurazioni): l'assenza qui significa «non lo so», mai «va bene», e Fleet:ReplaceMinCandidateMeasures
        // può quindi rendere il cancello più severo, mai più largo. Senza il lettore (costruzioni di
        // test) tutti restano non giudicabili: fail-closed, la sostituzione non parte.
        if (stabilitaReader is not null)
        {
            var chiavi = list
                .Select(c => c.Identity)
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (chiavi.Count > 0)
            {
                try
                {
                    var stabilita = await stabilitaReader.ReadAsync(chiavi, ct);
                    for (var i = 0; i < list.Count; i++)
                    {
                        var c = list[i];
                        if (c.Identity is not null && stabilita.TryGetValue(c.Identity, out var s))
                        {
                            list[i] = c with
                            {
                                StabilityMedian = s.Mediana,
                                StabilityMeasures = s.Misure,
                                StabilitySpread = s.Ampiezza,
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Fail-open sulla DIAGNOSTICA, fail-closed sull'AZIONE: senza stabilità la coda
                    // resta leggibile e le proposte al clic umano continuano, ma nessun candidato è
                    // ammesso alla sostituzione (RimpiazziAmmessi pretende le misure).
                    logger.LogWarning(ex, "Stabilità K57 non leggibile: i candidati restano non giudicabili per la sostituzione.");
                }
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
