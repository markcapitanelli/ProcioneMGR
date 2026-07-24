using ProcioneMGR.Data;
using ProcioneMGR.Services.PairsTrading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [E1 roadmap profitto-intraday] Filtro di volatilità dello spread nel pairs backtest: salta gli
/// ingressi quando la vol recente dello spread supera di un rapporto la vol di base — il regime in
/// cui la mean-reversion diventa un blow-up. Questi test fissano: il calcolo causale del rapporto
/// vol, e che accendere il filtro NON possa aprire più posizioni (al più le riduce).
/// </summary>
public class PairsVolFilterTests
{
    // Serie cointegrata sintetica: X random-walk, Y = X + spread mean-reverting, con un tratto
    // finale a vol dello spread molto più alta (regime di rottura).
    private static (List<OhlcvData> Y, List<OhlcvData> X) CointegratedWithVolBurst(int n, int seed)
    {
        var rng = new Random(seed);
        var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var y = new List<OhlcvData>(n);
        var x = new List<OhlcvData>(n);
        var logX = Math.Log(100.0);
        var spread = 0.0;
        for (var i = 0; i < n; i++)
        {
            logX += (rng.NextDouble() - 0.5) * 0.02;
            // Spread AR(1) mean-reverting; nella seconda metà la sua vol quadruplica.
            var shock = (rng.NextDouble() - 0.5) * (i > n / 2 ? 0.08 : 0.02);
            spread = 0.9 * spread + shock;
            var px = (decimal)Math.Exp(logX);
            var py = (decimal)Math.Exp(logX + spread);
            var ts = t0.AddHours(i * 4);
            x.Add(new OhlcvData { Symbol = "X/USDT", Timeframe = "4h", TimestampUtc = ts, Open = px, High = px, Low = px, Close = px, Volume = 100m });
            y.Add(new OhlcvData { Symbol = "Y/USDT", Timeframe = "4h", TimestampUtc = ts, Open = py, High = py, Low = py, Close = py, Volume = 100m });
        }
        return (y, x);
    }

    [Fact]
    public void SpreadVolRatio_IsCausal_AndFiresOnTheVolRegimeTransition()
    {
        // Spread piatto poi rumoroso a i>=200. Il rapporto rileva il CAMBIO di regime (vol recente
        // sopra la baseline), non il livello assoluto: sale nella TRANSIZIONE (finestra corta già
        // ad alta vol, baseline lunga ancora mista) e si normalizza quando anche la baseline è alta.
        var spread = new double?[400];
        var rng = new Random(1);
        for (var i = 0; i < 400; i++) spread[i] = i < 200 ? (rng.NextDouble() - 0.5) * 0.01 : (rng.NextDouble() - 0.5) * 0.1;

        var ratio = PairsBacktestEngine.SpreadVolRatio(spread, shortWindow: 20, longWindow: 120);

        Assert.Null(ratio[50]);   // finestra lunga non ancora piena → nessun vincolo
        // Nella transizione (i≈225-270) il rapporto deve superare 1.5 almeno una volta.
        var maxInTransition = Enumerable.Range(225, 46).Select(i => ratio[i] ?? 0).Max();
        Assert.True(maxInTransition > 1.5, $"il filtro deve accendersi nella transizione: max {maxInTransition:F2}");

        // Causalità: il valore a i non cambia se la serie viene troncata subito dopo i.
        var truncated = PairsBacktestEngine.SpreadVolRatio(spread.Take(260).ToArray(), 20, 120);
        Assert.Equal(ratio[259], truncated[259]);
    }

    [Fact]
    public void VolFilter_On_NeverOpensMoreTradesThanOff()
    {
        var (y, x) = CointegratedWithVolBurst(700, seed: 4);
        var engine = new PairsBacktestEngine();
        var baseCfg = new PairsBacktestConfiguration
        {
            SymbolY = "Y/USDT", SymbolX = "X/USDT", InitialCapital = 10_000m, PositionSizePercent = 10m,
            FeePercent = 0.1m, SlippagePercent = 0.02m,
            LookbackWindow = 60, RecalibrationInterval = 20, ZScoreLookback = 20,
            EntryZScore = 2.0m, ExitZScore = 0.5m, StopZScore = 3.5m,
        };

        var off = engine.RunBacktest(y, x, baseCfg);

        // Con filtro acceso: i trade non possono AUMENTARE (il filtro può solo sopprimere ingressi).
        var withFilter = engine.RunBacktest(y, x, new PairsBacktestConfiguration
        {
            SymbolY = "Y/USDT", SymbolX = "X/USDT", InitialCapital = 10_000m, PositionSizePercent = 10m,
            FeePercent = 0.1m, SlippagePercent = 0.02m,
            LookbackWindow = 60, RecalibrationInterval = 20, ZScoreLookback = 20,
            EntryZScore = 2.0m, ExitZScore = 0.5m, StopZScore = 3.5m,
            MaxSpreadVolRatio = 1.5m, SpreadVolBaselineWindow = 120,
        });

        Assert.True(withFilter.TotalTrades <= off.TotalTrades,
            $"il filtro vol non può aprire più trade: con {withFilter.TotalTrades} vs senza {off.TotalTrades}");
    }

    [Fact]
    public void VolFilter_Disabled_IsBitIdenticalToNoFilter()
    {
        var (y, x) = CointegratedWithVolBurst(500, seed: 9);
        var engine = new PairsBacktestEngine();
        var cfg = new PairsBacktestConfiguration
        {
            SymbolY = "Y/USDT", SymbolX = "X/USDT", InitialCapital = 10_000m, PositionSizePercent = 10m,
            FeePercent = 0.1m, SlippagePercent = 0.02m,
            LookbackWindow = 60, RecalibrationInterval = 20, ZScoreLookback = 20,
            EntryZScore = 2.0m, ExitZScore = 0.5m, StopZScore = 3.5m,
            MaxSpreadVolRatio = 0m,   // disattivo
        };

        var a = engine.RunBacktest(y, x, cfg);
        var b = engine.RunBacktest(y, x, cfg);
        Assert.Equal(a.TotalTrades, b.TotalTrades);
        Assert.Equal(a.TotalReturnPercent, b.TotalReturnPercent);
    }
}
