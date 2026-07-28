using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-07-28] Potenza del gate anti-overfitting: qual e' l'edge piu' piccolo che la piattaforma
/// potrebbe CONFERMARE, se fosse vero.
///
/// Nasce dalla domanda del proprietario — «di candidati se ne trovano ma non consolidano mai» — e
/// serve a separare due diagnosi opposte: non c'e' edge (e i gate hanno ragione), oppure il gate non
/// ha la potenza per confermare un edge della grandezza che esiste (e «zero sopravvissuti» e'
/// un'informazione sullo strumento, non sul mercato).
///
/// Il test che vale piu' di tutti e'
/// <see cref="Il_modello_riproduce_i_DSR_osservati_sui_candidati_veri"/>: la teoria deve riprodurre
/// i valori di DSR realmente registrati dalla pipeline sui candidati bocciati. Se non li
/// riproducesse, la tabella degli anni sarebbe un esercizio di aritmetica scollegato dalla
/// piattaforma di cui pretende di parlare.
/// </summary>
public sealed class GatePowerAnalyzerTests
{
    // ------------------------------------------------------------------ coerenza col gate vero

    /// <summary>
    /// Il valore restituito deve portare il DSR ESATTAMENTE alla soglia: si invertono le parti e si
    /// ricalcola col gate vero. Se divergesse, la misura descriverebbe un gate immaginario.
    /// </summary>
    [Theory]
    [InlineData(123, 10)]
    [InlineData(123, 1000)]
    [InlineData(2952, 100)]
    [InlineData(35424, 445_000)]
    public void Lo_sharpe_minimo_riporta_il_DSR_esattamente_alla_soglia(int osservazioni, int tentativi)
    {
        var v = 1.0 / osservazioni;

        var min = GatePowerAnalyzer.MinDetectablePerPeriodSharpe(osservazioni, v, tentativi);

        Assert.NotNull(min);
        var dsr = DeflatedSharpeRatio.Deflated(min!.Value, osservazioni, 0.0, 3.0, v, tentativi);
        Assert.InRange(dsr, 0.9499, 0.9501);

        // E appena sotto NON passa: la soglia e' un confine, non una zona.
        var sotto = DeflatedSharpeRatio.Deflated(min.Value * 0.98, osservazioni, 0.0, 3.0, v, tentativi);
        Assert.True(sotto < 0.95);
    }

    /// <summary>Piu' tentativi = asticella piu' alta. E' l'intera ragione d'essere del DSR.</summary>
    [Fact]
    public void Piu_tentativi_alzano_lasticella()
    {
        const int t = 1000;
        var v = 1.0 / t;

        var a = GatePowerAnalyzer.MinDetectableAnnualSharpe(t, v, 10, 365)!.Value;
        var b = GatePowerAnalyzer.MinDetectableAnnualSharpe(t, v, 1_000, 365)!.Value;
        var c = GatePowerAnalyzer.MinDetectableAnnualSharpe(t, v, 445_000, 365)!.Value;

        Assert.True(a < b, $"10 tentativi ({a:F2}) dovrebbe chiedere meno di 1.000 ({b:F2})");
        Assert.True(b < c, $"1.000 ({b:F2}) dovrebbe chiedere meno di 445.000 ({c:F2})");
    }

    // ------------------------------------------------------------------ il bug che ho fatto

    /// <summary>
    /// REGRESSIONE. La varianza cross-trial dipende da T: trattarla come input indipendente e
    /// iterare produce un punto fisso che OSCILLA fra due valori e non converge mai. La prima
    /// versione di questa misura faceva cosi' e stampava «oltre 50 anni» per OGNI grandezza di edge,
    /// che sembrava un risultato drammatico ed era solo un mio errore.
    ///
    /// Un edge enorme (Sharpe 3) con pochi tentativi deve essere confermabile in POCO tempo.
    /// </summary>
    [Fact]
    public void Un_edge_enorme_si_conferma_in_poco_tempo()
    {
        var anni = GatePowerAnalyzer.YearsNeededFor(3.0, trials: 10, periodsPerYear: 365);

        Assert.NotNull(anni);
        Assert.InRange(anni!.Value, 0.5, 2.5);
    }

