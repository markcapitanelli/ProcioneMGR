using ProcioneMGR.Services.Pipeline;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K54b, 2026-09-02] <b>Uno spreco è resa bassa MOLTIPLICATA per costo reale.</b>
///
/// <para><b>Il fatto che l'ha imposto.</b> La prima versione di K50 dichiarava «sterile» la
/// configurazione 8 sulla sola resa (0,06 chiavi grigie per run contro una mediana di 0,41).
/// Misurato il 2026-09-02: la config 8 è ferma dal <b>20 agosto</b> e consuma <b>0 ore su 48,7</b>
/// al mese. Metterla in sonno non libera niente — non era una decisione, era un'etichetta.</para>
///
/// <para>E il costo vero sta altrove: la configurazione <b>19</b> ha una mediana di <b>43,7 minuti a
/// run</b> — undici volte la config 17, che sta a 3,8 — e non ha schierato una sola gamba.</para>
///
/// <para><b>Il nullo di questa suite</b> è che il costo non deve diventare l'unico criterio: una
/// caccia cara che rende non è uno spreco, e condannarla sarebbe l'errore simmetrico.</para>
/// </summary>
public class ResaECostoK54bTests
{
    // I numeri veri delle cinque configurazioni, finestra dei 30 giorni al 2026-09-02.
    private static readonly (int Cfg, int Run, int Chiavi, double Ore, double MinMediani)[] Reali =
    [
        (17, 62, 82, 4.49, 3.8),
        (18, 63, 26, 21.88, 15.5),
        (20, 15, 13, 2.32, 8.7),
        (19, 16, 5, 14.09, 43.7),
        (8, 17, 1, 0.0, 7.4),     // ferma dal 20 agosto: zero ore nella finestra
    ];

    private static List<HuntYieldRow> Giudizio() => HuntYield.Judge(Reali);

    /// <summary>
    /// <b>Il caso che ha imposto la correzione.</b> La config 8 non deve più essere «sterile»:
    /// non consuma niente, quindi non c'è niente da liberare.
    /// </summary>
    [Fact]
    public void UNAcacciaFERMA_nonEsterile_eDORMIENTE()
    {
        var r = Giudizio().Single(x => x.ConfigurationId == 8);

        Assert.Equal(HuntYieldVerdict.Dormiente, r.Verdict);
        Assert.Contains("consuma troppo poco", HuntYield.Describe(r, 0.41), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il nullo.</b> Il costo non deve diventare l'unico criterio: la config 18 è la più cara di
    /// tutte (21,88 ore) e rende in linea con la mediana — non è uno spreco, ed etichettarla
    /// sarebbe l'errore simmetrico di quello appena corretto.
    /// </summary>
    [Fact]
    public void ILNULLO_unaCACCIAcaraCHErende_nonEunstreco()
    {
        var r = Giudizio().Single(x => x.ConfigurationId == 18);

        Assert.Equal(21.88, r.HoursSpent, 2);          // la più cara
        Assert.Equal(HuntYieldVerdict.Produttiva, r.Verdict);
    }

    /// <summary>
    /// La resa per ORA è la lettura che regge il confronto: il numero di run al denominatore è una
    /// scelta di pianificazione, non una proprietà della caccia. Misurato: stessa config, stesso
    /// motore, stesso universo, resa da 0,477 a 7,250 col tasso di grigi PIATTO — un fattore 15
    /// dal nulla.
    /// </summary>
    [Fact]
    public void LaRESAperORA_riordinaIlCONFRONTO()
    {
        var g = Giudizio().ToDictionary(x => x.ConfigurationId);

        // Per run la 20 (0,87) sta sopra la 18 (0,41). Per ORA il divario si allarga di molto,
        // perché la 18 costa dieci volte tanto.
        Assert.True(g[20].KeysPerRun > g[18].KeysPerRun);
        Assert.True(g[20].KeysPerHour > g[18].KeysPerHour * 4);

        // E la 19 resta ultima fra chi gira, su entrambe le letture.
        Assert.True(g[19].KeysPerHour < g[18].KeysPerHour);
        Assert.True(g[19].KeysPerHour < g[20].KeysPerHour);
    }

    /// <summary>
    /// L'ordine del pannello è per COSTO, perché la domanda che deve far venire in mente è «dove
    /// sono le ore». Con l'ordine per resa, la configurazione 19 — 14 ore e zero gambe — finiva in
    /// fondo all'elenco, che è esattamente dove non si guarda.
    /// </summary>
    [Fact]
    public void LORDINE_ePERcosto()
    {
        var ordine = Giudizio().Select(r => r.ConfigurationId).ToList();

        Assert.Equal(18, ordine[0]);   // 21,88 h
        Assert.Equal(19, ordine[1]);   // 14,09 h
        Assert.Equal(8, ordine[^1]);   // 0 h
    }

    [Fact]
    public void SENZAcosto_ilVERDETTOnonCAMBIAdaPRIMA()
    {
        // La firma vecchia (senza ore) resta e continua a funzionare: i chiamanti che non hanno il
        // costo non devono comportarsi diversamente... ma tutto diventa Dormiente, perché zero ore
        // è zero ore. È il verso voluto: senza sapere quanto costa, non si condanna nessuno.
        var senzaCosto = HuntYield.Judge([.. Reali.Select(r => (r.Cfg, r.Run, r.Chiavi))]);

        Assert.All(senzaCosto.Where(r => r.Runs >= HuntYield.MinRunsForVerdict),
            r => Assert.Equal(HuntYieldVerdict.Dormiente, r.Verdict));
    }

    [Fact]
    public void POCHIrun_restaTROPPOpresto_anchesCOSTAtanto()
        => Assert.Equal(HuntYieldVerdict.TroppoPresto,
            HuntYield.Judge([(99, 3, 0, 50.0, 60.0)]).Single().Verdict);
}
