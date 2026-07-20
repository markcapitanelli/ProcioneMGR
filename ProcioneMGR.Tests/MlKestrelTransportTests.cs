using System.Globalization;
using System.Net;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProcioneMGR.Contracts.Ml.V1;
using ProcioneMGR.Ml;
using ProcioneMGR.Services.Registry;

namespace ProcioneMGR.Tests;

/// <summary>
/// Avvia l'host REALE del microservizio ML (MlHost.Build, lo stesso che usa Program.cs) su Kestrel
/// vero, con socket TCP veri su porte effimere. È il complemento di
/// <see cref="MlGrpcRoundTripTests"/>, che passa dal TestServer in-memory e quindi NON esercita
/// Kestrel: quel test resta verde anche con gli endpoint configurati male, ed è per questo che il
/// bug HTTP_1_1_REQUIRED (0xd) arrivò fino a K8s (endpoint lasciato al default Http1AndHttp2: in
/// chiaro, senza ALPN, Kestrel serve HTTP/1.1 e rifiuta ogni connessione HTTP/2, quindi zero
/// chiamate gRPC servite). Qui il client è un GrpcChannel vero su http://127.0.0.1:porta.
/// </summary>
public sealed class MlKestrelHostFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _contentRoot;

    /// <summary>Porta h2c (solo HTTP/2) su cui risponde InferenceService.</summary>
    public int GrpcPort { get; private set; }

    /// <summary>Porta HTTP/1.1 su cui risponde /health (le probe K8s parlano HTTP/1.1).</summary>
    public int HealthPort { get; private set; }

    /// <summary>Il modello che il registry fake restituisce come Champion.</summary>
    public ProcioneMGR.Data.SavedMlModel Model { get; private set; } = null!;

    /// <summary>Vettore di feature valido per <see cref="Model"/>.</summary>
    public float[] Input { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var (model, input) = MlTestModel.TrainLinear();
        Model = model;
        Input = input;

        // Content root su una cartella vuota: senza, l'host leggerebbe gli appsettings.json finiti
        // nella bin dei test (arrivano dal monolite referenziato, e in locale possono contenere
        // segreti veri o una sezione Kestrel), rendendo il test dipendente dalla macchina.
        _contentRoot = Directory.CreateTempSubdirectory("ml-kestrel-test").FullName;

        // Porta 0 = il SO ne assegna una libera al bind: niente collisioni con un servizio in
        // esecuzione in locale o con altre run in parallelo, e nessuna race (a differenza del
        // "cerco una porta libera e poi la riuso"). Le porte reali si leggono dopo lo start.
        _app = MlHost.Build(
            [
                $"--contentRoot={_contentRoot}",
                $"--ConnectionStrings:PostgresConnection={MlTestModel.UnusedConnectionString}",
                "--Endpoints:GrpcPort=0",
                "--Endpoints:HealthPort=0",
            ],
            builder =>
            {
                // Nessun DB: il Champion arriva dal registry fake (vedi MlTestModel).
                builder.Services.RemoveAll<IModelRegistry>();
                builder.Services.AddSingleton<IModelRegistry>(new FixedChampionRegistry(model));
            });

        await _app.StartAsync();

        // Kestrel popola le Addresses dopo il bind, una per ListenAnyIP e nell'ordine di
        // registrazione: prima gRPC, poi health (vedi MlHost.Build).
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        Assert.Equal(2, addresses.Length);
        GrpcPort = PortOf(addresses[0]);
        HealthPort = PortOf(addresses[1]);
        Assert.NotEqual(GrpcPort, HealthPort);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_contentRoot is not null) Directory.Delete(_contentRoot, recursive: true);
    }

    /// <summary>Estrae la porta da un indirizzo Kestrel tipo <c>http://[::]:51234</c>.</summary>
    private static int PortOf(string address)
    {
        var trimmed = address.TrimEnd('/');
        var colon = trimmed.LastIndexOf(':');
        Assert.True(colon > 0, $"Indirizzo Kestrel inatteso: {address}");
        return int.Parse(trimmed[(colon + 1)..], CultureInfo.InvariantCulture);
    }
}

public class MlKestrelTransportTests(MlKestrelHostFixture fixture) : IClassFixture<MlKestrelHostFixture>
{
    /// <summary>
    /// Il test che il bug avrebbe fatto fallire: una PredictSignal vera su un GrpcChannel vero.
    /// Con l'endpoint al default Http1AndHttp2 in chiaro, qui arriverebbe l'RpcException
    /// HTTP_1_1_REQUIRED (0xd) invece della risposta.
    /// </summary>
    [Fact]
    public async Task PredictSignal_OverRealKestrelH2c_Succeeds()
    {
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{fixture.GrpcPort}");
        var client = new InferenceService.InferenceServiceClient(channel);

        var request = new PredictSignalRequest
        {
            Instrument = new ProcioneMGR.Contracts.Common.V1.Instrument
            {
                Symbol = fixture.Model.Symbol,
                Timeframe = fixture.Model.Timeframe,
            },
        };
        request.Features.AddRange(fixture.Input.Select(f => (double)f));

        var response = await client.PredictSignalAsync(request);

        Assert.Equal(fixture.Model.Id, response.ModelId);
        Assert.Equal(ProcioneMGR.Contracts.Ml.V1.ModelStage.Champion, response.StageUsed);
    }

    /// <summary>
    /// La porta health deve restare raggiungibile in HTTP/1.1: le probe httpGet di K8s parlano
    /// HTTP/1.1 e un endpoint solo-HTTP/2 le farebbe fallire, mandando il pod in CrashLoop.
    /// </summary>
    [Fact]
    public async Task Health_OverRealKestrelHttp11_ReturnsOk()
    {
        using var http = new HttpClient { DefaultRequestVersion = HttpVersion.Version11 };

        var response = await http.GetAsync($"http://127.0.0.1:{fixture.HealthPort}/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version11, response.Version);
        Assert.Contains("\"status\":\"ok\"", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Il negativo che inchioda la regressione: sulla porta gRPC l'HTTP/1.1 deve essere RIFIUTATO.
    /// Col default Http1AndHttp2 questa GET otterrebbe 200 (le route sono mappate su entrambi gli
    /// endpoint), ed è proprio quel "funziona" apparente che nascondeva il bug: era il gRPC a non
    /// passare più. Kestrel risponde 400 quando riceve HTTP/1.x su un endpoint solo-HTTP/2.
    /// </summary>
    [Fact]
    public async Task Http11Request_ToGrpcPort_IsRejected()
    {
        using var http = new HttpClient { DefaultRequestVersion = HttpVersion.Version11 };

        var response = await http.GetAsync($"http://127.0.0.1:{fixture.GrpcPort}/health");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
