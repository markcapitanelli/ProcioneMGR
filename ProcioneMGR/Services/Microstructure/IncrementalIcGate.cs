using ProcioneMGR.Services.Alpha;

namespace ProcioneMGR.Services.Microstructure;

// =============================================================================================
//  [D3, il gate] "Il book aggiunge informazione OLTRE al proxy che già abbiamo?"
//
//  La domanda non è "l'OFI ha un IC diverso da zero". Un IC diverso da zero non basta: se l'OFI
//  dicesse esattamente ciò che dice già TakerImbalanceFactor (che costa zero, esce dalle klines),
//  raccogliere il book sarebbe pagare per un doppione. La domanda giusta è INCREMENTALE, e la
//  misura giusta è la correlazione PARZIALE: quanto informa il candidato una volta rimosso ciò che
//  il proxy spiega già.
//
//  TRE GUARDIE, tutte imparate a spese della piattaforma:
//
//  1. NULLO A ROTAZIONE CIRCOLARE, come nel gate C1.b. Ruotare la serie del candidato conserva la
//     sua autocorrelazione (un segnale persistente resta persistente) e distrugge solo
//     l'allineamento temporale col rendimento futuro: è il nullo giusto per una serie con memoria,
//     dove un permutation test ingenuo darebbe una soglia troppo permissiva.
//
//  2. NULLO DEL MIGLIORE (family-wise), non del singolo. Si provano più candidati su più orizzonti:
//     il massimo di N misure rumorose è più grande di ciascuna, e confrontare il migliore con la
//     soglia di UNA misura è il modo classico di fabbricare una scoperta. A ogni giro del nullo si
//     ruota TUTTO con lo stesso spostamento e si tiene il MASSIMO |IC parziale| della famiglia: la
//     soglia diventa quella del migliore, non quella di uno qualunque. È la stessa correzione che
//     il DSR applica al numero di tentativi.
//
//  3. PAVIMENTO ECONOMICO **E** STATISTICO. La soglia è max(|IC| minimo economico, 1,96/√n), la
//     stessa regola del monitor di deriva D2 — dove un |IC| di 0,04 su 300 osservazioni era rumore
//     puro promosso a segnale. Su un pilota di microstruttura n è enorme (decine di migliaia di
//     barre), quindi il pavimento statistico è piccolissimo e a vincolare resta quello economico:
//     un IC che non paga i costi non è un segnale, è una curiosità.
//
//  MASCHERA COMUNE. Tutti i candidati girano sulle STESSE barre: se ciascuno usasse le proprie,
//  quello con più dati validi vincerebbe in parte per numerosità, e i confronti non sarebbero fra
//  loro comparabili.
// =============================================================================================

/// <summary>Un candidato da confrontare col proxy: valori allineati alle barre, <c>NaN</c> dove manca.</summary>
public sealed record IcCandidate(string Name, IReadOnlyList<double> Values);

/// <summary>Parametri del gate. I default sono quelli già in uso altrove nella piattaforma.</summary>
public sealed class IncrementalIcConfig
{
    /// <summary>|IC| minimo per contare come informazione economicamente rilevante (0,02 = soglia storica di /feature-selection).</summary>
    public double MinAbsIc { get; set; } = 0.02;

    /// <summary>Errori standard richiesti perché un IC sia distinguibile da zero.</summary>
    public double NoiseFloorZ { get; set; } = 1.96;

    /// <summary>Giri del nullo a rotazione (200 = come il giudice del gemello nullo).</summary>
    public int NullDraws { get; set; } = 200;

    /// <summary>Percentile del nullo, tenuto per il REPORT (la decisione passa dal p-value, vedi sotto).</summary>
    public double NullPercentile { get; set; } = 99;

