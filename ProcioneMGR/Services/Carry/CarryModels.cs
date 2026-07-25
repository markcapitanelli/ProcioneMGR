using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Carry;

/// <summary>
/// [E3 roadmap profitto-intraday] Configurazione del carry delta-neutro (long spot + short perp
/// sullo stesso simbolo). L'edge è il FUNDING incassato dallo short quando è positivo — un flusso,
/// non una previsione. Delta-neutro: la componente direzionale del prezzo si elide fra le due gambe.
/// </summary>
public sealed class CarryConfiguration
{
    /// <summary>Capitale iniziale (unità di conto, es. USDT).</summary>
    public decimal InitialCapital { get; set; } = 10_000m;

    /// <summary>% del capitale impegnata come nozionale PER GAMBA (le due gambe hanno lo stesso nozionale).</summary>
    public decimal PositionSizePercent { get; set; } = 50m;

    /// <summary>Si ENTRA quando il funding annualizzato medio (finestra <see cref="TrailingFundingEvents"/>) supera questa soglia (%).</summary>
    public decimal EnterAnnualFundingPercent { get; set; } = 5m;

    /// <summary>Si ESCE quando il funding annualizzato medio scende sotto questa soglia (%). Deve essere &lt; enter (isteresi).</summary>
    public decimal ExitAnnualFundingPercent { get; set; }

    /// <summary>Eventi di funding su cui mediare per la decisione (8h l'uno: 9 ≈ 3 giorni). Smussa gli spike singoli.</summary>
    public int TrailingFundingEvents { get; set; } = 9;

    /// <summary>Eventi di funding al giorno dell'exchange (Binance/Bitget: 3, ogni 8h).</summary>
    public int FundingEventsPerDay { get; set; } = 3;

    /// <summary>Commissione per lato della gamba SPOT (% del nozionale).</summary>
    public decimal SpotFeePercent { get; set; } = 0.1m;

    /// <summary>Commissione per lato della gamba PERP (% del nozionale, tipicamente &lt; spot).</summary>
    public decimal PerpFeePercent { get; set; } = 0.05m;

    /// <summary>Slippage sfavorevole per gamba (%), in entrata e in uscita.</summary>
    public decimal SlippagePercent { get; set; } = 0.03m;
}

/// <summary>Un episodio di carry: quando aperto/chiuso, funding incassato, costi, netto.</summary>
public sealed record CarryEpisode(
    DateTime OpenedUtc, DateTime ClosedUtc, int FundingEventsHeld,
    decimal FundingCollectedPercent, decimal CostPercent, decimal NetPercent);

/// <summary>
/// Esito del backtest carry. I "percent" sono sul nozionale di UNA gamba (il capitale impegnato per
/// episodio); il netto totale è sul capitale iniziale.
/// </summary>
public sealed class CarryBacktestResult
{
    public decimal FinalCapital { get; set; }
    public decimal TotalReturnPercent { get; set; }
    public decimal GrossFundingPercent { get; set; }
    public decimal TotalCostPercent { get; set; }
    public int Episodes { get; set; }
    public int FundingEventsInPosition { get; set; }
    public int FundingEventsTotal { get; set; }
    public decimal TimeInPositionFraction { get; set; }

    /// <summary>Rendimento netto annualizzato sul periodo INTERO (capitale sempre allocato).</summary>
    public decimal NetAnnualizedPercent { get; set; }
    public List<CarryEpisode> EpisodeList { get; set; } = [];
    public List<EquityPoint> EquityCurve { get; set; } = [];
}
