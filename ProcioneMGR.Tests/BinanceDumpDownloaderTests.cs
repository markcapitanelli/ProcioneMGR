using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ProcioneMGR.Services.Microstructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [D3] Scarico dei dump storici. Nessun test qui tocca la rete: si verificano gli URL (una lettera
/// sbagliata nel percorso darebbe 404 su tutto e sembrerebbe "dato non disponibile"), la verifica del
/// checksum e il comportamento sul giorno mancante.
/// </summary>
public class BinanceDumpDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "procione-dump-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeHandler(HttpStatusCode status, byte[]? body = null) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(status);
            if (body is not null) response.Content = new ByteArrayContent(body);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void TheUrlsFollowThePublishedLayout()
    {
        var day = new DateOnly(2026, 7, 25);

        Assert.Equal(
            "https://data.binance.vision/data/spot/daily/aggTrades/BTCUSDT/BTCUSDT-aggTrades-2026-07-25.zip",
            BinanceDumpDownloader.AggTradesUrl(DumpMarket.Spot, "BTCUSDT", day));

        Assert.Equal(
            "https://data.binance.vision/data/futures/um/daily/aggTrades/BTCUSDT/BTCUSDT-aggTrades-2026-07-25.zip",
            BinanceDumpDownloader.AggTradesUrl(DumpMarket.FuturesUm, "BTCUSDT", day));

        Assert.Equal(
            "https://data.binance.vision/data/futures/um/daily/klines/BTCUSDT/1m/BTCUSDT-1m-2026-07-25.zip",
            BinanceDumpDownloader.KlinesUrl(DumpMarket.FuturesUm, "BTCUSDT", "1m", day));

        // Il book esiste SOLO sui futures: è il vincolo che rende l'OFI top-of-book non misurabile
        // storicamente, e l'URL lo riflette senza parametro di mercato.
        Assert.Equal(
            "https://data.binance.vision/data/futures/um/daily/bookDepth/BTCUSDT/BTCUSDT-bookDepth-2026-07-25.zip",
            BinanceDumpDownloader.BookDepthUrl("BTCUSDT", day));
    }

    [Fact]
    public async Task AMissingDay_IsNotAnError_ButIsCounted()
    {
        // Le serie bookDepth cominciano dopo quelle del tape: il chiamante salta il giorno e lo
        // dichiara nel report, invece di far fallire una misura da 60 giorni per un buco.
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var downloader = new BinanceDumpDownloader(new HttpClient(handler), _dir);

        var path = await downloader.EnsureAsync(BinanceDumpDownloader.BookDepthUrl("BTCUSDT", new DateOnly(2020, 1, 1)));

        Assert.Null(path);
        Assert.Equal(1, downloader.MissingDays);
    }

    [Fact]
    public async Task ADownloadedFileIsCached_TheSecondCallDoesNotTouchTheNetwork()
    {
        var zip = BuildZip("BTCUSDT-aggTrades-2026-07-25.csv", "1,100,1,1,1,1784937600157,false\n");
        var handler = new FakeHandler(HttpStatusCode.OK, zip);
        var downloader = new BinanceDumpDownloader(new HttpClient(handler), _dir);
        var url = BinanceDumpDownloader.AggTradesUrl(DumpMarket.FuturesUm, "BTCUSDT", new DateOnly(2026, 7, 25));

        var first = await downloader.EnsureAsync(url);
        var requestsAfterFirst = handler.Requested.Count;
        var second = await downloader.EnsureAsync(url);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(requestsAfterFirst, handler.Requested.Count);
        Assert.Equal(1, downloader.CacheHits);
        Assert.True(downloader.DownloadedBytes > 0);
    }

    [Fact]
    public async Task TheCsvInsideTheZipIsReadable_AndTheArchiveIsReleased()
    {
        var zip = BuildZip("x-aggTrades.csv", "1,100,2,1,1,1784937600157,false\n");
        var path = Path.Combine(_dir, "x.zip");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(path, zip);

        using (var reader = BinanceDumpDownloader.OpenCsv(path))
        {
            var trades = new BinanceDumpParser().ReadAggTrades(reader).ToList();
            Assert.Equal(2m, trades.Single().Quantity);
        }

        // Se l'archivio non venisse rilasciato col reader, questo File.Delete lancerebbe: è il modo
        // più diretto di verificare che una misura da 60 giorni non lasci 180 file aperti.
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ChecksumVerification_SpotsATruncatedFile()
    {
        Directory.CreateDirectory(_dir);
        var full = Path.Combine(_dir, "full.bin");
        var truncated = Path.Combine(_dir, "truncated.bin");
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(full, payload);
        await File.WriteAllBytesAsync(truncated, payload[..2048]);

        var expected = Convert.ToHexString(SHA256.HashData(payload));

        Assert.True(await BinanceDumpDownloader.MatchesChecksumAsync(full, expected));
        Assert.False(await BinanceDumpDownloader.MatchesChecksumAsync(truncated, expected));
    }

    private static byte[] BuildZip(string entryName, string content)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = archive.CreateEntry(entryName).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
        return memory.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
