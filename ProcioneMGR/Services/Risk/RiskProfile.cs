using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Risk;

/// <summary>
/// [R3] Profilo di rischio: l'UNICA scelta tecnica che la Modalità Semplice chiede all'utente,
/// insieme al capitale.
///
/// COSA UN PROFILO È: un insieme di VINCOLI — quanto capitale per posizione, quanta esposizione
/// totale, quanta perdita si tollera, e soprattutto QUANTO SPESSO si opera.
///
/// COSA UN PROFILO NON È: una scelta di strategia. Il PDF di partenza proponeva di mappare
/// "aggressivo → scalping", "prudente → DCA". In questa piattaforma le strategie sono un OUTPUT
/// verificato di discovery → walk-forward → gate anti-overfitting (Deflated Sharpe, PBO): lasciare
/// che un profilo scelga la strategia scavalcherebbe proprio la macchina che protegge
/// dall'overfitting. Il profilo decide quanto si rischia, non cosa si compra.
///
/// PERCHÉ IL TURNOVER È IL PARAMETRO PRINCIPALE. La misura di R2
/// (<c>docs/REPORT-1M-COSTI-R2.md</c>) ha stabilito che il costo dell'operatività è funzione del
/// turnover, non della risoluzione dei dati: con un round-turn dello 0,30% il cost drag va dal 3,4%
/// (≈0,6 trade/giorno) al 77% (≈28 trade/giorno) sulla stessa finestra. Per questo ogni profilo
/// dichiara un tetto di operazioni giornaliere, ed è un tetto VERO: si traduce in
/// <see cref="SafetyConfiguration.MinOrderIntervalSeconds"/>, che il <see cref="SafetyChecker"/>
/// applica a ogni APERTURA (le chiusure non passano dal safety check — vedi
/// <see cref="RiskProfile.MaxTradesPerDay"/>, che è ciò che rende sicuri intervalli di ore).
///
/// NESSUN PROFILO "SCALPING". Il PDF lo proponeva fra le opzioni per l'utente finale. R2 lo ha
/// escluso per misura, non per opinione: a 1m servono ~8,9 candele tipiche catturate per pareggiare
/// i costi contro 1,1 a 1h, e il cost drag è 22 volte più alto. Offrirlo significherebbe vendere il
/// modo più caro di perdere denaro.
/// </summary>
public sealed record RiskProfile(
    string Name,
    string DisplayName,
    string Description,
    decimal PositionSizePercent,
    decimal MaxPositionSizePercent,
    decimal MaxTotalExposurePercent,
    decimal MaxDailyLossPercent,
    decimal MaxDrawdownPercent,
    int MaxOpenPositions,
    int MinOrderIntervalSeconds,
    int MaxLeverageAllowed,
    IReadOnlyList<string> PreferredTimeframes)
{
    /// <summary>
    /// Tetto di operazioni complete al giorno implicato da <see cref="MinOrderIntervalSeconds"/>.
    /// Un giro completo costa DUE ordini (apertura + chiusura), quindi il divisore è 2×intervallo.
    ///
    /// NB sul perché intervalli di ore non sono pericolosi: il <see cref="SafetyChecker"/> è
    /// invocato SOLO sul percorso di APERTURA (<c>TradingEngine.ExecuteOpenAsync</c> e
    /// <c>ExecutionSlicePlanner</c>); le chiusure — stop loss, take profit, trailing, liquidazione,
    /// emergency stop — non passano da lì. Un intervallo lungo frena quindi i nuovi ingressi, e non
    /// può in nessun caso impedire a una posizione di essere chiusa.
    /// </summary>
    public decimal MaxTradesPerDay => 86_400m / (2m * MinOrderIntervalSeconds);

    /// <summary>
    /// Costo annuo stimato in % del capitale se il profilo operasse SEMPRE al suo tetto di turnover.
    ///
    /// È un tetto, non una previsione: quasi nessuna strategia satura il proprio limite. Serve a
    /// rendere visibile all'utente la lezione di R2 — "operare più spesso costa, ed ecco quanto" —
    /// prima che scelga, invece di scoprirlo dall'estratto conto.
    /// </summary>
    public decimal EstimatedAnnualCostPercent(decimal roundTurnPercent) =>
        MaxTradesPerDay * 365m * roundTurnPercent * (PositionSizePercent / 100m);

    /// <summary>
    /// Sovrappone il profilo alla configurazione globale.
    ///
    /// La divisione delle responsabilità è deliberata: il PROFILO possiede l'appetito al rischio
    /// (dimensioni, esposizione, perdite tollerate, frequenza, leva); la configurazione GLOBALE
    /// possiede i fatti della piazza (commissione reale, margine di mantenimento, bande di
    /// plausibilità dei fill, stop resting sull'exchange, conferma manuale in Live). Un utente non
    /// deve poter "scegliere" la commissione del proprio exchange, e un profilo non deve poter
    /// disattivare la conferma manuale degli ordini Live.
    /// </summary>
    public SafetyConfiguration Apply(SafetyConfiguration global)
    {
        ArgumentNullException.ThrowIfNull(global);
        return new SafetyConfiguration
        {
            // --- appetito al rischio: deciso dal profilo ---
            PositionSizePercent = PositionSizePercent,
            MaxPositionSizePercent = MaxPositionSizePercent,
            MaxTotalExposurePercent = MaxTotalExposurePercent,
            MaxDailyLossPercent = MaxDailyLossPercent,
            MaxDrawdownPercent = MaxDrawdownPercent,
            MaxOpenPositions = MaxOpenPositions,
            MinOrderIntervalSeconds = MinOrderIntervalSeconds,
            MaxLeverageAllowed = MaxLeverageAllowed,

            // --- fatti della piazza e reti di sicurezza: restano globali ---
            FeePercent = global.FeePercent,
            MaintenanceMarginPercent = global.MaintenanceMarginPercent,
            MaxFillPriceDeviationPercent = global.MaxFillPriceDeviationPercent,
            MaxFillQuantityDeviationPercent = global.MaxFillQuantityDeviationPercent,
            UseExchangeRestingStops = global.UseExchangeRestingStops,
            RequireManualConfirmationForLive = global.RequireManualConfirmationForLive,
        };
    }
}

