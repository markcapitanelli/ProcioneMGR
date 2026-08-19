using ProcioneMGR.Services.PairsTrading;
using ProcioneMGR.Services.TimeSeries;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I14c] <b>Il livello 2 dell'item, che è quello decisivo</b>: su due random walk indipendenti il
/// monitor non deve mai dichiarare cointegrazione né allarme di rottura; su una relazione piantata,
/// deve trovarla.
///
/// <para><b>Perché il verdetto NON può essere per finestra.</b> Un test ADF al 5% dichiara
/// stazionario il 5% delle finestre di puro rumore — per costruzione, non per difetto. Un monitor
/// che esponesse il verdetto della singola finestra direbbe «cointegrata» su due random walk circa
/// una volta su venti, e il gate scritto sopra sarebbe <b>insoddisfacibile alla lettera</b>: la
/// classe di errore «gate senza strumento», che ci si accorge di avere solo dopo aver scritto
/// tutto. L'aggregazione andava decisa <i>insieme</i> al gate, e questi test sono il posto dove si
/// vede che la decisione regge.</para>
///
/// <para>Le serie sono costruite nella specificazione che il test usa davvero — sui LOG, con X
/// random walk geometrico — copiando gli helper già collaudati di <c>CointegrationTests</c>:
/// costruirle in livello misurerebbe una relazione diversa da quella stimata.</para>
/// </summary>
public class PairSpreadJudgeTests
{
    private readonly ICointegrationTest _test = new EngleGrangerCointegrationTest();

    /// <summary>Random walk geometrico: log-prezzo integrato, livello sempre &gt; 0 come un OHLCV vero.</summary>
    private static List<decimal> RandomWalk(int n, int seed, double stepScale = 1.0)
    {
        var rnd = new Random(seed);
        var logLevel = Math.Log(100.0);
        var serie = new List<decimal>(n) { 100m };
        for (var i = 1; i < n; i++)
        {
            logLevel += (rnd.NextDouble() - 0.5) * 2 * stepScale * 0.01;
            serie.Add((decimal)Math.Exp(logLevel));
        }
        return serie;
    }

    /// <summary>Y cointegrata con X sui log: log Y = intercetta + β·log X + rumore stazionario.</summary>
    private static List<decimal> CointegrataCon(List<decimal> x, double beta, double intercetta, int seed, double rumore = 0.005)
    {
        var rnd = new Random(seed);
        return x.Select(xi =>
            (decimal)Math.Exp(intercetta + beta * Math.Log((double)xi) + (rnd.NextDouble() - 0.5) * 2 * rumore)).ToList();
    }