    /// <summary>
    /// p-value massimo accettato, calcolato come <c>(1 + #{nullo ≥ osservato}) / (giri + 1)</c>.
    ///
    /// PERCHÉ IL p-value E NON IL PERCENTILE. La prima versione confrontava l'|IC parziale| col 99°
    /// percentile del nullo, e il test del rumore l'ha bocciata: **1 falso positivo su 30 semi di
    /// puro rumore** (3,3%, oltre tre volte il livello nominale dell'1%). La causa non è nel
    /// concetto ma nella stima: un 99° percentile ricavato da 200 giri sta fra il 2° e il 3° valore
    /// più grande, cioè è una stima rumorosissima, e quando cade basso il confronto passa. Il
    /// p-value empirico con la correzione +1 (Phipson-Smyth) usa TUTTI i giri e non può mai valere
    /// zero: con 200 giri, p ≤ 0,01 richiede che NESSUN giro del nullo raggiunga l'osservato — un
    /// criterio più severo e, soprattutto, stabile.
    /// </summary>
    public double MaxNullPValue { get; set; } = 0.01;

    /// <summary>Seme del generatore: la misura deve essere riproducibile bit per bit.</summary>
    public int Seed { get; set; } = 20260728;

    /// <summary>Minimo di osservazioni sotto cui non si emette verdetto.</summary>
    public int MinObservations { get; set; } = 500;

    /// <summary>
    /// Costo di un giro completo (entrata + uscita) in punti base. Attiva il SECONDO LIVELLO del
    /// verdetto: non «questo candidato informa?» ma «informa abbastanza da pagarsi il giro?».
    ///
    /// PERCHÉ È NEL GATE E NON NEL CHIAMANTE. Nella prima versione il gate diceva "AGGIUNGE" e la
    /// traduzione in punti base la faceva la fase CLI, dieci righe più in basso: due verdetti separati
    /// che si leggono a distanza di un rigo, e chi legge solo il primo capisce «si può operare». Su
    /// dati veri i due dicevano cose opposte — |IC| 0,04 con p-value 0,005 (informa) ed edge lordo
    /// 0,45 bp contro 4 bp di costo (non paga, manca un fattore 9). Adesso è un verdetto solo, a due
    /// livelli.
    ///
    /// Default 4 bp = 0,02% per lato, tariffa maker Binance USD-M. <c>null</c> disattiva il livello
    /// economico: va usato quando i rendimenti forward NON sono in unità di rendimento (per esempio
    /// nei test sintetici), perché lì «punti base» non vuol dire niente.
    /// </summary>
    public double? RoundTripCostBps { get; set; } = 4.0;
}

/// <summary>Esito per un candidato su un orizzonte.</summary>
public sealed record IncrementalIcOutcome(
    string Candidate,
    int HorizonBars,
    int Observations,
    double RawIc,
    double ProxyIc,
    double PartialIc,
    double CorrelationWithProxy,
    double Threshold,
    double NullBestPercentile,
    double NullPValue,
    bool AddsInformation,
    string Message)
{
    /// <summary>Deviazione standard dei rendimenti a questo orizzonte, in punti base (0 se non calcolabile).</summary>
    public double ForwardSigmaBps { get; init; }

    /// <summary>Edge lordo atteso per un segnale a 1σ, in punti base: |IC parziale| × σ.</summary>
    public double GrossEdgeBps { get; init; }

    /// <summary>|IC| che servirebbe perché l'edge lordo pareggi il costo del giro (0 = livello economico spento).</summary>
    public double IcRequiredByCosts { get; init; }

    /// <summary>
    /// Vero solo se il candidato informa **e** l'edge lordo copre il costo del giro. È il secondo
    /// livello: senza, un "AGGIUNGE" statistico si legge come "si può operare".
    /// </summary>
    public bool IsTradable { get; init; }
}

/// <summary>Verdetto complessivo del gate.</summary>
public sealed record IncrementalIcReport(
    int Observations,
    int Candidates,
    int Horizons,
    int NullDraws,
    double NullBestPercentile,
    IReadOnlyList<IncrementalIcOutcome> Outcomes,
    bool AnyAddsInformation,
    string Verdict)
{
    /// <summary>Vero se almeno un candidato supera anche il livello economico.</summary>
    public bool AnyTradable { get; init; }
}

