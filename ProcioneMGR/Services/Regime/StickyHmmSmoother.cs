namespace ProcioneMGR.Services.Regime;

/// <summary>
/// [R4 — ROADMAP-RENDIMENTO] Decodifica "sticky-HMM" delle etichette di regime: l'ibrido
/// K-means→HMM della letteratura, nella sua forma minima onesta.
///
/// <b>Perché esiste.</b> Misurato il 2026-07-25: i regimi K-means di questa piattaforma — già
/// smussati con maggioranza mobile + conferma a 3 candele — durano in mediana 2,2 giorni su BTC 1h,
/// esattamente il valore che la letteratura chiama "non operabile coi costi reali" (gli HMM stanno
/// a 21-40 giorni). Un router che seguisse regimi così brevi pagherebbe commissioni sul rumore.
///
/// <b>Come funziona.</b> I cluster K-means restano la definizione dei regimi (interpretabili, già
/// profilati per strategia); quello che cambia è la ricostruzione della SEQUENZA. Le etichette
/// grezze per-barra sono trattate come osservazioni rumorose dello stato vero: emissione = con
/// probabilità <c>emissionAccuracy</c> lo stato emette la propria etichetta, il resto si
/// distribuisce uniforme; transizione = "sticky", con autotransizione ρ (durata attesa di uno stato
/// = 1/(1−ρ) barre). Viterbi restituisce il percorso di stati più probabile.
///
/// <b>Cosa NON è.</b> Non è un Baum-Welch su emissioni gaussiane: i parametri non sono stimati dai
/// dati ma scelti misurando il compromesso persistenza/accordo su una griglia dichiarata. È una
/// scelta deliberata: meno gradi di libertà da sovradattare, e un comportamento interamente
/// spiegabile da due numeri. Se il gate della roadmap passa, un EM completo resta un raffinamento
/// possibile — non un prerequisito.
///
/// Causalità: Viterbi guarda l'intera sequenza, quindi questa decodifica è per ANALISI e
/// addestramento (profilare regimi, misurare persistenza), non per decisioni live barra-per-barra.
/// Per il live esiste la variante filtrata (<see cref="DecodeCausal"/>): usa solo il passato,
/// come richiede il router.
/// </summary>
public static class StickyHmmSmoother
{
    /// <summary>
    /// Decodifica Viterbi (offline: usa tutta la sequenza). <paramref name="raw"/> sono le etichette
    /// grezze 0..K-1; etichette fuori intervallo interrompono con eccezione — un -1 qui è un bug del
    /// chiamante, non un caso da nascondere.
    /// </summary>
    /// <param name="k">Numero di stati (i cluster del modello K-means).</param>
    /// <param name="rho">Autotransizione: durata attesa = 1/(1−ρ) barre. Es. 0,998 su 1h ≈ 21 giorni.</param>
    /// <param name="emissionAccuracy">P(etichetta grezza == stato vero). 0,75 = un quarto delle barre è rumore.</param>
    public static int[] Decode(IReadOnlyList<int> raw, int k, double rho, double emissionAccuracy)
    {
        Validate(raw, k, rho, emissionAccuracy);
        var n = raw.Count;

        var logStay = Math.Log(rho);
        var logSwitch = Math.Log((1 - rho) / (k - 1));
        var logHit = Math.Log(emissionAccuracy);
        var logMiss = Math.Log((1 - emissionAccuracy) / (k - 1));

        // Viterbi in spazio log. delta[s] = miglior log-prob di un percorso che finisce in s.
        var delta = new double[k];
        var psi = new int[n, k];
        for (var s = 0; s < k; s++) delta[s] = -Math.Log(k) + (raw[0] == s ? logHit : logMiss);

        var next = new double[k];
        for (var t = 1; t < n; t++)
        {
            for (var s = 0; s < k; s++)
            {
                var bestPrev = 0;
                var best = double.NegativeInfinity;
                for (var p = 0; p < k; p++)
                {
                    var v = delta[p] + (p == s ? logStay : logSwitch);
                    if (v > best) { best = v; bestPrev = p; }
                }
                next[s] = best + (raw[t] == s ? logHit : logMiss);
                psi[t, s] = bestPrev;
            }
            (delta, next) = (next, delta);
        }

        // Backtrack dal migliore stato finale.
        var path = new int[n];
        var last = 0;
        for (var s = 1; s < k; s++) if (delta[s] > delta[last]) last = s;
        path[n - 1] = last;
        for (var t = n - 1; t > 0; t--) path[t - 1] = psi[t, path[t]];
        return path;
    }

