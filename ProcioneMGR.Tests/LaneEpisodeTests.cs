using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-08-06] Episodi di corsia.
///
/// <para>Il problema che risolvono, misurato sul database vero: la corsia 0 aveva <b>610 ordini di
/// 7 strategie diverse su 3 simboli</b> in un elenco unico, e <c>Order.StrategyId</c> è un GUID che
/// non corrisponde a nulla in <c>SavedStrategies</c> — lo storico era orfano. I confini però
/// c'erano già: 11 voci di <c>StartEngine</c> nel registro di audit, mai usate.</para>
///
/// <para>Il contratto che questi test difendono: gli episodi separano correttamente gli
/// esperimenti, e <b>ciò che è dedotto non viene mai spacciato per dichiarato</b>.</para>
/// </summary>
public class LaneEpisodeTests
{
    private static TradingAuditLog Start(DateTime quando, string? payload = null) => new()
    {
        LaneId = 1,
        Action = "StartEngine",
        TimestampUtc = quando,
        Details = payload ?? """{"mode":"Paper","capital":10000,"strategies":1}""",
        Mode = TradingMode.Paper,
    };

    private static TradingAuditLog StartNuovo(DateTime quando, string symbol, string tf, params string[] strategie) =>
        Start(quando, $$"""
            {"mode":"Paper","capital":10000,"strategies":{{strategie.Length}},
             "symbol":"{{symbol}}","timeframe":"{{tf}}",
             "strategyNames":[{{string.Join(",", strategie.Select(s => $"\"{s}\""))}}]}
            """);

    private static Order Ordine(DateTime quando, string symbol) => new()
    {
        LaneId = 1, Symbol = symbol, CreatedAtUtc = quando,
        Side = OrderSide.Buy, Type = OrderType.Market, Quantity = 1m, Status = OrderStatus.Filled,
    };

    private static readonly DateTime T0 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ separazione

    [Fact]
    public void SenzaAvvii_NessunEpisodio()
        => Assert.Empty(LaneEpisodeBuilder.Build([], [Ordine(T0, "BTC/USDT")]));

    [Fact]
    public void OgniAvvioApreUnEpisodio_EIlPrecedenteSiChiude()
    {
        var ep = LaneEpisodeBuilder.Build(
            [Start(T0), Start(T0.AddDays(10)), Start(T0.AddDays(20))],
            []);

        Assert.Equal(3, ep.Count);
        // Il più recente per primo: è quello che si guarda.
        Assert.Equal(3, ep[0].Index);
        Assert.True(ep[0].IsCurrent);
        Assert.Equal(T0.AddDays(20), ep[0].StartedAtUtc);
        // Gli altri chiusi dall'avvio successivo.
        Assert.Equal(T0.AddDays(20), ep[1].EndedAtUtc);
        Assert.Equal(T0.AddDays(10), ep[2].EndedAtUtc);
        Assert.False(ep[1].IsCurrent);
    }

    /// <summary>Il cuore: ogni ordine finisce nell'episodio in cui è stato creato, e in nessun altro.</summary>
    [Fact]
    public void GliOrdiniFinisconoNellEpisodioGiusto()
    {
        var ep = LaneEpisodeBuilder.Build(
            [Start(T0), Start(T0.AddDays(10))],
            [
                Ordine(T0.AddDays(1), "BTC/USDT"),
                Ordine(T0.AddDays(2), "BTC/USDT"),
                Ordine(T0.AddDays(11), "DOT/USDT"),
            ]);

        var recente = ep[0];
        var vecchio = ep[1];
        Assert.Equal(1, recente.OrderCount);
        Assert.Equal(2, vecchio.OrderCount);
    }

    /// <summary>Un ordine esattamente sul confine appartiene all'episodio che COMINCIA, non a quello che finisce.</summary>
    [Fact]
    public void OrdineSulConfine_VaAllEpisodioNuovo()
    {
        var confine = T0.AddDays(10);
        var ep = LaneEpisodeBuilder.Build([Start(T0), Start(confine)], [Ordine(confine, "DOT/USDT")]);

        Assert.Equal(1, ep[0].OrderCount);   // il nuovo
        Assert.Equal(0, ep[1].OrderCount);   // il vecchio
    }

