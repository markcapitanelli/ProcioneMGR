namespace ProcioneMGR.Services.Ensemble;

/// <summary>
/// [K48, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Chi sta riscrivendo questa corsia, e perché.</b>
///
/// <para><b>Il fatto che l'ha resa necessaria.</b> Riscrivere la configurazione di una corsia è
/// l'azione <b>meno reversibile</b> della piattaforma: <c>EnsembleStates</c> ha un solo
/// <c>ConfigurationJson</c> e un solo <c>LastUpdatedUtc</c>, quindi la configurazione precedente non
/// è conservata da nessuna parte e, una volta sovrascritta, non è ricostruibile guardando la corsia
/// dopo. Eppure fino a oggi la si poteva fare <b>senza lasciare traccia</b>.</para>
///
/// <para><b>E gli scrittori sono dieci, non tre.</b> Le guardie del filone K erano state messe sulle
/// tre porte di <i>schieramento</i> — click F5, braccio della flotta, aggiunta grigia da
/// <c>/ensemble</c> — ma <c>EnsembleManager.UpdateConfigurationAsync</c> ha dieci chiamanti:
/// <c>GreyDeployer</c>, <c>PipelineApplier</c>, tre da <c>/ensemble</c>, due da <c>/bot</c>,
/// <c>/trading</c>, i due backfill (K37 e J9) e <c>/regimes</c>. Una guardia su tre porte non copre
/// un problema che ne ha dieci — ed è il motivo per cui i difetti di questo filone si sono ripetuti
/// da porte sempre diverse.</para>
///
/// <para><b>Il caso peggiore è l'ultimo.</b> <c>Regimes.razor</c> inietta l'<c>IEnsembleManager</c>
/// <b>non keyed</b>, che il composition root risolve alla <b>corsia 0</b>: quella pagina scrive
/// sempre sulla corsia 0 qualunque corsia l'operatore stia guardando, e non lasciava traccia da
/// nessuna parte. Con questo contesto obbligatorio quella scrittura deve dichiararsi, e diventa
/// visibile.</para>
///
/// <para><b>Perché obbligatorio e non opzionale con default.</b> Un parametro con default è un
/// parametro omissibile, e in sei mesi il buco torna: chi aggiunge l'undicesimo scrittore non lo
/// passa e nessuno se ne accorge. Con un parametro richiesto, l'undicesimo scrittore <b>non
/// compila</b> finché non dice chi è — che è l'unica forma di guardia che non si dimentica.</para>
/// </summary>
/// <param name="Source">
/// Chi scrive, in forma stabile e confrontabile: il nome della porta, non una frase.
/// Vedi le costanti di <see cref="ConfigWriteSources"/>.
/// </param>
/// <param name="Reason">
/// Perché, in italiano e per un umano che legge l'audit fra tre mesi. Vuoto non è ammesso: una
/// scrittura senza motivo è esattamente ciò che questo tipo esiste per impedire.
/// </param>
public sealed record ConfigWriteContext(string Source, string Reason)
{
    /// <summary>Valida alla costruzione: un contesto vuoto sarebbe una dichiarazione che non dichiara.</summary>
    public static ConfigWriteContext Create(string source, string reason)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("La fonte della scrittura non può essere vuota.", nameof(source));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Il motivo della scrittura non può essere vuoto.", nameof(reason));
        return new ConfigWriteContext(source.Trim(), reason.Trim());
    }
}

/// <summary>
/// [K48] Le porte note che riscrivono una corsia. Costanti e non stringhe sparse: servono a poter
/// <b>contare</b> le scritture per porta, che è la domanda «da dove è stata toccata questa corsia»
/// — e con stringhe libere quella domanda non ha risposta.
/// </summary>
public static class ConfigWriteSources
{
    public const string GreyDeployer = "grey-deployer";        // click F5 e braccio della flotta
    public const string PipelineApplier = "auto-apply";        // impronta 0..2
    public const string EnsemblePage = "/ensemble";
    public const string BotPage = "/bot";
    public const string TradingPage = "/trading";
    public const string RegimesPage = "/regimes";
    public const string Backfill = "backfill";                 // K37 (provenienza) e J9 (frequenza attesa)
    public const string EnsembleManagerInternal = "ensemble-manager";  // ribilanciamento, on/off
}
