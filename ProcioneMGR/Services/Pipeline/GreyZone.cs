namespace ProcioneMGR.Services.Pipeline;

/// <summary>
/// [F5 / PRD memoria-caccia 2026-08-14] IL giudice della fascia grigia — l'unica definizione,
/// promossa qui da <c>FleetStateReader.IsGrey</c> quando i consumatori sono diventati tre
/// (lettore di flotta, GreyDeployer, archivio candidati / assemblaggio ensemble). Due soglie in
/// due posti sarebbero due verdetti sulla stessa riga: il difetto già pagato con la doppia
/// regola dell'ampiezza finestra in D2.b.
///
/// Grigio = bocciato per SOLA finestra corta (ContoTrade "Solo N trade…" oppure DSR in
/// [0,80–0,95)) CON Sharpe holdout positivo e almeno un trade: un grigio che perde non è
/// grigio, è bocciato nel merito.
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
    /// <para><b>Che cosa cambia davvero, misurato prima di cambiarlo.</b> A 0,70 entrano 49
    /// candidati al mese, e <b>24 di loro finirebbero effettivamente schierati</b> (il pool grigio si
    /// ordina per Sharpe walk-forward e ne prende i primi a riempire i posti liberi). Non è cosmetico,
    /// ed è un miglioramento di QUALITÀ delle prove, non un allentamento: portano <b>25,2 trade
    /// medi</b> contro i <b>10,7</b> dei bocciati per finestra corta, cioè più del doppio delle
    /// osservazioni, a fronte di uno Sharpe walk-forward più basso (1,10 contro 1,43). È esattamente
    /// lo scambio che questa piattaforma dovrebbe volere: la sua storia intera dice che i campioni
    /// sottili con Sharpe alto non sopravvivono al forward test.</para>
    ///
    /// <para><b>Perché 0,70 e non meno.</b> Il DSR è la probabilità che l'edge sia reale dopo la
    /// deflazione: a 0,70 si ammette ciò che è probabilmente vero ma non provato, che è la
    /// definizione della fascia grigia. Scendendo si ammette evidenza progressivamente più debole
    /// (a 0,60 entrerebbero 158 candidati, a 0,50 duecentootto, e 0,50 è il lancio di una moneta).
    /// Il tetto resta 0,95, cioè la soglia di sopravvivenza: un grigio non è mai un sopravvissuto.</para>
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
