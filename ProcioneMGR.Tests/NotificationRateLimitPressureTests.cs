using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I4] La pressione sul budget CONDIVISO delle notifiche.
///
/// Il difetto che copre: <c>Notifications:MaxPerHour</c> è uno solo per processo e nel guscio ci
/// confluiscono otto sorveglianti (deriva feature e fattori, flotta, campagne, comitato, freschezza
/// serie, guardiano del patrimonio, digest). Venti messaggi/ora divisi fra otto significa che
/// <b>il primo che sbaglia soglia zittisce gli altri sette</b> — è già successo, con la staleness su
/// una serie illiquida che ha saturato il canale. Finora la soppressione viveva in un
/// <c>LogWarning</c> e nel conteggio accodato al messaggio successivo: nessuna superficie diceva
/// quanto è pieno il secchio ADESSO, cioè quanto manca al silenzio.
/// </summary>
public class NotificationRateLimitPressureTests
{
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class SilentProvider : INotificationProvider
    {
        public string Name => "Fake";
        public bool IsConfigured => true;
        public Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static (NotificationDispatcher Dispatcher, FakeTimeProvider Time) Build(int maxPerHour)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T10:00:00Z"));
        var dispatcher = new NotificationDispatcher(
            new NotificationOptions { Enabled = true, Provider = "Fake", MaxPerHour = maxPerHour }.AsMonitor(),
            [new SilentProvider()],
            NullLogger<NotificationDispatcher>.Instance,
            time);
        return (dispatcher, time);
    }

    /// <summary>
    /// <b>Il controllo sul rumore</b>: leggere la spia non deve consumare nulla né inventare niente.
    /// Se questa lettura avesse effetti, il pannello cambierebbe ciò che misura solo aprendolo.
    /// </summary>
    [Fact]
    public void ACanaleFermo_LaSpiaEVuotaELaLetturaNonConsumaSlot()
    {
        var (d, _) = Build(maxPerHour: 20);

        var a = d.RateLimitPressure;
        var b = d.RateLimitPressure;

        Assert.Equal(0, a.SentInWindow);
        Assert.Equal(20, a.MaxPerHour);
        Assert.Equal(20, a.Remaining);
        Assert.Equal(0, a.SuppressedPending);
        Assert.Equal(0, a.SuppressedTotal);
        Assert.Null(a.LastSuppressedUtc);
        Assert.False(a.IsLosingNow);
        Assert.Equal(a.SentInWindow, b.SentInWindow); // due letture, stesso stato
    }

    [Fact]
    public async Task SottoIlTetto_LaPressioneSaleELoSpazioResiduoCala()
    {
        var (d, _) = Build(maxPerHour: 3);

        await d.NotifyAsync(NotificationSeverity.Info, "1", "b");
        await d.NotifyAsync(NotificationSeverity.Info, "2", "b");

        var p = d.RateLimitPressure;
        Assert.Equal(2, p.SentInWindow);
        Assert.Equal(1, p.Remaining);
        Assert.False(p.IsLosingNow);
        Assert.Equal(0, p.SuppressedTotal);
    }

    /// <summary>
    /// Oltre il tetto la spia deve dire <b>sta perdendo adesso</b>: è la risposta alla prima domanda
    /// del censimento («come fa a dire di no?»), che prima non aveva nessun posto dove darsi.
    /// </summary>
    [Fact]
    public async Task OltreIlTetto_LaSpiaDichiaraCheIlCanaleStaPerdendoMessaggi()
    {
        var (d, _) = Build(maxPerHour: 1);

        await d.NotifyAsync(NotificationSeverity.Info, "1", "b");
        await d.NotifyAsync(NotificationSeverity.Critical, "2", "b"); // soppressa
        await d.NotifyAsync(NotificationSeverity.Critical, "3", "b"); // soppressa

        var p = d.RateLimitPressure;
        Assert.True(p.IsLosingNow);
        Assert.Equal(2, p.SuppressedPending);
        Assert.Equal(2, p.SuppressedTotal);
        Assert.Equal(0, p.Remaining);
        Assert.NotNull(p.LastSuppressedUtc);
    }

