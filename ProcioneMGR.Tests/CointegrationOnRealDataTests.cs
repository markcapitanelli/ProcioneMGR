using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;
using ProcioneMGR.Services.PairsTrading;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Services.TimeSeries;
using ProcioneMGR.Tests.Infrastructure;
using Xunit.Abstractions;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica sui DATI REALI del passaggio della cointegrazione ai log-prezzi.
///
/// Il caso che ha motivato il cambiamento è AAVE/XLM: sulla finestra di selezione 2024-01→2026-03
/// (4h) la vecchia specificazione sui prezzi grezzi la dichiarava cointegrata, ed è finita fra le
/// otto candidate salvo poi risultare la peggiore (−14,14%, maxDD 15,1% — docs/REPORT-RICERCA-2026-07.md).
///
/// Il risultato interessante della verifica è che a bocciarla NON è il filtro sull'elasticità:
/// quella vale ~0,69 ed è dentro la banda di sanità. È l'ADF stesso a rifiutarla, una volta che
/// gira sui log. In altre parole la stazionarietà dello spread era un artefatto della regressione
/// in unità di prezzo fra due monete con scale di prezzo lontanissime — il rilievo "cointegrazione
/// troppo liberale" dell'audit 2026-07, che sui log si chiude da solo.
///
/// Se il DB reale non è disponibile (CI) il test si salta invece di fallire.
/// </summary>
public class CointegrationOnRealDataTests(ITestOutputHelper output)
{
    private const string Timeframe = "4h";
    private static readonly DateTime From = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<List<OhlcvData>> LoadAsync(ApplicationDbContext db, string symbol) =>
        await db.OhlcvData.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Timeframe == Timeframe
                     && c.TimestampUtc >= From && c.TimestampUtc < To)
            .OrderBy(c => c.TimestampUtc)
            .ToListAsync();

    [Fact]
    public async Task AaveXlm_TheSpuriousPair_IsRejectedUnderLogPrices()
    {
        if (!RealMarketDb.IsAvailable())
        {
            output.WriteLine("DB procionemgr non disponibile: test saltato.");
            return;
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(RealMarketDb.ConnectionString)
            .Options;
        await using var db = new ApplicationDbContext(options, new PassthroughEncryption());

        var aave = await LoadAsync(db, "AAVE/USDT");
        var xlm = await LoadAsync(db, "XLM/USDT");
        if (aave.Count < 500 || xlm.Count < 500)
        {
            output.WriteLine($"Dati insufficienti (AAVE {aave.Count}, XLM {xlm.Count}): test saltato.");
            return;
        }

        var (ay, ax) = PairsCandleAligner.Align(aave, xlm);
        var result = new EngleGrangerCointegrationTest().Test(
            [.. ay.Select(c => c.Close)], [.. ax.Select(c => c.Close)]);

        output.WriteLine($"AAVE/XLM su {ay.Count} candele allineate {Timeframe}");
        output.WriteLine($"  elasticità β = {result.HedgeRatio:F3} (plausibile: {result.IsHedgeRatioPlausible})");
        output.WriteLine($"  ADF = {result.AdfStatistic:F3} vs CV MacKinnon {result.CriticalValue:F3} (lag {result.AdfLags})");
        output.WriteLine($"  cointegrata: {result.IsCointegrated} | operabile: {result.IsTradeable}");

        // L'esito che conta: la coppia non deve più entrare in produzione.
        Assert.False(result.IsTradeable);

        // E il MOTIVO conta quanto l'esito: se un giorno passasse la banda ma fallisse l'ADF (o
        // viceversa) rispetto a quanto misurato qui, la specificazione è cambiata sotto i piedi e
        // vale la pena accorgersene invece di leggere un verde rassicurante.
        Assert.False(result.IsCointegrated, "atteso: sui log lo spread NON risulta stazionario");
        Assert.True(result.IsHedgeRatioPlausible,
            "atteso: l'elasticità (~0,69) è dentro la banda — non è il filtro di sanità a bocciarla");
    }

    [Fact]
    public async Task LogSpecification_IsStricterThanRawPrices_AcrossTheUniverse()
    {
        if (!RealMarketDb.IsAvailable())
        {
            output.WriteLine("DB procionemgr non disponibile: test saltato.");
            return;
        }

        string[] universe =
        [
            "AAVE/USDT", "XLM/USDT", "BTC/USDT", "ETH/USDT", "SOL/USDT", "BNB/USDT", "LINK/USDT",
            "AVAX/USDT", "LTC/USDT", "DOT/USDT", "ATOM/USDT", "UNI/USDT", "NEAR/USDT", "ADA/USDT",
        ];

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(RealMarketDb.ConnectionString)
            .Options;
        await using var db = new ApplicationDbContext(options, new PassthroughEncryption());

        var series = new Dictionary<string, List<OhlcvData>>();
        foreach (var s in universe) series[s] = await LoadAsync(db, s);
        if (series.Values.Any(v => v.Count < 500))
        {
            output.WriteLine("Dati insufficienti su almeno un simbolo: test saltato.");
            return;
        }

        var test = new EngleGrangerCointegrationTest();
        int tested = 0, cointegrated = 0, tradeable = 0;

        for (var i = 0; i < universe.Length; i++)
        {
            for (var j = i + 1; j < universe.Length; j++)
            {
                var (ay, ax) = PairsCandleAligner.Align(series[universe[i]], series[universe[j]]);
                if (ay.Count < 500) continue;

                var r = test.Test([.. ay.Select(c => c.Close)], [.. ax.Select(c => c.Close)]);
                tested++;
                if (r.IsCointegrated) cointegrated++;
                if (r.IsTradeable) tradeable++;
            }
        }

        output.WriteLine($"Coppie testate: {tested} | cointegrate: {cointegrated} | operabili: {tradeable}");

        // Su asset cripto, che si muovono quasi tutti insieme, una frazione ALTA di coppie
        // "cointegrate" è il sintomo di un test troppo liberale (audit 2026-07): era il caso della
        // regressione in unità di prezzo. Sui log la selezione torna in un intervallo credibile.
        Assert.True(tested > 50, $"attese molte coppie testate, trovate {tested}");
        Assert.True(cointegrated < tested * 0.25,
            $"{cointegrated}/{tested} coppie cointegrate: frazione troppo alta, il test è tornato liberale");
    }
}
