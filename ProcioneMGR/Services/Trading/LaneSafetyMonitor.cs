using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Risk;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Riceve il profilo di rischio della corsia. Separato dal monitor per non costringere chi lo
/// imposta (il <see cref="TradingEngine"/> all'avvio) a conoscere l'implementazione concreta.
/// </summary>
public interface ILaneRiskProfileSink
{
    /// <summary>
    /// Imposta (o azzera, con <c>null</c>) il profilo attivo per la corsia. Idempotente.
    /// </summary>
    void SetProfile(RiskProfile? profile);

    /// <summary>Profilo attualmente attivo, o <c>null</c> se la corsia usa le soglie globali.</summary>
    RiskProfile? Profile { get; }
}

/// <summary>
/// [R3] Soglie di sicurezza EFFETTIVE di una corsia: profilo della corsia sovrapposto alla
/// configurazione globale.
///
/// Prima di R3 <see cref="SafetyConfiguration"/> era globale — un'unica sezione di
/// <c>appsettings.json</c> condivisa da tutte le corsie — quindi un "profilo di rischio" non poteva
/// essere per-corsia, e due corsie non potevano avere appetiti al rischio diversi.
///
/// PERCHÉ IMPLEMENTA <see cref="IOptionsMonitor{T}"/> invece di introdurre un'astrazione nuova: le
/// soglie sono lette in ~19 punti fra <c>TradingEngine</c>, <c>SafetyChecker</c>,
/// <c>PositionOpener</c>, <c>PositionCloser</c>, <c>SignalOrderBuilder</c> ed
/// <c>ExecutionSlicePlanner</c>, tutti già scritti contro questa interfaccia. Rispettandola, il
/// profilo per-corsia entra in vigore ovunque senza toccare un solo punto di lettura — e senza il
/// rischio, in un cambiamento a tappeto, di dimenticarne uno proprio sul percorso dei soldi.
///
/// L'hot-reload resta intatto: <see cref="CurrentValue"/> ricompone a ogni accesso partendo dal
/// valore corrente del monitor globale, quindi una modifica ad appsettings.json continua a
/// propagarsi entro ~1s sia alle corsie senza profilo sia ai campi che il profilo non possiede
/// (commissione, margine di mantenimento, bande di plausibilità dei fill).
/// </summary>
public sealed class LaneSafetyMonitor(IOptionsMonitor<SafetyConfiguration> global)
    : IOptionsMonitor<SafetyConfiguration>, ILaneRiskProfileSink
{
    private volatile RiskProfile? _profile;

    public RiskProfile? Profile => _profile;

    public void SetProfile(RiskProfile? profile) => _profile = profile;

    public SafetyConfiguration CurrentValue
    {
        get
        {
            var baseline = global.CurrentValue;
            return _profile is RiskProfile p ? p.Apply(baseline) : baseline;
        }
    }

    public SafetyConfiguration Get(string? name) => CurrentValue;

    /// <summary>
    /// Inoltra le notifiche del monitor globale. Il cambio di PROFILO non notifica di proposito:
    /// avviene solo all'avvio della corsia, quando il motore sta già rileggendo tutto.
    /// </summary>
    public IDisposable? OnChange(Action<SafetyConfiguration, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return global.OnChange((_, name) => listener(CurrentValue, name));
    }
}
