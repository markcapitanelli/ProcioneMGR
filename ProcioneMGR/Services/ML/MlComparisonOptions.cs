namespace ProcioneMGR.Services.ML;

/// <summary>
/// Opzioni del dual-read ML (Fase 2a, sezione config "Ml"). Il confronto col servizio remoto è
/// puramente OSSERVATIVO: non influenza mai una decisione di trading.
/// </summary>
public sealed class MlComparisonOptions
{
    /// <summary>
    /// Accende il confronto (hot-reload via IOptionsMonitor). <see cref="RemoteUrl"/> deve comunque
    /// essere valorizzato a startup perché il client gRPC venga registrato (cambiarlo richiede riavvio).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Indirizzo del servizio procionemgr-ml. Vuoto → client non registrato, confronto spento.</summary>
    public string? RemoteUrl { get; set; }

    /// <summary>Deadline della chiamata gRPC di confronto (ms). Stretto: è solo osservabilità.</summary>
    public int TimeoutMs { get; set; } = 300;
}
