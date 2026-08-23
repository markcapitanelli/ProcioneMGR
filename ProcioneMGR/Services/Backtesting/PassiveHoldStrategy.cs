using ProcioneMGR.Data;
using ProcioneMGR.Services.Indicators;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// [Difetto B, 2026-08-22] <b>Il benchmark banale: tieni la stessa direzione e non fare niente.</b>
///
/// <para>Apre alla prima barra valutabile e poi tace: la chiusura la fa il motore sull'ultima
/// candela, come per ogni posizione ancora aperta a fine backtest. Un solo round-trip di costi.</para>
///
/// <para><b>Perché serve.</b> Nessuno dei sette stadi della pipeline confrontava un candidato con
/// una posizione costante nella sua stessa direzione, e senza quel confronto <i>uno Sharpe positivo
/// su un mercato che scende non è un edge se la strategia era short</i>: sta prendendo il beta del
/// mercato col segno giusto per caso. Misurato sull'archivio il 2026-08-22, sei gambe su nove fra
/// quelle proposte non battevano il passivo nella loro stessa finestra — e infatti dal 27 luglio,
/// con quattordici simboli su quattordici in salita, quelle gambe hanno cambiato segno.</para>
///
/// <para>È il complemento esatto di <c>NullTwinValidation</c>, che confronta con un mercato <b>senza
/// struttura direzionale</b> (stessa volatilità, deriva rimossa): il gemello nullo toglie la deriva,
/// quindi non può per costruzione dire «questo Sharpe <i>è</i> la deriva».</para>
///
/// <para><b>Deliberatamente NON registrata in <see cref="StrategyFactory"/>.</b> I
/// <c>Prototypes</c> alimentano lo spazio di ricerca di <c>StrategyDiscoveryEngine</c>, che senza
/// una lista esplicita usa tutti i prototipi: registrarla gonfierebbe il conteggio tentativi e
/// quindi il DSR di ogni candidato del run. Si passa dall'overload di <see cref="IBacktestEngine"/>
/// che accetta un'istanza già pronta — stesso motore, stessa contabilità, stessi costi, nessuna
/// seconda verità.</para>
/// </summary>
public sealed class PassiveHoldStrategy(bool isLong) : IStrategy
{
    /// <summary>Nome usato solo nei log e nei messaggi: non è un nome risolvibile dalla factory.</summary>
    public string Name => "PassiveHold";

    public string DisplayName => isLong ? "Passivo long (compra e tieni)" : "Passivo short (vendi e tieni)";

    public IReadOnlyList<StrategyParameterDefinition> ParameterDefinitions => [];

    private bool _aperta;

    public Task InitializeAsync(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<OhlcvData> candles,
        IReadOnlyDictionary<string, decimal> parameters,
        ITechnicalIndicatorsService indicators,
        CancellationToken ct)
    {
        // Il motore crea UNA istanza per corsa, ma azzerare qui rende la classe riusabile senza
        // sorprese: un'istanza riciclata che si credesse già a mercato non aprirebbe mai.
        _aperta = false;
        return Task.CompletedTask;
    }

    public Signal EvaluateSignal(int index, decimal currentPrice, DateTime timestamp)
    {
        if (_aperta)
        {
            return Signal.Hold;
        }
        _aperta = true;
        return isLong ? Signal.Long : Signal.Short;
    }
}
