using ProcioneMGR.Services.Backtesting;

namespace ProcioneMGR.Services.Carry;

/// <summary>
/// [E3 roadmap profitto-intraday] Backtest DETERMINISTICO del carry delta-neutro (long spot + short
/// perp). Itera sugli EVENTI DI FUNDING (il passo naturale del carry, ogni 8h): a ogni evento, se in
/// posizione, lo short INCASSA il funding firmato (positivo → income; negativo → costo); la decisione
/// di aprire/chiudere usa la media annualizzata degli ultimi <c>TrailingFundingEvents</c> eventi, con
/// isteresi enter&gt;exit. I costi delle DUE gambe (fee+slippage spot e perp) si pagano all'apertura
/// e alla chiusura.
///
/// <para><b>Delta-neutralità e semplificazione dichiarata.</b> Con long spot e short perp allo stesso
/// nozionale sullo stesso sottostante, la componente DIREZIONALE del prezzo si elide: quel che resta è
/// funding − costi. La BASE (differenza spot/perp) e il suo drift sono un rischio del second'ordine
/// REALE che questo backtest — che vede solo la serie funding, non le due serie prezzo separate — NON
/// modella. Va dichiarato: il netto qui è il carry PURO; sul campo la base aggiunge rumore (di norma
/// piccolo, ma non zero, e va sorvegliato dal vivo). Questa è la STESSA logica della fase `carry`
/// (F1.b), qui resa un motore testato e riusabile dal percorso live.</para>
///
/// <para>Puro e deterministico: nessuna casualità, nessun accesso a DB/rete. Il chiamante fornisce la
/// serie funding (dal DB o dall'exchange).</para>
/// </summary>
public sealed class CarryBacktestEngine
{
    /// <param name="funding">Serie funding FIRMATA, ordinata cronologicamente (rate in % per 8h).</param>
    public CarryBacktestResult Run(IReadOnlyList<FundingRatePoint> funding, CarryConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(config);
        if (config.ExitAnnualFundingPercent >= config.EnterAnnualFundingPercent)
            throw new ArgumentException("La soglia di uscita deve essere < soglia di entrata (isteresi).", nameof(config));

        var events = funding.OrderBy(f => f.TimestampUtc).ToList();
        var result = new CarryBacktestResult { FundingEventsTotal = events.Count };
        if (events.Count < config.TrailingFundingEvents + 2)
        {
            result.FinalCapital = config.InitialCapital;
            return result;
        }

        var trailing = Math.Max(1, config.TrailingFundingEvents);
        var perYear = config.FundingEventsPerDay * 365m;
        // Nozionale per gamba come frazione del capitale: i "percent" sono su questo nozionale.
        var legFrac = config.PositionSizePercent / 100m;
        var episodeCostPct = 2m * (config.SpotFeePercent + config.SlippagePercent)
                           + 2m * (config.PerpFeePercent + config.SlippagePercent);   // 4 fill, due gambe

        var inPosition = false;
        DateTime openedAt = default;
        decimal episodeFundingPct = 0m;
        var episodeEvents = 0;
        decimal cumulativeNetOnCapital = 0m;   // netto cumulato in frazione del capitale iniziale

        var window = new Queue<decimal>(trailing);
        decimal windowSum = 0m;
        var eventsInPos = 0;

        var equity = new List<EquityPoint>(events.Count);
        var episodes = new List<CarryEpisode>();
        decimal grossPctSum = 0m, costPctSum = 0m;

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var ratePct = e.RatePercentPer8h;   // % per 8h, firmato

            // 1) Incasso del funding dell'evento CORRENTE se si era in posizione PRIMA dell'evento
            //    (prima l'income, poi la decisione: nessun look-ahead sull'evento stesso).
            if (inPosition)
            {
                episodeFundingPct += ratePct;    // lo short riceve il funding positivo
                episodeEvents++;
                eventsInPos++;
                grossPctSum += ratePct;
            }

            // 2) Aggiorna la media mobile per la decisione.
            window.Enqueue(ratePct);
            windowSum += ratePct;
            if (window.Count > trailing) windowSum -= window.Dequeue();

            // 3) Decisione (solo con finestra piena), tramite la REGOLA CONDIVISA col percorso live.
            if (window.Count >= trailing)
            {
                var annualized = windowSum / window.Count * perYear;
                switch (CarryDecider.Decide(annualized, inPosition, config))
                {
                    case CarryAction.Open:
                        inPosition = true;
                        openedAt = e.TimestampUtc;
                        episodeFundingPct = 0m;
                        episodeEvents = 0;
                        costPctSum += episodeCostPct;   // costo di apertura+chiusura contabilizzato all'ingresso
                        break;
                    case CarryAction.Close:
                        var netPct = episodeFundingPct - episodeCostPct;
                        episodes.Add(new CarryEpisode(openedAt, e.TimestampUtc, episodeEvents, episodeFundingPct, episodeCostPct, netPct));
                        cumulativeNetOnCapital += netPct / 100m * legFrac;
                        inPosition = false;
                        break;
                }
            }

            equity.Add(new EquityPoint
            {
                Timestamp = e.TimestampUtc,
                Capital = config.InitialCapital * (1m + cumulativeNetOnCapital
                    // Mark-to-market dell'episodio aperto (funding maturato − costo già pagato).
                    + (inPosition ? (episodeFundingPct - episodeCostPct) / 100m * legFrac : 0m)),
            });
        }

        // Chiude un eventuale episodio ancora aperto a fine serie.
        if (inPosition)
        {
            var netPct = episodeFundingPct - episodeCostPct;
            episodes.Add(new CarryEpisode(openedAt, events[^1].TimestampUtc, episodeEvents, episodeFundingPct, episodeCostPct, netPct));
            cumulativeNetOnCapital += netPct / 100m * legFrac;
        }

        var years = (double)(events[^1].TimestampUtc - events[0].TimestampUtc).TotalDays / 365.25;
        result.FinalCapital = config.InitialCapital * (1m + cumulativeNetOnCapital);
        result.TotalReturnPercent = cumulativeNetOnCapital * 100m;
        result.GrossFundingPercent = grossPctSum;
        result.TotalCostPercent = costPctSum;
        result.Episodes = episodes.Count;
        result.FundingEventsInPosition = eventsInPos;
        result.TimeInPositionFraction = events.Count > 0 ? (decimal)eventsInPos / events.Count : 0m;
        result.NetAnnualizedPercent = years > 0.05 ? result.TotalReturnPercent / (decimal)years : result.TotalReturnPercent;
        result.EpisodeList = episodes;
        result.EquityCurve = equity;
        return result;
    }
}
