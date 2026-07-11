using System.Globalization;
using System.Text.Json;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Exchanges;

/// <summary>
/// Client Binance Spot via REST pubblica (nessuna firma necessaria per i dati di mercato).
/// Endpoint klines: GET /api/v3/klines, max 1000 candele per richiesta.
/// </summary>
public sealed class BinanceClient(HttpClient http, ILogger<BinanceClient> logger) : IExchangeClient, IFuturesExchangeClient
{
    public ExchangeName Exchange => ExchangeName.Binance;

    // Binance Spot consente fino a 1000 candele per richiesta klines.
    public int MaxCandlesPerRequest => 1000;

    public async Task<List<Ohlcv>> FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)
    {
        if (!Timeframes.IsSupported(timeframe))
        {
            throw new ArgumentException($"Timeframe non supportato: '{timeframe}'.", nameof(timeframe));
        }

        var market = ToExchangeSymbol(symbol);
        var capped = Math.Clamp(limit, 1, MaxCandlesPerRequest);
        // Binance usa gli stessi codici timeframe canonici (1m, 1h, 1d, ...).
        var url = $"/api/v3/klines?symbol={market}&interval={timeframe}&startTime={since}&limit={capped}";

        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = new List<Ohlcv>(doc.RootElement.GetArrayLength());
        foreach (var k in doc.RootElement.EnumerateArray())
        {
            // [ openTime, open, high, low, close, volume, closeTime, ... ]
            var openTime = k[0].GetInt64();
            result.Add(new Ohlcv(
                DateTimeOffset.FromUnixTimeMilliseconds(openTime).UtcDateTime,
                ParseDecimal(k[1]),
                ParseDecimal(k[2]),
                ParseDecimal(k[3]),
                ParseDecimal(k[4]),
                ParseDecimal(k[5])));
        }

        return result;
    }

    public async Task<List<string>> GetSymbolsAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/api/v3/exchangeInfo", ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var symbols = new List<string>();
        foreach (var s in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (s.TryGetProperty("status", out var status) && status.GetString() != "TRADING")
            {
                continue;
            }
            var baseAsset = s.GetProperty("baseAsset").GetString();
            var quoteAsset = s.GetProperty("quoteAsset").GetString();
            if (!string.IsNullOrEmpty(baseAsset) && !string.IsNullOrEmpty(quoteAsset))
            {
                symbols.Add($"{baseAsset}/{quoteAsset}");
            }
        }

        return symbols;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync("/api/v3/ping", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Test connessione Binance fallito.");
            return false;
        }
    }

    /// <summary>"BTC/USDT" -> "BTCUSDT".</summary>
    private static string ToExchangeSymbol(string canonical) =>
        canonical.Replace("/", string.Empty).Replace("-", string.Empty).ToUpperInvariant();

    private static decimal ParseDecimal(JsonElement element) =>
        decimal.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture);

    private static decimal? ParseDecimalOrNull(JsonElement element)
    {
        var raw = element.GetString();
        return string.IsNullOrEmpty(raw) ? null : decimal.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ExchangeClientException(ExchangeName.Binance, (int)response.StatusCode, body);
    }

    // ---------------------------------------------------------------- trading (firmato)

    private const string ProdBase = "https://api.binance.com";
    private const string TestnetBase = "https://testnet.binance.vision";

    // USDT-M Futures: dominio COMPLETAMENTE separato dallo spot (sia in prod sia in testnet).
    private const string FuturesProdBase = "https://fapi.binance.com";
    private const string FuturesTestnetBase = "https://testnet.binancefuture.com";

    private static string Inv(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    public async Task<PlaceOrderResult> PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(request.Symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var parts = new List<string>
        {
            $"symbol={market}",
            $"side={request.Side}",
            $"type={request.Type}",
            $"quantity={Inv(request.Quantity)}",
        };
        if (request.Type == "LIMIT")
        {
            parts.Add("timeInForce=GTC");
            parts.Add($"price={Inv(request.Price ?? 0m)}");
        }
        if (!string.IsNullOrEmpty(request.ClientOrderId))
        {
            parts.Add($"newClientOrderId={request.ClientOrderId}");
        }
        parts.Add("recvWindow=5000");
        parts.Add($"timestamp={ts}");
        var query = string.Join("&", parts);

        var (ok, body, uncertain, error) = await SignedAsync(HttpMethod.Post, "/api/v3/order", query, request.Credentials, ct);
        if (!ok)
        {
            return new PlaceOrderResult { Success = false, NetworkUncertain = uncertain, Error = error };
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var result = new PlaceOrderResult
        {
            Success = true,
            ExchangeOrderId = root.TryGetProperty("orderId", out var oid) ? oid.GetRawText() : null,
            Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            FilledQuantity = root.TryGetProperty("executedQty", out var eq) ? ParseDecimal(eq) : null,
        };
        if (root.TryGetProperty("fills", out var fills) && fills.GetArrayLength() > 0)
        {
            decimal totQ = 0m, totN = 0m;
            foreach (var f in fills.EnumerateArray())
            {
                var p = ParseDecimal(f.GetProperty("price"));
                var q = ParseDecimal(f.GetProperty("qty"));
                totQ += q; totN += p * q;
            }
            result.FilledPrice = totQ > 0m ? totN / totQ : null;
        }
        return result;
    }

    public async Task<CancelOrderResult> CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&origClientOrderId={clientOrderId}&recvWindow=5000&timestamp={ts}";
        var (ok, _, _, error) = await SignedAsync(HttpMethod.Delete, "/api/v3/order", query, creds, ct);
        return new CancelOrderResult { Success = ok, Error = error };
    }

    public async Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&recvWindow=5000&timestamp={ts}";
        var (ok, body, _, _) = await SignedAsync(HttpMethod.Get, "/api/v3/openOrders", query, creds, ct);
        var list = new List<OpenOrder>();
        if (!ok) return list;

        using var doc = JsonDocument.Parse(body);
        foreach (var o in doc.RootElement.EnumerateArray())
        {
            list.Add(new OpenOrder
            {
                ExchangeOrderId = o.GetProperty("orderId").GetRawText(),
                ClientOrderId = o.TryGetProperty("clientOrderId", out var c) ? c.GetString() ?? "" : "",
                Symbol = o.GetProperty("symbol").GetString() ?? "",
                Side = o.GetProperty("side").GetString() ?? "",
                Quantity = ParseDecimal(o.GetProperty("origQty")),
                Price = ParseDecimal(o.GetProperty("price")),
                Status = o.GetProperty("status").GetString() ?? "",
            });
        }
        return list;
    }

    public async Task<OrderStatusResult> GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&origClientOrderId={clientOrderId}&recvWindow=5000&timestamp={ts}";
        var (ok, body, uncertain, error) = await SignedAsync(HttpMethod.Get, "/api/v3/order", query, creds, ct);
        if (!ok)
        {
            return BuildStatusLookupFailure(body, uncertain, error);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var executed = root.TryGetProperty("executedQty", out var eq) ? ParseDecimal(eq) : 0m;

        // L'endpoint di query non restituisce fills[] (a differenza del place):
        // prezzo medio = controvalore quote eseguito / quantità base eseguita.
        decimal? avg = null;
        if (executed > 0m && root.TryGetProperty("cummulativeQuoteQty", out var cq))
        {
            var quote = ParseDecimal(cq);
            if (quote > 0m) avg = quote / executed;
        }

        return new OrderStatusResult
        {
            Found = true,
            Status = NormalizeBinanceOrderStatus(root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : ""),
            FilledPrice = avg,
            FilledQuantity = executed > 0m ? executed : null,
            ExchangeOrderId = root.TryGetProperty("orderId", out var oid) ? oid.GetRawText() : null,
        };
    }

    /// <summary>
    /// Mappa un fallimento del lookup di stato: -2013 ("Order does not exist") = NON TROVATO
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
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("code", out var code)
                    && code.ValueKind == JsonValueKind.Number
                    && code.TryGetInt32(out var c) && c == -2013)
                {
                    return new OrderStatusResult { Found = false, Error = error };
                }
            }
            catch (JsonException) { /* body non-JSON: stato resta ignoto */ }
        }
        return new OrderStatusResult { Found = false, NetworkUncertain = true, Error = error };
    }

    /// <summary>Normalizza lo stato ordine Binance nello schema comune di <see cref="OrderStatusResult"/>.</summary>
    internal static string NormalizeBinanceOrderStatus(string status) => status.ToUpperInvariant() switch
    {
        "NEW" or "PENDING_NEW" or "PENDING_CANCEL" => "Open",
        "PARTIALLY_FILLED" => "PartiallyFilled",
        "FILLED" => "Filled",
        "CANCELED" => "Cancelled",
        "REJECTED" => "Rejected",
        "EXPIRED" or "EXPIRED_IN_MATCH" => "Expired",
        // Stato sconosciuto: trattato come vivo, così il riconciliatore cancella e ricontrolla
        // invece di dichiararlo erroneamente concluso.
        _ => "Open",
    };

    public async Task<AccountBalance> GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default)
    {
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"recvWindow=5000&timestamp={ts}";
        var (ok, body, _, _) = await SignedAsync(HttpMethod.Get, "/api/v3/account", query, creds, ct);
        var bal = new AccountBalance();
        if (!ok) return bal;

        using var doc = JsonDocument.Parse(body);
        foreach (var b in doc.RootElement.GetProperty("balances").EnumerateArray())
        {
            var asset = b.GetProperty("asset").GetString() ?? "";
            var free = ParseDecimal(b.GetProperty("free"));
            var locked = ParseDecimal(b.GetProperty("locked"));
            if (free > 0m) bal.Free[asset] = free;
            if (locked > 0m) bal.Locked[asset] = locked;
        }
        return bal;
    }

    public async Task<SymbolFilters> GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
    {
        var baseUrl = testnet ? TestnetBase : ProdBase;
        var market = ToExchangeSymbol(symbol);
        var filters = new SymbolFilters();

        using var resp = await http.GetAsync($"{baseUrl}/api/v3/exchangeInfo?symbol={market}", ct);
        if (!resp.IsSuccessStatusCode)
        {
            return filters;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var symbols = doc.RootElement.GetProperty("symbols");
        if (symbols.GetArrayLength() == 0) return filters;

        foreach (var f in symbols[0].GetProperty("filters").EnumerateArray())
        {
            var type = f.GetProperty("filterType").GetString();
            switch (type)
            {
                case "LOT_SIZE":
                    filters.StepSize = ParseDecimal(f.GetProperty("stepSize"));
                    filters.MinQty = ParseDecimal(f.GetProperty("minQty"));
                    break;
                case "PRICE_FILTER":
                    filters.TickSize = ParseDecimal(f.GetProperty("tickSize"));
                    break;
                case "NOTIONAL":
                case "MIN_NOTIONAL":
                    if (f.TryGetProperty("minNotional", out var mn))
                    {
                        filters.MinNotional = ParseDecimal(mn);
                    }
                    break;
            }
        }
        return filters;
    }

    /// <summary>
    /// Esegue una richiesta firmata. Distingue: 4xx = errore noto (ordine NON piazzato),
    /// 5xx/eccezione di rete = INCERTO (uncertain=true -> il chiamante deve riconciliare).
    /// </summary>
    /// <param name="prodBaseOverride">Dominio prod alternativo (es. fapi.binance.com per i futures). Default = spot.</param>
    /// <param name="testnetBaseOverride">Dominio testnet alternativo. Default = spot.</param>
    private async Task<(bool Ok, string Body, bool Uncertain, string? Error)> SignedAsync(
        HttpMethod method, string path, string query, TradingCredentials creds, CancellationToken ct,
        string? prodBaseOverride = null, string? testnetBaseOverride = null)
    {
        var baseUrl = creds.IsTestnet ? (testnetBaseOverride ?? TestnetBase) : (prodBaseOverride ?? ProdBase);
        var sig = ExchangeSigning.HmacSha256Hex(query, creds.ApiSecret);
        var url = $"{baseUrl}{path}?{query}&signature={sig}";

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-MBX-APIKEY", creds.ApiKey);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var uncertain = (int)resp.StatusCode >= 500;
                return (false, body, uncertain, $"HTTP {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
            }
            return (true, body, false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, string.Empty, true, ex.Message); // rete: stato dell'ordine INCERTO
        }
    }

    // ---------------------------------------------------------------- futures (USDT-M, margine isolato)

    public async Task<SetLeverageResult> SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);

        // Margine ISOLATO esplicito (non CROSSED): ogni posizione rischia solo il proprio
        // margine. Ignora l'errore "no need to change margin type" (già isolato).
        var marginQuery = $"symbol={market}&marginType=ISOLATED&timestamp={ts}";
        await SignedAsync(HttpMethod.Post, "/fapi/v1/marginType", marginQuery, credentials, ct, FuturesProdBase, FuturesTestnetBase);

        var levQuery = $"symbol={market}&leverage={leverage}&timestamp={ExchangeSigning.UnixMillis(DateTime.UtcNow)}";
        var (ok, body, _, error) = await SignedAsync(HttpMethod.Post, "/fapi/v1/leverage", levQuery, credentials, ct, FuturesProdBase, FuturesTestnetBase);
        if (!ok)
        {
            return new SetLeverageResult { Success = false, Error = error };
        }
        using var doc = JsonDocument.Parse(body);
        var actual = doc.RootElement.TryGetProperty("leverage", out var l) ? l.GetInt32() : leverage;
        return new SetLeverageResult { Success = true, Leverage = actual };
    }

    public async Task<PlaceOrderResult> PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(request.Symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var parts = new List<string>
        {
            $"symbol={market}",
            $"side={request.Side}",
            $"type={request.Type}",
            $"quantity={Inv(request.Quantity)}",
        };
        if (request.Type == "LIMIT")
        {
            parts.Add("timeInForce=GTC");
            parts.Add($"price={Inv(request.Price ?? 0m)}");
        }
        if (reduceOnly)
        {
            parts.Add("reduceOnly=true");
        }
        if (!string.IsNullOrEmpty(request.ClientOrderId))
        {
            parts.Add($"newClientOrderId={request.ClientOrderId}");
        }
        parts.Add("recvWindow=5000");
        parts.Add($"timestamp={ts}");
        var query = string.Join("&", parts);

        var (ok, body, uncertain, error) = await SignedAsync(HttpMethod.Post, "/fapi/v1/order", query, request.Credentials, ct, FuturesProdBase, FuturesTestnetBase);
        if (!ok)
        {
            return new PlaceOrderResult { Success = false, NetworkUncertain = uncertain, Error = error };
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new PlaceOrderResult
        {
            Success = true,
            ExchangeOrderId = root.TryGetProperty("orderId", out var oid) ? oid.GetRawText() : null,
            Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
            FilledQuantity = root.TryGetProperty("executedQty", out var eq) ? ParseDecimal(eq) : null,
            // I futures MARKET restituiscono avgPrice direttamente (a differenza dello spot,
            // che va ricostruito dai fills[]).
            FilledPrice = root.TryGetProperty("avgPrice", out var ap) ? ParseDecimalOrNull(ap) : null,
        };
    }

    /// <summary>
    /// [P0-5] Ordine TRIGGER reduce-only "resting" via /fapi/v1/order: STOP_MARKET (stop) o
    /// TAKE_PROFIT_MARKET (target), attivato sul MARK price (workingType=MARK_PRICE), che chiude la
    /// posizione anche se il processo va giù. Verificabile sul Testnet Binance Futures
    /// (testnet.binancefuture.com) prima di abilitare <c>UseExchangeRestingStops</c> in Live.
    /// </summary>
    public async Task<PlaceOrderResult> PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)
    {
        if (request.TriggerPrice is not decimal trigger || trigger <= 0m)
        {
            return new PlaceOrderResult { Success = false, Error = "TriggerPrice mancante o non valido per l'ordine trigger Binance." };
        }

        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = BuildTriggerQuery(ToExchangeSymbol(request.Symbol), request.Side, isStopLoss, request.Quantity, trigger, request.ClientOrderId, ts);

        var (ok, body, uncertain, error) = await SignedAsync(HttpMethod.Post, "/fapi/v1/order", query, request.Credentials, ct, FuturesProdBase, FuturesTestnetBase);
        if (!ok)
        {
            return new PlaceOrderResult { Success = false, NetworkUncertain = uncertain, Error = error };
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new PlaceOrderResult
        {
            Success = true,
            ExchangeOrderId = root.TryGetProperty("orderId", out var oid) ? oid.GetRawText() : null,
            Status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "plan-submitted",
        };
    }

    /// <summary>
    /// Query firmabile per un ordine trigger reduce-only market (funzione pura, testabile). STOP_MARKET
    /// per lo stop-loss, TAKE_PROFIT_MARKET per il take-profit; stopPrice = prezzo di attivazione.
    /// </summary>
    internal static string BuildTriggerQuery(string market, string side, bool isStopLoss, decimal quantity, decimal stopPrice, string clientOrderId, long timestampMs)
    {
        var parts = new List<string>
        {
            $"symbol={market}",
            $"side={side}",
            $"type={(isStopLoss ? "STOP_MARKET" : "TAKE_PROFIT_MARKET")}",
            $"quantity={Inv(quantity)}",
            $"stopPrice={Inv(stopPrice)}",
            "reduceOnly=true",
            "workingType=MARK_PRICE",
        };
        if (!string.IsNullOrEmpty(clientOrderId))
        {
            parts.Add($"newClientOrderId={clientOrderId}");
        }
        parts.Add("recvWindow=5000");
        parts.Add($"timestamp={timestampMs}");
        return string.Join("&", parts);
    }

    public async Task<FuturesPosition?> GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&timestamp={ts}";
        var (ok, body, _, _) = await SignedAsync(HttpMethod.Get, "/fapi/v2/positionRisk", query, credentials, ct, FuturesProdBase, FuturesTestnetBase);
        if (!ok) return null;

        using var doc = JsonDocument.Parse(body);
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            // positionAmt: positivo = long, negativo = short, zero = flat (Binance restituisce
            // sempre la riga anche a posizione chiusa in modalita' one-way).
            var amt = ParseDecimal(p.GetProperty("positionAmt"));
            if (amt == 0m) continue;

            return new FuturesPosition
            {
                Symbol = symbol,
                Quantity = Math.Abs(amt),
                Side = amt > 0m ? "LONG" : "SHORT",
                EntryPrice = ParseDecimal(p.GetProperty("entryPrice")),
                MarkPrice = p.TryGetProperty("markPrice", out var mp) ? ParseDecimal(mp) : 0m,
                Leverage = p.TryGetProperty("leverage", out var lv) ? int.Parse(lv.GetString() ?? "1", CultureInfo.InvariantCulture) : 1,
                LiquidationPrice = p.TryGetProperty("liquidationPrice", out var lp) ? ParseDecimal(lp) : 0m,
                UnrealizedPnl = p.TryGetProperty("unRealizedProfit", out var up) ? ParseDecimal(up) : 0m,
                MarginBalance = p.TryGetProperty("isolatedMargin", out var im) ? ParseDecimal(im) : 0m,
            };
        }
        return null;
    }

    public async Task<CancelOrderResult> CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&origClientOrderId={clientOrderId}&recvWindow=5000&timestamp={ts}";
        var (ok, _, _, error) = await SignedAsync(HttpMethod.Delete, "/fapi/v1/order", query, credentials, ct, FuturesProdBase, FuturesTestnetBase);
        return new CancelOrderResult { Success = ok, Error = error };
    }

    public async Task<List<OpenOrder>> GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&recvWindow=5000&timestamp={ts}";
        var (ok, body, _, _) = await SignedAsync(HttpMethod.Get, "/fapi/v1/openOrders", query, credentials, ct, FuturesProdBase, FuturesTestnetBase);
        var list = new List<OpenOrder>();
        if (!ok) return list;

        using var doc = JsonDocument.Parse(body);
        foreach (var o in doc.RootElement.EnumerateArray())
        {
            list.Add(new OpenOrder
            {
                ExchangeOrderId = o.GetProperty("orderId").GetRawText(),
                ClientOrderId = o.TryGetProperty("clientOrderId", out var c) ? c.GetString() ?? "" : "",
                Symbol = o.GetProperty("symbol").GetString() ?? "",
                Side = o.GetProperty("side").GetString() ?? "",
                Quantity = ParseDecimal(o.GetProperty("origQty")),
                Price = ParseDecimal(o.GetProperty("price")),
                Status = o.GetProperty("status").GetString() ?? "",
            });
        }
        return list;
    }

    public async Task<OrderStatusResult> GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = default)
    {
        var market = ToExchangeSymbol(symbol);
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"symbol={market}&origClientOrderId={clientOrderId}&recvWindow=5000&timestamp={ts}";
        var (ok, body, uncertain, error) = await SignedAsync(HttpMethod.Get, "/fapi/v1/order", query, credentials, ct, FuturesProdBase, FuturesTestnetBase);
        if (!ok)
        {
            return BuildStatusLookupFailure(body, uncertain, error);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var executed = root.TryGetProperty("executedQty", out var eq) ? ParseDecimal(eq) : 0m;
        // I futures riportano avgPrice direttamente ("0" finché non eseguito).
        var avg = root.TryGetProperty("avgPrice", out var ap) ? ParseDecimalOrNull(ap) : null;

        return new OrderStatusResult
        {
            Found = true,
            Status = NormalizeBinanceOrderStatus(root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : ""),
            FilledPrice = avg is > 0m ? avg : null,
            FilledQuantity = executed > 0m ? executed : null,
            ExchangeOrderId = root.TryGetProperty("orderId", out var oid) ? oid.GetRawText() : null,
        };
    }

    public async Task<FuturesBalance> GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)
    {
        var ts = ExchangeSigning.UnixMillis(DateTime.UtcNow);
        var query = $"recvWindow=5000&timestamp={ts}";
        var (ok, body, _, _) = await SignedAsync(HttpMethod.Get, "/fapi/v2/account", query, credentials, ct, FuturesProdBase, FuturesTestnetBase);
        if (!ok) return new FuturesBalance();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new FuturesBalance
        {
            AvailableMargin = root.TryGetProperty("availableBalance", out var ab) ? ParseDecimal(ab) : 0m,
            TotalEquity = root.TryGetProperty("totalMarginBalance", out var tmb) ? ParseDecimal(tmb) : 0m,
        };
    }

    public async Task<SymbolFilters> GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)
    {
        var baseUrl = testnet ? FuturesTestnetBase : FuturesProdBase;
        var market = ToExchangeSymbol(symbol);
        var filters = new SymbolFilters();

        // A differenza dello spot, l'exchangeInfo dei futures NON supporta il filtro
        // ?symbol=... lato server: si scarica la lista intera e si filtra qui.
        using var resp = await http.GetAsync($"{baseUrl}/fapi/v1/exchangeInfo", ct);
        if (!resp.IsSuccessStatusCode) return filters;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var s in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (s.GetProperty("symbol").GetString() != market) continue;
            foreach (var f in s.GetProperty("filters").EnumerateArray())
            {
                var type = f.GetProperty("filterType").GetString();
                switch (type)
                {
                    case "LOT_SIZE":
                        filters.StepSize = ParseDecimal(f.GetProperty("stepSize"));
                        filters.MinQty = ParseDecimal(f.GetProperty("minQty"));
                        break;
                    case "PRICE_FILTER":
                        filters.TickSize = ParseDecimal(f.GetProperty("tickSize"));
                        break;
                    case "MIN_NOTIONAL":
                        // I futures Binance chiamano il campo "notional" (non "minNotional" come lo spot).
                        if (f.TryGetProperty("notional", out var n)) filters.MinNotional = ParseDecimal(n);
                        else if (f.TryGetProperty("minNotional", out var mn)) filters.MinNotional = ParseDecimal(mn);
                        break;
                }
            }
            break;
        }
        return filters;
    }

    public async Task<decimal> GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default)
    {
        var baseUrl = testnet ? FuturesTestnetBase : FuturesProdBase;
        var market = ToExchangeSymbol(symbol);
        using var resp = await http.GetAsync($"{baseUrl}/fapi/v1/premiumIndex?symbol={market}", ct);
        if (!resp.IsSuccessStatusCode) return 0m;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        // Binance riporta il funding come frazione per periodo di 8h (es. 0.0001 = 0.01%):
        // moltiplichiamo per 100 per allinearci alla convenzione "percentuale" del resto della piattaforma.
        return doc.RootElement.TryGetProperty("lastFundingRate", out var fr) ? ParseDecimal(fr) * 100m : 0m;
    }
}
