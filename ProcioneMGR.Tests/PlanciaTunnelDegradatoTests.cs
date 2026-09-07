using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prove del 2026-09-07: «il marcatore combacia» non basta.
///
/// La plancia sapeva già che <b>la porta in ascolto non basta</b> (il pod può essere stato
/// sostituito) e confrontava quindi il pod servito con quello vivo. Restava scoperto un terzo
/// modo di morire, e si è manifestato: lo stream sotto al port-forward si degrada mentre il pod
/// resta esattamente lo stesso.
///
/// Misurato quel giorno sul motore: <c>/health</c> rispondeva in 0,03–0,20 s <b>tre volte su
/// cinque</b> e si piantava per i venti secondi interi del tetto le altre due, con il marcatore
/// perfettamente allineato. Richiuso e riaperto il tunnel: <b>otto prove su otto in 0,03 s</b>.
///
/// Il quadro allora si contraddiceva a due righe di distanza — «tunnel sano» e «motore giù» — e,
/// peggio, mandava a eseguire <c>procione ripara tunnel</c>, che in quel caso <b>non fa nulla</b>:
/// lo script vede il marcatore combaciare e dichiara «già attivo».
/// </summary>
public class PlanciaTunnelDegradatoTests
{
    private static readonly Pod PodVivo =
        new("procionemgr-trading", "procionemgr-trading-ff5db5cdc-j6hqt", "Running", 0, true, DateTimeOffset.UtcNow);

    private static Check Tunnel(bool? servizioRisponde, string? marcatore = null, params int[] inAscolto) =>
        Verdicts.Tunnel("motore", [18092, 18093], marcatore ?? PodVivo.Identity, PodVivo,
                        new HashSet<int>(inAscolto.Length == 0 ? [18092, 18093] : inAscolto),
                        clusterSu: true, serve: "/trading", servizioRisponde: servizioRisponde);

    [Fact]
    public void Tunnel_aperto_sul_pod_giusto_ma_che_non_trasporta_e_un_guasto()
    {
        var c = Tunnel(servizioRisponde: false);

        Assert.Equal(Level.Down, c.Level);
        Assert.Contains("NON TRASPORTA", c.Detail);
    }

    [Fact]
    public void E_il_rimedio_non_e_quello_che_non_ripara()
    {
        // Il punto più importante del difetto: `ripara tunnel` da solo trova il marcatore
        // allineato e dichiara «già attivo». Suggerirlo qui vorrebbe dire mandare l'operatore a
        // eseguire un comando che non tocca niente — e poi a chiedersi perché non è cambiato nulla.
        var c = Tunnel(servizioRisponde: false);

        Assert.NotNull(c.Fix);
        Assert.Contains("--rifai", c.Fix);
    }

    [Fact]
    public void Un_tunnel_che_trasporta_resta_muto()
    {
        // Il controllo obbligatorio: la sonda nuova non deve accendersi sul funzionamento normale.
        var c = Tunnel(servizioRisponde: true);

        Assert.Equal(Level.Ok, c.Level);
        Assert.Null(c.Fix);
    }

    [Fact]
    public void Senza_la_sonda_il_verdetto_resta_quello_di_prima()
    {
        // `null` = non l'ho chiesto. Non si inventa un guasto per una domanda non posta: è la
        // stessa regola che governa tutto il resto del quadro.
        var c = Tunnel(servizioRisponde: null);

        Assert.Equal(Level.Ok, c.Level);
    }

    [Fact]
    public void I_guasti_gia_noti_hanno_la_precedenza_sulla_sonda_nuova()
    {
        // Un tunnel STANTIO va detto stantio, anche se per caso il servizio risponde (può
        // succedere durante un rollout, con il pod vecchio ancora vivo): la diagnosi precisa vale
        // più di quella generica, e il rimedio è diverso.
        var stantio = Verdicts.Tunnel("motore", [18092, 18093], "procionemgr-trading-VECCHIO-aaaaa#3",
                                      PodVivo, new HashSet<int> { 18092, 18093 },
                                      clusterSu: true, serve: "/trading", servizioRisponde: true);
        Assert.Equal(Level.Down, stantio.Level);
        Assert.Contains("STANTIO", stantio.Detail);

        // E un tunnel che NON C'È non è «aperto ma non trasporta»: è assente, e si dice così.
        var assente = Verdicts.Tunnel("motore", [18092, 18093], PodVivo.Identity, PodVivo,
                                      new HashSet<int>(), clusterSu: true, serve: "/trading",
                                      servizioRisponde: false);
        Assert.Equal(Level.Down, assente.Level);
        Assert.Contains("non in ascolto", assente.Detail);
        Assert.DoesNotContain("NON TRASPORTA", assente.Detail);
    }

    [Fact]
    public void Tunnel_a_meta_resta_un_avviso_anche_se_il_servizio_tace()
    {
        // Una porta su due in ascolto ha già la sua diagnosi, più precisa: rifarlo intero è il
        // rimedio, e non serve la sonda per saperlo.
        var c = Verdicts.Tunnel("motore", [18092, 18093], PodVivo.Identity, PodVivo,
                                new HashSet<int> { 18092 }, clusterSu: true, serve: "/trading",
                                servizioRisponde: false);

        Assert.Equal(Level.Warn, c.Level);
        Assert.Contains("incompleto", c.Detail);
    }
}
