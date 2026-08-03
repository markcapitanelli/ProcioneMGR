using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Bracket SL/TP data-driven per (symbol, timeframe), estratto da PipelineApplier perché ora ha
/// DUE consumatori (l'applica della raccomandazione e lo schieramento dei candidati grigi) e la
/// regola della piattaforma è una sola implementazione, nessuna deriva.
///
/// Primario (R1.5): MAE/MFE sull'orizzonte di detenzione condizionato al regime di volatilità
/// corrente (<see cref="ExcursionAnalyzer.SuggestAdaptiveBracket"/>), media dei bracket long/short
/// per un livello simmetrico. Fallback: escursioni a barra singola (95° percentile) quando il
/// campionamento sull'orizzonte è troppo rado. (0,0) se i dati non bastano: chi schiera decide se
/// procedere senza protezioni o fermarsi.
/// </summary>
public static class AutoBracket
{
    public static async Task<(decimal Sl, decimal Tp)> ComputeAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ExcursionAnalyzer excursion,
        string symbol,
        string timeframe,
        CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var candles = await db.OhlcvData
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(5000)
                .ToListAsync(ct);
            if (candles.Count < 100) return (0m, 0m);
            candles.Reverse(); // cronologico per l'analisi

            static decimal Avg(decimal a, decimal b)
            {
                var v = new[] { a, b }.Where(x => x > 0m).ToList();
                return v.Count > 0 ? Math.Round(v.Average(), 2) : 0m;
            }

            var longB = excursion.SuggestAdaptiveBracket(candles, OrderSide.Buy);
            var shortB = excursion.SuggestAdaptiveBracket(candles, OrderSide.Sell);
            var sl = Avg(longB.StopLossPercent, shortB.StopLossPercent);
            var tp = Avg(longB.TakeProfitPercent, shortB.TakeProfitPercent);
            if (sl > 0m || tp > 0m) return (sl, tp);

            var slBar = excursion.SuggestStopLoss(candles);
            var tpBar = excursion.SuggestTakeProfit(candles);
            return (Avg(slBar.LongStopPercentile95, slBar.ShortStopPercentile95),
                    Avg(tpBar.LongTakeProfitPercentile95, tpBar.ShortTakeProfitPercentile95));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return (0m, 0m); }
    }
}
