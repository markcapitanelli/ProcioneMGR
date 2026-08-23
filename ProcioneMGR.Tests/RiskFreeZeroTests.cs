using ProcioneMGR.Services.Backtesting;
using ProcioneMGR.Services.Optimization;
using ProcioneMGR.Services.Validation;

namespace ProcioneMGR.Tests;

/// <summary>
/// [RF0, 2026-08-22] <b>Il risk-free sottratto sull'equity totale mentre ne è investito una frazione.</b>
///
/// <para>Nel motore la cassa non rende nulla (<c>Portfolio.Equity</c> = cash + margine + PnL non
/// realizzato, nessun accredito da nessuna parte) mentre è investito solo <c>PositionSizePercent</c>
/// del capitale. Sottrarre rf sull'equity INTERA addebitava il costo-opportunità di capitale che la
/// simulazione stessa tiene fermo a zero: un <b>doppio conteggio</b>. La convenzione contabilmente
/// corretta — accreditare rf a tutta l'equity e poi sottrarlo — dà lo stesso numero di rf = 0 per
/// identità algebrica, quindi rf = 0 <i>è</i> quella convenzione, a costo zero.</para>
///
/// <para>Il dazio valeva <c>rf/σ</c>, cioè una funzione dell'<b>esposizione</b> e non della qualità:
/// mediana <b>0,545 punti di Sharpe</b> su 12.967 candidati d'archivio — più dell'intero gate
/// <c>minHoldoutSharpe = 0,5</c> che li giudicava — e sistematicamente peggiore per le strategie
/// selettive (RegimeConditional 0,625) che per quelle sempre a mercato (Stochastic 0,310).</para>
/// </summary>
public class RiskFreeZeroTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Curva costruita da una serie di rendimenti per-periodo.</summary>
    private static List<EquityPoint> CurvaDa(IReadOnlyList<decimal> rendimenti)
    {
        var punti = new List<EquityPoint> { new() { Timestamp = T0, Capital = 1000m } };
        var c = 1000m;
        for (var i = 0; i < rendimenti.Count; i++)
        {
            c *= 1m + rendimenti[i];
            punti.Add(new EquityPoint { Timestamp = T0.AddHours(i + 1), Capital = c });
        }
        return punti;
    }

    /// <summary>Rendimenti deterministici con media e varianza non nulle, senza dipendere dal caso.</summary>
    private static List<decimal> Rendimenti(int n, decimal scala)
    {
        var r = new List<decimal>(n);
        for (var i = 0; i < n; i++)
        {
            // Onda con deriva: media positiva piccola, varianza sana.
            var v = 0.0004m + (i % 7 - 3) * 0.0025m + (i % 3 - 1) * 0.0011m;
            r.Add(v * scala);
        }
        return r;
    }

    // ------------------------------------------------- T1: invarianza alla scala dei rendimenti

    /// <summary>
    /// <b>Il guardiano principale.</b> Lo Sharpe di una serie DEVE essere invariante alla scala dei
    /// rendimenti: moltiplicare ogni rendimento per k moltiplica media e deviazione standard per lo
    /// stesso k, e il rapporto non cambia. Il termine <c>−rf/σ</c> è l'unica cosa che può romperlo,
    /// perché σ scala e rf no.
    ///
    /// <para>Con la vecchia convenzione questo test falliva con uno scarto dell'ordine di <b>0,5</b>
    /// — cinque ordini di grandezza sopra la tolleranza. È il test che rende impossibile
    /// reintrodurre il difetto senza accorgersene.</para>
    /// </summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.2)]
    [InlineData(0.1)]
    [InlineData(0.05)]
    public void Sharpe_InvarianteAllaScalaDeiRendimenti(double kRaw)
    {
        var k = (decimal)kRaw;
        var baseR = Rendimenti(400, 1m);
        var scalata = baseR.Select(x => x * k).ToList();

        var sr1 = Statistics.SharpeRatio(CurvaDa(baseR), 8760);
        var sr2 = Statistics.SharpeRatio(CurvaDa(scalata), 8760);

        Assert.NotEqual(0m, sr1);
        Assert.True(Math.Abs(sr1 - sr2) < 0.01m,
            $"Sharpe non invariante alla scala: {sr1} a scala 1 contro {sr2} a scala {k}. "
            + "Se lo scarto è dell'ordine di 0,5, qualcuno ha rimesso un risk-free sull'equity.");
    }

    /// <summary>
    /// Il difetto, riprodotto esplicitamente: basta passare il vecchio 2% e l'invarianza sparisce.
    /// Serve a documentare l'ampiezza, non solo l'esistenza.
    /// </summary>
    [Fact]
    public void ConIlVecchioRiskFree_LInvarianzaSiROMPE_ELoScartoEGrande()
    {
        var baseR = Rendimenti(400, 1m);
        var piccola = baseR.Select(x => x * 0.1m).ToList();

        var sr1 = Statistics.SharpeRatio(CurvaDa(baseR), 8760, 0.02m);
        var sr2 = Statistics.SharpeRatio(CurvaDa(piccola), 8760, 0.02m);

        // Dieci volte meno volatilità ⇒ dieci volte più dazio. Su QUESTA serie lo scarto misurato
        // vale 0,378: la soglia è messa sotto il valore misurato, non sopra un numero desiderato.
        // Sull'archivio reale il dazio mediano per candidato era 0,545 (q1 0,362, q3 0,749), cioè
        // dello stesso ordine — e più dell'intero gate minHoldoutSharpe = 0,5 che li giudicava.
        Assert.True(Math.Abs(sr1 - sr2) > 0.30m,
            $"scarto atteso grande col rf al 2%, misurato {Math.Abs(sr1 - sr2)}");

        // E il contrappunto che rende il test una MISURA e non un'affermazione: le stesse due curve,
        // a rf = 0, coincidono.
        var puliti = Math.Abs(Statistics.SharpeRatio(CurvaDa(baseR), 8760)
                            - Statistics.SharpeRatio(CurvaDa(piccola), 8760));
        Assert.True(puliti < 0.01m, $"a rf = 0 lo scarto deve sparire, misurato {puliti}");
    }

    // ---------------------------------------------- T3: le due catene non possono più divergere

    /// <summary>
    /// <b>La saldatura fra le due catene.</b> Fino al 2026-08-22 la piattaforma aveva due
    /// convenzioni che si incontravano <i>nella stessa invocazione</i>: <c>SelectionValidator</c>
    /// riceve gli Sharpe dei tentativi calcolati da <c>Statistics.SharpeRatio</c> (rf 2%) e
    /// ricalcola l'osservato con <c>ReturnMoments.PerPeriodSharpe</c> (rf 0).
    ///
    /// <para>Questo test asserisce che le due grandezze ora coincidono a meno
    /// dell'annualizzazione. Fallisce se una delle due catene reintroduce un risk-free.</para>
    /// </summary>
    [Fact]
    public void Sharpe_CoincideConPerPeriodSharpe_LeDueCateneSonoSaldate()
    {
        var r = Rendimenti(500, 1m);
        var curva = CurvaDa(r);
        const int ppy = 8760;

        var annualizzato = Statistics.SharpeRatio(curva, ppy);

        var perPeriodo = new List<double>(curva.Count - 1);
        for (var i = 1; i < curva.Count; i++)
        {
            var prev = curva[i - 1].Capital;
            perPeriodo.Add((double)((curva[i].Capital - prev) / prev));
        }
        var atteso = ReturnMoments.PerPeriodSharpe(perPeriodo) * Math.Sqrt(ppy);

        Assert.True(Math.Abs((double)annualizzato - atteso) < 1e-6,
            $"le due catene divergono: {annualizzato} contro {atteso}. "
            + "Una delle due ha un risk-free che l'altra non ha.");
    }

    // ------------------------------------------------------------- il Sortino e il tearsheet

    [Fact]
    public void Sortino_SegueLaStessaConvenzione()
    {
        var r = Rendimenti(400, 1m);
        var curva = CurvaDa(r);

        var conDefault = Statistics.SortinoRatio(curva, 8760);
        var aZeroEsplicito = Statistics.SortinoRatio(curva, 8760, 0m);

        Assert.Equal(aZeroEsplicito, conDefault);
    }

    [Fact]
    public void Tearsheet_UsaLaStessaConvenzioneDelleFunzioniSingole()
    {
        var r = Rendimenti(400, 1m);
        var curva = CurvaDa(r);
        var t = Statistics.ComputeTearsheet(curva, [], 8760);

        Assert.Equal(Statistics.SharpeRatio(curva, 8760), t.Sharpe);
        Assert.Equal(Statistics.SortinoRatio(curva, 8760), t.Sortino);
    }

    // ------------------------------------------------------------- la data di taglio

    [Fact]
    public void LaConvenzione_DistingueIlPrimaDalDopo_ENullEIlPRIMA()
    {
        var taglio = MetricsConvention.RiskFreeZeroSinceUtc;

        Assert.False(MetricsConvention.IsRiskFreeZero(null));                    // fail-closed
        Assert.False(MetricsConvention.IsRiskFreeZero(taglio.AddSeconds(-1)));
        Assert.True(MetricsConvention.IsRiskFreeZero(taglio));
        Assert.True(MetricsConvention.IsRiskFreeZero(taglio.AddDays(1)));
    }
}
