namespace ProcioneMGR.Services.Research;

/// <summary>
/// [K10, PRD autonomia-piena 2026-08-31] <b>L'archivio della ricerca si aggiorna da solo.</b>
///
/// <para>Fino a oggi <see cref="IResearchCandidateIndexer"/> era iniettato in un solo posto —
/// <c>ResearchPageService</c> — e quindi l'indice cresceva <b>solo quando un umano apriva
/// <c>/research</c></b>. Misura del 2026-08-30: ultimo run indicizzato il <b>2026-08-25 13:15</b>
/// (il giorno della ricostruzione manuale dopo l'incidente), <b>34 run completati dopo, nessuno
/// indicizzato</b>. La macchina della ricerca girava 4-8 volte al giorno e depositava nel vuoto.</para>
///
/// <para>È la stessa forma del difetto che [J7] ha chiuso per le coppie — «costruito, collaudato e
/// mai azionato» — e questo worker è deliberatamente il suo gemello: stessa struttura, stesso
/// ritardo iniziale, stessa politica sugli errori. Uno strumento che esiste solo dietro un click
/// che nessuno dà non è uno strumento.</para>
///
/// <para><b>Cosa NON ripara.</b> L'archivio nutre <c>/research</c> e il ragionamento sulla fascia
/// grigia, non la flotta: <c>FleetStateReader</c> legge <c>PipelineRuns.RecommendationJson</c> e
/// gli artefatti, non questa tabella. Chi si aspettasse che indicizzare sblocchi lo schieramento
/// resterebbe deluso — è la leva sbagliata, e va detto qui perché è la confusione naturale.</para>
///
/// <para>Nessuna manopola nuova: non è una politica da tarare, è manutenzione dell'indice. I
/// pulsanti della pagina restano per il controllo manuale e per la ricostruzione totale.</para>
/// </summary>
public sealed class ResearchIndexSyncWorker(
    IResearchCandidateIndexer indexer,
    ILogger<ResearchIndexSyncWorker> logger) : BackgroundService
{
    /// <summary>Attesa iniziale: il DB dev'essere migrato e pronto. Stessa del gemello J7.</summary>
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Cadenza. Mezz'ora contro le 4-8 cacce al giorno: l'arretrato massimo è un run, e a indice
    /// allineato il giro costa una query. Più stretto del gemello delle coppie (un'ora) perché qui
    /// la produzione è continua, non notturna.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

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
            var r = await indexer.IndexNewRunsAsync(ct);
            if (r.RunsIndexed > 0)
            {
                logger.LogInformation(
                    "Archivio ricerca: indicizzati {Runs} run nuovi ({Candidati} candidati){Saltati}.",
                    r.RunsIndexed, r.CandidatesIndexed,
                    r.RunsSkipped > 0 ? $", {r.RunsSkipped} SALTATI (payload illeggibile)" : "");
            }
            else if (r.RunsSkipped > 0)
            {
                // Un run saltato non è un run allineato: senza questa riga l'unica traccia del
                // guasto sarebbe il silenzio (regola 4: fail-open, ma DICHIARATO).
                logger.LogWarning(
                    "Archivio ricerca: NESSUN run indicizzato e {Saltati} SALTATI — l'indice non è allineato agli artefatti.",
                    r.RunsSkipped);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Il prossimo giro riprova: un guasto transitorio del DB non deve uccidere il worker.
            logger.LogWarning(ex, "Archivio ricerca: giro di indicizzazione fallito, riprovo al prossimo.");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