    [Fact]
    public void OrdiniPrimaDelPrimoAvvio_NonEntranoInNessunEpisodio()
    {
        var ep = LaneEpisodeBuilder.Build([Start(T0)], [Ordine(T0.AddDays(-1), "BTC/USDT")]);

        Assert.Equal(0, ep[0].OrderCount);
    }

    // ------------------------------------------------------------------ dichiarato vs dedotto

    /// <summary>
    /// LA PROPRIETÀ CHE CONTA: un episodio col payload nuovo dice ciò che il motore ha
    /// DICHIARATO — simbolo, timeframe e nomi delle strategie.
    /// </summary>
    [Fact]
    public void PayloadNuovo_EDichiarato()
    {
        var ep = LaneEpisodeBuilder.Build([StartNuovo(T0, "DOT/USDT", "15m", "Composite")], []);

        Assert.Equal(LaneEpisodeSource.Declared, ep[0].Source);
        Assert.Equal("DOT/USDT", ep[0].Symbol);
        Assert.Equal("15m", ep[0].Timeframe);
        Assert.Equal("Composite", Assert.Single(ep[0].StrategyNames));
        Assert.Equal("Composite DOT/USDT 15m", ep[0].Title);
    }

    /// <summary>
    /// E il rovescio: col payload VECCHIO il simbolo si deduce dagli ordini, ma la fonte lo
    /// dichiara e le strategie NON si inventano.
    /// </summary>
    [Fact]
    public void PayloadVecchio_SimboloDedotto_StrategieMaiInventate()
    {
        var ep = LaneEpisodeBuilder.Build([Start(T0)], [Ordine(T0.AddDays(1), "SHIB/USDT")]);

        Assert.Equal(LaneEpisodeSource.InferredFromOrders, ep[0].Source);
        Assert.Equal("SHIB/USDT", ep[0].Symbol);
        Assert.Empty(ep[0].StrategyNames);
        Assert.Contains("strategia non registrata", ep[0].Title);
    }

    [Fact]
    public void PayloadVecchioSenzaOrdini_SiDichiaraIgnoto()
    {
        var ep = LaneEpisodeBuilder.Build([Start(T0)], []);

        Assert.Equal(LaneEpisodeSource.Unknown, ep[0].Source);
        Assert.Equal(string.Empty, ep[0].Symbol);
        Assert.Contains("simbolo ignoto", ep[0].Title);
    }

    [Fact]
    public void PayloadIllegibile_DegradaSenzaPerdereIlConfine()
    {
        var ep = LaneEpisodeBuilder.Build(
            [Start(T0, "{ non è json")],
            [Ordine(T0.AddDays(1), "ETH/USDT")]);

        Assert.Single(ep);
        Assert.Equal(LaneEpisodeSource.InferredFromOrders, ep[0].Source);
        Assert.Equal("ETH/USDT", ep[0].Symbol);
    }

    /// <summary>Più simboli nello stesso episodio (non dovrebbe capitare): vince il più frequente, in modo deterministico.</summary>
    [Fact]
    public void SimboliMisti_VinceIlPiuFrequente_EInModoStabile()
    {
        var ordini = new[]
        {
            Ordine(T0.AddDays(1), "AAA/USDT"),
            Ordine(T0.AddDays(2), "BBB/USDT"),
            Ordine(T0.AddDays(3), "BBB/USDT"),
        };

        var a = LaneEpisodeBuilder.Build([Start(T0)], ordini);
        var b = LaneEpisodeBuilder.Build([Start(T0)], ordini.Reverse().ToArray());

        Assert.Equal("BBB/USDT", a[0].Symbol);
        Assert.Equal(a[0].Symbol, b[0].Symbol);   // stesso insieme, stessa risposta
    }

    /// <summary>
    /// Il ponte verso <c>/strategies</c>. Serve perché <c>Order.StrategyId</c> NON lo è: è un GUID
    /// di sessione che su 4 campioni veri corrispondeva a 0 righe di <c>SavedStrategies</c>. L'id
    /// salvato viaggia adesso nell'avvio, l'unico punto in cui il motore lo conosce davvero.
    /// </summary>
    [Fact]
    public void EpisodioDichiarato_PortaGliIdDelleStrategieSalvate()
    {
        var ep = LaneEpisodeBuilder.Build(
            [Start(T0, """
                {"mode":"Paper","symbol":"DOT/USDT","timeframe":"15m",
                 "strategyNames":["Composite","Carry"],"savedStrategyIds":[41,57]}
                """)],
            []);

        Assert.Equal([41, 57], ep[0].SavedStrategyIds);
    }

