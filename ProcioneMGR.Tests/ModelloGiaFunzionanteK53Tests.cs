using ProcioneMGR.Services.Llm;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K53, 2026-09-02] <b>La prova batte l'indovinello: il selettore automatico non deve riportare la
/// configurazione su un modello morto.</b>
///
/// <para><b>Il fatto.</b> <see cref="ModelAutoSelector"/> sceglie per euristica sul NOME, e il nome
/// non dice se l'account può invocare quel modello. Per NVIDIA la prima preferenza è
/// «llama + instruct + 70b», e nel catalogo del 2026-09-02 — 82 modelli — esiste <b>un solo</b>
/// candidato che la soddisfa: <c>nvidia/llama-3.1-nemotron-70b-instruct</c>, che risponde
/// <c>404 Function not found for account</c> (misurato). Cioè il pilota automatico riportava la
/// configurazione esattamente sul modello morto ogni volta che qualcuno apriva il pannello, e la
/// pagina lo annunciava come una riparazione.</para>
///
/// <para>La correzione: <c>LlmUsageRecords</c> registra solo le chiamate <b>riuscite</b>, quindi un
/// modello che ha prodotto token è la prova che quell'account può invocarlo. Quella prova precede
/// qualunque euristica.</para>
/// </summary>
public class ModelloGiaFunzionanteK53Tests
{
    /// <summary>Il catalogo NVIDIA vero del 2026-09-02, ridotto ai nomi che contano per la prova.</summary>
    private static readonly string[] CatalogoNvidia =
    [
        "meta/llama-3.2-90b-vision-instruct",
        "nvidia/llama-3.1-nemotron-70b-instruct",   // <- l'unico che soddisfa l'euristica, e dà 404
        "nvidia/nemotron-3-super-120b-a12b",        // <- quello che risponde davvero
        "nvidia/nemotron-3.5-lightning-30b-a3b",
        "openai/gpt-oss-120b",
    ];

