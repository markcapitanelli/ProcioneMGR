using ProcioneMGR.Services.MarketData;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Tests;

/// <summary>
/// L'ALLARME CHE NON POTEVA RIENTRARE.
///
/// <para>La riga «Liquidazioni Binance — serie ASSENTE» in Home diceva il vero — la serie è
/// davvero vuota, perché da questa postazione la famiglia WebSocket dei derivati Binance completa
/// l'handshake e non consegna un frame — ma era chiusa da <b>tre lucchetti in serie</b>, e il terzo
/// non si apriva nemmeno risolvendo i primi due.</para>
///
/// <list type="number">
///   <item>la fonte è muta, e il worker non tenta alternative;</item>
///   <item>servono ≥ 100 punti, superabile in poche ore di accumulo vero;</item>
///   <item><b>la profondità</b>: <c>LiquidationsMinStartUtc = 2026-08-01</c>, una data assoluta già
///   passata, su un feed che <i>esiste solo al presente</i>. Nessun backfill: i due endpoint REST
///   di liquidazione sono stati ritirati e il dump storico USDS-M non è mai esistito. Quindi anche
///   nello scenario più fortunato — app spostata fuori dall'EEA, feed che riparte, cento punti
///   entro sera — il punto più vecchio sarebbe di oggi, cioè più recente dell'àncora, e la riga
///   sarebbe rimasta rossa per sempre cambiando solo messaggio.</item>
/// </list>
///
/// <para>Questi test provano il terzo lucchetto aperto: la stessa serie che la vecchia regola
/// avrebbe condannato in eterno ora <b>rientra</b>, e le tre uscite possibili (vuota, appena
/// partita, ferma) sono tutte transitorie.</para>
/// </summary>
public sealed class LiquidationsHeritageTests
{
    private sealed record FakeFeed(
        bool Enabled = true,
        bool IsConnected = false,
        long TotalMessages = 0,
        bool EndpointLikelyBlocked = false) : ILiquidationFeedDiagnostics;

    private static HeritageSeriesDepth Valuta(
        DateTime? oldest, DateTime? newest, long count,
        int minCount = 100, int staleHours = 12, string causa = "vuota", bool enforced = true)
        => SentimentHeritageGuardWorker.EvaluateAccumulating(
            "Liquidations", "Liquidazioni Binance", oldest, newest, count, minCount, staleHours, causa, enforced);

    // ----------------------------------------------------------------------------------------
    //  Il terzo lucchetto
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void UnAccumuloNATOOGGI_ESANO_NonUnaProfonditaPersa()
    {
        // IL TEST CHE CONTA. Con la vecchia regola — «la storia deve arrivare a ≤ 2026-08-01» —
        // questa identica serie sarebbe stata dichiarata «profondità persa» e lo sarebbe rimasta
        // per sempre, perché quella data non torna più.
        var inizio = DateTime.UtcNow.AddHours(-6);
        var d = Valuta(oldest: inizio, newest: DateTime.UtcNow.AddMinutes(-20), count: 240);

        Assert.False(d.Violated);
        Assert.Null(d.Problem);
        Assert.True(d.Accumulating);
    }

    [Fact]
    public void LeTreUscite_SonoTUTTETransitorie()
    {
        // Nessuna delle tre dipende da una data assoluta: ognuna si chiude con un evento che può
        // accadere. È la proprietà che la vecchia soglia non aveva.
        var vuota = Valuta(null, null, 0, causa: "accumulo mai partito: lo stream è muto");
        var appenaPartita = Valuta(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-5), 12);
        var ferma = Valuta(DateTime.UtcNow.AddDays(-9), DateTime.UtcNow.AddHours(-40), 500);

        Assert.Contains("mai partito", vuota.Problem);
        Assert.Contains("appena partito", appenaPartita.Problem);
        Assert.Contains("FERMO da 40 ore", ferma.Problem);

