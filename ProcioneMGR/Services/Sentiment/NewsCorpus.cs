using System.Linq.Expressions;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// [I15] <b>Che cos'è «corpus patrimonio» fra le notizie</b>: la regola sola, condivisa da chi lo
/// protegge (la purge) e da chi lo sorveglia (il guardiano di profondità).
///
/// <para><b>Non è tutta la tabella.</b> Esente dalla purge è la notizia <b>con punteggio</b>: quella
/// che un <c>ISentimentScorer</c> ha già valutato, e che quindi ha un costo di produzione e un
/// valore per chi la rilegge. Le notizie senza punteggio restano potabili — sono grezze, nessun
/// consumatore le guarda, e esentare tutta la tabella la farebbe crescere senza limite per
/// conservare righe che nessuno legge.</para>
///
/// <para><b>Perché il predicato vive qui e non nei due chiamanti.</b> Se la purge e il guardiano
/// avessero due definizioni di «scorata», il guardiano misurerebbe la profondità di un insieme
/// diverso da quello protetto — e direbbe «tutto a posto» di righe che il worker sta cancellando.
/// È la classe di difetto già pagata quattro volte in questa ondata, e qui varrebbe un archivio.</para>
///
/// <para><b>Due espressioni e non una negata.</b> EF Core traduce in SQL un
/// <see cref="Expression"/>, non un delegato: <c>!Scored(a)</c> non è traducibile, e scriverlo a
/// mano nel filtro della purge ricreerebbe la seconda definizione. Le due espressioni sono
/// complementari per costruzione, e un test lo verifica su un campione che comprende il caso
/// insidioso — <b>punteggio 0 non è punteggio assente</b>.</para>
/// </summary>
public static class NewsCorpus
{
    /// <summary>La notizia è patrimonio: ha un punteggio di sentiment calcolato.</summary>
    public static Expression<Func<AltDataPoint, bool>> Scored => a => a.SentimentScore != null;

    /// <summary>La notizia è potabile: nessun punteggio, nessun consumatore.</summary>
    public static Expression<Func<AltDataPoint, bool>> NotScored => a => a.SentimentScore == null;

    /// <summary>
    /// La versione compilata, per i controlli in memoria e per il test di complementarità. Non
    /// usarla nelle query: EF la eseguirebbe lato client dopo aver caricato tutta la tabella.
    /// </summary>
    public static bool IsScored(AltDataPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.SentimentScore is not null;
    }
}
