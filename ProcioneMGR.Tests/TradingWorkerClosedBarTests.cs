using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-06] Il worker deve alimentare il motore SOLO con barre chiuse.
///
/// <para><b>Il guasto, trovato dal proprietario</b>: sulla corsia 3 uno short ETC/USDT con take
/// profit a 6,3786 non si è chiuso, benché il minimo della barra 4h delle 08:00 fosse <b>6,31</b>.
/// Causa: l'ingestione REST scrive anche l'ultima kline INCOMPLETA, il worker la consumava appena
/// comparsa — quando il minimo era ancora sopra il target — e avanzava il cursore. Quando la barra
/// chiudeva col minimo vero, <c>ProcessCandleAsync</c> la scartava come «già vista».</para>
///
/// <para>Il tratto peggiore era che <b>nessun indicatore lo mostrava</b>: il battito diceva
/// «ultima candela 16:00 · 0 barre indietro» in verde, mentre quella barra 4h chiudeva alle 20:00.
/// Su 4h il punto cieco vale fino a quattro ore di prezzi.</para>
/// </summary>
public class TradingWorkerClosedBarTests
{
    /// <summary>
    /// Il caso reale, con i suoi numeri: alle 16:48 UTC su 4h l'ultima barra CHIUSA è quella delle
    /// 12:00. La barra delle 16:00 esiste a database ma chiude alle 20:00 — non va alimentata.
    /// </summary>
    [Fact]
    public void QuattroOre_AlleSedici48_LUltimaChiusaEQuellaDelleDodici()
    {
        var ultima = TradingWorker.LastClosedBarOpenUtc("4h", new DateTime(2026, 8, 6, 16, 48, 51, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc), ultima);
    }

    /// <summary>
    /// LA PROPRIETÀ CHE CONTA: la barra in formazione non è MAI inclusa, in nessun istante della
    /// sua vita — né appena aperta, né a un secondo dalla chiusura.
    /// </summary>
    [Theory]
    [InlineData("1m", 60)]
    [InlineData("5m", 300)]
    [InlineData("15m", 900)]
    [InlineData("30m", 1800)]
    [InlineData("1h", 3600)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    public void LaBarraInFormazioneNonEMaiAlimentata(string timeframe, int durataSecondi)
    {
        var durata = TimeSpan.FromSeconds(durataSecondi);
        var apertura = new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

        // Un istante dopo l'apertura, a metà, e un secondo prima della chiusura: sempre esclusa.
        foreach (var dentro in new[] { TimeSpan.FromSeconds(1), durata / 2, durata - TimeSpan.FromSeconds(1) })
        {
            var ultima = TradingWorker.LastClosedBarOpenUtc(timeframe, apertura + dentro);
            Assert.NotNull(ultima);
            Assert.True(ultima < apertura,
                $"{timeframe}: a {dentro} dall'apertura il worker includerebbe la barra ancora aperta.");
        }
    }

    /// <summary>
    /// E il rovescio, altrettanto necessario: appena la barra chiude diventa alimentabile. Un
    /// filtro troppo prudente non sarebbe una correzione — sarebbe lo stesso ritardo con un'altra
    /// causa.
    /// </summary>
    [Fact]
    public void AppenaChiusa_LaBarraDiventaAlimentabile()
    {
        var apertura = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        var chiusura = apertura.AddHours(4);   // 16:00

        Assert.True(TradingWorker.LastClosedBarOpenUtc("4h", chiusura.AddSeconds(-1)) < apertura);
        Assert.Equal(apertura, TradingWorker.LastClosedBarOpenUtc("4h", chiusura));
        Assert.Equal(apertura, TradingWorker.LastClosedBarOpenUtc("4h", chiusura.AddSeconds(1)));
    }

    /// <summary>
    /// Timeframe ignoto: <c>null</c>, e il chiamante non alimenta nulla. Tornare "adesso" avrebbe
    /// rimesso dentro la barra aperta proprio nel caso in cui non si sa quanto duri.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("2h")]
    [InlineData("settimanale")]
    public void TimeframeIgnoto_NienteDaAlimentare(string timeframe)
        => Assert.Null(TradingWorker.LastClosedBarOpenUtc(timeframe, DateTime.UtcNow));

    /// <summary>
    /// Coerenza con <see cref="ProcioneMGR.Services.Ingestion.SeriesFreshness"/>, che misura il
    /// ritardo contro l'ultima barra chiusa: alimentando quella barra il battito legge <b>0</b>.
    /// Le due regole devono dare lo stesso verdetto, o la pagina tornerebbe a rassicurare per conto
    /// suo — è la lezione di D2 e del Filone E.
    /// </summary>
    [Theory]
    [InlineData("15m")]
    [InlineData("1h")]
    [InlineData("4h")]
    [InlineData("1d")]
    public void AlimentandoLUltimaChiusa_IlBattitoDiceZeroBarreIndietro(string timeframe)
    {
        var adesso = new DateTime(2026, 8, 6, 16, 48, 51, DateTimeKind.Utc);
        var ultima = TradingWorker.LastClosedBarOpenUtc(timeframe, adesso);

        Assert.Equal(0, ProcioneMGR.Services.Ingestion.SeriesFreshness.BarsBehind(timeframe, ultima, adesso));
        Assert.False(ProcioneMGR.Services.Ingestion.SeriesFreshness.IsStale(timeframe, ultima, adesso));
    }
}
