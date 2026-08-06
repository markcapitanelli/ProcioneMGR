using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Trading;

/// <summary>
/// Una protezione che risulta toccata da barre GIÀ CHIUSE mentre la posizione è ancora aperta.
/// Non è una previsione né una stima: è un confronto fra due fatti registrati.
/// </summary>
/// <param name="PositionId">La posizione rimasta aperta.</param>
/// <param name="Symbol">Il simbolo su cui è aperta.</param>
/// <param name="Kind">"take profit" o "stop loss".</param>
/// <param name="Level">Il livello impostato.</param>
/// <param name="ReachedPrice">Il prezzo estremo raggiunto: il minimo per uno short al target, ecc.</param>
/// <param name="BarsTouched">Quante barre chiuse hanno superato il livello.</param>
/// <param name="FirstTouchUtc">L'apertura della PRIMA barra che l'ha superato: da lì la posizione doveva essere chiusa.</param>
public sealed record ProtectiveExitAnomaly(
    string PositionId,
    string Symbol,
    string Kind,
    decimal Level,
    decimal ReachedPrice,
    int BarsTouched,
    DateTime FirstTouchUtc);

/// <summary>
/// [2026-08-06] Il controllo che mancava.
///
/// <para><b>Perché esiste</b>: il proprietario si è accorto a occhio che uno short ETC/USDT sulla
/// corsia 3 aveva raggiunto il take profit senza chiudersi. Il minimo VERO della barra 4h delle
/// 08:00 era 6,31 contro un target di 6,3786 — un fatto scritto a database da ore. Nessun pannello
/// lo diceva: il battito mostrava «ultima candela 16:00 · 0 barre indietro» in verde, e la riga
/// della posizione mostrava un PnL non realizzato positivo, cioè due indicatori che rassicuravano
/// mentre l'uscita non scattava.</para>
///
/// <para>La causa è stata corretta (<see cref="TradingWorker.LastClosedBarOpenUtc"/>: si alimentano
/// solo barre chiuse). Questo controllo è l'altra metà: <b>una causa corretta non è un controllo</b>,
/// e la prossima ragione per cui un'uscita non scatta sarà diversa da questa. Qui non si indaga il
/// perché — si confronta ciò che è impostato con ciò che è successo, e si dice che non torna.</para>
///
/// <para>Puro e senza I/O, così la regola è verificabile senza database né motore.</para>
/// </summary>
public static class ProtectiveExitAudit
{
    /// <summary>
    /// Le anomalie fra le posizioni aperte e le barre <b>già chiuse</b> del loro simbolo.
    ///
    /// <para><paramref name="closedBars"/> deve contenere SOLO barre chiuse (vedi
    /// <c>SeriesFreshness.LastClosedBarOpenUtc</c>): passare la barra in formazione qui
    /// riprodurrebbe dentro il controllo lo stesso difetto che il controllo deve scoprire.</para>
    ///
    /// <para>Si guardano solo le barre <b>successive all'apertura</b> della posizione: il prezzo
    /// toccato prima che la posizione esistesse non dice nulla su di essa.</para>
    /// </summary>
    public static IReadOnlyList<ProtectiveExitAnomaly> Find(
        IEnumerable<OpenPosition> positions,
        IEnumerable<OhlcvData> closedBars)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(closedBars);

        var barre = closedBars.OrderBy(b => b.TimestampUtc).ToList();
        var esiti = new List<ProtectiveExitAnomaly>();

        foreach (var pos in positions)
        {
            var mie = barre
                .Where(b => string.Equals(b.Symbol, pos.Symbol, StringComparison.OrdinalIgnoreCase)
                            && b.TimestampUtc >= pos.OpenedAtUtc)
                .ToList();
            if (mie.Count == 0) continue;

            var isLong = pos.Side == OrderSide.Buy;

            // Per un LONG il target sta sopra (lo tocca il massimo) e lo stop sotto (lo tocca il
            // minimo); per uno SHORT è l'opposto. Sbagliare questo verso è il modo più facile per
            // scrivere un controllo che non trova mai niente.
            if (pos.TakeProfit is decimal tp && tp > 0m)
            {
                var toccate = mie.Where(b => isLong ? b.High >= tp : b.Low <= tp).ToList();
                if (toccate.Count > 0)
                {
                    esiti.Add(new ProtectiveExitAnomaly(
                        pos.PositionId, pos.Symbol, "take profit", tp,
                        isLong ? toccate.Max(b => b.High) : toccate.Min(b => b.Low),
                        toccate.Count, toccate[0].TimestampUtc));
                }
            }

            if (pos.StopLoss is decimal sl && sl > 0m)
            {
                var toccate = mie.Where(b => isLong ? b.Low <= sl : b.High >= sl).ToList();
                if (toccate.Count > 0)
                {
                    esiti.Add(new ProtectiveExitAnomaly(
                        pos.PositionId, pos.Symbol, "stop loss", sl,
                        isLong ? toccate.Min(b => b.Low) : toccate.Max(b => b.High),
                        toccate.Count, toccate[0].TimestampUtc));
                }
            }
        }

        return esiti;
    }
}
