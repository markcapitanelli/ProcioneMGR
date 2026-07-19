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
}
