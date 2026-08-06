using ProcioneMGR.Services.Exchanges;

namespace ProcioneMGR.Services.Ingestion;

/// <summary>
/// [B2] Regola UNICA per dire se una serie è aggiornata o FERMA.
///
/// Nasce da una cecità del gate B2, trovata il 2026-07-28 guardando il database invece dei
/// documenti. Il gate chiedeva «7 giorni senza buchi nelle candele», ma nessuno dei due strumenti
/// che dovevano misurarlo sapeva vedere una serie che ha smesso di avanzare:
///
///  - lo stato di sync scriveva <c>OK: N candele</c> guardando quante righe erano state
///    processate. Su una serie ferma il cursore incrementale ri-chiede l'ultima candela nota,
///    l'exchange la restituisce, e l'upsert la riscrive: <c>OK: 1 candele</c> a ogni giro, per
///    sempre. MKR/USDT lo diceva da dieci mesi;
///  - l'audit di copertura misurava le candele presenti sull'intervallo [prima, ultima] della
///    serie STESSA. Una serie che si ferma ha copertura 100% del proprio passato: per costruzione
///    non poteva accorgersene.
///
/// Qui la freschezza si misura contro ADESSO, che è l'unico riferimento che non si sposta insieme
/// al guasto. Regola sola e condivisa dai due chiamanti — due regole darebbero due verdetti sulla
/// stessa serie, che è il difetto già visto e corretto in D2.
/// </summary>
public static class SeriesFreshness
{
    /// <summary>
    /// Quante barre di ritardo si tollerano prima di chiamare ferma una serie. Tre, non zero:
    /// l'exchange pubblica con un ritardo suo, il ciclo di sync gira ogni 5 minuti e la barra in
    /// formazione non è un buco. Sotto questa soglia il silenzio è normale; sopra è un guasto.
    /// </summary>
    public const int DefaultToleranceBars = 3;

    /// <summary>
    /// [2026-08-06] L'istante di APERTURA dell'ultima barra che ha già CHIUSO. <c>null</c> se il
    /// timeframe non è riconosciuto.
    ///
    /// <para>Sta qui, accanto a <see cref="BarsBehind"/>, perché è la stessa nozione vista dall'altro
    /// lato: là serve a misurare il ritardo, qui a decidere cosa si può consumare. Due definizioni
    /// separate di «ultima barra chiusa» darebbero due verdetti sulla stessa serie, che è
    /// esattamente il difetto corretto in D2 e nel Filone E.</para>
    ///
    /// <para><b>Il guasto che ha reso necessaria la versione pubblica</b>, trovato dal proprietario
    /// il 2026-08-06: la barra in formazione È a database — l'ingestione REST scrive anche l'ultima
    /// kline incompleta — e chi legge «fino ad adesso» se la prende. Sul motore di trading questo
    /// significava valutare stop e target su un High/Low parziale: uno short ETC/USDT con target a
    /// 6,3786 non si è chiuso benché il minimo VERO della barra 4h delle 08:00 fosse 6,31, perché
    /// quella barra era stata valutata poco dopo le 08:00 e la versione definitiva veniva poi
    /// scartata come «già vista».</para>
    /// </summary>
    public static DateTime? LastClosedBarOpenUtc(string timeframe, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(timeframe)
            || !Timeframes.Supported.TryGetValue(timeframe, out var step)
            || step <= TimeSpan.Zero)
        {
            return null;
        }

        var currentOpen = new DateTime(nowUtc.Ticks - (nowUtc.Ticks % step.Ticks), DateTimeKind.Utc);
        return currentOpen - step;
    }

    /// <summary>
    /// Quante barre CHIUSE mancano all'appello. <c>null</c> se il timeframe non è riconosciuto o la
    /// serie è vuota: due casi che NON sono "aggiornata" e non devono poter essere scambiati per
    /// tale da un confronto numerico.
    ///
    /// Il riferimento è l'ultima barra CHIUSA, non quella in formazione. La differenza sembra un
    /// dettaglio e non lo è: la barra in formazione a database c'è solo se il ciclo di sync è
    /// passato mentre era aperta, quindi una serie perfettamente sana la ha o non la ha a seconda
    /// del momento in cui la si guarda. Misurare contro di essa farebbe oscillare il ritardo fra 0
    /// e 1 senza che nulla sia successo. L'ultima barra chiusa, invece, una serie viva deve averla
    /// sempre — ed è per questo l'unico riferimento su cui si può mettere una soglia.
    /// </summary>
    public static int? BarsBehind(string timeframe, DateTime? lastCandleUtc, DateTime nowUtc)
    {
        if (lastCandleUtc is not DateTime last) return null;
        if (!Timeframes.Supported.TryGetValue(timeframe, out var step) || step <= TimeSpan.Zero) return null;

        var lastClosedOpen = LastClosedBarOpenUtc(timeframe, nowUtc)!.Value;

        var behind = (lastClosedOpen - DateTime.SpecifyKind(last, DateTimeKind.Utc)).Ticks / step.Ticks;
        return behind <= 0 ? 0 : (int)behind;
    }

    /// <summary>Ferma = più indietro della tolleranza, oppure vuota, oppure di timeframe ignoto.</summary>
    public static bool IsStale(string timeframe, DateTime? lastCandleUtc, DateTime nowUtc,
        int toleranceBars = DefaultToleranceBars) =>
        BarsBehind(timeframe, lastCandleUtc, nowUtc) is not int behind || behind > Math.Max(0, toleranceBars);

    /// <summary>
    /// Stato leggibile per la UI e per i log. Il conteggio delle candele processate resta, ma NON
    /// è più da solo a decidere l'esito: era esattamente il numero che diceva "OK" mentre la serie
    /// era morta.
    /// </summary>
    public static string Describe(string timeframe, DateTime? lastCandleUtc, DateTime nowUtc,
        long candlesProcessed, int toleranceBars = DefaultToleranceBars)
    {
        if (lastCandleUtc is null)
        {
            return "FERMA: nessuna candela";
        }

        var behind = BarsBehind(timeframe, lastCandleUtc, nowUtc);
        if (behind is null)
        {
            return $"Timeframe '{timeframe}' non riconosciuto: freschezza non verificabile";
        }

        return behind > Math.Max(0, toleranceBars)
            ? $"FERMA: ultima {lastCandleUtc:yyyy-MM-dd HH:mm}, {behind} barre indietro"
            : $"OK: {candlesProcessed} candele";
    }
}
