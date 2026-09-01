using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Monitoring;

/// <summary>
/// Confronta la performance REALIZZATA (trade chiusi dal vivo, Paper/Testnet/Live) di una gamba
/// dell'ensemble con quella ATTESA dal backtest/holdout che l'ha validata — "l'edge è morto?"
/// come segnale misurabile invece che intuizione. Puro/deterministico: nessuna dipendenza da DB
/// o orologio all'interno del calcolo (i trade e l'istante di analisi sono passati dal chiamante),
/// per restare testabile in isolamento con dati sintetici.
/// </summary>
public interface IStrategyDecayMonitor
{
    /// <summary>
    /// Analizza una gamba dato l'intero storico dei suoi trade chiusi (di qualunque strategia
    /// dell'ensemble contenga anche altre gambe — il filtro per <see cref="EnsembleStrategy.StrategyId"/>
    /// è fatto internamente, così il chiamante può passare l'intera tabella TradeRecords senza
    /// doverla già segmentare per gamba).
    /// </summary>
    /// <param name="timeframe">
    /// [M5] Timeframe della corsia (es. "1h"): serve a portare lo Sharpe realizzato sulla STESSA
    /// base per-candela dello Sharpe atteso, altrimenti il confronto non è interpretabile.
    /// </param>
    DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList<TradeRecord> allClosedTrades, string timeframe, DecayMonitorOptions? options = null);
}

/// <summary>Soglie del monitor di decadimento. Stessa finestra funge da minimo di trade richiesti e da ampiezza del rolling.</summary>
public sealed class DecayMonitorOptions
{
    /// <summary>Quante delle ultime operazioni chiuse considerare (e minimo richiesto prima di poter valutare).</summary>
    public int WindowTradeCount { get; set; } = 20;

    /// <summary>Sotto questa frazione di RealizedSharpe/ExpectedSharpe scatta l'alert (default 50%).</summary>
    public decimal AlertThresholdRatio { get; set; } = 0.5m;

    /// <summary>
    /// [M5b] Il tasso risk-free annuo con cui è stato calcolato lo Sharpe ATTESO
    /// (<c>Statistics.SharpeRatio</c>, default 2%). Serve solo a quantificare in
    /// <see cref="DecayReport.RiskFreeBiasSharpe"/> quanto il realizzato — che è LORDO — sia
    /// lusingato rispetto all'atteso. Non viene sottratto a nulla: vedi il commento del campo.
    /// </summary>
    /// <summary>
    /// [RF0, 2026-08-22] Portato a ZERO: dal 2026-08-22 <c>Statistics.SharpeRatio</c> non sottrae
    /// piu' alcun risk-free, quindi atteso e realizzato sono entrambi lordi e non c'e' nulla da
    /// quantificare. Il campo NON si rimuove: resta come guardiano — se
    /// <see cref="DecayReport.RiskFreeBiasSharpe"/> tornasse non nullo, qualcuno ha rimesso un
    /// risk-free sull'equity da qualche parte.
    /// </summary>
    public decimal ExpectedRiskFreeRateAnnual { get; set; } = 0m;

    /// <summary>
    /// [C1b] Oltre questo <c>|PnlPercent|</c> una riga non è un'operazione, è un <b>fill rotto</b>:
    /// va tolta dal calcolo e <b>dichiarata</b>, non silenziata.
    ///
    /// <para>Il caso è reale e vive ancora in tabella: la corsia 2 ha un trade SUI/USDT del 9 luglio
    /// 2026 entrato a 0,7694 e uscito a 1748,18 — <b>−227.340%</b> — prodotto dai fill patologici del
    /// testnet. Il buco è chiuso dal 18 luglio (<c>FillSanityCheck</c> guarda il fill al ritorno), ma
    /// quel guardiano protegge solo le righe NUOVE: la riga storica resta, e basta a decidere da sola
    /// lo Sharpe «realizzato» di una gamba se la corsia torna su quel simbolo.</para>
    ///
    /// <para>Il valore è volutamente <b>assurdo</b> e non una soglia statistica: 1.000% su una singola
    /// operazione non è una perdita grande, è aritmeticamente impossibile con i limiti di leva e di
    /// dimensione del motore. Non serve a togliere le code — quelle sono il segnale — ma a togliere
    /// ciò che non è un rendimento. Zero o negativo disattiva il filtro.</para>
    /// </summary>
    public decimal MaxPlausibleTradeReturnPercent { get; set; } = 1000m;
}

