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
    /// <summary>Reader il cui esito può CAMBIARE fra un probe e l'altro, come la realtà.</summary>
    private sealed class MutableReader(int total, int unreadable) : IExchangeCredentialReader
    {
        public int Total { get; set; } = total;
        public int Unreadable { get; set; } = unreadable;
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<(int Total, int Unreadable)> CountUnreadableAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("DB irraggiungibile (simulato).");
            return Task.FromResult((Total, Unreadable));
        }

        public Task<IReadOnlyList<DecryptedExchangeCredential>> LoadForUserAsync(string userId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<DecryptedExchangeCredential?> FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

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

    // ---------------------------------------------------------------- ri-probe dopo modifica

    [Fact]
    public async Task RefreshAfterCredentialChange_ClearsAnAlarmThatIsNoLongerTrue()
    {
        // BUG REALE (2026-07-20): l'esito era un'istantanea presa una volta all'avvio. Chi
        // reinseriva le credenziali sistemandole si vedeva restare addosso il banner "non
        // decifrabili" fino al riavvio dell'app, e concludeva — ragionevolmente — che il
        // salvataggio non avesse funzionato. L'allarme accusava uno stato che non esisteva più.
        var reader = new MutableReader(total: 3, unreadable: 3);
        var probe = new MasterKeyProbe(reader, NullLogger<MasterKeyProbe>.Instance);
        await probe.ProbeAsync();
        Assert.True(probe.Result!.HasUnreadable);

        // L'utente elimina le credenziali illeggibili e ne reinserisce una buona.
        reader.Total = 1;
        reader.Unreadable = 0;
        await probe.RefreshAfterCredentialChangeAsync();

        Assert.False(probe.Result!.HasUnreadable);
        Assert.Equal(1, probe.Result.Total);
    }

    [Fact]
    public async Task RefreshAfterCredentialChange_RaisesAnAlarmThatHasJustBecomeTrue()
    {
        // Simmetrico: il ri-probe deve poter anche ACCENDERE l'allarme, non solo spegnerlo.
        var reader = new MutableReader(total: 0, unreadable: 0);
        var probe = new MasterKeyProbe(reader, NullLogger<MasterKeyProbe>.Instance);
        await probe.ProbeAsync();
        Assert.False(probe.Result!.HasUnreadable);

        reader.Total = 1;
        reader.Unreadable = 1;
        await probe.RefreshAfterCredentialChangeAsync();

        Assert.True(probe.Result!.HasUnreadable);
    }

    [Fact]
    public async Task RefreshAfterCredentialChange_NeverThrows_AndKeepsThePreviousResult()
    {
        // Il refresh e' agganciato al salvataggio delle credenziali: se il DB singhiozza proprio
        // in quel momento, un banner diagnostico stantio e' molto meglio di un salvataggio
        // riuscito che all'utente appare fallito.
        var reader = new MutableReader(total: 2, unreadable: 1);
        var probe = new MasterKeyProbe(reader, NullLogger<MasterKeyProbe>.Instance);
        await probe.ProbeAsync();
        var before = probe.Result;

        reader.Throw = true;
        await probe.RefreshAfterCredentialChangeAsync();   // non deve lanciare

        Assert.Same(before, probe.Result);
    }
}
