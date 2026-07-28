using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-07-28] L'IMBUTO, cioe' dove muoiono i candidati.
///
/// Fino a oggi la pipeline registrava solo "Candidates" e "Survivors": 32 run, 2.049 candidati, zero
/// sopravvissuti, e nessun modo di sapere quale gate li stesse uccidendo. Sono tre diagnosi opposte —
/// un candidato bocciato per «solo 8 trade in holdout» non dice niente sul mercato, dice che la
/// finestra e' troppo corta per la sua frequenza; uno bocciato con Sharpe -1,9 dice che perde davvero;
/// uno bocciato dal DSR dice che guadagna ma non e' distinguibile dal caso. Confonderle era il motivo
/// per cui la domanda «perche' non consolida mai» e' rimasta aperta per settimane.
///
/// Il raggruppamento in CLASSI e' la parte che puo' rompersi in silenzio: i motivi contengono il
/// valore misurato («DSR 0,677 ≤ 0,95»), quindi contarli per stringa darebbe una categoria per
/// candidato e un conteggio che non dice niente.
/// </summary>
public sealed class PipelineFunnelMetricsTests
{
    private static ValidatedCandidate Rejected(string reason) =>
        new() { StrategyName = "X", Symbol = "BTC/USDT", Timeframe = "1h", Survived = false, RejectReason = reason };

    private static ValidatedCandidate Survived() =>
        new() { StrategyName = "X", Symbol = "BTC/USDT", Timeframe = "1h", Survived = true };

    /// <summary>
    /// I motivi che portano il VALORE misurato devono comunque collassare in una classe sola:
    /// e' l'intero punto della classificazione.
    /// </summary>
    [Fact]
    public void I_motivi_con_valori_diversi_finiscono_nella_stessa_classe()
    {
        var candidati = new List<ValidatedCandidate>
        {
            Rejected("DSR 0,677 ≤ 0,95 (probabile overfitting da selezione)"),
            Rejected("DSR 0,314 ≤ 0,95 (probabile overfitting da selezione)"),
            Rejected("DSR 0,773 ≤ 0,95 (probabile overfitting da selezione)"),
            Rejected("Sharpe holdout -1,87 < 0,5"),
            Rejected("Sharpe holdout 0,31 < 0,5"),
            Rejected("Solo 6 trade in holdout (< 10)"),
            Rejected("Solo 9 trade in holdout (< 10)"),
        };

        var classi = PipelineEngine.ClassifyRejections(candidati).ToDictionary(x => x.Classe, x => x.Quanti);

        Assert.Equal(3m, classi["Dsr"]);
        Assert.Equal(2m, classi["SharpeHoldout"]);
        Assert.Equal(2m, classi["ContoTrade"]);
        Assert.Equal(3, classi.Count);   // tre classi, non sette stringhe
    }

    /// <summary>
    /// I sopravvissuti non entrano nell'imbuto degli scarti: se ci entrassero, un run sano
    /// sembrerebbe un run che boccia tutto.
    /// </summary>
    [Fact]
    public void I_sopravvissuti_non_sono_scarti()
    {
        var candidati = new List<ValidatedCandidate>
        {
            Survived(), Survived(), Rejected("Sharpe holdout -1,0 < 0,5"),
        };

        var classi = PipelineEngine.ClassifyRejections(candidati).ToList();

        Assert.Single(classi);
        Assert.Equal("SharpeHoldout", classi[0].Classe);
        Assert.Equal(1m, classi[0].Quanti);
    }

    /// <summary>
    /// Un backtest fallito NON e' un verdetto sul candidato: e' un guasto, e va in una classe sua.
    /// Contarlo come «Sharpe insufficiente» significherebbe leggere un errore di sistema come una
    /// prova che il mercato non offre nulla — esattamente il tipo di scambio che questa misura esiste
    /// per impedire.
    /// </summary>
    [Fact]
    public void Un_guasto_non_si_confonde_con_un_verdetto()
    {
        var candidati = new List<ValidatedCandidate>
        {
            Rejected("Backtest fallito: sequence contains no elements"),
            Rejected("Sharpe holdout -1,0 < 0,5"),
        };

        var classi = PipelineEngine.ClassifyRejections(candidati).ToDictionary(x => x.Classe, x => x.Quanti);

        Assert.Equal(1m, classi["Errore"]);
        Assert.Equal(1m, classi["SharpeHoldout"]);
    }

    /// <summary>Un motivo mai visto prima non deve sparire nel nulla: finisce in "Altro" e si vede.</summary>
    [Fact]
    public void Un_motivo_sconosciuto_resta_visibile()
    {
        var classi = PipelineEngine
            .ClassifyRejections([Rejected("Qualcosa che non avevo previsto")])
            .ToDictionary(x => x.Classe, x => x.Quanti);

        Assert.Equal(1m, classi["Altro"]);
    }

    /// <summary>Nessuno scarto ⇒ nessuna riga, non una riga a zero: le metriche restano leggibili.</summary>
    [Fact]
    public void Senza_scarti_non_si_producono_righe()
    {
        Assert.Empty(PipelineEngine.ClassifyRejections([Survived(), Survived()]));
    }
}
