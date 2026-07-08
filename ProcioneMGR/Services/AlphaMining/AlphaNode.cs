using System.Globalization;
using System.Text;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.AlphaMining;

/// <summary>Operatore di un nodo dell'albero di espressione alpha.</summary>
public enum AlphaOp
{
    // Terminali
    Var,    // campo OHLCV ($Close, $High, ...)
    Const,  // costante

    // Aritmetici unari
    Neg, Abs, Log, Sign,

    // Aritmetici binari
    Add, Sub, Mul, Div,

    // Temporali unari (finestra causale)
    Ref,    // valore di d periodi fa
    Delta,  // x - Ref(x, d)
    Mean, Std, TsMax, TsMin, TsRank,

    // Temporali binari (finestra causale)
    Corr,
}

/// <summary>
/// Nodo di un <b>albero di espressione alpha</b> (formulaic alpha mining, rif.
/// <c>docs/ROADMAP-QLIB.md §1.7</c>). Ogni nodo compila a una serie <c>decimal?[]</c> allineata alle
/// candele. CONTRATTO ANTI-LOOK-AHEAD PER COSTRUZIONE: ogni operatore usa solo <c>candles[0..i]</c>
/// (i temporali leggono la finestra che termina a i), quindi qualunque albero — anche generato a
/// caso dal miner genetico — rispetta l'invariante senza bisogno di verifiche per-nodo.
///
/// I <c>null</c> si propagano: dove un ingresso non è calcolabile (warm-up, divisione per zero,
/// log di non-positivo) l'uscita è <c>null</c>, così la serie resta allineata per indice ai prezzi.
/// </summary>
public sealed class AlphaNode
{
    private const decimal Epsilon = 0.000000000001m; // 1e-12: soglia denominatore per Div

    public AlphaOp Op { get; init; }
    public string? Field { get; init; }        // per Var: "Open"|"High"|"Low"|"Close"|"Volume"
    public decimal Const { get; init; }         // per Const
    public int Window { get; init; }            // per operatori temporali
    public AlphaNode[] Children { get; init; } = [];

    public static readonly string[] Fields = ["Open", "High", "Low", "Close", "Volume"];

    // --- Costruttori di comodo ---------------------------------------------------------------

    public static AlphaNode Variable(string field) => new() { Op = AlphaOp.Var, Field = field };
    public static AlphaNode Constant(decimal value) => new() { Op = AlphaOp.Const, Const = value };
    public static AlphaNode Unary(AlphaOp op, AlphaNode a) => new() { Op = op, Children = [a] };
    public static AlphaNode Binary(AlphaOp op, AlphaNode a, AlphaNode b) => new() { Op = op, Children = [a, b] };
    public static AlphaNode TimeUnary(AlphaOp op, AlphaNode a, int window) => new() { Op = op, Window = window, Children = [a] };
    public static AlphaNode TimeBinary(AlphaOp op, AlphaNode a, AlphaNode b, int window) => new() { Op = op, Window = window, Children = [a, b] };

    // --- Struttura ---------------------------------------------------------------------------

    /// <summary>Numero totale di nodi (misura di complessità per la penalità anti-overfitting).</summary>
    public int Size() => 1 + Children.Sum(c => c.Size());

    /// <summary>Profondità dell'albero.</summary>
    public int Depth() => Children.Length == 0 ? 1 : 1 + Children.Max(c => c.Depth());

    public AlphaNode Clone() => new()
    {
        Op = Op,
        Field = Field,
        Const = Const,
        Window = Window,
        Children = Children.Select(c => c.Clone()).ToArray(),
    };

    // --- Valutazione causale -----------------------------------------------------------------

    public decimal?[] Evaluate(IReadOnlyList<OhlcvData> candles)
    {
        var n = candles.Count;
        switch (Op)
        {
            case AlphaOp.Var:
            {
                var r = new decimal?[n];
                for (var i = 0; i < n; i++) r[i] = Select(candles[i], Field);
                return r;
            }
            case AlphaOp.Const:
            {
                var r = new decimal?[n];
                for (var i = 0; i < n; i++) r[i] = Const;
                return r;
            }
            case AlphaOp.Neg: return Map(Children[0].Evaluate(candles), x => -x);
            case AlphaOp.Abs: return Map(Children[0].Evaluate(candles), x => Math.Abs(x));
            case AlphaOp.Sign: return Map(Children[0].Evaluate(candles), x => x > 0m ? 1m : x < 0m ? -1m : 0m);
            case AlphaOp.Log: return Map(Children[0].Evaluate(candles), x => x > 0m ? (decimal?)Math.Log((double)x) : null);

            case AlphaOp.Add: return Zip(Children[0].Evaluate(candles), Children[1].Evaluate(candles), (a, b) => a + b);
            case AlphaOp.Sub: return Zip(Children[0].Evaluate(candles), Children[1].Evaluate(candles), (a, b) => a - b);
            case AlphaOp.Mul: return Zip(Children[0].Evaluate(candles), Children[1].Evaluate(candles), (a, b) => a * b);
            case AlphaOp.Div: return Zip(Children[0].Evaluate(candles), Children[1].Evaluate(candles), (a, b) => Math.Abs(b) < Epsilon ? null : a / b);

            case AlphaOp.Ref: return Lag(Children[0].Evaluate(candles), Window);
            case AlphaOp.Delta:
            {
                var x = Children[0].Evaluate(candles);
                return Zip(x, Lag(x, Window), (a, b) => a - b);
            }
            case AlphaOp.Mean: return Rolling(Children[0].Evaluate(candles), Window, WindowMean);
            case AlphaOp.Std: return Rolling(Children[0].Evaluate(candles), Window, WindowStd);
            case AlphaOp.TsMax: return Rolling(Children[0].Evaluate(candles), Window, w => w.Max());
            case AlphaOp.TsMin: return Rolling(Children[0].Evaluate(candles), Window, w => w.Min());
            case AlphaOp.TsRank: return RollingRank(Children[0].Evaluate(candles), Window);
            case AlphaOp.Corr: return RollingCorr(Children[0].Evaluate(candles), Children[1].Evaluate(candles), Window);

            default: return new decimal?[n];
        }
    }

