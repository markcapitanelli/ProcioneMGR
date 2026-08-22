using ProcioneMGR.Data;
using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Direzione prevalente di un candidato, misurata sul TEMPO passato a mercato.</summary>
public enum DominantDirection
{
    /// <summary>Non determinabile: nessun trade, o trade tutti istantanei.</summary>
    Unknown = 0,
    Long,
    Short,
    /// <summary>Nessuno dei due lati domina oltre la soglia: il confronto passivo non ha un lato ovvio.</summary>
    Mixed,
}

/// <summary>Che cosa il passivo ha fatto sulla stessa finestra, e come si legge.</summary>
public sealed record PassiveComparison(
    DominantDirection Direction,
    decimal NetExposure,
    decimal? TimeInMarketFraction,
    decimal PassiveSharpe,
    decimal CandidateSharpe,
    decimal ExcessSharpe);

/// <summary>
/// [Difetto B, 2026-08-22] Il confronto col benchmark banale: «tieni la stessa direzione e non fare
/// niente». <b>Misura, non gate</b> — nessun consumatore cambia comportamento, e
/// <see cref="GreyZone"/> non viene toccata.
///
/// <para>Due scelte di metodo che valgono più del codice, entrambe misurate.</para>
///
/// <para><b>1. Il passivo NON paga il funding.</b> <c>BacktestEngine</c> lo applica a ogni candela
/// con posizione aperta, e lo applica <b>firmato</b>: con tasso positivo il long paga e lo short
/// <i>incassa</i>. Il tasso è la costante 0,01%/8h di <c>PipelineCosts</c> — non un dato:
/// <c>FundingHistory</c> non è popolata da nessuno, quindi è lo stesso numero per ogni simbolo e
/// ogni barra. Il passivo sta a mercato il 100% della finestra: lasciandogli quella costante,
/// l'asticella short si alzerebbe di ~0,21 Sharpe di reddito inventato che un candidato selettivo
/// non può guadagnare, e il confronto finirebbe per chiedere «non hai tenuto la carry 24 ore su 24»
/// invece di «non hai battuto il beta».
/// <br/><b>Residuo dichiarato</b>: il candidato la sua carry ce l'ha dentro, e non si toglie senza un
/// secondo backtest. L'eccesso resta quindi favorevole al candidato di circa <c>f × 0,21</c>, con
/// <c>f</c> = frazione di tempo a mercato.</para>
///
/// <para><b>2. L'eccesso si calcola a risk-free ZERO su entrambe le gambe.</b>
/// <c>Statistics.SharpeRatio</c> sottrae il 2%/anno al rendimento del <b>capitale intero</b> mentre
/// ne è investito il 10% e il resto rende zero nella simulazione. Il drag in Sharpe vale
/// <c>rf/σ</c>, e le σ non sono paragonabili: misurate sui grigi in archivio il 2026-08-22, da
/// 1,23% (Composite GRT 1h) a 5,26% (RsiOversold ADA 5m) contro ~4,8% del passivo. Con rf = 2% il
/// candidato tipico prende un handicap di 0,6-1,6 Sharpe contro lo 0,4 del passivo: <b>da 0,2 a 1,2
/// Sharpe di differenza fabbricata</b>, più grande del margine su cui un gate deciderebbe. A rf = 0
/// l'errore residuo è <c>0,2/σ</c> — il drag corretto per il 10% investito — cioè 0,06-0,11.
/// Costa zero: la curva del candidato è già in mano allo stadio.</para>
/// </summary>
public static class PassiveBenchmark
{
    /// <summary>Il risk-free con cui si misurano ENTRAMBE le gambe del confronto. Vedi il commento di classe.</summary>
    public const decimal RiskFreeForComparison = 0m;

    /// <summary>Il funding che il passivo paga. Zero, e il perché è nel commento di classe.</summary>
    public const decimal PassiveFundingRatePercentPer8h = 0m;

