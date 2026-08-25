using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Services.Pipeline.Stages;

/// <summary>
/// [F4 PRD Valore] Check di potenza MinTRL (Bailey-López de Prado): PRIMA di spendere i backtest,
/// dichiara quale Sharpe annualizzato può superare i gate su QUESTA finestra con QUESTI tentativi.
///
/// <para>Perché esiste: su 4 mesi di holdout un candidato con Sharpe ~1 non può passare il DSR per
/// aritmetica — la piattaforma lo ha scoperto empiricamente («0 sopravvissuti» dopo ore di CPU, dieci
/// volte). Questo stage rende quel numero un OUTPUT del run, in testa: se nessun candidato plausibile
/// può farcela, lo dice subito, e con <c>enforce=true</c> blocca il run con la spiegazione invece di
/// lasciarlo bruciare calcolo. Le soglie dei gate NON vengono toccate: il check informa
/// (e al limite ferma), mai ammorbidisce.</para>
///
/// <para>Deterministico e puro: nessun accesso a dati, solo l'aritmetica su finestre e conteggi già
/// noti al contesto. Il numero di tentativi è un parametro dichiarato (default conservativo), perché
/// il conteggio VERO dipende dagli stage a valle: la stima prudente sta al proprietario del run.</para>
/// </summary>
public sealed class PowerCheckStage : IPipelineStage
{
    public string Name => "PowerCheck";
    public string DisplayName => "Check di potenza (MinTRL)";
    public string Description => "Dichiara PRIMA dei backtest lo Sharpe minimo che può superare i gate su questa finestra, dati i tentativi previsti.";
    public int DefaultOrder => 2; // subito dopo l'ingestione: serve solo la geometria delle finestre

    public IReadOnlyList<StageDependency> Dependencies => [StageDependency.On("DataIngestion")];

    public IReadOnlyList<StageParameterDefinition> ParameterDefinitions =>
    [
        new("expectedTrials", "Tentativi previsti", "300",
            "quante combinazioni verranno provate nel run (discovery × parametri): determina fin dove arriva il puro caso (E[max] del DSR). Sottostimarlo gonfia la potenza dichiarata"),
        new("confidence", "Confidenza", "0.95", "confidenza del MinTRL (z della normale)"),
        new("maxPlausibleSharpe", "Sharpe plausibile massimo", "2.0",
            "tetto annualizzato considerato raggiungibile da una strategia reale: se il minimo rilevabile lo supera, il run è dichiarato sotto potenza"),
        new("enforce", "Blocca se sotto potenza", "false",
            "true = il run si ferma QUI con la spiegazione invece di girare a vuoto; false = solo dichiarazione nel log e nel riepilogo (default, additivo)"),
    ];

    public string? ValidateInput(PipelineContext ctx) =>
        ctx.Ranges.HoldoutTo <= ctx.Ranges.HoldoutFrom
            ? "Finestra di holdout vuota o invertita: il check di potenza non ha nulla da misurare."
            : null;

