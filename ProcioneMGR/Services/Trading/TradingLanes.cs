namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Numero di corsie di trading isolate (LaneId 0..Count-1). UNICA fonte di verità: prima il "3"
/// era ripetuto a mano in Program.cs (registrazioni keyed), Trading.razor, Ensemble.razor e
/// PromotionEvaluator — aumentare le corsie toccandone solo alcuni avrebbe prodotto corsie
/// invisibili in UI o mai valutate dalla promozione.
///
/// <b>Configurabile</b> da <c>Trading:LaneCount</c> (default <see cref="DefaultCount"/>), letto una
/// volta sola all'avvio da <c>AddTradingLanes</c>. Resta uno <i>static</i> e non un servizio
/// iniettato perché è consultato da posti in cui un servizio non arriverebbe senza una cascata di
/// modifiche: pagine Razor, watchdog degli invarianti, valutatore di promozione, validatore di lane
/// del gRPC. Un valore letto una volta e mai più cambiato è la forma più semplice che risolve il
/// problema; renderlo mutabile a caldo significherebbe invece che il numero di corsie può cambiare
/// mentre dei motori stanno operando — cioè avere corsie orfane o registrazioni keyed inesistenti.
/// </summary>
public static class TradingLanes
{
    /// <summary>Valore storico, e default se nessuno configura nulla.</summary>
    public const int DefaultCount = 3;

    /// <summary>
    /// Tetto invalicabile. Non è una stima di capacità: è una protezione dal refuso. Ogni corsia
    /// avvia tre worker (il più frequente batte ogni 2 secondi con una lettura di stato), quindi un
    /// <c>"LaneCount": 300</c> scritto per sbaglio creerebbe novecento cicli di fondo prima che
    /// qualcuno se ne accorga. A 12 corsie il carico continuo resta di poche letture al secondo,
    /// che il database locale assorbe senza accorgersene.
    /// </summary>
    public const int MaxCount = 12;

    private static readonly Lock Sync = new();
    private static int _count = DefaultCount;
    private static bool _frozen;

    /// <summary>Numero di corsie attive in questo processo.</summary>
    public static int Count
    {
        get
        {
            lock (Sync)
            {
                // La prima lettura congela il valore: da qui in poi registrazioni keyed, UI e worker
                // si basano su questo numero, e cambiarlo sotto di loro non avrebbe senso coerente.
                _frozen = true;
                return _count;
            }
        }
    }

    /// <summary>
    /// Imposta il numero di corsie. Va chiamata una volta sola all'avvio, prima che qualunque cosa
    /// legga <see cref="Count"/>. Ri-chiamarla con lo STESSO valore è innocuo (i test costruiscono
    /// più contenitori DI nello stesso processo); con un valore diverso dopo la prima lettura è un
    /// errore di programmazione e viene detto subito, invece di produrre corsie fantasma.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Fuori da 1..<see cref="MaxCount"/>.</exception>
    public static void Configure(int count)
    {
        if (count < 1 || count > MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count,
                $"Trading:LaneCount deve stare fra 1 e {MaxCount}.");
        }

        lock (Sync)
        {
            if (_count == count) return;
            if (_frozen)
            {
                throw new InvalidOperationException(
                    $"Il numero di corsie è già stato usato come {_count} in questo processo e non può diventare {count}: "
                    + "va impostato all'avvio, prima che motori, worker e UI vi si aggancino.");
            }
            _count = count;
        }
    }

    /// <summary>Solo per i test: riporta il conteggio al default e scongela.</summary>
    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _count = DefaultCount;
            _frozen = false;
        }
    }
}
