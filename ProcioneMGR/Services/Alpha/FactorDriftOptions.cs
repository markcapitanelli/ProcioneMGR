namespace ProcioneMGR.Services.Alpha;

/// <summary>
/// [M1, 2026-08-20] Sezione <c>FactorDrift</c>: governa <see cref="FactorDriftWorker"/>, il monitor
/// che misura periodicamente se l'information coefficient dei fattori alpha si sta spegnendo.
///
/// <para><b>Perché nasce un POCO per quattro chiavi che esistevano già.</b> Il worker le leggeva
/// direttamente da <c>IConfiguration</c> con <c>GetValue("FactorDrift:...", default)</c>, cioè
/// l'overload a tipo INFERITO. Il guardiano per sezione (<c>ConfigurationUiCoverageTests</c>) cerca
/// <c>GetValue&lt;T&gt;(</c> con argomento di tipo esplicito, quindi non vedeva la sezione; e il
/// guardiano per chiave (<c>ConfigurationKeyUiCoverageTests</c>) parte dai POCO registrati con
/// <c>Configure&lt;T&gt;</c>, e un POCO non c'era. Quattro chiavi vive, un worker vivo, e nessuno dei
/// due guardiani in grado di accorgersene: la sezione era amministrabile solo editando a mano un file
/// che per giunta non la contiene affatto — girava interamente sui default scritti nel codice.
/// Il POCO la rende visibile a entrambi i guardiani senza toccarne i regex, ed è la forma canonica
/// del progetto.</para>
///
/// <para><b>Perché <see cref="MaxSeries"/> meritava un pannello più delle altre.</b> È il numero di
/// serie della watchlist esaminate a ogni giro. Coi default (5 serie ogni 12 ore) coprire una
/// watchlist di ~220 serie richiede circa 22 giorni: «nessun allarme di deriva» può quindi voler dire
/// «non ho ancora guardato quella serie», e senza manopola l'operatore non aveva modo né di saperlo
/// né di cambiarlo. È la stessa forma dei controlli che rassicurano a prescindere dalla realtà.</para>
///
/// I vincoli qui sotto sono gli STESSI che il worker applicava al momento della lettura
/// (<c>Math.Max</c>/<c>Math.Clamp</c>): vivono adesso in un punto solo, e <c>AdminConfigRules</c> li
/// fa rispettare al salvataggio invece di correggerli in silenzio a ogni giro.
/// </summary>
public sealed class FactorDriftOptions
{
    /// <summary>
    /// Se il monitor gira. Default <b>true</b>: è in sola lettura e advisory — misura e scrive in
    /// tabella, non retrocede nulla e non tocca corsie. Spegnerlo azzera il costo di CPU periodico
    /// e fa invecchiare la fotografia mostrata in Home e /feature-selection.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ogni quante ore parte un giro. Minimo 1. Default 12.</summary>
    public int IntervalHours { get; set; } = 12;

    /// <summary>
    /// Quante serie della watchlist esaminare a ogni giro (rotazione: chi è stato provato più
    /// tempo fa ha la precedenza). Ammesso 1..30, default 5. È la manopola della COPERTURA:
    /// serie_totali ÷ MaxSeries × IntervalHours è il tempo per un giro completo.
    /// </summary>
    public int MaxSeries { get; set; } = 5;

    /// <summary>
    /// Tetto di candele lette per serie in un giro. Ammesso 1.000..200.000, default 20.000.
    /// Limita la finestra su cui si misura l'IC: alzarlo allunga lo storico osservato e il costo.
    /// </summary>
    public int MaxCandles { get; set; } = 20_000;

    // I tre valori "effettivi" sono METODI e non proprietà calcolate, come EffectiveStages() su
    // DriftMonitorOptions: AppConfigWriter serializza il POCO intero per riscrivere la sezione, e
    // una proprietà get-only finirebbe in appsettings.json come chiave inventata al primo salvataggio.

    /// <summary>Ore fra un giro e il successivo, già vincolate. Unico punto in cui il minimo vive.</summary>
    public int EffectiveIntervalHours() => Math.Max(1, IntervalHours);

    /// <summary>Serie per giro, già vincolate.</summary>
    public int EffectiveMaxSeries() => Math.Clamp(MaxSeries, 1, 30);

    /// <summary>Candele per serie, già vincolate.</summary>
    public int EffectiveMaxCandles() => Math.Clamp(MaxCandles, 1_000, 200_000);
}
