namespace ProcioneMGR.Services.ML;

/// <summary>
/// Cross-validation temporale con purging ed embargo (López de Prado, "Advances in Financial
/// Machine Learning"). Su serie storiche con label a rendimento forward, un semplice K-fold
/// casuale causa leakage: un campione di training può avere un orizzonte di label che si
/// sovrappone al periodo di test (o viceversa), gonfiando artificialmente le metriche.
/// Assente in ML.NET di default.
/// </summary>
public interface IPurgedTimeSeriesCv
{
    /// <summary>
    /// Divide <paramref name="sampleCount"/> campioni ORDINATI TEMPORALMENTE in
    /// <paramref name="folds"/> blocchi di test contigui e non sovrapposti. Per ogni fold, il
    /// training esclude non solo il blocco di test ma anche:
    ///  - <b>purge</b>: i <paramref name="purgeWindow"/> campioni immediatamente prima del test
    ///    (le cui label a rendimento forward potrebbero "vedere" nel periodo di test);
    ///  - <b>embargo</b>: gli <paramref name="embargoPeriods"/> campioni immediatamente dopo il
    ///    test (per l'autocorrelazione seriale dei rendimenti finanziari).
    /// </summary>
    IReadOnlyList<CvSplit> Split(int sampleCount, int folds, int purgeWindow, int embargoPeriods);
}