public sealed class DecayReport
{
    public string StrategyId { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public decimal? ExpectedSharpe { get; set; }

    /// <summary>
    /// [M5] Sharpe realizzato su base PER-CANDELA (bucket del timeframe, bucket senza trade = 0),
    /// annualizzato come lo Sharpe holdout: è il numero CONFRONTABILE con <see cref="ExpectedSharpe"/>.
    /// </summary>
    public decimal? RealizedSharpe { get; set; }

    /// <summary>
    /// Sharpe realizzato "a trade" (annualizzato con sqrt(trade/anno) stimati dalla cadenza del
    /// campione) — il valore storico del monitor, conservato come INFORMATIVO: non è sulla stessa
    /// base dell'atteso e non partecipa più alla soglia di alert.
    /// </summary>
    public decimal? RealizedTradeSharpe { get; set; }

    /// <summary>RealizedSharpe - ExpectedSharpe. Vedi <see cref="RiskFreeBiasSharpe"/>: il delta NON è pulito.</summary>
    public decimal? SharpeDelta { get; set; }

    /// <summary>
    /// [M5b, 2026-08-20] Di quanti punti di Sharpe il <see cref="RealizedSharpe"/> è
    /// <b>lusingato</b> rispetto all'<see cref="ExpectedSharpe"/> per la sola differenza di
    /// convenzione sul tasso risk-free.
    ///
    /// <para>L'atteso nasce da <c>Statistics.SharpeRatio</c>, che sottrae un risk-free (default 2%
    /// annuo); il realizzato è calcolato qui come mean/std × √(bucket/anno), <b>senza</b> alcun
    /// termine di risk-free. Sono due Sharpe diversi, e il secondo è sistematicamente più alto di
    /// rf/σ_annualizzata. La correzione M5 del 2026-08-19 aveva allineato la BASE TEMPORALE, non
    /// il risk-free — e infatti il test di sanità, per far tornare i due numeri, deve passare
    /// esplicitamente <c>riskFreeRateAnnual: 0</c> al lato atteso.</para>
    ///
    /// <para><b>Perché si dichiara invece di correggere.</b> Sottrarre lo stesso 2% al realizzato
    /// sembrerebbe la mossa ovvia ed è sbagliata: le due serie non hanno la stessa base di
    /// capitale. L'atteso viene da un'equity con posizioni a una frazione del capitale, il
    /// realizzato da <c>PnlPercent</c>, cioè un rendimento sul nozionale (spot) o sul margine
    /// (futures) della singola posizione. Applicare rf al secondo darebbe una correzione di
    /// ampiezza sbagliata di circa un ordine di grandezza. Renderli davvero confrontabili richiede
    /// di persistere la taglia con cui l'holdout è stato girato, che oggi non si salva: è un lavoro
    /// a sé, non una riga. Finché non c'è, il numero si mostra e non si applica.</para>
    ///
    /// <para>Null quando non calcolabile (serie degenere o volatilità nulla).</para>
    /// </summary>
    public decimal? RiskFreeBiasSharpe { get; set; }

    /// <summary>RealizedSharpe / ExpectedSharpe (1 = in linea, &lt;0.5 = alert di default). Null se ExpectedSharpe non è positivo (il rapporto non è interpretabile).</summary>
    public decimal? SharpeRatio { get; set; }

    public decimal? ExpectedProfitFactor { get; set; }
    public decimal? RealizedProfitFactor { get; set; }

    public int TradeCount { get; set; }
    public bool IsAlert { get; set; }

    /// <summary>
    /// [I13b] Il simbolo su cui il realizzato è stato misurato — cioè quello ATTUALE della corsia.
    /// Vuoto nei report costruiti da chiamanti che non lo dichiarano.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// [I13b] Trade di questa gamba <b>scartati</b> perché su un simbolo diverso da quello attuale
    /// della corsia (la corsia è stata riassegnata, o la coppia è stata cambiata a mano senza
    /// riscrivere le gambe).
    ///
    /// <para>Prima li si contava insieme agli altri: lo Sharpe «realizzato» di una gamba poteva
    /// nascere da trade fatti su DUE mercati diversi, e nessuna riga lo diceva. Il criterio è il
    /// <b>simbolo attuale</b> (AF2c-2), e lo scarto va dichiarato: un conteggio più basso senza
    /// spiegazione si legge come un guasto.</para>
    /// </summary>
    public int TradesExcludedOtherSymbol { get; set; }

    /// <summary>
    /// [C1b] Trade di questa gamba <b>scartati</b> perché il rendimento riportato è impossibile
    /// (oltre <see cref="DecayMonitorOptions.MaxPlausibleTradeReturnPercent"/>): fill rotti, non
    /// operazioni. Sempre &gt; 0 significa che in tabella c'è ancora una riga da bonificare.
    /// </summary>
    public int TradesExcludedImplausible { get; set; }

    /// <summary>
    /// [K39, 2026-09-01] Trade sul simbolo attuale <b>scartati perché più vecchi della gamba</b>,
    /// cioè chiusi prima del suo <c>ExpectedSharpeAtUtc</c>.
    ///
    /// <para><b>Perché esiste.</b> <c>TradeRecords</c> porta i tempi della CANDELA, e al riavvio del
    /// motore il feed rigioca fino a trenta giorni di storia: quelle righe hanno lo
    /// <c>StrategyId</c> e il simbolo <i>attuali</i>, quindi superano gli altri due filtri. Misurato
    /// il 2026-09-01: delle 66 righe lette per le cinque corsie di flotta, <b>65 erano precedenti
    /// alla creazione della gamba</b>, e l'unica gamba dichiarata «misurabile» aveva una finestra di
    /// venti righe di replay su venti.</para>
    ///
    /// <para>Va dichiarato per la stessa ragione degli altri due scarti, e con più forza: senza,
    /// una corsia con decine di righe in tabella risulterebbe «0 trade» e si leggerebbe come un
    /// guasto, mentre la verità è «0 trade <i>di questa gamba</i>, 27 di quelle prima».</para>
    /// </summary>
    public int TradesExcludedBeforeLeg { get; set; }

    /// <summary>
    /// [K39] La gamba <b>non dichiara quando è nata</b> (<c>ExpectedSharpeAtUtc</c> nullo), quindi
    /// non si misura affatto. Fail-closed voluto: misurare su una finestra di cui non si conosce
    /// l'inizio è peggio che dire «non lo so». Riguarda le gambe delle corsie d'impronta, e rende
    /// visibile il lavoro RF0 invece di mascherarlo con un numero.
    /// </summary>
    public bool LegHasNoBirthStamp { get; set; }

    /// <summary>
    /// [I13b] <b>Questa gamba è misurabile?</b> Vero quando i trade sul simbolo attuale bastano
    /// perché il confronto realizzato-vs-atteso significhi qualcosa.
    ///
    /// <para>È il gate del punto (b) dell'item, e <b>può chiudere il filone</b>: a 2-6 trade/mese,
    /// «≥20 trade sul simbolo attuale» può dare zero gambe misurabili su tutta la flotta. In quel
    /// caso il pannello lo dice e il freno per gamba (c) non si fa — misurare prima di agire vuol
    /// dire anche accettare che la misura dica di non agire.</para>
    /// </summary>
    public bool IsMeasurable { get; set; }

    /// <summary>Messaggio sempre valorizzato: spiega l'esito anche quando non scatta un alert (es. "trade insufficienti").</summary>
    public string StatusMessage { get; set; } = string.Empty;

    public DateTime AnalyzedAtUtc { get; set; }
}

public sealed class StrategyDecayMonitor : IStrategyDecayMonitor
{
    public DecayReport Analyze(EnsembleStrategy strategy, IReadOnlyList<TradeRecord> allClosedTrades, string timeframe, DecayMonitorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        allClosedTrades ??= [];
        options ??= new DecayMonitorOptions();

        var report = new DecayReport
        {
            StrategyId = strategy.StrategyId,
            StrategyName = strategy.StrategyName,
            DisplayName = strategy.DisplayName,
            ExpectedSharpe = strategy.ExpectedSharpe,
            ExpectedProfitFactor = strategy.ExpectedProfitFactor,
            AnalyzedAtUtc = DateTime.UtcNow,
        };

        // Ultime N chiuse (piu' recenti prima per il Take, poi rimesse in ordine cronologico:
        // il bucketing per periodo ha bisogno dell'ordine temporale corretto).
        var window = allClosedTrades
            .Where(t => t.StrategyId == strategy.StrategyId)
            .OrderByDescending(t => t.ClosedAtUtc)
            .Take(options.WindowTradeCount)
            .OrderBy(t => t.ClosedAtUtc)
            .ToList();
        report.TradeCount = window.Count;
        report.IsMeasurable = window.Count >= options.WindowTradeCount;

        if (!report.IsMeasurable)
        {
            report.StatusMessage = $"Trade insufficienti per una valutazione affidabile ({window.Count}/{options.WindowTradeCount}).";
            return report;
        }

        if (strategy.ExpectedSharpe is not decimal expected)
        {
            report.StatusMessage = "Metriche attese non disponibili (nessuna validazione collegata a questa gamba): nessun confronto possibile.";
            return report;
        }

        // [M5] Base OMOGENEA con l'atteso: l'ExpectedSharpe della gamba viene dal backtest/holdout,
        // calcolato su rendimenti PER CANDELA annualizzati con sqrt(candele/anno) (vedi
        // Statistics.SharpeRatio e PipelineApplier). Il vecchio realizzato era invece "a trade"
        // annualizzato con sqrt(trade/anno): due unità di misura diverse — es. una strategia con
        // 1 trade/settimana su 1h aveva un realizzato sgonfiato di ~sqrt(8760/52) ≈ 13x rispetto
        // all'atteso, e la soglia del 50% scattava (o taceva) senza significato. Qui i trade
        // vengono proiettati sui bucket del timeframe (bucket senza trade = rendimento 0, come le
        // candele piatte dell'holdout) e annualizzati con la STESSA convenzione.
        //
        // [M5b, 2026-08-20] La base TEMPORALE è omogenea; il RISK-FREE no. L'atteso è netto di
        // ExpectedRiskFreeRateAnnual, il realizzato qui sotto è lordo. Non si allinea sottraendo:
        // le due serie non hanno la stessa base di capitale (vedi DecayReport.RiskFreeBiasSharpe).
        // Si misura l'ampiezza del divario e la si dichiara, così il Delta non si legge come pulito.
        var (periodReturns, bucketsPerYear) = BuildPeriodReturns(window, timeframe);
        var realizedSharpe = AnnualizedSharpe(periodReturns, bucketsPerYear);
        var (tradeSharpe, realizedPf) = ComputeTradeMetrics(window);

        report.RealizedSharpe = realizedSharpe;
        report.RealizedTradeSharpe = tradeSharpe;
        report.RealizedProfitFactor = realizedPf;
        report.SharpeDelta = realizedSharpe - expected;
        report.RiskFreeBiasSharpe = RiskFreeBias(periodReturns, bucketsPerYear, options.ExpectedRiskFreeRateAnnual);

        if (expected <= 0m)
        {
            // Una soglia "% dell'atteso" non ha senso se l'atteso stesso non era un edge positivo:
            // il segno capovolgerebbe il significato del rapporto (es. -0.6/-1.2 = 0.5 sembrerebbe
            // "in linea" quando in realtà -0.6 è MEGLIO di -1.2, non uguale).
            report.StatusMessage = $"Sharpe atteso non positivo ({expected:F2}): la soglia percentuale non è applicabile, valutare il delta ({report.SharpeDelta:F2}) a occhio.";
            return report;
        }

        var ratio = realizedSharpe / expected;
        report.SharpeRatio = ratio;
        report.IsAlert = ratio < options.AlertThresholdRatio;
        // [M5b] Il divario di risk-free viaggia col verdetto: il realizzato è LORDO, l'atteso è
        // netto, e il rapporto è quindi generoso di quella quantità. Dirlo qui è ciò che impedisce
        // di leggere «in linea» come una misura pulita — regola 5, degradare dicendolo.
        // [RF0, 2026-08-22] La meta' RISK-FREE del disallineamento e' chiusa: Statistics.SharpeRatio
        // non sottrae piu' nulla, quindi atteso e realizzato sono entrambi LORDI e
        // RiskFreeBiasSharpe e' null per costruzione. Il ramo resta come GUARDIANO: se un giorno
        // tornasse non nullo, qualcuno ha rimesso un risk-free sull'equity.
        //
        // Resta aperta la meta' BASE DI CAPITALE, e va detta lo stesso: il realizzato e' un
        // rendimento sul NOZIONALE (PnlPercent), l'atteso una curva di equity a una frazione del
        // capitale. Il rapporto fra le due sigma e' dell'ordine di 10x, ma non si puo' correggere
        // finche' la taglia con cui l'holdout e' girato non viene persistita: si DICHIARA, non si
        // applica — inventare un fattore sarebbe peggio del silenzio.
        var bias = report.RiskFreeBiasSharpe is decimal b && b > 0.01m
            ? FormattableString.Invariant(
                $" ATTENZIONE: risk-free residuo di {options.ExpectedRiskFreeRateAnnual:P0}/anno sull'atteso (~{b:F2} punti di Sharpe). Dal 2026-08-22 dovrebbe essere zero: qualcuno lo ha rimesso.")
            : " Attenzione: il realizzato è un rendimento sul NOZIONALE, l'atteso su un'equity a una frazione del capitale. Il rapporto non è pulito, e non si può correggere: la taglia con cui l'holdout è girato non viene persistita.";
        report.StatusMessage = (report.IsAlert
            ? $"ALERT: Sharpe realizzato {realizedSharpe:F2} vs atteso {expected:F2} ({ratio:P0}) — sotto la soglia {options.AlertThresholdRatio:P0}."
            : $"In linea: Sharpe realizzato {realizedSharpe:F2} vs atteso {expected:F2} ({ratio:P0}).") + bias;
        return report;
    }

