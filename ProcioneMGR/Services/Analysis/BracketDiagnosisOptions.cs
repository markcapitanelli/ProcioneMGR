namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// [2026-09-07] Manopole della diagnosi del bracket. Sezione <c>Trading:BracketDiagnosis</c>.
///
/// <para>Sono qui e non costanti nel codice per la regola del 2026-08-09: <b>ogni chiave nasce col
/// suo pannello</b>, e i rischi si scrivono accanto alla manopola invece che in un documento. Il
/// pannello è la scheda «Diagnosi del bracket» di <c>/admin/protections</c>.</para>
///
/// <para><b>Nessuna proprietà calcolata qui dentro.</b> <c>SaveSectionAsync</c> serializza il POCO
/// intero: una get-only diventerebbe una chiave inventata nel file di configurazione. Il guardiano
/// <c>ConfigPocoComputedPropertyTests</c> lo fa fallire se qualcuno ci prova.</para>
/// </summary>
public sealed class BracketDiagnosisOptions
{
    public const string SectionName = "Trading:BracketDiagnosis";

    /// <summary>
    /// Il guardiano periodico. Default ON: è diagnostica pura, non tocca niente, e la piattaforma ha
    /// già una collezione di meccanismi che misurano e non parlano.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ogni quante ore il guardiano rimisura tutte le corsie. Default 24: le uscite arrivano al ritmo
    /// di poche al mese, e misurare più spesso non aggiunge informazione — aggiunge solo carico.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// Uscite a barriera vive minime perché il confronto previsto-contro-osservato abbia potere.
    /// Sotto questa soglia il verdetto è «non giudicabile», che è un esito e non un errore.
    ///
    /// <para><b>Abbassarla non rende il giudizio più severo: lo rende rumoroso.</b> Con dieci uscite
    /// l'errore campionario su una proporzione dell'85% vale dodici punti percentuali, cioè quasi
    /// quanto la soglia di scarto: si comincerebbe ad allarmare sul caso. È lo stesso errore già
    /// pagato abbassando <c>minHoldoutTrades</c> nel governo della flotta.</para>
    /// </summary>
    public int MinBarrierExits { get; set; } = 20;

    /// <summary>
    /// Punti percentuali di scarto fra osservato e nominale oltre i quali la corsia non è più «come
    /// prevista». Default 15, ampio di proposito: con venti uscite l'errore campionario ne vale già
    /// otto, e una soglia stretta produrrebbe allarmi che sono solo rumore di campionamento.
    /// </summary>
    public decimal AllowedDeviationPoints { get; set; } = 15m;

    /// <summary>
    /// Candele lette per corsia nella corsa nominale. Cinquemila barre coprono 17 giorni a 5 minuti e
    /// quasi tre anni a 4 ore. Alzarlo rende la misura più stabile e più lenta; il costo cresce
    /// linearmente ed è tutto in memoria.
    /// </summary>
    public int MaxCandles { get; set; } = 5000;

    /// <summary>
    /// Costo di andata e ritorno in punti percentuali (commissioni più slippage), usato SOLO per il
    /// valore atteso della corsa. Non influenza né i conteggi delle barriere né il verdetto.
    /// </summary>
    public decimal RoundTripCostPercent { get; set; } = 0.20m;
}
