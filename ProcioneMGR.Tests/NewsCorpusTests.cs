using ProcioneMGR.Data;
using ProcioneMGR.Services.Sentiment;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I15] <b>Il giudice unico del corpus-patrimonio</b>: la regola che dice quale notizia la purge
/// deve risparmiare e quale il guardiano deve contare.
///
/// <para>La ragione per cui vive in un posto solo è la lezione del funding, pagata due volte:
/// l'esenzione dalla purge protegge dal worker, non da un restore sbagliato o da un drop — quindi
/// serve anche un guardiano. Ma se il guardiano misurasse la profondità di un insieme <i>diverso</i>
/// da quello protetto, direbbe «tutto a posto» di righe che il worker sta cancellando: sarebbe una
/// protezione che non si vede fallire, cioè peggio di nessuna protezione.</para>
/// </summary>
public class NewsCorpusTests
{
    private static AltDataPoint News(decimal? score) => new()
    {
        TimestampUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        Source = "TestFeed", Title = "t", DedupeKey = "k", SentimentScore = score,
    };

    /// <summary>
    /// <b>Il caso che conta davvero: punteggio ZERO non è punteggio ASSENTE.</b> Zero è un giudizio
    /// («questa notizia è neutra»), null è la sua mancanza. Una lettura sbagliata del predicato —
    /// falsy invece di null, l'errore più naturale del mondo — cancellerebbe proprio le notizie
    /// neutre, che sono la maggioranza di qualunque corpus.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(0.0, true)]
    [InlineData(-1.0, true)]
    [InlineData(0.42, true)]
    public void LoZeroEUnPunteggio_IlNullNo(double? score, bool patrimonio)
    {
        var punto = News(score is null ? null : (decimal)score.Value);

        Assert.Equal(patrimonio, NewsCorpus.IsScored(punto));
        Assert.Equal(patrimonio, NewsCorpus.Scored.Compile()(punto));
        Assert.Equal(!patrimonio, NewsCorpus.NotScored.Compile()(punto));
    }

    /// <summary>
    /// <b>Le due espressioni sono complementari, e devono restare tali.</b> Sono due perché EF Core
    /// traduce in SQL un albero di espressioni, non un delegato: <c>!Scored(a)</c> non è
    /// traducibile. Ma due espressioni scritte a mano possono divergere, e il giorno che divergono
    /// esiste una notizia che né la purge cancella né il guardiano conta — un buco che nessuna delle
    /// due superfici può vedere da sola.
    /// </summary>
    [Fact]
    public void LeDueEspressioniCopronoTuttiICasi_SenzaSovrapporsi()
    {
        var scored = NewsCorpus.Scored.Compile();
        var notScored = NewsCorpus.NotScored.Compile();

        decimal?[] campione = [null, 0m, 0.0001m, -0.0001m, 1m, -1m, 0.5m, decimal.MinValue, decimal.MaxValue];

        foreach (var s in campione)
        {
            var punto = News(s);
            Assert.True(scored(punto) ^ notScored(punto),
                $"punteggio {s?.ToString() ?? "null"}: le due espressioni devono essere esattamente complementari");
        }
    }

    /// <summary>
    /// Il predicato compilato e quello in memoria dicono la stessa cosa: <c>IsScored</c> esiste per
    /// i controlli fuori dalle query, e una terza definizione mascherata da comodità sarebbe
    /// esattamente il difetto che questa classe evita.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(0.7)]
    public void LaVersioneInMemoriaCoincideConLEspressione(double? score)
    {
        var punto = News(score is null ? null : (decimal)score.Value);

        Assert.Equal(NewsCorpus.Scored.Compile()(punto), NewsCorpus.IsScored(punto));
    }
}
