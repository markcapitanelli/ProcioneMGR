using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Services.Config;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K6, PRD autonomia-piena 2026-08-31] <b>La corsia riservata ai <c>Critical</c>.</b>
///
/// <para>Il difetto misurato: il rate-limit era <b>cieco alla gravità</b> — il parametro
/// <c>severity</c> compariva solo nella stringa di log e non entrava mai nella decisione — e a
/// budget pieno il messaggio veniva <b>scartato</b>, non accodato: sopravviveva il contatore, il
/// testo si perdeva. Nel guscio confluiscono nello stesso budget da 20/ora la guardia di
/// freschezza delle serie, il guardiano del patrimonio, l'orchestratore di flotta e il digest:
/// bastavano venti messaggi informativi nell'ora scorrevole per zittire l'allarme di invariante di
/// corsia o quello della master key. Otto punti nel guscio producono <c>Critical</c>, e sono
/// esattamente quelli che non si possono perdere.</para>
///
/// <para><b>Il controllo sul rumore, qui, è il caso simmetrico</b>: una corsia preferenziale senza
/// tetto sarebbe un canale senza rate-limit. Un critico ripetuto sessanta volte in un'ora smette di
/// essere letto come tutti gli altri, e il test lo pretende.</para>
/// </summary>
public class NotificationCorsiaCriticiTests
{
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class ContaProvider : INotificationProvider
    {
        public string Name => "Fake";
        public bool IsConfigured => true;
        public List<(NotificationSeverity Severity, string Title)> Recapitate { get; } = [];
        public Task SendAsync(NotificationSeverity severity, string title, string body, CancellationToken ct = default)
        {
            Recapitate.Add((severity, title));
            return Task.CompletedTask;
        }
    }

    private static (NotificationDispatcher D, ContaProvider P, FakeTimeProvider T) Build(
        int maxPerHour = 20, int maxCriticalPerHour = 10)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-31T10:00:00Z"));
        var provider = new ContaProvider();
        var d = new NotificationDispatcher(
            new NotificationOptions
            {
                Enabled = true,
                Provider = "Fake",
                MaxPerHour = maxPerHour,
                MaxCriticalPerHour = maxCriticalPerHour,
            }.AsMonitor(),
            [provider],
            NullLogger<NotificationDispatcher>.Instance,
            time);
        return (d, provider, time);
    }

    private static async Task SaturaConInfoAsync(NotificationDispatcher d, int quanti)
    {
        for (var i = 0; i < quanti; i++)
        {
            await d.SendDiagnosticAsync(NotificationSeverity.Info, $"rumore {i}", "corpo");
        }
    }

    [Fact]
    public async Task ABudgetCondivisoPIENO_unCriticalPASSA()
    {
        // IL test di questo item. Venti informative saturano il canale, poi arriva l'allarme che
        // non si può perdere: prima veniva scartato con un LogWarning e basta.
        var (d, p, _) = Build(maxPerHour: 20, maxCriticalPerHour: 10);
        await SaturaConInfoAsync(d, 20);

        var ordinaria = await d.SendDiagnosticAsync(NotificationSeverity.Warning, "un avviso qualunque", "corpo");
        var critica = await d.SendDiagnosticAsync(NotificationSeverity.Critical, "invariante di corsia", "corpo");

        Assert.Equal(NotificationOutcome.RateLimited, ordinaria.Outcome);
        Assert.Equal(NotificationOutcome.Delivered, critica.Outcome);
        Assert.Contains(p.Recapitate, r => r.Severity == NotificationSeverity.Critical && r.Title == "invariante di corsia");
    }

    [Fact]
    public async Task LaCorsiaCriticaHAunTetto_nonEUnCanaleLibero()
    {
        // Il controllo simmetrico: senza tetto questa corsia sarebbe un canale senza rate-limit.
        var (d, p, _) = Build(maxPerHour: 20, maxCriticalPerHour: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(NotificationOutcome.Delivered,
                (await d.SendDiagnosticAsync(NotificationSeverity.Critical, $"grave {i}", "corpo")).Outcome);
        }
        var quarta = await d.SendDiagnosticAsync(NotificationSeverity.Critical, "grave 4", "corpo");

        Assert.Equal(NotificationOutcome.RateLimited, quarta.Outcome);
        Assert.Contains("CRITICI", quarta.Detail);
        Assert.Equal(3, p.Recapitate.Count);
    }

    [Fact]
    public async Task LaCorsiaCritica_SIRICARICA_dopoUnOra()
    {
        // Dimenticare il trim della seconda finestra avrebbe prodotto un budget che si esaurisce e
        // non si ricarica mai: dopo N allarmi veri il canale critico resterebbe chiuso PER SEMPRE,
        // cioè il difetto che questa corsia esiste per impedire, spostato di un metro.
        var (d, p, t) = Build(maxCriticalPerHour: 2);
        await d.SendDiagnosticAsync(NotificationSeverity.Critical, "grave 1", "corpo");
        await d.SendDiagnosticAsync(NotificationSeverity.Critical, "grave 2", "corpo");
        Assert.Equal(NotificationOutcome.RateLimited,
            (await d.SendDiagnosticAsync(NotificationSeverity.Critical, "grave 3", "corpo")).Outcome);

        t.Advance(TimeSpan.FromMinutes(61));

        Assert.Equal(NotificationOutcome.Delivered,
            (await d.SendDiagnosticAsync(NotificationSeverity.Critical, "grave 4", "corpo")).Outcome);
        Assert.Equal(3, p.Recapitate.Count);
    }

    [Fact]
    public async Task UnCriticoCONSUMA_ancheIlBudgetCondiviso()
    {
        // Il recapito è banda vera: se i critici non entrassero nella finestra condivisa, il
        // pannello della pressione direbbe che il canale è più libero di quanto sia. Cambia il
        // cancello, non la contabilità.
        var (d, _, _) = Build(maxPerHour: 20, maxCriticalPerHour: 10);

        await d.SendDiagnosticAsync(NotificationSeverity.Critical, "grave", "corpo");

        Assert.Equal(1, d.RateLimitPressure.SentInWindow);
    }

    [Fact]
    public async Task LeOrdinarieRestanoGovernateDalTettoCondiviso()
    {
        // Nessuna regressione: la corsia nuova non deve allargare quella vecchia.
        var (d, p, _) = Build(maxPerHour: 3, maxCriticalPerHour: 10);

        await SaturaConInfoAsync(d, 3);
        var quarta = await d.SendDiagnosticAsync(NotificationSeverity.Info, "rumore 4", "corpo");

        Assert.Equal(NotificationOutcome.RateLimited, quarta.Outcome);
        Assert.Contains("ordinaria", quarta.Detail);
        Assert.Equal(3, p.Recapitate.Count);
    }

    [Fact]
    public void UnaCorsiaCriticaAZERO_vieneRIFIUTATAdallaValidazione()
    {
        // La trappola della manopola nuova: a 0 la corsia riservata diventa una sbarra chiusa e i
        // critici passerebbero MENO di prima — il contrario di ciò per cui esiste.
        var errore = AdminConfigRules.Validate(new NotificationOptions
        {
            Enabled = true, Provider = "Logging", MaxPerHour = 20, MaxCriticalPerHour = 0,
        });

        Assert.NotNull(errore);
        Assert.Contains("critici", errore, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IDefaultDiFabbricaPassanoLeProprieRegole()
    {
        // «Una regola che rifiuta la configurazione di fabbrica è un bug della regola»
        // (STANDARD-VERIFICA, livello 1 adattato).
        Assert.Null(AdminConfigRules.Validate(new NotificationOptions()));
    }
}
