using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Services.Health;

/// <summary>Lo stato della macchina della ricerca, a TRE stati: due non bastano (regola 4).</summary>
public enum ResearchLivenessState
{
    /// <summary>Un run recente c'è, o uno è in corso adesso: la macchina cerca.</summary>
    Viva,

    /// <summary>Nessun run da più della soglia: la macchina NON sta cercando, e va detto.</summary>
    Ferma,

    /// <summary>La lettura è fallita. NON è «viva»: è ignoranza, e si dichiara.</summary>
    NonMisurabile,
}

/// <summary>
/// [J3] I fatti da cui si deriva il verdetto, separati dal giudizio (stessa forma di
/// <see cref="AgentStateFacts"/>): la parte pura si prova senza database e senza orologio.
/// </summary>
/// <param name="DistinctCandidates24h">
/// CandidateKey DISTINTI valutati nelle 24h — mai il conteggio righe: ogni caccia ri-registra la
/// griglia e le righe sono ~19× le chiavi (l'artefatto che ha già prodotto una misura sbagliata,
/// usata per abbassare GreyZone.DsrFloor, rettificata il 2026-08-21).
/// </param>
/// <param name="NewCandidates7d">
/// Chiavi MAI viste prima degli ultimi 7 giorni: la novità, che è un'altra cosa dall'attività —
/// 90 run sulla stessa griglia congelata producono attività e zero novità, ed è esattamente il
/// guasto che questa sonda esiste per rendere visibile.
/// </param>
/// <param name="LastHoldoutToUtc">
/// L'estremo destro dell'ultima finestra di holdout usata (dallo snapshot del run più recente):
/// un holdout fermo a un mese fa dice che la ricerca sta giudicando un mercato che non c'è più.
/// </param>
public sealed record ResearchLivenessFacts(
    DateTime? LastRunCompletedUtc,
    int RunsInProgress,
    int RunsCompleted24h,
    int DistinctCandidates24h,
    int NewCandidates7d,
    DateTime? LastHoldoutToUtc,
    string? CampaignStatusSummary);

/// <summary>Il verdetto, coi fatti accanto perché la card li mostri senza rileggerli.</summary>
public sealed record ResearchLivenessReport(
    ResearchLivenessState State,
    string Reason,
    ResearchLivenessFacts? Facts,
    DateTime CheckedAtUtc);