/// <summary>Misura l'informazione INCREMENTALE di uno o più candidati sopra un proxy già disponibile.</summary>
public static class IncrementalIcGate
{
    /// <summary>
    /// Correlazione parziale di Spearman fra <paramref name="x"/> e <paramref name="y"/> tenuto conto
    /// di <paramref name="z"/>:
    /// <code>ρ(x,y|z) = (ρxy − ρxz·ρyz) / √((1 − ρxz²)(1 − ρyz²))</code>
    /// Sui ranghi, come tutto l'IC della piattaforma. Restituisce 0 se il denominatore degenera
    /// (candidato identico al proxy: in quel caso "informazione incrementale" non è definita, ed è
    /// giusto che il gate legga zero).
    /// </summary>
    public static double PartialSpearman(IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> z)
    {
        var rxy = Correlation.Spearman(x, y);
        var rxz = Correlation.Spearman(x, z);
        var ryz = Correlation.Spearman(y, z);

        var denom = (1 - rxz * rxz) * (1 - ryz * ryz);
        if (denom <= 1e-12) return 0d;
        return (rxy - rxz * ryz) / Math.Sqrt(denom);
    }

    /// <summary>
    /// La stessa quantità per una strada diversa: si regredisce il rango del candidato su quello del
    /// proxy, si tiene il residuo e lo si correla col rendimento. Esiste per i test — due strade
    /// indipendenti che danno lo stesso numero sono la prova che nessuna delle due è sbagliata, ed è
    /// il metodo con cui è stato verificato TreeSHAP in D1.
    /// </summary>
    public static double PartialSpearmanByResidual(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<double> z)
        => PartialSpearmanMulti(x, y, [z]);

    /// <summary>
    /// Correlazione parziale con PIÙ controlli, per residui: si toglie da candidato e rendimento tutto
    /// ciò che l'insieme dei controlli spiega, poi si correla quel che resta.
    ///
    /// PERCHÉ SERVIVA. Con un solo controllo (il proxy trade-flow) un candidato di book può risultare
    /// informativo semplicemente perché è il **rendimento recente travestito**: lo sbilanciamento di
    /// profondità cambia quando il prezzo si muove, e il reversal di brevissimo periodo è un effetto
    /// noto — già misurato dalla piattaforma e già non redditizio dopo i costi. Aggiungere il
    /// rendimento passato fra i controlli trasforma «aggiunge informazione» in «aggiunge informazione
    /// che non fosse già nel flusso taker né nel movimento appena avvenuto».
    ///
    /// I controlli vengono ortogonalizzati fra loro (Gram-Schmidt) prima della proiezione: con
    /// controlli correlati — e proxy e rendimento passato lo sono — proiettare uno alla volta senza
    /// ortogonalizzare lascerebbe dentro una parte di ciò che si vuole togliere.
    /// </summary>
    public static double PartialSpearmanMulti(
        IReadOnlyList<double> x, IReadOnlyList<double> y, IReadOnlyList<IReadOnlyList<double>> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var n = Math.Min(x.Count, y.Count);
        foreach (var c in controls) n = Math.Min(n, c.Count);
        if (n < 3) return 0d;

        var basis = BuildBasis(controls, n);
        var residX = ResidualizeAgainst(Correlation.Ranks(x, n), basis, n);
        var residY = ResidualizeAgainst(Correlation.Ranks(y, n), basis, n);
        return Correlation.Pearson(residX, residY);
    }

    /// <summary>
    /// Base ortogonale dei controlli (Gram-Schmidt sui ranghi). Estratta perché nel nullo la si
    /// costruisce **una volta sola** invece che a ogni giro: i controlli non cambiano mai.
    /// </summary>
    private static List<double[]> BuildBasis(IReadOnlyList<IReadOnlyList<double>> controls, int n)
    {
        var basis = new List<double[]>(controls.Count);
        foreach (var c in controls)
        {
            var vector = Correlation.Ranks(c, n);
            foreach (var b in basis) vector = Residualize(vector, b, n);
            if (Norm(vector, n) > 1e-9) basis.Add(vector); // un controllo ridondante non aggiunge nulla
        }
        return basis;
    }

    private static double[] ResidualizeAgainst(double[] vector, List<double[]> basis, int n)
    {
        foreach (var b in basis) vector = Residualize(vector, b, n);
        return vector;
    }

