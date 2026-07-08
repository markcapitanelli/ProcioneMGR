using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Alpha.Alpha158;

/// <summary>
/// Descrittore di un operatore Alpha158: codice tecnico, categoria, se è parametrizzato da un
/// orizzonte rolling, e la funzione di calcolo (causale) che riceve le serie e l'orizzonte.
/// </summary>
internal sealed record OpDescriptor(
    string Code,
    FactorCategory Category,
    bool HorizonBased,
    Func<Bars, int, decimal?[]> Compute);

/// <summary>
/// Un fattore alpha generato da un <see cref="OpDescriptor"/> a un orizzonte fisso. Implementa la
/// stessa interfaccia <see cref="IAlphaFactor"/> degli 8 fattori scritti a mano: si innesta senza
/// modifiche in <c>FactorEvaluator</c>, <c>DatasetBuilder</c>, <c>MlStrategy</c> e nella UML.
///
/// L'orizzonte è "cotto" nell'istanza (non un parametro runtime): ogni combinazione
/// operatore×orizzonte è una feature distinta con un <see cref="Name"/> univoco e stabile
/// (es. <c>A158_ROC_20</c>), così il round-trip di persistenza esistente
/// (<c>SavedFactorSpecDto</c> → <c>IAlphaFactorFactory.Create(Name)</c>) funziona senza cambiare
/// nulla: <see cref="ParameterDefinitions"/> è vuoto e il nome basta a ricostruire il fattore.
/// </summary>
public sealed class Alpha158Factor : IAlphaFactor
{
    private static readonly IReadOnlyList<FactorParameterDefinition> NoParams = Array.Empty<FactorParameterDefinition>();

    private readonly OpDescriptor _op;
    private readonly int _horizon;

    internal Alpha158Factor(OpDescriptor op, int horizon)
    {
        _op = op;
        _horizon = horizon;
    }

    /// <summary>Orizzonte rolling (in candele); 0 per i fattori di forma candela (KBAR), orizzonte-indipendenti.</summary>
    public int Horizon => _horizon;

    public string Name => _op.HorizonBased ? $"A158_{_op.Code}_{_horizon}" : $"A158_{_op.Code}";
    public string DisplayName => _op.HorizonBased ? $"{_op.Code}({_horizon})" : _op.Code;
    public FactorCategory Category => _op.Category;
    public IReadOnlyList<FactorParameterDefinition> ParameterDefinitions => NoParams;

    public IReadOnlyList<decimal?> Compute(IReadOnlyList<OhlcvData> candles, IReadOnlyDictionary<string, decimal> parameters)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var bars = new Bars(candles);
        return _op.Compute(bars, _horizon);
    }
}

/// <summary>
/// Catalogo Alpha158: pochi operatori rolling causali × più orizzonti, generati come istanze
/// <see cref="Alpha158Factor"/> invece di scrivere ~150 classi a mano (rif.
/// <c>docs/ROADMAP-QLIB.md §1.1</c>). Nessuna nuova infrastruttura di valutazione: il catalogo
/// alimenta gli stessi <c>FactorEvaluator</c>/<c>DatasetBuilder</c> già equivalenti ad Alphalens.
/// </summary>
public static class Alpha158Catalog
{
    /// <summary>Orizzonti rolling di default (come Alpha158 di Qlib): 5/10/20/30/60 candele.</summary>
    public static readonly int[] DefaultHorizons = [5, 10, 20, 30, 60];

