using ProcioneMGR.Data;
using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.AlphaMining;

/// <summary>Configurazione della ricerca genetica di alpha.</summary>
public sealed class MiningConfig
{
    public int PopulationSize { get; set; } = 200;
    public int Generations { get; set; } = 15;
    public int MaxDepth { get; set; } = 5;
    public int MaxSize { get; set; } = 40;
    public int TournamentSize { get; set; } = 4;
    public double CrossoverRate { get; set; } = 0.7;
    public double MutationRate { get; set; } = 0.3;

    /// <summary>Penalità di fitness per nodo: scoraggia formule complesse (anti-overfitting).</summary>
    public double ComplexityPenalty { get; set; } = 0.002;

    /// <summary>Finestre ammesse per gli operatori temporali.</summary>
    public int[] Windows { get; set; } = [2, 5, 10, 20, 30, 60];

    public int ForwardHorizon { get; set; } = 1;
    public int MinObservations { get; set; } = 50;
    public int TopN { get; set; } = 15;
    public int Seed { get; set; } = 42;
}

/// <summary>Un alpha sopravvissuto alla ricerca: espressione + diagnostica.</summary>
public sealed class MinedFactor
{
    public required string Expression { get; init; }
    public double SelectionIc { get; init; }
    public double Fitness { get; init; }
    public int Size { get; init; }
    public int Observations { get; init; }

    /// <summary>IC sull'holdout mai visto (valorizzato dalla verifica fuori campione).</summary>
    public double? HoldoutIc { get; set; }
}

/// <summary>
/// <b>Formulaic alpha mining</b> via programmazione genetica in C# puro (rif.
/// <c>docs/ROADMAP-QLIB.md §1.7</c>): evolve alberi di <see cref="AlphaNode"/> massimizzando |IC|
/// sul periodo di SELEZIONE, con penalità di complessità contro l'overfitting. Deterministico a
/// parità di <see cref="MiningConfig.Seed"/>.
///
/// Disciplina: la fitness è misurata SOLO sul periodo di selezione; le formule sopravvissute vanno
/// poi sottoposte al verdetto su un holdout mai visto (<see cref="EvaluateIc"/>), come per ogni
/// strategia/modello della piattaforma — nessun percorso con standard più bassi.
///
/// Deviazione dichiarata dalla roadmap: nessuna dipendenza <c>GeneticSharp</c>. La GP su alberi a
/// dimensione variabile non mappa bene sui cromosomi a lunghezza fissa di quella libreria; una
/// implementazione diretta è più semplice, deterministica e coerente col principio "C# puro".
/// </summary>
public sealed class GeneticAlphaMiner
{
    private static readonly AlphaOp[] UnaryOps = [AlphaOp.Neg, AlphaOp.Abs, AlphaOp.Log, AlphaOp.Sign];
    private static readonly AlphaOp[] BinaryOps = [AlphaOp.Add, AlphaOp.Sub, AlphaOp.Mul, AlphaOp.Div];
    private static readonly AlphaOp[] TimeUnaryOps = [AlphaOp.Ref, AlphaOp.Delta, AlphaOp.Mean, AlphaOp.Std, AlphaOp.TsMax, AlphaOp.TsMin, AlphaOp.TsRank];
    private static readonly AlphaOp[] TimeBinaryOps = [AlphaOp.Corr];
    private static readonly AlphaOp[] AllFunctions = [.. UnaryOps, .. BinaryOps, .. TimeUnaryOps, .. TimeBinaryOps];
    private static readonly decimal[] Constants = [-2m, -1m, -0.5m, 0.5m, 1m, 2m, 5m];

    public IReadOnlyList<MinedFactor> Mine(IReadOnlyList<OhlcvData> candles, MiningConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        config ??= new MiningConfig();
        var rng = new Random(config.Seed);

        // Rendimenti forward precalcolati UNA volta (target dell'IC), riusati per ogni individuo.
        var forward = ForwardReturns(candles, config.ForwardHorizon);

        // Popolazione iniziale casuale.
        var population = new List<AlphaNode>(config.PopulationSize);
        for (var i = 0; i < config.PopulationSize; i++) population.Add(RandomTree(rng, config.MaxDepth, config));

        var scored = Evaluate(population, candles, forward, config);

        for (var gen = 0; gen < config.Generations; gen++)
        {
            ct.ThrowIfCancellationRequested();
            scored.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

            var next = new List<AlphaNode>(config.PopulationSize);
            next.Add(scored[0].Tree); // elitismo: il migliore sopravvive intatto
            if (scored.Count > 1) next.Add(scored[1].Tree);

            while (next.Count < config.PopulationSize)
            {
                var parent = Tournament(scored, rng, config.TournamentSize).Tree;
                AlphaNode child;
                if (rng.NextDouble() < config.CrossoverRate)
                {
                    var other = Tournament(scored, rng, config.TournamentSize).Tree;
                    child = Crossover(parent, other, rng, config);
                }
                else
                {
                    child = parent.Clone();
                }
                if (rng.NextDouble() < config.MutationRate) child = Mutate(child, rng, config);

                if (child.Size() > config.MaxSize || child.Depth() > config.MaxDepth + 2)
                    child = RandomTree(rng, config.MaxDepth, config); // taglia gli obesi
                next.Add(child);
            }

            scored = Evaluate(next, candles, forward, config);
        }

        scored.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

        // Top-N distinti per espressione, scartando gli invalidi.
        var seen = new HashSet<string>();
        var result = new List<MinedFactor>();
        foreach (var s in scored)
        {
            if (double.IsNegativeInfinity(s.Fitness)) continue;
            var expr = s.Tree.ToExpression();
            if (!seen.Add(expr)) continue;
            result.Add(new MinedFactor
            {
                Expression = expr,
                SelectionIc = s.Ic,
                Fitness = s.Fitness,
                Size = s.Tree.Size(),
                Observations = s.Observations,
            });
            if (result.Count >= config.TopN) break;
        }
        return result;
    }

