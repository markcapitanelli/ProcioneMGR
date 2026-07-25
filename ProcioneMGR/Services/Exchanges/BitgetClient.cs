using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Client Bitget Spot via REST pubblica v2 (dati di mercato non firmati).
/// Endpoint candele: GET /api/v2/spot/market/candles.
/// </summary>
public sealed class BitgetClient(
    HttpClient http,
    ILogger<BitgetClient> logger,
    IConfiguration? configuration = null,
    IExchangeClock? clock = null) : IExchangeClient, IFuturesExchangeClient
{
    public ExchangeName Exchange => ExchangeName.Bitget;

    /// <summary>
    /// Timestamp per le richieste FIRMATE, corretto per l'offset misurato verso il server Bitget
    /// (vedi <see cref="IExchangeClock"/>). Il clock è opzionale: se non iniettato si ricade
    /// sull'orologio locale, cioè esattamente il comportamento precedente.
    /// </summary>
    private long SignedTimestamp() => clock?.TimestampMillis(Exchange) ?? ExchangeSigning.UnixMillis(DateTime.UtcNow);

    // Bitget consigliato: 200 candele per richiesta per restare sotto i rate-limit.
    public int MaxCandlesPerRequest => 200;

    // Mappa timeframe canonico -> "granularity" Bitget v2.
    private static readonly IReadOnlyDictionary<string, string> Granularity = new Dictionary<string, string>
    {
        ["1m"] = "1min",
        ["5m"] = "5min",
        ["15m"] = "15min",
        ["30m"] = "30min",
        ["1h"] = "1h",
        ["4h"] = "4h",
        ["1d"] = "1day",
    };

    public async Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)
    {
        if (!Granularity.TryGetValue(timeframe, out var granularity))
        {
            throw new ArgumentException($"Timeframe non supportato da Bitget: '{timeframe}'.", nameof(timeframe));
        }

        var market = ToExchangeSymbol(symbol);
        var capped = Math.Clamp(limit, 1, MaxCandlesPerRequest);
        // Finestra temporale esplicita [since, since + capped*durata] per paginazione in avanti deterministica.
        var endTime = since + capped * Timeframes.ToMilliseconds(timeframe);
        var url = $"/api/v2/spot/market/candles?symbol={market}&granularity={granularity}&startTime={since}&endTime={endTime}&limit={capped}";

        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        EnsureBitgetOk(doc.RootElement);

        var data = doc.RootElement.GetProperty("data");
        var result = new List<Ohlcv>(data.GetArrayLength());
        foreach (var k in data.EnumerateArray())
        {
            // [ ts(string ms), open, high, low, close, baseVolume, quoteVolume ]
            // [T0.3] Bitget non espone trades/taker, ma il quoteVolume c'e' e veniva scartato.
            var ts = long.Parse(k[0].GetString() ?? "0", CultureInfo.InvariantCulture);
            result.Add(new Ohlcv(
                DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                ParseDecimal(k[1]),
                ParseDecimal(k[2]),
                ParseDecimal(k[3]),
                ParseDecimal(k[4]),
                ParseDecimal(k[5]),
                QuoteVolume: k.GetArrayLength() > 6 ? ParseDecimal(k[6]) : null));
        }

        // Bitget puo' restituire le candele non ordinate: normalizziamo in ordine cronologico.
        result.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return result;
    }

    public async Task<List<string>> GetSymbolsAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/api/v2/spot/public/symbols", ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        EnsureBitgetOk(doc.RootElement);

        var symbols = new List<string>();
        foreach (var s in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (s.TryGetProperty("status", out var status) &&
                !string.Equals(status.GetString(), "online", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var baseCoin = s.GetProperty("baseCoin").GetString();
            var quoteCoin = s.GetProperty("quoteCoin").GetString();
            if (!string.IsNullOrEmpty(baseCoin) && !string.IsNullOrEmpty(quoteCoin))
            {
                symbols.Add($"{baseCoin}/{quoteCoin}");
            }
        }

        return symbols;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("/api/v2/public/time", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Test connessione Bitget fallito.");
            return false;
        }
    }

    /// <summary>"BTC/USDT" -> "BTCUSDT".</summary>
    private static string ToExchangeSymbol(string canonical) =>
        canonical.Replace("/", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    private static decimal ParseDecimal(JsonElement element) =>
        decimal.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture);

    /// <summary>Bitget incapsula gli errori in un codice "00000" = successo.</summary>
    private static void EnsureBitgetOk(JsonElement root)
    {
        if (root.TryGetProperty("code", out var code) && code.GetString() != "00000")
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : "errore sconosciuto";
            throw new ExchangeClientException(ExchangeName.Bitget, 200, $"code={code.GetString()} msg={msg}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ExchangeClientException(ExchangeName.Bitget, (int)response.StatusCode, body);
    }

    // ---------------------------------------------------------------- trading (firmato v2)

    private const string Base = "https://api.bitget.com";

    public async Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        // GUARD (audit 2026-07, probabile bug serio): per i MARKET-BUY spot la v2 di Bitget
        // documenta "size" come CONTROVALORE QUOTE (USDT), non quantità base — questo codice
        // manda la quantità BASE, che su un buy reale produrrebbe un ordine di taglia
        // completamente sbagliata (es. size=0.05 BTC interpretato come 0.05 USDT, o viceversa
        // un fill enorme). Finché la semantica non è VERIFICATA dal vivo (tools/SpotVerify,
        // vedi docs/REPORT-HARDENING-P1-2026-07.md) i market-buy spot Bitget sono RIFIUTATI:
        // fallire forte batte un ordine di taglia sbagliata. Sblocco esplicito e consapevole
        // via Trading:Bitget:SpotMarketBuyVerified=true dopo la verifica.
        if (request.Type == "MARKET" && request.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            && configuration?.GetValue<bool>("Trading:Bitget:SpotMarketBuyVerified") != true)
        {
            const string reason = "MARKET-BUY spot Bitget bloccato: la semantica del campo 'size' (quote vs base) " +
                "non è ancora stata verificata dal vivo. Esegui tools/SpotVerify e poi imposta " +
                "Trading:Bitget:SpotMarketBuyVerified=true per sbloccare consapevolmente.";
            logger.LogError("{Reason} (symbol {Symbol}, qty {Qty})", reason, request.Symbol, request.Quantity);
            return new PlaceOrderResult { Success = false, Error = reason };
        }

        var market = ToExchangeSymbol(request.Symbol);
        var body = JsonSerializer.Serialize(new
        {
            symbol = market,
            side = request.Side.ToLowerInvariant(),                 // buy/sell
            orderType = request.Type.ToLowerInvariant(),            // market/limit
            force = "gtc",
            size = request.Quantity.ToString(CultureInfo.InvariantCulture),
            price = request.Type == "LIMIT" ? (request.Price ?? 0m).ToString(CultureInfo.InvariantCulture) : null,
            clientOid = request.ClientOrderId,
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var (ok, resp, uncertain, error) = await SignedAsync(HttpMethod.Post, "/api/v2/spot/trade/place-order", string.Empty, body, request.Credentials, ct);
        if (!ok)
        {
            return new PlaceOrderResult { Success = false, NetworkUncertain = uncertain, Error = error };
        }
        PlaceOrderResult result;
        using (var doc = JsonDocument.Parse(resp))
        {
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            result = new PlaceOrderResult
            {
                Success = true,
                ExchangeOrderId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("orderId", out var oid) ? oid.GetString() : null,
                Status = "submitted",
            };
        }
        return await EnrichWithFillAsync(result,
            () => GetOrderStatusAsync(request.Symbol, request.ClientOrderId, request.Credentials, ct), ct, logger);
    }

    public async Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { symbol = ToExchangeSymbol(symbol), clientOid = clientOrderId });
        var (ok, _, _, error) = await SignedAsync(HttpMethod.Post, "/api/v2/spot/trade/cancel-order", string.Empty, body, creds, ct);
        return new CancelOrderResult { Success = ok, Error = error };
    }

    public async Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default)
    {
        var query = $"symbol={ToExchangeSymbol(symbol)}";
        var (ok, resp, _, _) = await SignedAsync(HttpMethod.Get, "/api/v2/spot/trade/unfilled-orders", query, string.Empty, creds, ct);
        var list = new List<OpenOrder>();
        if (!ok) return list;
        using var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in data.EnumerateArray())
            {
                list.Add(new OpenOrder
                {
                    ExchangeOrderId = o.TryGetProperty("orderId", out var id) ? id.GetString() ?? "" : "",
                    ClientOrderId = o.TryGetProperty("clientOid", out var c) ? c.GetString() ?? "" : "",
                    Symbol = o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "",
                    Side = o.TryGetProperty("side", out var sd) ? sd.GetString() ?? "" : "",
                    Status = o.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                });
            }
        }
        return list;
    }

    public async Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)
    {
        // L'endpoint accetta orderId O clientOid; il simbolo non è richiesto.
        var (ok, resp, uncertain, error) = await SignedAsync(HttpMethod.Get, "/api/v2/spot/trade/orderInfo", $"clientOid={clientOrderId}", string.Empty, creds, ct);
        if (!ok)
        {
            return BuildStatusLookupFailure(resp, uncertain, error);
        }

        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return new OrderStatusResult { Found = false, Error = "orderInfo: nessun ordine per questo clientOid." };
        }

        var o = data[0];
        var filled = o.TryGetProperty("baseVolume", out var bv) ? ParseDecimalOrZero(bv) : 0m;
        var avg = o.TryGetProperty("priceAvg", out var pa) ? ParseDecimalOrZero(pa) : 0m;
        return new OrderStatusResult
        {
            Found = true,
            Status = NormalizeBitgetOrderStatus(o.TryGetProperty("status", out var st) ? st.GetString() ?? "" : ""),
            FilledPrice = avg > 0m ? avg : null,
            FilledQuantity = filled > 0m ? filled : null,
            ExchangeOrderId = o.TryGetProperty("orderId", out var oid) ? oid.GetString() : null,
        };
    }

    /// <summary>
    /// Mappa un fallimento del lookup di stato: 43001 ("The order does not exist") = NON TROVATO
    /// certo; QUALUNQUE altro errore = stato IGNOTO (NetworkUncertain), mai "non trovato" —
    /// scambiare un errore generico per un not-found riaprirebbe la finestra dell'ordine duplicato.
    /// </summary>
    private static OrderStatusResult BuildStatusLookupFailure(string body, bool uncertain, string? error)
    {
        if (!uncertain && !string.IsNullOrEmpty(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";
                if (code == "43001" || msg.Contains("not exist", StringComparison.OrdinalIgnoreCase))
                {
                    return new OrderStatusResult { Found = false, Error = error };
                }
            }
            catch (JsonException) { /* body non-JSON: stato resta ignoto */ }
        }
        return new OrderStatusResult { Found = false, NetworkUncertain = true, Error = error };
    }

    /// <summary>Normalizza lo stato ordine Bitget (spot "status" / mix "state") nello schema comune.</summary>
    internal static string NormalizeBitgetOrderStatus(string status) => status.ToLowerInvariant() switch
    {
        "live" or "new" or "init" or "not_trigger" => "Open",
        "partially_filled" or "partial-fill" => "PartiallyFilled",
        "filled" or "full-fill" => "Filled",
        "cancelled" or "canceled" or "cancel" => "Cancelled",
        "rejected" or "reject" => "Rejected",
        "expired" => "Expired",
        // Stato sconosciuto: trattato come vivo, così il riconciliatore cancella e ricontrolla.
        _ => "Open",
    };

    /// <summary>Come <see cref="ParseDecimal"/> ma tollera campi vuoti (Bitget usa "" per i non valorizzati).</summary>
    private static decimal ParseDecimalOrZero(JsonElement element)
    {
        var raw = element.GetString();
        return string.IsNullOrEmpty(raw) ? 0m : decimal.Parse(raw, CultureInfo.InvariantCulture);
    }

    public async Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default)
    {
        var (ok, resp, _, _) = await SignedAsync(HttpMethod.Get, "/api/v2/spot/account/assets", string.Empty, string.Empty, creds, ct);
        var bal = new AccountBalance();
        if (!ok) return bal;
        using var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in data.EnumerateArray())
            {
                var coin = a.TryGetProperty("coin", out var c) ? c.GetString() ?? "" : "";
                var avail = a.TryGetProperty("available", out var av) ? ParseDecimal(av) : 0m;
                if (avail > 0m) bal.Free[coin] = avail;
            }
        }
        return bal;
    }

    public async Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var filters = new SymbolFilters();
        using var resp = await http.GetAsync($"{Base}/api/v2/spot/public/symbols?symbol={market}", ct);
        if (!resp.IsSuccessStatusCode) return filters;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0) return filters;

        var s = data[0];
        decimal Pow10Neg(int p) => p <= 0 ? 1m : 1m / (decimal)Math.Pow(10, p);
        if (s.TryGetProperty("quantityPrecision", out var qp) && int.TryParse(qp.GetString(), out var qpi))
            filters.StepSize = Pow10Neg(qpi);
        if (s.TryGetProperty("pricePrecision", out var pp) && int.TryParse(pp.GetString(), out var ppi))
            filters.TickSize = Pow10Neg(ppi);
        if (s.TryGetProperty("minTradeAmount", out var mta) && decimal.TryParse(mta.GetString(), CultureInfo.InvariantCulture, out var minQty))
            filters.MinQty = minQty;
        if (s.TryGetProperty("minTradeUSDT", out var mtu) && decimal.TryParse(mtu.GetString(), CultureInfo.InvariantCulture, out var minN))
            filters.MinNotional = minN;
        return filters;
    }

    /// <summary>Richiesta firmata Bitget. prehash = timestamp+method+path(+?query)+body.</summary>
    private async Task<(bool Ok, string Body, bool Uncertain, string? Error)> SignedAsync(
        HttpMethod method, string path, string query, string jsonBody, TradingCredentials creds, CancellationToken ct)
    {
        var ts = SignedTimestamp().ToString();
        var requestPath = string.IsNullOrEmpty(query) ? path : $"{path}?{query}";
        var prehash = ts + method.Method.ToUpperInvariant() + requestPath + jsonBody;
        var sign = ExchangeSigning.HmacSha256Base64(prehash, creds.ApiSecret);

        using var req = new HttpRequestMessage(method, Base + requestPath);
        req.Headers.Add("ACCESS-KEY", creds.ApiKey);
        req.Headers.Add("ACCESS-SIGN", sign);
        req.Headers.Add("ACCESS-TIMESTAMP", ts);
        req.Headers.Add("ACCESS-PASSPHRASE", creds.Passphrase ?? string.Empty);
        req.Headers.Add("locale", "en-US");
        if (creds.IsTestnet)
        {
            req.Headers.Add("paptrading", "1"); // Bitget demo trading
        }
        if (method != HttpMethod.Get && !string.IsNullOrEmpty(jsonBody))
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, body, (int)resp.StatusCode >= 500, $"HTTP {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
            }
            // Bitget incapsula errori applicativi in code != "00000".
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.GetString() != "00000")
            {
                var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : "errore";
                return (false, body, false, $"Bitget code={code.GetString()} msg={msg}");
            }
            return (true, body, false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, string.Empty, true, ex.Message);
        }
    }

    // ---------------------------------------------------------------- futures (Mix v2, USDT perpetual, margine isolato)
    //
    // NOTA IMPORTANTE: a differenza del Testnet Binance (dominio separato, ampiamente
    // documentato e verificato dal vivo in questa piattaforma), Bitget non ha un dominio
    // testnet: il "Demo Trading" usa lo STESSO dominio con productType "SUSDT-FUTURES" e
    // l'header "paptrading" (gia' gestito da SignedAsync per creds.IsTestnet). Endpoint e
    // struttura payload seguono la documentazione pubblica Bitget API v2 Mix, ma — a
    // differenza del client Binance Futures — NON sono stati ancora verificati con una
    // chiamata reale in questa sessione: vanno testati contro il Demo Trading reale prima
    // di qualunque uso in produzione (vedi report finale).

    /// <summary>"USDT-FUTURES" per il conto reale, "SUSDT-FUTURES" per il Demo Trading Bitget.</summary>
    private static string ProductType(bool testnet) => testnet ? "SUSDT-FUTURES" : "USDT-FUTURES";

    /// <summary>
    /// I dati di MERCATO (contratti, funding) sono pubblici e condivisi tra reale e demo:
    /// productType "S..." (demo) su questi endpoint risponde sempre con data=[] (verificato
    /// empiricamente). Il productType demo si usa SOLO sugli endpoint privati/di account
    /// (ordini, posizioni, saldo), dove seleziona davvero l'ambiente simulato.
    /// </summary>
    private const string PublicMarketProductType = "USDT-FUTURES";

    public async Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var productType = ProductType(credentials.IsTestnet);

        // Margine ISOLATO esplicito (non "crossed"): ogni posizione rischia solo il proprio margine.
        var marginModeBody = JsonSerializer.Serialize(new
        {
            symbol = market,
            productType,
            marginCoin = "USDT",
            marginMode = "isolated",
        });
        await SignedAsync(HttpMethod.Post, "/api/v2/mix/account/set-margin-mode", string.Empty, marginModeBody, credentials, ct);

        var leverageBody = JsonSerializer.Serialize(new
        {
            symbol = market,
            productType,
            marginCoin = "USDT",
            leverage = leverage.ToString(CultureInfo.InvariantCulture),
        });
        var (ok, resp, _, error) = await SignedAsync(HttpMethod.Post, "/api/v2/mix/account/set-leverage", string.Empty, leverageBody, credentials, ct);
        return new SetLeverageResult { Success = ok, Leverage = leverage, Error = error };
    }

    public async Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(request.Symbol);
        var productType = ProductType(request.Credentials.IsTestnet);
        var body = JsonSerializer.Serialize(new
        {
            symbol = market,
            productType,
            marginMode = "isolated",
            marginCoin = "USDT",
            side = request.Side.ToLowerInvariant(),              // buy/sell (posizione one-way)
            orderType = request.Type.ToLowerInvariant(),          // market/limit
            force = "gtc",
            size = request.Quantity.ToString(CultureInfo.InvariantCulture),
            price = request.Type == "LIMIT" ? (request.Price ?? 0m).ToString(CultureInfo.InvariantCulture) : null,
            clientOid = request.ClientOrderId,
            reduceOnly = reduceOnly ? "YES" : "NO",
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var (ok, resp, uncertain, error) = await SignedAsync(HttpMethod.Post, "/api/v2/mix/order/place-order", string.Empty, body, request.Credentials, ct);
        if (!ok)
        {
            return new PlaceOrderResult { Success = false, NetworkUncertain = uncertain, Error = error };
        }
        PlaceOrderResult result;
        using (var doc = JsonDocument.Parse(resp))
        {
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            result = new PlaceOrderResult
            {
                Success = true,
                ExchangeOrderId = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("orderId", out var oid) ? oid.GetString() : null,
                Status = "submitted",
            };
        }
        return await EnrichWithFillAsync(result,
            () => GetFuturesOrderStatusAsync(request.Symbol, request.ClientOrderId, request.Credentials, ct), ct, logger);
    }

    /// <summary>
    /// [M4] Bitget non restituisce i fill nella risposta di piazzamento (a differenza del POST
    /// Binance con <c>fills[]</c>): senza un lookup l'engine ripiegava SEMPRE su currentPrice
    /// come prezzo d'ingresso e lo slippage reale restava invisibile a PnL/stop. Poll breve e
    /// best-effort del lookup C2: subito + 1 retry dopo 500ms se l'ordine è ancora vivo; ogni
    /// altro esito lascia i fill null (l'engine degrada come prima). MAI un errore qui può
    /// far fallire un piazzamento riuscito.
    /// </summary>
    private static async Task<PlaceOrderResult> EnrichWithFillAsync(
        PlaceOrderResult placed, Func<Task<OrderStatusResult>> lookup, CancellationToken ct, ILogger logger)
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var status = await lookup();
                if (status.Found && status.Status is "Filled" or "PartiallyFilled" && status.FilledQuantity is > 0m)
                {
                    placed.FilledPrice = status.FilledPrice;
                    placed.FilledQuantity = status.FilledQuantity;
                    placed.Status = status.Status;
                    return placed;
                }
                if (status.NetworkUncertain || !status.Found || status.IsTerminalUnfilled)
                {
                    return placed;   // niente di meglio da fare: fill null, degradazione odierna
                }
                if (attempt == 0) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort per definizione (il place è riuscito, solo l'arricchimento fallisce), ma
            // un fallimento sistematico di questo lookup (es. l'endpoint Bitget cambia forma) deve
            // lasciare una traccia diagnosticabile invece di sparire nel nulla.
            logger.LogDebug(ex, "EnrichWithFillAsync: lookup fill fallito, l'ordine resta senza prezzo/quantità arricchiti.");
        }
        return placed;
    }

    /// <summary>
    /// [P0-5] Ordine TRIGGER reduce-only "resting" via place-plan-order (Mix v2): stop-market o
    /// take-profit-market attivato sul MARK PRICE, che chiude la posizione anche se il processo va giù.
    /// ⚠️ Costruzione payload conforme alla doc pubblica Bitget v2 Mix ma — come gli altri endpoint
    /// futures Bitget di questo client — NON ancora verificata con una chiamata reale: va provata contro
    /// il Demo Trading (paptrading) prima di abilitare <c>UseExchangeRestingStops</c> in Live.
    /// </summary>
    public async Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)
    {
        if (request.TriggerPrice is not decimal trigger || trigger <= 0m)
        {
            return new PlaceOrderResult { Success = false, Error = "TriggerPrice mancante o non valido per l'ordine trigger Bitget." };
        }

        var body = BuildTriggerPlanBody(
            market: ToExchangeSymbol(request.Symbol),
            productType: ProductType(request.Credentials.IsTestnet),
            side: request.Side.ToLowerInvariant(),
            triggerPrice: trigger,
            size: request.Quantity,
            clientOid: request.ClientOrderId);

        var (ok, resp, uncertain, error) = await SignedAsync(HttpMethod.Post, "/api/v2/mix/order/place-plan-order", string.Empty, body, request.Credentials, ct);
        if (!ok)
        {
            return new PlaceOrderResult { Success = false, NetworkUncertain = uncertain, Error = error };
        }
        using var doc = JsonDocument.Parse(resp);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
        string? planId = null;
        if (data.ValueKind == JsonValueKind.Object)
        {
            planId = data.TryGetProperty("orderId", out var oid) ? oid.GetString()
                   : data.TryGetProperty("planOrderId", out var pid) ? pid.GetString()
                   : null;
        }
        return new PlaceOrderResult { Success = true, ExchangeOrderId = planId, Status = "plan-submitted" };
    }

    /// <summary>
    /// Corpo JSON di un ordine plan reduce-only market su mark price (funzione pura, testabile). Il verso
    /// (stop vs take-profit) è implicito nel <paramref name="triggerPrice"/> rispetto al mark corrente.
    /// </summary>
    internal static string BuildTriggerPlanBody(string market, string productType, string side, decimal triggerPrice, decimal size, string clientOid)
        => JsonSerializer.Serialize(new
        {
            symbol = market,
            productType,
            marginMode = "isolated",
            marginCoin = "USDT",
            planType = "normal_plan",
            triggerType = "mark_price",
            triggerPrice = triggerPrice.ToString(CultureInfo.InvariantCulture),
            side,                                   // buy/sell = lato di CHIUSURA (opposto alla posizione)
            orderType = "market",
            size = size.ToString(CultureInfo.InvariantCulture),
            reduceOnly = "YES",
            clientOid,
        });

    public async Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var productType = ProductType(credentials.IsTestnet);
        var query = $"symbol={market}&productType={productType}&marginCoin=USDT";
        var (ok, resp, _, _) = await SignedAsync(HttpMethod.Get, "/api/v2/mix/position/single-position", query, string.Empty, credentials, ct);
        if (!ok) return null;

        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return null; // nessuna posizione aperta
        }
        var p = data[0];
        var total = p.TryGetProperty("total", out var t) ? ParseDecimal(t) : 0m;
        if (total == 0m) return null;

        return new FuturesPosition
        {
            Symbol = symbol,
            Quantity = total,
            Side = p.TryGetProperty("holdSide", out var hs) ? (hs.GetString() ?? "").ToUpperInvariant() : "",
            EntryPrice = p.TryGetProperty("averageOpenPrice", out var aop) ? ParseDecimal(aop) : 0m,
            MarkPrice = p.TryGetProperty("markPrice", out var mp) ? ParseDecimal(mp) : 0m,
            Leverage = p.TryGetProperty("leverage", out var lv) && int.TryParse(lv.GetString(), out var levInt) ? levInt : 1,
            LiquidationPrice = p.TryGetProperty("liquidationPrice", out var lp) ? ParseDecimal(lp) : 0m,
            UnrealizedPnl = p.TryGetProperty("unrealizedPL", out var up) ? ParseDecimal(up) : 0m,
            MarginBalance = p.TryGetProperty("marginSize", out var ms) ? ParseDecimal(ms) : 0m,
        };
    }

    public async Task<CancelOrderResult> CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            symbol = ToExchangeSymbol(symbol),
            productType = ProductType(credentials.IsTestnet),
            clientOid = clientOrderId,
        });
        var (ok, _, _, error) = await SignedAsync(HttpMethod.Post, "/api/v2/mix/order/cancel-order", string.Empty, body, credentials, ct);
        return new CancelOrderResult { Success = ok, Error = error };
    }

    public async Task<List<OpenOrder>> GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var productType = ProductType(credentials.IsTestnet);
        var query = $"symbol={market}&productType={productType}";
        var (ok, resp, _, _) = await SignedAsync(HttpMethod.Get, "/api/v2/mix/order/orders-pending", query, string.Empty, credentials, ct);
        var list = new List<OpenOrder>();
        if (!ok) return list;
        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var dataRoot)) return list;
        // La risposta puo' essere un array diretto o un oggetto con campo "entrustedList" a seconda della versione.
        var entries = dataRoot.ValueKind == JsonValueKind.Array
            ? dataRoot
            : (dataRoot.TryGetProperty("entrustedList", out var el) ? el : default);
        if (entries.ValueKind != JsonValueKind.Array) return list;

        foreach (var o in entries.EnumerateArray())
        {
            list.Add(new OpenOrder
            {
                ExchangeOrderId = o.TryGetProperty("orderId", out var id) ? id.GetString() ?? "" : "",
                ClientOrderId = o.TryGetProperty("clientOid", out var c) ? c.GetString() ?? "" : "",
                Symbol = o.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "",
                Side = o.TryGetProperty("side", out var sd) ? sd.GetString() ?? "" : "",
                Status = o.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            });
        }
        return list;
    }

    public async Task<OrderStatusResult> GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var productType = ProductType(credentials.IsTestnet);
        var query = $"symbol={market}&productType={productType}&clientOid={clientOrderId}";
        var (ok, resp, uncertain, error) = await SignedAsync(HttpMethod.Get, "/api/v2/mix/order/detail", query, string.Empty, credentials, ct);
        if (!ok)
        {
            return BuildStatusLookupFailure(resp, uncertain, error);
        }

        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var o) || o.ValueKind != JsonValueKind.Object)
        {
            return new OrderStatusResult { Found = false, Error = "order detail: nessun ordine per questo clientOid." };
        }

        var filled = o.TryGetProperty("baseVolume", out var bv) ? ParseDecimalOrZero(bv) : 0m;
        var avg = o.TryGetProperty("priceAvg", out var pa) ? ParseDecimalOrZero(pa) : 0m;
        return new OrderStatusResult
        {
            Found = true,
            Status = NormalizeBitgetOrderStatus(o.TryGetProperty("state", out var st) ? st.GetString() ?? "" : ""),
            FilledPrice = avg > 0m ? avg : null,
            FilledQuantity = filled > 0m ? filled : null,
            ExchangeOrderId = o.TryGetProperty("orderId", out var oid) ? oid.GetString() : null,
        };
    }

    public async Task<FuturesBalance> GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)
    {
        // NB: l'endpoint "singolo account" (.../account/account) richiede ANCHE "symbol", che
        // questo metodo non riceve (il saldo è a livello di account, non di simbolo) — con solo
        // productType+marginCoin Bitget rifiuta la richiesta con code 400172 "Parameter
        // verification failed", un fallimento che veniva ingoiato silenziosamente restituendo un
        // FuturesBalance vuoto invece di propagare l'errore (bug reale, trovato verificando le
        // credenziali Bitget appena configurate: la UI mostrava "available=0" indistinguibile da
        // un vero saldo zero). Si usa invece l'endpoint "lista account" (.../account/accounts),
        // che con il solo productType restituisce l'array di conti per moneta di margine.
        var productType = ProductType(credentials.IsTestnet);
        var (ok, resp, _, _) = await SignedAsync(HttpMethod.Get, "/api/v2/mix/account/accounts", $"productType={productType}", string.Empty, credentials, ct);
        if (!ok) return new FuturesBalance();

        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new FuturesBalance();
        }
        foreach (var acct in data.EnumerateArray())
        {
            if (!acct.TryGetProperty("marginCoin", out var mc) || mc.GetString() != "USDT") continue;
            return new FuturesBalance
            {
                AvailableMargin = acct.TryGetProperty("available", out var av) ? ParseDecimal(av) : 0m,
                TotalEquity = acct.TryGetProperty("accountEquity", out var eq) ? ParseDecimal(eq) : 0m,
            };
        }
        return new FuturesBalance();
    }

    public async Task<SymbolFilters> GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var filters = new SymbolFilters();

        // NB: productType sempre "reale" qui, vedi PublicMarketProductType.
        using var resp = await http.GetAsync($"{Base}/api/v2/mix/market/contracts?productType={PublicMarketProductType}&symbol={market}", ct);
        if (!resp.IsSuccessStatusCode) return filters;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return filters;
        }

        var s = data[0];
        decimal Pow10Neg(int p) => p <= 0 ? 1m : 1m / (decimal)Math.Pow(10, p);
        if (s.TryGetProperty("volumePlace", out var vp) && int.TryParse(vp.GetString(), out var vpi))
            filters.StepSize = Pow10Neg(vpi);
        if (s.TryGetProperty("pricePlace", out var pp) && int.TryParse(pp.GetString(), out var ppi))
            filters.TickSize = Pow10Neg(ppi);
        if (s.TryGetProperty("minTradeNum", out var mtn) && decimal.TryParse(mtn.GetString(), CultureInfo.InvariantCulture, out var minQty))
            filters.MinQty = minQty;
        if (s.TryGetProperty("minTradeUSDT", out var mtu) && decimal.TryParse(mtu.GetString(), CultureInfo.InvariantCulture, out var minNotional))
            filters.MinNotional = minNotional;
        return filters;
    }

    public async Task<decimal> GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        // NB: productType sempre "reale" qui, vedi PublicMarketProductType.
        using var resp = await http.GetAsync($"{Base}/api/v2/mix/market/current-fund-rate?symbol={market}&productType={PublicMarketProductType}", ct);
        if (!resp.IsSuccessStatusCode) return 0m;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return 0m;
        }
        // Bitget riporta il funding come frazione per periodo (es. 0.0001 = 0.01%): x100 per la
        // convenzione "percentuale" del resto della piattaforma (stessa scelta fatta per Binance).
        return data[0].TryGetProperty("fundingRate", out var fr) ? ParseDecimal(fr) * 100m : 0m;
    }
}
