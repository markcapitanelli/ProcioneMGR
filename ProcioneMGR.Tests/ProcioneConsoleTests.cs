using System.Text;
using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prove della plancia di comando (`procione`, tools/Procione).
///
/// Si provano SOLO le funzioni pure — lettura dell'output degli strumenti esterni e verdetti —
/// perche' e' li' che una plancia puo' rovinarsi in silenzio: se il confronto sul tunnel sbaglia,
/// il quadro resta verde su una piattaforma rotta (oppure, peggio, grida al lupo di continuo e si
/// smette di guardarlo). Il resto della plancia parla con docker, kubectl e la rete, ed e' provato
/// dal vivo contro la piattaforma vera.
///
/// I dati d'esempio non sono inventati: sono copiati dall'output reale degli strumenti su questa
/// macchina (2026-08-17).
/// </summary>
public class ProcioneConsoleTests
{
    // =============================================================================================
    //  Livello 1 — lettura dell'output, contro esempi reali
    // =============================================================================================

    [Fact]
    public void Marker_toglie_il_BOM_che_PowerShell_5_1_scrive()
    {
        // Riferimento indipendente: non si simula il BOM a mano, si producono i BYTE che
        // `Set-Content -Encoding utf8` scrive davvero, e si rilegge come fa la plancia.
        // Senza TrimStart il confronto fallirebbe SEMPRE e ogni tunnel sano risulterebbe stantio.
        var file = Path.GetTempFileName();
        try
        {
            var byteScritti = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("procionemgr-trading-6cf8c78dff-zb8dz#9\r\n"))
                .ToArray();
            File.WriteAllBytes(file, byteScritti);

            Assert.Equal("procionemgr-trading-6cf8c78dff-zb8dz#9", Parsing.Marker(File.ReadAllText(file)));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("﻿")]
    public void Marker_tratta_come_assente_un_marcatore_vuoto(string? contenuto)
        => Assert.Null(Parsing.Marker(contenuto));

    [Fact]
    public void Pods_legge_le_righe_del_jsonpath()
    {
        const string uscita =
            "procionemgr-trading|procionemgr-trading-6cf8c78dff-zb8dz|Running|9|true|2026-08-13T15:42:11Z\n" +
            "procionemgr-ml|procionemgr-ml-67d5d57cc-lb7hn|Running|6|true|2026-08-12T22:05:00Z\n";

        var pods = Parsing.Pods(uscita);

        Assert.Equal(2, pods.Count);
        Assert.Equal("procionemgr-trading", pods[0].Ns);
        Assert.Equal("Running", pods[0].Phase);
        Assert.Equal(9, pods[0].Restarts);
        Assert.True(pods[0].Ready);
        // L'identita' che il confronto sul tunnel usa davvero.
        Assert.Equal("procionemgr-trading-6cf8c78dff-zb8dz#9", pods[0].Identity);
    }

