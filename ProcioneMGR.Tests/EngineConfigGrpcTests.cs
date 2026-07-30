using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Contracts.Grpc;
using ProcioneMGR.Contracts.Trading.V1;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Notifications;
using ProcioneMGR.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// Livello 3 (integrazione) per il canale di configurazione del motore: gli endpoint gRPC serviti
/// dall'host REALE <c>ProcioneMGR.Trading</c>, non chiamate C# dirette a
/// <c>EngineConfigService</c>.
///
/// <para>Nasce da una lacuna trovata rileggendo <c>docs/STANDARD-VERIFICA.md</c> dopo aver
/// dichiarato il lavoro finito: <c>EngineConfigTests</c> copre il servizio in-process, e la verifica
/// dal vivo copre il percorso completo dal browser, ma il pezzo in mezzo — l'adattatore gRPC, con la
/// sua traduzione dei rifiuti in codici di stato — non aveva alcun test. È esattamente la regola 1
/// di quel documento: «il verde a livello di classe non è integrazione».</para>
///
/// <para>Ciò che conta qui NON è che la scrittura funzioni (lo dice già il livello 1) ma che i
/// RIFIUTI arrivino sul filo distinguibili l'uno dall'altro: chi chiama deve poter dire «non ti è
/// permesso» da «l'hai scritta male», perché sono due azioni diverse per chi legge il messaggio.</para>
/// </summary>
public class EngineConfigGrpcTests : IDisposable
{
    private const string TestSharedSecret = "test-only-shared-secret";

    private readonly string _contentRoot;

    public EngineConfigGrpcTests()
    {
        // ContentRootPath dedicato: EngineConfigService scrive in <ContentRoot>/appsettings.json, e
        // un test che scrivesse nella cartella dell'host toccherebbe un file condiviso con gli altri.
        _contentRoot = Path.Combine(Path.GetTempPath(), "procione-grpccfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_contentRoot);
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    private WebApplicationFactory<TradingCommandServiceImpl> CreateHost() =>
        new WebApplicationFactory<TradingCommandServiceImpl>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:PostgresConnection", "Host=localhost;Database=unused;Username=x;Password=x");
            b.UseSetting("Security:MasterKey", Convert.ToBase64String(new byte[32]));
            b.UseSetting("Trading:GrpcSharedSecret", TestSharedSecret);
            // UseSetting e non UseContentRoot: quest'ultimo è un'estensione di IHostBuilder, non
            // disponibile qui. La chiave "contentRoot" è la stessa che quel metodo imposterebbe.
            b.UseSetting("contentRoot", _contentRoot);
        });

    private static TradingCommandService.TradingCommandServiceClient ClientFor(
        WebApplicationFactory<TradingCommandServiceImpl> factory)
    {
        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        return new TradingCommandService.TradingCommandServiceClient(
            channel.Intercept(new SharedSecretClientInterceptor(TestSharedSecret)));
    }

    // --- IL CONFINE, sul filo ------------------------------------------------------------------

    [Fact]
    public async Task SetEngineConfig_OnAForbiddenSection_IsPermissionDenied_NotInvalidArgument()
    {
        // I due rifiuti NON vanno collassati: "non puoi toccarla" e "l'hai scritta male" richiedono
        // reazioni diverse da chi chiama. E la connection string è il caso peggiore possibile.
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SetEngineConfigAsync(
            new SetEngineConfigRequest
            {
                Section = "ConnectionStrings",
                Json = """{"PostgresConnection":"Host=cattivo"}""",
            }).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Contains("non scrivibile", ex.Status.Detail);
    }

    [Theory]
    [InlineData("Security:MasterKey")]
    [InlineData("Trading:GrpcSharedSecret")]
    [InlineData("Trading:UseRemoteTrading")]
    [InlineData("Trading:LaneCount")]
    public async Task SetEngineConfig_OnSecretsAndTopology_IsAlwaysRefused(string section)
    {
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SetEngineConfigAsync(
            new SetEngineConfigRequest { Section = section, Json = "\"qualunque\"" }).ResponseAsync);

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task GetEngineConfig_NeverLeaksSecrets_EvenWhenAskedForThemExplicitly()
    {
        // In lettura le sezioni proibite vengono SALTATE, non negate: un pannello che chiede più del
        // dovuto non deve far fallire l'intera schermata. Ma non devono comparire nella risposta.
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var response = await client.GetEngineConfigAsync(new GetEngineConfigRequest
        {
            Sections = { "Carry", "Security:MasterKey", "ConnectionStrings", "Trading:GrpcSharedSecret" },
        });

        var paths = response.Sections.Select(s => s.Path).ToList();
        Assert.Equal(["Carry"], paths);

        // E per sicurezza: nessun payload contiene la master key di test, in nessuna forma.
        var everything = string.Join('\n', response.Sections.Select(s => s.Json));
        Assert.DoesNotContain(Convert.ToBase64String(new byte[32]), everything);
        Assert.DoesNotContain("Host=localhost", everything);
    }

