using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests;

/// <summary>
/// Fase 3-C2 (PRD Autonomia §6): l'avvio con la master key sbagliata deve diventare RUMOROSO
/// (LogCritical + notifica + stato per il banner UI) invece di morire in silenzio sul percorso
/// credenziali finché una pagina non va in 500.
/// </summary>
public class MasterKeyProbeTests
{
    private sealed class ScriptedReader(int total, int unreadable) : IExchangeCredentialReader
    {
        public Task<(int Total, int Unreadable)> CountUnreadableAsync(CancellationToken ct = default)
            => Task.FromResult((total, unreadable));

        public Task<IReadOnlyList<DecryptedExchangeCredential>> LoadForUserAsync(string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<DecryptedExchangeCredential?> FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingNotifier : ProcioneMGR.Services.Notifications.INotifier
    {
        public List<(ProcioneMGR.Services.Notifications.NotificationSeverity Severity, string Title)> Sent { get; } = new();
        public Task NotifyAsync(ProcioneMGR.Services.Notifications.NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Sent.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UnreadableCredentials_SetResult_AndNotifyCritical()
    {
        var notifier = new RecordingNotifier();
        var probe = new MasterKeyProbe(new ScriptedReader(total: 3, unreadable: 2), NullLogger<MasterKeyProbe>.Instance, notifier);

        var result = await probe.ProbeAsync();

        Assert.True(result.HasUnreadable);
        Assert.Equal(2, result.Unreadable);
        Assert.Same(result, probe.Result); // stato esposto per i banner di /trading e /settings/exchanges
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(ProcioneMGR.Services.Notifications.NotificationSeverity.Critical, sent.Severity);
        Assert.Contains("Master key", sent.Title);
    }

    [Fact]
    public async Task AllReadable_NoNotification()
    {
        var notifier = new RecordingNotifier();
        var probe = new MasterKeyProbe(new ScriptedReader(total: 4, unreadable: 0), NullLogger<MasterKeyProbe>.Instance, notifier);

        var result = await probe.ProbeAsync();

        Assert.False(result.HasUnreadable);
        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task NoCredentialsAtAll_IsHealthy()
    {
        // Installazione fresca (o master key placeholder senza credenziali): nessun falso allarme.
        var probe = new MasterKeyProbe(new ScriptedReader(total: 0, unreadable: 0), NullLogger<MasterKeyProbe>.Instance);

        var result = await probe.ProbeAsync();

        Assert.False(result.HasUnreadable);
    }
}
