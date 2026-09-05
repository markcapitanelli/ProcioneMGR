namespace ProcioneMGR.Data;

/// <summary>
/// [2026-09-05] <b>Un episodio del forward test del carry, persistito.</b> Fino a oggi il carry
/// teneva lo stato per simbolo in memoria e il funding «incassato» veniva solo azzerato all'apertura:
/// a ogni rischieramento del pod (uno per merge) i sei simboli «riaprivano» e nulla di misurabile
/// sopravviveva — il pannello diceva «carry Paper ATTIVO», che è un interruttore, non una misura.
/// Il carry è l'unica classe di edge misurata positiva (5,5-11,9 %/anno, doc 30) e non aveva un
/// forward test leggibile.
///
/// <para>Una riga per episodio (apertura → chiusura) per simbolo e modalità. Il funding si accredita
/// a ogni evento nuovo mentre la posizione è aperta: lo short perp incassa <c>rate × nozionale</c>
/// quando il tasso è positivo e paga quando è negativo. I costi sono quelli del modello del backtest
/// (quattro fill, due gambe), fissati all'apertura, così episodio vivo e backtest si confrontano
/// nella stessa unità.</para>
/// </summary>
public class CarryLedgerEntry
{
    public int Id { get; set; }

    /// <summary>Simbolo nella forma del carry (es. «BTC/USDT»).</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>«Paper» o «Testnet». Mai Live: il carry non ha quel valore per costruzione.</summary>
    public string Mode { get; set; } = string.Empty;

    public DateTime OpenedUtc { get; set; }

    /// <summary><c>null</c> = episodio ancora aperto: è la riga da cui si ripristina lo stato al riavvio.</summary>
    public DateTime? ClosedUtc { get; set; }

    /// <summary>Nozionale per gamba in valuta quote.</summary>
    public decimal NotionalQuote { get; set; }

    /// <summary>Funding annualizzato (%) che ha fatto aprire.</summary>
    public decimal EntryAnnualizedPercent { get; set; }

    /// <summary>Funding annualizzato (%) che ha fatto chiudere.</summary>
    public decimal? ExitAnnualizedPercent { get; set; }

    /// <summary>Eventi di funding accreditati mentre la posizione era aperta.</summary>
    public int FundingEventsAccrued { get; set; }

    /// <summary>Somma dei tassi per evento (%, con segno) accreditati.</summary>
    public decimal FundingCollectedPercent { get; set; }

    /// <summary>Funding incassato in valuta quote (nozionale × tasso, con segno).</summary>
    public decimal FundingCollectedQuote { get; set; }

    /// <summary>Timestamp dell'ultimo evento di funding accreditato: lo stesso evento non si conta due volte.</summary>
    public DateTime? LastFundingUtc { get; set; }

    /// <summary>Costo del giro completo (%, quattro fill), dal modello del backtest, fissato all'apertura.</summary>
    public decimal CostPercent { get; set; }

    /// <summary>Funding incassato meno i costi, in quote. Scritto alla chiusura.</summary>
    public decimal? NetQuote { get; set; }

    public string? ClosedReason { get; set; }
}
