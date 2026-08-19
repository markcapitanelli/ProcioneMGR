using ProcioneMGR.Services.Health;

namespace ProcioneMGR.Tests;

/// <summary>
/// [I1] La parte PURA della sonda dello stato degli agenti.
///
/// Il difetto che copre è documentato e costoso: il 2026-08-18 si è pianificata un'ondata di lavoro
/// sulla premessa che sei sottosistemi fossero «spenti per configurazione», e <b>tre erano accesi da
/// settimane</b> — il <c>false</c> che tutti ricordavano stava in <c>appsettings.json.example</c>,
/// che è un documento, non lo stato. Nessuna superficie diceva quale delle due cose fosse vera.
///
/// La proprietà che questi test difendono è UNA: <b>«acceso» e «operante» non sono la stessa cosa</b>,
/// e nemmeno «spento» e «non determinabile» lo sono. Ogni caso qui sotto è preso da uno stato reale
/// misurato quel giorno, non inventato.
///
/// <para>Tre di questi test esistono perché la <b>revisione avversaria</b> dello stesso giorno ha
/// trovato che la prima versione della sonda mentiva in tre punti — sempre nella stessa direzione,
/// dichiarando il sistema più autonomo di quanto fosse. Sono marcati nel loro doc-comment.</para>
/// </summary>
public class AgentStateProbeTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Tutto spento, ma con soggetti in abbondanza: il caso di riferimento.</summary>
    private static AgentStateFacts AllOff() => new(
        CampaignEnabled: false, CampaignsEnabled: 0, CampaignsRotating: 0, CampaignsWaitingForTrigger: 0,
        RegimeTriggerEnabled: true,
        FleetEnabled: false, FleetDryRun: true, FleetExecutionImplemented: false, FleetAuthorizedLanes: 0,
        FleetUseCommittee: false, FleetGovernedLanes: 5,
        CommitteeEnabled: false, CommitteeProviders: 3, CommitteeProvidersWithKey: 3,
        CommitteeMinValidVotes: 2, CommitteeVotesInWindow: 0, CommitteeWindowDays: 14,
        DriftEnabled: false, DriftRetireChampionOnAlert: false, SavedModelCount: 53, ChampionCount: 0);

    private static AgentState Agent(AgentStateReport r, string name) =>
        r.Agents.Single(a => a.Name == name);

    // ------------------------------------------------------------------ base

    [Fact]
    public void TuttiSpenti_NessunAgenteAccesoNeOperante()
    {
        var r = AgentStateProbe.Describe(AllOff(), Now);

        Assert.All(r.Agents, a => Assert.Equal(AgentActivation.Spento, a.Activation));
        Assert.False(r.AnyOn);
        Assert.False(r.AnyOperating);
        Assert.NotEmpty(r.Summary);
    }

    /// <summary>
    /// <b>Il controllo che conta (livello 2 dello standard): i SOGGETTI da soli non accendono
    /// niente.</b> Campagne abilitate, modelli salvati, provider con chiave, voti nel journal e
    /// corsie libere in abbondanza — coi gate spenti nessun agente deve risultare acceso. Se questo
    /// fallisse, la sonda starebbe leggendo la presenza di dati come attività, cioè inventando
    /// allarmi.
    /// </summary>
    [Fact]
    public void SoggettiInAbbondanza_MaGateSpenti_NonAccendonoNulla()
    {
        var facts = AllOff() with
        {
            CampaignsEnabled = 4,
            CampaignsRotating = 4,
            SavedModelCount = 999,
            ChampionCount = 7,
            CommitteeProvidersWithKey = 5,
            CommitteeVotesInWindow = 120,
            FleetGovernedLanes = 12,
        };

        var r = AgentStateProbe.Describe(facts, Now);

        Assert.All(r.Agents, a => Assert.Equal(AgentActivation.Spento, a.Activation));
        Assert.False(r.AnyOn);
    }

    /// <summary>
    /// <b>Lo stato REALE del 2026-08-18</b>, ricostruito dai numeri misurati sul database: planner
    /// acceso con una campagna in attesa di trigger, flotta accesa in dry-run su 5 corsie, comitato
    /// acceso e interrogabile ma con <b>zero voti</b> in quindici giorni, monitor di deriva spento
    /// col ritiro automatico armato e zero Champion.
    ///
    /// È il test che, se fosse esistito, avrebbe risparmiato la premessa sbagliata — e nota che il
    /// comitato risulta <b>inerte, non operante</b>: i suoi interruttori sono tutti a posto, ed è
    /// esattamente per questo che il verdetto non può fermarsi a loro.
    /// </summary>
    [Fact]
    public void StatoRealeDel18Agosto_DueOperantiUnComitatoInerteEIlCicloChiusoSenzaSoggetto()
    {
        var facts = new AgentStateFacts(
            CampaignEnabled: true, CampaignsEnabled: 1, CampaignsRotating: 0, CampaignsWaitingForTrigger: 1,
            RegimeTriggerEnabled: true,
            FleetEnabled: true, FleetDryRun: true, FleetExecutionImplemented: false, FleetAuthorizedLanes: 0,
            FleetUseCommittee: true, FleetGovernedLanes: 5,
            CommitteeEnabled: true, CommitteeProviders: 3, CommitteeProvidersWithKey: 3,
            CommitteeMinValidVotes: 2, CommitteeVotesInWindow: 0, CommitteeWindowDays: 14,
            DriftEnabled: false, DriftRetireChampionOnAlert: true, SavedModelCount: 53, ChampionCount: 0);

        var r = AgentStateProbe.Describe(facts, Now);

        Assert.True(r.AnyOperating);
        Assert.Equal(AgentActivation.AccesoOperante, Agent(r, "Campaign Planner").Activation);
        Assert.Equal(AgentActivation.AccesoOperante, Agent(r, "Orchestratore di flotta").Activation);

        // Tutti i flag a posto e ZERO voti: acceso e inerte. È la misura, non l'interruttore.
        var committee = Agent(r, "Comitato AI");
        Assert.Equal(AgentActivation.AccesoInerte, committee.Activation);
        Assert.Contains("ZERO voti", committee.Detail, StringComparison.Ordinal);

        // Il monitor è spento, ma la manopola del ciclo chiuso si dichiara lo stesso: è quella che
        // all'accensione entrerebbe in vigore col default del POCO, e oggi è inerte solo perché
        // non esiste alcun Champion — una salvezza per coincidenza, che va detta.
        var drift = Agent(r, "Drift feature ML");
        Assert.Equal(AgentActivation.Spento, drift.Activation);
        Assert.Contains("ARMATO", drift.Detail, StringComparison.Ordinal);
        Assert.Contains("SENZA SOGGETTO", drift.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- Campaign Planner

    [Fact]
    public void Campaign_AccesoSenzaCampagneAbilitate_EInerte()
    {
        var r = AgentStateProbe.Describe(AllOff() with { CampaignEnabled = true, CampaignsEnabled = 0 }, Now);

        Assert.Equal(AgentActivation.AccesoInerte, Agent(r, "Campaign Planner").Activation);
        Assert.False(r.AnyOperating);
        Assert.True(r.AnyOn);
    }

    /// <summary>
    /// La regola che è più facile scrivere al contrario. Una campagna in <c>WaitingForTrigger</c>
    /// non è inerte: un wake del trigger contestuale la rimette in rotazione senza che nessuno prema
    /// nulla.
    /// </summary>
    [Fact]
    public void Campaign_InAttesaDiTriggerColTriggerAcceso_EOperanteNonInerte()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CampaignEnabled = true, CampaignsEnabled = 1,
                CampaignsRotating = 0, CampaignsWaitingForTrigger = 1, RegimeTriggerEnabled = true,
            }, Now);

        var a = Agent(r, "Campaign Planner");
        Assert.Equal(AgentActivation.AccesoOperante, a.Activation);
        Assert.Contains("wake", a.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Trovato dalla revisione avversaria.</b> La prima versione affermava il wake senza guardare
    /// se il wake potesse avvenire: col trigger contestuale spento nessuna campagna in attesa
    /// riparte mai, e la colonna «Perché» — che è tutto il valore della card — descriveva un
    /// percorso di riavvio inesistente.
    /// </summary>
    [Fact]
    public void Campaign_InAttesaDiTriggerMaTriggerSpento_EInerteELoDice()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CampaignEnabled = true, CampaignsEnabled = 1,
                CampaignsRotating = 0, CampaignsWaitingForTrigger = 1, RegimeTriggerEnabled = false,
            }, Now);

        var a = Agent(r, "Campaign Planner");
        Assert.Equal(AgentActivation.AccesoInerte, a.Activation);
        Assert.Contains("non ripartono da sole", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Trovato dalla revisione avversaria.</b> Una campagna in <c>Observing</c> non viene MAI
    /// risvegliata (<c>WakeAsync</c> filtra <c>Status != Observing</c>): dirle «in attesa di
    /// trigger» descriveva un percorso che per lei non esiste. Resta operante, ma per un'altra
    /// ragione — il riallineamento delle corsie Paper dopo un riavvio.
    /// </summary>
    [Fact]
    public void Campaign_SoloInOsservazione_EOperanteMaPerIlRiallineamentoNonPerIlWake()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CampaignEnabled = true, CampaignsEnabled = 1,
                CampaignsRotating = 0, CampaignsWaitingForTrigger = 0,
            }, Now);

        var a = Agent(r, "Campaign Planner");
        Assert.Equal(AgentActivation.AccesoOperante, a.Activation);
        Assert.Contains("riallineate", a.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("wake", a.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Campaign_InRotazione_DichiaraCheINuoviRunPartonoDaSoli()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with { CampaignEnabled = true, CampaignsEnabled = 2, CampaignsRotating = 2 }, Now);

        Assert.Equal(AgentActivation.AccesoOperante, Agent(r, "Campaign Planner").Activation);
        Assert.Contains("2 in rotazione", Agent(r, "Campaign Planner").Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ Flotta

    [Fact]
    public void Fleet_AccesoSenzaCorsieOltreLImpronta_EInerte()
    {
        var r = AgentStateProbe.Describe(AllOff() with { FleetEnabled = true, FleetGovernedLanes = 0 }, Now);

        Assert.Equal(AgentActivation.AccesoInerte, Agent(r, "Orchestratore di flotta").Activation);
    }

    /// <summary>
    /// Il dry-run NON è inerzia: decide, scrive il journal e produce proposte (nel periodo misurato,
    /// 83 in quindici giorni). Dirlo inerte spiegherebbe male le proposte che l'operatore riceve.
    /// </summary>
    [Fact]
    public void Fleet_InDryRun_EOperanteEDichiaraCheNonEsegue()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with { FleetEnabled = true, FleetDryRun = true, FleetGovernedLanes = 5 }, Now);

        var a = Agent(r, "Orchestratore di flotta");
        Assert.Equal(AgentActivation.AccesoOperante, a.Activation);
        Assert.Contains("DRY-RUN", a.Detail, StringComparison.Ordinal);
        Assert.Contains("non esegue", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il difetto peggiore trovato dalla revisione avversaria, e la sua regressione.</b> La prima
    /// versione deduceva l'esecuzione dal solo <c>Fleet:DryRun</c> e dichiarava «ESECUZIONE ATTIVA:
    /// può avviare e fermare corsie da solo». Falso: finché AF2b non esiste, spegnere il dry-run non
    /// accende nulla — il worker emette un warning e journalizza comunque <c>Applied=false</c>.
    /// La sonda nata per dire con precisione che cosa agisce da solo pubblicava un'affermazione
    /// smentita dal codice che stava descrivendo, e nella direzione peggiore: sistema più autonomo
    /// del vero.
    /// </summary>
    [Fact]
    public void Fleet_DryRunSpentoMaBraccioEsecutivoAssente_NonPromettEsecuzione()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                FleetEnabled = true, FleetDryRun = false,
                FleetExecutionImplemented = false, FleetGovernedLanes = 5,
            }, Now);

        var a = Agent(r, "Orchestratore di flotta");
        Assert.Equal(AgentActivation.AccesoOperante, a.Activation);
        Assert.Contains("NON è implementato", a.Detail, StringComparison.Ordinal);
        Assert.Contains("solo journal", a.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ESECUZIONE ATTIVA", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// [AF2b, 2026-08-19] Braccio presente, dry-run spento <b>e corsie autorizzate</b>: solo allora
    /// la sonda dichiara l'esecuzione attiva — e dice anche quale metà del braccio esiste, perché
    /// «può fermare» e «può avviare» non sono la stessa autonomia.
    /// </summary>
    [Fact]
    public void Fleet_DryRunSpentoBraccioECorsieAutorizzate_DichiaraEsecuzioneAttiva()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                FleetEnabled = true, FleetDryRun = false,
                FleetExecutionImplemented = true, FleetAuthorizedLanes = 2, FleetGovernedLanes = 5,
            }, Now);

        var a = Agent(r, "Orchestratore di flotta");
        Assert.Contains("ESECUZIONE ATTIVA", a.Detail, StringComparison.Ordinal);
        Assert.Contains("2 corsie autorizzate", a.Detail, StringComparison.Ordinal);
        Assert.Contains("l'avvio automatico non è implementato", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// [AF2b] <b>La stessa bugia, spostata di un flag.</b> Braccio implementato e dry-run spento, ma
    /// <c>Fleet:ExecutionLanes</c> VUOTA: la macchina non può toccare nulla. Dichiarare «esecuzione
    /// attiva» qui sarebbe la ripetizione esatta del difetto trovato il 2026-08-18 — un controllo
    /// che rassicura (o allarma) a prescindere dalla realtà, con un flag in più a coprirlo.
    /// </summary>
    [Fact]
    public void Fleet_BraccioPresenteMaNessunaCorsiaAutorizzata_NonEEsecuzioneAttiva()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                FleetEnabled = true, FleetDryRun = false,
                FleetExecutionImplemented = true, FleetAuthorizedLanes = 0, FleetGovernedLanes = 5,
            }, Now);

        var a = Agent(r, "Orchestratore di flotta");
        Assert.DoesNotContain("ESECUZIONE ATTIVA", a.Detail, StringComparison.Ordinal);
        Assert.Contains("VUOTA", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// I fatti vengono dalle costanti dichiarate nel worker, non da numeri riscritti a mano qui.
    /// Le DUE metà del braccio sono separate perché lo sono davvero: dal 2026-08-19 l'orchestratore
    /// sa fermare una corsia, e continua a NON saperla avviare — l'ordine deciso dal proprietario.
    /// </summary>
    [Fact]
    public void Fleet_LeDueMetaDelBraccioSonoDichiarateDalWorker()
    {
        Assert.True(ProcioneMGR.Services.Fleet.FleetOrchestratorWorker.RetirementArmImplemented);
        Assert.False(ProcioneMGR.Services.Fleet.FleetOrchestratorWorker.AssignmentArmImplemented);
    }

    // ---------------------------------------------------------- Comitato AI

    /// <summary>
    /// Il caso che il 2026-08-18 era vero e invisibile: il comitato acceso che non voterà MAI
    /// perché nessuno gli pone domande. Un pannello che mostra solo la spunta è vero e inutile.
    /// </summary>
    [Theory]
    [InlineData(false, true)]   // Fleet spento: l'orchestratore che lo interroga non gira
    [InlineData(true, false)]   // Fleet acceso ma UseCommittee spento
    public void Committee_AccesoMaSenzaChiGliPongaDomande_EInerte(bool fleetEnabled, bool useCommittee)
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CommitteeEnabled = true,
                FleetEnabled = fleetEnabled,
                FleetUseCommittee = useCommittee,
                FleetGovernedLanes = 5,
            }, Now);

        var a = Agent(r, "Comitato AI");
        Assert.Equal(AgentActivation.AccesoInerte, a.Activation);
        Assert.Contains("non voterà mai", a.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Committee_ProviderConChiaveSottoIlQuorum_EInerte()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CommitteeEnabled = true,
                FleetEnabled = true,
                FleetUseCommittee = true,
                CommitteeProvidersWithKey = 1,
                CommitteeMinValidVotes = 2,
            }, Now);

        Assert.Equal(AgentActivation.AccesoInerte, Agent(r, "Comitato AI").Activation);
    }

    /// <summary>
    /// <b>Trovato dalla revisione avversaria.</b> Con tutti gli interruttori a posto la prima
    /// versione promuoveva a «ACCESO E OPERANTE» e scriveva «arbitra i pareggi dell'orchestratore» —
    /// nello stato in cui il comitato, misurato, non aveva emesso un solo voto in sedici giorni.
    /// Il verdetto va preso dal fatto (i voti nel journal), non dai flag: è la seconda domanda del
    /// censimento del 2026-07-31, «misura contro la realtà o contro sé stesso?».
    /// </summary>
    [Fact]
    public void Committee_InterruttoriAPostoMaZeroVoti_EInerteNonOperante()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CommitteeEnabled = true,
                FleetEnabled = true,
                FleetUseCommittee = true,
                FleetGovernedLanes = 5,
                CommitteeProvidersWithKey = 3,
                CommitteeMinValidVotes = 2,
                CommitteeVotesInWindow = 0,
            }, Now);

        var a = Agent(r, "Comitato AI");
        Assert.Equal(AgentActivation.AccesoInerte, a.Activation);
        Assert.Contains("ZERO voti negli ultimi 14 giorni", a.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("arbitra i pareggi", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Il caso-trappola.</b> Fonte muta ⇒ «non determinabile», che NON è «spento» e nemmeno
    /// «inerte»: dichiarare inerte un comitato che magari funziona è un verdetto costruito
    /// sull'ignoranza — la stessa regola per cui <c>LaneCountCoherenceProbe</c> tiene separato il
    /// motore irraggiungibile dal motore disallineato. Vale per entrambe le fonti.
    /// </summary>
    [Fact]
    public void Committee_KeyringMuto_ENonDeterminabileNonSpento()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CommitteeEnabled = true,
                FleetEnabled = true,
                FleetUseCommittee = true,
                FleetGovernedLanes = 0, // flotta inerte apposta: così l'unico candidato a «operante» è il comitato
                CommitteeProvidersWithKey = null,
            }, Now);

        var a = Agent(r, "Comitato AI");
        Assert.Equal(AgentActivation.NonDeterminabile, a.Activation);
        Assert.Contains("non è determinabile", a.Detail, StringComparison.Ordinal);
        Assert.False(r.AnyOperating);
        Assert.True(r.AnyOn);
    }

    [Fact]
    public void Committee_JournalMuto_ENonDeterminabileNonInerte()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CommitteeEnabled = true,
                FleetEnabled = true,
                FleetUseCommittee = true,
                FleetGovernedLanes = 0,
                CommitteeProvidersWithKey = 3,
                CommitteeVotesInWindow = null,
            }, Now);

        var a = Agent(r, "Comitato AI");
        Assert.Equal(AgentActivation.NonDeterminabile, a.Activation);
        Assert.False(r.AnyOperating);
    }

    [Fact]
    public void Committee_ConVotiVeriNellaFinestra_EOperante()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with
            {
                CommitteeEnabled = true,
                FleetEnabled = true,
                FleetUseCommittee = true,
                FleetGovernedLanes = 5,
                CommitteeProvidersWithKey = 3,
                CommitteeMinValidVotes = 2,
                CommitteeVotesInWindow = 7,
            }, Now);

        var a = Agent(r, "Comitato AI");
        Assert.Equal(AgentActivation.AccesoOperante, a.Activation);
        Assert.Contains("7 voti", a.Detail, StringComparison.Ordinal);
    }

    // ----------------------------------------------------- Drift / [I2] Champion

    [Fact]
    public void Drift_AccesoSenzaModelliSalvati_EInerte()
    {
        var r = AgentStateProbe.Describe(AllOff() with { DriftEnabled = true, SavedModelCount = 0 }, Now);

        Assert.Equal(AgentActivation.AccesoInerte, Agent(r, "Drift feature ML").Activation);
    }

    [Fact]
    public void Drift_AccesoConModelli_EOperante()
    {
        var r = AgentStateProbe.Describe(AllOff() with { DriftEnabled = true, SavedModelCount = 53 }, Now);

        var a = Agent(r, "Drift feature ML");
        Assert.Equal(AgentActivation.AccesoOperante, a.Activation);
        Assert.Contains("53 modelli", a.Detail, StringComparison.Ordinal);
    }

    /// <summary>[I2] Champion non contabile ⇒ si dice «non determinabile», mai «0».</summary>
    [Fact]
    public void Drift_ChampionNonContabile_NonDiventaZero()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with { DriftRetireChampionOnAlert = true, ChampionCount = null }, Now);

        var a = Agent(r, "Drift feature ML");
        Assert.Contains("non determinabile", a.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SENZA SOGGETTO", a.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Drift_CicloChiusoSpento_LoDichiaraSenzaParlareDiChampion()
    {
        var r = AgentStateProbe.Describe(AllOff() with { DriftRetireChampionOnAlert = false }, Now);

        var a = Agent(r, "Drift feature ML");
        Assert.Contains("ritiro automatico del Champion spento", a.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ARMATO", a.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Drift_CicloChiusoConChampionInCarica_LoDichiaraColNumero()
    {
        var r = AgentStateProbe.Describe(
            AllOff() with { DriftEnabled = true, DriftRetireChampionOnAlert = true, ChampionCount = 2 }, Now);

        Assert.Contains("su 2 Champion in carica", Agent(r, "Drift feature ML").Detail, StringComparison.Ordinal);
    }
}
