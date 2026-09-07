using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Analysis;

/// <summary>
/// [2026-09-07] <b>Il guardiano che parla senza che nessuno apra il pannello.</b>
///
/// <para><b>Perché esiste.</b> Un pannello con un bottone risponde a chi lo preme. Questa
/// piattaforma ha già una collezione documentata di meccanismi che misurano correttamente e poi
/// tacciono: il funding sparito due volte in silenzio, la pagina dei backup cieca per diciotto
/// giorni su dieci dump sani, il comitato AI senza quorum per sedici giorni, la quarta scheda della
/// Home cieca per diciannove giorni proprio sui soldi. Aggiungere un sesto misuratore muto sarebbe
/// ripetere l'errore con una motivazione nuova.</para>
///
/// <para><b>Che cosa dice, e quando.</b> Due eventi, entrambi rari per costruzione:</para>
/// <list type="number">
/// <item><b>Scarto dal previsto</b> — le uscite osservate si discostano dal nominale del bracket
/// oltre la soglia. Ha due letture opposte e la notifica le distingue: più stop del previsto
/// significa che il segnale entra male; meno stop significa che il segnale sta aggiungendo
/// qualcosa, ed è la prima buona notizia misurabile che questa piattaforma possa produrre.</item>
/// <item><b>Corsia diventata giudicabile</b> — ha accumulato abbastanza uscite vive perché il
/// confronto abbia potere. Va detto <i>una volta</i>, perché è il momento in cui la corsia smette
/// di essere un'ipotesi e comincia a essere una misura. Al ritmo dichiarato dalle gambe questo
/// accade dopo mesi, e senza una notifica passerebbe inosservato.</item>
/// </list>
///
/// <para><b>Il silenzio è l'esito normale.</b> Non si notifica «come previsto»: sarebbe un allarme
/// che suona sempre, cioè un allarme che nessuno guarda. E non si notifica «non giudicabile», che è
/// lo stato in cui quasi tutte le corsie vivranno per mesi.</para>
///
/// <para><b>Non scrive niente.</b> Né configurazioni, né parametri di rischio, né soglie. È
/// diagnostica: fail-open, un ciclo fallito si ritenta al prossimo giro e non ferma nulla.</para>
/// </summary>
public sealed class BracketDiagnosisWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<BracketDiagnosisOptions> options,
    ILogger<BracketDiagnosisWorker> logger,
    INotifier? notifier = null) : BackgroundService
{
    /// <summary>Ultimo verdetto notificato per corsia: si parla ai CAMBIAMENTI, non a ogni giro.</summary>
    private readonly Dictionary<int, VerdettoBracket> _ultimoDetto = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ore = Math.Clamp(options.CurrentValue.CheckIntervalHours, 1, 168);
        logger.LogInformation("Guardiano della diagnosi del bracket avviato (ogni {Ore}h, attivo={Attivo}).",
            ore, options.CurrentValue.Enabled);

        // Ritardo iniziale: all'avvio le corsie stanno ancora restaurando lo stato, e una misura
        // presa in quella finestra descriverebbe il riavvio invece del bracket.
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(ore));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ciclo del guardiano del bracket fallito; ritento al prossimo giro.");
            }
        }
        while (await AttendiAsync(timer, stoppingToken));

        logger.LogInformation("Guardiano della diagnosi del bracket fermato.");
    }

    private static async Task<bool> AttendiAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Un giro su tutte le corsie. Pubblico per il collaudo: nessun tempo di attesa dentro.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled) return;

        using var scope = scopeFactory.CreateScope();
        var servizio = scope.ServiceProvider.GetRequiredService<BracketDiagnosisService>();
        var diagnosi = await servizio.DiagnoseAllAsync(ct);

        foreach (var d in diagnosi)
        {
            var precedente = _ultimoDetto.TryGetValue(d.LaneId, out var v) ? v : (VerdettoBracket?)null;
            _ultimoDetto[d.LaneId] = d.Verdetto;

            // Nessuna novità: stesso verdetto del giro scorso. Il silenzio è l'esito normale.
            if (precedente == d.Verdetto) continue;

            switch (d.Verdetto)
            {
                case VerdettoBracket.ScartoDalPrevisto:
                    await AvvisaAsync(NotificationSeverity.Warning,
                        $"Corsia {d.LaneId}: uscite fuori dal previsto",
                        $"{d.Symbol} {d.Timeframe} — {d.Racconto}", ct);
                    logger.LogWarning("Corsia {LaneId} ({Symbol} {Tf}): {Racconto}",
                        d.LaneId, d.Symbol, d.Timeframe, d.Racconto);
                    break;

                // Il passaggio a giudicabile si annuncia UNA volta, e solo se prima non lo era:
                // è il momento in cui la corsia comincia a produrre una misura invece che un'attesa.
                case VerdettoBracket.ComePrevisto when precedente is VerdettoBracket.NonGiudicabile:
                    await AvvisaAsync(NotificationSeverity.Info,
                        $"Corsia {d.LaneId}: ora è giudicabile",
                        $"{d.Symbol} {d.Timeframe} — {d.Racconto}", ct);
                    logger.LogInformation("Corsia {LaneId} diventata giudicabile: {Racconto}", d.LaneId, d.Racconto);
                    break;

                default:
                    // «Non giudicabile» e «non misurabile» non si notificano: sono lo stato in cui
                    // quasi tutte le corsie vivranno per mesi, e un avviso che suona sempre non è
                    // un avviso. Restano nel registro, dove chi vuole li trova.
                    logger.LogInformation("Corsia {LaneId} ({Symbol} {Tf}) → {Verdetto}: {Racconto}",
                        d.LaneId, d.Symbol, d.Timeframe, d.Verdetto, d.Racconto);
                    break;
            }
        }
    }

    private async Task AvvisaAsync(NotificationSeverity gravita, string titolo, string corpo, CancellationToken ct)
    {
        if (notifier is null) return;
        try { await notifier.NotifyAsync(gravita, titolo, corpo, ct); }
        catch (Exception ex)
        {
            // Un canale muto non deve spegnere la misura: il verdetto resta nel log e nel pannello.
            logger.LogWarning(ex, "Notifica della diagnosi del bracket non recapitata.");
        }
    }
}
