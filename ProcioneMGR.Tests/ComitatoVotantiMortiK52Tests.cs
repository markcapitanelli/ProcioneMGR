using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Carry;
using ProcioneMGR.Services.Fleet;
using ProcioneMGR.Services.Llm;
using ProcioneMGR.Services.Llm.Committee;
using ProcioneMGR.Services.Security;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K52, PRD autonomia-piena — Fase 4, 2026-09-02] <b>Un votante morto non è un'astensione.</b>
///
/// <para><b>Il fatto misurato.</b> Il comitato di flotta ha tre votanti e ne chiede due validi. Il
/// 2026-09-01, alla prima decisione utile dopo settimane, i voti registrati nel journal
/// (<c>OrchestratorDecisions.VotesJson</c>, righe 129 e 130) erano:</para>
/// <code>
/// Nvidia : HTTP 410 — «model 'meta/llama-3.3-70b-instruct' has reached its end of life on 2026-08-26»
/// Groq   : HTTP 404 — «model `llama-3.3-70b-versatile` does not exist or you do not have access»
/// Gemini : valido, confidenza 0,95
/// </code>
///
/// <para>Un voto su tre contro una soglia di due: <b>quorum aritmeticamente irraggiungibile</b>, e
/// tale dal 2026-08-17 (ultima risposta valida di Groq in <c>LlmUsageRecords</c>; NVIDIA il 25/08).
/// Per due settimane la Regina ha deciso col default deterministico e nessuna superficie lo diceva,
/// perché il comitato è progettato — giustamente — perché un'astensione non costi nulla. Il difetto
/// è che quel principio copriva anche il votante che <b>non tornerà</b>.</para>
///
/// <para><b>Il nullo di questa suite</b> è la parte che conta: un classificatore che marchia tutto
/// come guasto permanente passerebbe le prove positive e trasformerebbe ogni 503 di free tier in un
/// allarme critico — cioè rifarebbe, nel verso opposto, lo stesso errore.</para>
/// </summary>
public class ComitatoVotantiMortiK52Tests
{
    private static CommitteeVote Guasto(string provider, int status) => new(
        provider, null, null, $"astensione: {provider.ToUpperInvariant()} HTTP {status}: {{}}",
        Valid: false, FaultCause: LlmCallGuard.Classify(
            new InvalidOperationException($"{provider.ToUpperInvariant()} HTTP {status}: {{}}")).Cause);

    private static CommitteeVote Valido(string provider, string optionId) =>
        new(provider, optionId, 0.9, "va bene", Valid: true);

    // ---------------------------------------------------------------- il classificatore

    /// <summary>
    /// 404 e 410 sono le due forme reali osservate, e devono cadere nella stessa categoria: il
    /// catalogo di Groq lo toglie (404), quello di NVIDIA lo dichiara ritirato (410). Il rimedio è
    /// identico, e la proprietà che conta è che <b>nessun retry le risolve</b>.
    /// </summary>
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public void ModelloSparito_eGuastoPERMANENTE(int status)
    {
        var (retryable, cause) = LlmCallGuard.Classify(new InvalidOperationException($"GROQ HTTP {status}: {{}}"));
        Assert.False(retryable);
        Assert.Equal(LlmCallGuard.ModelloAssente, cause);
    }

    /// <summary>
    /// <b>Il nullo del classificatore.</b> Senza questo, marchiare tutto come «modello assente»
    /// passerebbe la prova qui sopra: sono proprio le cause che GUARISCONO da sole a dover restare
    /// fuori, perché su di esse una notifica critica è rumore e il rimedio giusto è aspettare.
    /// </summary>
    [Theory]
    [InlineData(429, "rate-limit")]
    [InlineData(402, "credito API")]
    [InlineData(401, "credenziali")]
    [InlineData(503, "server")]
    [InlineData(500, "server")]
    [InlineData(400, "richiesta non valida")]
    public void LeCAUSEcheGUARISCONOdaSOLE_nonSonoGUASTIdiCONFIGURAZIONE(int status, string attesa)
    {
        var (_, cause) = LlmCallGuard.Classify(new InvalidOperationException($"NVIDIA HTTP {status}: {{}}"));
        Assert.Equal(attesa, cause);
        Assert.NotEqual(LlmCallGuard.ModelloAssente, cause);
    }

    // ---------------------------------------------------------------- la diagnosi del comitato