    private static double Norm(double[] v, int n)
    {
        double mean = 0;
        for (var i = 0; i < n; i++) mean += v[i];
        mean /= n;
        double sum = 0;
        for (var i = 0; i < n; i++) { var d = v[i] - mean; sum += d * d; }
        return Math.Sqrt(sum / n);
    }

    private static double[] Residualize(double[] target, double[] control, int n)
    {
        double mt = 0, mc = 0;
        for (var i = 0; i < n; i++) { mt += target[i]; mc += control[i]; }
        mt /= n; mc /= n;

        double sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            var dc = control[i] - mc;
            sxy += dc * (target[i] - mt);
            sxx += dc * dc;
        }
        var beta = sxx > 1e-12 ? sxy / sxx : 0d;

        var resid = new double[n];
        for (var i = 0; i < n; i++) resid[i] = target[i] - mt - beta * (control[i] - mc);
        return resid;
    }

    /// <summary>
    /// Esegue il gate. <paramref name="forwardByHorizon"/> associa a ogni orizzonte (in barre) il
    /// rendimento forward allineato alle barre; <c>NaN</c> dove non esiste.
    /// </summary>
    public static IncrementalIcReport Run(
        IReadOnlyList<double> proxy,
        IReadOnlyDictionary<int, IReadOnlyList<double>> forwardByHorizon,
        IReadOnlyList<IcCandidate> candidates,
        IncrementalIcConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        return Run([new IcCandidate("proxy", proxy)], forwardByHorizon, candidates, config);
    }

    /// <summary>
    /// Come sopra, ma con PIÙ controlli: il candidato deve aggiungere informazione oltre a tutti
    /// quanti. Il primo controllo è quello che i report chiamano "proxy".
    /// </summary>
    public static IncrementalIcReport Run(
        IReadOnlyList<IcCandidate> controls,
        IReadOnlyDictionary<int, IReadOnlyList<double>> forwardByHorizon,
        IReadOnlyList<IcCandidate> candidates,
        IncrementalIcConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(forwardByHorizon);
        ArgumentNullException.ThrowIfNull(candidates);
        config ??= new IncrementalIcConfig();

        if (controls.Count == 0)
        {
            throw new ArgumentException("Serve almeno un controllo: senza, la domanda 'aggiunge oltre a cosa?' non ha senso.", nameof(controls));
        }
        var proxy = controls[0].Values;

        var horizons = forwardByHorizon.Keys.OrderBy(h => h).ToList();
        if (candidates.Count == 0 || horizons.Count == 0)
        {
            return new IncrementalIcReport(0, candidates.Count, horizons.Count, 0, 0, [], false,
                "Niente da misurare: nessun candidato o nessun orizzonte.");
        }

        // Maschera comune: una barra vale solo se TUTTI i controlli, TUTTI i candidati e TUTTI gli
        // orizzonti hanno un valore. Costa qualche barra, compra la comparabilità.
        var n = proxy.Count;
        var keep = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            if (controls.Any(c => i >= c.Values.Count || double.IsNaN(c.Values[i]))) continue;
            if (candidates.Any(c => i >= c.Values.Count || double.IsNaN(c.Values[i]))) continue;
            if (horizons.Any(h => i >= forwardByHorizon[h].Count || double.IsNaN(forwardByHorizon[h][i]))) continue;
            keep.Add(i);
        }

        if (keep.Count < config.MinObservations)
        {
            return new IncrementalIcReport(keep.Count, candidates.Count, horizons.Count, 0, 0, [], false,
                $"Osservazioni insufficienti ({keep.Count} < {config.MinObservations}): nessun verdetto.");
        }

        var obs = keep.Count;
        var ctrl = controls.Select(c => (IReadOnlyList<double>)keep.Select(i => c.Values[i]).ToArray()).ToList();
        var p = (double[])ctrl[0];
        var cands = candidates.Select(c => (c.Name, Values: keep.Select(i => c.Values[i]).ToArray())).ToList();
        var fwd = horizons.ToDictionary(h => h, h => keep.Select(i => forwardByHorizon[h][i]).ToArray());

        // --- Nullo del MIGLIORE: stesso spostamento per tutta la famiglia a ogni giro ---
        //
        // PRECALCOLO. La versione ingenua rifaceva a ogni giro tutto il lavoro: ranghi dei controlli,
        // ranghi del rendimento, ranghi del candidato ruotato. Su 43.000 barre × 200 giri × 12 coppie
        // sono ~14.000 ordinamenti, cioè un quarto d'ora per simbolo. Qui:
        //  · la base dei controlli e i residui del rendimento non dipendono dal giro → una volta sola;
        //  · **i ranghi di una serie RUOTATA sono i ranghi ruotati**: il rango dipende solo
        //    dall'ordine relativo, e una rotazione è una permutazione delle posizioni. Quindi si
        //    ordina il candidato una volta e nel ciclo si ruota il vettore dei ranghi.
        // Restano O(n) per giro invece di O(n log n) ripetuto: misurato, 62 secondi contro ~15 minuti
        // per simbolo. Che i numeri siano gli STESSI non è una speranza: un test confronta questo
        // nullo con quello ingenuo (ranghi ricalcolati a ogni giro) e pretende la stessa soglia —
        // un'ottimizzazione che cambia il risultato è un bug, non un'ottimizzazione.
        var basis = BuildBasis(ctrl, obs);
        var residY = horizons.ToDictionary(h => h, h => ResidualizeAgainst(Correlation.Ranks(fwd[h], obs), basis, obs));
        var candRanks = cands.Select(c => Correlation.Ranks(c.Values, obs)).ToList();

        var rnd = new Random(config.Seed);
        var nullBest = new List<double>(config.NullDraws);
        for (var draw = 0; draw < config.NullDraws; draw++)
        {
            var shift = rnd.Next(1, obs);
            var best = 0d;
            foreach (var ranks in candRanks)
            {
                var residX = ResidualizeAgainst(Rotate(ranks, shift), basis, obs);
                foreach (var h in horizons)
                {
                    var ic = Math.Abs(Correlation.Pearson(residX, residY[h]));
                    if (ic > best) best = ic;
                }
            }
            nullBest.Add(best);
        }
        var nullThreshold = Percentile(nullBest, config.NullPercentile);

        var floor = Math.Max(config.MinAbsIc, config.NoiseFloorZ / Math.Sqrt(obs));

        // Scala dei rendimenti per orizzonte: è ciò che trasforma un IC in punti base. Si misura sui
        // dati, non si assume — la volatilità a 1 minuto e quella a 5 non stanno in rapporto fisso.
        var sigmaBps = horizons.ToDictionary(h => h, h => StdDev(fwd[h]) * 10_000);

        var outcomes = new List<IncrementalIcOutcome>(cands.Count * horizons.Count);

        foreach (var (name, values) in cands)
        {
            var withProxy = Correlation.Spearman(values, p);
            foreach (var h in horizons)
            {
                var raw = Correlation.Spearman(values, fwd[h]);
                var proxyIc = Correlation.Spearman(p, fwd[h]);
                var partial = PartialSpearmanMulti(values, fwd[h], ctrl);

                // p-value family-wise con correzione +1: quanti giri del nullo del MIGLIORE arrivano
                // almeno dove è arrivato questo candidato.
                var atLeast = nullBest.Count(v => v >= Math.Abs(partial));
                var pValue = (1.0 + atLeast) / (config.NullDraws + 1.0);

                var beatsFloor = Math.Abs(partial) >= floor;
                var beatsNull = pValue <= config.MaxNullPValue;
                var adds = beatsFloor && beatsNull;

                // --- Secondo livello: quel che informa si paga il giro? ---
                var sigma = sigmaBps[h];
                var edgeBps = Math.Abs(partial) * sigma;
                var icRequired = config.RoundTripCostBps is { } cost && sigma > 0 ? cost / sigma : 0d;
                var tradable = adds && icRequired > 0 && Math.Abs(partial) >= icRequired;

                var economics = config.RoundTripCostBps is { } c && sigma > 0
                    ? tradable
                        ? $" E PAGA IL GIRO: edge lordo {edgeBps:F2} bp contro {c:F0} bp di costo."
                        : $" ma NON paga il giro: edge lordo {edgeBps:F2} bp contro {c:F0} bp di costo (servirebbe |IC| ≥ {icRequired:F3}, manca un fattore {(edgeBps > 0 ? c / edgeBps : 0):F0}×)."
                    : string.Empty;

                var message = adds
                    ? $"AGGIUNGE informazione: |IC parziale| {Math.Abs(partial):F4} sopra la soglia {floor:F4}, p-value del nullo {pValue:F4} ≤ {config.MaxNullPValue:F3}.{economics}"
                    : !beatsFloor
                        ? $"Non aggiunge: |IC parziale| {Math.Abs(partial):F4} sotto la soglia operativa {floor:F4} (max fra minimo economico {config.MinAbsIc:F3} e pavimento di rumore {config.NoiseFloorZ / Math.Sqrt(obs):F4})."
                        : $"Non aggiunge: |IC parziale| {Math.Abs(partial):F4} dentro ciò che produce il caso (p-value {pValue:F4} > {config.MaxNullPValue:F3}; {config.NullPercentile:F0}° percentile del nullo del migliore {nullThreshold:F4}).";

                outcomes.Add(new IncrementalIcOutcome(
                    name, h, obs, raw, proxyIc, partial, withProxy, floor, nullThreshold, pValue, adds, message)
                {
                    ForwardSigmaBps = sigma,
                    GrossEdgeBps = edgeBps,
                    IcRequiredByCosts = icRequired,
                    IsTradable = tradable,
                });
            }
        }

        var ordered = outcomes.OrderByDescending(o => Math.Abs(o.PartialIc)).ToList();
        var any = ordered.Any(o => o.AddsInformation);
        var anyTradable = ordered.Any(o => o.IsTradable);

        // VERDETTO A DUE LIVELLI. Le tre uscite sono deliberatamente distinte, perché "informa" e
        // "conviene" sono due domande diverse e confonderle è il modo classico di schierare una
        // strategia che perde: su dati veri il primo livello è passato e il secondo no, di 9 volte.
        var strongest = ordered[0];
        var verdict = !any
            ? $"NEGATIVO: nessun candidato aggiunge informazione oltre i controlli. Il migliore è {strongest.Candidate} "
              + $"a {strongest.HorizonBars} barre con |IC parziale| {Math.Abs(strongest.PartialIc):F4} "
              + $"(p-value {strongest.NullPValue:F4}), contro soglia {floor:F4}."
            : anyTradable
                ? $"POSITIVO E OPERABILE: {ordered.First(o => o.IsTradable).Candidate} aggiunge informazione "
                  + $"e l'edge lordo copre il costo del giro. Da confermare col collaudo di sempre (holdout, DSR, PBO)."
                : $"INFORMA MA NON È OPERABILE: {ordered.First(o => o.AddsInformation).Candidate} aggiunge informazione "
                  + $"oltre i controlli, però l'edge lordo ({ordered.First(o => o.AddsInformation).GrossEdgeBps:F2} bp) "
                  + $"non paga il giro ({config.RoundTripCostBps:F0} bp). Utile per l'ESECUZIONE, dove il giro è già "
                  + $"pagato; non per decidere un ingresso.";

        return new IncrementalIcReport(obs, cands.Count, horizons.Count, config.NullDraws, nullThreshold, ordered, any, verdict)
        {
            AnyTradable = anyTradable,
        };
    }

    /// <summary>Deviazione standard di popolazione, per portare l'IC in punti base.</summary>
    private static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0d;
        double mean = 0;
        foreach (var v in values) mean += v;
        mean /= values.Count;
        double sum = 0;
        foreach (var v in values) { var d = v - mean; sum += d * d; }
        return Math.Sqrt(sum / values.Count);
    }

    private static double[] Rotate(double[] values, int shift)
    {
        var n = values.Length;
        var rotated = new double[n];
        for (var i = 0; i < n; i++) rotated[i] = values[(i + shift) % n];
        return rotated;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0d;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = percentile / 100.0 * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(sorted.Count - 1, lower + 1);
        var frac = rank - lower;
        return sorted[lower] * (1 - frac) + sorted[upper] * frac;
    }
}
