namespace ProcioneMGR.Services.Validation;

/// <summary>
/// [T1.5 roadmap macchina-ricerca] Test di randomizzazione per lo Sharpe: quanto è probabile
/// osservare uno Sharpe almeno così alto se la strategia NON avesse alcuna deriva sistematica?
///
/// <para><b>Perché a blocchi e perché lungo il tempo.</b> La lezione pagata il 2026-07-20 (la "t di
/// 141"): randomizzare fra asset correlati dentro una stessa finestra fabbrica significatività
/// finta, perché le repliche non sono indipendenti. L'unica randomizzazione onesta su questi dati è
/// LUNGO IL TEMPO. E dev'essere a BLOCCHI: capovolgere il segno di ogni barra indipendentemente
/// distruggerebbe l'autocorrelazione dei rendimenti, producendo una distribuzione nulla più stretta
/// del vero e quindi p-value troppo generosi — lo stesso difetto dello shuffle iid nel Monte Carlo.
/// Qui il segno si capovolge per blocchi contigui di lunghezza geometrica media
/// <c>meanBlockLength</c> (Politis–Romano): la struttura interna dei rendimenti sopravvive, la
/// direzione complessiva no — che è esattamente l'ipotesi nulla giusta per "c'è una deriva?".</para>
///
/// <para><b>Cosa NON dice.</b> Un p-value basso dice che la deriva osservata difficilmente è rumore
/// simmetrico; non dice che sopravviverà ai costi né fuori campione. È un filtro in PIÙ rispetto a
/// DSR/PBO (che correggono per il test multiplo), non un sostituto.</para>
/// </summary>
public static class PermutationTest
{
    public readonly record struct Result(double PValue, double ObservedSharpe, int Permutations);

    /// <param name="returns">Rendimenti per-periodo (non annualizzati), in ordine temporale.</param>
    /// <param name="permutations">Numero di repliche nulle. 500-1000 è un buon compromesso.</param>
    /// <param name="meanBlockLength">Lunghezza media (geometrica) dei blocchi di segno.</param>
    /// <param name="seed">Seme fisso = risultato riproducibile (stessa disciplina del resto della piattaforma).</param>
    public static Result SharpeSignificance(
        IReadOnlyList<double> returns, int permutations = 500, int meanBlockLength = 10, int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ArgumentOutOfRangeException.ThrowIfLessThan(permutations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(meanBlockLength, 1);

        var n = returns.Count;
        if (n < 3) return new Result(1.0, 0.0, 0);

        var observed = Sharpe(returns);
        if (double.IsNaN(observed) || observed == 0.0) return new Result(1.0, observed, 0);

        var rng = new Random(seed);
        var p = 1.0 / meanBlockLength;      // probabilità di chiudere il blocco a ogni barra
        var flipped = new double[n];
        var atLeastAsGood = 0;

        for (var k = 0; k < permutations; k++)
        {
            var sign = rng.NextDouble() < 0.5 ? 1.0 : -1.0;
            for (var i = 0; i < n; i++)
            {
                if (rng.NextDouble() < p) sign = rng.NextDouble() < 0.5 ? 1.0 : -1.0;
                flipped[i] = sign * returns[i];
            }
            if (Sharpe(flipped) >= observed) atLeastAsGood++;
        }

        // Stimatore conservativo (+1/+1): il p-value non può mai essere 0 con repliche finite.
        return new Result((1.0 + atLeastAsGood) / (1.0 + permutations), observed, permutations);
    }

    private static double Sharpe(IReadOnlyList<double> r)
    {
        var mean = r.Average();
        var sd = Math.Sqrt(r.Sum(v => (v - mean) * (v - mean)) / (r.Count - 1));
        return sd > 1e-12 ? mean / sd : double.NaN;
    }
}