    /// <summary>
    /// [M5] Proietta i trade chiusi sui bucket temporali del timeframe: bucket i = periodo
    /// i-esimo dal primo trade, ogni trade contribuisce il proprio rendimento (PnlPercent/100)
    /// al bucket della sua CHIUSURA, bucket senza trade = 0 (come le candele piatte
    /// dell'holdout, dove l'equity non si muove). Se la finestra copre più di
    /// <paramref name="maxBuckets"/> periodi il bucket viene ingrossato di un fattore k intero
    /// (es. 2 candele per bucket) e l'annualizzazione usa i periodi-per-anno del bucket
    /// EFFETTIVO (PeriodsPerYear/k), così il vettore resta bounded senza distorcere la scala.
    /// </summary>
    internal static (IReadOnlyList<decimal> Returns, decimal BucketsPerYear) BuildPeriodReturns(
        IReadOnlyList<TradeRecord> chronological, string timeframe, int maxBuckets = 20_000)
    {
        var ppy = Statistics.PeriodsPerYear(timeframe);
        var period = TimeSpan.FromDays(365.0 / ppy);

        var start = chronological[0].ClosedAtUtc;
        var end = chronological[^1].ClosedAtUtc;
        var rawBuckets = (long)Math.Floor((end - start) / period) + 1;
        var k = (int)Math.Max(1L, (rawBuckets + maxBuckets - 1) / maxBuckets);
        var bucket = period * k;

        var count = (int)Math.Floor((end - start) / bucket) + 1;
        var returns = new decimal[count];
        foreach (var t in chronological)
        {
            var idx = (int)Math.Floor((t.ClosedAtUtc - start) / bucket);
            returns[idx] += t.PnlPercent / 100m;
        }
        return (returns, (decimal)ppy / k);
    }

