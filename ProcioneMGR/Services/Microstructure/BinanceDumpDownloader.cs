using System.IO.Compression;
using System.Security.Cryptography;

namespace ProcioneMGR.Services.Microstructure;

// =============================================================================================
//  [D3] Scarico dei dump pubblici storici (data.binance.vision).
//
//  PERCHÉ ESISTE. Il gate di C5 chiedeva 90 giorni di raccolta dal vivo prima di poter rispondere
//  "il book aggiunge informazione?". Ma tape e profondità storici sono già pubblicati come file
//  statici: si misura oggi, su mesi di dati, senza accendere alcuna raccolta e senza lasciare un
//  costo permanente sulla piattaforma. Se il verdetto è no, non avremo pagato nulla per scoprirlo.
//
//  TRE SCELTE, TUTTE PER NON MENTIRE SUI DATI:
//  1. CACHE SU DISCO fuori dal repo (il repo è pubblico e la sua igiene è già costata una pulizia
//     da 7,9 GB): rilanciare la misura non riscarica, e il file resta ispezionabile a mano.
//  2. CHECKSUM VERIFICATO quando Binance lo pubblica (.CHECKSUM accanto allo zip). Un file troncato
//     a metà download produrrebbe barre mancanti alla fine del giorno, cioè un buco che somiglia
//     tanto a "quel giorno il mercato era fermo".
//  3. 404 = giorno assente, non errore. Le serie bookDepth cominciano dopo quelle del tape e non
//     coprono tutti i simboli: il chiamante salta il giorno e lo dichiara nel report.
// =============================================================================================

/// <summary>Mercato del dump: i due domini hanno formati leggermente diversi (vedi BinanceDumpParser).</summary>
public enum DumpMarket
{
    /// <summary>Spot: quello su cui la piattaforma opera.</summary>
    Spot,

    /// <summary>Futures USD-M (perpetual): l'unico che pubblica anche la profondità del book.</summary>
    FuturesUm,
}

/// <summary>Scarica (e mette in cache) i dump giornalieri di Binance.</summary>
public sealed class BinanceDumpDownloader(HttpClient http, string? cacheDirectory = null)
{
    private const string Root = "https://data.binance.vision/data";

    /// <summary>Cartella di cache. Fuori dal repo per costruzione (default: temp di sistema).</summary>
    public string CacheDirectory { get; } =
        cacheDirectory
        ?? Environment.GetEnvironmentVariable("PROCIONE_MICROSTRUCTURE_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "procione-microstructure");

    /// <summary>Byte scaricati dalla rete in questa sessione (la cache non conta).</summary>
    public long DownloadedBytes { get; private set; }

    /// <summary>File serviti dalla cache senza toccare la rete.</summary>
    public int CacheHits { get; private set; }

    /// <summary>Giorni assenti a monte (404): non sono errori, sono buchi da dichiarare.</summary>
    public int MissingDays { get; private set; }

    private static string MarketPath(DumpMarket market) => market switch
    {
        DumpMarket.Spot => "spot",
        DumpMarket.FuturesUm => "futures/um",
        _ => throw new ArgumentOutOfRangeException(nameof(market)),
    };

    /// <summary>URL del dump dei trade aggregati di un giorno.</summary>
    public static string AggTradesUrl(DumpMarket market, string symbol, DateOnly day) =>
        $"{Root}/{MarketPath(market)}/daily/aggTrades/{symbol}/{symbol}-aggTrades-{day:yyyy-MM-dd}.zip";

    /// <summary>URL del dump delle klines di un giorno.</summary>
    public static string KlinesUrl(DumpMarket market, string symbol, string timeframe, DateOnly day) =>
        $"{Root}/{MarketPath(market)}/daily/klines/{symbol}/{timeframe}/{symbol}-{timeframe}-{day:yyyy-MM-dd}.zip";

    /// <summary>
    /// URL del dump della profondità del book di un giorno. Esiste SOLO sui futures USD-M: sullo
    /// spot Binance non pubblica alcun dato di book, ed è la ragione per cui l'OFI top-of-book vero
    /// non è misurabile storicamente.
    /// </summary>
    public static string BookDepthUrl(string symbol, DateOnly day) =>
        $"{Root}/futures/um/daily/bookDepth/{symbol}/{symbol}-bookDepth-{day:yyyy-MM-dd}.zip";

    /// <summary>
    /// Garantisce lo zip in cache e ne restituisce il percorso; <c>null</c> se il giorno non esiste
    /// a monte (404).
    /// </summary>
    public async Task<string?> EnsureAsync(string url, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, Path.GetFileName(new Uri(url).LocalPath));

        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            CacheHits++;
            return path;
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            MissingDays++;
            return null;
        }
        response.EnsureSuccessStatusCode();

        // Si scrive su un file temporaneo e si rinomina solo a scaricamento completo: così
        // un'interruzione non lascia in cache uno zip mutilo che al giro dopo passerebbe per valido.
        var partial = path + ".partial";
        await using (var dest = File.Create(partial))
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        {
            await src.CopyToAsync(dest, ct);
        }

        DownloadedBytes += new FileInfo(partial).Length;

        var expected = await TryGetChecksumAsync(url, ct);
        if (expected is not null && !await MatchesChecksumAsync(partial, expected, ct))
        {
            File.Delete(partial);
            throw new InvalidDataException(
                $"Checksum non corrispondente per {url}: il file scaricato non è quello pubblicato (download troncato o corrotto).");
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>Apre il primo (e unico) CSV dentro lo zip, in streaming.</summary>
    public static StreamReader OpenCsv(string zipPath)
    {
        var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Nessun CSV dentro {zipPath}.");
        // Lo StreamReader possiede lo stream dell'entry; l'archivio si chiude quando lo fa lui.
        return new StreamReader(new ZipEntryStream(archive, entry.Open()));
    }

    /// <summary>SHA-256 pubblicato accanto allo zip, se c'è.</summary>
    private async Task<string?> TryGetChecksumAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url + ".CHECKSUM", ct);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            var token = text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return token?.Length == 64 ? token : null;
        }
        catch (HttpRequestException)
        {
            return null; // il checksum è una garanzia in più, non un prerequisito
        }
    }

    internal static async Task<bool> MatchesChecksumAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tiene in vita l'archivio finché lo stream dell'entry è aperto.</summary>
    private sealed class ZipEntryStream(ZipArchive archive, Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); archive.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
