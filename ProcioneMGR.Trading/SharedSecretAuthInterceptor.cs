using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using ProcioneMGR.Contracts.Grpc;

namespace ProcioneMGR.Trading;

/// <summary>
/// Autorizzazione applicativa sul gRPC di trading: fino a qui l'unico confine era la
/// <c>NetworkPolicy</c> K8s (topologia di rete, non applicativa) — un confine noto per avere un
/// limite documentato (<c>kubectl port-forward</c> lo scavalca, vedi infra/k8s/README.md).
/// <c>ConfirmOrder</c>/<c>StartLane</c> possono muovere denaro vero: un secondo fattore applicativo,
/// anche solo un segreto condiviso, alza il costo di uno sbaglio di configurazione della rete da
/// "ordini reali" a "richiesta rifiutata".
///
/// FAIL-CLOSED per scelta, non fail-open: se il segreto non è configurato lato server, OGNI
/// chiamata viene rifiutata — mai un servizio "protetto solo se qualcuno si ricorda di attivarlo".
/// Stesso principio già in uso per la master key (<c>Program.cs</c>, fail-fast in Production).
/// </summary>
public sealed class SharedSecretAuthInterceptor(
    IConfiguration configuration, ILogger<SharedSecretAuthInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        // Letto a ogni chiamata (non in costruzione): la configurazione ha reloadOnChange, così una
        // rotazione del segreto (nuovo Secret K8s + riavvio del solo pod, o rimontaggio del volume)
        // si propaga senza dover ricreare l'interceptor.
        var expected = configuration["Trading:GrpcSharedSecret"];
        if (string.IsNullOrEmpty(expected))
        {
            logger.LogError(
                "Trading:GrpcSharedSecret non configurato: rifiuto {Method} invece di servire senza autorizzazione applicativa.",
                context.Method);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Servizio non configurato per l'autorizzazione applicativa."));
        }

        var provided = context.RequestHeaders.GetValue(SharedSecretClientInterceptor.HeaderName);
        if (provided is null || !FixedTimeEquals(provided, expected))
        {
            logger.LogWarning("Chiamata {Method} rifiutata: header {Header} assente o non corrispondente.",
                context.Method, SharedSecretClientInterceptor.HeaderName);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Segreto condiviso assente o non valido."));
        }

        return await continuation(request, context);
    }

    /// <summary>Confronto a tempo costante: un segreto condiviso non va confrontato con <c>==</c> (side-channel timing).</summary>
    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
