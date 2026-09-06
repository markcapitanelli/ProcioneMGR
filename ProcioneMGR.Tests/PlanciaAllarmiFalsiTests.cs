using Procione;

namespace ProcioneMGR.Tests;

/// <summary>
/// Prove della correzione del 2026-09-06 sera: l'icona mandava «ProcioneMGR: guasto — Docker:
/// nessuna risposta entro 20s · il cluster non esiste · kind-apiproxy assente», e qualche minuto
/// dopo «rientrato», su una piattaforma che non aveva mai smesso di funzionare.
///
/// Un difetto, tre errori sovrapposti, e tutti e tre della stessa famiglia:
///   1. un TIMEOUT letto come un guasto;
///   2. una lista vuota, prodotta da quel timeout, letta come «non esiste niente»;
///   3. un fumetto sparato su una rilevazione sola, senza conferma.
/// </summary>
public class PlanciaAllarmiFalsiTests
{
    // =============================================================================================
    //  1. Un timeout non e' un guasto
    // =============================================================================================

    [Fact]
    public void Docker_che_non_risponde_entro_il_tetto_e_un_avviso_non_un_guasto()
    {
        // E' il caso vero: su questa macchina `docker info` sta fra 0,7 e 2,9 s, ma con la memoria
        // quasi esaurita sfora i 20. Non lo si e' misurato — e «non lo so» non e' «e' giu'».
        var c = Verdicts.Docker(ok: false, versione: "", codice: Proc.TimedOut,
                                diagnosi: "nessuna risposta entro 20s", tettoSecondi: 20);

        Assert.Equal(Level.Warn, c.Level);
        Assert.Contains("NON so", c.Detail);
    }

    [Fact]
    public void Docker_davvero_fermo_resta_un_guasto()
    {
        // Il complemento, e serve: se il timeout diventasse un avviso e ANCHE l'errore vero lo
        // diventasse, si sarebbe solo spostato il difetto — un rosso che non si accende mai.
        // Docker fermo risponde subito, con un codice d'uscita vero.
        var c = Verdicts.Docker(ok: false, versione: "", codice: 1,
                                diagnosi: "error during connect: il sistema non trova il file", tettoSecondi: 20);

        Assert.Equal(Level.Down, c.Level);
        Assert.Contains("error during connect", c.Detail);
    }

    [Fact]
    public void Docker_assente_dal_PATH_e_un_guasto_con_il_suo_messaggio()
    {
        var c = Verdicts.Docker(ok: false, versione: "", codice: Proc.Failed, diagnosi: "boh", tettoSecondi: 20);

        Assert.Equal(Level.Down, c.Level);
        Assert.Contains("PATH", c.Detail);
    }

    [Fact]
    public void Docker_vivo_e_in_ordine_e_dice_la_versione()
    {
        var c = Verdicts.Docker(ok: true, versione: "29.6.1", codice: 0, diagnosi: "", tettoSecondi: 20);

        Assert.Equal(Level.Ok, c.Level);
        Assert.Contains("29.6.1", c.Detail);
        Assert.Null(c.Fix);
    }

    // =============================================================================================
    //  2. I guardrail non devono fallire APERTI
    // =============================================================================================

    [Fact]
    public void Layout_sconosciuto_non_e_layout_None()
    {
        // `Which` riceve solo booleani, quindi non puo' distinguere «ho guardato e non c'e'
        // niente» da «non ho potuto guardare»: e' il CHIAMANTE a doverlo fare, e da oggi lo fa
        // (Probes passa Layout.Unknown quando la lista dei container non e' nota).
        // Qui si fissa la premessa che rende necessaria quella scelta: con due booleani falsi la
        // funzione dice «nessun assetto», che su una lettura fallita sarebbe falso.
        Assert.Equal(Layout.None, Verdicts.Which(dockerVivo: true, nodoKindSu: false, guscioComposeSu: false));

        // E il valore che il chiamante deve usare al suo posto esiste, ed e' diverso.
        Assert.NotEqual(Layout.None, Layout.Unknown);
    }