/// <summary>
/// [J3, PRD autonomia-operativa 2026-08-25] <b>La sonda «la ricerca è viva»</b>.
///
/// Nasce da un fatto: il 2026-08-23 alle 04:25 la macchina della ricerca si è fermata e per oltre
/// 43 ore NESSUNA superficie lo ha detto. Chi apriva lo scheduler vedeva tredici configurazioni con
/// <c>ScheduleEnabled=false</c> e concludeva che non gira niente; chi leggeva
/// <c>Campaign:Enabled=true</c> concludeva l'opposto. Serviva il numero che distingue «non ha
/// trovato» da «non ha cercato» — e non esisteva.
///
/// <para>Il verdetto è a TRE stati perché una query fallita non è «viva» (fail-open sulla
/// diagnostica, ma dichiarato — regola 4); e un run IN CORSO conta come vita anche se l'ultimo
/// completato è vecchio, altrimenti ogni caccia lunga produrrebbe un falso allarme a metà corsa.</para>
///
/// <para>Non notifica, di proposito: come per <see cref="AgentStateProbe"/>, lo stato è una
/// condizione, non un evento — si mostra in Home, non si spende il budget degli allarmi veri.</para>
/// </summary>
public sealed class ResearchLivenessProbe(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOptionsMonitor<CampaignOptions> campaign,
    ILogger<ResearchLivenessProbe> logger)
{
    public async Task<ResearchLivenessReport> ProbeAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        ResearchLivenessFacts facts;
        try
        {
            facts = await ReadFactsAsync(now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sonda della ricerca: lettura fallita — verdetto NON MISURABILE.");
            return new ResearchLivenessReport(ResearchLivenessState.NonMisurabile,
                $"lettura fallita ({ex.GetType().Name}): non si può dire se la ricerca giri", null, now);
        }

        var stallHours = Math.Max(1, campaign.CurrentValue.StallAlertHours);
        return Judge(facts, TimeSpan.FromHours(stallHours), now);
    }

    /// <summary>[J3] Il giudizio, PURO: stesso stato ⇒ stesso verdetto, provabile senza DB.</summary>
    internal static ResearchLivenessReport Judge(ResearchLivenessFacts facts, TimeSpan stallThreshold, DateTime nowUtc)
    {
        if (facts.RunsInProgress > 0)
        {
            return new ResearchLivenessReport(ResearchLivenessState.Viva,
                $"un run è in corso adesso ({facts.RunsCompleted24h} completati nelle 24h)", facts, nowUtc);
        }

        if (facts.LastRunCompletedUtc is not DateTime last)
        {
            return new ResearchLivenessReport(ResearchLivenessState.Ferma,
                "nessun run completato risulta nel database: la macchina non ha mai cercato (o la storia è stata svuotata)",
                facts, nowUtc);
        }

        var age = nowUtc - last;
        if (age > stallThreshold)
        {
            return new ResearchLivenessReport(ResearchLivenessState.Ferma,
                $"ultimo run completato {age.TotalHours:F0}h fa e nessuno in corso"
                + (facts.CampaignStatusSummary is { Length: > 0 } cs ? $" — {cs}" : ""),
                facts, nowUtc);
        }

        return new ResearchLivenessReport(ResearchLivenessState.Viva,
            $"ultimo run completato {(age.TotalHours < 1 ? $"{age.TotalMinutes:F0}′" : $"{age.TotalHours:F0}h")} fa",
            facts, nowUtc);
    }

    private async Task<ResearchLivenessFacts> ReadFactsAsync(DateTime now, CancellationToken ct)
    {
        var da24h = now.AddHours(-24);
        var da7d = now.AddDays(-7);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var lastCompleted = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.CompletedAt != null)
            .MaxAsync(r => r.CompletedAt, ct);

        // SOLO Running: la pausa è un atto dell'operatore, non attività della macchina. Contare
        // i Paused ha tenuto la card su «un run è in corso adesso» grazie a TRE run in pausa da
        // luglio, mentre nessuno cercava (2026-08-28) — la sonda nata contro i controlli che
        // rassicurano ne era diventata uno.
        var inProgress = await db.PipelineRuns.AsNoTracking()
            .CountAsync(r => r.Status == "Running", ct);

        var completed24h = await db.PipelineRuns.AsNoTracking()
            .CountAsync(r => r.Status == "Completed" && r.CompletedAt >= da24h, ct);

        // DISTINCT sulle chiavi, mai sulle righe (vedi il commento sul record).
        var distinct24h = await db.ResearchCandidates.AsNoTracking()
            .Where(c => c.RunCompletedUtc >= da24h)
            .Select(c => c.CandidateKey)
            .Distinct()
            .CountAsync(ct);

        var new7d = await db.ResearchCandidates.AsNoTracking()
            .Where(c => c.RunCompletedUtc >= da7d)
            .Where(c => !db.ResearchCandidates.Any(e => e.CandidateKey == c.CandidateKey && e.RunCompletedUtc < da7d))
            .Select(c => c.CandidateKey)
            .Distinct()
            .CountAsync(ct);

        // L'estremo destro dell'holdout dell'ultimo run: dallo SNAPSHOT del run (le date risolte,
        // J2), non dalla config — la config relativa non ha date assolute vere.
        DateTime? holdoutTo = null;
        var lastSnapshot = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.Status == "Completed" && r.CompletedAt != null)
            .OrderByDescending(r => r.CompletedAt)
            .Select(r => r.ContextSnapshotJson)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(lastSnapshot))
        {
            try
            {
                var ctx = System.Text.Json.JsonSerializer.Deserialize<PipelineContext>(lastSnapshot);
                if (ctx?.Ranges is { } r && r.HoldoutTo > DateTime.MinValue) holdoutTo = r.HoldoutTo;
            }
            catch (System.Text.Json.JsonException) { /* snapshot illeggibile: si tace il dato, non il verdetto */ }
        }

        // Lo stato delle campagne abilitate, per dare al «ferma» il suo perché più probabile.
        var campaigns = await db.VettingCampaigns.AsNoTracking()
            .Where(c => c.Enabled)
            .Select(c => new { c.Status, c.UpdatedAtUtc })
            .ToListAsync(ct);
        string? campaignSummary = campaigns.Count == 0
            ? null
            : string.Join("; ", campaigns
                .GroupBy(c => c.Status)
                .Select(g => g.Count() == 1
                    ? $"campagna in {g.Key} da {(now - g.Max(c => c.UpdatedAtUtc)).TotalHours:F0}h"
                    : $"{g.Count()} campagne in {g.Key}"));

        return new ResearchLivenessFacts(
            lastCompleted, inProgress, completed24h, distinct24h, new7d, holdoutTo, campaignSummary);
    }
}
