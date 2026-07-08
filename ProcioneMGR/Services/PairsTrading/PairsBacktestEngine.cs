using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// Implementazione di <see cref="IPairsBacktestEngine"/>. Pipeline:
///  1. allinea le due serie di candele per timestamp;
///  2. calcola hedge ratio/spread/z-score in modo rolling e anti-look-ahead
///     (<see cref="RollingPairsSpreadAnalyzer"/>);
///  3. itera candela per candela: |z| oltre <c>EntryZScore</c> apre lo spread (dollar-neutral,
///     stesso notional sulle due gambe), |z| sotto <c>ExitZScore</c> chiude;
///  4. aggiorna l'equity curve mark-to-market ad ogni barra.
/// Deterministico, nessuna dipendenza da IStrategy/IBacktestEngine.
/// </summary>
public sealed class PairsBacktestEngine : IPairsBacktestEngine
{
    public PairsBacktestResult RunBacktest(IReadOnlyList<OhlcvData> candlesY, IReadOnlyList<OhlcvData> candlesX, PairsBacktestConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(candlesY);
        ArgumentNullException.ThrowIfNull(candlesX);
        ArgumentNullException.ThrowIfNull(config);

        var (alignedY, alignedX) = PairsCandleAligner.Align(candlesY, candlesX);
        var n = alignedY.Count;
        if (n == 0)
        {
            return new PairsBacktestResult { FinalCapital = config.InitialCapital, TotalReturnPercent = 0m };
        }

        var closesY = alignedY.Select(c => c.Close).ToList();
        var closesX = alignedX.Select(c => c.Close).ToList();

        var analyzer = new RollingPairsSpreadAnalyzer();
        var analysis = analyzer.Analyze(closesY, closesX, config.LookbackWindow, config.RecalibrationInterval, config.ZScoreLookback);

        var book = new PairsPortfolio(config.InitialCapital, config.FeePercent, config.PositionSizePercent);
        var equity = new List<EquityPoint>(n);
        var trades = new List<PairsTrade>();
        var side = PairsPositionSide.Flat;
        PairsTrade? openTrade = null;

        for (var i = 0; i < n; i++)
        {
            var ts = DateTime.SpecifyKind(alignedY[i].TimestampUtc, DateTimeKind.Utc);
            var priceY = closesY[i];
            var priceX = closesX[i];
            var z = analysis.ZScore[i];

            if (z is not null)
            {
                if (side == PairsPositionSide.Flat)
                {
                    if ((decimal)z.Value > config.EntryZScore)
                    {
                        side = PairsPositionSide.ShortSpread;
                        book.Open(side, priceY, priceX);
                        openTrade = new PairsTrade
                        {
                            EntryTime = ts,
                            Side = side,
                            EntryPriceY = priceY,
                            EntryPriceX = priceX,
                            HedgeRatioAtEntry = (decimal)(analysis.HedgeRatio[i] ?? 0),
                        };
                    }
                    else if ((decimal)z.Value < -config.EntryZScore)
                    {
                        side = PairsPositionSide.LongSpread;
                        book.Open(side, priceY, priceX);
                        openTrade = new PairsTrade
                        {
                            EntryTime = ts,
                            Side = side,
                            EntryPriceY = priceY,
                            EntryPriceX = priceX,
                            HedgeRatioAtEntry = (decimal)(analysis.HedgeRatio[i] ?? 0),
                        };
                    }
                }
                else if (Math.Abs((decimal)z.Value) < config.ExitZScore && openTrade is not null)
                {
                    CloseTrade(book, openTrade, priceY, priceX, ts, trades);
                    openTrade = null;
                    side = PairsPositionSide.Flat;
                }
            }

            equity.Add(new EquityPoint { Timestamp = ts, Capital = book.Equity(priceY, priceX) });
        }

        // Chiude l'eventuale posizione residua sull'ultima barra.
        if (side != PairsPositionSide.Flat && openTrade is not null)
        {
            var lastTs = equity[^1].Timestamp;
            CloseTrade(book, openTrade, closesY[^1], closesX[^1], lastTs, trades);
            equity[^1] = new EquityPoint { Timestamp = lastTs, Capital = book.Cash };
        }

        return BuildResult(config, book, equity, trades, n);
    }