    [Fact]
    public void ILcasoREALEdel1SETTEMBRE_dueVOTANTImorti_quorumIRRAGGIUNGIBILE()
    {
        List<CommitteeVote> voti = [Guasto("Nvidia", 410), Guasto("Groq", 404), Valido("Gemini", "abc")];

        var guasti = CommitteeDiagnosis.VotantiGuasti(voti);
        Assert.Equal(["Nvidia", "Groq"], guasti.Select(g => g.Provider));
        Assert.True(CommitteeDiagnosis.QuorumIrraggiungibile(voti, minValidVotes: 2));
    }

    /// <summary>
    /// <b>Il nullo della diagnosi.</b> Gli stessi tre votanti, ma le astensioni sono transitorie:
    /// nessun guasto di configurazione, e il quorum è mancato solo per stavolta. Senza questa
    /// prova, un predicato che dicesse sempre «irraggiungibile» supererebbe quella sopra.
    /// </summary>
    [Fact]
    public void ILNULLO_astensioniTRANSITORIE_nonSonoUNguasto()
    {
        List<CommitteeVote> voti = [Guasto("Nvidia", 503), Guasto("Groq", 429), Valido("Gemini", "abc")];

        Assert.Empty(CommitteeDiagnosis.VotantiGuasti(voti));
        Assert.False(CommitteeDiagnosis.QuorumIrraggiungibile(voti, minValidVotes: 2));
    }

    /// <summary>
    /// Un votante morto su tre con soglia 2: il comitato è più fragile, ma il quorum è ancora
    /// possibile. La distinzione serve al testo della notifica — «decide con meno voci» contro
    /// «ogni decisione la prende il default» — e senza di essa i due casi si confonderebbero.
    /// </summary>
    [Fact]
    public void UNguastoSOLO_conDUEsuperstiti_ilQUORUMrestaPOSSIBILE()
    {
        List<CommitteeVote> voti = [Guasto("Nvidia", 410), Valido("Groq", "abc"), Valido("Gemini", "abc")];

        Assert.Single(CommitteeDiagnosis.VotantiGuasti(voti));
        Assert.False(CommitteeDiagnosis.QuorumIrraggiungibile(voti, minValidVotes: 2));
    }

    /// <summary>
    /// Comitato mai interrogato: zero voti. NON è «irraggiungibile», perché quella parola
    /// attribuirebbe la colpa a un guasto dei provider che non è stato misurato — le cause vere
    /// sono altre (spento, budget esaurito, nessuna chiave) e hanno già la loro etichetta.
    /// </summary>
    [Fact]
    public void ZEROvoti_nonSiCHIAMAirraggiungibile()
    {
        Assert.False(CommitteeDiagnosis.QuorumIrraggiungibile([], minValidVotes: 2));
        Assert.Empty(CommitteeDiagnosis.VotantiGuasti([]));
    }

    // ---------------------------------------------------------------- l'etichetta del journal

    [Fact]
    public void LaFONTE_diceGUASTOeNONquorumMANCATO()
    {
        var verdetto = new CommitteeVerdict("abc", ByQuorum: false,
            [Guasto("Nvidia", 410), Guasto("Groq", 404), Valido("Gemini", "abc")]);

        // [Revisione 2026-09-03] «provider-guasti» solo se il caduto è CONFERMATO dall'isteresi.
        Assert.Equal("default:provider-guasti",
            FleetOrchestratorWorker.DescribeAssignSource(verdetto, provideConfermatiGuasti: ["Groq"]));
    }

    /// <summary>
    /// <b>Il nullo della rettifica K53.</b> Un 404 isolato è rumore misurato (4 su 10 su NVIDIA con
    /// un modello che funziona): senza conferma dell'isteresi il journal NON deve scrivere
    /// «provider-guasti», che manderebbe a cercare un modello morto che non c'è.
    /// </summary>
    [Fact]
    public void ILNULLO_un404isolato_senzaCONFERMA_nonEguasto()
    {
        var verdetto = new CommitteeVerdict("abc", ByQuorum: false,
            [Guasto("Nvidia", 404), Valido("Groq", "abc"), Valido("Gemini", "xyz")]);

        Assert.Equal("default:quorum-mancato", FleetOrchestratorWorker.DescribeAssignSource(verdetto));
        Assert.Equal("default:quorum-mancato",
            FleetOrchestratorWorker.DescribeAssignSource(verdetto, provideConfermatiGuasti: []));
        // Confermato un ALTRO provider, non quello caduto in questo giro: ancora non è la causa.
        Assert.Equal("default:quorum-mancato",
            FleetOrchestratorWorker.DescribeAssignSource(verdetto, provideConfermatiGuasti: ["HuggingFace"]));
    }

