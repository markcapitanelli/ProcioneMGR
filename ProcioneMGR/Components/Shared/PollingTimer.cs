namespace ProcioneMGR.Components.Shared;

/// <summary>
/// Timer di polling per il refresh periodico delle pagine Blazor Server. Sostituisce il pattern
/// fragile <c>new System.Threading.Timer(async _ =&gt; await ..., ...)</c>, la cui lambda è
/// <c>async void</c>: un'eccezione sfuggita al corpo non ha nessuno che la osservi e termina il
/// PROCESSO (non solo il circuito). Qui il loop su <see cref="PeriodicTimer"/> avvolge OGNI tick in
/// try/catch — un tick che lancia (es. DB irraggiungibile, circuito in chiusura) viene loggato e il
/// polling prosegue al tick successivo. La protezione diventa struttura, non convenzione di pagina.
///
/// Uso: creare in <c>OnInitialized</c>/<c>OnAfterRender(firstRender)</c> passando
/// <c>() =&gt; InvokeAsync(RefreshAsync)</c> (il marshaling sul contesto del renderer resta a carico
/// della pagina), e disporre in <c>Dispose</c>/<c>DisposeAsync</c>. Supporta entrambe le dispose così
/// le pagine <c>@implements IDisposable</c> non devono cambiare contratto.
/// </summary>
public sealed class PollingTimer : IAsyncDisposable, IDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <param name="interval">Intervallo fra un tick e il successivo. Il primo tick avviene dopo <paramref name="interval"/> (come <c>System.Threading.Timer</c> con dueTime = period).</param>
    /// <param name="onTickAsync">Callback eseguita a ogni tick. Tipicamente <c>() =&gt; InvokeAsync(RefreshAsync)</c>.</param>
    /// <param name="logger">Opzionale: se fornito, un tick fallito viene loggato come errore (altrimenti è ingoiato in silenzio, ma mai propagato).</param>
    public PollingTimer(TimeSpan interval, Func<Task> onTickAsync, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(onTickAsync);
        _timer = new PeriodicTimer(interval);
        _loop = RunAsync(onTickAsync, logger);
    }

    private async Task RunAsync(Func<Task> onTickAsync, ILogger? logger)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    await onTickAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogError(ex, "Tick di polling UI fallito; il polling continua.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose in corso: uscita pulita dal loop.
        }
    }

    /// <summary>
    /// Cleanup sincrono per le pagine <c>@implements IDisposable</c>: cancella e ferma il timer. Il
    /// loop osserva la cancellazione ed esce da sé — non lo si attende qui per non bloccare il thread
    /// del circuito. Un eventuale tick già in volo termina naturalmente (come <c>Timer.Dispose</c>).
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
    }

    /// <summary>
    /// Cleanup asincrono per le pagine <c>@implements IAsyncDisposable</c>: come <see cref="Dispose"/>
    /// ma ATTENDE la fine del loop prima di rilasciare il <see cref="CancellationTokenSource"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _timer.Dispose();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