    // =============================================================================================
    //  3. Il fumetto vuole una conferma
    // =============================================================================================

    [Fact]
    public void Un_guasto_visto_una_volta_sola_non_si_annuncia()
    {
        // La prima rilevazione «guasto» vale come avviso: il colore dell'icona diventa rosso
        // subito — e' l'ultima cosa letta — ma non si interrompe l'utente per una misura sola.
        Assert.Equal(Level.Warn, Verdicts.LivelloConfermato(Level.Down, downDiFila: 1));
        Assert.Equal(Level.Down, Verdicts.LivelloConfermato(Level.Down, downDiFila: 2));
        Assert.Equal(Level.Down, Verdicts.LivelloConfermato(Level.Down, downDiFila: 9));

        // Cio' che non e' un guasto passa intatto: la conferma vale solo verso il rosso.
        Assert.Equal(Level.Ok, Verdicts.LivelloConfermato(Level.Ok, downDiFila: 0));
        Assert.Equal(Level.Warn, Verdicts.LivelloConfermato(Level.Warn, downDiFila: 0));
    }

    [Fact]
    public void La_sequenza_che_disturbava_il_proprietario_ora_e_muta()
    {
        // Riproduzione esatta di cio' che e' successo: piattaforma sana, una rilevazione storta
        // (Docker che sfora il tetto sotto carico), poi di nuovo sana. Prima: «guasto» e, qualche
        // minuto dopo, «rientrato». Adesso: silenzio, perche' non e' mai stato un guasto.
        var annunciato = (Level?)Level.Ok;
        var fumetti = 0;

        foreach (var (letto, diFila) in new[] { (Level.Ok, 0), (Level.Down, 1), (Level.Ok, 0), (Level.Ok, 0) })
        {
            var confermato = Verdicts.LivelloConfermato(letto, diFila);
            if (confermato == annunciato) continue;
            if (Verdicts.Fumetto(annunciato, confermato) is not null) fumetti++;
            annunciato = confermato;
        }

        Assert.Equal(0, fumetti);
    }

    [Fact]
    public void Un_guasto_VERO_si_annuncia_lo_stesso_una_volta_sola()
    {
        // Il complemento indispensabile: la conferma non deve trasformarsi in silenzio. Un guasto
        // che persiste va annunciato — una volta — e il rientro pure.
        var annunciato = (Level?)Level.Ok;
        var titoli = new List<string>();

        // sano, poi guasto che dura quattro rilevazioni, poi rientro.
        foreach (var (letto, diFila) in new[]
                 {
                     (Level.Ok, 0), (Level.Down, 1), (Level.Down, 2), (Level.Down, 3), (Level.Down, 4), (Level.Ok, 0),
                 })
        {
            var confermato = Verdicts.LivelloConfermato(letto, diFila);
            if (confermato == annunciato) continue;
            if (Verdicts.Fumetto(annunciato, confermato) is { } f) titoli.Add(f.Titolo);
            annunciato = confermato;
        }

        // Esattamente due: il guasto (alla seconda conferma) e il rientro. Mai uno per rilevazione.
        Assert.Equal(2, titoli.Count);
        Assert.Contains("guasto", titoli[0]);
        Assert.Contains("rientrat", titoli[1]);
    }

    [Fact]
    public void Un_guasto_che_dura_non_produce_un_fumetto_al_minuto()
    {
        // Venti rilevazioni consecutive di guasto: un solo annuncio. E' la disciplina di
        // watchdog.ps1 — si avvisa sulle transizioni — applicata all'icona.
        var annunciato = (Level?)Level.Ok;
        var fumetti = 0;

        for (var i = 1; i <= 20; i++)
        {
            var confermato = Verdicts.LivelloConfermato(Level.Down, i);
            if (confermato == annunciato) continue;
            if (Verdicts.Fumetto(annunciato, confermato) is not null) fumetti++;
            annunciato = confermato;
        }

        Assert.Equal(1, fumetti);
    }
}