    /// <summary>
    /// La direzione prevalente, <b>pesata sul TEMPO</b> e non sul numero di trade.
    ///
    /// <para>Contare i trade darebbe la risposta sbagliata proprio sui casi che contano:
    /// <c>RsiOversold ETC/USDT 4h</c> ha una mediana di detenzione di 200 ore, e un long da 200 ore
    /// più venti short da un'ora sono un candidato <b>long</b> che il conteggio chiamerebbe short
    /// 20 a 1.</para>
    ///
    /// <para>Nel ramo degenere — tutti i trade istantanei, o nessun trade — restituisce
    /// <see cref="DominantDirection.Unknown"/> e <c>TimeInMarketFraction = null</c>: <b>non</b> il
    /// numero di trade travestito da frazione di tempo.</para>
    /// </summary>
    /// <param name="netThreshold">
    /// Quanto un lato deve dominare per chiamarlo prevalente: |esposizione netta| oltre questa
    /// soglia. Sotto, la direzione è <see cref="DominantDirection.Mixed"/> e il confronto non ha un
    /// lato ovvio.
    /// </param>
    public static (DominantDirection Direction, decimal NetExposure, decimal? TimeInMarketFraction) ClassifyDirection(
        IReadOnlyList<BacktestTrade> trades, DateTime from, DateTime to, decimal netThreshold = 0.60m)
    {
        var oreFinestra = (decimal)(to - from).TotalHours;
        decimal oreLong = 0m, oreShort = 0m;

        foreach (var t in trades)
        {
            var uscita = t.ExitTime ?? to;
            var ore = (decimal)(uscita - t.EntryTime).TotalHours;
            if (ore <= 0m) continue;   // trade istantaneo: non pesa
            if (string.Equals(t.Direction, "Short", StringComparison.OrdinalIgnoreCase)) oreShort += ore;
            else oreLong += ore;
        }

        var totale = oreLong + oreShort;
        if (totale <= 0m)
        {
            return (DominantDirection.Unknown, 0m, null);
        }

        var netta = (oreLong - oreShort) / totale;
        var frazione = oreFinestra > 0m ? totale / oreFinestra : (decimal?)null;
        var direzione = netta >= netThreshold ? DominantDirection.Long
                      : netta <= -netThreshold ? DominantDirection.Short
                      : DominantDirection.Mixed;
        return (direzione, netta, frazione);
    }

    /// <summary>
    /// La configurazione con cui si esegue il passivo: stessi costi del candidato, <b>tranne il
    /// funding</b> (vedi il commento di classe).
    /// </summary>
    public static BacktestConfiguration BuildConfig(
        BacktestConfiguration candidateConfig, DateTime from, DateTime to) => new()
        {
            ExchangeName = candidateConfig.ExchangeName,
            Symbol = candidateConfig.Symbol,
            Timeframe = candidateConfig.Timeframe,
            From = from,
            To = to,
            InitialCapital = candidateConfig.InitialCapital,
            PositionSizePercent = candidateConfig.PositionSizePercent,
            StrategyName = "PassiveHold",
            StrategyParameters = new(),
            SlippagePercent = candidateConfig.SlippagePercent,
            FeePercent = candidateConfig.FeePercent,
            FundingRatePercentPer8h = PassiveFundingRatePercentPer8h,
        };

    /// <summary>
    /// L'eccesso, con entrambe le gambe misurate a <see cref="RiskFreeForComparison"/>.
    /// Non si passa mai da <c>HoldoutSharpe</c>, che è calcolato col risk-free di default.
    /// </summary>
    public static PassiveComparison Compare(
        IReadOnlyList<EquityPoint> candidateCurve,
        IReadOnlyList<EquityPoint> passiveCurve,
        int periodsPerYear,
        DominantDirection direction,
        decimal netExposure,
        decimal? timeInMarket)
    {
        var candidato = Statistics.SharpeRatio(candidateCurve, periodsPerYear, RiskFreeForComparison);
        var passivo = Statistics.SharpeRatio(passiveCurve, periodsPerYear, RiskFreeForComparison);
        return new PassiveComparison(direction, netExposure, timeInMarket, passivo, candidato, candidato - passivo);
    }
}