    [Fact]
    public void Pods_tratta_come_zero_il_conteggio_riavvii_mancante()
    {
        // Container non ancora avviato: jsonpath emette i separatori ma non il valore. Trattarlo
        // come 0 (e non come ignoto) e' la stessa scelta di ensure-trading-portforward.ps1.
        var pods = Parsing.Pods("procionemgr-ingestion|pod-nuovo|Pending|||2026-08-17T05:00:00Z");

        Assert.Single(pods);
        Assert.Equal(0, pods[0].Restarts);
        Assert.False(pods[0].Ready);
        Assert.Equal("pod-nuovo#0", pods[0].Identity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("riga|senza|abbastanza|campi")]
    [InlineData("ns||Running|0|true|2026-08-17T05:00:00Z")]  // nome vuoto: non e' un pod
    public void Pods_ignora_le_righe_che_non_sono_pod(string uscita)
        => Assert.Empty(Parsing.Pods(uscita));

    [Fact]
    public void Containers_legge_il_formato_tabulato_di_docker_ps()
    {
        const string uscita =
            "kind-apiproxy\trunning\tUp 34 minutes\t\t\n" +
            "procionemgr-ui-1\trunning\tUp 2 hours\tprocionemgr\tui\n" +
            "procionemgr-postgres-1\texited\tExited (0) 3 days ago\tprocionemgr\tpostgres\n";

        var c = Parsing.Containers(uscita);

        Assert.Equal(3, c.Count);
        Assert.Equal("kind-apiproxy", c[0].Name);
        Assert.Equal("", c[0].Project);           // container fuori da compose: etichette vuote
        Assert.Equal("ui", c[1].Service);
        Assert.Equal("exited", c[2].State);
    }

    [Fact]
    public void KubeServer_trova_il_cluster_giusto_fra_piu_cluster()
    {
        const string kubeconfig = """
        {
          "clusters": [
            { "name": "docker-desktop",         "cluster": { "server": "https://kubernetes.docker.internal:6443" } },
            { "name": "kind-procionemgr-dev",   "cluster": { "server": "https://127.0.0.1:16443" } }
          ]
        }
        """;

        Assert.Equal("https://127.0.0.1:16443", Parsing.KubeServer(kubeconfig, "kind-procionemgr-dev"));
        Assert.Null(Parsing.KubeServer(kubeconfig, "kind-inesistente"));
        Assert.Null(Parsing.KubeServer("non e' json", "kind-procionemgr-dev"));
    }

    [Fact]
    public void HeartbeatAge_legge_il_payload_vero_dell_ingestion()
    {
        // Copiato da GET http://localhost:18080/health.
        Assert.Contains("2m", Parsing.HeartbeatAge(
            """{"status":"ok","lastLoopTickUtc":"2026-08-17T05:51:03.1489604Z","ageSeconds":152}"""));

        // Payload senza il campo: si degrada, non si inventa.
        Assert.Equal("in salute", Parsing.HeartbeatAge("""{"status":"ok"}"""));
        Assert.Equal("in salute", Parsing.HeartbeatAge("non e' json"));
    }

    [Theory]
    [InlineData("0", true)]        // riuscita
    [InlineData("267011", true)]   // mai eseguita: non e' un fallimento
    [InlineData("267009", true)]   // in esecuzione adesso
    [InlineData("", true)]         // esito ignoto: non si grida
    [InlineData("1", false)]           // fallita davvero (e' l'esito vero del backup notturno)
    [InlineData("2147942401", false)]  // 0x80070001: HRESULT senza segno, TRABOCCA da Int32
    [InlineData("-2147024809", false)] // 0x80070057: la stessa cosa con il segno
    [InlineData("boh", false)]         // illeggibile: un giallo da guardare, non un verde falso
    public void TaskResultIsFine_distingue_il_fallimento_dallo_stato_normale(string esito, bool atteso)
        => Assert.Equal(atteso, Parsing.TaskResultIsFine(esito));

    [Fact]
    public void TaskResultLabel_non_azzera_gli_esiti_che_traboccano_da_Int32()
    {
        // Il difetto gemello, sullo SCHERMO invece che nel verdetto: con int.TryParse + ripiego a 0
        // un fallimento 0x80070001 veniva mostrato come «0x00000000», cioe' come una riuscita.
        Assert.Equal("0x80070001", Parsing.TaskResultLabel("2147942401"));
        Assert.Equal("0x00000001", Parsing.TaskResultLabel("1"));
        Assert.Equal("'boh'", Parsing.TaskResultLabel("boh"));
    }

    [Fact]
    public void NodeStatus_e_ContainerUptime_leggono_le_forme_reali()
    {
        Assert.Equal("Ready", Parsing.NodeStatus(
            "procionemgr-dev-control-plane   Ready   control-plane   21d   v1.36.1"));
        Assert.Null(Parsing.NodeStatus(""));
        Assert.Equal("34 minutes", Parsing.ContainerUptime("Up 34 minutes"));
        Assert.Equal("Exited (0) 3 days ago", Parsing.ContainerUptime("Exited (0) 3 days ago"));
    }

    // =============================================================================================
    //  Livello 2 — controllo: sul sano la plancia deve TACERE, sul rotto deve accendersi
    // =============================================================================================

    private static readonly Pod PodVivo =
        new("procionemgr-trading", "procionemgr-trading-6cf8c78dff-zb8dz", "Running", 9, true, DateTimeOffset.UtcNow);

    private static Check Tunnel(string? marcatore, Pod? pod, params int[] inAscolto) =>
        Verdicts.Tunnel("motore", [18092, 18093], marcatore, pod,
                        new HashSet<int>(inAscolto), clusterSu: true, serve: "/trading");

    [Fact]
    public void Tunnel_sano_non_accende_niente()
    {
        var c = Tunnel(PodVivo.Identity, PodVivo, 18092, 18093);

        Assert.Equal(Level.Ok, c.Level);
        Assert.Null(c.Fix);
    }

    [Fact]
    public void Tunnel_sano_resta_muto_su_una_batteria_di_casi_normali()
    {
        // Il complemento del test precedente: un verdetto che si accende ogni tanto sul normale
        // e' un verdetto che si impara a ignorare. Nessun falso positivo ammesso.
        for (var riavvii = 0; riavvii <= 50; riavvii++)
        {
            var pod = PodVivo with { Name = $"procionemgr-trading-{riavvii:D4}abc-x{riavvii}", Restarts = riavvii };
            var c = Tunnel(pod.Identity, pod, 18092, 18093);
            Assert.Equal(Level.Ok, c.Level);
        }
    }

    [Fact]
    public void Tunnel_verso_un_pod_SOSTITUITO_e_un_guasto()
    {
        // Deploy/OOM-kill/rollout: kubectl resta in ascolto sulla porta locale ma instrada verso
        // un pod che non esiste piu'. /trading si svuota mentre il motore opera regolarmente.
        var c = Tunnel("procionemgr-trading-VECCHIO-aaaaa#3", PodVivo, 18092, 18093);

        Assert.Equal(Level.Down, c.Level);
        Assert.Contains("STANTIO", c.Detail);
    }

    [Fact]
    public void Tunnel_verso_lo_STESSO_pod_con_container_riavviato_e_un_guasto()
    {
        // IL caso che per mesi e' passato inosservato: il pod mantiene nome e identita', il tunnel
        // di kubectl muore lo stesso, e la porta locale resta regolarmente in ascolto. Con il
        // confronto sul solo NOME questo verdetto sarebbe verde.
        var c = Tunnel("procionemgr-trading-6cf8c78dff-zb8dz#8", PodVivo, 18092, 18093);

        Assert.Equal(Level.Down, c.Level);
        Assert.Contains("STANTIO", c.Detail);
        Assert.Contains("#8", c.Detail);
        Assert.Contains("#9", c.Detail);
    }

    [Fact]
    public void Tunnel_a_meta_e_un_avviso_non_un_ok()
    {
        // Una porta sola in ascolto: tunnel aperto da una versione precedente dello script, o
        // kubectl morente. Non e' sano, e non e' nemmeno morto.
        var c = Tunnel(PodVivo.Identity, PodVivo, 18092);

        Assert.Equal(Level.Warn, c.Level);
        Assert.Contains("incompleto", c.Detail);
    }

    [Fact]
    public void Tunnel_assente_e_un_guasto_con_il_rimedio()
    {
        var c = Tunnel(PodVivo.Identity, PodVivo);

        Assert.Equal(Level.Down, c.Level);
        Assert.Contains("ripara tunnel", c.Fix);
    }

    [Fact]
    public void Tunnel_senza_marcatore_non_puo_dirsi_sano()
    {
        // Porte in ascolto ma non si sa verso CHI: e' esattamente l'incertezza che il marcatore
        // esiste per togliere. Dichiararlo verde sarebbe un controllo che rassicura e basta.
        var c = Tunnel(null, PodVivo, 18092, 18093);

        Assert.Equal(Level.Warn, c.Level);
        Assert.Contains("SCONOSCIUTO", c.Detail);
    }

    [Fact]
    public void Tunnel_a_cluster_giu_non_e_colpa_del_tunnel()
    {
        // Niente porte e cluster giu': non e' un guasto DEL TUNNEL, e segnarlo rosso sposterebbe
        // l'attenzione dal problema vero, che sta un piano piu' sotto.
        var spento = Verdicts.Tunnel("motore", [18092, 18093], null, null,
                                     new HashSet<int>(), clusterSu: false, serve: "/trading");
        Assert.Equal(Level.NotApplicable, spento.Level);

        // Porte in ascolto col cluster giu' e' invece una bugia da segnalare: il tunnel c'e' ma
        // non porta da nessuna parte.
        var fantasma = Verdicts.Tunnel("motore", [18092, 18093], null, null,
                                       new HashSet<int> { 18092, 18093 }, clusterSu: false, serve: "/trading");
        Assert.Equal(Level.Warn, fantasma.Level);
    }

    // L'atteso viaggia come stringa perche' Layout e' un tipo interno della plancia e non puo'
    // comparire nella firma pubblica che xunit richiede.
    [Theory]
    [InlineData(true, true, true, "Both")]     // la violazione della regola 2
    [InlineData(true, true, false, "Kind")]
    [InlineData(true, false, true, "Compose")]
    [InlineData(true, false, false, "None")]
    [InlineData(false, false, false, "Unknown")]
    public void Which_riconosce_l_assetto(bool docker, bool kind, bool compose, string atteso)
        => Assert.Equal(atteso, Verdicts.Which(docker, kind, compose).ToString());

    // =============================================================================================
    //  Verdetto complessivo
    // =============================================================================================

    [Fact]
    public void Il_verdetto_complessivo_ignora_cio_che_non_e_previsto()
    {
        // NotApplicable non deve pesare: i tunnel kubectl sull'assetto Compose non sono guasti, e
        // contarli come tali renderebbe il codice di uscita inutilizzabile in uno script.
        var s = new Snapshot
        {
            Taken = DateTimeOffset.Now,
            Layout = Layout.Compose,
            Checks =
            [
                new Check("a", "uno", Level.Ok, ""),
                new Check("a", "due", Level.NotApplicable, ""),
            ],
        };

        Assert.Equal(Level.Ok, s.Worst);
        Assert.Equal(0, s.ExitCode);
    }

    [Fact]
    public void Il_codice_di_uscita_distingue_avvisi_e_guasti()
    {
        Snapshot Con(params Level[] livelli) => new()
        {
            Taken = DateTimeOffset.Now,
            Layout = Layout.Kind,
            Checks = [.. livelli.Select((l, i) => new Check("g", $"c{i}", l, ""))],
        };

        Assert.Equal(0, Con(Level.Ok, Level.Ok).ExitCode);
        Assert.Equal(1, Con(Level.Ok, Level.Warn).ExitCode);
        Assert.Equal(2, Con(Level.Warn, Level.Down).ExitCode);
    }
}
