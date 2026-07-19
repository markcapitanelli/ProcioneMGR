using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>
/// Fake in-memory di <see cref="ILaneQuarantineStore"/> (Fase 0-A3): condiviso dai test
/// dell'orchestrazione pagina (TradingPageServiceTests) e bUnit (AuditBlazorUiTests), che non
/// hanno bisogno del Postgres reale — la persistenza vera è coperta da LaneInvariantWatchdogTests.
/// </summary>
public sealed class FakeLaneQuarantineStore : ILaneQuarantineStore
{
    private readonly Dictionary<int, LaneQuarantine> _rows = new();

    public (int LaneId, string? UserId)? LastCleared { get; private set; }

    public void Seed(int laneId, string reason) => _rows[laneId] = new LaneQuarantine
    {
        LaneId = laneId, Reason = reason, CreatedAtUtc = DateTime.UtcNow, DetailsJson = "{}",
    };

    public Task<LaneQuarantine?> GetAsync(int laneId, CancellationToken ct = default)
        => Task.FromResult(_rows.TryGetValue(laneId, out var row) ? row : null);

    public Task<IReadOnlyList<LaneQuarantine>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LaneQuarantine>>(_rows.Values.OrderBy(r => r.LaneId).ToList());

    public Task<bool> TryQuarantineAsync(int laneId, string reason, string detailsJson, CancellationToken ct = default)
    {
        if (_rows.ContainsKey(laneId)) return Task.FromResult(false);
        _rows[laneId] = new LaneQuarantine
        {
            LaneId = laneId, Reason = reason, DetailsJson = detailsJson, CreatedAtUtc = DateTime.UtcNow,
        };
        return Task.FromResult(true);
    }

    public Task<bool> ClearAsync(int laneId, string? userId, CancellationToken ct = default)
    {
        LastCleared = (laneId, userId);
        return Task.FromResult(_rows.Remove(laneId));
    }
}