    private static decimal Select(OhlcvData c, string? field) => field switch
    {
        "Open" => c.Open,
        "High" => c.High,
        "Low" => c.Low,
        "Volume" => c.Volume,
        _ => c.Close,
    };

    private static decimal?[] Map(decimal?[] a, Func<decimal, decimal?> f)
    {
        var r = new decimal?[a.Length];
        for (var i = 0; i < a.Length; i++) if (a[i].HasValue) r[i] = f(a[i]!.Value);
        return r;
    }

    private static decimal?[] Zip(decimal?[] a, decimal?[] b, Func<decimal, decimal, decimal?> f)
    {
        var n = Math.Min(a.Length, b.Length);
        var r = new decimal?[n];
        for (var i = 0; i < n; i++) if (a[i].HasValue && b[i].HasValue) r[i] = f(a[i]!.Value, b[i]!.Value);
        return r;
    }

    private static decimal?[] Lag(decimal?[] a, int d)
    {
        var r = new decimal?[a.Length];
        for (var i = d; i < a.Length; i++) r[i] = a[i - d];
        return r;
    }

    private static decimal?[] Rolling(decimal?[] a, int d, Func<List<decimal>, decimal?> agg)
    {
        var n = a.Length;
        var r = new decimal?[n];
        if (d < 1) return r;
        for (var i = d - 1; i < n; i++)
        {
            var window = new List<decimal>(d);
            var ok = true;
            for (var j = i - d + 1; j <= i; j++)
            {
                if (!a[j].HasValue) { ok = false; break; }
                window.Add(a[j]!.Value);
            }
            if (ok) r[i] = agg(window);
        }
        return r;
    }

    private static decimal? WindowMean(List<decimal> w)
    {
        decimal s = 0m;
        foreach (var v in w) s += v;
        return s / w.Count;
    }

    private static decimal? WindowStd(List<decimal> w)
    {
        if (w.Count < 2) return null;
        var mean = w.Average();
        decimal ss = 0m;
        foreach (var v in w) { var d = v - mean; ss += d * d; }
        return (decimal)Math.Sqrt((double)(ss / w.Count));
    }

    private static decimal?[] RollingRank(decimal?[] a, int d)
    {
        var n = a.Length;
        var r = new decimal?[n];
        if (d < 1) return r;
        for (var i = d - 1; i < n; i++)
        {
            if (!a[i].HasValue) continue;
            var cur = a[i]!.Value;
            var below = 0;
            var ok = true;
            for (var j = i - d + 1; j <= i; j++)
            {
                if (!a[j].HasValue) { ok = false; break; }
                if (a[j]!.Value <= cur) below++;
            }
            if (ok) r[i] = (decimal)below / d;
        }
        return r;
    }

    private static decimal?[] RollingCorr(decimal?[] a, decimal?[] b, int d)
    {
        var n = Math.Min(a.Length, b.Length);
        var r = new decimal?[n];
        if (d < 3) return r;
        for (var i = d - 1; i < n; i++)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            var ok = true;
            for (var j = i - d + 1; j <= i; j++)
            {
                if (!a[j].HasValue || !b[j].HasValue) { ok = false; break; }
                double x = (double)a[j]!.Value, y = (double)b[j]!.Value;
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            }
            if (!ok) continue;
            var cov = sxy - sx * sy / d;
            var vx = sxx - sx * sx / d;
            var vy = syy - sy * sy / d;
            if (vx <= 0 || vy <= 0) continue;
            r[i] = (decimal)(cov / Math.Sqrt(vx * vy));
        }
        return r;
    }

    // --- Serializzazione (S-expression) ------------------------------------------------------

    public string ToExpression()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    private void Write(StringBuilder sb)
    {
        switch (Op)
        {
            case AlphaOp.Var: sb.Append('$').Append(Field); return;
            case AlphaOp.Const: sb.Append(Const.ToString(CultureInfo.InvariantCulture)); return;
            default:
                sb.Append(Op).Append('(');
                for (var i = 0; i < Children.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    Children[i].Write(sb);
                }
                if (IsTimeOp(Op)) sb.Append(',').Append(Window);
                sb.Append(')');
                return;
        }
    }

    public static bool IsTimeOp(AlphaOp op) => op is AlphaOp.Ref or AlphaOp.Delta or AlphaOp.Mean
        or AlphaOp.Std or AlphaOp.TsMax or AlphaOp.TsMin or AlphaOp.TsRank or AlphaOp.Corr;

    public override string ToString() => ToExpression();
}
