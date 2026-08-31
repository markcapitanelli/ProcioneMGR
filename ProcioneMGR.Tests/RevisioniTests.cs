using Procione;
using ProcioneMGR.Services.Health;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K1, PRD autonomia-piena 2026-08-31] Prove della sonda «revisione viva vs HEAD».
///
/// <para>Il difetto che rende visibile: la notte del 2026-08-30 il guscio girava indietro di 7
/// commit e la plancia di 13, e nessuna superficie lo diceva. Il sintomo era una Home che dichiarava
/// «un run è in corso adesso» con zero run in corso — cioè esattamente il difetto che il commit
/// mancante correggeva.</para>
///
/// <para><b>Livello 1 (riferimento indipendente):</b> l'estrazione dello sha esiste in DUE
/// implementazioni che non condividono una riga — <c>BuildRevision.Extract</c> nel guscio e
/// <c>Revisions.DaInformationalVersion</c> nella plancia, due processi distinti — e devono
/// rispondere identicamente su ogni ingresso. È il secondo modo, calcolato diversamente, di sapere
/// la risposta giusta.</para>
///
/// <para><b>Livello 2 (il rumore non deve accendere niente):</b> nessun ingresso spazzatura deve
/// produrre uno sha inventato, e — la parte che conta — il verdetto non deve MAI dire «allineato»
/// quando la revisione non si è potuta leggere. Un piano troppo vecchio per dichiarare la propria
/// revisione è precisamente un piano molto indietro.</para>
/// </summary>
public class RevisioniTests
{
    // Valori ancorati a misure reali del 2026-08-30/31, non inventati.
    private const string ShaReale = "9e4b69007c261a5f8d6099b5c5672a6440bb678d";
    private const string ImmagineReale = "ghcr.io/markcapitanelli/procionemgr-trading:local-850290e5";

    // =============================================================================================
    //  Livello 1 — le due implementazioni devono coincidere, su tutto
    // =============================================================================================

