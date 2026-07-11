namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Numero di corsie di trading isolate (LaneId 0..Count-1). UNICA fonte di verità: prima il "3"
/// era ripetuto a mano in Program.cs (registrazioni keyed), Trading.razor, Ensemble.razor e
/// PromotionEvaluator — aumentare le corsie toccandone solo alcuni avrebbe prodotto corsie
/// invisibili in UI o mai valutate dalla promozione.
/// </summary>
public static class TradingLanes
{
    public const int Count = 3;
}
