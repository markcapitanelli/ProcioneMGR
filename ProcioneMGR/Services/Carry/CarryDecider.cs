namespace ProcioneMGR.Services.Carry;

/// <summary>Cosa fare al prossimo punto di decisione del carry.</summary>
public enum CarryAction
{
    /// <summary>Restare come si è (flat resta flat, in posizione resta in posizione).</summary>
    Hold,

    /// <summary>Aprire il carry: long spot + short perp allo stesso nozionale.</summary>
    Open,

    /// <summary>Chiudere entrambe le gambe.</summary>
    Close,
}

/// <summary>
/// [E3] La REGOLA DI DECISIONE del carry, pura e UNICA: la usano sia il
/// <see cref="CarryBacktestEngine"/> sia il motore live, così backtest e operatività non possono
/// divergere. Isteresi: si entra sopra la soglia di ingresso, si esce sotto quella di uscita (che
/// è più bassa), e fra le due non si fa nulla — niente ping-pong attorno a una singola soglia.
/// </summary>
public static class CarryDecider
{
    /// <param name="annualizedFundingPercent">Funding annualizzato medio recente (%), firmato.</param>
    /// <param name="inPosition">Se il carry è attualmente aperto.</param>
    public static CarryAction Decide(decimal annualizedFundingPercent, bool inPosition, CarryConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ExitAnnualFundingPercent >= config.EnterAnnualFundingPercent)
            throw new ArgumentException("La soglia di uscita deve essere < soglia di entrata (isteresi).", nameof(config));

        if (!inPosition && annualizedFundingPercent > config.EnterAnnualFundingPercent) return CarryAction.Open;
        if (inPosition && annualizedFundingPercent < config.ExitAnnualFundingPercent) return CarryAction.Close;
        return CarryAction.Hold;
    }

    /// <summary>
    /// Funding annualizzato dalla media degli ultimi <paramref name="trailing"/> rate (% per 8h).
    /// null se i punti non bastano (finestra non piena) → il chiamante non decide (Hold).
    /// </summary>
    public static decimal? TrailingAnnualized(IReadOnlyList<decimal> ratesPercentPer8h, int trailing, int eventsPerDay)
    {
        ArgumentNullException.ThrowIfNull(ratesPercentPer8h);
        var t = Math.Max(1, trailing);
        if (ratesPercentPer8h.Count < t) return null;
        var last = ratesPercentPer8h.Skip(ratesPercentPer8h.Count - t).Take(t);
        return last.Average() * eventsPerDay * 365m;
    }
}
