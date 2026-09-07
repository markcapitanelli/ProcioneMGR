using ProcioneMGR.Data;
using ProcioneMGR.Services.Analysis;

namespace ProcioneMGR.Tests;

/// <summary>
/// [2026-09-07] Collaudo di <see cref="BracketRaceAnalyzer"/> — il misuratore che risponde a «perché
/// tante uscite in stop» con un numero invece che con un'opinione.
///
/// <para>I primi casi sono verificati contro un <b>riferimento indipendente</b>: serie costruite a
/// mano dove l'esito è noto prima di eseguire il codice, non contro una seconda implementazione
/// della stessa formula. Gli ultimi due difendono le due proprietà che rendono onesto il numero: che
/// le finestre senza barriera <b>contino</b> nel valore atteso, e che ciò che non si può misurare si
/// dichiari invece di uscire come zero.</para>
/// </summary>
public sealed class CorsaDelBracketTests
{
    private static OhlcvData C(int i, decimal o, decimal h, decimal l, decimal c) => new()
    {
        Symbol = "TEST/USDT",
        Timeframe = "1h",
        TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
        Open = o,
        High = h,
        Low = l,
        Close = c,
        Volume = 1_000m,
    };

    /// <summary>Serie piatta a 100, con una sola barra che si muove: tutto il resto è inerte.</summary>
    private static List<OhlcvData> Piatta(int n, decimal prezzo = 100m)
        => Enumerable.Range(0, n).Select(i => C(i, prezzo, prezzo, prezzo, prezzo)).ToList();

    /// <summary>
    /// <b>Riferimento indipendente.</b> Serie piatta a 100 tranne una barra che scende a 98,5. Con
    /// stop all'1% (99) e target al 3% (103), l'unico esito possibile per un long è lo stop; per lo
    /// short della stessa finestra non succede nulla. Il conteggio atteso è noto senza eseguire nulla.
    /// </summary>
    [Fact]
    public void UnaSolaDiscesa_ProduceEsattamenteGliStopCheLaSerieGiustifica()
    {
        var candele = Piatta(30);
        candele[20] = C(20, 100m, 100m, 98.5m, 99.5m);

        var corsa = BracketRaceAnalyzer.Run(candele, stopPercent: 1m, takePercent: 3m, horizonBars: 10);

        Assert.True(corsa.Misurabile);

        // Long: solo le entrate nelle 10 barre precedenti la discesa la vedono → indici 10..19.
        // Short: il minimo a 98,5 è FAVOREVOLE, ma il target short sta a 97 e non viene raggiunto;
        // lo stop short sta a 101 e il massimo resta 100. Quindi nessuno short tocca nulla.
        Assert.Equal(10, corsa.StopFirst);
        Assert.Equal(0, corsa.TakeFirst);
    }

    /// <summary>
    /// <b>Riferimento indipendente, il gap.</b> Se la barra APRE già sotto lo stop, il riempimento
    /// deve avvenire all'apertura e non al livello: è la regola del motore
    /// (<c>ProtectiveExitEvaluator</c>), ed è il motivo per cui questo misuratore la riusa invece di
    /// riscriverla. Con un'apertura a 95 e uno stop a 99, la perdita vera è del 5%, non dell'1%.
    /// </summary>
    [Fact]
    public void SeLaBarraApreOltreLoStop_LaPerditaEQuellaDellApertura_NonQuellaDelLivello()
    {
        // Tredici candele, non una di piu': con l'orizzonte a 1 l'ultima finestra e' quella
        // dell'indice 11, che guarda proprio la barra del gap. Una candela in piu' aprirebbe una
        // finestra che entra DENTRO il gap a 94,5 e torna a 100, aggiungendo un secondo stop e un
        // secondo target che non c'entrano con la proprieta' in esame.
        var candele = Piatta(13);
        candele[12] = C(12, 95m, 95m, 94m, 94.5m);

        var corsa = BracketRaceAnalyzer.Run(candele, stopPercent: 1m, takePercent: 3m,
            horizonBars: 1, roundTripCostPercent: 0m);

        // Long: stop a 99, apertura 95 → riempimento a 95, cioè −5%.
        // Short: target a 97, apertura 95 → riempimento a 95, cioè +5%.
        // Tutte le altre finestre sono piatte e non toccano nulla, quindi rendono 0.
        // Il valore atteso complessivo resta 0: le due si annullano. Ma se il fill fosse al LIVELLO
        // sarebbe −1% contro +3%, e la media uscirebbe positiva.
        Assert.Equal(1, corsa.StopFirst);
        Assert.Equal(1, corsa.TakeFirst);
        Assert.Equal(0m, Math.Round(corsa.ExpectedValuePercent, 6));
    }

