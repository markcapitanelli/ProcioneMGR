using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Validation;

/// <summary>
/// [I2 roadmap frontiere-profitto] Genera "mercati gemelli NULLI": serie sintetiche costruite dai
/// rendimenti reali in cui — per costruzione — non esiste alcuna struttura direzionale sfruttabile,
/// ma la volatilità conserva il suo clustering. Una caccia eseguita su N gemelli produce la
/// distribuzione nulla del proprio "miglior risultato": il candidato sui dati VERI è credibile solo
/// se batte quella distribuzione. È l'esperimento di controllo del 2026-07 (edge piantato) reso
/// organo permanente, girato al contrario: là si verificava che la pipeline TROVA l'edge quando
/// c'è; qui si verifica che NON lo trova quando non c'è.
///
/// Costruzione del nullo, in due mosse dichiarate:
///  1. <b>Stationary block bootstrap</b> dei rendimenti (blocchi geometrici con reinserimento e
///     wrap-around, Politis–Romano — stessa famiglia di <c>MonteCarloSamplingMode.StationaryBlock</c>):
///     conserva il clustering di |r| a scala di blocco e dà varietà fra i gemelli;
///  2. <b>Segno i.i.d. per barra</b> (p=0,5): annienta OGNI prevedibilità direzionale, anche
///     intra-blocco — un sign-flip a blocchi (come nel PermutationTest, pensato per giudicare i
///     rendimenti di una strategia FISSA) lascerebbe vivo il momentum dentro il blocco, che una
///     caccia rifarebbe sua. Il prezzo dichiarato: muore anche la correlazione segno-vol (leverage
///     effect) — accettabile per un nullo, che deve essere privo di segnale, non realistico in tutto.
///
/// Il volume viaggia accoppiato al proprio rendimento (stesso indice sorgente): la relazione
/// |r|↔volume sopravvive, quella direzione↔volume muore col segno. Timestamp e prezzo iniziale
/// restano quelli reali: timeframe, annualizzazione e ancore di sessione continuano a funzionare.
/// </summary>
public static class NullTwinGenerator
{
    /// <summary>
    /// Genera un gemello nullo della serie <paramref name="real"/> (ordinata, ≥ 3 barre).
    /// Deterministico a parità di <paramref name="seed"/>.
    /// </summary>
    /// <param name="meanBlockLength">Lunghezza media (geometrica) dei blocchi di |r|; default 24 barre.</param>
    public static List<OhlcvData> Generate(IReadOnlyList<OhlcvData> real, int seed, double meanBlockLength = 24)
    {
        ArgumentNullException.ThrowIfNull(real);
        if (real.Count < 3) throw new ArgumentException("Servono almeno 3 candele.", nameof(real));
        if (meanBlockLength < 1) throw new ArgumentOutOfRangeException(nameof(meanBlockLength));

        var n = real.Count;
        var rng = new Random(seed);
        var continueProb = 1.0 - 1.0 / meanBlockLength;

        // Rendimenti sorgente (indice 1..n-1) con le grandezze di forma della barra, accoppiate.
        var m = n - 1;
        var srcReturn = new double[m];
        var srcVolume = new decimal[m];
        var srcWick = new double[m]; // (high-low-|body|)/close: la parte di escursione FUORI dal corpo
        // [D-04, Fase 1 PRD-RISANAMENTO] Quota dello stoppino SOPRA il corpo, campionata dalla
        // stessa barra sorgente (come volume e ampiezza). Prima lo stoppino era spartito 50/50
        // sopra e sotto: geometria idealizzata che altera la probabilita' di TOCCO di stop e
        // target intra-barra — proprio cio' che i backtest usano per decidere le uscite, e
        // proprio sull'orizzonte intraday di riferimento. Col campionamento accoppiato il nullo
        // conserva l'asimmetria reale degli stoppini pur restando privo di segnale direzionale.
        var srcWickUpShare = new double[m];
        for (var i = 1; i < n; i++)
        {
            var prev = real[i - 1].Close;
            srcReturn[i - 1] = prev > 0m ? (double)(real[i].Close / prev - 1m) : 0d;
            srcVolume[i - 1] = real[i].Volume;
            var c = real[i];
            var body = Math.Abs((double)(c.Close - c.Open));
            var wick = (double)(c.High - c.Low) - body;
            srcWick[i - 1] = c.Close > 0m ? Math.Max(0d, wick) / (double)c.Close : 0d;
            var upperWick = (double)(c.High - Math.Max(c.Open, c.Close));
            srcWickUpShare[i - 1] = wick > 0d ? Math.Clamp(upperWick / wick, 0d, 1d) : 0.5d;
        }

        // 1) Stationary bootstrap degli INDICI sorgente (wrap-around).
        var pick = new int[m];
        var pos = rng.Next(m);
        for (var k = 0; k < m; k++)
        {
            pick[k] = pos;
            pos = rng.NextDouble() < continueProb ? (pos + 1) % m : rng.Next(m);
        }

        // 2) Ricostruzione della serie con segno i.i.d. per barra.
        var twin = new List<OhlcvData>(n)
        {
            new()
            {
                Symbol = real[0].Symbol, Timeframe = real[0].Timeframe, TimestampUtc = real[0].TimestampUtc,
                Open = real[0].Open, High = real[0].High, Low = real[0].Low, Close = real[0].Close,
                Volume = real[0].Volume,
            },
        };
        var prevClose = real[0].Close;
        for (var k = 0; k < m; k++)
        {
            var j = pick[k];
            var sign = rng.NextDouble() < 0.5 ? -1d : 1d;
            var r = sign * srcReturn[j];
            // Pavimento anti-degenerazione: un -100%+ resampled non deve azzerare la serie.
            var close = prevClose * (decimal)Math.Max(0.01, 1d + r);

            var open = prevClose;
            var bodyHi = Math.Max(open, close);
            var bodyLo = Math.Min(open, close);
            var wick = (decimal)srcWick[j] * close;
            // [D-04] Ripartizione dello stoppino campionata dalla barra sorgente, SPECCHIATA
            // quando il segno e' stato invertito: una barra ribaltata porta con se' anche la
            // propria geometria ribaltata (lo stoppino superiore diventa inferiore), cosi' il
            // legame |forma|↔|rendimento| sopravvive alla randomizzazione del segno.
            var upShare = (decimal)(sign < 0d ? 1d - srcWickUpShare[j] : srcWickUpShare[j]);
            var low = bodyLo - wick * (1m - upShare);
            if (low <= 0m) low = bodyLo * 0.5m;

            twin.Add(new OhlcvData
            {
                Symbol = real[0].Symbol,
                Timeframe = real[0].Timeframe,
                TimestampUtc = real[k + 1].TimestampUtc,
                Open = open,
                High = bodyHi + wick * upShare,
                Low = low,
                Close = close,
                Volume = srcVolume[j],
            });
            prevClose = close;
        }
        return twin;
    }
}