    /// <summary>IC (Spearman) di un'espressione su un set di candele; <paramref name="obs"/> = coppie valide.</summary>
    public double EvaluateIc(AlphaNode node, IReadOnlyList<OhlcvData> candles, int horizon, int minObs, out int obs)
    {
        var forward = ForwardReturns(candles, horizon);
        return IcOf(node, candles, forward, minObs, out obs);
    }

    /// <summary>
    /// PBO (Probability of Backtest Overfitting) via CSCV sul pannello delle formule minate, valutate
    /// sulle STESSE candele di selezione (asse temporale comune ⇒ setup CSCV corretto). Per ogni
    /// formula la serie per-periodo è il "payoff" valore×rendimento-forward (scala-invariante: il PBO
    /// usa lo Sharpe del payoff). Risponde a: <i>la scelta della formula migliore regge fuori campione
    /// o è guidata dall'overfitting?</i> — complementare al verdetto IC selezione/holdout già presente.
    /// null se meno di 2 formule valide o serie più corta di <paramref name="partitions"/>. Deterministico.
    /// </summary>
    public Validation.PboResult? ComputeSelectionPbo(
        IReadOnlyList<OhlcvData> candles, IReadOnlyList<string> expressions, int horizon, int partitions = 10)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(expressions);
        if (candles.Count < partitions) return null;

        var forward = ForwardReturns(candles, horizon);
        var panel = new List<IReadOnlyList<double>>();
        foreach (var expr in expressions)
        {
            AlphaNode node;
            try { node = AlphaExpressionParser.Parse(expr); }
            catch (FormatException) { continue; } // formula non ricostruibile: la si salta
            var values = node.Evaluate(candles);

            var payoff = new double[candles.Count];
            var nonZero = 0;
            for (var i = 0; i < payoff.Length; i++)
            {
                if (values[i].HasValue && forward[i].HasValue)
                {
                    payoff[i] = (double)values[i]!.Value * (double)forward[i]!.Value;
                    if (payoff[i] != 0.0) nonZero++;
                }
            }
            if (nonZero >= 2) panel.Add(payoff);
        }