    /// <summary>E sugli episodi vecchi resta vuoto: nessun id inventato per riempire la colonna.</summary>
    [Fact]
    public void EpisodioDedotto_NonHaIdSalvati()
        => Assert.Empty(LaneEpisodeBuilder.Build([Start(T0)], [Ordine(T0.AddDays(1), "SHIB/USDT")])[0].SavedStrategyIds);

    // ------------------------------------------------------------------ ordini orfani

    /// <summary>
    /// Il caso vero della corsia 0: 474 ordini su 500 sono anteriori al primo <c>StartEngine</c>
    /// annotato, perché il registro comincia il 30/06 e gli ordini il 1º giugno. Devono restare
    /// FUORI da ogni episodio — attribuirli al più vecchio sarebbe una bugia comoda.
    /// </summary>
    [Fact]
    public void OrdiniAnterioriAlPrimoAvvio_SonoOrfani_ENonEntranoInNessunEpisodio()
    {
        var episodi = LaneEpisodeBuilder.Build([Start(T0), Start(T0.AddDays(10))], []);
        var ordini = new[]
        {
            Ordine(T0.AddDays(-5), "BTC/USDT"),   // orfano
            Ordine(T0.AddDays(-1), "BTC/USDT"),   // orfano
            Ordine(T0.AddDays(1), "DOT/USDT"),    // dentro il primo episodio
        };

        var orfani = LaneEpisodeBuilder.OrphanOrders(episodi, ordini);

        Assert.Equal(2, orfani.Count);
        Assert.All(orfani, o => Assert.Equal("BTC/USDT", o.Symbol));
    }

    /// <summary>Un ordine ESATTAMENTE sull'avvio non è orfano: appartiene all'episodio che comincia.</summary>
    [Fact]
    public void OrdineSullIstanteDelPrimoAvvio_NonEOrfano()
        => Assert.Empty(LaneEpisodeBuilder.OrphanOrders(
            LaneEpisodeBuilder.Build([Start(T0)], []), [Ordine(T0, "BTC/USDT")]));

    /// <summary>
    /// Senza episodi non esiste un "prima": nessun ordine è orfano, e la tabella li mostra piatti
    /// come faceva prima. Serve perché il conto degli orfani non va mai in confusione col totale.
    /// </summary>
    [Fact]
    public void SenzaEpisodi_NessunOrfano()
        => Assert.Empty(LaneEpisodeBuilder.OrphanOrders([], [Ordine(T0, "BTC/USDT")]));

    // ------------------------------------------------------------------ il caso reale

    /// <summary>
    /// Ricostruzione del caso che ha motivato tutto: una corsia con tre vite su tre simboli
    /// diversi, come la corsia 2 vera (SHIB, poi SUI, poi XLM). Prima erano 13 ordini in un
    /// elenco unico; adesso sono tre episodi con dentro i loro.
    /// </summary>
    [Fact]
    public void TreViteSuTreSimboli_DiventanoTreEpisodiSeparati()
    {
        var ep = LaneEpisodeBuilder.Build(
            [Start(T0), Start(T0.AddDays(12)), StartNuovo(T0.AddDays(40), "XLM/USDT", "1h", "RegimeConditional")],
            [
                Ordine(T0.AddDays(1), "SHIB/USDT"),
                Ordine(T0.AddDays(3), "SHIB/USDT"),
                Ordine(T0.AddDays(20), "SUI/USDT"),
            ]);

        Assert.Equal(3, ep.Count);
        // Il corrente: dichiarato, nessun ordine ancora.
        Assert.Equal("XLM/USDT", ep[0].Symbol);
        Assert.Equal(LaneEpisodeSource.Declared, ep[0].Source);
        Assert.Equal(0, ep[0].OrderCount);
        // I due precedenti: dedotti, ognuno col suo simbolo e i suoi ordini.
        Assert.Equal("SUI/USDT", ep[1].Symbol);
        Assert.Equal(1, ep[1].OrderCount);
        Assert.Equal("SHIB/USDT", ep[2].Symbol);
        Assert.Equal(2, ep[2].OrderCount);
        Assert.All(ep.Skip(1), e => Assert.Equal(LaneEpisodeSource.InferredFromOrders, e.Source));
    }
}
