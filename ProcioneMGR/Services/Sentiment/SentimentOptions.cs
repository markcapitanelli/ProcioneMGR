namespace ProcioneMGR.Services.Sentiment;

/// <summary>
/// Opzioni di Sentiment 2.0 (sezione <c>Sentiment</c>): raccolta delle serie di market mood
/// (Fear &amp; Greed + derivati Binance, API pubbliche senza chiave), composite con z-score e
/// retention. Hot-reload via IOptionsMonitor (editabile da /admin/autonomy); gli INTERVALLI del
/// worker si leggono al boot (PeriodicTimer) e richiedono riavvio.
/// </summary>
public sealed class SentimentOptions
{
    /// <summary>Worker di raccolta. Default ON: sole GET pubbliche a cadenza modesta, e le serie Binance esistono solo per 30 giorni — i buchi sono irrecuperabili.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadenza del fetch delle metriche (minuti). Richiede riavvio.</summary>
    public int MetricsIntervalMinutes { get; set; } = 30;

    /// <summary>Cadenza del sync delle notizie RSS/calendario/retail (minuti). Richiede riavvio.</summary>
    public int NewsIntervalMinutes { get; set; } = 60;

    /// <summary>Mercati Binance USDS-M osservati (formato exchange, es. BTCUSDT).</summary>
    public List<string> Symbols { get; set; } = ["BTCUSDT", "ETHUSDT"];

    /// <summary>Retention delle notizie (AltDataPoints), giorni.</summary>
    public int NewsRetentionDays { get; set; } = 180;

    /// <summary>Retention delle serie metriche, giorni (la fonte FearGreed è ESENTE: è il baseline lungo, ~2500 righe totali).</summary>
    public int MetricRetentionDays { get; set; } = 400;

    /// <summary>Finestra del baseline per gli z-score, giorni.</summary>
    public int BaselineDays { get; set; } = 30;

    /// <summary>|z| oltre cui una metrica è "estrema" (flag contrarian).</summary>
    public double ExtremeZScore { get; set; } = 2.0;

    /// <summary>Fear &amp; Greed ≤ questa soglia = extreme fear (flag contrarian).</summary>
    public int FearGreedExtremeLow { get; set; } = 20;

    /// <summary>Fear &amp; Greed ≥ questa soglia = extreme greed (flag contrarian).</summary>
    public int FearGreedExtremeHigh { get; set; } = 80;

    // Pesi del composite (rinormalizzati sui componenti effettivamente disponibili).
    public double WeightNews { get; set; } = 0.20;
    public double WeightFearGreed { get; set; } = 0.25;
    public double WeightFunding { get; set; } = 0.20;
    public double WeightLongShort { get; set; } = 0.20;
    public double WeightTaker { get; set; } = 0.15;

    /// <summary>
    /// Opt-in: rende il fattore "Sentiment" disponibile come feature ML (AlphaFactorFactory).
    /// Default OFF: il sentiment entra nei modelli solo per scelta esplicita dell'operatore.
    /// </summary>
    public bool EnableMlFeature { get; set; }

    /// <summary>
    /// Scorer delle notizie: "Keyword" (default, lessicale, zero costi), "Llm" (provider AI attivo
    /// del layer multi-provider — sceglierlo è il consenso esplicito al costo per chiamata) o
    /// "Onnx" (inferenza locale del pilota). Hot-reload via DelegatingSentimentScorer; ogni scorer
    /// non-lessicale ripiega DA SOLO sul lessico quando il suo canale/modello manca.
    /// </summary>
    public string ScorerProvider { get; set; } = SentimentScorerProviders.Keyword;

    /// <summary>
    /// Percorso del modello ONNX del pilota sentiment (relativo al content root se non assoluto).
    /// Il file NON sta nel repository (è un artefatto addestrato, cartella gitignored): si genera
    /// dal pannello in /sentiment.
    /// </summary>
    public string OnnxModelPath { get; set; } = Path.Combine("models", "sentiment-pilot.onnx");

    /// <summary>Guardiano di profondità delle serie-patrimonio (vedi <see cref="SentimentHeritageGuardOptions"/>).</summary>
    public SentimentHeritageGuardOptions HeritageGuard { get; set; } = new();
}

