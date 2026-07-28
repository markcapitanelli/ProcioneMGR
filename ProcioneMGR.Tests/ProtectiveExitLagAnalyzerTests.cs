using ProcioneMGR.Data;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Tests;

/// <summary>
/// [B3] Il gate B3 chiede il confronto tick-vs-candela, ma in assetto osservativo i tick vengono
/// scartati e la serie <c>source=tick</c> non può esistere: il confronto che deve autorizzare
/// l'accensione richiedeva l'accensione. <see cref="ProtectiveExitLagAnalyzer"/> chiude la domanda
/// offline usando le candele fini come surrogato dei tick.
///
/// Una misura del genere è pericolosa proprio perché è facile che dia il risultato che si spera:
/// basta sbagliare di un passo il momento in cui un percorso "scopre" l'uscita e il feed sembra
/// anticipare di un'intera barra senza aver fatto nulla. Da qui il primo test, che è il controllo
/// della misura e non una sua applicazione: con risoluzione fine UGUALE a quella di corsia
/// l'anticipo deve essere ESATTAMENTE zero. Se lo strumento non sa dire "nessun vantaggio" quando
/// non ce n'è, nessuno dei numeri successivi vale niente.
/// </summary>
public sealed class ProtectiveExitLagAnalyzerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OhlcvData Bar(string tf, DateTime t, decimal o, decimal h, decimal l, decimal c) =>
        new() { Symbol = "TEST/USDT", Timeframe = tf, TimestampUtc = t, Open = o, High = h, Low = l, Close = c, Volume = 1m };

    /// <summary>
    /// Serie di corsia piatta a 100, così ogni ingresso parte dallo stesso prezzo e i livelli sono
    /// a mente: stop al 2% = 98.
    /// </summary>
    private static List<OhlcvData> FlatLane(int bars, string tf = "1h")
    {
        var step = ProtectiveExitLagAnalyzer.Step(tf);
        return Enumerable.Range(0, bars)
            .Select(i => Bar(tf, T0 + step * i, 100m, 100m, 100m, 100m))
            .ToList();
    }

    /// <summary>Le barre fini che coprono una barra di corsia, tutte identiche a essa (nessuna informazione in più).</summary>
    private static List<OhlcvData> FineFromLane(IEnumerable<OhlcvData> lane, string laneTf, string fineTf)
    {
        var laneStep = ProtectiveExitLagAnalyzer.Step(laneTf);
        var fineStep = ProtectiveExitLagAnalyzer.Step(fineTf);
        var n = (int)(laneStep.Ticks / fineStep.Ticks);

        var fine = new List<OhlcvData>();
        foreach (var b in lane)
        {
            for (var k = 0; k < n; k++)
            {
                fine.Add(Bar(fineTf, b.TimestampUtc + fineStep * k, b.Open, b.High, b.Low, b.Close));
            }
        }
        return fine;
    }

    // ------------------------------------------------------------------ controllo della misura

    /// <summary>
    /// CONTROLLO. Stessa risoluzione sui due percorsi ⇒ la scoperta avviene nello stesso istante,
    /// quindi anticipo zero su OGNI posizione. Non è un dettaglio di arrotondamento: è la prova che
    /// il confronto misura la differenza di risoluzione e non uno sfasamento introdotto da me.
    /// </summary>
    [Fact]
    public void Stessa_risoluzione_nessun_anticipo()
    {
        var lane = FlatLane(40);
        lane[20] = Bar("1h", lane[20].TimestampUtc, 100m, 100m, 97m, 99m);  // tocca 97 < stop 98

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, lane, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "1h",
            StopLossPercent = 2m,
            MaxHoldBars = 10,
        });

        Assert.True(report.BothExited > 0, "lo scenario deve produrre uscite su entrambi i percorsi");
        Assert.All(report.Observations.Where(o => o.CandleExited && o.FineExited),
            o => Assert.Equal(0d, o.LeadSeconds));
        Assert.Equal(0d, report.MedianLeadSeconds);
        Assert.Equal(0d, report.P90LeadSeconds);
    }

    /// <summary>
    /// CONTROLLO, seconda faccia. Barre fini che non aggiungono NIENTE (replicano la barra di
    /// corsia) non devono produrre anticipo: la finezza del campionamento non è di per sé un
    /// vantaggio, lo è solo l'informazione che porta.
    /// </summary>
    [Fact]
    public void Barre_fini_senza_informazione_nessun_anticipo()
    {
        var lane = FlatLane(40);
        lane[20] = Bar("1h", lane[20].TimestampUtc, 100m, 100m, 97m, 99m);
        var fine = FineFromLane(lane, "1h", "5m");

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "5m",
            StopLossPercent = 2m,
            MaxHoldBars = 10,
        });

        // Ogni barra fine replica l'intera escursione della barra di corsia, quindi la PRIMA barra
        // fine di quella di corsia fa già scattare lo stop: l'anticipo è di 55 minuti, non di zero.
        // Il numero è esatto e verificabile a mano — ed è il massimo ottenibile, perché qui il
        // tocco è finto e sta all'inizio. Serve a fissare la scala prima degli scenari realistici.
        Assert.All(report.Observations.Where(o => o.CandleExited && o.FineExited),
            o => Assert.Equal(55 * 60d, o.LeadSeconds));
    }

    // ------------------------------------------------------------------ anticipo piantato

    /// <summary>
    /// ANTICIPO PIANTATO. Dentro la barra di corsia il prezzo scende a 97 nel primo quarto d'ora e
    /// poi risale a 99,5 alla chiusura. Il percorso a candela se ne accorge solo alla chiusura
    /// (60 minuti dopo l'apertura della barra); il percorso a 5m alla chiusura della barra fine che
    /// contiene il tocco. Entrambi i numeri sono calcolabili a mano.
    /// </summary>
    [Fact]
    public void Anticipo_piantato_ritrovato_col_valore_esatto()
    {
        var lane = FlatLane(40);
        var hit = 20;
        lane[hit] = Bar("1h", lane[hit].TimestampUtc, 100m, 100m, 97m, 99.5m);

        // 12 barre da 5m: il tocco sta nella terza (minuti 10-15), il resto risale.
        var fine = new List<OhlcvData>();
        foreach (var b in lane)
        {
            for (var k = 0; k < 12; k++)
            {
                var t = b.TimestampUtc + TimeSpan.FromMinutes(5 * k);
                if (b.TimestampUtc == lane[hit].TimestampUtc && k == 2)
                {
                    fine.Add(Bar("5m", t, 100m, 100m, 97m, 97.5m));
                }
                else
                {
                    fine.Add(Bar("5m", t, 99.5m, 99.6m, 99.4m, 99.5m));
                }
            }
        }

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "5m",
            StopLossPercent = 2m,   // stop a 98
            MaxHoldBars = 5,
        });

        var o = report.Observations.First(x => x.CandleExited && x.FineExited);

        // La barra fine col tocco chiude al minuto 15; quella di corsia al minuto 60 ⇒ 45 minuti.
        Assert.Equal(45 * 60d, o.LeadSeconds);

        // Prezzo ottenibile: 98 (livello, sul percorso fine) contro 99,5 (chiusura della barra di
        // corsia). Per un LONG uscire a 99,5 è MEGLIO: il ritardo qui ha pagato, e la misura deve
        // dirlo col segno negativo invece di raccontare un vantaggio che non c'è.
        Assert.Equal(-150d, o.DelayCostBps, 6);   // (98 - 99,5)/100 * 10000
        Assert.True(report.AdverseShare > 0.9d, "in questo scenario il ritardo conviene quasi sempre");
    }

    /// <summary>
    /// Lo scenario opposto, che è quello per cui il feed esiste: il prezzo rompe lo stop e
    /// CONTINUA a scendere fino alla chiusura della barra. Uscire al tocco salva la differenza.
    /// </summary>
    [Fact]
    public void Rottura_che_prosegue_il_ritardo_costa()
    {
        var lane = FlatLane(40);
        var hit = 20;
        lane[hit] = Bar("1h", lane[hit].TimestampUtc, 100m, 100m, 94m, 94m);

        var fine = new List<OhlcvData>();
        foreach (var b in lane)
        {
            for (var k = 0; k < 12; k++)
            {
                var t = b.TimestampUtc + TimeSpan.FromMinutes(5 * k);
                if (b.TimestampUtc == lane[hit].TimestampUtc)
                {
                    // discesa progressiva da 100 a 94 lungo l'ora
                    var lvl = 100m - 0.5m * k;
                    fine.Add(Bar("5m", t, lvl, lvl, lvl - 0.5m, lvl - 0.5m));
                }
                else
                {
                    fine.Add(Bar("5m", t, 100m, 100m, 100m, 100m));
                }
            }
        }

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "5m",
            StopLossPercent = 2m,   // stop a 98
            MaxHoldBars = 5,
        });

        var o = report.Observations.First(x => x.CandleExited && x.FineExited);

        // La discesa parte da 100 e ogni barra fine perde mezzo punto: la k=3 (min 15-20) apre a
        // 98,5 e segna 98 di minimo, che è esattamente il livello. Chiude al minuto 20 ⇒ 40 minuti
        // prima della chiusura della barra di corsia.
        Assert.Equal(40 * 60d, o.LeadSeconds);
        // 98 contro 94 alla chiusura della barra di corsia ⇒ 400 bps salvati.
        Assert.Equal(400d, o.DelayCostBps, 6);
        Assert.True(report.MedianDelayCostBps > 0d);
    }

    // ------------------------------------------------------------------ ottimismo della contabilità

    /// <summary>
    /// Il motore, sul percorso a candela, REGISTRA il fill al livello dello stop mentre a barra
    /// chiusa il prezzo davvero ottenibile è la chiusura. In Paper la differenza è finzione
    /// contabile; in Live sarebbe denaro. La misura la espone anche senza scomodare il feed.
    /// </summary>
    [Fact]
    public void Ottimismo_del_fill_a_candela_misurato()
    {
        var lane = FlatLane(40);
        lane[20] = Bar("1h", lane[20].TimestampUtc, 100m, 100m, 94m, 94m);

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, lane, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "1h",
            StopLossPercent = 2m,
            MaxHoldBars = 5,
        });

        var o = report.Observations.First(x => x.CandleExited);
        Assert.Equal(400d, o.CandleFillOptimismBps, 6);   // registra 98, ottiene 94
        Assert.True(report.MedianCandleFillOptimismBps > 0d);
    }

    // ------------------------------------------------------------------ simmetria e causalità

    /// <summary>Lo short è il long allo specchio: stesso anticipo, stesso costo col segno giusto.</summary>
    [Fact]
    public void Short_simmetrico_al_long()
    {
        var lane = FlatLane(40);
        var hit = 20;
        lane[hit] = Bar("1h", lane[hit].TimestampUtc, 100m, 106m, 100m, 106m);

        var fine = new List<OhlcvData>();
        foreach (var b in lane)
        {
            for (var k = 0; k < 12; k++)
            {
                var t = b.TimestampUtc + TimeSpan.FromMinutes(5 * k);
                if (b.TimestampUtc == lane[hit].TimestampUtc)
                {
                    var lvl = 100m + 0.5m * k;
                    fine.Add(Bar("5m", t, lvl, lvl + 0.5m, lvl, lvl + 0.5m));
                }
                else
                {
                    fine.Add(Bar("5m", t, 100m, 100m, 100m, 100m));
                }
            }
        }

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "5m",
            StopLossPercent = 2m,   // short: stop a 102
            Side = OrderSide.Sell,
            MaxHoldBars = 5,
        });

        var o = report.Observations.First(x => x.CandleExited && x.FineExited);
        Assert.Equal(40 * 60d, o.LeadSeconds);   // barra fine k=3, massimo 102 = livello
        Assert.Equal(400d, o.DelayCostBps, 6);   // esce a 102 invece che a 106
    }

    /// <summary>
    /// CAUSALITÀ. Le barre fini che aprono PRIMA dell'ingresso non devono essere guardate:
    /// conterrebbero prezzi che la posizione non ha vissuto, e farebbero uscire una posizione su un
    /// passato altrui. Qui il crollo sta subito prima dell'ingresso: nessuno dei due percorsi deve
    /// uscire.
    /// </summary>
    [Fact]
    public void Prezzi_precedenti_allingresso_non_fanno_uscire()
    {
        var lane = FlatLane(10);
        var fine = FineFromLane(lane, "1h", "5m");

        // Crollo nelle barre fini che precedono la chiusura della barra di corsia 0 (= l'ingresso
        // della prima posizione simulata).
        for (var k = 0; k < 12; k++)
        {
            fine[k] = Bar("5m", fine[k].TimestampUtc, 90m, 90m, 90m, 90m);
        }

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "5m",
            StopLossPercent = 2m,
            MaxHoldBars = 3,
        });

        var first = report.Observations[0];
        Assert.False(first.FineExited);
        Assert.False(first.CandleExited);
    }

    /// <summary>
    /// TRAILING. Il best-since-entry è la sola memoria che le due simulazioni condividerebbero se
    /// riusassero la stessa posizione: contaminarlo farebbe muovere lo stop del percorso a candela
    /// col ritmo dei tick, cioè darebbe al feed proprio il potere che l'assetto osservativo gli
    /// nega. Qui il prezzo sale dentro la barra e poi ritraccia: il trailing fine ratcheta più in
    /// alto e fa scattare lo stop, quello a candela no.
    /// </summary>
    [Fact]
    public void Il_trailing_dei_due_percorsi_non_si_contamina()
    {
        var lane = FlatLane(10);
        lane[1] = Bar("1h", lane[1].TimestampUtc, 100m, 110m, 99m, 99m);

        var fine = new List<OhlcvData>();
        foreach (var b in lane)
        {
            for (var k = 0; k < 12; k++)
            {
                var t = b.TimestampUtc + TimeSpan.FromMinutes(5 * k);
                if (b.TimestampUtc == lane[1].TimestampUtc)
                {
                    // sale a 110 nella prima metà, ritraccia a 99 nella seconda
                    var lvl = k < 6 ? 100m + 2m * k : 110m - 2m * (k - 5);
                    fine.Add(Bar("5m", t, lvl, lvl, lvl, lvl));
                }
                else
                {
                    fine.Add(Bar("5m", t, 100m, 100m, 100m, 100m));
                }
            }
        }

        var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "5m",
            StopLossPercent = 20m,          // stop fisso lontanissimo: decide solo il trailing
            TrailingStopPercent = 5m,
            MaxHoldBars = 2,
        });

        var o = report.Observations[0];
        Assert.True(o.FineExited && o.CandleExited);

        // Percorso fine: il best sale barra dopo barra fino a 110, il livello di trailing lo segue
        // a 104,5 e il ritracciamento lo tocca alla barra k=8, cioè al minuto 45 della prima ora.
        //
        // Percorso a candela: sulla barra che contiene tutto il movimento il best vale ancora 100
        // (causalità: il trailing guarda le barre PRECEDENTI), quindi il livello è 95 e il minimo
        // 99 non lo tocca; il best diventa 110 solo a fine barra e lo stop scatta sulla barra dopo,
        // alla terza ora.
        //
        // I 4.500 secondi sono la firma della SEPARAZIONE dei due stati: se le due simulazioni
        // condividessero il best-since-entry, il percorso a candela avrebbe già avuto 110 durante
        // la prima ora e sarebbe uscito alla sua chiusura — anticipo di 900 secondi, non 4.500.
        // Questo test fallirebbe rumorosamente proprio nel caso in cui il feed acquistasse potere
        // dalla porta di servizio.
        Assert.Equal(4500d, o.LeadSeconds);
        Assert.Equal(400d, o.DelayCostBps, 6);   // 104 al tocco contro 100 alla chiusura
    }

    // ------------------------------------------------------------------ controllo sul rumore

    /// <summary>
    /// CONTROLLO SUL RUMORE, ed è il test che decide se ai numeri su dati veri si può credere.
    ///
    /// Su una passeggiata aleatoria senza deriva il costo del ritardo deve essere ZERO in media:
    /// per il teorema d'arresto opzionale, sapendo che il prezzo ha toccato il livello dentro la
    /// barra, il valore atteso della chiusura di quella barra è il livello stesso. Se invece questa
    /// misura restituisse un segno anche sul rumore, allora il segno misurato sulle serie vere
    /// sarebbe una firma della mia costruzione e non una proprietà del mercato — e la conclusione
    /// «il feed farebbe uscire peggio» sarebbe un artefatto.
    ///
    /// Le barre di corsia sono qui l'aggregazione ESATTA di quelle fini, come nella realtà: nessuna
    /// informazione compare o sparisce fra i due percorsi, cambia solo la risoluzione.
    /// </summary>
    [Fact]
    public void Su_rumore_puro_il_ritardo_non_costa_ne_rende()
    {
        var costs = new List<double>();

        for (var seed = 0; seed < 12; seed++)
        {
            var rnd = new Random(seed);
            var fine = new List<OhlcvData>();
            var price = 100m;

            const int laneBars = 400;
            const int perLane = 12;   // 5m dentro 1h

            for (var i = 0; i < laneBars * perLane; i++)
            {
                var open = price;
                // passo simmetrico: nessuna deriva, nessun ritorno alla media
                var stepPct = (decimal)((rnd.NextDouble() - 0.5) * 0.006);
                var close = open * (1m + stepPct);
                var high = Math.Max(open, close) * 1.0005m;
                var low = Math.Min(open, close) * 0.9995m;
                fine.Add(Bar("5m", T0 + TimeSpan.FromMinutes(5 * i), open, high, low, close));
                price = close;
            }

            // Aggregazione esatta a barre di corsia.
            var lane = new List<OhlcvData>();
            for (var i = 0; i < laneBars; i++)
            {
                var slice = fine.GetRange(i * perLane, perLane);
                lane.Add(Bar("1h", T0 + TimeSpan.FromHours(i),
                    slice[0].Open, slice.Max(b => b.High), slice.Min(b => b.Low), slice[^1].Close));
            }

            var report = new ProtectiveExitLagAnalyzer().Measure(lane, fine, new ProtectiveExitLagRequest
            {
                Symbol = "NOISE/USDT",
                LaneTimeframe = "1h",
                FineTimeframe = "5m",
                StopLossPercent = 1.5m,
                MaxHoldBars = 48,
                SampleEveryNBars = 3,
            });

            Assert.True(report.BothExited > 30, $"seme {seed}: campione troppo piccolo per concludere ({report.BothExited})");
            costs.Add(report.MeanDelayCostBps);
        }

        // Media sui semi: la dispersione fra semi è l'incertezza, e lo zero deve starci dentro.
        var mean = costs.Average();
        var sd = Math.Sqrt(costs.Sum(c => (c - mean) * (c - mean)) / (costs.Count - 1));
        var stdErr = sd / Math.Sqrt(costs.Count);

        Assert.True(Math.Abs(mean) < 3d * stdErr,
            $"sul rumore il costo del ritardo dovrebbe essere zero, invece vale {mean:F2} bps (errore standard {stdErr:F2}): la misura ha un segno suo.");
    }

    // ------------------------------------------------------------------ guardie

    [Fact]
    public void Risoluzione_fine_piu_grossa_della_corsia_e_rifiutata()
    {
        var lane = FlatLane(10, "15m");
        var ex = Assert.Throws<ArgumentException>(() => new ProtectiveExitLagAnalyzer().Measure(
            lane, lane, new ProtectiveExitLagRequest
            {
                Symbol = "TEST/USDT",
                LaneTimeframe = "15m",
                FineTimeframe = "1h",
                StopLossPercent = 2m,
            }));
        Assert.Contains("surrogato", ex.Message);
    }

    [Fact]
    public void Timeframe_sconosciuto_e_rifiutato_invece_di_ripiegare()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtectiveExitLagAnalyzer.Step("7m"));
    }

    [Fact]
    public void Senza_stop_non_ce_uscita_protettiva_da_misurare()
    {
        var lane = FlatLane(10);
        Assert.Throws<ArgumentException>(() => new ProtectiveExitLagAnalyzer().Measure(
            lane, lane, new ProtectiveExitLagRequest
            {
                Symbol = "TEST/USDT",
                LaneTimeframe = "1h",
                FineTimeframe = "1h",
                StopLossPercent = 0m,
            }));
    }

    /// <summary>
    /// Serie senza alcun movimento: nessuna uscita da nessuna parte, e il rapporto deve dirlo
    /// invece di produrre mediane calcolate su zero campioni che sembrano misure.
    /// </summary>
    [Fact]
    public void Serie_piatta_nessuna_uscita_e_lo_dichiara()
    {
        var lane = FlatLane(30);
        var report = new ProtectiveExitLagAnalyzer().Measure(lane, lane, new ProtectiveExitLagRequest
        {
            Symbol = "TEST/USDT",
            LaneTimeframe = "1h",
            FineTimeframe = "1h",
            StopLossPercent = 2m,
            MaxHoldBars = 5,
        });

        Assert.Equal(0, report.BothExited);
        Assert.Equal(report.PositionsSimulated, report.NeitherExited);
        Assert.Equal(0d, report.MedianDelayCostBps);
    }
}