    /// <summary>
    /// Gli anni necessari crescono al crescere dei tentativi e al calare dell'edge, e i valori
    /// coincidono con l'aritmetica a mano: anni ≈ ((k(N) + 1,645) / Sharpe)², dove k(N) e' il
    /// fattore del massimo atteso su N estrazioni. Per Sharpe 1,0 e 100 tentativi da' ~18 anni.
    /// </summary>
    [Fact]
    public void Gli_anni_necessari_seguono_la_forma_attesa()
    {
        var s1n100 = GatePowerAnalyzer.YearsNeededFor(1.0, 100, 365)!.Value;
        var s2n100 = GatePowerAnalyzer.YearsNeededFor(2.0, 100, 365)!.Value;
        var s1n10 = GatePowerAnalyzer.YearsNeededFor(1.0, 10, 365)!.Value;

        // Raddoppiare lo Sharpe divide gli anni per circa quattro (dipendenza quadratica).
        Assert.InRange(s1n100 / s2n100, 3.2, 4.8);
        // Meno tentativi = meno anni.
        Assert.True(s1n10 < s1n100);
        // Ordine di grandezza verificato a mano.
        Assert.InRange(s1n100, 12.0, 25.0);
    }

    // ------------------------------------------------------------------ il controllo incrociato

    /// <summary>
    /// IL TEST CHE CONTA. La pipeline ha registrato 61 candidati bocciati dal DSR, con valori
    /// osservati fra 0,31 e 0,77 e Sharpe holdout medio 1,14, su un holdout di ~4 mesi (123 barre
    /// giornaliere). Se il modello e' quello giusto, deve riprodurre quella banda — non un valore
    /// vicino a 0,95 (che direbbe «quasi passati») ne' vicino a 0 (che direbbe «senza speranza»).
    ///
    /// E' la verifica che lega la tabella degli anni ai dati veri della piattaforma.
    /// </summary>
    [Fact]
    public void Il_modello_riproduce_i_DSR_osservati_sui_candidati_veri()
    {
        const int osservazioni = 123;          // ~4 mesi di barre giornaliere
        const double sharpeAnnuoOsservato = 1.14;
        var perPeriodo = sharpeAnnuoOsservato / Math.Sqrt(365);

        // La dispersione cross-trial NON e' un'assunzione: misurata sui 31 run reali vale 1,76
        // annualizzata (fra 0,99 e 2,57), contro 1,72 previsti dal nullo teorico su 123 osservazioni.
        // Teoria e dati coincidono, quindi si usa il valore misurato.
        const double dispersioneMisurataAnnua = 1.76;
        var varianzaPerPeriodo = Math.Pow(dispersioneMisurataAnnua / Math.Sqrt(365), 2);

        // I tentativi che contano sono quelli EFFETTIVI, non i nominali: EffectiveTrials collassa i
        // candidati con rendimenti correlati (griglie fitte, simboli gemelli) in un solo test. E' la
        // ragione per cui i DSR osservati stanno fra 0,31 e 0,77 invece che vicino a zero come
        // darebbe il conteggio nominale — un dettaglio che avevo trascurato, e che il confronto coi
        // dati veri ha smascherato.
        var dsr3 = DeflatedSharpeRatio.Deflated(perPeriodo, osservazioni, 0.0, 3.0, varianzaPerPeriodo, 3);
        var dsr5 = DeflatedSharpeRatio.Deflated(perPeriodo, osservazioni, 0.0, 3.0, varianzaPerPeriodo, 5);

        Assert.InRange(dsr3, 0.28, 0.80);
        Assert.InRange(dsr5, 0.20, 0.75);
        Assert.True(dsr5 < dsr3, "piu' tentativi effettivi devono abbassare il DSR a parita' di tutto il resto");
    }

    /// <summary>
    /// La conclusione operativa, resa un test perche' non si perda: con l'holdout attuale di ~4 mesi
    /// nessun edge realistico e' confermabile. Anche uno Sharpe 3 — che in natura praticamente non
    /// esiste al netto dei costi — ne richiede piu' del doppio.
    /// </summary>
    [Fact]
    public void Con_quattro_mesi_di_holdout_nessun_edge_realistico_e_confermabile()
    {
        const double holdoutAttualeAnni = 4.0 / 12.0;

        foreach (var sharpe in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
        {
            var anni = GatePowerAnalyzer.YearsNeededFor(sharpe, trials: 10, periodsPerYear: 365);
            Assert.NotNull(anni);
            Assert.True(anni!.Value > holdoutAttualeAnni,
                $"Sharpe {sharpe}: servono {anni.Value:F1} anni, l'holdout ne ha {holdoutAttualeAnni:F2}");
        }
    }
}