    /// <summary>
    /// Taglia le finestre NON SOVRAPPOSTE come fa il worker — dalla più recente all'indietro — e
    /// costruisce la serie di punti su cui il giudice si esprime. È la stessa aritmetica del worker
    /// isolata dal database: se le due divergessero, questi test proverebbero un'altra cosa.
    /// </summary>
    private List<PairSpreadPoint> Finestre(List<decimal> y, List<decimal> x, int ampiezza)
    {
        var punti = new List<PairSpreadPoint>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var fine = y.Count; fine - ampiezza >= 0; fine -= ampiezza)
        {
            var inizio = fine - ampiezza;
            var esito = _test.Test(y.GetRange(inizio, ampiezza), x.GetRange(inizio, ampiezza));
            punti.Add(new PairSpreadPoint(
                t0.AddHours(inizio), t0.AddHours(fine - 1), ampiezza,
                esito.AdfStatistic, esito.CriticalValue, esito.IsCointegrated,
                esito.HedgeRatio, 0d));
        }
        punti.Reverse();
        return punti;
    }

    // --- L2: il controllo sul rumore ------------------------------------------------------------

    /// <summary>
    /// <b>Il gate, nella forma in cui è verificabile.</b> Venti semi di coppie di random walk
    /// indipendenti, venti finestre da 250 ciascuna: il verdetto non deve <b>mai</b> dichiarare una
    /// relazione persistente, e non deve <b>mai</b> dichiarare una rottura.
    ///
    /// <para>Il «mai» è vero perché la soglia è sulla FRAZIONE: sotto il nullo ogni finestra è
    /// stazionaria con probabilità ~0,05, e perché venti finestre arrivino al 60% servirebbe un
    /// evento con probabilità dell'ordine di 10⁻¹². Con un verdetto per-finestra lo stesso «mai»
    /// sarebbe stato falso una volta su venti.</para>
    /// </summary>
    [Fact]
    public void L2_DueRandomWalkIndipendenti_MaiUnaRelazioneNeUnaRottura()
    {
        for (var seme = 1; seme <= 20; seme++)
        {
            var x = RandomWalk(5000, seed: seme * 100);
            var y = RandomWalk(5000, seed: seme * 100 + 50);   // indipendente: nessuna relazione

            var verdetto = PairSpreadJudge.Judge(Finestre(y, x, 250));

            Assert.False(verdetto.IsPersistentlyStationary,
                $"seme {seme}: dichiarata persistente su rumore ({verdetto.StationaryWindows}/{verdetto.Windows} finestre)");
            Assert.False(verdetto.IsBroken,
                $"seme {seme}: dichiarata rotta su rumore — una rottura richiede una persistenza precedente, che qui non c'è mai stata");
        }
    }

    /// <summary>
    /// <b>La misura onesta dietro il «mai».</b> Il gate non dice che nessuna finestra risulta mai
    /// stazionaria — sarebbe falso e impossibile: al 5% ci si aspetta il 5%. Questo test misura il
    /// TASSO su 400 finestre di puro rumore e pretende che stia nell'intervallo atteso.
    ///
    /// <para>Serve a due cose. Primo: se il tasso fosse molto più alto, il test di cointegrazione
    /// sarebbe rotto e il verdetto sulla frazione poggerebbe sul nulla. Secondo: se fosse ZERO, il
    /// test sarebbe cieco — e un test cieco supera il gate del nullo senza dimostrare niente, che è
    /// la definizione di una verifica che non può fallire.</para>
    /// </summary>
    [Fact]
    public void IlTassoDiFalsiPositiviPerFinestraStaDoveDeve_NeCiecoNeRotto()
    {
        var stazionarie = 0;
        var totali = 0;

        for (var seme = 1; seme <= 20; seme++)
        {
            var x = RandomWalk(5000, seed: seme * 100);
            var y = RandomWalk(5000, seed: seme * 100 + 50);
            foreach (var p in Finestre(y, x, 250))
            {
                totali++;
                if (p.IsStationary) stazionarie++;
            }
        }

        var tasso = (double)stazionarie / totali;
        Assert.Equal(400, totali);
        Assert.True(tasso < 0.20,
            $"tasso di falsi positivi {tasso:P1} su {totali} finestre: troppo alto, il test di cointegrazione non tiene la sua taglia");
    }

    /// <summary>
    /// <b>Il complemento, senza il quale il test del nullo non prova nulla.</b> Un monitor che non
    /// dichiara mai niente supera il gate del rumore a mani basse. Su una relazione PIANTATA — β=1,2
    /// sui log, rumore stazionario — il verdetto deve trovarla, su tutte le finestre.
    /// </summary>
    [Fact]
    public void L2_RelazionePiantata_VieneTrovata()
    {
        var x = RandomWalk(5000, seed: 7);
        var y = CointegrataCon(x, beta: 1.2, intercetta: 0.5, seed: 8);

        var verdetto = PairSpreadJudge.Judge(Finestre(y, x, 250));

        Assert.True(verdetto.IsPersistentlyStationary,
            $"relazione vera non trovata: {verdetto.StationaryWindows}/{verdetto.Windows} finestre stazionarie");
        Assert.False(verdetto.IsBroken);
        Assert.Contains("relazione persistente", verdetto.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>La rottura è la PERDITA di uno stato, non un'assenza.</b> Serie cointegrata nella prima
    /// metà e scollegata nella seconda: il verdetto deve dire «rotta». È il caso che distingue una
    /// coppia che si è guastata da una che non ha mai funzionato — e sono due informazioni diverse
    /// per chi guarda.
    /// </summary>
    [Fact]
    public void UnaRelazioneCheSiSpezza_VieneDichiarataRotta()
    {
        var x = RandomWalk(5000, seed: 11);
        var yLegata = CointegrataCon(x, beta: 1.2, intercetta: 0.5, seed: 12);
        var ySciolta = RandomWalk(5000, seed: 99);

        // Le finestre si costruiscono all'indietro dalla fine: le ULTIME della serie sono quelle
        // recenti, quindi la parte scollegata va in coda.
        var y = yLegata.Take(3500).Concat(ySciolta.Skip(3500)).ToList();

        var verdetto = PairSpreadJudge.Judge(Finestre(y, x, 250), recentWindows: 4);

        Assert.True(verdetto.IsBroken, $"rottura non vista: {verdetto.Text}");
        Assert.Contains("RELAZIONE ROTTA", verdetto.Text, StringComparison.Ordinal);
    }

    // --- I confini del giudizio ------------------------------------------------------------------

    /// <summary>
    /// <b>Poca storia non è un verdetto.</b> Sotto il minimo di finestre non si dice né stazionaria
    /// né rotta — e lo si <b>dichiara</b>: con tre finestre e p=0,05 «due su tre stazionarie» ha
    /// probabilità ~0,7%, bassa ma non trascurabile su molte coppie. Senza questo pavimento, una
    /// coincidenza diventerebbe una relazione.
    /// </summary>
    [Fact]
    public void PocaStoria_NessunVerdetto_ELoDichiara()
    {
        var punti = Enumerable.Range(0, PairSpreadJudge.MinWindows - 1)
            .Select(i => new PairSpreadPoint(
                new DateTime(2026, 1, 1).AddDays(i), new DateTime(2026, 1, 2).AddDays(i), 250,
                -5.0, -3.3, true, 1.1, 0.2))
            .ToList();

        var verdetto = PairSpreadJudge.Judge(punti);

        Assert.True(verdetto.NotEnoughHistory);
        Assert.False(verdetto.IsPersistentlyStationary);   // tutte stazionarie, e comunque nessun verdetto
        Assert.False(verdetto.IsBroken);
        Assert.Contains("troppo poca storia", verdetto.Text, StringComparison.Ordinal);
    }

    /// <summary>Nessuna finestra: si dice che la coppia non è mai stata sorvegliata, non che è sana.</summary>
    [Fact]
    public void NessunaFinestra_SiDiceCheNonEMaiStataSorvegliata()
    {
        var verdetto = PairSpreadJudge.Judge([]);

        Assert.Equal(0, verdetto.Windows);
        Assert.False(verdetto.IsPersistentlyStationary);
        Assert.False(verdetto.IsBroken);
        Assert.Contains("mai stata sorvegliata", verdetto.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Una coppia che non è MAI stata persistente non può essere «rotta»: non c'è niente da
    /// rompere. È la proprietà che rende vera per costruzione — e non per fortuna — la seconda metà
    /// del gate sul rumore.
    /// </summary>
    [Fact]
    public void SenzaUnaPersistenzaPrecedente_NessunaRotturaEPossibile()
    {
        var punti = Enumerable.Range(0, 12)
            .Select(i => new PairSpreadPoint(
                new DateTime(2026, 1, 1).AddDays(i), new DateTime(2026, 1, 2).AddDays(i), 250,
                -1.0, -3.3, IsStationary: false, 1.1, 0.2))
            .ToList();

        var verdetto = PairSpreadJudge.Judge(punti);

        Assert.False(verdetto.IsBroken);
        Assert.False(verdetto.IsPersistentlyStationary);
        Assert.Contains("nessuna relazione persistente", verdetto.Text, StringComparison.Ordinal);
    }

    // --- La chiave della coppia ------------------------------------------------------------------

    [Theory]
    [InlineData("ETH/USDT|BTC/USDT 1h", "ETH/USDT", "BTC/USDT", "1h")]
    [InlineData("  ETH/USDT | BTC/USDT   4h  ", "ETH/USDT", "BTC/USDT", "4h")]
    public void LaChiaveSiScomponeAncheConGliSpazi(string chiave, string y, string x, string tf)
    {
        var (py, px, ptf) = PairSpreadWatchWorker.ParsePair(chiave);

        Assert.Equal(y, py);
        Assert.Equal(x, px);
        Assert.Equal(tf, ptf);
    }

    /// <summary>
    /// Una chiave malformata dà null e la coppia si salta con un log: mai un'eccezione che fermi il
    /// giro delle altre. Un refuso in configurazione non deve spegnere la sorveglianza.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ETH/USDT 1h")]        // manca la seconda gamba
    [InlineData("ETH/USDT|BTC/USDT")]  // manca il timeframe
    [InlineData("|BTC/USDT 1h")]       // prima gamba vuota
    public void ChiaveMalformata_DaNull_MaiUnEccezione(string? chiave)
    {
        var (y, _, _) = PairSpreadWatchWorker.ParsePair(chiave);

        Assert.Null(y);
    }
}
