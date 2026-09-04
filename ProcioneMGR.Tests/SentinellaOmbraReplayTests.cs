using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-04] La sentinella d'ombra delle uscite protettive non confronta il tick di oggi con la
/// barra di tre giorni fa. Vedi <see cref="ProtectiveExitShadowReplayGuard"/> per il fatto che ha
/// motivato il guardiano (14 confronti su 24 erano replay, 3 allarmi falsi sopra soglia).
/// </summary>
public class SentinellaOmbraReplayTests
{
    private static readonly DateTime Barra = new(2026, 8, 20, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>Il caso normale: il tick cade dentro la barra, o poco dopo. Si confronta.</summary>
    [Theory]
    [InlineData("15m", 3)]
    [InlineData("15m", 29)]   // ingestione in ritardo di quasi un passo intero: ancora la barra di adesso
    [InlineData("1h", 90)]
    [InlineData("4h", 420)]   // sette ore su una barra da quattro: sotto i due passi
    public void UnTICKdentroDUEpassi_NONeReplay(string timeframe, int minutiDopo)
        => Assert.False(ProtectiveExitShadowReplayGuard.EReplay(Barra.AddMinutes(minutiDopo), Barra, timeframe));

    /// <summary>Il fatto del 23/08: barra del 20/08, tick del 23/08. Non si scrive.</summary>
    [Theory]
    [InlineData("15m", 31)]
    [InlineData("15m", 3 * 24 * 60)]
    [InlineData("1h", 121)]
    [InlineData("4h", 481)]
    public void UnTICKoltreDUEpassi_eReplay(string timeframe, int minutiDopo)
        => Assert.True(ProtectiveExitShadowReplayGuard.EReplay(Barra.AddMinutes(minutiDopo), Barra, timeframe));

    /// <summary>Il tick PRIMA della barra (rilevazione in anticipo, il caso per cui la sentinella esiste) non è mai replay.</summary>
    [Fact]
    public void UnTICKprimaDELLAbarra_NONeReplay()
        => Assert.False(ProtectiveExitShadowReplayGuard.EReplay(Barra.AddMinutes(-23), Barra, "1h"));

    /// <summary>Un timeframe ignoto o vuoto non zittisce la sentinella: si conserva il comportamento di prima.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7m")]
    public void TimeframeIGNOTO_nonScarta(string? timeframe)
        => Assert.False(ProtectiveExitShadowReplayGuard.EReplay(Barra.AddDays(3), Barra, timeframe));
}
