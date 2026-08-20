using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Backtesting;

/// <summary>
/// [revisione algoritmi 2026-08-20] <b>La strategia tiene uno specchio della posizione aperta, e
/// qualcuno deve rimetterglielo davanti.</b>
///
/// <para><b>Il difetto che questa interfaccia esiste per chiudere.</b> Quattro strategie —
/// GridMeanReversion, DonchianBreakout, EventTrigger e (in altra forma) RegimeConditional — tengono
/// uno stato interno che rispecchia la posizione del motore: i loro stessi commenti lo dicono
/// («specchio della posizione del motore», «mirrors the engine position»). Nel <b>backtest</b> il
/// motore istanzia la strategia UNA volta e scorre le candele, quindi lo specchio si mantiene. Nel
/// <b>trading vivo</b> il motore crea un'istanza NUOVA a ogni candela e chiama
/// <c>InitializeAsync</c>, che azzera lo specchio.</para>
///
/// <para>La conseguenza, misurata leggendo il codice: i rami di uscita che dipendono da
/// <c>_side != 0</c> <b>non si raggiungono mai dal vivo</b>. Grid apriva e non prendeva mai il
/// proprio profitto; la posizione restava fino al bracket SL/TP o a un segnale opposto. Al
/// 2026-08-20 GridMeanReversion girava su <b>due corsie Paper</b> (4 XRP/USDT e 5 UNI/USDT), e la
/// corsia 4 aveva chiuso <b>un solo trade in sedici giorni</b>.</para>
///
/// <para>È la peggior specie di difetto d'integrazione: il backtest valida una strategia, e dal vivo
/// ne opera un'altra — con metà della logica spenta e nessuna superficie che lo dica. Ogni
/// conclusione tratta da quelle corsie era su un oggetto diverso da quello validato.</para>
///
/// <para><b>Perché così e non cachando l'istanza.</b> Tenere viva la strategia fra le candele
/// sembrerebbe più semplice, ma <c>InitializeAsync</c> deve comunque essere richiamato a ogni barra:
/// ricalcola gli indicatori sulla finestra che si allunga. Sarebbe rimasto lo stesso azzeramento,
/// con in più uno stato lungo da gestire. Qui invece il motore RIDICE alla strategia ciò che gia'
/// sa — la posizione vera, che è la sorgente autorevole — e lo specchio torna a combaciare con
/// l'originale per costruzione.</para>
/// </summary>
public interface IPositionMirroringStrategy
{
    /// <summary>
    /// Rimette lo specchio in pari con la posizione VERA del motore. Chiamata dopo
    /// <c>InitializeAsync</c> e prima di <c>EvaluateSignal</c>, a ogni candela.
    /// </summary>
    /// <param name="side">Lato aperto, o <c>null</c> se la strategia è flat.</param>
    /// <param name="entryPrice">Prezzo di ingresso della posizione aperta (0 se flat).</param>
    /// <param name="openedAtUtc">Quando la posizione è stata aperta (serve a chi conta le barre).</param>
    void RestorePosition(OrderSide? side, decimal entryPrice, DateTime openedAtUtc);
}
