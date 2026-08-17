using Grpc.Core;
using Grpc.Core.Interceptors;

namespace ProcioneMGR.Contracts.Grpc;

/// <summary>
/// [2026-08-17] Impone una <b>deadline di default</b> a ogni chiamata gRPC uscente che non ne
/// porti già una.
///
/// <para><b>Il guasto che chiude.</b> Nessuna delle chiamate del client di trading aveva una
/// deadline, e la gRPC client factory disabilita di proposito il timeout dell'<c>HttpClient</c>
/// perché il limite dovrebbe essere proprio la deadline: una chiamata poteva quindi restare appesa
/// <b>per sempre</b> — partizione di rete che non chiude il TCP, o motore bloccato sul proprio
/// semaforo mentre parla con l'exchange. Il polling della pagina è sequenziale, quindi una sola RPC
/// appesa lo congelava tutto; e siccome nessuna eccezione veniva lanciata, <c>StaleSince</c> restava
/// null e il banner «DATI TRADING NON AGGIORNATI» non compariva mai: la pagina mostrava come attuali
/// numeri di ore prima. È la regola 5 («degradare dicendolo») rovesciata — senza deadline non c'è
/// nemmeno il fallimento da dichiarare. Lo stesso valeva per il <c>PromotionWorker</c>, che restava
/// appeso sulla prima corsia e smetteva in silenzio di valutare le retrocessioni di sicurezza.
/// </para>
///
/// <para><b>Perché due timeout.</b> Le LETTURE hanno un tetto stretto: oltre, il dato è vecchio
/// comunque ed è meglio dirlo. I COMANDI ne hanno uno generoso, perché la deadline cancella la
/// chiamata <em>anche lato server</em>: uno <c>StartLane</c> troncato a metà lascerebbe stato
/// parziale, e un timeout stretto su un comando trasformerebbe una lentezza in un danno.</para>
///
/// <para>Sta in un interceptor e non nei singoli metodi di proposito: un punto solo, nessuna rpc
/// futura può dimenticarselo, e una deadline esplicita passata dal chiamante ha comunque la
/// precedenza.</para>
/// </summary>
/// <param name="readTimeout">Tetto per le chiamate di sola lettura.</param>
/// <param name="commandTimeout">Tetto per le chiamate che mutano lo stato di esecuzione.</param>
/// <param name="readMethods">
/// Nomi dei metodi (senza percorso) trattati come letture. Passarli dall'esterno evita che questa
/// libreria condivisa debba conoscere la semantica di un servizio specifico.
/// </param>
public sealed class DeadlineClientInterceptor(
    TimeSpan readTimeout,
    TimeSpan commandTimeout,
    IReadOnlySet<string> readMethods) : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (context.Options.Deadline is not null)
        {
            return continuation(request, context);   // il chiamante ha già deciso: non si sovrascrive
        }

        var timeout = readMethods.Contains(context.Method.Name) ? readTimeout : commandTimeout;
        var options = context.Options.WithDeadline(DateTime.UtcNow + timeout);
        return continuation(request, new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options));
    }
}
