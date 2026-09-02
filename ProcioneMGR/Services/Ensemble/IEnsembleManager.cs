using ProcioneMGR.Services.Monitoring;

namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// Gestione dell'ensemble multi-strategia con allocazione dinamica del capitale basata
/// su Sharpe rolling. La performance è una simulazione storica deterministica: ogni
/// strategia membro viene backtestata sulla finestra, e il capitale viene riallocato
/// periodicamente in base alla Sharpe degli ultimi N giorni.
/// </summary>
public interface IEnsembleManager
{
    /// <summary>Corsia di trading isolata a cui appartiene questa istanza (0 = corsia di default).</summary>
    int LaneId { get; }

    Task<EnsembleConfiguration> GetConfigurationAsync(CancellationToken ct = default);
    /// <summary>
    /// [K48, 2026-09-02] <b><paramref name="writtenBy"/> è obbligatorio</b>, e non è burocrazia.
    ///
    /// <para>Questo metodo è l'unico punto da cui passa OGNI riscrittura della configurazione di una
    /// corsia — misurato: <b>dieci chiamanti</b>, dal braccio della flotta a una pagina che scrive
    /// sempre sulla corsia 0 senza saperlo. È anche l'azione meno reversibile della piattaforma:
    /// <c>EnsembleStates</c> tiene un solo <c>ConfigurationJson</c>, quindi ciò che c'era prima non
    /// esiste più da nessuna parte.</para>
    ///
    /// <para>Un parametro con default sarebbe un parametro omissibile, e in sei mesi il buco
    /// tornerebbe: chi aggiunge l'undicesimo scrittore non lo passa e nessuno se ne accorge. Così
    /// invece l'undicesimo scrittore <b>non compila</b> finché non dice chi è.</para>
    /// </summary>
    Task UpdateConfigurationAsync(EnsembleConfiguration config, ConfigWriteContext writtenBy, CancellationToken ct = default);
    Task<EnsembleStatus> GetStatusAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<EnsemblePerformance> GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default);
    Task RebalanceAsync(string reason = "Manual", CancellationToken ct = default);

    /// <summary>Confronta Sharpe realizzato (trade chiusi dal vivo) vs atteso (backtest/holdout) per ogni gamba attiva.</summary>
    Task<IReadOnlyList<DecayReport>> GetDecayReportsAsync(CancellationToken ct = default);
}
