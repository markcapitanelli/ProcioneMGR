using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Notifications;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Lo stato dell'ultimo giro, per il pannello: null = mai guardato da questo processo.</summary>
/// <param name="QuandoUtc">Quando è stato misurato.</param>
/// <param name="Cacce">Il costo di ognuna, come misurato.</param>
/// <param name="Budget">Il tetto in vigore, 0 = nessuno.</param>
/// <param name="Proposte">Che cosa rallentare, e di quanto. Vuoto = si sta dentro.</param>
/// <param name="Applicate">Vero = le proposte sono state scritte davvero.</param>
/// <param name="ScritturaFallita">
/// [Revisione 2026-09-03] Vero = l'applicazione automatica era ACCESA e la scrittura è FALLITA.
/// Senza questo terzo stato la notifica diceva «BudgetAutoApply è spento» a chi lo aveva acceso.
/// </param>
public sealed record BudgetReport(
    DateTime QuandoUtc,
    IReadOnlyList<CostoCaccia> Cacce,
    double Budget,
    IReadOnlyList<ProposteCadenza> Proposte,
    bool Applicate,
    bool ScritturaFallita = false)
{
    public double OreTotali => Cacce.Sum(c => c.OreAttualiAlMese);
    public double OreRisparmiabili => Proposte.Sum(p => p.OreRisparmiate);
    public string Racconto => HuntBudget.Racconta(Cacce, Budget);
}

