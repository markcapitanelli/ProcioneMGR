using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProcioneMGR.Data;
using ProcioneMGR.Services.Health;
using ProcioneMGR.Services.Pipeline;
using ProcioneMGR.Services.Research;
using ProcioneMGR.Services.Security;

using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [J3, PRD autonomia-operativa 2026-08-25] La sonda «la ricerca è viva» — il numero che distingue
/// «non ha trovato» da «non ha cercato». Il 2026-08-23 la macchina si è fermata e per 43+ ore
/// nessuna superficie lo ha detto. Questi test pinnano: il verdetto a TRE stati (una lettura
/// fallita non è «viva»), il run in corso che vale come vita (niente falsi allarmi a metà di una
/// caccia lunga), e il conteggio su chiavi DISTINTE (le righe sono ~19× le chiavi: l'artefatto già
/// pagato due volte).
/// </summary>
public class ResearchLivenessJudgeTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Soglia = TimeSpan.FromHours(12);

    private static ResearchLivenessFacts Facts(
        DateTime? lastRun = null, int inProgress = 0, int completed24h = 0) =>
        new(lastRun, inProgress, completed24h, 0, 0, null, null);

    [Fact]
    public void RunRecente_Viva()
    {
        var report = ResearchLivenessProbe.Judge(Facts(lastRun: Now.AddHours(-2)), Soglia, Now);
        Assert.Equal(ResearchLivenessState.Viva, report.State);
    }

    [Fact]
    public void SilenzioOltreSoglia_Ferma_ColNumeroDelleOre()
    {
        // Il caso reale: ultimo run 43 ore fa, nessuno in corso.
        var report = ResearchLivenessProbe.Judge(Facts(lastRun: Now.AddHours(-43)), Soglia, Now);
        Assert.Equal(ResearchLivenessState.Ferma, report.State);
        Assert.Contains("43h", report.Reason);
    }

    [Fact]
    public void RunInCorso_Viva_AncheSeLUltimoCompletatoEVecchio()
    {
        // Una caccia lunga a metà corsa non è un fermo: senza questo ramo ogni run lungo
        // produrrebbe un falso allarme.
        var report = ResearchLivenessProbe.Judge(Facts(lastRun: Now.AddHours(-43), inProgress: 1), Soglia, Now);
        Assert.Equal(ResearchLivenessState.Viva, report.State);
        Assert.Contains("in corso", report.Reason);
    }

    [Fact]
    public void NessunRunMai_Ferma_NonViva()
    {
        var report = ResearchLivenessProbe.Judge(Facts(lastRun: null), Soglia, Now);
        Assert.Equal(ResearchLivenessState.Ferma, report.State);
        Assert.Contains("mai", report.Reason);
    }

    [Fact]
    public void IlFermoPortaLoStatoDellaCampagna()
    {
        var facts = Facts(lastRun: Now.AddHours(-43)) with { CampaignStatusSummary = "campagna in WaitingForTrigger da 43h" };
        var report = ResearchLivenessProbe.Judge(facts, Soglia, Now);
        Assert.Contains("WaitingForTrigger", report.Reason);
    }

    // =============================================================================================
    //  [K9, 2026-08-31] La soglia sotto il riarmo, e il verdetto cieco ai candidati
    // =============================================================================================

    [Fact]
    public void Soglia_NonPuoStareSottoIlCicloDiRiarmo()
    {
        // Il caso REALE della configurazione viva del 2026-08-30: soglia 12 (default) contro
        // riarmo 13. A regime sano — rotazione esaurita, attesa, ripartenza dopo 13 ore — la sonda
        // passava un'ora piena a gridare «FERMA» a ogni giro, senza che nulla fosse rotto.
        var soglia = ResearchLivenessProbe.SogliaEffettiva(stallAlertHours: 12, rearmHours: 13);

        Assert.True(soglia > TimeSpan.FromHours(13),
            $"soglia {soglia} sotto il riarmo di 13h: un allarme che suona a macchina sana insegna a ignorarlo");
        Assert.Equal(TimeSpan.FromHours(13 + ResearchLivenessProbe.MargineSulRiarmoOre), soglia);
    }

    [Fact]
    public void Soglia_UnValoreConfiguratoPiuLARGO_SiRispetta()
    {
        // L'operatore può allargare, mai stringere sotto il ciclo: la manopola non è finta.
        var soglia = ResearchLivenessProbe.SogliaEffettiva(stallAlertHours: 48, rearmHours: 13);
        Assert.Equal(TimeSpan.FromHours(48), soglia);
    }

    [Theory]
    [InlineData(0, 0)]      // valori assurdi non devono produrre una soglia a zero
    [InlineData(-5, -5)]
    public void Soglia_NonEmaiNulla(int stall, int rearm) =>
        Assert.True(ResearchLivenessProbe.SogliaEffettiva(stall, rearm) >= TimeSpan.FromHours(1));

    [Fact]
    public void RunCompletatiMaZeroCandidati_NON_ediceViva()
    {
        // Il difetto misurato: la Home stampava «Ricerca viva» accanto a «0 candidati
        // distinti/24h» — il numero che smentisce il badge, di fianco al badge. È durato cinque
        // giorni con 34 run non indicizzati dietro.
        var facts = Facts(lastRun: Now.AddHours(-2), completed24h: 4);

        var report = ResearchLivenessProbe.Judge(facts, Soglia, Now);

        Assert.Equal(ResearchLivenessState.VivaSenzaDeposito, report.State);
        Assert.Contains("ZERO candidati", report.Reason);
        // Non attribuisce la causa: da qui le due sono indistinguibili, e sceglierne una
        // manderebbe a guardare nel posto sbagliato metà delle volte.
        Assert.Contains("indicizzazione ferma", report.Reason);
        Assert.Contains("cacce che non producono", report.Reason);
    }

    [Fact]
    public void RunCompletatiConCandidati_Viva()
    {
        var facts = Facts(lastRun: Now.AddHours(-2), completed24h: 4) with { DistinctCandidates24h = 37 };

        var report = ResearchLivenessProbe.Judge(facts, Soglia, Now);

        Assert.Equal(ResearchLivenessState.Viva, report.State);
    }

    [Fact]
    public void UnRunInCorso_NON_riscattaVentiquattroOreDiDepositiAZero()
    {
        // Il controllo sui depositi viene PRIMA del ramo «un run è in corso»: metterlo dopo lo
        // renderebbe invisibile proprio nei momenti di attività, che sono la maggioranza.
        var facts = Facts(lastRun: Now.AddHours(-2), inProgress: 1, completed24h: 4);

        var report = ResearchLivenessProbe.Judge(facts, Soglia, Now);

        Assert.Equal(ResearchLivenessState.VivaSenzaDeposito, report.State);
    }

    [Fact]
    public void NessunRunNelleVentiquattroOre_NonEUnProblemaDiDEPOSITO()
    {
        // Il controllo sul deposito non deve rubare il verdetto al fermo: con zero run completati
        // lo zero candidati è una conseguenza, non un guasto a sé. Confonderli manderebbe a
        // cercare l'indicizzatore mentre il guasto è che la macchina non cerca.
        var facts = Facts(lastRun: Now.AddHours(-43), completed24h: 0);

        var report = ResearchLivenessProbe.Judge(facts, Soglia, Now);

        Assert.Equal(ResearchLivenessState.Ferma, report.State);
    }
}

