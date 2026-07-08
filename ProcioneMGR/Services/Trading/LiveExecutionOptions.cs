namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Opzioni dell'esecuzione live "a fette" (TWAP/VWAP/Iceberg su Testnet/Live). Sezione config
/// <c>Trading:LiveExecution</c>. Letta via <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// (hot-reload): <see cref="Enabled"/> è un interruttore di sicurezza, deve poter essere spento
/// senza riavviare l'app. Default safe-off, come ogni automazione della piattaforma.
/// </summary>
public sealed class LiveExecutionOptions
{
    /// <summary>Master switch. Default false: nessun piano di esecuzione viene mai creato o avanzato.</summary>
    public bool Enabled { get; set; }

    /// <summary>Finestra di esecuzione di default (minuti) se la strategia non ne specifica una propria.</summary>
    public int DefaultWindowMinutes { get; set; } = 5;

    /// <summary>Cadenza del worker che avanza le fette dovute.</summary>
    public int WorkerTickSeconds { get; set; } = 15;

    /// <summary>Grazia oltre la finestra prima di dichiarare abbandonate le fette non piazzabili.</summary>
    public int AbandonGraceMinutes { get; set; } = 5;
}