    // --- La validazione, sul filo --------------------------------------------------------------

    [Fact]
    public async Task SetEngineConfig_WithAnInvalidValue_IsInvalidArgument_WithTheHumanMessage()
    {
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SetEngineConfigAsync(
            new SetEngineConfigRequest
            {
                Section = "Carry",
                // Uscita sopra l'ingresso: senza isteresi il carry aprirebbe e chiuderebbe nella
                // stessa valutazione. Il messaggio deve arrivare intero, non come codice opaco.
                Json = JsonSerializer.Serialize(new CarryOptions
                {
                    EnterAnnualFundingPercent = 5m,
                    ExitAnnualFundingPercent = 9m,
                }),
            }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("isteresi", ex.Status.Detail);
    }

    [Fact]
    public async Task SetEngineConfig_WithMalformedJson_IsInvalidArgument()
    {
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SetEngineConfigAsync(
            new SetEngineConfigRequest { Section = "Carry", Json = "{ non json" }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // --- Il giro completo ----------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_WriteThenRead_ReturnsWhatWasWritten()
    {
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        await client.SetEngineConfigAsync(new SetEngineConfigRequest
        {
            Section = "Carry",
            Json = JsonSerializer.Serialize(new CarryOptions { Enabled = true, EnterAnnualFundingPercent = 11m }),
        });

        var response = await client.GetEngineConfigAsync(new GetEngineConfigRequest { Sections = { "Carry" } });
        var carry = JsonSerializer.Deserialize<CarryOptions>(response.Sections.Single().Json)!;

        Assert.True(carry.Enabled);
        Assert.Equal(11m, carry.EnterAnnualFundingPercent);

        // E il file del motore lo contiene davvero: il giro non è solo in memoria.
        var onDisk = File.ReadAllText(Path.Combine(_contentRoot, "appsettings.json"));
        Assert.Contains("\"Enabled\": true", onDisk);
    }

    [Fact]
    public async Task GetEngineConfig_WithNoSections_ReturnsAllReadable_AndSaysWhereItWrites()
    {
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var response = await client.GetEngineConfigAsync(new GetEngineConfigRequest());

        Assert.Contains("Trading:Safety", response.Sections.Select(s => s.Path));
        Assert.Contains("Notifications", response.Sections.Select(s => s.Path));
        // La diagnostica che il pannello mostra all'operatore prima di lasciarlo salvare.
        Assert.EndsWith("appsettings.json", response.ConfigPath);
        Assert.True(response.Writable);
        // Le sole letture di topologia sono marcate non scrivibili anche nella risposta.
        Assert.False(response.Sections.Single(s => s.Path == "Trading:LaneCount").Writable);
    }

    // --- La prova del canale di notifica, sul filo ---------------------------------------------

    [Fact]
    public async Task SendTestNotification_ReportsDisabled_WhenTheEngineChannelIsOff()
    {
        // È LA risposta che ha scoperto il guasto reale: il motore non aveva mai avuto le notifiche
        // accese, quindi gli allarmi di quarantena non arrivavano a nessuno. Deve restare
        // distinguibile da "consegnato" per sempre.
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var response = await client.SendTestNotificationAsync(new SendTestNotificationRequest());

        Assert.Equal(nameof(NotificationOutcome.Disabled), response.Outcome);
        Assert.NotEmpty(response.Detail);
    }

    [Fact]
    public async Task SendTestNotification_ReportsDelivered_OnceTheChannelIsConfigured()
    {
        // Si accende il canale PASSANDO DAL CANALE DI CONFIGURAZIONE, non da una scorciatoia: è la
        // sequenza che l'operatore esegue davvero dal pannello.
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        await client.SetEngineConfigAsync(new SetEngineConfigRequest
        {
            Section = "Notifications",
            // Provider Logging: recapita nel log, quindi la prova è verde senza dipendere da
            // Telegram, da un token o dalla rete.
            Json = JsonSerializer.Serialize(new NotificationOptions { Enabled = true, Provider = "Logging" }),
        });

        var response = await client.SendTestNotificationAsync(new SendTestNotificationRequest());

        Assert.Equal(nameof(NotificationOutcome.Delivered), response.Outcome);
        Assert.Equal("Logging", response.Provider);
    }

    [Fact]
    public async Task SetEngineConfig_OnNotifications_RefusesTelegramWithoutAChatId()
    {
        // Stessa regola dei pannelli, applicata dall'altra parte del filo: un canale Telegram senza
        // destinatario è configurato e muto, cioè il guasto che tutto questo lavoro esiste per
        // togliere.
        await using var factory = CreateHost();
        var client = ClientFor(factory);

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.SetEngineConfigAsync(
            new SetEngineConfigRequest
            {
                Section = "Notifications",
                Json = JsonSerializer.Serialize(new NotificationOptions
                {
                    Enabled = true,
                    Provider = "Telegram",
                    ChatId = "",
                }),
            }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("ChatId", ex.Status.Detail);
    }
}