    /// <summary>
    /// <b>La proprietà che rende utile la spia.</b> Il conteggio «in attesa» si azzera col primo
    /// messaggio che passa — è giusto, quelli sono stati dichiarati in coda a quel messaggio — ma il
    /// TOTALE no. Senza il totale, un'occhiata al pannello un minuto dopo la tempesta direbbe che non
    /// è successo niente: è la differenza fra «adesso va bene» e «oggi è andata male due volte».
    /// </summary>
    [Fact]
    public async Task DopoLaTempesta_IlPendingSiAzzeraMaIlTotaleResta()
    {
        var (d, time) = Build(maxPerHour: 1);

        await d.NotifyAsync(NotificationSeverity.Info, "1", "b");
        await d.NotifyAsync(NotificationSeverity.Info, "2", "b"); // soppressa
        await d.NotifyAsync(NotificationSeverity.Info, "3", "b"); // soppressa

        time.Advance(TimeSpan.FromMinutes(61));
        await d.NotifyAsync(NotificationSeverity.Info, "4", "b"); // passa e dichiara i 2 soppressi

        var p = d.RateLimitPressure;
        Assert.Equal(0, p.SuppressedPending);
        Assert.False(p.IsLosingNow);
        Assert.Equal(2, p.SuppressedTotal);   // non si azzera MAI
        Assert.NotNull(p.LastSuppressedUtc);
    }

    /// <summary>
    /// La finestra è scorrevole, e la spia deve seguirla anche <b>senza</b> che passi un messaggio:
    /// altrimenti mostrerebbe come occupati slot liberi da un'ora, cioè un valore vecchio spacciato
    /// per attuale.
    /// </summary>
    [Fact]
    public async Task LaFinestraScorre_ELaSpiaSiLiberaAncheSenzaNuoviMessaggi()
    {
        var (d, time) = Build(maxPerHour: 2);

        await d.NotifyAsync(NotificationSeverity.Info, "1", "b");
        await d.NotifyAsync(NotificationSeverity.Info, "2", "b");
        Assert.Equal(2, d.RateLimitPressure.SentInWindow);

        time.Advance(TimeSpan.FromMinutes(61));

        var p = d.RateLimitPressure; // nessun invio in mezzo: solo la lettura
        Assert.Equal(0, p.SentInWindow);
        Assert.Equal(2, p.Remaining);
    }

    /// <summary>
    /// Il tetto si legge <b>a caldo</b> dalle opzioni, non congelato alla costruzione: cambiarlo dal
    /// pannello deve cambiare ciò che la spia dichiara.
    ///
    /// <para>La prima versione di questo test costruiva con <c>MaxPerHour=7</c> e asseriva 7 senza
    /// mai toccare le opzioni: sarebbe passata identica anche se la spia avesse copiato il tetto nel
    /// costruttore — cioè proprio il difetto che il commento diceva di escludere. Trovato dalla
    /// revisione avversaria del 2026-08-18. Un commento che dichiara una proprietà non esercitata
    /// dall'asserzione è una falsa assicurazione scritta nel codice.</para>
    /// </summary>
    [Fact]
    public async Task IlTettoSiLeggeACaldo_NonECongelatoAllaCostruzione()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T10:00:00Z"));
        var options = new MutableOptionsMonitor<NotificationOptions>(
            new NotificationOptions { Enabled = true, Provider = "Fake", MaxPerHour = 7 });
        var d = new NotificationDispatcher(options, [new SilentProvider()], NullLogger<NotificationDispatcher>.Instance, time);

        Assert.Equal(7, d.RateLimitPressure.MaxPerHour);
        Assert.Equal(7, d.RateLimitPressure.Remaining);

        // Il pannello abbassa il tetto: la spia deve seguirlo SENZA ricostruire il dispatcher.
        options.CurrentValue = new NotificationOptions { Enabled = true, Provider = "Fake", MaxPerHour = 2 };

        Assert.Equal(2, d.RateLimitPressure.MaxPerHour);
        Assert.Equal(2, d.RateLimitPressure.Remaining);

        // E il nuovo tetto vale davvero anche in invio: due passano, la terza è soppressa.
        await d.NotifyAsync(NotificationSeverity.Info, "1", "b");
        await d.NotifyAsync(NotificationSeverity.Info, "2", "b");
        await d.NotifyAsync(NotificationSeverity.Info, "3", "b");

        var p = d.RateLimitPressure;
        Assert.Equal(2, p.SentInWindow);
        Assert.Equal(0, p.Remaining);
        Assert.True(p.IsLosingNow);
        Assert.Equal(1, p.SuppressedTotal);
    }

    /// <summary>Un tetto a 0 o negativo vale 1, come nell'invio: le due strade non possono divergere.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TettoNonPositivo_ValeUnoComeNellInvio(int configured)
    {
        var (d, _) = Build(configured);
        Assert.Equal(1, d.RateLimitPressure.MaxPerHour);
    }
}