    /// <summary>
    /// Variante CAUSALE (filtering): lo stato alla barra t usa solo le barre 0..t — è la forma che
    /// un router live può consumare. Restituisce l'argmax del forward filtrato, che è più reattivo
    /// e leggermente più rumoroso del Viterbi: la persistenza va misurata su QUESTA, se l'uso è live.
    /// </summary>
    public static int[] DecodeCausal(IReadOnlyList<int> raw, int k, double rho, double emissionAccuracy)
    {
        Validate(raw, k, rho, emissionAccuracy);
        var n = raw.Count;

        var stay = rho;
        var sw = (1 - rho) / (k - 1);
        var hit = emissionAccuracy;
        var miss = (1 - emissionAccuracy) / (k - 1);

        var belief = new double[k];
        for (var s = 0; s < k; s++) belief[s] = 1.0 / k;

        var outPath = new int[n];
        var predicted = new double[k];
        for (var t = 0; t < n; t++)
        {
            if (t > 0)
            {
                for (var s = 0; s < k; s++)
                {
                    double sum = 0;
                    for (var p = 0; p < k; p++) sum += belief[p] * (p == s ? stay : sw);
                    predicted[s] = sum;
                }
                (belief, predicted) = (predicted, belief);
            }

            double norm = 0;
            for (var s = 0; s < k; s++)
            {
                belief[s] *= raw[t] == s ? hit : miss;
                norm += belief[s];
            }
            for (var s = 0; s < k; s++) belief[s] /= norm;

            var arg = 0;
            for (var s = 1; s < k; s++) if (belief[s] > belief[arg]) arg = s;
            outPath[t] = arg;
        }
        return outPath;
    }

    /// <summary>Durate (in barre) dei tratti consecutivi nello stesso stato — la misura del gate.</summary>
    public static List<int> RunLengths(IReadOnlyList<int> path)
    {
        var runs = new List<int>();
        if (path.Count == 0) return runs;
        var len = 1;
        for (var i = 1; i < path.Count; i++)
        {
            if (path[i] == path[i - 1]) { len++; continue; }
            runs.Add(len);
            len = 1;
        }
        runs.Add(len);
        return runs;
    }

    private static void Validate(IReadOnlyList<int> raw, int k, double rho, double emissionAccuracy)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Count == 0) throw new ArgumentException("Sequenza vuota.", nameof(raw));
        if (k < 2) throw new ArgumentOutOfRangeException(nameof(k), k, "Servono almeno 2 stati.");
        if (rho is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(rho), rho, "ρ in (0,1).");
        if (emissionAccuracy is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(emissionAccuracy), emissionAccuracy, "Accuratezza in (0,1).");
        // Perché l'emissione informi, la propria etichetta deve restare più probabile del rumore.
        if (emissionAccuracy <= 1.0 / k) throw new ArgumentOutOfRangeException(nameof(emissionAccuracy), emissionAccuracy, $"Deve superare 1/k = {1.0 / k:F2}.");
        for (var i = 0; i < raw.Count; i++)
        {
            if (raw[i] < 0 || raw[i] >= k)
                throw new ArgumentException($"Etichetta {raw[i]} fuori da 0..{k - 1} all'indice {i}: un -1 qui è un bug del chiamante.", nameof(raw));
        }
    }
}