/// <summary>
/// [K59, PRD autonomia-piena — Fase 4, 2026-09-03] <b>Il guardiano del budget di caccia: misura,
/// propone, e scrive solo se glielo si dice.</b>
///
/// <para><b>Perché propone invece di agire.</b> Riscrivere la cadenza di una caccia è una decisione
/// di budget del proprietario, e in questo filone la stessa scelta è già stata fatta due volte:
/// <c>GreyAutoDeploy</c> nasce spento, e K50 dichiara «nessuna azione automatica» sul sonno di una
/// caccia. Con <c>Campaign:BudgetAutoApply = false</c> (il default) il worker misura, dichiara e
/// notifica — che è esattamente ciò che serve per guardarlo girare prima di dargli il potere di
/// scrivere.</para>
///
/// <para><b>Che cosa misura.</b> Per ogni configurazione: la durata mediana <i>osservata</i>, le ore
/// al mese al ritmo corrente (contando la cadenza propria di K56), e la resa in chiavi grigie per
/// ora (K54b). Chi non ha ancora una durata misurata compare col costo a zero e <b>non viene mai
/// rallentato</b>: l'ignoranza non condanna.</para>
///
/// <para><b>Perché serve un tetto esplicito.</b> Il gate del DSR deflaziona per i tentativi del
/// proprio run e non vede le altre cacce: aggiungerne non lo rende più severo. Nessun freno scatta
/// da solo, e il controllo che scala col numero di cacce è K57 — non il DSR.</para>
/// </summary>
public sealed class HuntBudgetWorker(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IHuntYieldReader yieldReader,
    IOptionsMonitor<CampaignOptions> options,
    ILogger<HuntBudgetWorker> logger,
    INotifier? notifier = null) : BackgroundService
{
    /// <summary>L'ultimo giro, per <c>/pipeline</c>. Copia: la legge un circuito Blazor mentre il worker la muta.</summary>
    public BudgetReport? Ultimo { get; private set; }

    private bool _sforoNotificato;

    /// <summary>
    /// [Revisione 2026-09-03] Un giro per volta: <see cref="TickAsync"/> è chiamato dal ciclo del
    /// worker e — prima di questa revisione — anche dai pulsanti di <c>/pipeline</c>. Due giri
    /// sovrapposti con <c>BudgetAutoApply</c> acceso erano due scrittori sulla stessa riga di
    /// configurazione e due notifiche per lo stesso sforo.
    /// </summary>
    private readonly SemaphoreSlim _giro = new(1, 1);

    /// <summary>
    /// [Revisione 2026-09-03] <b>Misura e basta.</b> Legge i costi e calcola le proposte senza
    /// scrivere, senza notificare e senza toccare lo stato della notifica: è ciò che «Guarda
    /// adesso» deve fare. Prima il pulsante chiamava <see cref="TickAsync"/>, che con
    /// <c>Campaign:BudgetAutoApply</c> acceso riscriveva le cadenze e mandava la notifica — un
    /// pulsante etichettato come lettura con effetti di scrittura.
    /// </summary>
    public async Task<BudgetReport> MisuraAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        var cacce = await LeggiCostiAsync(ct);
        var proposte = HuntBudget.Riallinea(cacce, opt.MonthlyHourBudget);
        var report = new BudgetReport(DateTime.UtcNow, cacce, opt.MonthlyHourBudget, proposte, Applicate: false);
        Ultimo = report;
        return report;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stessa attesa iniziale degli altri worker: all'avvio la macchina ha di meglio da fare, e
        // il budget non è una decisione urgente.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            if (opt.BudgetTickMinutes > 0)
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Un guasto qui non deve fermare la caccia: questo worker non governa nulla di
                    // essenziale, misura. Ma si dichiara, perché un guardiano muto non è un guardiano.
                    logger.LogError(ex, "Giro del budget di caccia fallito; ritento al prossimo.");
                }
            }

            var attesa = TimeSpan.FromMinutes(Math.Clamp(opt.BudgetTickMinutes <= 0 ? 60 : opt.BudgetTickMinutes, 5, 1440));
            try { await Task.Delay(attesa, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Un giro completo: misura, propone, e scrive solo con <c>BudgetAutoApply</c>. Pubblico per i test.</summary>
    public async Task<BudgetReport> TickAsync(CancellationToken ct)
    {
        await _giro.WaitAsync(ct);
        try
        {
            return await TickSerializzatoAsync(ct);
        }
        finally
        {
            _giro.Release();
        }
    }

    private async Task<BudgetReport> TickSerializzatoAsync(CancellationToken ct)
    {
        var opt = options.CurrentValue;
        var cacce = await LeggiCostiAsync(ct);
        var proposte = HuntBudget.Riallinea(cacce, opt.MonthlyHourBudget);

        var applicate = false;
        var scritturaFallita = false;
        if (proposte.Count > 0 && opt.BudgetAutoApply)
        {
            applicate = await ApplicaAsync(proposte, ct);
            scritturaFallita = !applicate;
        }

        var report = new BudgetReport(DateTime.UtcNow, cacce, opt.MonthlyHourBudget, proposte, applicate, scritturaFallita);
        Ultimo = report;

        if (proposte.Count == 0)
        {
            // La guarigione è una notizia quanto lo sforo: senza, la notifica resterebbe «già data»
            // per sempre e la prossima volta tacerebbe.
            if (_sforoNotificato)
            {
                logger.LogInformation("Budget di caccia rientrato: {Racconto}", report.Racconto);
                _sforoNotificato = false;
            }
            return report;
        }

        logger.LogWarning("Budget di caccia: {Racconto} Proposte: {Proposte}",
            report.Racconto,
            string.Join(" · ", proposte.Select(p => $"cfg {p.ConfigurationId} {p.CadenzaAttuale}h→{p.CadenzaProposta}h")));

        if (_sforoNotificato || notifier is null) return report;
        // Una scrittura FALLITA non è «già notificato»: il giro dopo deve ridirlo, finché non riesce.
        if (!scritturaFallita) _sforoNotificato = true;
        try
        {
            var elenco = string.Join("\n", proposte.Select(p =>
                $"· config {p.ConfigurationId}: da {(p.CadenzaAttuale > 0 ? $"{p.CadenzaAttuale}h" : "nessun limite")} "
                + $"a {p.CadenzaProposta}h — {p.Perche}"));
            await notifier.NotifyAsync(scritturaFallita ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                applicate ? "Budget di caccia superato: cadenze riallineate"
                    : scritturaFallita ? "Budget di caccia superato: la riscrittura delle cadenze è FALLITA"
                    : "Budget di caccia superato",
                $"{report.Racconto}\n{elenco}\n\n"
                + (applicate
                    ? "Le cadenze sono state riscritte (Campaign:BudgetAutoApply è acceso). Vale per la rotazione di campagna e per i run a cron."
                    : scritturaFallita
                        ? "Campaign:BudgetAutoApply è ACCESO ma la scrittura delle cadenze non è riuscita (vedi il log del guscio): "
                          + "le proposte restano da applicare e il guardiano ritenterà al prossimo giro."
                        : "Nessuna modifica applicata: Campaign:BudgetAutoApply è spento, e rallentare una caccia "
                          + "resta una decisione tua. Le proposte sono in /pipeline."), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Anche la notifica del budget è fallita.");
        }
        return report;
    }

    /// <summary>
    /// Il costo di ogni configurazione ATTIVA, dalle durate osservate. Non si stima nulla: chi non
    /// ha mai girato compare a zero, ed è il conteggio che poi lo protegge dal rallentamento.
    /// </summary>
    internal async Task<IReadOnlyList<CostoCaccia>> LeggiCostiAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var configs = await db.PipelineConfigurations.AsNoTracking()
            .Where(c => c.ExecutionMode != "Disabled")
            .Select(c => new { c.Id, c.MinHoursBetweenRuns })
            .ToListAsync(ct);
        if (configs.Count == 0) return [];

        var da = DateTime.UtcNow.AddDays(-30);
        var run = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.CompletedAt != null && r.StartedAt > da)
            .Select(r => new { r.ConfigurationId, r.StartedAt, r.CompletedAt })
            .ToListAsync(ct);

        // La resa per ora viene dalla stessa lettura che alimenta il pannello (K54b): un secondo
        // calcolo qui sarebbe una seconda verità sullo stesso numero.
        var rese = (await yieldReader.ReadAsync(30, ct)).ToDictionary(r => r.ConfigurationId);

        var adesso = DateTime.UtcNow;
        var perConfig = run.GroupBy(r => r.ConfigurationId).ToDictionary(g => g.Key, g =>
        {
            var minuti = g.Select(r => (r.CompletedAt!.Value - r.StartedAt).TotalMinutes)
                .Where(m => m >= 0).OrderBy(m => m).ToList();
            if (minuti.Count == 0) return new CostoGrezzo(0d, 0d, 1d, 0);
            var i = minuti.Count / 2;
            var mediana = minuti.Count % 2 == 1 ? minuti[i] : (minuti[i - 1] + minuti[i]) / 2;
            // L'età della finestra si misura fino a ORA, non fra primo e ultimo run: con un run
            // solo lo span sarebbe zero, e «un giorno» proietterebbe quel run come quotidiano.
            var giorni = (adesso - g.Min(r => r.StartedAt)).TotalDays;
            return new CostoGrezzo(mediana, minuti.Sum() / 60.0, Math.Max(giorni, 1d), minuti.Count);
        });

        return [.. configs.Select(c =>
        {
            var m = perConfig.TryGetValue(c.Id, out var p) ? p : new CostoGrezzo(0d, 0d, 1d, 0);
            // [Revisione 2026-09-03] Le ore al mese AL RITMO IN VIGORE: durata MEDIA × il minore fra
            // il ritmo della cadenza e quello osservato. Così una cadenza appena riscritta si riflette
            // al giro dopo (niente sforo riproposto e raddoppi a catena), e una caccia lanciata una
            // volta a mano non vale 720/cadenza run al mese solo perché ha una cadenza scritta.
            var runAlMese = m.Run > 0 ? m.Run / m.Giorni * 30 : 0;
            var minutiMedi = m.Run > 0 ? m.Ore * 60 / m.Run : 0;
            var oreMese = HuntBudget.ProiettaOreAlMese(minutiMedi, runAlMese, c.MinHoursBetweenRuns);
            var resa = rese.TryGetValue(c.Id, out var y) ? y.KeysPerHour : 0;
            return new CostoCaccia(c.Id, m.Mediana, oreMese, resa, c.MinHoursBetweenRuns, m.Run, runAlMese);
        })];
    }

    /// <summary>Durata mediana, ore consumate e età della finestra in giorni: i numeri grezzi da cui nasce il costo.</summary>
    private readonly record struct CostoGrezzo(double Mediana, double Ore, double Giorni, int Run);

    private async Task<bool> ApplicaAsync(IReadOnlyList<ProposteCadenza> proposte, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            foreach (var p in proposte)
            {
                var riga = await db.PipelineConfigurations.FirstOrDefaultAsync(c => c.Id == p.ConfigurationId, ct);
                if (riga is null) continue;
                logger.LogWarning(
                    "Budget di caccia [HuntBudgetWorker, Campaign:BudgetAutoApply]: config {Id} cadenza {Da}h -> {A}h ({Perche}).",
                    riga.Id, riga.MinHoursBetweenRuns, p.CadenzaProposta, p.Perche);
                riga.MinHoursBetweenRuns = p.CadenzaProposta;
                riga.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Budget di caccia: {N} cadenze riscritte.", proposte.Count);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Fallire la scrittura non deve far sembrare che sia riuscita: il report porta
            // Applicate=false e il pannello mostra le proposte come ancora da applicare.
            logger.LogError(ex, "Riscrittura delle cadenze fallita: le proposte restano da applicare.");
            return false;
        }
    }
}
