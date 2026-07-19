using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// Orchestrazione di <c>Components/Pages/Campaign.razor</c> (stesso pattern P1-5 delle altre
/// pagine: la logica sta qui, testabile senza Blazor; il componente fa solo rendering).
/// Registrato Scoped (un'istanza per circuito utente).
/// </summary>
public sealed class CampaignPageService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICampaignPlanner planner,
    IOptionsMonitor<CampaignOptions> options)
{
    public List<VettingCampaign> Campaigns { get; private set; } = [];
    public List<PipelineConfiguration> Configs { get; private set; } = [];
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    /// <summary>Gate globale (Campaign:Enabled): se spento, il planner non agisce qualunque cosa dica la campagna.</summary>
    public bool GloballyEnabled => options.CurrentValue.Enabled;

    public async Task RefreshAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        Campaigns = await db.VettingCampaigns.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        Configs = await db.PipelineConfigurations.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }

    public string ConfigName(int configurationId)
        => Configs.FirstOrDefault(c => c.Id == configurationId)?.Name ?? $"#{configurationId}";

    public static List<CampaignConfigState> StatesOf(VettingCampaign campaign)
        => CampaignPlanner.ParseConfigStates(campaign.ConfigStatesJson);

    public async Task CreateAsync(string name, IReadOnlyList<int> configurationIds, int backoffHours, bool autoStartPaper, string? userId)
    {
        if (string.IsNullOrWhiteSpace(name) || configurationIds.Count == 0)
        {
            SetMsg("Serve un nome e almeno una configurazione di caccia.", true);
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        db.VettingCampaigns.Add(new VettingCampaign
        {
            Name = name.Trim(),
            CreatedBy = userId ?? string.Empty,
            Enabled = false, // nasce spenta: l'attivazione è un gesto esplicito, come da PRD §4
            Status = CampaignStatus.Rotating,
            ConfigStatesJson = CampaignPlanner.SerializeConfigStates(
                configurationIds.Select(id => new CampaignConfigState { ConfigurationId = id }).ToList()),
            BackoffHours = Math.Max(1, backoffHours),
            AutoStartPaperLanes = autoStartPaper,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        SetMsg($"Campagna '{name.Trim()}' creata (spenta: abilitala quando sei pronto).", false);
        await RefreshAsync();
    }

    public async Task SetEnabledAsync(int campaignId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var campaign = await db.VettingCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign is null) return;
        campaign.Enabled = enabled;
        campaign.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        SetMsg(enabled
            ? $"Campagna '{campaign.Name}' ABILITATA{(GloballyEnabled ? "" : " (ma il gate globale Campaign:Enabled è spento: il planner non agirà finché non lo accendi)")}."
            : $"Campagna '{campaign.Name}' disabilitata.", false);
        await RefreshAsync();
    }

    /// <summary>Riporta in rotazione una campagna in attesa/osservazione (gesto esplicito dell'operatore, bypassa il backoff).</summary>
    public async Task WakeAsync(int campaignId, string? userId)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var campaign = await db.VettingCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null) return;
            campaign.Status = CampaignStatus.Rotating;
            campaign.PendingWakeReason = $"Riattivata manualmente dall'operatore {userId ?? "sconosciuto"}";
            campaign.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        SetMsg("Campagna riportata in rotazione: il prossimo run parte al prossimo tick del planner (backoff bypassato).", false);
        await RefreshAsync();
    }

    public async Task DeleteAsync(int campaignId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var campaign = await db.VettingCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
        if (campaign is null) return;
        db.VettingCampaigns.Remove(campaign);
        await db.SaveChangesAsync();
        SetMsg($"Campagna '{campaign.Name}' eliminata.", false);
        await RefreshAsync();
    }

    /// <summary>Tick manuale del planner (utile per non aspettare il prossimo giro del worker).</summary>
    public async Task TickNowAsync()
    {
        if (!GloballyEnabled)
        {
            SetMsg("Il gate globale Campaign:Enabled è spento: il planner non agisce.", true);
            return;
        }
        await planner.TickAsync();
        SetMsg("Tick del planner eseguito.", false);
        await RefreshAsync();
    }

    private void SetMsg(string text, bool error) { Message = text; IsError = error; }
}
