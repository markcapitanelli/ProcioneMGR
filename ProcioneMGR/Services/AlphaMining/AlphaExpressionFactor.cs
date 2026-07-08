using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.AlphaMining;

/// <summary>
/// Adatta un albero di espressione (<see cref="AlphaNode"/>) all'interfaccia <see cref="IAlphaFactor"/>:
/// un alpha "minato" diventa così un fattore di prima classe, riusabile ovunque un
/// <c>IAlphaFactor</c> è consumato oggi (dataset ML, <c>MlStrategy</c>, valutazione IC), senza toccare
/// quei consumatori. Rif. <c>docs/ROADMAP-QLIB.md §1.7</c>.
///
/// Il <see cref="Name"/> incapsula l'espressione con prefisso <c>expr:</c> così che
/// <c>IAlphaFactorFactory.Create(Name)</c> possa ricostruirlo per parsing — round-trip identico a
/// quello degli altri fattori persistiti in un <c>SavedMlModel</c>.
/// </summary>
public sealed class AlphaExpressionFactor : IAlphaFactor
{
    /// <summary>Prefisso che marca un nome di fattore come espressione alpha serializzata.</summary>
    public const string NamePrefix = "expr:";

    private readonly AlphaNode _root;
    private readonly string _expression;

    public AlphaExpressionFactor(AlphaNode root)
    {
        _root = root;
        _expression = root.ToExpression();
    }

    public AlphaNode Root => _root;
    public string Expression => _expression;

    public string Name => NamePrefix + _expression;
    public string DisplayName => _expression;
    public FactorCategory Category => FactorCategory.Technical;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions => [];

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> parameters)
        => _root.Evaluate(candles);

    /// <summary>Ricostruisce il fattore dal nome "expr:&lt;espressione&gt;".</summary>
    public static AlphaExpressionFactor FromName(string name)
    {
        var expr = name.StartsWith(NamePrefix, StringComparison.Ordinal) ? name[NamePrefix.Length..] : name;
        return new AlphaExpressionFactor(AlphaExpressionParser.Parse(expr));
    }
}