    /// <summary>
    /// <b>La proprietà che genera tutto.</b> Su una serie senza deriva, un target tre volte più
    /// lontano dello stop viene toccato molto meno spesso: la quota di stop supera l'80%. È la
    /// lettura NOMINALE del 119:21 osservato in esercizio — il numero atteso, non un allarme.
    /// </summary>
    [Fact]
    public void UnTargetTreVolteLontano_ProduceOltreLOttantaPerCentoDiStop()
    {
        var candele = ZigZag(600);

        var simmetrico = BracketRaceAnalyzer.Run(candele, 1m, 1m, horizonBars: 10);
        var asimmetrico = BracketRaceAnalyzer.Run(candele, 1m, 3m, horizonBars: 10);

        // Simmetrico: nessuno dei due lati è favorito, quindi la quota di stop sta vicino alla metà.
        // (Non esattamente: lo stop ha la precedenza quando la stessa barra tocca entrambi.)
        Assert.InRange(simmetrico.StopSharePercent, 45m, 62m);

        // Asimmetrico 1:3, la geometria schierata sulle corsie: la quota di stop esplode.
        Assert.True(asimmetrico.StopSharePercent > 80m,
            $"con un target tre volte più lontano la quota di stop deve superare l'80%: {asimmetrico.StopSharePercent}%");
    }

    /// <summary>
    /// <b>L'onestà del valore atteso: riferimento calcolato a mano.</b> Serie piatta a 100 con una
    /// sola discesa a 98,5 (chiusura 99,5) alla barra 20, stop 1%, target 3%, orizzonte 10, costo
    /// zero. Le venti finestre danno quaranta esiti, e sono tutti enumerabili senza eseguire nulla:
    /// dieci long fermati a 99 (−1% ciascuno), uno short che chiude a 99,5 all'orizzonte (+0,5%),
    /// ventinove esiti a zero. Somma −9,5 su quaranta esiti: <b>−0,2375% per operazione</b>.
    ///
    /// <para>Lo stesso numero calcolato SOLO sulle uscite a barriera farebbe −1% esatto: quattro
    /// volte peggio. È la trappola da sopravvivenza che questo test difende — le finestre che non
    /// toccano nulla sono la maggioranza e vanno contate, altrimenti ogni bracket sembra rovinoso.</para>
    /// </summary>
    [Fact]
    public void ValoreAtteso_ContaAncheLeFinestreCheNonToccanoNulla()
    {
        var candele = Piatta(30);
        candele[20] = C(20, 100m, 100m, 98.5m, 99.5m);

        var corsa = BracketRaceAnalyzer.Run(candele, 1m, 3m, horizonBars: 10, roundTripCostPercent: 0m);

        Assert.Equal(10, corsa.StopFirst);
        Assert.Equal(0, corsa.TakeFirst);
        Assert.Equal(30, corsa.Neither);
        Assert.Equal(-0.2375m, Math.Round(corsa.ExpectedValuePercent, 6));

        // E la media sulle sole uscite a barriera: quattro volte peggiore, sulla stessa serie.
        Assert.Equal(-1m, Math.Round(10 * -1m / corsa.StopFirst, 6));
    }

    /// <summary>
    /// Ciò che non si può misurare si DICHIARA. Una gamba senza protezioni non ha una corsa da
    /// correre, e il misuratore deve dirlo invece di restituire zero stop e zero target — che si
    /// leggerebbe come «nessun problema».
    /// </summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    public void SenzaProtezioni_DichiaraCheNonSiPuoMisurare(int stop, int take)
    {
        var corsa = BracketRaceAnalyzer.Run(ZigZag(200), stop, take);

        Assert.False(corsa.Misurabile);
        Assert.False(string.IsNullOrWhiteSpace(corsa.Motivo));
        Assert.Contains("non misurabile", corsa.Racconto);
    }

    /// <summary>Serie troppo corta: stesso principio, motivo diverso e leggibile.</summary>
    [Fact]
    public void SerieTroppoCorta_DichiaraQuanteCandeleServono()
    {
        var corsa = BracketRaceAnalyzer.Run(Piatta(5), 1m, 3m, horizonBars: 10);

        Assert.False(corsa.Misurabile);
        Assert.Contains("12", corsa.Motivo);   // 10 barre di orizzonte + 2
    }

