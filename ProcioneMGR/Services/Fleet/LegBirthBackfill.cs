using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Ensemble;
using ProcioneMGR.Services.Trading;

namespace ProcioneMGR.Services.Fleet;

/// <summary>L'esito per gamba: cosa ha ricevuto il timbro, o perché no. Si mostra, non si inghiotte.</summary>
public sealed record LegBirthOutcome(int LaneId, string Leg, DateTime? Timbro, string Detail);

public sealed record LegBirthReport(bool DryRun, IReadOnlyList<LegBirthOutcome> Legs)
{
    public int Updated => Legs.Count(l => l.Timbro is not null);
}

/// <summary>
/// [K22, PRD autonomia-piena — Fase 3, 2026-09-02] <b>Il timbro di nascita delle gambe che non
/// l'hanno mai avuto.</b>
///
/// <para><b>Il problema.</b> Da [K39] il monitor di decadimento giudica una gamba solo sui trade
/// chiusi <b>dopo</b> <see cref="EnsembleStrategy.ExpectedSharpeAtUtc"/>: senza quell'ancora, il
/// monitor confrontava lo Sharpe atteso con trade che appartenevano a un'altra ipotesi — 65 righe
/// su 66, misurato. La conseguenza voluta è che una gamba <b>senza timbro non è misurabile</b>, ed è
/// il verso giusto; la conseguenza indesiderata è che <b>4 gambe su 7</b> (corsie 1, 2 e 5,
/// misurato il 2026-09-02) restano fuori dal giudizio per sempre.</para>
///
/// <para><b>Perché il rimedio del PRD non si può usare.</b> «Rimuovere e ri-aggiungere le gambe da
/// <c>/ensemble</c>» conia un <c>StrategyId</c> nuovo, quindi <b>azzera l'identità della corsia</b> e
/// con essa l'orologio dell'osservazione — la cosa che tiene il ritiro per Sharpe irraggiungibile.
/// Si pagherebbe con dieci giorni di cancello per guadagnare un campo.</para>
///
/// <para><b>La sola ancora ammissibile, e perché è onesta.</b> Il ledger di osservazione
/// (<c>FleetLaneObservations</c>) registra <c>FirstSeenUtc</c>: <i>il primo tick in cui la flotta ha
/// visto quella identità in corsa</i>. Non è la nascita della gamba — è il primo momento in cui
/// qualcuno l'ha vista — ma <b>per costruzione non può precederla</b>: la riga nasce a un tick
/// successivo allo schieramento. Quindi:</para>
/// <list type="bullet">
///   <item>l'ancora può essere <b>più tardi</b> del vero: si escludono trade legittimi, il monitor ha
///   meno dati e non condanna — errore nella direzione prudente;</item>
///   <item>l'ancora <b>non può essere prima</b> del vero: non può mai far entrare nel giudizio i
///   trade di un'ipotesi precedente, che è il difetto che K39 ha corretto.</item>
/// </list>
///
/// <para><b>L'identità deve combaciare.</b> Il timbro si scrive solo se la riga del ledger porta
/// esattamente lo <c>StrategyId</c> della gamba: il ledger è per corsia, e una corsia riassegnata
/// dopo la nascita della riga la <b>riscrive</b> (è il ramo di cambio identità di
/// <c>LaneObservationLedger</c>). Senza questo confronto si scriverebbe sulla gamba di oggi la data
/// in cui è stata vista quella di ieri: un'invenzione con l'aria di una misura, esattamente
/// l'errore che questo filone ha già pagato tre volte.</para>
///
/// <para>Dove il ledger tace — le corsie fuori dal governo della flotta — la gamba <b>resta senza
/// timbro</b>. È il verso voluto: la copertura scende, la fiducia sale.</para>
/// </summary>
public sealed class LegBirthBackfill(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IServiceProvider serviceProvider,
    ILogger<LegBirthBackfill> logger)
{
    public async Task<LegBirthReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var outcomes = new List<LegBirthOutcome>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ledger = await db.FleetLaneObservations.AsNoTracking().ToListAsync(ct);

        for (var laneId = 0; laneId < TradingLanes.Count; laneId++)
        {
            ct.ThrowIfCancellationRequested();
            EnsembleConfiguration cfg;
            IEnsembleManager manager;
            try
            {
                manager = serviceProvider.GetRequiredKeyedService<IEnsembleManager>(laneId);
                cfg = await manager.GetConfigurationAsync(ct);
            }
            catch (Exception ex)
            {
                outcomes.Add(new(laneId, "—", null, $"configurazione non leggibile: {ex.Message}"));
                continue;
            }

            var riga = ledger.FirstOrDefault(o => o.LaneId == laneId);
            var changed = false;

            foreach (var leg in cfg.Strategies.Where(s => s.IsActive && s.ExpectedSharpeAtUtc is null))
            {
                var nome = $"{leg.StrategyName} {cfg.Symbol} {cfg.Timeframe}";

                if (riga is null)
                {
                    outcomes.Add(new(laneId, nome, null,
                        "il ledger di osservazione non ha righe per questa corsia: nessuna data registrata, "
                        + "e inventarla renderebbe misurabile una gamba su un'ancora finta"));
                    continue;
                }

                // L'identità del ledger è «SYM|TF|StrategyId» (LaneObservationLedger): confrontare il
                // solo StrategyId basta ed è la parte che non collide.
                if (!riga.Identity.Contains(leg.StrategyId, StringComparison.OrdinalIgnoreCase))
                {
                    outcomes.Add(new(laneId, nome, null,
                        $"il ledger osserva un'ALTRA identità su questa corsia ({Troncato(riga.Identity)}): "
                        + "la sua data appartiene a un'ipotesi diversa, e usarla sarebbe un'invenzione"));
                    continue;
                }

                outcomes.Add(new(laneId, nome, riga.FirstSeenUtc,
                    $"primo avvistamento della STESSA identità: {riga.FirstSeenUtc:yyyy-MM-dd HH:mm} UTC "
                    + "(≥ la nascita vera per costruzione: il giudizio parte più tardi, mai prima)"));

                if (!dryRun)
                {
                    leg.ExpectedSharpeAtUtc = riga.FirstSeenUtc;
                    changed = true;
                }
            }

            if (changed)
            {
                await manager.UpdateConfigurationAsync(cfg, ConfigWriteContext.Create(ConfigWriteSources.Backfill,
                    "K22: timbro di nascita dal ledger di osservazione (FirstSeenUtc, ancora conservativa)"), ct);
                logger.LogInformation(
                    "Corsia {Lane}: timbro di nascita ricostruito dal ledger di osservazione (K22).", laneId);
            }
        }

        return new LegBirthReport(dryRun, outcomes);
    }

    private static string Troncato(string s) => s.Length <= 60 ? s : s[..60];
}