    /// <summary>
    /// [M5b] Punti di Sharpe di cui il realizzato LORDO supera un realizzato calcolato con lo stesso
    /// risk-free dell'atteso: rf_annuo / σ_annualizzata. È l'ampiezza del disallineamento di
    /// convenzione, misurata sulla volatilità della serie realizzata stessa — non una correzione
    /// da applicare (le due serie non condividono la base di capitale). Null se non calcolabile.
    /// </summary>
    internal static decimal? RiskFreeBias(IReadOnlyList<decimal> returns, decimal periodsPerYear, decimal riskFreeRateAnnual)
    {
        if (returns.Count < 2 || riskFreeRateAnnual <= 0m || periodsPerYear <= 0m) return null;
        var mean = returns.Average();
        decimal sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }
        var stdDev = (decimal)Math.Sqrt((double)(sumSq / returns.Count));
        if (stdDev <= 0m) return null;
        var annualizedSigma = stdDev * (decimal)Math.Sqrt((double)periodsPerYear);
        return annualizedSigma <= 0m ? null : riskFreeRateAnnual / annualizedSigma;
    }

    /// <summary>
    /// Sharpe annualizzato mean/std × sqrt(bucket/anno), varianza di popolazione (coerente con
    /// Statistics.SharpeRatio tranne che per il risk-free, che qui NON si sottrae — vedi
    /// <see cref="DecayReport.RiskFreeBiasSharpe"/>). 0 se degenere.
    /// </summary>
    private static decimal AnnualizedSharpe(IReadOnlyList<decimal> returns, decimal periodsPerYear)
    {
        if (returns.Count < 2) return 0m;
        var mean = returns.Average();
        decimal sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }
        var stdDev = (decimal)Math.Sqrt((double)(sumSq / returns.Count));
        return stdDev == 0m ? 0m : mean / stdDev * (decimal)Math.Sqrt((double)periodsPerYear);
    }

    /// <summary>
    /// Metriche "a trade": Profit Factor (invariato) e lo Sharpe per-trade storico, annualizzato
    /// con sqrt(trade/anno) stimati dalla cadenza reale del campione (trade/anno = N / giorni di
    /// ampiezza × 365, ampiezza ≥ 1 giorno per non esplodere su burst compressi). Dal fix M5
    /// questo numero è solo INFORMATIVO (<see cref="DecayReport.RealizedTradeSharpe"/>): non è
    /// sulla stessa base per-candela dell'atteso e non pilota più la soglia di alert.
    /// </summary>
    private static (decimal Sharpe, decimal ProfitFactor) ComputeTradeMetrics(IReadOnlyList<TradeRecord> chronological)
    {
        var returns = chronological.Select(t => t.PnlPercent / 100m).ToList();
        var n = returns.Count;
        var mean = returns.Average();

        decimal sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }
        var variance = sumSq / n; // popolazione, coerente con Statistics.SharpeRatio
        var stdDev = (decimal)Math.Sqrt((double)variance);

        decimal sharpe;
        if (stdDev == 0m)
        {
            sharpe = 0m;
        }
        else
        {
            var tradesPerYear = EstimateTradesPerYear(chronological);
            sharpe = mean / stdDev * (decimal)Math.Sqrt((double)tradesPerYear);
        }

        var grossProfit = chronological.Where(t => t.Pnl > 0m).Sum(t => t.Pnl);
        var grossLoss = chronological.Where(t => t.Pnl < 0m).Sum(t => t.Pnl);
        var pf = grossLoss == 0m ? 0m : grossProfit / Math.Abs(grossLoss);

        return (sharpe, pf);
    }

    private static decimal EstimateTradesPerYear(IReadOnlyList<TradeRecord> chronological)
    {
        var n = chronological.Count;
        var spanDays = (decimal)(chronological[^1].ClosedAtUtc - chronological[0].ClosedAtUtc).TotalDays;
        var effectiveDays = Math.Max(spanDays, 1m);
        return n / effectiveDays * 365m;
    }
}


