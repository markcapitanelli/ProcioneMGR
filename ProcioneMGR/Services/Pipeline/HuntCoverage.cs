using Microsoft.EntityFrameworkCore;
using ProcioneMGR.Data;

namespace ProcioneMGR.Services.Pipeline;

/// <summary>Una cella dell'universo: la coppia (serie, timeframe) su cui una caccia può guardare.</summary>
public readonly record struct CellaUniverso(string Symbol, string Timeframe);

/// <summary>
/// [K58, PRD autonomia-piena — Fase 4, 2026-09-03] Che cosa la piattaforma <b>tiene aggiornato</b> e
/// che cosa <b>guarda davvero</b>.
/// </summary>
/// <param name="Seguite">Celle abilitate in watchlist: costano ingestione a ogni giro.</param>
/// <param name="Cacciate">Celle presenti nell'universo di almeno una configurazione in rotazione.</param>
/// <param name="Scoperte">Seguite ma mai cacciate: si pagano e non si guardano.</param>
public sealed record CoperturaCaccia(
    IReadOnlyList<CellaUniverso> Seguite,
    IReadOnlyList<CellaUniverso> Cacciate,
    IReadOnlyList<CellaUniverso> Scoperte)
{
    public double FrazioneCoperta => Seguite.Count == 0 ? 0 : (double)Cacciate.Count / Seguite.Count;

    /// <summary>Le scoperte raggruppate per timeframe, dal buco più grande.</summary>
    public IReadOnlyList<(string Timeframe, IReadOnlyList<string> Simboli)> BuchiPerTimeframe =>
        [.. Scoperte
            .GroupBy(c => c.Timeframe, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Timeframe: g.Key, Simboli: (IReadOnlyList<string>)[.. g.Select(x => x.Symbol).Order(StringComparer.Ordinal)]))
            .OrderByDescending(x => x.Simboli.Count)];
}

public interface IHuntCoverageReader
{
    /// <summary>La copertura corrente, considerando cacciate solo le configurazioni indicate.</summary>
    Task<CoperturaCaccia> ReadAsync(IReadOnlyCollection<int> configurazioniInRotazione, CancellationToken ct = default);
}

/// <summary>
/// [K58] <b>La piattaforma paga per tenere fresche 222 serie e ne guarda 125.</b>
///
/// <para><b>Il fatto, misurato il 2026-09-03</b> sulle nove configurazioni in rotazione:</para>
/// <code>
/// timeframe   seguite   cacciate   MAI cacciate
///    15m         44        10          34
///     5m         30        10          20
///     4h         49        33          16
///     1d         49        33          16
///     1h         49        39          10
/// </code>
/// <para><b>97 celle su 222.</b> Ognuna costa ingestione a ogni giro del worker — e nessuna caccia
/// la guarda. È il buco che serve a rispondere alla domanda «che tipo di caccia aggiungere»: non
/// serve inventarla, basta leggere che cosa manca.</para>
///
/// <para><b>Perché è la dimensione giusta e non le famiglie di strategia.</b> Ho misurato anche
/// quelle — sul motore corrente le famiglie a indicatore classico (Bollinger, Stochastic, MacdTrend,
/// EmaCross, VwapReversion, PriceSmaCross) compaiono <i>solo</i> a 4h. Sembrava un buco di
/// copertura: non lo è. <b>Tutte le configurazioni hanno la stessa identica catena di fasi</b>
/// (verificato: 8, 17, 18 e 19 hanno la stessa sequenza di diciotto stage), quindi quelle famiglie
/// vengono cercate ovunque. Che compaiano solo a 4h non è ciò che la caccia guarda: è ciò che
/// sopravvive allo screening. <b>È un risultato, non una lacuna</b> — e trattarlo come lacuna
/// avrebbe fabbricato una caccia per cercare qualcosa che è già stato cercato e non ha retto.</para>
/// </summary>
public sealed class HuntCoverageReader(IDbContextFactory<ApplicationDbContext> dbFactory) : IHuntCoverageReader
{
    public async Task<CoperturaCaccia> ReadAsync(
        IReadOnlyCollection<int> configurazioniInRotazione, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configurazioniInRotazione);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Solo le serie ABILITATE: una serie disabilitata non si paga più e non va contata fra i
        // buchi — sarebbe la stessa trappola di K49b, dove l'universo conteneva serie che non
        // potevano produrre nulla e le loro bocciature entravano nel denominatore del gate.
        var seguite = await db.TrackedSeries.AsNoTracking()
            .Where(t => t.Enabled)
            .Select(t => new { t.Symbol, t.Timeframe })
            .Distinct()
            .ToListAsync(ct);

        var universi = await db.PipelineConfigurations.AsNoTracking()
            .Where(c => configurazioniInRotazione.Contains(c.Id))
            .Select(c => c.UniverseJson)
            .ToListAsync(ct);

        var cacciate = new HashSet<CellaUniverso>();
        foreach (var json in universi)
        {
            foreach (var cella in Leggi(json)) cacciate.Add(cella);
        }

        var tutte = seguite
            .Select(s => new CellaUniverso(s.Symbol, s.Timeframe))
            .Distinct()
            .ToList();

        return new CoperturaCaccia(
            tutte,
            [.. tutte.Where(cacciate.Contains)],
            [.. tutte.Where(c => !cacciate.Contains(c))]);
    }

    /// <summary>
    /// Puro: un universo malformato non deve far cadere il conteggio della copertura. Una
    /// configurazione illeggibile diventa «zero celle cacciate», che è il verso prudente — dichiara
    /// più buchi, non meno, e i buchi si propongono, non si eseguono.
    /// </summary>
    internal static IReadOnlyList<CellaUniverso> Leggi(string? universeJson)
    {
        if (string.IsNullOrWhiteSpace(universeJson)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(universeJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return [];

            var celle = new List<CellaUniverso>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var sym = e.TryGetProperty("Symbol", out var s) ? s.GetString() : null;
                var tf = e.TryGetProperty("Timeframe", out var t) ? t.GetString() : null;
                if (!string.IsNullOrWhiteSpace(sym) && !string.IsNullOrWhiteSpace(tf))
                {
                    celle.Add(new CellaUniverso(sym, tf));
                }
            }
            return celle;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