        if (panel.Count < 2) return null;
        return Validation.BacktestOverfitting.ProbabilityOfOverfitting(panel, partitions);
    }

    // --- Valutazione -------------------------------------------------------------------------

    private sealed class Scored
    {
        public required AlphaNode Tree { get; init; }
        public double Ic { get; init; }
        public double Fitness { get; init; }
        public int Observations { get; init; }
    }

    private List<Scored> Evaluate(List<AlphaNode> population, IReadOnlyList<OhlcvData> candles, decimal?[] forward, MiningConfig config)
    {
        var result = new List<Scored>(population.Count);
        foreach (var tree in population)
        {
            var ic = IcOf(tree, candles, forward, config.MinObservations, out var obs);
            double fitness;
            if (obs < config.MinObservations || double.IsNaN(ic))
            {
                fitness = double.NegativeInfinity;
            }
            else
            {
                fitness = Math.Abs(ic) - config.ComplexityPenalty * tree.Size();
            }
            result.Add(new Scored { Tree = tree, Ic = ic, Fitness = fitness, Observations = obs });
        }
        return result;
    }

    private static double IcOf(AlphaNode node, IReadOnlyList<OhlcvData> candles, decimal?[] forward, int minObs, out int obs)
    {
        var values = node.Evaluate(candles);
        var fx = new List<double>();
        var fy = new List<double>();
        var n = Math.Min(values.Length, forward.Length);
        for (var i = 0; i < n; i++)
        {
            if (values[i].HasValue && forward[i].HasValue)
            {
                fx.Add((double)values[i]!.Value);
                fy.Add((double)forward[i]!.Value);
            }
        }
        obs = fx.Count;
        if (obs < Math.Max(3, minObs)) return 0d;

        // Espressione degenere (valore costante): Spearman non definito → IC 0.
        if (fx.Distinct().Count() < 2) return 0d;
        return Correlation.Spearman(fx, fy);
    }

    private static decimal?[] ForwardReturns(IReadOnlyList<OhlcvData> candles, int horizon)
    {
        var n = candles.Count;
        var r = new decimal?[n];
        if (horizon < 1) return r;
        for (var i = 0; i + horizon < n; i++)
        {
            var now = candles[i].Close;
            if (now > 0m) r[i] = (candles[i + horizon].Close - now) / now;
        }
        return r;
    }

    // --- Operatori genetici ------------------------------------------------------------------

    private static Scored Tournament(List<Scored> scored, Random rng, int size)
    {
        var best = scored[rng.Next(scored.Count)];
        for (var i = 1; i < size; i++)
        {
            var candidate = scored[rng.Next(scored.Count)];
            if (candidate.Fitness > best.Fitness) best = candidate;
        }
        return best;
    }

    private AlphaNode Crossover(AlphaNode a, AlphaNode b, Random rng, MiningConfig config)
    {
        var clone = a.Clone();
        var slots = CollectSlots(clone);
        var target = slots[rng.Next(slots.Count)];

        var donorNodes = CollectSlots(b);
        var donor = donorNodes[rng.Next(donorNodes.Count)].Node.Clone();

        if (target.Parent is null) return donor; // crossover alla radice = sottoalbero del donatore
        target.Parent.Children[target.Index] = donor;
        return clone;
    }

    private AlphaNode Mutate(AlphaNode tree, Random rng, MiningConfig config)
    {
        var clone = tree.Clone();
        var slots = CollectSlots(clone);
        var target = slots[rng.Next(slots.Count)];

        var replacement = rng.NextDouble() < 0.5
            ? RandomTree(rng, Math.Max(2, config.MaxDepth - 1), config) // sostituzione di sottoalbero
            : PointMutate(target.Node, rng, config);                     // mutazione puntuale

        if (target.Parent is null) return replacement;
        target.Parent.Children[target.Index] = replacement;
        return clone;
    }

    private AlphaNode PointMutate(AlphaNode node, Random rng, MiningConfig config)
    {
        switch (node.Op)
        {
            case AlphaOp.Var:
                return AlphaNode.Variable(AlphaNode.Fields[rng.Next(AlphaNode.Fields.Length)]);
            case AlphaOp.Const:
                return AlphaNode.Constant(Constants[rng.Next(Constants.Length)]);
            default:
                // Cambia la finestra (se temporale) o l'operatore mantenendo aritmetica compatibile.
                if (AlphaNode.IsTimeOp(node.Op))
                {
                    var pool = node.Children.Length == 2 ? TimeBinaryOps : TimeUnaryOps;
                    return new AlphaNode { Op = pool[rng.Next(pool.Length)], Window = config.Windows[rng.Next(config.Windows.Length)], Children = node.Children };
                }
                var ops = node.Children.Length == 2 ? BinaryOps : UnaryOps;
                return new AlphaNode { Op = ops[rng.Next(ops.Length)], Children = node.Children };
        }
    }

    // --- Generazione casuale -----------------------------------------------------------------

    private AlphaNode RandomTree(Random rng, int maxDepth, MiningConfig config)
    {
        if (maxDepth <= 1 || rng.NextDouble() < 0.3) return RandomTerminal(rng);

        var op = AllFunctions[rng.Next(AllFunctions.Length)];
        if (Array.IndexOf(UnaryOps, op) >= 0)
            return AlphaNode.Unary(op, RandomTree(rng, maxDepth - 1, config));
        if (Array.IndexOf(BinaryOps, op) >= 0)
            return AlphaNode.Binary(op, RandomTree(rng, maxDepth - 1, config), RandomTree(rng, maxDepth - 1, config));
        if (Array.IndexOf(TimeUnaryOps, op) >= 0)
            return AlphaNode.TimeUnary(op, RandomTree(rng, maxDepth - 1, config), config.Windows[rng.Next(config.Windows.Length)]);
        return AlphaNode.TimeBinary(op, RandomTree(rng, maxDepth - 1, config), RandomTree(rng, maxDepth - 1, config), config.Windows[rng.Next(config.Windows.Length)]);
    }

    private AlphaNode RandomTerminal(Random rng)
        => rng.NextDouble() < 0.65
            ? AlphaNode.Variable(AlphaNode.Fields[rng.Next(AlphaNode.Fields.Length)])
            : AlphaNode.Constant(Constants[rng.Next(Constants.Length)]);

    // --- Navigazione dell'albero -------------------------------------------------------------

    private sealed class NodeSlot
    {
        public required AlphaNode Node { get; init; }
        public AlphaNode? Parent { get; init; }
        public int Index { get; init; }
    }

    /// <summary>Tutti i nodi come "slot" (nodo + posizione nel padre), per sostituzioni funzionali.</summary>
    private static List<NodeSlot> CollectSlots(AlphaNode root)
    {
        var list = new List<NodeSlot> { new() { Node = root, Parent = null, Index = -1 } };
        void Walk(AlphaNode node)
        {
            for (var i = 0; i < node.Children.Length; i++)
            {
                list.Add(new NodeSlot { Node = node.Children[i], Parent = node, Index = i });
                Walk(node.Children[i]);
            }
        }
        Walk(root);
        return list;
    }
}