    public Task ExecuteAsync(PipelineContext ctx, StageConfig config, CancellationToken ct)
    {
        var trials = Math.Max(1, config.GetInt("expectedTrials", 300));
        var confidence = Math.Clamp((double)config.GetDecimal("confidence", 0.95m), 0.5, 0.9999);
        var maxPlausible = (double)config.GetDecimal("maxPlausibleSharpe", 2.0m);
        var enforce = config.GetBool("enforce", false);

        var holdoutDays = (ctx.Ranges.HoldoutTo - ctx.Ranges.HoldoutFrom).TotalDays;
        var output = new PowerCheckOutput { TrialsAssumed = trials, Confidence = confidence, MaxPlausibleSharpe = maxPlausible };

        // [J17, 2026-08-25] Il calcolo dipende SOLO da timeframe e finestra di holdout: farlo per
        // serie produceva 34 righe di log identiche che sembravano 34 misure indipendenti. Si
        // calcola una volta per timeframe e si dichiara a quante serie si applica.
        foreach (var group in ctx.Universe.GroupBy(s => s.Timeframe))
        {
            ct.ThrowIfCancellationRequested();
            var ppy = Statistics.PeriodsPerYear(group.Key);
            var observations = (int)Math.Floor(holdoutDays / 365.25 * ppy);

            // SR* = fin dove arriva il puro caso con N tentativi su T osservazioni: è il benchmark
            // che il DSR usa davvero, e battere lo zero non basta a nessuno.
            var nullBenchmark = MinTrackRecord.ExpectedMaxSharpeUnderNull(trials, observations);
            var minPerPeriod = MinTrackRecord.MinDetectableSharpe(observations, nullBenchmark, confidence);
            var minAnnualized = MinTrackRecord.PerPeriodToAnnualized(minPerPeriod, ppy);

            foreach (var series in group)
            {
                output.Series.Add(new PowerSeriesEntry
                {
                    Symbol = series.Symbol,
                    Timeframe = series.Timeframe,
                    HoldoutObservations = observations,
                    NullBenchmarkSharpe = nullBenchmark,
                    MinDetectableAnnualizedSharpe = minAnnualized,
                });
            }

            ctx.LogLine($"[{Name}] {group.Key} ({group.Count()} serie — il numero dipende solo dal timeframe): " +
                        $"holdout {observations} oss., con {trials} tentativi il caso arriva a " +
                        $"SR*={MinTrackRecord.PerPeriodToAnnualized(nullBenchmark, ppy):F2} ann. " +
                        $"⇒ per passare serve Sharpe ≥ {minAnnualized:F2} annualizzato.");
        }

        output.WorstMinDetectableAnnualizedSharpe = output.Series.Count > 0
            ? output.Series.Max(s => s.MinDetectableAnnualizedSharpe)
            : double.PositiveInfinity;
        output.Underpowered = output.Series.Count == 0
            || output.Series.All(s => s.MinDetectableAnnualizedSharpe > maxPlausible);
        ctx.Power = output;

        // [J17] Il caso MISTO era il difetto: giudizio con All (tutte sotto potenza) ma riepilogo
        // col Max (il peggiore) — su un universo 1h+4h usciva «Potenza OK: minimo rilevabile 8,91»,
        // una frase che si contraddice da sola. Ora il caso parziale è uno stato dichiarato.
        var deboli = output.UnderpoweredTimeframes();
        if (!output.Underpowered && deboli.Count > 0)
        {
            var forti = output.Series.Select(s => s.Timeframe).Distinct().Except(deboli).ToList();
            ctx.LogLine(
                $"[{Name}] POTENZA PARZIALE: {string.Join(", ", deboli)} sotto potenza (minimo rilevabile oltre il " +
                $"tetto plausibile {maxPlausible:F1}); {string.Join(", ", forti)} può produrre sopravvissuti. " +
                "I candidati dei timeframe deboli hanno l'esito «0 promossi» scritto nell'aritmetica.");
        }

        if (output.Underpowered)
        {
            var message =
                $"[{Name}] RUN SOTTO POTENZA: su ogni serie dell'universo, per superare i gate servirebbe uno Sharpe " +
                $"annualizzato oltre il tetto plausibile ({maxPlausible:F1}) — con questa finestra di holdout " +
                $"({holdoutDays:F0} giorni) e {trials} tentativi, l'esito «0 promossi» è scritto nell'aritmetica. " +
                "Rimedi: allungare l'holdout, ridurre i tentativi, o instradare i candidati al forward test Paper (F5).";
            ctx.LogLine(message);
            if (enforce)
            {
                throw new InvalidOperationException(message);
            }
        }

        return Task.CompletedTask;
    }

    public StageSummary Summarize(PipelineContext ctx)
    {
        var p = ctx.Power;
        // [J17] Tre stati, con lo STESSO metro del giudizio (UnderpoweredTimeframes legge il tetto
        // persistito nell'output): «Potenza OK» col numero del peggiore era una frase che su un
        // universo misto si contraddiceva da sola.
        var deboli = p?.UnderpoweredTimeframes() ?? [];
        return new StageSummary
        {
            StageName = Name,
            DisplayName = DisplayName,
            Text = p is null
                ? "Nessun esito."
                : p.Underpowered
                    ? $"SOTTO POTENZA: minimo rilevabile {p.WorstMinDetectableAnnualizedSharpe:F2} ann. (peggiore) con {p.TrialsAssumed} tentativi."
                    : deboli.Count > 0
                        ? $"POTENZA PARZIALE: {string.Join(", ", deboli)} sotto potenza (peggiore {p.WorstMinDetectableAnnualizedSharpe:F2} ann.); gli altri timeframe possono produrre sopravvissuti. {p.TrialsAssumed} tentativi."
                        : $"Potenza OK: minimo rilevabile {p.WorstMinDetectableAnnualizedSharpe:F2} ann. (peggiore) con {p.TrialsAssumed} tentativi.",
            Metrics = p is null ? new() : new Dictionary<string, decimal>
            {
                ["SharpeMinRilevabileAnn"] = (decimal)Math.Round(Math.Min(p.WorstMinDetectableAnnualizedSharpe, 999), 2),
                ["TentativiAssunti"] = p.TrialsAssumed,
                ["OssHoldoutMin"] = p.Series.Count > 0 ? p.Series.Min(s => s.HoldoutObservations) : 0,
            },
        };
    }
}
