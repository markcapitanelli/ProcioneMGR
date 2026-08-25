namespace ProcioneMGR.Services.PairsTrading;

/// <summary>
/// [J7, PRD autonomia-operativa 2026-08-25] <b>L'indicizzazione automatica delle coppie.</b>
///
/// L'indice (<see cref="IPairCandidateIndexer"/>, item I14) era costruito, collaudato e
/// raggiungibile dai pulsanti di <c>/pairs-trading</c> — e <b>mai azionato</b>: al 2026-08-25
/// <c>PairCandidates</c> era a 0 righe con <b>174 artefatti PairScreen</b> scritti dal 2026-07-02
/// (l'ultimo del 2026-08-23, la produzione è viva). Il test di cointegrazione si pagava ogni notte
/// e si buttava. Uno strumento che esiste solo dietro un click che nessuno dà è la stessa classe
/// del «costruito e mai letto» che I14 doveva chiudere: il rimedio era giusto, mancava il braccio
/// che lo aziona da solo.
///
/// <para>Primo giro poco dopo l'avvio (recupera l'arretrato al primo deploy), poi ogni
/// <see cref="Interval"/>: l'incrementale è idempotente e a indice allineato costa una query.
/// Nessuna manopola: non è una politica da tarare, è manutenzione dell'indice — i pulsanti della
/// pagina restano per il controllo manuale e la ricostruzione totale.</para>
/// </summary>
public sealed class PairIndexSyncWorker(
    IPairCandidateIndexer indexer,
    ILogger<PairIndexSyncWorker> logger) : BackgroundService
{
    /// <summary>Attesa iniziale: il DB deve essere migrato e pronto.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);

    /// <summary>Cadenza: le cacce che scrivono PairScreen sono notturne, un'ora è già generosa.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            await TickOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Un giro di indicizzazione. Pubblico per i test.</summary>
    public async Task TickOnceAsync(CancellationToken ct)
    {
        try
        {
            var r = await indexer.IndexNewAsync(ct);
            if (r.RunsIndexed > 0)
            {
                logger.LogInformation(
                    "Indice coppie: indicizzati {Runs} run nuovi ({Pairs} coppie){Skipped}.",
                    r.RunsIndexed, r.PairsIndexed,
                    r.RunsSkipped > 0 ? $", {r.RunsSkipped} SALTATI (payload illeggibile)" : "");
            }
            else if (r.RunsSkipped > 0)
            {
                // Un run saltato non è un run allineato: senza questa riga l'unica traccia del
                // guasto sarebbe il silenzio (regola 4: fail-open dichiarato).
                logger.LogWarning(
                    "Indice coppie: NESSUN run indicizzato e {Skipped} SALTATI — l'indice non è allineato agli artefatti.",
                    r.RunsSkipped);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Il prossimo giro riprova: un guasto transitorio del DB non deve uccidere il worker.
            logger.LogWarning(ex, "Indice coppie: giro di indicizzazione fallito, riprovo al prossimo.");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