    /// <summary>
    /// <b>Il nullo dell'etichetta.</b> Due voti validi e discordi: il quorum è mancato davvero, per
    /// disaccordo, e chiamarlo «provider guasti» manderebbe il proprietario a cercare un guasto che
    /// non c'è. Il ramo nuovo deve precedere gli altri <i>solo</i> quando la sua condizione vale.
    /// </summary>
    [Fact]
    public void ILNULLO_disaccordoVERO_restaQUORUMmancato()
    {
        var verdetto = new CommitteeVerdict("abc", ByQuorum: false,
            [Valido("Nvidia", "abc"), Valido("Groq", "xyz")]);

        Assert.Equal("default:quorum-mancato", FleetOrchestratorWorker.DescribeAssignSource(verdetto));
    }

    /// <summary>Le altre due cause di I8 non devono essere state assorbite dal ramo nuovo.</summary>
    [Fact]
    public void LeCAUSEdiI8_sopravvivono()
    {
        Assert.Equal("default:non-interrogato",
            FleetOrchestratorWorker.DescribeAssignSource(new CommitteeVerdict("abc", false, [])));

        Assert.Equal("default:tutti-astenuti", FleetOrchestratorWorker.DescribeAssignSource(
            new CommitteeVerdict("abc", false, [Guasto("Nvidia", 503), Guasto("Groq", 429)])));

        Assert.Equal("committee", FleetOrchestratorWorker.DescribeAssignSource(
            new CommitteeVerdict("abc", true, [Valido("Nvidia", "abc"), Valido("Groq", "abc")])));
    }

    /// <summary>
    /// La lezione di K45, applicata prima di pagarla: l'etichetta nuova deve STARE nella colonna.
    /// <c>OrchestratorDecisions.Source</c> è <c>varchar(32)</c> da K45 — e la volta scorsa una
    /// stringa diagnostica più lunga della colonna ha tenuto ferma la flotta per settimane.
    /// </summary>
    [Fact]
    public void LETICHETTAnuova_STAnellaCOLONNA()
        => Assert.True("default:provider-guasti".Length <= 32);

    // ------------------------------------------------------------------ l'isteresi (rettifica)

    /// <summary>
    /// <b>La soglia deve esistere ed essere maggiore di uno.</b> È la rettifica misurata: la prima
    /// versione di K52 dichiarava il guasto alla prima risposta 404. Campione controllato su NVIDIA
    /// del 2026-09-02 — dieci tentativi identici, stesso modello, stessa chiave — <b>6 successi e 4
    /// volte 404 «Function not found for account»</b>, col 404 restituito in 753 ms. Su quel
    /// provider un 404 isolato non prova niente, e con la regola vecchia la piattaforma avrebbe
    /// emesso una notifica critica «il modello non esiste più» ogni due giri.
    ///
    /// <para>Se qualcuno riportasse la soglia a 1, questo test cade — ed è il suo unico scopo.</para>
    /// </summary>
    [Fact]
    public void LaCONFERMA_richiedePIUdiUNgiro()
        => Assert.True(FleetOrchestratorWorker.ConfermaGuastoGiri >= 2,
            "Un 404 isolato è rumore misurato (4 su 10 su NVIDIA): la conferma deve richiedere ripetizione.");

    /// <summary>
    /// E non deve nemmeno essere così alta da non scattare mai: col tick a 15 minuti, tre giri sono
    /// 45 minuti — contro i sedici giorni del caso vero. Una soglia a dieci giri sarebbe due ore e
    /// mezza, ancora ragionevole; a cento sarebbe un giorno intero, cioè un allarme che arriva dopo
    /// che il danno è fatto. È lo stesso ragionamento di K33 su <c>StarvationMinDays</c>: alzare una
    /// soglia oltre la vita dell'oggetto misurato non rende severi, spegne.
    /// </summary>
    [Fact]
    public void LaCONFERMA_nonEcosiALTAdaNONscattareMAI()
        => Assert.True(FleetOrchestratorWorker.ConfermaGuastoGiri <= 12,
            "Col tick a 15 minuti, oltre 12 giri l'allarme arriverebbe dopo tre ore di comitato muto.");

