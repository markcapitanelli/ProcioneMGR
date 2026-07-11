using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Preferences;

/// <summary>
/// Persistenza delle configurazioni di pagina per utente: preset con nome e "ultima
/// configurazione usata" (nome vuoto, riscritta a ogni Run). Il JSON è opaco: lo schema
/// lo definisce la pagina, il servizio si limita a upsert/lettura/lista/cancellazione.
/// </summary>
public interface IPageConfigStore
{
    /// <summary>Nome riservato del preset implicito "ultima configurazione usata".</summary>
    const string LastUsedName = "";

    Task SaveAsync(string userId, string pageKey, string name, string configJson, CancellationToken ct = default);
    Task<string?> LoadAsync(string userId, string pageKey, string name, CancellationToken ct = default);
    /// <summary>Solo i preset con nome (l'ultima configurazione usata è esclusa), in ordine alfabetico.</summary>
    Task<List<string>> ListNamesAsync(string userId, string pageKey, CancellationToken ct = default);
    Task DeleteAsync(string userId, string pageKey, string name, CancellationToken ct = default);
}

public sealed class PageConfigStore(IDbContextFactory<ApplicationDbContext> dbFactory) : IPageConfigStore
{
    public async Task SaveAsync(string userId, string pageKey, string name, string configJson, CancellationToken ct = default)
    {
        name = name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.UserPageConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.PageKey == pageKey && c.Name == name, ct);
        if (existing is null)
        {
            db.UserPageConfigs.Add(new UserPageConfig
            {
                UserId = userId,
                PageKey = pageKey,
                Name = name,
                ConfigJson = configJson,
                UpdatedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.ConfigJson = configJson;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> LoadAsync(string userId, string pageKey, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserPageConfigs
            .Where(c => c.UserId == userId && c.PageKey == pageKey && c.Name == name)
            .Select(c => c.ConfigJson)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<string>> ListNamesAsync(string userId, string pageKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserPageConfigs
            .Where(c => c.UserId == userId && c.PageKey == pageKey && c.Name != IPageConfigStore.LastUsedName)
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task DeleteAsync(string userId, string pageKey, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.UserPageConfigs
            .Where(c => c.UserId == userId && c.PageKey == pageKey && c.Name == name)
            .ExecuteDeleteAsync(ct);
    }
}
