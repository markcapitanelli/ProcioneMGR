namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// [F5 / PRD memoria-caccia 2026-08-14] IL giudice della fascia grigia — l'unica definizione,
/// promossa qui da <c>FleetStateReader.IsGrey</c> quando i consumatori sono diventati tre
/// (lettore di flotta, GreyDeployer, archivio candidati / assemblaggio ensemble). Due soglie in
/// due posti sarebbero due verdetti sulla stessa riga: il difetto già pagato con la doppia
/// regola dell'ampiezza finestra in D2.b.
///
/// Grigio = bocciato per SOLA finestra corta (ContoTrade "Solo N trade…" oppure DSR in
/// [<see cref="DsrFloor"/>–<see cref="DsrCeiling"/>)) CON Sharpe holdout positivo e almeno un
/// trade: un grigio che perde non è grigio, è bocciato nel merito.
///
/// <para>Le due porte non sono equivalenti in pratica: <b>quella del DSR non si è mai aperta</b>
/// (misurato il 2026-08-21, vedi <see cref="DsrFloor"/>). Ogni grigio esistente è passato dalla
/// finestra corta.</para>
/// </summary>
public static class GreyZone
{
    /// <summary>
    /// Pavimento F5: DSR in [DsrFloor, DsrCeiling) = fascia grigia.
    ///
    /// <para><b>[F5b, 2026-08-20] Portato da 0,80 a 0,70, e non per gusto: a 0,80 questa porta era
    /// MURATA.</b> Misurato su 30 giorni e 11.496 candidati validati (campagne 17 e 18): 402 sono
    /// arrivati al gate DSR e il <b>massimo prodotto è stato 0,773</b> — sotto il vecchio pavimento.
    /// Non «raramente in banda»: <i>mai</i>, perché il DSR è deflazionato su ~8.160 combinazioni per
    /// run e SR* cresce con la dimensione della ricerca. Ogni grigio arrivava quindi dall'unica porta
    /// rimasta, la finestra corta. Era un gate senza strumento, della stessa famiglia della lezione
    /// del 2026-07-28: «dove si legge il numero, e può esistere in questo assetto?».</para>
    ///
    /// <para><b>[RETTIFICA, 2026-08-21] La misura che giustificava questo cambio contava RIGHE, non
    /// candidati — e il pavimento a 0,70 non ha ammesso nessuno.</b> Qui sopra era scritto «a 0,70
    /// entrano 49 candidati al mese, 24 dei quali finirebbero schierati». Verificato su
    /// <c>ResearchCandidates</c>: quelle 49 sono <b>righe</b>, e le righe sono ~19 volte i candidati
    /// perché ogni caccia notturna ri-registra la stessa griglia. I <b>CandidateKey distinti</b> mai
    /// finiti in banda [0,70; 0,95) in tutta la storia dell'archivio sono <b>sei</b>, e due soli
    /// (EventTrigger DOT/USDT 1h e ATOM/USDT 1h) producono 42 di quelle righe, una per notte. Il
    /// «24 schierati» non è derivabile da sei distinti: era un artefatto di conteggio, e va detto.</para>
    ///
    /// <para>Peggio: quei due erano <b>usciti dalla banda undici giorni prima</b> che il pavimento
    /// scendesse a prenderli, e non per un cambio di prestazione. Il 2026-08-09 è entrata la
    /// correzione del conteggio tentativi della deflazione, e il loro DSR è passato da 0,7474 a
    /// 0,6590 e da 0,7282 a 0,6388 — salto identico di −0,089 — con <b>Sharpe holdout e numero di
    /// trade invariati</b>. Da quel giorno il DSR massimo mai osservato è <b>0,659</b>: zero
    /// candidati raggiungono 0,70. La porta è ancora murata, solo un po' più in basso.</para>
    ///
    /// <para><b>Perché il valore resta 0,70 lo stesso.</b> Non perché la misura reggesse, ma perché
    /// non c'è ragione di rimetterlo a 0,80: a entrambe le altezze la porta è chiusa, e 0,70 è
    /// difendibile per conto proprio — il DSR è la probabilità che l'edge sia reale dopo la
    /// deflazione, e a 0,70 si ammette ciò che è probabilmente vero ma non provato, che è la
    /// definizione della fascia grigia. Scendere ancora ammetterebbe evidenza progressivamente più
    /// debole (0,50 è il lancio di una moneta). Il tetto resta 0,95, la soglia di sopravvivenza: un
    /// grigio non è mai un sopravvissuto.</para>
    ///
    /// <para><b>La conseguenza operativa, da non dimenticare.</b> La fascia grigia ha due porte
    /// dichiarate e <b>una sola esiste in pratica</b>: la finestra corta. Ogni grigio che questa
    /// piattaforma abbia mai prodotto è passato di lì. Chi volesse davvero aprire la porta DSR deve
    /// agire sul numeratore — meno combinazioni per run, o un edge più forte — non sulla soglia:
    /// abbassare il pavimento per far entrare qualcuno è la stessa mossa del ridurre la griglia per
    /// spostare un gate, con un'altra maschera. E <b>non si ricostruisce l'archivio</b> di
    /// <c>/research</c> credendo di sbloccarla: le 49 righe in banda sono tutte precedenti alla
    /// correzione del 2026-08-09 e verrebbero promosse su un DSR già invalidato di ~0,09.</para>
    /// </summary>
    public const double DsrFloor = 0.70;

    /// <summary>Il tetto coincide con la soglia di sopravvivenza del gate DSR.</summary>
    public const double DsrCeiling = 0.95;

    /// <summary>
    /// Prefisso del RejectReason del gate ContoTrade ("Solo N trade in holdout (&lt; M)"):
    /// bocciatura per finestra corta, non nel merito. Confronto Ordinal deliberato — il testo
    /// è prodotto dal nostro stesso codice, non da input libero.
    /// </summary>
    public const string ShortWindowRejectPrefix = "Solo ";

    public static bool IsGrey(ValidatedCandidate candidate) =>
        !candidate.Survived
        && candidate.HoldoutSharpe > 0m
        && candidate.HoldoutTrades > 0
        && ((candidate.RejectReason?.StartsWith(ShortWindowRejectPrefix, StringComparison.Ordinal) ?? false)
            || candidate.DeflatedSharpe is >= DsrFloor and < DsrCeiling);
}
