using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Tests;

/// <summary>
/// Verifica la firma HMAC della richiesta. Il valore atteso è stato calcolato in modo
/// INDIPENDENTE (HMACSHA256 di sistema, via PowerShell) sullo stesso (secret, query) usato
/// dall'esempio Binance: pinna l'output esatto della funzione di firma. Se la firma è
/// sbagliata, ogni ordine reale verrebbe rifiutato dall'exchange.
/// </summary>
public class ExchangeSigningTests
{
    [Fact]
    public void Binance_HmacSha256Hex_MatchesIndependentComputation()
    {
        const string secret = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0";
        const string query = "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1&recvWindow=5000&timestamp=1499827319559";
        // Calcolato indipendentemente con System.Security.Cryptography.HMACSHA256.
        const string expected = "b89008e7051ffbf2242be7dc5ae67fd146e6430688627b802c0cbec146e46aef";

        Assert.Equal(expected, ExchangeSigning.HmacSha256Hex(query, secret));
    }

    [Fact]
    public void Hex_IsLowercase64Chars()
    {
        var sig = ExchangeSigning.HmacSha256Hex("anything", "secret");
        Assert.Equal(64, sig.Length);
        Assert.Equal(sig, sig.ToLowerInvariant());
    }

    [Fact]
    public void Bitget_HmacSha256Base64_IsDeterministicAndValidBase64()
    {
        const string secret = "mysecret";
        var prehash = "1700000000000GET/api/v2/spot/account/assets";
        var a = ExchangeSigning.HmacSha256Base64(prehash, secret);
        var b = ExchangeSigning.HmacSha256Base64(prehash, secret);
        Assert.Equal(a, b);                            // deterministico
        Assert.Equal(44, a.Length);                    // SHA256 (32 byte) -> 44 char base64
        _ = Convert.FromBase64String(a);               // base64 valido
    }

    [Fact]
    public void DifferentSecret_ProducesDifferentSignature()
    {
        var m = "symbol=BTCUSDT&timestamp=1";
        Assert.NotEqual(ExchangeSigning.HmacSha256Hex(m, "s1"), ExchangeSigning.HmacSha256Hex(m, "s2"));
    }
}
