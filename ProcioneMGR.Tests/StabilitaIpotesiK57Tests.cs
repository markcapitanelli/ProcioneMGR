using ProcioneMGR.Services.Research;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K57, PRD autonomia-piena — Fase 4, 2026-09-02] <b>La stabilità fra rimisurazioni: informazione
/// già pagata che nessuno usava.</b>
///
/// <para><b>Il fatto.</b> Le finestre di selezione e holdout scorrono, quindi la stessa identica
/// ipotesi — parametri compresi — viene rivalutata a ogni giro su dati leggermente diversi.
/// Misurato il 2026-09-02 sul motore corrente, ampiezza mediana del ventaglio:</para>
/// <code>
/// cfg 17 (4h) : 168 chiavi, 14 misure, ventaglio 0,752
/// cfg 18 (1h) :  96 chiavi, 16 misure, ventaglio 0,616
/// cfg 19 (5m) :  24 chiavi, 13 misure, ventaglio 0,534
/// cfg 20 (15m):  36 chiavi, 13 misure, ventaglio 0,398
/// </code>
///
/// <para>Il cancello dello Sharpe holdout sta a <b>0,5</b>: con quei ventagli, per una fetta
/// consistente delle ipotesi passare o non passare dipende da quale notte si guarda. E la fascia
/// grigia ordina per Sharpe, quindi propone per costruzione la notte migliore.</para>
///
/// <para><b>Quanto pesa:</b> su 324 chiavi giudicabili, <b>111</b> passano col massimo e <b>87</b>
/// con la mediana. Le <b>24</b> di differenza — il 22% — passano solo per fortuna.</para>
/// </summary>
public class StabilitaIpotesiK57Tests
{
    /// <summary>Le dodici misure vere della corsia 6 (GridMeanReversion DOGE/USDT 15m).</summary>
    private static readonly decimal[] Corsia6 =
    [
        1.8754m, 0.2329m, 0.4787m, 0.4720m, 0.3212m, 0.3244m,
        0.4531m, 0.6743m, 0.5902m, 0.5522m, 0.5180m, 0.5901m,
    ];

    [Fact]
    public void ILCASOreale_dellaCORSIA6_eINSTABILE()
    {
        var s = StabilitaReader.Calcola(Corsia6);

        Assert.Equal(12, s.Misure);
        Assert.True(s.Giudicabile);
        Assert.Equal(1.8754m, s.Massimo);
        Assert.InRange(s.Mediana, 0.47m, 0.50m);
        // Il ventaglio (1,64) è più del triplo della mediana (0,48).
        Assert.True(s.Instabile);
        // E la «fortuna» misura esattamente quanto il massimo promette in più della mediana.
        Assert.InRange(s.Fortuna, 1.37m, 1.41m);
    }

    /// <summary>
    /// <b>Il nullo principale.</b> Un'ipotesi misurata tante volte con valori stretti NON deve
    /// essere marcata: se il predicato colpisse anche questa, retrocederebbe tutto e il gate non
    /// direbbe niente. È il caso della corsia 5, deterministica su 44 rimisurazioni.
    /// </summary>
    [Fact]
    public void ILNULLO_unIPOTESIstretta_eSTABILE()
    {
        var s = StabilitaReader.Calcola([1.187m, 1.187m, 1.190m, 1.185m, 1.188m, 1.186m]);

        Assert.True(s.Giudicabile);
        Assert.False(s.Instabile);
        Assert.Contains("stabile", s.Racconto, StringComparison.Ordinal);
    }

    /// <summary>
    /// La soglia è RELATIVA, e questo è il caso che lo giustifica: la stessa ampiezza assoluta
    /// (1,0) è un ventaglio più largo del valore su una mediana di 0,6 e ordinaria su una di 3,0.
    /// Una soglia assoluta punirebbe le ipotesi migliori per essere migliori.
    /// </summary>
    [Fact]
    public void LaSOGLIAeRELATIVA_nonASSOLUTA()
    {
        var debole = StabilitaReader.Calcola([0.2m, 0.4m, 0.6m, 0.8m, 1.0m, 1.2m]);   // mediana 0,7 · ampiezza 1,0
        var forte = StabilitaReader.Calcola([2.6m, 2.8m, 3.0m, 3.2m, 3.4m, 3.6m]);    // mediana 3,1 · ampiezza 1,0

        Assert.True(debole.Instabile);
        Assert.False(forte.Instabile);
    }

    /// <summary>
    /// Poche misure non giudicano. È la stessa soglia di <c>ExpectationEvidence</c>: due o tre
    /// finestre adiacenti non sono una distribuzione, e un gate costruito su di esse è rumore
    /// travestito da prova.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void SOTTOleMISUREminime_nonSiGIUDICA(int quante)
    {
        // Valori volutamente assurdi: se il gate scattasse, scatterebbe qui.
        var s = StabilitaReader.Calcola([.. Enumerable.Range(0, quante).Select(i => i % 2 == 0 ? 10m : -10m)]);

        Assert.False(s.Giudicabile);
        Assert.False(s.Instabile);
        Assert.Contains("non si giudica", s.Racconto, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mediana ≤ 0: il rapporto ampiezza/mediana non è interpretabile — il segno capovolgerebbe il
    /// significato — e un'ipotesi che oscilla intorno allo zero non è un'ipotesi. Instabile.
    /// </summary>
    [Fact]
    public void UNAmedianaNONpositiva_eINSTABILEperDEFINIZIONE()
        => Assert.True(StabilitaReader.Calcola([-0.4m, -0.2m, 0.0m, 0.1m, 0.2m, 0.3m]).Instabile);

    /// <summary>
    /// Mediana e non media: una singola notte anomala non deve spostare la stima centrale, che è
    /// esattamente il difetto da correggere preso dall'altra parte.
    /// </summary>
    [Fact]
    public void LaSTIMAcentrale_eLaMEDIANA()
    {
        decimal[] normali = [1.0m, 1.1m, 1.2m, 1.3m, 1.4m];
        var senza = StabilitaReader.Calcola(normali);
        var con = StabilitaReader.Calcola([.. normali, 50m]);

        Assert.Equal(1.2m, senza.Mediana);
        Assert.InRange(con.Mediana, 1.2m, 1.3m);      // la mediana quasi non si muove
        Assert.Equal(50m, con.Massimo);               // il massimo sì, ed è quello che oggi guida
    }

    [Fact]
    public void UNELENCOvuoto_nonESPLODE()
    {
        var s = StabilitaReader.Calcola([]);
        Assert.Equal(0, s.Misure);
        Assert.False(s.Giudicabile);
        Assert.False(s.Instabile);
    }

    /// <summary>
    /// La soglia deve restare quella MISURATA. Il rapporto ampiezza/mediana ha mediana osservata
    /// 0,57 sulle 324 chiavi giudicabili: 1,0 sta intorno al 73° percentile e taglia il quarto
    /// peggiore. A 0,5 resterebbero 35 chiavi su 87 — più della metà buttata; a 2,0 ne resterebbero
    /// 79, cioè non cambierebbe quasi nulla. Se qualcuno la sposta fuori da questa banda, il gate
    /// smette di essere calibrato sui dati.
    /// </summary>
    [Fact]
    public void LaSOGLIA_restaQUELLAmisurata()
        => Assert.InRange(StabilitaIpotesi.MaxAmpiezzaSuMediana, 0.8m, 1.3m);
}
