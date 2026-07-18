using Grpc.Core;
using Grpc.Core.Interceptors;

namespace ProcioneMGR.Contracts.Grpc;

/// <summary>
/// Interceptor client-side che aggiunge a ogni chiamata gRPC uscente un header con un segreto
/// condiviso — controparte di <c>SharedSecretAuthInterceptor</c> lato server in
/// <c>ProcioneMGR.Trading</c>. Vive qui (libreria condivisa referenziata da entrambi gli host)
/// perché sia il monolite (client, quando <c>Trading:UseRemoteTrading=true</c>) sia i test del
/// servizio standalone devono poterlo costruire con lo stesso nome header/segreto.
///
/// Non è un sostituto di mTLS: è un secondo fattore oltre alla NetworkPolicy K8s, a costo quasi
/// zero, per il caso in cui il confine di rete da solo si riveli insufficiente (es. un
/// `kubectl port-forward` che lo scavalca, documentato in infra/k8s/README.md).
/// </summary>
public sealed class SharedSecretClientInterceptor(string sharedSecret) : Interceptor
{
    /// <summary>Nome dell'header sul filo — condiviso testualmente col controllo lato server.</summary>
    public const string HeaderName = "x-trading-shared-secret";

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        => continuation(request, WithSecretHeader(context));

    private ClientInterceptorContext<TRequest, TResponse> WithSecretHeader<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = new Metadata();
        if (context.Options.Headers is not null)
        {
            foreach (var entry in context.Options.Headers) headers.Add(entry);
        }
        headers.Add(HeaderName, sharedSecret);
        var options = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
    }
}
