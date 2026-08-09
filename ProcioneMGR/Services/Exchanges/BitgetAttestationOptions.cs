namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// [Fase 3 PRD-RISANAMENTO] Sezione <c>Trading:Bitget</c>: l'attestazione che sblocca i
/// MARKET-BUY spot su Bitget. Il POCO esiste per il pannello di /admin/protections — il consumo
/// vero resta la lettura puntuale in <c>BitgetClient.PlaceOrderAsync</c> (hot, a ogni ordine).
///
/// Non è una preferenza: è la registrazione di un FATTO («ho verificato dal vivo con
/// tools/SpotVerify che la semantica del campo size è quella che il client manda»). Il default
/// false blocca il percorso d'ordine perché la v2 di Bitget documenta size come controvalore
/// QUOTE sui market-buy spot, e un ordine di taglia sbagliata è il danno che il blocco previene.
/// </summary>
public sealed class BitgetAttestationOptions
{
    public bool SpotMarketBuyVerified { get; set; }
}