    private static void CloseTrade(PairsPortfolio book, PairsTrade trade, decimal priceY, decimal priceX, DateTime ts, List<PairsTrade> trades)
    {
        var netPnl = book.Close(priceY, priceX);
        trade.ExitTime = ts;
        trade.ExitPriceY = priceY;
        trade.ExitPriceX = priceX;
        trade.Pnl = netPnl;
        trade.PnlPercent = book.LastTradeNotionalPerLeg > 0m ? netPnl / (book.LastTradeNotionalPerLeg * 2m) * 100m : 0m;
        trades.Add(trade);
    }

    private static PairsBacktestResult BuildResult(PairsBacktestConfiguration config, PairsPortfolio book, List<EquityPoint> equity, List<PairsTrade> trades, int candlesEvaluated)
    {
        var winning = trades.Count(t => t.Pnl > 0m);
        var losing = trades.Count(t => t.Pnl < 0m);
        var finalCapital = book.Cash;

        return new PairsBacktestResult
        {
            FinalCapital = finalCapital,
            TotalReturnPercent = config.InitialCapital == 0m ? 0m : (finalCapital - config.InitialCapital) / config.InitialCapital * 100m,
            TotalTrades = trades.Count,
            WinningTrades = winning,
            LosingTrades = losing,
            WinRate = trades.Count == 0 ? 0m : (decimal)winning / trades.Count * 100m,
            MaxDrawdownPercent = Statistics.MaxDrawdownPercent(equity),
            CandlesEvaluated = candlesEvaluated,
            Trades = trades,
            EquityCurve = equity,
        };
    }

    /// <summary>
    /// Contabilità a due gambe, dollar-neutral (stesso notional su Y e X all'apertura).
    /// LongSpread: Long Y, Short X. ShortSpread: Short Y, Long X.
    /// </summary>
    private sealed class PairsPortfolio(decimal initialCapital, decimal feePercent, decimal sizePercent)
    {
        private readonly decimal _feeFrac = feePercent / 100m;
        private readonly decimal _sizeFrac = sizePercent / 100m;

        private PairsPositionSide _side = PairsPositionSide.Flat;
        private decimal _qtyY;
        private decimal _qtyX;
        private decimal _entryY;
        private decimal _entryX;

        public decimal Cash { get; private set; } = initialCapital;
        public decimal LastTradeNotionalPerLeg { get; private set; }

        public void Open(PairsPositionSide side, decimal priceY, decimal priceX)
        {
            var notionalPerLeg = Cash * _sizeFrac;
            LastTradeNotionalPerLeg = notionalPerLeg;
            _qtyY = priceY > 0m ? notionalPerLeg / priceY : 0m;
            _qtyX = priceX > 0m ? notionalPerLeg / priceX : 0m;
            _entryY = priceY;
            _entryX = priceX;
            _side = side;

            var fee = notionalPerLeg * _feeFrac * 2m; // due gambe aperte
            Cash -= fee;
        }

        /// <summary>Chiude la posizione e restituisce il PnL NETTO (già dedotte le commissioni di uscita).</summary>
        public decimal Close(decimal priceY, decimal priceX)
        {
            var pnl = UnrealizedPnl(priceY, priceX);
            var exitNotional = _qtyY * priceY + _qtyX * priceX;
            var fee = exitNotional * _feeFrac;
            var net = pnl - fee;

            Cash += net;
            _side = PairsPositionSide.Flat;
            return net;
        }

        private decimal UnrealizedPnl(decimal priceY, decimal priceX)
        {
            if (_side == PairsPositionSide.Flat) return 0m;
            var sign = _side == PairsPositionSide.LongSpread ? 1m : -1m;
            var pnlY = sign * _qtyY * (priceY - _entryY);
            var pnlX = -sign * _qtyX * (priceX - _entryX);
            return pnlY + pnlX;
        }

        public decimal Equity(decimal priceY, decimal priceX) => Cash + UnrealizedPnl(priceY, priceX);
    }
}
