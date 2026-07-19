using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests.Infrastructure;

/// <summary>Fake di <see cref="IMasterKeyProbe"/> (Fase 3-C2) per i test bUnit delle pagine che mostrano il banner.</summary>
public sealed class FakeMasterKeyProbe : IMasterKeyProbe
{
    public MasterKeyProbeResult? Result { get; set; }

    public Task<MasterKeyProbeResult> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(Result ??= new MasterKeyProbeResult(0, 0, DateTime.UtcNow));
}
