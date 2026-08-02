using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Esito del confronto fra il numero di corsie del guscio e quello del motore remoto.
/// </summary>
/// <param name="ShellLaneCount">Le corsie di QUESTO processo (<see cref="TradingLanes.Count"/>).</param>
/// <param name="EngineLaneCount">
/// Le corsie effettive del motore, come dichiarate da lui. <c>null</c> = non determinabile
/// (motore irraggiungibile o valore illeggibile): non è un disallineamento, è ignoranza — e le due
/// cose vanno dette in modo diverso.
/// </param>
/// <param name="Detail">Come si è arrivati al verdetto, per il log e per un eventuale banner.</param>
public sealed record LaneCountCoherenceResult(
    int ShellLaneCount,
    int? EngineLaneCount,
    string Detail,
    DateTime CheckedAtUtc)
{
    /// <summary>Vero solo quando ENTRAMBI i numeri sono noti e diversi.</summary>
    public bool Mismatch => EngineLaneCount is int engine && engine != ShellLaneCount;
}

/// <summary>
/// [AF0] Il numero di corsie è TOPOLOGIA duplicata per necessità in due processi: il guscio lo
/// legge dal proprio <c>Trading:LaneCount</c>, il motore in-cluster dal suo (ConfigMap
/// <c>trading-config.env</c>), e ognuno lo congela alla prima lettura. Un disallineamento produce
/// corsie che il guscio vede e il motore non ha (comandi rifiutati dal validatore gRPC) o, peggio,
/// corsie che il motore fa girare e il guscio non mostra — motori senza occhi.
///
/// Fino a oggi il sintomo esisteva ma era TARDIVO: lo si scopriva dal primo comando fallito su una
/// corsia alta, molto lontano dalla causa. Questa sonda lo dice all'avvio, con la stessa voce del
/// <see cref="Security.MasterKeyProbe"/>: LogCritical + notifica, una volta.
///
/// Vive SOLO nel monolite in modalità remota (registrata nel ramo <c>useRemote</c> di
/// <c>AddTradingLanes</c>): col motore in-process i due numeri escono per costruzione dallo stesso
/// file, e non c'è nulla da sorvegliare.
/// </summary>
public sealed class LaneCountCoherenceProbe(
    IEngineConfigStore store,
    ILogger<LaneCountCoherenceProbe> logger,
    INotifier? notifier = null)
{
    private const string Section = "Trading:LaneCount";

    /// <summary>Ultimo esito, per un eventuale banner diagnostico (stesso patto di MasterKeyProbe.Result).</summary>
    public LaneCountCoherenceResult? Result { get; private set; }

    /// <summary>
    /// Legge il valore effettivo dal motore e lo confronta col proprio. Non lancia per
    /// irraggiungibilità: quella ha già la sua superficie (ogni pannello che parla col motore la
    /// dichiara), e la sonda non deve trasformarla in un secondo allarme.
    /// </summary>
    public async Task<LaneCountCoherenceResult> ProbeAsync(CancellationToken ct = default)
    {
        var shell = TradingLanes.Count;
        var snapshot = await store.ReadAsync([Section], ct);

        LaneCountCoherenceResult result;
        if (!snapshot.Reachable)
        {
            result = new LaneCountCoherenceResult(shell, null,
                $"Motore non raggiungibile: {snapshot.Error ?? "nessun dettaglio"}.", DateTime.UtcNow);
        }
        else
        {
            var view = snapshot.Sections.FirstOrDefault(
                s => string.Equals(s.Path, Section, StringComparison.OrdinalIgnoreCase));
            result = Interpret(shell, view?.Json);
        }

        Result = result;

        if (result.Mismatch)
        {
            var body =
                $"Il guscio ha {result.ShellLaneCount} corsie, il motore {result.EngineLaneCount}. " +
                "I comandi sulle corsie che il motore non ha verranno rifiutati; le corsie che il motore ha in più " +
                "girano senza che la UI le mostri. Trading__LaneCount della ConfigMap del motore " +
                "(infra/k8s/trading/trading-config.env) e Trading:LaneCount del guscio devono combaciare, " +
                "e il valore si applica solo al riavvio di ciascun processo.";
            logger.LogCritical("CORSIE DISALLINEATE FRA GUSCIO E MOTORE: {Detail} {Body}", result.Detail, body);
            if (notifier is not null)
            {
                await notifier.NotifyAsync(NotificationSeverity.Critical,
                    "Corsie disallineate fra guscio e motore", body, ct);
            }
        }
        else if (result.EngineLaneCount is null)
        {
            logger.LogWarning("Coerenza corsie non verificabile: {Detail}", result.Detail);
        }
        else
        {
            logger.LogInformation("Corsie coerenti fra guscio e motore: {Count}. {Detail}", shell, result.Detail);
        }

        return result;
    }

    /// <summary>
    /// Il valore scalare arriva come JSON grezzo (di norma la stringa <c>"8"</c>, vedi
    /// EngineConfigService.SerializeSection; <c>"null"</c> = chiave assente dal file del motore,
    /// che quindi usa il default del codice). Un valore illeggibile NON diventa un default:
    /// sarebbe un allarme costruito sull'ignoranza.
    /// </summary>
    private static LaneCountCoherenceResult Interpret(int shell, string? json)
    {
        var now = DateTime.UtcNow;
        if (json is null)
        {
            return new LaneCountCoherenceResult(shell, null,
                "Il motore non ha restituito la sezione (build precedente al canale di configurazione?).", now);
        }

        if (json == "null")
        {
            return new LaneCountCoherenceResult(shell, TradingLanes.DefaultCount,
                $"Trading:LaneCount assente dalla configurazione del motore: vale il default del codice ({TradingLanes.DefaultCount}).", now);
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var raw = node?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => node.GetValue<int>().ToString(),
                System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
                _ => null,
            };
            if (int.TryParse(raw, out var engine))
            {
                return new LaneCountCoherenceResult(shell, engine, $"Il motore dichiara {engine} corsie.", now);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // cade nel ramo "illeggibile" qui sotto
        }

        return new LaneCountCoherenceResult(shell, null,
            $"Trading:LaneCount del motore illeggibile ({json}).", now);
    }
}

/// <summary>
/// Esegue la sonda all'avvio, con gli stessi tempi del <see cref="Security.MasterKeyProbeWorker"/>:
/// attesa iniziale (il port-forward o il pod possono non essere ancora su), poi qualche tentativo
/// distanziato. Ritenta anche sull'irraggiungibilità — che a freddo è lo stato normale — e si
/// arrende senza allarme se il motore resta muto: il silenzio del motore ha già i suoi allarmi.
/// </summary>
public sealed class LaneCountCoherenceProbeWorker(
    LaneCountCoherenceProbe probe,
    ILogger<LaneCountCoherenceProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        for (var attempt = 1; attempt <= 5 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var result = await probe.ProbeAsync(stoppingToken);
                if (result.EngineLaneCount is not null) return; // verdetto emesso (coerente o no)
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sonda di coerenza corsie fallita (tentativo {Attempt}/5): riprovo tra 30s.", attempt);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