/// <summary>
/// [I13b] <b>Il verdetto di misurabilità della corsia</b>: quante gambe hanno abbastanza trade sul
/// simbolo attuale perché il confronto realizzato-vs-atteso significhi qualcosa, e in quanto tempo
/// le altre ci arriverebbero al ritmo che dichiarano.
///
/// <para>È il gate del punto (b) del filone «freno per gamba», e <b>può chiuderlo</b>: a 2-6
/// trade/mese, «≥20 trade sul simbolo attuale» può dare <b>zero</b> gambe misurabili su tutta la
/// flotta — al 2026-08-19 le corsie 3-7 avevano un trade ciascuna o zero in 13-15 giorni. In quel
/// caso il pannello lo dice e il freno per gamba (c) <b>non si fa</b>: misurare prima di agire vuol
/// dire anche accettare che la misura dica di non agire.</para>
///
/// <para>Puro e statico: prende i report già calcolati e le attese delle gambe, e non tocca né
/// database né orologio.</para>
/// </summary>
public static class DecayMeasurability
{
    /// <param name="Measurable">Gambe con abbastanza trade sul simbolo attuale.</param>
    /// <param name="Total">Gambe considerate (le attive della corsia).</param>
    /// <param name="Verdict">La frase da mostrare, che dichiara anche il tempo-al-verdetto delle non misurabili.</param>
    public sealed record Result(int Measurable, int Total, string Verdict)
    {
        /// <summary>Vero quando NESSUNA gamba è misurabile: il caso in cui il filone si chiude.</summary>
        public bool NothingMeasurable => Total > 0 && Measurable == 0;
    }