[Collection("Postgres")]
public class ResearchLivenessProbeIntegrationTests : IAsyncDisposable
{
    private readonly string _connString;
    private ServiceProvider? _provider;

    public ResearchLivenessProbeIntegrationTests(PostgresFixture pg) => _connString = pg.CreateDatabase();

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private async Task<(ResearchLivenessProbe Probe, IDbContextFactory<ApplicationDbContext> DbFactory)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEncryptionService, PassthroughEncryption>();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(_connString));
        var provider = services.BuildServiceProvider();
        _provider = provider;
        var dbFactory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

        var probe = new ResearchLivenessProbe(
            dbFactory,
            new CampaignOptions { StallAlertHours = 12 }.AsMonitor(),
            NullLogger<ResearchLivenessProbe>.Instance);
        return (probe, dbFactory);
    }

    private static ResearchCandidate Candidate(string key, DateTime completed) => new()
    {
        RunId = Guid.NewGuid(),
        RunCompletedUtc = completed,
        StrategyName = "S",
        Symbol = "BTC/USDT",
        Timeframe = "4h",
        CandidateKey = key,
        ParametersJson = "{}",
        BestStopVariant = "base",
    };

    [Fact]
    public async Task ContaChiaviDistinte_NonRighe_ENoviteVere()
    {
        var (probe, dbFactory) = await BuildAsync();
        var now = DateTime.UtcNow;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            // Un run completato fresco, così il verdetto è Viva e i numeri sono leggibili.
            db.PipelineRuns.Add(new PipelineRun
            {
                Id = Guid.NewGuid(), ConfigurationId = 1, StartedAt = now.AddHours(-3),
                CompletedAt = now.AddHours(-2), Status = "Completed", Trigger = "Campaign",
            });
            // La stessa chiave registrata da TRE run nelle 24h = UN candidato distinto (le righe
            // sono ~19x le chiavi sull'archivio vero: contarle è l'artefatto).
            db.ResearchCandidates.AddRange(
                Candidate("K1", now.AddHours(-1)),
                Candidate("K1", now.AddHours(-5)),
                Candidate("K1", now.AddHours(-9)),
                Candidate("K2", now.AddHours(-2)),
                // K3 esiste da PRIMA dei 7 giorni: nella finestra è attività, non novità.
                Candidate("K3", now.AddHours(-3)),
                Candidate("K3", now.AddDays(-20)));
            await db.SaveChangesAsync();
        }

        var report = await probe.ProbeAsync();

        Assert.Equal(ResearchLivenessState.Viva, report.State);
        Assert.NotNull(report.Facts);
        Assert.Equal(1, report.Facts!.RunsCompleted24h);
        Assert.Equal(3, report.Facts.DistinctCandidates24h);   // K1, K2, K3 — non 5 righe
        Assert.Equal(2, report.Facts.NewCandidates7d);          // K1 e K2: K3 era già noto
    }

    [Fact]
    public async Task DatabaseVuoto_Ferma_NonMenteViva()
    {
        var (probe, _) = await BuildAsync();

        var report = await probe.ProbeAsync();

        Assert.Equal(ResearchLivenessState.Ferma, report.State);
    }
}