    // Registro degli operatori. Ordine deliberato (forma candela → prezzo → correlazioni →
    // conteggi → volume) per una UI raggruppata leggibile.
    private static readonly OpDescriptor[] Operators =
    [
        // --- KBAR: forma della candela, orizzonte-indipendenti ---
        new("KMID",  FactorCategory.Technical,     false, (b, _) => RollingOps.Kmid(b)),
        new("KLEN",  FactorCategory.Technical,     false, (b, _) => RollingOps.Klen(b)),
        new("KMID2", FactorCategory.Technical,     false, (b, _) => RollingOps.Kmid2(b)),
        new("KUP",   FactorCategory.Technical,     false, (b, _) => RollingOps.Kup(b)),
        new("KUP2",  FactorCategory.Technical,     false, (b, _) => RollingOps.Kup2(b)),
        new("KLOW",  FactorCategory.Technical,     false, (b, _) => RollingOps.Klow(b)),
        new("KLOW2", FactorCategory.Technical,     false, (b, _) => RollingOps.Klow2(b)),
        new("KSFT",  FactorCategory.Technical,     false, (b, _) => RollingOps.Ksft(b)),
        new("KSFT2", FactorCategory.Technical,     false, (b, _) => RollingOps.Ksft2(b)),

        // --- Prezzo ---
        new("ROC",   FactorCategory.Momentum,      true,  (b, d) => RollingOps.Roc(b.Close, d)),
        new("MA",    FactorCategory.Technical,     true,  (b, d) => RollingOps.Ma(b.Close, d)),
        new("STD",   FactorCategory.Volatility,    true,  (b, d) => RollingOps.Std(b.Close, d)),
        new("BETA",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Beta(b.Close, d)),
        new("RSQR",  FactorCategory.Technical,     true,  (b, d) => RollingOps.Rsqr(b.Close, d)),
        new("RESI",  FactorCategory.Technical,     true,  (b, d) => RollingOps.Resi(b.Close, d)),
        new("MAX",   FactorCategory.Technical,     true,  (b, d) => RollingOps.Max(b.High, b.Close, d)),
        new("MIN",   FactorCategory.Technical,     true,  (b, d) => RollingOps.Min(b.Low, b.Close, d)),
        new("QTLU",  FactorCategory.Technical,     true,  (b, d) => RollingOps.Qtlu(b.Close, d)),
        new("QTLD",  FactorCategory.Technical,     true,  (b, d) => RollingOps.Qtld(b.Close, d)),
        new("RANK",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Rank(b.Close, d)),
        new("RSV",   FactorCategory.Momentum,      true,  (b, d) => RollingOps.Rsv(b.High, b.Low, b.Close, d)),
        new("IMAX",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Imax(b.High, d)),
        new("IMIN",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Imin(b.Low, d)),
        new("IMXD",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Imxd(b.High, b.Low, d)),

        // --- Correlazioni prezzo-volume ---
        new("CORR",  FactorCategory.Volume,        true,  (b, d) => RollingOps.Corr(b.Close, b.Volume, d)),
        new("CORD",  FactorCategory.Volume,        true,  (b, d) => RollingOps.Cord(b.Close, b.Volume, d)),

        // --- Conteggi/somme direzionali del prezzo ---
        new("CNTP",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Cntp(b.Close, d)),
        new("CNTN",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Cntn(b.Close, d)),
        new("CNTD",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Cntd(b.Close, d)),
        new("SUMP",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Sump(b.Close, d)),
        new("SUMN",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Sumn(b.Close, d)),
        new("SUMD",  FactorCategory.Momentum,      true,  (b, d) => RollingOps.Sumd(b.Close, d)),

        // --- Volume ---
        new("VMA",   FactorCategory.Volume,        true,  (b, d) => RollingOps.Vma(b.Volume, d)),
        new("VSTD",  FactorCategory.Volume,        true,  (b, d) => RollingOps.Vstd(b.Volume, d)),
        new("WVMA",  FactorCategory.Volume,        true,  (b, d) => RollingOps.Wvma(b.Close, b.Volume, d)),
        new("VSUMP", FactorCategory.Volume,        true,  (b, d) => RollingOps.Vsump(b.Volume, d)),
        new("VSUMN", FactorCategory.Volume,        true,  (b, d) => RollingOps.Vsumn(b.Volume, d)),
        new("VSUMD", FactorCategory.Volume,        true,  (b, d) => RollingOps.Vsumd(b.Volume, d)),
    ];

    /// <summary>Numero di operatori distinti (KBAR + rolling).</summary>
    public static int OperatorCount => Operators.Length;

    /// <summary>
    /// Genera l'intero catalogo: ogni operatore di forma candela una volta, ogni operatore rolling
    /// per ciascun orizzonte. Con gli orizzonti di default produce ~150 feature distinte.
    /// </summary>
    public static IReadOnlyList<IAlphaFactor> BuildCatalog(IEnumerable<int>? horizons = null)
    {
        var hs = (horizons ?? DefaultHorizons).Where(h => h >= 1).Distinct().OrderBy(h => h).ToArray();
        var list = new List<IAlphaFactor>(Operators.Length * Math.Max(1, hs.Length));
        foreach (var op in Operators)
        {
            if (!op.HorizonBased)
            {
                list.Add(new Alpha158Factor(op, 0));
            }
            else
            {
                foreach (var h in hs) list.Add(new Alpha158Factor(op, h));
            }
        }
        return list;
    }

    /// <summary>
    /// Ricostruisce un fattore Alpha158 dal suo <see cref="IAlphaFactor.Name"/>
    /// (es. <c>A158_ROC_20</c>, <c>A158_KMID</c>). Necessario perché <c>IAlphaFactorFactory.Create</c>
    /// deve poter riottenere per nome qualunque feature persistita in un <c>SavedMlModel</c>,
    /// per QUALSIASI orizzonte, non solo quelli di default.
    /// </summary>
    public static bool TryCreate(string name, out IAlphaFactor factor)
    {
        factor = null!;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("A158_", StringComparison.Ordinal)) return false;

        var parts = name.Split('_');
        if (parts.Length is < 2 or > 3) return false;

        var op = Array.Find(Operators, o => o.Code == parts[1]);
        if (op is null) return false;

        if (op.HorizonBased)
        {
            if (parts.Length != 3 || !int.TryParse(parts[2], out var h) || h < 1) return false;
            factor = new Alpha158Factor(op, h);
            return true;
        }

        if (parts.Length != 2) return false;
        factor = new Alpha158Factor(op, 0);
        return true;
    }
}