    [Fact]
    public void SENZAprova_lEURISTICAsceglieIlMODELLOmorto()
    {
        // Questo test NON descrive il comportamento voluto: fissa il difetto, così se qualcuno
        // togliesse la prova dal chiamante si vedrebbe subito dove si ricade.
        Assert.Equal("nvidia/llama-3.1-nemotron-70b-instruct",
            ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia));
    }

    [Fact]
    public void CONlaPROVA_vinceIlMODELLOcheHAgiaRISPOSTO()
    {
        var provati = new[] { "nvidia/nemotron-3-super-120b-a12b", "nvidia/nemotron-3.5-lightning-30b-a3b" };

        Assert.Equal("nvidia/nemotron-3-super-120b-a12b",
            ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia, provati));
    }

    /// <summary>L'ordine dei provati conta: il più recente per primo, ed è quello che deve vincere.</summary>
    [Fact]
    public void FRAdueMODELLIprovati_vinceIlPIUrecente()
    {
        var provati = new[] { "nvidia/nemotron-3.5-lightning-30b-a3b", "nvidia/nemotron-3-super-120b-a12b" };

        Assert.Equal("nvidia/nemotron-3.5-lightning-30b-a3b",
            ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia, provati));
    }

    /// <summary>
    /// <b>Il nullo.</b> Senza prova si torna esattamente alle euristiche: la novità non deve
    /// cambiare il comportamento di chi non ha uno storico, altrimenti sarebbe una regressione
    /// mascherata da miglioramento.
    /// </summary>
    [Fact]
    public void ILNULLO_senzaPROVA_siTORNAalleEURISTICHE()
    {
        var euristica = ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia);

        Assert.Equal(euristica, ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia, null));
        Assert.Equal(euristica, ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia, []));
    }

    /// <summary>
    /// Un modello che ha risposto ma NON è più in catalogo non può essere scelto: è sparito. È il
    /// caso di <c>meta/llama-3.3-70b-instruct</c>, che ha risposto fino al 25/08 ed è andato in
    /// end-of-life il 26/08 — riproporlo sarebbe la stessa trappola al contrario.
    /// </summary>
    [Fact]
    public void UNmodelloUSCITOdalCATALOGO_nonSiRIPROPONE()
    {
        var provati = new[] { "meta/llama-3.3-70b-instruct", "nvidia/nemotron-3-super-120b-a12b" };

        Assert.Equal("nvidia/nemotron-3-super-120b-a12b",
            ModelAutoSelector.Pick(AiProviders.Nvidia, CatalogoNvidia, provati));
    }

    /// <summary>
    /// E la prova non scavalca il filtro di FORMA: un modello che ha risposto ma è di tipo
    /// non-chat resta la scelta sbagliata. (Un embedding <i>risponde</i> a una richiesta, quindi
    /// può benissimo comparire fra i «già funzionanti».)
    /// </summary>
    [Fact]
    public void LaPROVAnonSCAVALCAilFILTROdiFORMA()
    {
        string[] catalogo = ["nvidia/nv-embedqa-mistral-7b-v2", "nvidia/nemotron-3-super-120b-a12b"];
        var provati = new[] { "nvidia/nv-embedqa-mistral-7b-v2", "nvidia/nemotron-3-super-120b-a12b" };

        Assert.Equal("nvidia/nemotron-3-super-120b-a12b",
            ModelAutoSelector.Pick(AiProviders.Nvidia, catalogo, provati));
    }

    /// <summary>
    /// [K53] Il filtro di forma diceva <c>"embedding"</c>, e nel catalogo NVIDIA vero del
    /// 2026-09-02 <b>nessun</b> modello di embedding si chiama così: sono <c>embed-qa</c>,
    /// <c>nv-embedqa</c>, <c>arctic-embed</c>, <c>nemotron-3-embed</c>. Un filtro scritto sulla
    /// parola del dominio invece che sui nomi che esistono — sembrava coprire una categoria e non
    /// ne copriva un caso solo. Questi sono i nomi presi dall'elenco reale.
    /// </summary>
    [Theory]
    [InlineData("nvidia/embed-qa-4")]
    [InlineData("nvidia/nv-embedqa-mistral-7b-v2")]
    [InlineData("nvidia/llama-3.2-nv-embedqa-1b-v1")]
    [InlineData("nvidia/nemotron-3-embed-1b")]
    [InlineData("snowflake/arctic-embed-l")]
    [InlineData("nvidia/llama-3.2-nemoretriever-1b-vlm-embed-v1")]
    [InlineData("nvidia/nemotron-4-340b-reward")]
    [InlineData("nvidia/nemotron-parse")]
    [InlineData("nvidia/ai-synthetic-video-detector")]
    public void INOMIveriDEImodelliNONdaCHAT_vengonoSCARTATI(string nonChat)
    {
        string[] catalogo = [nonChat, "nvidia/nemotron-3-super-120b-a12b"];

        Assert.Equal("nvidia/nemotron-3-super-120b-a12b",
            ModelAutoSelector.Pick(AiProviders.Nvidia, catalogo));
    }

    /// <summary>
    /// <b>Il nullo del filtro:</b> allargare i frammenti non deve mangiarsi i modelli di chat veri.
    /// Se «embed» prendesse anche questi, la correzione avrebbe sostituito un difetto con un altro.
    /// </summary>
    [Theory]
    [InlineData("nvidia/nemotron-3-super-120b-a12b")]
    [InlineData("nvidia/nemotron-3.5-lightning-30b-a3b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("meta/llama-3.3-70b-instruct")]
    [InlineData("mistralai/mistral-large-2-instruct")]
    [InlineData("moonshotai/kimi-k3")]
    [InlineData("deepseek-ai/deepseek-v4-pro-0813")]
    [InlineData("google/gemma-4-31b-it")]
    public void ILNULLOdelFILTRO_iMODELLIdiCHATveriSOPRAVVIVONO(string chat)
        => Assert.Equal(chat, ModelAutoSelector.Pick(AiProviders.Nvidia, [chat]));
}