    /// <summary>
    /// <b>L'invariante della scansione.</b> Allontanare il target può solo trasformare un
    /// take-primo in uno stop-primo o in un nulla-di-fatto: non può <i>creare</i> take-primo. Quindi
    /// la quota di stop deve crescere in modo monotòno col rapporto target/stop, sempre e su
    /// qualunque serie. È la proprietà che rende leggibile la tabella delle geometrie alternative.
    ///
    /// <para>Non si asserisce invece che il valore atteso resti costante: su una serie con barre
    /// larghe rispetto al bracket i pareggi di barra sono frequenti, il motore assegna sempre lo
    /// stop, e la geometria sposta la media in modo reale. Quella banda si misura sulle serie vere
    /// della piattaforma, non si postula qui.</para>
    /// </summary>
    [Fact]
    public void LaScansione_MostraCheAllontanareIlTargetSpostaSoloLaFormaDellEsito()
    {
        var candele = ZigZag(600);
        var corse = BracketRaceAnalyzer.Scan(candele, 1m, BracketRaceAnalyzer.DefaultMultipliers,
            horizonBars: 10, roundTripCostPercent: 0m);

        var utili = corse.Where(c => c.Misurabile).ToList();
        Assert.Equal(BracketRaceAnalyzer.DefaultMultipliers.Count, utili.Count);

        for (var i = 1; i < utili.Count; i++)
        {
            Assert.True(utili[i].StopSharePercent >= utili[i - 1].StopSharePercent,
                $"la quota di stop deve crescere col rapporto: {utili[i - 1].RewardToRisk} -> "
                + $"{utili[i - 1].StopSharePercent}%, {utili[i].RewardToRisk} -> {utili[i].StopSharePercent}%");
            Assert.True(utili[i].TakeFirst <= utili[i - 1].TakeFirst,
                "allontanare il target non puo' aumentare il numero di target raggiunti");
        }

        // E il salto deve essere grande: e' la FORMA dell'esito che il bracket governa.
        Assert.True(utili[^1].StopSharePercent - utili[0].StopSharePercent > 30m,
            $"la quota di stop deve dipendere fortemente dalla geometria: "
            + $"{utili[0].StopSharePercent}%..{utili[^1].StopSharePercent}%");
    }

    /// <summary>
    /// I pareggi di barra sono DICHIARATI. Dove la barra tocca entrambi i livelli il motore assegna
    /// lo stop, ed è la scelta giusta perché dentro la barra l'ordine dei tocchi non si osserva. Ma
    /// e' anche il punto in cui la misura diventa sistematicamente pessimista, e chi legge il valore
    /// atteso deve poterlo sapere invece di scoprirlo per caso.
    /// </summary>
    [Fact]
    public void IPareggiDiBarra_SonoContatiEDetti()
    {
        var candele = Piatta(30);
        // Una barra che scende sotto lo stop del long E sale sopra il suo target: ambigua per costruzione.
        candele[20] = C(20, 100m, 104m, 98.5m, 100m);

        var corsa = BracketRaceAnalyzer.Run(candele, 1m, 3m, horizonBars: 10, roundTripCostPercent: 0m);

        Assert.True(corsa.AmbiguousBars > 0, "la barra costruita apposta deve risultare ambigua");
        Assert.True(corsa.AmbiguousSharePercent > 0m);
        Assert.Contains("anche il target", corsa.Racconto);
    }

    /// <summary>
    /// Serie deterministica senza deriva: zig-zag ad ampiezza variabile. Nessun <c>Random</c> — un
    /// test che dà un numero diverso a ogni corsa non è un test.
    /// </summary>
    private static List<OhlcvData> ZigZag(int n)
    {
        var candele = new List<OhlcvData>(n);
        var prezzo = 100m;
        for (var i = 0; i < n; i++)
        {
            var ampiezza = 0.4m + (i % 7) * 0.15m + (i % 13) * 0.05m;
            var passo = i % 2 == 0 ? ampiezza : -ampiezza;
            var open = prezzo;
            var close = prezzo + passo;
            candele.Add(C(i, open,
                Math.Max(open, close) + ampiezza * 0.6m,
                Math.Min(open, close) - ampiezza * 0.6m,
                close));
            prezzo = close;
        }
        return candele;
    }
}