    // ---------------------------------------------------------- l'isteresi, sul comportamento

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class UnusedReader : IFleetStateReader
    {
        public Task<FleetState> ReadAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// Il worker senza database: <c>DeclareCommitteeFaultAsync</c> vive tutto in memoria, quindi la
    /// stringa di connessione non viene mai aperta. Niente Testcontainers per una prova di logica.
    /// </summary>
    private static FleetOrchestratorWorker Worker()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o =>
            o.UseNpgsql("Host=127.0.0.1;Port=1;Database=mai-aperto;Username=x;Password=y"));
        var provider = services.BuildServiceProvider();
        return new FleetOrchestratorWorker(
            new UnusedReader(),
            provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>(),
            new FleetOptions { TickMinutes = 15 }.AsMonitor(),
            new CarryOptions().AsMonitor(),
            provider,
            NullLogger<FleetOrchestratorWorker>.Instance);
    }

    private static CommitteeVerdict GiroConNvidiaGiu() => new("abc", ByQuorum: false,
        [Guasto("Nvidia", 404), Valido("Groq", "abc"), Valido("Gemini", "abc")]);

    [Fact]
    public async Task ILNULLOcheCONTAdiPIU_unSOLO404_nonEunGUASTO()
    {
        var w = Worker();

        await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);

        var r = w.LastCommitteeFault!;
        Assert.Single(r.Sospetti);                 // il giro storto si VEDE...
        Assert.Empty(r.Confermati);                // ...ma non si chiama guasto
        Assert.False(r.QuorumIrraggiungibile);
        Assert.Equal(1, r.Serie["Nvidia"]);
    }

    [Fact]
    public async Task DOPOtreGIRIdiFILA_diventaUNguastoCONFERMATO()
    {
        var w = Worker();

        for (var i = 0; i < FleetOrchestratorWorker.ConfermaGuastoGiri; i++)
        {
            await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        }

        var r = w.LastCommitteeFault!;
        Assert.Equal(["Nvidia"], r.Confermati);
        Assert.Equal(FleetOrchestratorWorker.ConfermaGuastoGiri, r.Serie["Nvidia"]);
        // Restano due votanti su tre contro una soglia di due: il quorum è ancora possibile.
        Assert.False(r.QuorumIrraggiungibile);
    }

    /// <summary>
    /// <b>La proprietà che rende l'isteresi utile e non solo prudente:</b> un voto valido AZZERA la
    /// serie. È il caso misurato di NVIDIA — 6 successi su 10 — dove il guasto non si accumula mai
    /// perché il provider continua a rispondere.
    /// </summary>
    [Fact]
    public async Task UNvotoVALIDO_azzeraLaSERIE()
    {
        var w = Worker();
        await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        Assert.Equal(2, w.LastCommitteeFault!.Serie["Nvidia"]);

        // NVIDIA risponde: due terzi dei suoi tentativi vanno a buon fine, ed è il caso normale.
        await w.ValutaComitatoPerTestAsync(new CommitteeVerdict("abc", true,
            [Valido("Nvidia", "abc"), Valido("Groq", "abc"), Valido("Gemini", "abc")]), minValidVotes: 2);

        Assert.Empty(w.LastCommitteeFault!.Sospetti);
        Assert.False(w.LastCommitteeFault!.Serie.ContainsKey("Nvidia"));
    }

    /// <summary>
    /// Un'astensione per ALTRA causa (timeout, scelta fuori menù) non è prova né a favore né
    /// contro un guasto di configurazione: non deve né far salire la serie né azzerarla. Contarla
    /// in un verso o nell'altro sarebbe estrarre da quel voto un'informazione che non porta.
    /// </summary>
    [Fact]
    public async Task UNtimeout_nonMUOVEnullaINunSENSOoNELLaltro()
    {
        var w = Worker();
        await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        Assert.Equal(1, w.LastCommitteeFault!.Serie["Nvidia"]);

        await w.ValutaComitatoPerTestAsync(new CommitteeVerdict("abc", false,
            [Guasto("Nvidia", 503), Valido("Groq", "abc"), Valido("Gemini", "abc")]), minValidVotes: 2);

        Assert.Equal(1, w.LastCommitteeFault!.Serie["Nvidia"]);   // né 2, né sparita
    }

    /// <summary>
    /// [Revisione 2026-09-03] <b>Un giro a zero voti non guarisce nessuno.</b> Budget esaurito o
    /// comitato spento: il quadro precedente resta (dichiarando di essere più vecchio), la serie
    /// non si azzera, e al 404 successivo NON parte una seconda notifica critica.
    /// </summary>
    [Fact]
    public async Task ZEROvoti_nonCANCELLAlaCONFERMA_eNONriarmaLaNOTIFICA()
    {
        var w = Worker();
        for (var i = 0; i < FleetOrchestratorWorker.ConfermaGuastoGiri; i++)
        {
            await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        }
        Assert.Equal(["Nvidia"], w.LastCommitteeFault!.Confermati);

        await w.ValutaComitatoPerTestAsync(new CommitteeVerdict("abc", false, []), minValidVotes: 2);

        var r = w.LastCommitteeFault!;
        Assert.Equal(["Nvidia"], r.Confermati);                       // la conferma resta
        Assert.Equal(FleetOrchestratorWorker.ConfermaGuastoGiri, r.Serie["Nvidia"]);
        Assert.True(r.UltimoGiroSenzaVoti);                          // ...e il quadro dice di essere vecchio
    }

    /// <summary>
    /// [Revisione 2026-09-03] Un provider confermato che in QUESTO giro si astiene per altra causa
    /// (503, timeout) resta confermato: l'astensione non è una guarigione.
    /// </summary>
    [Fact]
    public async Task UNconfermatoCHEvaInTIMEOUT_restaCONFERMATO()
    {
        var w = Worker();
        for (var i = 0; i < FleetOrchestratorWorker.ConfermaGuastoGiri; i++)
        {
            await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        }

        await w.ValutaComitatoPerTestAsync(new CommitteeVerdict("abc", false,
            [Guasto("Nvidia", 503), Valido("Groq", "abc"), Valido("Gemini", "xyz")]), minValidVotes: 2);

        var r = w.LastCommitteeFault!;
        Assert.Empty(r.Sospetti);                                     // nessun «modello assente» in questo giro...
        Assert.Equal(["Nvidia"], r.Confermati);                       // ...ma il guasto confermato resta
    }

    /// <summary>
    /// [Revisione 2026-09-03] <b>Il nullo della conferma.</b> Un provider confermato guasto e poi
    /// TOLTO dal comitato (il rimedio che la notifica suggerisce) non è più fra i votanti: esce dalla
    /// diagnosi. Non deve restare «confermato» falsando i superstiti e dichiarando irraggiungibile
    /// un quorum che il comitato ha appena raggiunto.
    /// </summary>
    [Fact]
    public async Task UNconfermatoTOLTOdalCOMITATO_esceDALLAdiagnosi()
    {
        var w = Worker();
        for (var i = 0; i < FleetOrchestratorWorker.ConfermaGuastoGiri; i++)
        {
            await w.ValutaComitatoPerTestAsync(GiroConNvidiaGiu(), minValidVotes: 2);
        }
        Assert.Equal(["Nvidia"], w.LastCommitteeFault!.Confermati);

        // Nvidia rimossa dalla configurazione: il giro ha solo Groq e Gemini, e delibera.
        await w.ValutaComitatoPerTestAsync(new CommitteeVerdict("abc", true,
            [Valido("Groq", "abc"), Valido("Gemini", "abc")]), minValidVotes: 2);

        var r = w.LastCommitteeFault!;
        Assert.Empty(r.Confermati);
        Assert.False(r.QuorumIrraggiungibile);
    }

    /// <summary>
    /// Il caso vero di Groq — morto per sedici giorni — deve comunque arrivare a
    /// «quorum irraggiungibile»: l'isteresi doveva togliere i falsi allarmi, non l'allarme.
    /// </summary>
    [Fact]
    public async Task ILCASOvero_dueVOTANTImortiAlungo_arrivaAirraggiungibile()
    {
        var w = Worker();
        var giro = new CommitteeVerdict("abc", false,
            [Guasto("Nvidia", 410), Guasto("Groq", 404), Valido("Gemini", "abc")]);

        for (var i = 0; i < FleetOrchestratorWorker.ConfermaGuastoGiri; i++)
        {
            await w.ValutaComitatoPerTestAsync(giro, minValidVotes: 2);
        }

        var r = w.LastCommitteeFault!;
        Assert.Equal(["Groq", "Nvidia"], r.Confermati);
        Assert.True(r.QuorumIrraggiungibile);
    }
}