/// <summary>
/// Soglie del guardiano di PROFONDITÀ delle serie-patrimonio di <c>SentimentMetricPoints</c> —
/// le tre esenti dalla purge del worker (FundingRate, FearGreed, BinanceLiquidations).
///
/// <para>Perché esiste: il backfill profondo del funding (dal 2019, T0.2) è andato perso DUE volte
/// in silenzio (2026-07-24 e 2026-08-11) nonostante l'esenzione dalla purge fosse al suo posto —
/// restava ~ la finestra di <c>MetricRetentionDays</c>, e carry e backtest a leva leggevano una
/// serie corta senza che nessun controllo lo dicesse. L'esenzione protegge dal worker; questo
/// guardiano misura che la storia CI SIA davvero, qualunque sia stata la via della perdita.</para>
///
/// <para>La soglia è una DICHIARAZIONE: «questa serie deve arrivare almeno fino a quella data, con
/// almeno tanti punti». Su violazione: log Error a ogni giro + notifica (una per transizione, come
/// SeriesFreshnessWatchWorker) + badge in /sentiment e alert in Home. Nessuna azione automatica:
/// il ripristino (es. <c>fundingbackfill</c>) resta una scelta umana.</para>
/// </summary>
public sealed class SentimentHeritageGuardOptions
{
    /// <summary>Default ON: sola lettura di aggregati, a cadenza lenta — spento, una perdita resta invisibile.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadenza del controllo (ore). Richiede riavvio (PeriodicTimer letto al boot).</summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>
    /// Ticker base (BTC, ETH, …) su cui si pretende la storia profonda del funding. VUOTO di
    /// proposito nel codice: il binder .NET APPENDE gli elementi del JSON ai default invece di
    /// sostituirli (trappola già pagata) — i default reali sono <see cref="DefaultFundingSymbols"/>,
    /// usati quando la lista configurata è vuota.
    /// </summary>
    public List<string> FundingSymbols { get; set; } = [];

    /// <summary>I sei mercati del backfill T0.2 (tools/PlatformExpand fundingbackfill).</summary>
    public static readonly IReadOnlyList<string> DefaultFundingSymbols =
        ["BTC", "ETH", "SOL", "BNB", "XRP", "DOGE"];

    /// <summary>
    /// Simboli effettivi: la lista configurata, o i default incorporati se è vuota.
    ///
    /// <para>È un METODO e non una proprietà calcolata, come <c>EffectiveStages()</c> su
    /// <c>DriftMonitorOptions</c> e i tre "effettivi" di <c>FactorDriftOptions</c>:
    /// <c>AppConfigWriter.SaveSectionAsync</c> riscrive la sezione serializzando il POCO
    /// <b>intero</b>, e System.Text.Json serializza anche le get-only — al primo «Salva» del
    /// pannello Sentiment di /admin/autonomy sarebbe comparsa in appsettings.json una chiave
    /// <c>Sentiment:HeritageGuard:EffectiveFundingSymbols</c> che il POCO non sa rileggere.
    /// <c>ConfigurationKeyUiCoverageTests</c> non l'avrebbe vista — salta le sole-lettura, quindi
    /// resta verde mentre il file si sporca: il guardiano giusto è
    /// <c>ConfigPocoComputedPropertyTests</c>, nato da questo caso.</para>
    /// </summary>
    public IReadOnlyList<string> EffectiveFundingSymbols() =>
        FundingSymbols.Count > 0 ? FundingSymbols : DefaultFundingSymbols;