    public static Result Evaluate(
        IReadOnlyList<DecayReport> reports,
        IReadOnlyDictionary<string, decimal?> expectedTradesPerMonthByStrategyId,
        int requiredTrades)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(expectedTradesPerMonthByStrategyId);

        if (reports.Count == 0) return new Result(0, 0, "Nessuna gamba da misurare su questa corsia.");

        var measurable = reports.Count(r => r.IsMeasurable);
        if (measurable == reports.Count)
        {
            return new Result(measurable, reports.Count,
                $"Tutte le {reports.Count} gambe hanno almeno {requiredTrades} trade sul simbolo attuale: il confronto realizzato-vs-atteso è interpretabile.");
        }

        // Il tempo che manca alle non misurabili, alla frequenza che DICHIARANO. È il numero che
        // trasforma «non ancora» in una data, e a volte rivela che quella data non arriverà mai.
        var attese = reports
            .Where(r => !r.IsMeasurable)
            .Select(r =>
            {
                var perMese = expectedTradesPerMonthByStrategyId.GetValueOrDefault(r.StrategyId);
                var mancanti = Math.Max(0, requiredTrades - r.TradeCount);
                var mesi = Fleet.TradeFrequency.MonthsToVerdict(perMese, mancanti);
                return (r, perMese, mancanti, mesi);
            })
            .ToList();

        var maiGiudicabili = attese.Count(a => a.perMese is null or <= 0m);
        var piuLontana = attese.Select(a => a.mesi).Where(m => m is not null).DefaultIfEmpty(null).Max();

        var testo = $"{measurable} gambe su {reports.Count} sono misurabili ({requiredTrades} trade sul simbolo attuale). ";
        if (measurable == 0) testo += "NESSUNA misura è interpretabile in questo momento. ";

        if (maiGiudicabili == attese.Count)
        {
            testo += "Per le altre il ritmo atteso non è dichiarato (o è zero): quando lo saranno non è derivabile.";
        }
        else if (piuLontana is decimal m)
        {
            testo += $"Alle altre servono fino a ~{m:0.#} mesi al ritmo che dichiarano";
            testo += maiGiudicabili > 0 ? $", e {maiGiudicabili} non hanno un ritmo dichiarato." : ".";
        }

        return new Result(measurable, reports.Count, testo);
    }
}
