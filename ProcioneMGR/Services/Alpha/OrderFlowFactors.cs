using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha;

// =============================================================================================
//  [3.8b roadmap macchina-ricerca] Fattori ORDER FLOW sui campi klines recuperati da T0.3
//  (TakerBuyVolume, TradeCount — prima scartati al parsing). Rispettano il contratto
//  anti-look-ahead di IAlphaFactor e restituiscono null dove i campi estesi non sono stati
//  ancora raccolti: un fattore calcolato su uno zero finto sarebbe un segnale inventato.
// =============================================================================================

/// <summary>
/// SBILANCIAMENTO TAKER: quota della pressione aggressiva in acquisto, mediata su
/// <c>Lookback</c> barre e centrata in [-1, +1]:
///   value[i] = media su [i-Lookback+1, i] di (2·TakerBuyVolume/Volume − 1)
/// +1 = tutto il volume ha attraversato lo spread in acquisto, −1 in vendita, 0 = equilibrio.
/// È l'order flow aggregato — chi paga lo spread pur di eseguire SUBITO — cioè la componente
/// degli scambi che la letteratura microstrutturale lega all'informazione, non alla liquidità.
/// Null finché la finestra non è interamente coperta dai campi estesi.
/// </summary>
public sealed class TakerImbalanceFactor : IAlphaFactor
{
    public string Name => "TakerImbalance";
    public string DisplayName => "Sbilanciamento taker";
    public FactorCategory Category => FactorCategory.Volume;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 10m, 1m, 500m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(1, p.GetIntOrDefault("Lookback", 10));
        var n = candles.Count;

        // Imbalance per-barra, null dove i campi mancano o il volume è zero.
        var raw = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            var c = candles[i];
            if (c.TakerBuyVolume is decimal tb && c.Volume > 0m)
            {
                raw[i] = 2m * tb / c.Volume - 1m;
            }
        }

        var r = new decimal?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            decimal sum = 0m;
            var complete = true;
            for (var k = i - lookback + 1; k <= i; k++)
            {
                if (raw[k] is not decimal v) { complete = false; break; }
                sum += v;
            }
            if (complete) r[i] = sum / lookback;
        }
        return r;
    }
}

/// <summary>
/// DIMENSIONE MEDIA DEL TRADE relativa alla propria storia:
///   value[i] = (Volume/TradeCount)[i] / media su Lookback barre − 1
/// Sopra zero = stanno passando ordini più grossi del solito (attività "istituzionale" o
/// aggressiva), sotto = frammentazione retail. Il rapporto alla propria media rolling rimuove la
/// non-stazionarietà di lungo periodo (la dimensione media dei trade cambia di anno in anno).
/// Null dove TradeCount manca o è zero, o la finestra non è coperta.
/// </summary>
public sealed class AvgTradeSizeFactor : IAlphaFactor
{
    public string Name => "AvgTradeSize";
    public string DisplayName => "Dimensione media trade";
    public FactorCategory Category => FactorCategory.Volume;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions { get; } =
    [
        new("Lookback", "Lookback", 20m, 2m, 500m),
    ];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> p)
    {
        var lookback = Math.Max(2, p.GetIntOrDefault("Lookback", 20));
        var n = candles.Count;

        var size = new decimal?[n];
        for (var i = 0; i < n; i++)
        {
            var c = candles[i];
            if (c.TradeCount is long tc && tc > 0)
            {
                size[i] = c.Volume / tc;
            }
        }

        var r = new decimal?[n];
        for (var i = lookback - 1; i < n; i++)
        {
            decimal sum = 0m;
            var complete = true;
            for (var k = i - lookback + 1; k <= i; k++)
            {
                if (size[k] is not decimal v) { complete = false; break; }
                sum += v;
            }
            if (!complete) continue;
            var mean = sum / lookback;
            if (mean > 0m && size[i] is decimal cur)
            {
                r[i] = cur / mean - 1m;
            }
        }
        return r;
    }
}