        // E ciascuna rientra portando la serie allo stato sano.
        Assert.False(Valuta(DateTime.UtcNow.AddHours(-6), DateTime.UtcNow.AddMinutes(-1), 240).Violated);
    }

    [Fact]
    public void SoloLaSerieVUOTA_ELaFonteMAIPARTITA()
    {
        // La distinzione che divide i due blocchi in Home. Una serie che ha punti ma è ferma NON è
        // «mai partita»: è un accumulo interrotto, e manda a fare una cosa diversa.
        Assert.True(Valuta(null, null, 0).NeverStarted);
        Assert.False(Valuta(DateTime.UtcNow.AddDays(-9), DateTime.UtcNow.AddHours(-40), 500).NeverStarted);
        Assert.False(Valuta(DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-5), 12).NeverStarted);
    }

    [Fact]
    public void UnaSerieBACKFILLABILEAZero_NonEMaiPartita_EUnaPERDITA()
    {
        // Il complemento, e il caso che questo guardiano esiste per vedere: il funding a zero righe
        // non è «una fonte da collegare», è sette anni di storia spariti — ed è ricostruibile.
        // Se NeverStarted guardasse solo il conteggio, la perdita più grave finirebbe nel blocco
        // giallo «fonti mai partite», con la conseguenza sbagliata scritta accanto.
        var funding = new HeritageSeriesDepth(
            "Funding:BTC", "Funding BTC", null, 0, "…", "serie ASSENTE: nessun punto in SentimentMetricPoints");

        Assert.True(funding.Violated);
        Assert.False(funding.NeverStarted);
        Assert.False(funding.Accumulating);
    }

    // ----------------------------------------------------------------------------------------
    //  La causa si legge, non si asserisce
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void SerieVuota_RiportaLaCausaCheGLIEDATA_NonUnaFraseFissa()
    {
        // Il messaggio non è più cablato: arriva dallo stato reale del feed. Prima diceva sempre
        // «serie ASSENTE: nessun punto in SentimentMetricPoints» — la stessa frase della perdita
        // di patrimonio del funding — e mandava a cercare un incidente che non c'era.
        var bloccata = Valuta(null, null, 0, causa: "accumulo mai partito: lo stream futures Binance non consegna frame");
        var spenta = Valuta(null, null, 0, causa: "accumulo mai partito: l'interruttore è SPENTO");

        Assert.Contains("non consegna frame", bloccata.Problem);
        Assert.Contains("interruttore", spenta.Problem);
        Assert.NotEqual(bloccata.Problem, spenta.Problem);
    }

    [Theory]
    [InlineData(false, false, false, 0, "interruttore")]
    [InlineData(true, false, true, 0, "non consegna alcun frame")]
    [InlineData(true, true, false, 1500, "connesso ma ancora senza punti")]
    [InlineData(true, false, false, 0, "non è connesso")]
    public void LaDiagnosiDelFeed_ProduceQuattroFrasiDIVERSE(
        bool enabled, bool connected, bool blocked, long messages, string atteso)
    {
        // Quattro stati che mandano a fare quattro cose diverse: accendere l'interruttore,
        // rassegnarsi al blocco (o cambiare venue), aspettare, guardare la rete. Una frase sola
        // per tutti e quattro è un messaggio che non aiuta in nessuno dei casi.
        var feed = new FakeFeed(enabled, connected, messages, blocked);
        var frase = SentimentHeritageGuardWorker.DescribeSilence(feed);

        Assert.Contains(atteso, frase, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaDiagnostica_LoDICE_InveceDiInventareUnaCausa()
    {
        var frase = SentimentHeritageGuardWorker.DescribeSilence(null);
        Assert.Contains("non interrogabile", frase, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    //  L'interruttore di sorveglianza
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void NonSorvegliata_RestaMISURATA_MaSenzaGiudizio()
    {
        var d = Valuta(null, null, 0, causa: "accumulo mai partito", enforced: false);

        Assert.False(d.Violated);
        Assert.False(d.Enforced);
        // Il punto: la misura c'è comunque. Spegnere la sorveglianza non deve produrre un OK finto.
        Assert.Equal(0, d.Count);
        Assert.Contains("accumulo vivo", d.Expected);
    }

    [Fact]
    public void LaSogliaDiSilenzio_ESotto_ILControlloSulRumore()
    {
        // Livello 2: la soglia deve TACERE su un accumulo che respira normalmente. Il feed
        // !forceOrder@arr è di eventi sparsi e i secchi sono orari: un vuoto di qualche ora è
        // fisiologico, e una soglia a 1 ora trasformerebbe la normalità in allarme continuo.
        for (var oreDiSilenzio = 0; oreDiSilenzio <= 11; oreDiSilenzio++)
        {
            var d = Valuta(DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddHours(-oreDiSilenzio), 500, staleHours: 12);
            Assert.False(d.Violated, $"un silenzio di {oreDiSilenzio} ore non deve accendere nulla con soglia 12");
        }

        Assert.True(Valuta(DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddHours(-13), 500, staleHours: 12).Violated);
    }
}
