namespace ProcioneMGR.Services.Optimization;

/// <summary>
/// [RF0, 2026-08-22] <b>La data di taglio della convenzione metrica.</b>
///
/// <para>Uno Sharpe prodotto <b>prima</b> di questo istante porta dentro un risk-free del 2%
/// sottratto sull'equity totale; uno prodotto <b>dopo</b> no. I due non sono confrontabili: il
/// divario mediano misurato sull'archivio è di <b>0,545 punti di Sharpe</b>, con q1 0,362 e q3
/// 0,749 — più dell'intero gate che li giudica. Vedi <see cref="Statistics.SharpeRatio"/>.</para>
///
/// <para><b>Non è una tabella e non è una colonna</b>, ed è deliberato. La provenienza si legge
/// dalla <b>data</b> che ogni riga già porta: <c>ResearchCandidates.RunCompletedUtc</c>, che è
/// denormalizzata da <c>PipelineRuns.CompletedAt</c> ed è quindi STABILE fra le ricostruzioni
/// dell'indice derivato. Una colonna nuova sarebbe ridondante, e un backfill dei numeri
/// sparirebbe al primo click su «Ricostruisci» — la tabella si cancella e si rifà dagli artifact,
/// che contengono gli Sharpe vecchi. Correggerli con l'identità algebrica
/// <c>SR₀ = SR·μ/(μ−rf)</c> produrrebbe inoltre, dentro una tabella di MISURE, numeri che nessun
/// run ha mai prodotto — ed è cieca su 926 righe su 13.893 (σ non derivabile).</para>
///
/// <para><b>Dove serve davvero.</b> Solo nei punti che confrontano attraverso run DIVERSI: la
/// riserva grigia di <c>/ensemble</c> (che non ha filtro d'età, quindi lì il taglio è permanente)
/// e le medie d'archivio di <c>/research</c>. Il resto scade quasi da sé — 10.788 righe su 10.789
/// utilizzabili stanno negli ultimi 30 giorni, perché ogni caccia notturna ri-registra la stessa
/// griglia — ma le <b>otto gambe schierate</b> no: quelle decidono oggi, e finché non vengono
/// ri-misurate il comparatore le rifiuta (vedi <c>EnsembleComparator</c>).</para>
/// </summary>
public static class MetricsConvention
{
    /// <summary>L'istante dal quale <see cref="Statistics.SharpeRatio"/> non sottrae più alcun risk-free.</summary>
    public static readonly DateTime RiskFreeZeroSinceUtc = new(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// True se il numero è nato con rf = 0. <b>null o precedente al taglio = convenzione vecchia</b>,
    /// e va trattata come tale: fail-closed, non «probabilmente va bene».
    /// </summary>
    public static bool IsRiskFreeZero(DateTime? producedAtUtc)
        => producedAtUtc is DateTime t && t >= RiskFreeZeroSinceUtc;
}
