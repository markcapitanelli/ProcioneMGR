using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// [B3, sentinella] Un confronto COMPLETATO fra il momento in cui il feed real-time avrebbe fatto
/// scattare un'uscita protettiva e il momento in cui il percorso a candele l'ha fatta scattare
/// davvero. Una riga per confronto, scritta solo quando entrambi i lati esistono.
///
/// Non serve a produrre una media: su tre corsie che fanno una dozzina di trade al mese le
/// osservazioni sono troppo poche perché una mediana significhi qualcosa, e quella domanda è già
/// stata chiusa offline dal replay su migliaia di posizioni (REPORT-B3-EXITLAG-2026-07-28). Serve
/// a vedere il caso SINGOLO che il replay non poteva vedere: un crollo con gap, dove aspettare la
/// chiusura della barra non costa qualche punto base ma una categoria diversa di danno.
/// </summary>
public class ProtectiveExitShadow
{
    public int Id { get; set; }

    public int LaneId { get; set; }

    [MaxLength(32)]
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Modalità della corsia al momento del confronto (mai mescolare Paper e Testnet).</summary>
    public TradingMode Mode { get; set; }

    [MaxLength(64)]
    public string PositionId { get; set; } = string.Empty;

    public OrderSide Side { get; set; }
    public decimal EntryPrice { get; set; }

    /// <summary>Quando il primo tick ha soddisfatto la condizione di uscita, e a che prezzo.</summary>
    public DateTime DetectedAtUtc { get; set; }
    public decimal DetectedPrice { get; set; }

    /// <summary>Motivo che sarebbe scattato sul tick ("StopLoss", "TakeProfit", "Liquidation").</summary>
    [MaxLength(16)]
    public string DetectedReason { get; set; } = string.Empty;

    /// <summary>Prezzo di riempimento che il tick avrebbe ottenuto, dallo stesso evaluator del motore.</summary>
    public decimal ShadowFillPrice { get; set; }

    /// <summary>Quando il percorso a candele ha chiuso davvero, a che prezzo e per quale motivo.</summary>
    public DateTime ActualExitAtUtc { get; set; }
    public decimal ActualFillPrice { get; set; }

    [MaxLength(16)]
    public string ActualReason { get; set; } = string.Empty;

    /// <summary>Secondi di anticipo del feed sulla scoperta.</summary>
    public double LeadSeconds { get; set; }

    /// <summary>
    /// Costo del ritardo in punti base dell'ingresso, orientato sulla posizione: POSITIVO = il feed
    /// avrebbe fatto uscire meglio, negativo = aspettare la chiusura è convenuto. Stessa convenzione
    /// di <see cref="ProtectiveExitLagAnalyzer"/>, così i due numeri sono confrontabili.
    /// </summary>
    public double DelayCostBps { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Persiste i confronti d'ombra e ALLERTA sul caso singolo. La soglia è il punto di tutto il
/// meccanismo: senza, questa tabella sarebbe un raccoglitore che nessuno legge.
/// </summary>
public interface IProtectiveExitShadowRecorder
{
    Task RecordAsync(ProtectiveExitShadow comparison, CancellationToken ct = default);
}

/// <summary>Opzioni della sentinella.</summary>
public sealed class ProtectiveExitShadowOptions
{
    /// <summary>
    /// Spegnibile: la sentinella osserva e basta, ma osservare a ogni tick ha un costo e chi non la
    /// vuole deve poterla togliere senza toccare il codice.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sopra questo costo in punti base si allerta, sul SINGOLO evento. 200 bps (2%) non è una
    /// stima: è la soglia oltre la quale un caso solo vale più di una media, perché a quel punto non
    /// si sta più misurando l'effetto dell'ombra sullo stop ma un salto di prezzo dentro la barra.
    /// Il segno conta: si allerta solo quando il ritardo COSTA (feed migliore del ritardo), perché
    /// il caso opposto è già il verdetto noto e non è una notizia.
    /// </summary>
    public double AlertAboveBps { get; set; } = 200d;
}

/// <summary>
/// Implementazione: una INSERT per confronto, più l'allarme sopra soglia. Nessun aggiornamento e
/// nessuno stato: le rilevazioni ancora in sospeso vivono in memoria nel motore e si perdono a un
/// riavvio del core. È una rinuncia dichiarata — raddoppiare lo schema per non perdere una manciata
/// di rilevazioni in volo su una sentinella non vale il suo costo.
/// </summary>
public sealed class ProtectiveExitShadowRecorder(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<ProtectiveExitShadowOptions> options,
    ILogger<ProtectiveExitShadowRecorder> logger,
    Notifications.INotifier? notifier = null) : IProtectiveExitShadowRecorder
{
    public async Task RecordAsync(ProtectiveExitShadow c, CancellationToken ct = default)
    {
        c.CreatedAtUtc = DateTime.UtcNow;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Set<ProtectiveExitShadow>().Add(c);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Ombra R1 corsia {Lane} {Symbol}: il feed avrebbe chiuso {Reason} {Lead:F0}s prima, "
            + "a {Shadow} contro {Actual} ⇒ {Cost:F1} bps ({Verdict}).",
            c.LaneId, c.Symbol, c.DetectedReason, c.LeadSeconds, c.ShadowFillPrice, c.ActualFillPrice,
            c.DelayCostBps, c.DelayCostBps > 0 ? "il ritardo è costato" : "il ritardo è convenuto");

        var threshold = options.CurrentValue.AlertAboveBps;
        if (c.DelayCostBps <= threshold) return;

        // Un caso solo sopra soglia È la notizia: a quel punto non si sta misurando l'effetto
        // dell'ombra sullo stop, si sta guardando un salto di prezzo dentro la barra — cioè
        // esattamente lo scenario che la finestra del replay non conteneva.
        logger.LogWarning(
            "SENTINELLA R1: sulla corsia {Lane} ({Symbol}) uscire al tocco avrebbe reso {Cost:F0} bps in più "
            + "({Lead:F0}s di anticipo). Oltre la soglia di {Threshold:F0} bps: un caso solo di questa "
            + "grandezza vale più di una media, ed è lo scenario che il replay non poteva vedere.",
            c.LaneId, c.Symbol, c.DelayCostBps, c.LeadSeconds, threshold);

        if (notifier is null) return;
        await notifier.NotifyAsync(Notifications.NotificationSeverity.Warning,
            $"Sentinella R1: ritardo costoso sulla corsia {c.LaneId}",
            $"{c.Symbol}: il feed avrebbe chiuso {c.DetectedReason} {c.LeadSeconds:F0}s prima, a "
            + $"{c.ShadowFillPrice} invece di {c.ActualFillPrice} — {c.DelayCostBps:F0} bps di differenza. "
            + "Il verdetto di B3 (uscite protettive NON guidate dai tick) è stato preso su una finestra "
            + "senza crolli con gap: se questo si ripete, va rimisurato.", ct);
    }
}