    /// <summary>
    /// La storia del funding deve arrivare almeno a questa data. 2020-10-01 e non 2020-01-01:
    /// una serie non può precedere il LISTING del suo mercato USDS-M (misurato sul DB vero il
    /// 2026-08-13: BTC 2019-09, ETH 2019-11, XRP 2020-01, BNB 2020-02, DOGE 2020-07, SOL 2020-09)
    /// — con l'àncora a gennaio quattro serie COMPLETE risultavano violate. Il taglio
    /// dell'incidente (storia dal 2025-06) resta preso con anni di margine.
    /// </summary>
    public DateTime FundingMinStartUtc { get; set; } = new(2020, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Eventi di funding minimi per simbolo (~3/giorno: dal 2019 sono ~7.000; 5.000 lascia margine).</summary>
    public int FundingMinEventsPerSymbol { get; set; } = 5000;

    /// <summary>Il baseline lungo del Fear &amp; Greed deve arrivare almeno a questa data (la fonte parte dal 2018-02).</summary>
    public DateTime FearGreedMinStartUtc { get; set; } = new(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Punti minimi del Fear &amp; Greed (un punto/giorno: dal 2018 sono ~2.500).</summary>
    public int FearGreedMinPoints { get; set; } = 2000;

    /// <summary>
    /// Se sorvegliare la serie delle liquidazioni. Default ON (è patrimonio come le altre), ma
    /// spegnibile con cognizione: dalle postazioni EEA lo stream futures Binance è MUTO (blocco
    /// MiCA sul market-data derivati, trovato dal vivo il 2026-07-24 — vedi LiquidationSyncWorker)
    /// e l'accumulo resta a zero PER COSTRUZIONE: lì l'allarme sarebbe perpetuo, e un allarme
    /// perpetuo smette di essere letto. Da spenta, la riga resta MISURATA e mostrata in /sentiment
    /// come «non sorvegliata» — mai un OK finto.
    /// </summary>
    public bool LiquidationsEnforced { get; set; } = true;

    /// <summary>
    /// Ore senza un punto nuovo oltre le quali l'accumulo delle liquidazioni è dichiarato FERMO.
    ///
    /// <para><b>Ha sostituito un'àncora a data assoluta (2026-08-24).</b> Fino a quel giorno la
    /// riga era giudicata con <c>LiquidationsMinStartUtc = 2026-08-01</c>, cioè «la storia deve
    /// arrivare almeno al primo agosto». Su un feed che <b>esiste solo al presente</b> — nessun
    /// backfill: i due endpoint REST di liquidazione sono stati ritirati e il dump storico USDS-M
    /// non è mai esistito — quella soglia è <b>inesigibile per aritmetica</b>: anche se lo stream
    /// ripartisse domani, il punto più vecchio sarebbe di domani, che è più recente dell'àncora, e
    /// la riga resterebbe rossa <i>per sempre</i>. Cambierebbe solo il messaggio, da «serie
    /// ASSENTE» a «profondità persa».</para>
    ///
    /// <para>La domanda giusta per una serie che si può solo accumulare non è «quanto indietro
    /// arriva» ma <b>«sta ancora arrivando»</b>, e quella è una soglia che si può rientrare. 12 ore
    /// e non 1: <c>!forceOrder@arr</c> è un feed di eventi sparsi e i secchi sono orari, quindi i
    /// vuoti brevi sono normali (stessa lezione di <c>LiquidationsOptions.StaleSeconds</c>, dove
    /// 120 secondi producevano riconnessioni a vuoto).</para>
    /// </summary>
    public int LiquidationsStaleAfterHours { get; set; } = 12;

    /// <summary>Punti minimi complessivi della fonte liquidazioni (4 metriche/ora/simbolo).</summary>
    public int LiquidationsMinPoints { get; set; } = 100;

    /// <summary>
    /// [I15] Se sorvegliare la profondità del corpus di notizie CON PUNTEGGIO (esente dalla purge
    /// dal 2026-08-19). <b>Default OFF, e non è timidezza: è aritmetica.</b>
    ///
    /// <para>La purge delle notizie ha girato a ogni tick da sempre, quindi <b>oggi il corpus non
    /// può essere più profondo di <c>NewsRetentionDays</c></b> (180 giorni). Qualunque àncora
    /// sensata scatterebbe al primo giro, e un allarme perpetuo smette di essere letto — è
    /// letteralmente la ragione per cui esiste <see cref="LiquidationsEnforced"/>.</para>
    ///
    /// <para>Da spenta la riga resta <b>MISURATA</b> e mostrata in <c>/sentiment</c> come «non
    /// sorvegliata», mai un OK finto. La sequenza giusta è: si lascia accumulare, si legge il
    /// minimo VERO, si sceglie <see cref="NewsMinStartUtc"/> da quella misura, poi si accende. È
    /// la stessa storia dell'àncora del funding, spostata da gennaio a ottobre 2020 solo dopo la
    /// misura sul database reale.</para>
    /// </summary>
    public bool NewsEnforced { get; set; }

    /// <summary>
    /// [I15] Il corpus di notizie con punteggio deve arrivare almeno a questa data. Il valore di
    /// nascita è la data dell'esenzione stessa: prima di allora la purge cancellava, quindi
    /// chiedere una storia più antica pretenderebbe qualcosa che non può esistere. Va rialzato —
    /// cioè spostato indietro — solo dopo aver MISURATO quanto è profondo il corpus davvero.
    /// </summary>
    public DateTime NewsMinStartUtc { get; set; } = new(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// [I15] Notizie con punteggio minime nel corpus. 1.000 è deliberatamente basso: la soglia
    /// serve a distinguere «archivio perso» da «archivio che cresce», non a certificare che sia
    /// abbastanza grande per addestrare qualcosa.
    /// </summary>
    public int NewsMinPoints { get; set; } = 1000;
}
