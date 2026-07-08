using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Execution;

/// <summary>
/// Simula il riempimento di un <see cref="ExecutionPlan"/> sulle candele fini, con impatto di
/// mercato e spread, invece di assumere un fill istantaneo a chiusura candela (assunzione odierna
/// del <c>BacktestEngine</c>). Serve a MISURARE la differenza fra algoritmi (Immediate vs TWAP/VWAP/
/// Iceberg) sugli stessi dati — la premessa del 10-20% di miglioramento va misurata qui, non
/// assunta (rif. <c>docs/ROADMAP-QLIB.md §1.2</c>). Puro/deterministico.
/// </summary>
public interface IExecutionSimulator
{
    ExecutionResult Simulate(
        ExecutionPlan plan,
        ExecutionIntent intent,
        IReadOnlyList<OhlcvData> fineCandles,
        ExecutionParameters parameters);
}

public sealed class ExecutionSimulator : IExecutionSimulator
{
    public ExecutionResult Simulate(
        ExecutionPlan plan,
        ExecutionIntent intent,
        IReadOnlyList<OhlcvData> fineCandles,
        ExecutionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(intent);
        fineCandles ??= [];
        parameters ??= new ExecutionParameters();

        var sign = intent.Side == ExecutionSide.Buy ? 1m : -1m;
        var n = fineCandles.Count;

        decimal filledQty = 0m, notional = 0m;
        var fills = new List<ExecutionFill>(plan.Slices.Count);

        foreach (var s in plan.Slices)
        {
            if (s.Quantity <= 0m) continue;

            decimal reference, volume;
            if (n == 0)
            {
                reference = intent.ArrivalPrice;
                volume = 0m;
            }
            else
            {
                var c = fineCandles[Math.Clamp(s.CandleIndex, 0, n - 1)];
                reference = (c.High + c.Low + c.Close) / 3m;    // prezzo tipico della candela
                if (reference <= 0m) reference = c.Close > 0m ? c.Close : intent.ArrivalPrice;
                volume = Math.Max(0m, c.Volume);
            }

            // Impatto lineare nella partecipazione (quota del volume di candela), con tetto.
            var participation = volume > 0m ? s.Quantity / volume : 1m;
            var impact = Math.Min(parameters.ImpactCoefficient * participation, parameters.MaxImpactPct);
            var costPct = impact + parameters.HalfSpreadPct;

            // Il buy paga di più, il sell incassa di meno: il costo va SEMPRE contro l'ordine.
            var price = reference * (1m + sign * costPct);

            filledQty += s.Quantity;
            notional += price * s.Quantity;
            fills.Add(new ExecutionFill(s.CandleIndex, s.Quantity, price, participation * 100m));
        }

        var avg = filledQty > 0m ? notional / filledQty : intent.ArrivalPrice;
        var arrival = intent.ArrivalPrice > 0m ? intent.ArrivalPrice : avg;
        var slippageBps = arrival > 0m ? sign * (avg - arrival) / arrival * 10_000m : 0m;

        return new ExecutionResult
        {
            Algorithm = plan.Algorithm,
            FilledQuantity = filledQty,
            AverageFillPrice = avg,
            ArrivalPrice = arrival,
            SlippageBps = slippageBps,
            Fills = fills,
        };
    }
}