/// <summary>
/// I profili offerti dalla Modalità Semplice. Tre, non dieci: la scelta deve essere fattibile da
/// chi non conosce il dominio.
///
/// I tetti di turnover sono calibrati sui numeri misurati in R2, non sull'intuizione. Là le uniche
/// configurazioni con un cost drag tollerabile (3,4% su sei mesi) giravano a ~0,6 operazioni al
/// giorno; a ~5/giorno il drag era già 24%, a ~28/giorno il 77%. Da qui la scala **0,5 / 0,75 / 1,5
/// operazioni al giorno**, che circonda il valore misurato invece di superarlo di un ordine di
/// grandezza. Una prima stesura proponeva 3 / 6 / 12 al giorno: la formula del costo annuo, girata
/// su quei valori, dava 16% / 66% / 131% l'anno di sole commissioni — cioè profili che perdono per
/// costruzione. La formula è validata contro R2: 0,57 trade/giorno al 10% di size dà 6,2%/anno,
/// contro il 3,43% su sei mesi effettivamente misurato.
///
/// INVARIANTE che ogni profilo DEVE rispettare (validata all'avvio della corsia e da test):
/// <c>PositionSizePercent × leva ≤ MaxPositionSizePercent ≤ MaxTotalExposurePercent</c>.
/// Violarla non produrrebbe un profilo "aggressivo" ma una corsia che non fa MAI trading, perché il
/// SafetyChecker rifiuterebbe ogni singolo ordine.
/// </summary>
public static class RiskProfiles
{
    public const string Prudente = "Prudente";
    public const string Equilibrato = "Equilibrato";
    public const string Dinamico = "Dinamico";

    public static readonly RiskProfile Conservative = new(
        Name: Prudente,
        DisplayName: "Prudente",
        Description: "Poche operazioni, posizioni piccole, nessuna leva. Punta a perdere poco nei "
                   + "periodi sfavorevoli, accettando di guadagnare meno in quelli favorevoli.",
        PositionSizePercent: 5m,
        MaxPositionSizePercent: 10m,
        MaxTotalExposurePercent: 20m,
        MaxDailyLossPercent: 2m,
        MaxDrawdownPercent: 10m,
        MaxOpenPositions: 2,
        MinOrderIntervalSeconds: 86_400,   // 24h fra ingressi ⇒ ≤0,5 operazioni/giorno (~2,7%/anno di costi)
        MaxLeverageAllowed: 1,
        PreferredTimeframes: ["4h", "1d"]);

    public static readonly RiskProfile Balanced = new(
        Name: Equilibrato,
        DisplayName: "Equilibrato",
        Description: "Via di mezzo: posizioni moderate, leva bassa, operatività contenuta. "
                   + "È il punto di partenza sensato per chi non ha una preferenza precisa.",
        PositionSizePercent: 8m,
        MaxPositionSizePercent: 20m,       // ≥ 8 × leva 2 = 16
        MaxTotalExposurePercent: 40m,
        MaxDailyLossPercent: 4m,
        MaxDrawdownPercent: 15m,
        MaxOpenPositions: 3,
        MinOrderIntervalSeconds: 57_600,   // 16h ⇒ ≤0,75 operazioni/giorno (~6,6%/anno di costi)
        MaxLeverageAllowed: 2,
        PreferredTimeframes: ["1h", "4h"]);

    public static readonly RiskProfile Dynamic = new(
        Name: Dinamico,
        DisplayName: "Dinamico",
        Description: "Posizioni più grandi, leva fino a 3x e operatività più frequente. "
                   + "Le oscillazioni del capitale sono sensibilmente più ampie, e i costi di "
                   + "transazione pesano molto di più.",
        PositionSizePercent: 10m,
        MaxPositionSizePercent: 35m,       // ≥ 10 × leva 3 = 30
        MaxTotalExposurePercent: 60m,
        MaxDailyLossPercent: 6m,
        MaxDrawdownPercent: 20m,
        MaxOpenPositions: 5,
        MinOrderIntervalSeconds: 28_800,   // 8h ⇒ ≤1,5 operazioni/giorno (~16%/anno di costi)
        MaxLeverageAllowed: 3,
        PreferredTimeframes: ["15m", "1h"]);

    public static IReadOnlyList<RiskProfile> All => [Conservative, Balanced, Dynamic];

    /// <summary>Il profilo predefinito quando l'utente non ha ancora scelto.</summary>
    public static RiskProfile Default => Balanced;

    /// <summary>
    /// Profilo per nome, oppure <c>null</c> se il nome è vuoto o sconosciuto. Il null NON è un
    /// errore: significa "questa corsia non usa la Modalità Semplice" e le soglie restano quelle
    /// globali — cioè il comportamento di ogni corsia esistente prima di R3.
    /// </summary>
    public static RiskProfile? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
