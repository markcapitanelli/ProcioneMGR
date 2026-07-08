using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Risk;

/// <summary>
/// Consulente per la leva: dati i trade di un backtest a leva 1, simula per bootstrap
/// (ricampionamento con reimmissione, stile Montecarlo evoluta) migliaia di sequenze di
/// trade a diversi livelli di leva e misura cio' che conta davvero per un capitale piccolo:
///
///  - crescita mediana del capitale (non la media, gonfiata dalle code fortunate);
///  - probabilita' di DIMEZZARE il capitale almeno una volta lungo il percorso;
///  - probabilita' di ROVINA (equity sotto una soglia di sopravvivenza);
///  - frequenza di liquidazioni (perdita del margine su un singolo trade).
///
/// La leva consigliata e' la piu' alta con P(dimezzamento) sotto la tolleranza richiesta:
/// la leva ottima per la crescita (Kelly) e' quasi sempre PIU' BASSA di quella che sembra
/// attraente — oltre, la crescita mediana CROLLA anche se la media sale.
/// </summary>
public sealed class LeverageAdvisor
{
    /// <param name="trades">Trade di un backtest eseguito a leva 1 (si usa PnlPercent = ritorno sul nozionale).</param>
    /// <param name="marginFraction">Frazione di capitale usata come margine per trade (es. 0.2 = 20%).</param>
    /// <param name="maintenanceMarginFraction">Margine di mantenimento come frazione del nozionale.</param>
    public LeverageAdvice Advise(
        IReadOnlyList<BacktestTrade> trades,
        decimal marginFraction = 0.2m,
        decimal maintenanceMarginFraction = 0.005m,
        decimal[]? leverageLevels = null,
        int simulations = 1000,
        decimal ruinThreshold = 0.3m,
        decimal halvingTolerance = 0.10m,
        int? seed = 42)
    {
        ArgumentNullException.ThrowIfNull(trades);
        if (marginFraction is <= 0m or > 1m) throw new ArgumentOutOfRangeException(nameof(marginFraction));

        leverageLevels ??= [1m, 2m, 3m, 5m, 10m, 20m];
        var returns = trades.Where(t => t.PnlPercent != 0m).Select(t => t.PnlPercent / 100m).ToArray();
        if (returns.Length < 20)
        {
            return new LeverageAdvice([], 1m, returns.Length,
                "Servono almeno 20 trade decisi per una stima affidabile.");
        }

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var horizon = Math.Min(returns.Length, 200); // trade per percorso simulato

        var rows = new List<LeverageScenario>(leverageLevels.Length);
        foreach (var lev in leverageLevels)
        {
            var terminals = new double[simulations];
            int halvings = 0, ruins = 0;
            long liquidations = 0, tradesRun = 0;

            for (var s = 0; s < simulations; s++)
            {
                double equityMult = 1.0;
                double peak = 1.0;
                var halved = false;
                var ruined = false;

                for (var t = 0; t < horizon; t++)
                {
                    var r = (double)returns[rng.Next(returns.Length)];
                    tradesRun++;

                    // Ritorno sul margine = leva * ritorno sul nozionale, con pavimento alla
                    // liquidazione: non si puo' perdere piu' del margine (meno il mantenimento).
                    var onMargin = (double)lev * r;
                    var floor = -(1.0 - (double)maintenanceMarginFraction * (double)lev);
                    if (onMargin <= floor)
                    {
                        onMargin = floor;
                        liquidations++;
                    }

                    equityMult *= 1.0 + (double)marginFraction * onMargin;
                    if (equityMult <= 0)
                    {
                        equityMult = 0;
                        ruined = true;
                        break;
                    }
                    if (equityMult > peak) peak = equityMult;
                    if (!halved && equityMult <= peak * 0.5) halved = true;
                    if (equityMult <= (double)ruinThreshold) ruined = true;
                }

                terminals[s] = equityMult;
                if (halved) halvings++;
                if (ruined) ruins++;
            }

            Array.Sort(terminals);
            rows.Add(new LeverageScenario(
                Leverage: lev,
                MedianGrowth: (decimal)terminals[simulations / 2],
                Percentile5Growth: (decimal)terminals[(int)(simulations * 0.05)],
                HalvingProbability: (decimal)halvings / simulations,
                RuinProbability: (decimal)ruins / simulations,
                LiquidationRate: tradesRun == 0 ? 0m : (decimal)liquidations / tradesRun,
                TradesPerPath: horizon));
        }

        // Consiglio: la leva piu' alta che resta sotto la tolleranza di dimezzamento E che
        // non peggiora la crescita mediana rispetto alle leve inferiori (oltre il picco di
        // Kelly la mediana scende: piu' leva = piu' rischio E meno crescita, mai accettabile).
        var acceptable = rows.Where(r => r.HalvingProbability <= halvingTolerance).ToList();
        var recommended = 1m;
        if (acceptable.Count > 0)
        {
            var bestMedian = acceptable.Max(r => r.MedianGrowth);
            recommended = acceptable.Last(r => r.MedianGrowth >= bestMedian * 0.95m).Leverage;
        }

        return new LeverageAdvice(rows, recommended, returns.Length, null);
    }
}

/// <summary>Esito della simulazione per un singolo livello di leva.</summary>
public sealed record LeverageScenario(
    decimal Leverage,
    /// <summary>Moltiplicatore mediano del capitale a fine percorso (1 = invariato).</summary>
    decimal MedianGrowth,
    /// <summary>5° percentile del moltiplicatore finale (scenario sfortunato realistico).</summary>
    decimal Percentile5Growth,
    /// <summary>Probabilita' di trovarsi almeno una volta a -50% dal massimo.</summary>
    decimal HalvingProbability,
    /// <summary>Probabilita' di scendere sotto la soglia di rovina.</summary>
    decimal RuinProbability,
    /// <summary>Quota di trade chiusi in liquidazione (perdita dell'intero margine).</summary>
    decimal LiquidationRate,
    int TradesPerPath);

/// <summary>Tabella degli scenari + leva consigliata.</summary>
public sealed record LeverageAdvice(
    IReadOnlyList<LeverageScenario> Scenarios,
    decimal RecommendedLeverage,
    int SampleTrades,
    string? Warning);
