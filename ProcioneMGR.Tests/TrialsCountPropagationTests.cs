using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Pipeline.Stages;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D-01, Fase 1 PRD-RISANAMENTO] Il gate DSR deve usare le combinazioni REALMENTE provate, non i
/// soli sopravvissuti al Top-N. Prima nello stesso run convivevano tre conteggi che non si
/// parlavano: PowerCheckStage ne assumeva 300, StrategyDiscoveryEngine misurava il numero vero
/// (solo per la UI), e il gate usava validated.Count ≤ topN = 15 — con 3.000 combinazioni la
/// soglia SR* applicata era la metà di quella dovuta (1,77σ contro 3,56σ,
/// docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §2).
/// </summary>
public sealed class TrialsCountPropagationTests
{
    /// <summary>Candidati con rendimenti holdout DECORRELATI (rumore indipendente per seed) e uno
    /// Sharpe di selezione moderato: il collasso dei correlati resta fuori gioco e si isola
    /// l'effetto del solo N.</summary>
    private static (List<ValidatedCandidate> Validated, List<double[]> Returns) Batch(int count, int length = 120)
    {
        var validated = new List<ValidatedCandidate>();
        var returns = new List<double[]>();
        for (var i = 0; i < count; i++)
        {
            var rng = new Random(1000 + i * 97);
            // Deriva positiva moderata + rumore: uno Sharpe "carino ma non schiacciante", il caso
            // in cui il conteggio dei tentativi decide davvero il verdetto.
            var r = Enumerable.Range(0, length).Select(_ => 0.0012 + (rng.NextDouble() - 0.5) * 0.02).ToArray();
            returns.Add(r);
            validated.Add(new ValidatedCandidate
            {
                StrategyName = $"S{i}",
                Symbol = "BTC/USDT",
                Timeframe = "1h",
                SelectionSharpe = 1.0m + i * 0.05m,
                HoldoutSharpe = 1.0m,
                Survived = true,
            });
        }
        return (validated, returns);
    }

    [Fact]
    public void MoreExploredTrials_LowerDeflatedSharpe()
    {
        // Stesso batch osservato, due dichiarazioni di esplorazione: 0 (ignota = storico) e 3.000.
        var (v1, r1) = Batch(10);
        OverfittingGate.Apply(v1, r1, minDeflatedSharpe: 0.0, maxPbo: 1.0, trialCorrelationThreshold: 1.0);
        var dsrSmallN = v1.Where(v => v.DeflatedSharpe.HasValue).Select(v => v.DeflatedSharpe!.Value).Max();

        var (v2, r2) = Batch(10);
        OverfittingGate.Apply(v2, r2, minDeflatedSharpe: 0.0, maxPbo: 1.0, trialCorrelationThreshold: 1.0,
            trialsExplored: 3_000);
        var dsrBigN = v2.Where(v => v.DeflatedSharpe.HasValue).Select(v => v.DeflatedSharpe!.Value).Max();

        // Con 3.000 prove dichiarate la soglia SR* sale e il DSR deve SCENDERE: assolvere di meno
        // è il comportamento corretto, non una regressione.
        Assert.True(dsrBigN < dsrSmallN,
            $"atteso DSR più severo con N=3000 (trovato {dsrBigN:F4}) che con N=candidati (trovato {dsrSmallN:F4})");
    }

    [Fact]
    public void UnknownExploration_PreservesHistoricalBehaviour()
    {
        // trialsExplored = 0 (default): il gate deve produrre ESATTAMENTE il verdetto di prima,
        // così i chiamanti non aggiornati (tool CLI) non cambiano numeri in silenzio.
        var (v1, r1) = Batch(8);
        OverfittingGate.Apply(v1, r1, minDeflatedSharpe: 0.0, maxPbo: 1.0, trialCorrelationThreshold: 1.0);

        var (v2, r2) = Batch(8);
        OverfittingGate.Apply(v2, r2, minDeflatedSharpe: 0.0, maxPbo: 1.0, trialCorrelationThreshold: 1.0,
            trialsExplored: 0);

        for (var i = 0; i < v1.Count; i++)
        {
            Assert.Equal(v1[i].DeflatedSharpe, v2[i].DeflatedSharpe);
            Assert.Equal(v1[i].Survived, v2[i].Survived);
        }
    }

    [Fact]
    public void ExploredFewerThanCandidates_NeverLowersN()
    {
        // Un conteggio esplorato incoerente (minore dei candidati osservati) non deve mai RIDURRE
        // la severità: max(candidati, esplorate) fa da pavimento.
        var (v1, r1) = Batch(10);
        OverfittingGate.Apply(v1, r1, minDeflatedSharpe: 0.0, maxPbo: 1.0, trialCorrelationThreshold: 1.0,
            trialsExplored: 3);
        var withFloor = v1.Where(v => v.DeflatedSharpe.HasValue).Select(v => v.DeflatedSharpe!.Value).Max();

        var (v2, r2) = Batch(10);
        OverfittingGate.Apply(v2, r2, minDeflatedSharpe: 0.0, maxPbo: 1.0, trialCorrelationThreshold: 1.0);
        var historical = v2.Where(v => v.DeflatedSharpe.HasValue).Select(v => v.DeflatedSharpe!.Value).Max();

        Assert.Equal(historical, withFloor);
    }

    [Fact]
    public void PipelineContext_AccumulatesAcrossStages()
    {
        // Il contratto del contesto: ogni stage che cerca SOMMA (discovery + creative + …).
        var ctx = new PipelineContext();
        Assert.Equal(0, ctx.TrialsExplored);
        ctx.TrialsExplored += 1_200;   // discovery
        ctx.TrialsExplored += 480;     // creative (spec × serie)
        Assert.Equal(1_680, ctx.TrialsExplored);
    }
}
