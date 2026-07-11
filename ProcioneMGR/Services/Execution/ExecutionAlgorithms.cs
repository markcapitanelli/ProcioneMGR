using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.Execution;

// =============================================================================================
//  Algoritmi di esecuzione (rif. docs/ROADMAP-QLIB.md §1.2). Ogni BuildPlan garantisce
//  Σ(fette) == TotalQuantity esatto e indici di candela validi. Puri/stateless.
// =============================================================================================

/// <summary>Helper condivisi per costruire piani con somma quantità esatta.</summary>
internal static class ExecutionPlanning
{
    /// <summary>
    /// Costruisce le fette da coppie (indice candela, quantità grezza) correggendo l'ultimo elemento
    /// così che la somma sia ESATTAMENTE <paramref name="total"/> (nessuna perdita per arrotondamento).
    /// Scarta le fette a quantità ≤ 0 tranne il caso limite in cui resterebbe vuoto.
    /// </summary>
    public static IReadOnlyList<ExecutionSlice> FinalizeSlices(List<(int Index, decimal Qty)> raw, decimal total)
    {
        var kept = raw.Where(r => r.Qty > 0m).ToList();
        if (kept.Count == 0) kept.Add((raw.Count > 0 ? raw[0].Index : 0, total));

        decimal sumExceptLast = 0m;
        for (var i = 0; i < kept.Count - 1; i++) sumExceptLast += kept[i].Qty;
        var last = kept[^1];
        kept[^1] = (last.Index, total - sumExceptLast); // assorbe il residuo di arrotondamento

        return kept.Select(k => new ExecutionSlice(k.Index, k.Qty)).ToList();
    }
}

/// <summary>
/// ESECUZIONE IMMEDIATA: un solo ordine per l'intera quantità, alla prima candela. Riproduce il
/// comportamento ODIERNO della piattaforma (nessuno slicing) ed è il default retrocompatibile.
/// </summary>
public sealed class ImmediateExecutionAlgorithm : IExecutionAlgorithm
{
    public string Name => "Immediate";

    public ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles, ExecutionParameters parameters)
        => new() { Algorithm = Name, Slices = [new ExecutionSlice(0, intent.TotalQuantity)] };
}

/// <summary>
/// TWAP (Time-Weighted Average Price): fette uguali distribuite uniformemente nel tempo lungo la
/// finestra di esecuzione. Riduce l'impatto di mercato di size grandi spargendo l'ordine.
/// </summary>
public sealed class TwapExecutionAlgorithm : IExecutionAlgorithm
{
    public string Name => "Twap";

    public ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles, ExecutionParameters parameters)
    {
        var n = fineGrainedCandles.Count;
        var total = intent.TotalQuantity;
        if (n <= 1) return new() { Algorithm = Name, Slices = [new ExecutionSlice(0, total)] };

        var k = Math.Clamp(parameters.MaxSlices, 1, n);
        var per = total / k;
        var raw = new List<(int, decimal)>(k);
        for (var i = 0; i < k; i++)
        {
            var idx = (int)((long)i * n / k); // spaziatura uniforme sulla finestra
            raw.Add((Math.Min(idx, n - 1), per));
        }
        return new() { Algorithm = Name, Slices = ExecutionPlanning.FinalizeSlices(raw, total) };
    }
}

/// <summary>
/// VWAP (Volume-Weighted Average Price): quantità proporzionale al profilo di volume delle candele
/// fini — concentra dove c'è più liquidità, minimizzando la partecipazione (e quindi l'impatto).
/// In backtest usa il volume realizzato della finestra; nel live si userebbe un profilo storico.
/// </summary>
public sealed class VwapExecutionAlgorithm : IExecutionAlgorithm
{
    public string Name => "Vwap";

    public ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles, ExecutionParameters parameters)
    {
        var n = fineGrainedCandles.Count;
        var total = intent.TotalQuantity;
        if (n <= 1) return new() { Algorithm = Name, Slices = [new ExecutionSlice(0, total)] };

        decimal totalVolume = 0m;
        for (var i = 0; i < n; i++) totalVolume += Math.Max(0m, fineGrainedCandles[i].Volume);

        // Volume tutto nullo: nessun profilo utile → ricade su una distribuzione uniforme (TWAP).
        if (totalVolume <= 0m)
            return new TwapExecutionAlgorithm().BuildPlan(intent, fineGrainedCandles, parameters);

        var raw = new List<(int, decimal)>(n);
        for (var i = 0; i < n; i++)
        {
            var vol = Math.Max(0m, fineGrainedCandles[i].Volume);
            if (vol <= 0m) continue;
            raw.Add((i, total * vol / totalVolume));
        }
        return new() { Algorithm = Name, Slices = ExecutionPlanning.FinalizeSlices(raw, total) };
    }
}

/// <summary>
/// ICEBERG: mostra solo un "clip" fisso per volta e lo rimpiazza finché la quantità totale è esaurita.
/// Nasconde la size reale distribuendola in ordini figli piccoli e sequenziali nel tempo.
/// </summary>
public sealed class IcebergExecutionAlgorithm : IExecutionAlgorithm
{
    public string Name => "Iceberg";

