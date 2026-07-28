using ProcioneMGR.Services.Ingestion;

namespace ProcioneMGR.Tests;

/// <summary>
/// [B2] Il gate B2 chiedeva «7 giorni senza buchi nelle candele» e nessuno dei due strumenti che
/// dovevano misurarlo sapeva vedere una serie che ha smesso di avanzare: lo stato di sync scriveva
/// <c>OK: 1 candele</c> perché il cursore incrementale ri-chiedeva l'ultima candela nota e
/// l'exchange gliela ridava, e l'audit di copertura misurava la densità della serie sul proprio
/// intervallo — dove una serie ferma è densa al 100%.
///
/// Il test che conta davvero è <see cref="Il_caso_MKR_una_serie_ferma_da_mesi_che_si_dichiarava_sana"/>:
/// riproduce la situazione reale trovata a database il 2026-07-28, che è la ragione per cui questa
/// regola esiste.
/// </summary>
public sealed class SeriesFreshnessTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 14, 55, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ il caso reale

    /// <summary>
    /// MKR/USDT 1h: ultima candela il 2025-09-15 02:00, sync che riportava "OK: 1 candele" a ogni
    /// giro. Sono più di settemila barre di ritardo, e devono essere dichiarate.
    /// </summary>
    [Fact]
    public void Il_caso_MKR_una_serie_ferma_da_mesi_che_si_dichiarava_sana()
    {
        var last = new DateTime(2025, 9, 15, 2, 0, 0, DateTimeKind.Utc);

        Assert.True(SeriesFreshness.IsStale("1h", last, Now));
        Assert.True(SeriesFreshness.BarsBehind("1h", last, Now) > 7_000);

        var stato = SeriesFreshness.Describe("1h", last, Now, candlesProcessed: 1);
        Assert.StartsWith("FERMA", stato);
        Assert.DoesNotContain("OK", stato);
    }

    /// <summary>
    /// TON/USDT 1d, l'altro caso trovato: ferma dal 2026-06-30, 28 giorni indietro. Più recente di
    /// MKR e per questo più insidioso — a occhio, in una tabella, "30 giugno" non grida.
    /// </summary>
    [Fact]
    public void Il_caso_TON_ferma_da_meno_ma_ferma()
    {
        var last = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(27, SeriesFreshness.BarsBehind("1d", last, Now));
        Assert.True(SeriesFreshness.IsStale("1d", last, Now));
    }

    // ------------------------------------------------------------------ il caso sano

    /// <summary>
    /// Le cinque serie vive lette a database il 2026-07-28 alle 14:55 UTC devono risultare tutte a
    /// ritardo ZERO. Il caso 5m è quello che ha fatto scegliere il riferimento: alle 14:55 la barra
    /// delle 14:50 è l'ultima chiusa e la serie ne ha esattamente quella. Se il riferimento fosse
    /// la barra in formazione, la stessa serie sana oscillerebbe fra 0 e 1 a seconda che il ciclo
    /// di sync sia passato o no — un allarme che lampeggia da solo è rumore, cioè il modo migliore
    /// per non farlo leggere (stessa lezione del watchdog di staleness del feed).
    /// </summary>
    [Theory]
    [InlineData("5m", "2026-07-28T14:50:00Z")]
    [InlineData("15m", "2026-07-28T14:45:00Z")]
    [InlineData("1h", "2026-07-28T14:00:00Z")]
    [InlineData("4h", "2026-07-28T12:00:00Z")]
    [InlineData("1d", "2026-07-28T00:00:00Z")]
    public void Le_serie_vive_di_oggi_non_sono_ferme(string tf, string lastIso)
    {
        var last = DateTime.Parse(lastIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal);

        Assert.Equal(0, SeriesFreshness.BarsBehind(tf, last, Now));
        Assert.False(SeriesFreshness.IsStale(tf, last, Now));
        Assert.StartsWith("OK", SeriesFreshness.Describe(tf, last, Now, candlesProcessed: 2));
    }

    /// <summary>
    /// La soglia è dove dichiarato e non un passo prima o dopo: a tolleranza 3, tre barre indietro
    /// passano e quattro no. Una soglia che scivola di uno è una soglia che nessuno può ragionare.
    /// Le candele sono su confini di barra veri (alle 14:55 l'ultima chiusa è quella delle 13:00).
    /// </summary>
    [Fact]
    public void La_tolleranza_e_esattamente_dove_e_scritta()
    {
        var tre = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);      // 13:00 − 3h
        var quattro = new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);   // 13:00 − 4h

        Assert.Equal(3, SeriesFreshness.BarsBehind("1h", tre, Now));
        Assert.False(SeriesFreshness.IsStale("1h", tre, Now, toleranceBars: 3));

        Assert.Equal(4, SeriesFreshness.BarsBehind("1h", quattro, Now));
        Assert.True(SeriesFreshness.IsStale("1h", quattro, Now, toleranceBars: 3));
    }

    // ------------------------------------------------------------------ i casi che non sono numeri

    /// <summary>
    /// Serie vuota e timeframe ignoto NON sono "aggiornata". È la trappola classica di questo tipo
    /// di controllo: un null che finisce in un confronto numerico si comporta da zero, e zero è
    /// esattamente il valore che significa "fresca".
    /// </summary>
    [Fact]
    public void Vuota_o_di_timeframe_ignoto_non_significa_fresca()
    {
        Assert.Null(SeriesFreshness.BarsBehind("1h", null, Now));
        Assert.True(SeriesFreshness.IsStale("1h", null, Now));
        Assert.Equal("FERMA: nessuna candela", SeriesFreshness.Describe("1h", null, Now, 0));

        Assert.Null(SeriesFreshness.BarsBehind("7m", Now, Now));
        Assert.True(SeriesFreshness.IsStale("7m", Now, Now));
        Assert.Contains("non riconosciuto", SeriesFreshness.Describe("7m", Now, Now, 1));
    }

    /// <summary>
    /// Una candela nel futuro (orologio dell'exchange avanti, o barra in formazione datata avanti)
    /// non deve produrre un ritardo negativo che poi si confronta con la tolleranza: si satura a
    /// zero, cioè "fresca".
    /// </summary>
    [Fact]
    public void Una_candela_nel_futuro_non_produce_ritardo_negativo()
    {
        Assert.Equal(0, SeriesFreshness.BarsBehind("1h", Now.AddHours(5), Now));
        Assert.False(SeriesFreshness.IsStale("1h", Now.AddHours(5), Now));
    }

    /// <summary>
    /// Il numero di candele processate NON entra nel verdetto: è precisamente il numero che diceva
    /// "OK" mentre la serie era morta. Anche processandone mille, se la serie non è avanzata resta
    /// ferma.
    /// </summary>
    [Fact]
    public void Il_conteggio_delle_candele_processate_non_puo_salvare_una_serie_ferma()
    {
        var last = new DateTime(2025, 9, 15, 2, 0, 0, DateTimeKind.Utc);
        Assert.StartsWith("FERMA", SeriesFreshness.Describe("1h", last, Now, candlesProcessed: 1000));
    }
}