    [Theory]
    [InlineData("1.0.0+" + ShaReale, ShaReale)]                       // il caso vero, misurato
    [InlineData("1.0.0+B410C76357EF032447739869CCEC6EFC347BAFCE",     // maiuscolo: normalizzato
                "b410c76357ef032447739869ccec6efc347bafce")]
    [InlineData("1.0.0+9e4b690", "9e4b690")]                          // sha abbreviato: ammesso
    [InlineData("1.0.0", null)]                                       // nessun metadato di build
    [InlineData("1.0.0+", null)]                                      // '+' senza nulla dopo
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("1.0.0+main", null)]                                  // un ramo non è una revisione
    [InlineData("1.0.0+build.42", null)]                              // un numero di CI nemmeno
    [InlineData("1.0.0+9e4b69", null)]                                // 6 cifre: troppo corto per essere uno sha
    [InlineData("1.0.0+" + ShaReale + "0", null)]                     // 41 cifre: non è uno sha
    [InlineData("1.0.0+9e4b690z", null)]                              // 'z' non è esadecimale
    public void Estrazione_LeDueImplementazioniDannoLaStessaRisposta(string? versione, string? atteso)
    {
        var dalGuscio = BuildRevision.Extract(versione);
        var dallaPlancia = Revisions.DaInformationalVersion(versione);

        Assert.Equal(atteso, dalGuscio);
        Assert.Equal(atteso, dallaPlancia);
        // La ridondanza è il punto: due processi diversi devono leggere lo stesso timbro allo
        // stesso modo, altrimenti il confronto fra piani parla di due cose.
        Assert.Equal(dalGuscio, dallaPlancia);
    }

    // =============================================================================================
    //  Le altre due sorgenti
    // =============================================================================================

    [Theory]
    [InlineData(ImmagineReale, "850290e5")]                                   // il caso vero
    [InlineData("procionemgr-trading:local-9A3E8DBE", "9a3e8dbe")]            // normalizzato
    [InlineData("ghcr.io/x/y:latest", null)]                                  // non dichiara l'origine
    [InlineData("ghcr.io/x/y:local-nonuno", null)]                            // 'local-' ma non uno sha
    [InlineData("ghcr.io/x/y", null)]                                         // nessun tag
    [InlineData("ghcr.io/x/y:", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TagImmagine_SoloLaFormaLocalSha(string? immagine, string? atteso) =>
        Assert.Equal(atteso, Revisions.DaTagImmagine(immagine));

    [Theory]
    [InlineData("{\"status\":\"ok\",\"revision\":\"" + ShaReale + "\"}", ShaReale)]
    [InlineData("{\"revision\":\"" + ShaReale + "\",\"status\":\"ok\"}", ShaReale)]  // ordine indifferente
    [InlineData("{\"status\":\"ok\"}", null)]                                        // guscio precedente a K1
    [InlineData("{\"status\":\"ok\",\"revision\":null}", null)]                      // timbro assente nel binario
    [InlineData("{\"status\":\"ok\",\"revision\":\"ramo-mio\"}", null)]              // non è uno sha
    [InlineData("{\"status\":\"ok\",\"revision\":123}", null)]                       // tipo sbagliato
    [InlineData("non è json", null)]
    [InlineData("[1,2,3]", null)]                                                    // json valido, non un oggetto
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CorpoHealth_LeggeSoloUnaRevisioneVera(string? corpo, string? atteso) =>
        Assert.Equal(atteso, Revisions.DaCorpoHealth(corpo));

    // =============================================================================================
    //  Livello 2 — il verdetto
    // =============================================================================================

    [Fact]
    public void Verdetto_AllineatoQuandoIlContenutoCoincide()
    {
        var c = Verdicts.Revisione(new Piano("guscio", ShaReale, "fonte"), avanti: 0, indietro: 0, contenutoDiverso: false);

        Assert.Equal(Level.Ok, c.Level);
        Assert.Contains("9e4b6900", c.Detail);
    }

    [Fact]
    public void Verdetto_IlSoloPinNonRendeStantioNessuno()
    {
        // Il caso che, senza l'esclusione del file del pin, resterebbe rosso PER SEMPRE dopo ogni
        // deploy riuscito: il lavoro `deploy` committa il pin da solo, quindi master è sempre
        // almeno un commit avanti al motore appena schierato. Un allarme che non può rientrare
        // smette di essere letto e si porta dietro anche quelli veri.
        var c = Verdicts.Revisione(new Piano("motore", ShaReale, "tag"), avanti: 0, indietro: 1, contenutoDiverso: false);

        Assert.Equal(Level.Ok, c.Level);
        Assert.Contains("solo il pin", c.Detail);
    }

    [Fact]
    public void Verdetto_StantioQuandoIlContenutoDifferisce()
    {
        var c = Verdicts.Revisione(new Piano("plancia", ShaReale, "assembly"), avanti: 0, indietro: 13,
                                   contenutoDiverso: true, fix: "ricompila la plancia");

        Assert.Equal(Level.Warn, c.Level);
        Assert.Contains("INDIETRO di 13", c.Detail);
        Assert.Equal("ricompila la plancia", c.Fix);
    }

    [Fact]
    public void Verdetto_PianoAvanti_NonSiChiamaINDIETRO()
    {
        // REGRESSIONE, trovata al livello 4 il 2026-08-31: la prima stesura contava solo
        // `rev-list --count <sha>..HEAD` e su una plancia compilata da un ramo AVANTI a master
        // stampava «INDIETRO di 0 commit». Un numero che dice zero accanto alla parola sbagliata
        // insegna a non leggere la riga. Nessun test lo aveva visto: la funzione era corretta per
        // ogni ingresso che le avevo dato, e sbagliata per quello che il mondo le ha dato.
        var c = Verdicts.Revisione(new Piano("plancia", ShaReale, "assembly"), avanti: 1, indietro: 0,
                                   contenutoDiverso: true, fix: "ricompila la plancia");

        Assert.Equal(Level.Warn, c.Level);
        Assert.Contains("AVANTI di 1", c.Detail);
        Assert.DoesNotContain("INDIETRO", c.Detail);
        // E il rimedio non dev'essere quello dello stantio: qui non manca niente, c'è in più.
        Assert.DoesNotContain("ricompila", c.Fix ?? "");
    }

    [Fact]
    public void Verdetto_RevisioneNonLetta_NONdiceAllineato()
    {
        // Il cuore del livello 2. Un guscio troppo vecchio per dichiarare la propria revisione è
        // proprio quello che si vuole scoprire: se questa riga dicesse verde, la sonda nata contro
        // i controlli che rassicurano ne diventerebbe uno.
        var c = Verdicts.Revisione(
            new Piano("guscio", null, "/health del processo vivo", "il guscio non risponde"),
            avanti: null, indietro: null, contenutoDiverso: null);

        Assert.NotEqual(Level.Ok, c.Level);
        Assert.Contains("NON dichiarata", c.Detail);
        Assert.Contains("il guscio non risponde", c.Detail);
    }

    [Fact]
    public void Verdetto_ShaSconosciutoAlRepo_NONdiceAllineato()
    {
        // Compilato da un ramo mai mergiato e potato: `git rev-list` fallisce. Dedurne «zero
        // commit di scarto» sarebbe inventare un allineamento.
        var c = Verdicts.Revisione(new Piano("guscio", ShaReale, "fonte"), avanti: null, indietro: null,
                                   contenutoDiverso: null);

        Assert.NotEqual(Level.Ok, c.Level);
        Assert.Contains("sconosciuta al repository", c.Detail);
    }

    [Fact]
    public void Verdetto_DiffInErrore_NONdiceAllineato()
    {
        // `git diff --quiet` con un codice diverso da 0/1 è un errore, non un «identico».
        var c = Verdicts.Revisione(new Piano("motore", ShaReale, "tag"), avanti: 0, indietro: 3,
                                   contenutoDiverso: null);

        Assert.NotEqual(Level.Ok, c.Level);
    }

    [Fact]
    public void Verdetto_TutteLeRigheStannoNelloStessoGruppo() =>
        Assert.All(
            new[]
            {
                Verdicts.Revisione(new Piano("guscio", ShaReale, "f"), 0, 0, false),
                Verdicts.Revisione(new Piano("plancia", null, "f", "muta"), null, null, null),
                Verdicts.Revisione(new Piano("motore", ShaReale, "f"), 0, 5, true),
            },
            c => Assert.Equal("revisioni", c.Group));
}