    public ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles, ExecutionParameters parameters)
    {
        var n = Math.Max(1, fineGrainedCandles.Count);
        var total = intent.TotalQuantity;

        var fraction = parameters.IcebergClipFraction is > 0m and <= 1m ? parameters.IcebergClipFraction : 0.1m;
        var clip = total * fraction;
        if (clip <= 0m) return new() { Algorithm = Name, Slices = [new ExecutionSlice(0, total)] };

        var raw = new List<(int, decimal)>();
        decimal remaining = total;
        var j = 0;
        while (remaining > 0m)
        {
            var qty = Math.Min(clip, remaining);
            raw.Add((Math.Min(j, n - 1), qty));
            remaining -= qty;
            j++;
            if (j > 100_000) break; // guardia difensiva (non dovrebbe accadere: clip>0)
        }
        return new() { Algorithm = Name, Slices = ExecutionPlanning.FinalizeSlices(raw, total) };
    }
}

/// <summary>
/// ADATTIVO (Almgren-Chriss semplificato, NON appreso — deliberatamente scartato un agente RL: si
/// sarebbe allenato contro il nostro stesso simulatore d'impatto illustrativo (√-partecipazione,
/// legge di Almgren, vedi ExecutionSimulator — era lineare prima di E1), imparando i suoi
/// artefatti invece della dinamica reale del mercato — rischio di "sim-to-real gap" e overfitting
/// documentato in letteratura per questo esatto ambito). Come VWAP ma pesa MOLTIPLICATIVAMENTE il
/// profilo di volume con un decadimento esponenziale nel tempo la cui intensità dipende dalla
/// volatilità realizzata del profilo: più alta la volatilità, più front-loaded l'esecuzione — si
/// riduce l'esposizione al RISCHIO di prezzo nel tempo (non il costo di impatto simulato, che nel
/// modello di fill dipende solo dalla √ della partecipazione al volume, mai da volatilità).
/// Degrada a VWAP quando la volatilità è nulla (mercato piatto: decayWeight uniforme) e a TWAP
/// quando il volume è nullo (stesso fallback esplicito di VWAP). Formula chiusa, deterministica,
/// verificabile per lettura — nessun training, nessun rischio di convergenza.
/// </summary>
public sealed class AdaptiveExecutionAlgorithm : IExecutionAlgorithm
{
    public string Name => "Adaptive";

    public ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList<OhlcvData> fineGrainedCandles, ExecutionParameters parameters)
    {
        var n = fineGrainedCandles.Count;
        var total = intent.TotalQuantity;
        if (n <= 1) return new() { Algorithm = Name, Slices = [new ExecutionSlice(0, total)] };

        decimal totalVolume = 0m;
        for (var i = 0; i < n; i++) totalVolume += Math.Max(0m, fineGrainedCandles[i].Volume);
        if (totalVolume <= 0m)
            return new TwapExecutionAlgorithm().BuildPlan(intent, fineGrainedCandles, parameters);

        var sigma = RealizedVolatility(fineGrainedCandles);
        var refVol = parameters.ReferenceVolatility > 0m ? parameters.ReferenceVolatility : 0.01m;
        var urgencyRatio = Math.Clamp(sigma / refVol, 0.25m, 4.0m);
        var decayLambda = parameters.DecayBaseRate * urgencyRatio;

        // Peso di decadimento (urgenza): sigma=0 -> urgencyRatio al floor -> pesi quasi uniformi.
        var decayWeights = new decimal[n];
        decimal decaySum = 0m;
        for (var i = 0; i < n; i++)
        {
            var exponent = Math.Clamp((double)(-decayLambda * i / n), -50.0, 50.0);
            decayWeights[i] = (decimal)Math.Exp(exponent);
            decaySum += decayWeights[i];
        }

        // Fusione MOLTIPLICATIVA col profilo di volume (a decay uniforme collassa esattamente su VWAP),
        // rinormalizzata a somma 1 (il prodotto di due distribuzioni non somma a 1 di per sé).
        var blended = new decimal[n];
        decimal blendedSum = 0m;
        for (var i = 0; i < n; i++)
        {
            var vol = Math.Max(0m, fineGrainedCandles[i].Volume);
            blended[i] = (decayWeights[i] / decaySum) * (vol / totalVolume);
            blendedSum += blended[i];
        }

        var raw = new List<(int, decimal)>(n);
        for (var i = 0; i < n; i++)
        {
            if (blended[i] <= 0m) continue;
            raw.Add((i, total * blended[i] / blendedSum));
        }
        return new() { Algorithm = Name, Slices = ExecutionPlanning.FinalizeSlices(raw, total) };
    }

    /// <summary>Deviazione standard dei rendimenti log di Close sull'intero profilo (scalare unico, non per-candela).</summary>
    private static decimal RealizedVolatility(IReadOnlyList<OhlcvData> candles)
    {
        var n = candles.Count;
        var logReturns = new List<decimal>(Math.Max(0, n - 1));
        for (var i = 1; i < n; i++)
        {
            var prev = candles[i - 1].Close;
            var cur = candles[i].Close;
            if (prev <= 0m || cur <= 0m) continue;
            logReturns.Add((decimal)Math.Log((double)(cur / prev)));
        }
        return logReturns.Count >= 2 ? FactorMath.StdDev(logReturns, 0, logReturns.Count - 1) : 0m;
    }
}
