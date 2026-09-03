using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProcioneMGR.Data;
using ProcioneMGR.Tests.Infrastructure;

namespace ProcioneMGR.Tests;

/// <summary>
/// [K53, 2026-09-02] <b>Una colonna obbligatoria dev'essere scrivibile anche da chi non sa che
/// esiste.</b>
///
/// <para><b>Il fatto, e quanto è costato.</b> La migrazione <c>AddDecisionOutcome</c> è stata
/// applicata al database <b>vivo</b> mentre il codice che usa <c>Outcome</c> stava in un ramo non
/// ancora fuso. Il guscio in esecuzione — compilato da master — non conosce quella proprietà,
/// quindi la sua INSERT non la elenca; senza un default la colonna prende NULL e Postgres respinge
/// la riga con <c>23502</c>. <b>Journal di flotta fermo per cinque ore e mezza</b>: nessuno
/// schieramento, nessun ritiro, e da fuori sembrava che la Regina non avesse niente da fare.</para>
///
/// <para><b>La regola generale.</b> Con le migrazioni applicate come passo separato e un database
/// condiviso fra guscio, motore e worktree, fra il cambio di schema e l'entrata in servizio del
/// codice nuovo c'è <b>sempre</b> una finestra in cui il binario vecchio scrive sullo schema nuovo.
/// Espandi, poi contrai: o la colonna nasce con un default, o nasce annullabile e diventa
/// obbligatoria in una <i>seconda</i> migrazione, dopo il rilascio.</para>
///
/// <para><b>Un limite dichiarato.</b> Questo test costruisce lo schema dal MODELLO
/// (<c>EnsureCreated</c>) e non dalle migrazioni, perché l'app non referenzia l'assembly delle
/// migrazioni — scelta deliberata di <c>Program.cs</c> — e quindi <c>MigrateAsync</c> dentro la
/// suite non trova nulla. <b>È anche il motivo per cui questo difetto non poteva essere preso da un
/// test: nessuna prova di questa suite esercita le migrazioni.</b> Il default vive quindi in due
/// posti (modello e migrazione <c>DecisionOutcomeDefault</c>), e qui si verifica il primo.</para>
/// </summary>
[Collection("Postgres")]
public class ColonnaObbligatoriaScrivibileK53Tests(PostgresFixture pg)
{
    private readonly string _connString = pg.CreateDatabase();

    /// <summary>
    /// Il caso esatto che ha rotto la produzione: una INSERT che <b>non nomina</b> <c>Outcome</c>,
    /// cioè quella che genera un binario che non conosce la colonna. Deve passare.
    /// </summary>
    [Fact]
    public async Task UnaINSERTcheNONnominaOutcome_DEVEpassare()
    {
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_connString).Options, null!);
        await db.Database.EnsureCreatedAsync();

        // SQL grezzo, esattamente come lo scriverebbe un EF che non sa della colonna.
        var righe = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "OrchestratorDecisions"
                ("AtUtc","Kind","LaneId","Source","Reason","VotesJson","Applied","DryRun")
            VALUES (now() at time zone 'utc', 'Assign', 4, 'rules', 'binario vecchio', '[]', true, false);
            """);

        Assert.Equal(1, righe);

        var outcome = await db.OrchestratorDecisions.AsNoTracking()
            .Where(d => d.Reason == "binario vecchio").Select(d => d.Outcome).SingleAsync();
        // [Revisione 2026-09-03] Chi non dichiara l'esito NON LO SA: il default è «Unknown», mai
        // «Applied». Con 'Applied' come default ogni riga scritta da un binario vecchio — e ogni
        // scrittore che dimenticava il campo — risultava «eseguita» nel pannello.
        Assert.Equal(DecisionOutcome.Unknown, outcome);
    }

    /// <summary>
    /// <b>Il guardiano generale, ed è il vero valore di questo file.</b> Ogni colonna NOT NULL senza
    /// valore generato dal database deve avere un default — altrimenti la prossima migrazione
    /// ripeterà lo stesso guasto su un'altra tabella.
    ///
    /// <para>Le colonne con <c>ValueGenerated</c> (identity, timestamp di riga) sono escluse: il
    /// database le riempie da sé, che è la stessa garanzia per un'altra via. E le colonne che
    /// esistono da prima di questa regola sono elencate come <b>debito dichiarato</b>: non si
    /// nascondono, si contano.</para>
    /// </summary>
    [Fact]
    public async Task OGNIcolonnaOBBLIGATORIAnuova_haUNdefault()
    {
        await using var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_connString).Options, null!);
        await db.Database.EnsureCreatedAsync();

        // Le tabelle toccate dal filone K: quelle su cui la regola è stata pagata e vale da subito.
        string[] sorvegliate =
        [
            "OrchestratorDecisions", "FleetLaneObservations", "FleetLaneIdentityEpisodes",
        ];

        var scoperte = new List<string>();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync();
        cmd.CommandText =
            """
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = ANY(@tabelle)
              AND is_nullable = 'NO'
              AND column_default IS NULL
              AND is_identity = 'NO'
              AND identity_generation IS NULL;
            """;
        var p = cmd.CreateParameter();
        p.ParameterName = "tabelle";
        p.Value = sorvegliate;
        cmd.Parameters.Add(p);

        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) scoperte.Add(reader.GetString(0));
        }

        // Debito DICHIARATO: colonne obbligatorie senza default che precedono la regola. Ogni voce
        // è una riga che un binario vecchio non potrebbe scrivere — si tollerano perché nessuno le
        // scrive da un binario vecchio, e si contano perché la lista non deve crescere.
        string[] debitoNoto =
        [
            "OrchestratorDecisions.AtUtc", "OrchestratorDecisions.Kind", "OrchestratorDecisions.Source",
            "OrchestratorDecisions.Reason", "OrchestratorDecisions.VotesJson",
            "OrchestratorDecisions.Applied", "OrchestratorDecisions.DryRun",
            "FleetLaneObservations.Identity", "FleetLaneObservations.FirstSeenUtc",
            "FleetLaneObservations.ObservedSeconds", "FleetLaneObservations.LastTickUtc",
            "FleetLaneIdentityEpisodes.LaneId", "FleetLaneIdentityEpisodes.Identity",
            "FleetLaneIdentityEpisodes.FirstSeenUtc", "FleetLaneIdentityEpisodes.ClosedUtc",
            "FleetLaneIdentityEpisodes.ObservedSeconds", "FleetLaneIdentityEpisodes.NextIdentity",
        ];

        var nuove = scoperte.Except(debitoNoto, StringComparer.Ordinal).OrderBy(x => x).ToList();

        Assert.True(nuove.Count == 0,
            "Colonne obbligatorie SENZA default e non generate dal database: "
            + string.Join(", ", nuove)
            + ". Una colonna così non è scrivibile da un binario che non la conosce, ed è il guasto "
            + "che il 2026-09-02 ha tenuto ferma la flotta per cinque ore e mezza. Dalle un default, "
            + "oppure falla nascere annullabile e rendila obbligatoria in una seconda migrazione, "
            + "dopo il rilascio del codice che la scrive.");

        // E `Outcome` NON deve comparire fra le scoperte: è quella che ha imposto la regola.
        Assert.DoesNotContain("OrchestratorDecisions.Outcome", scoperte, StringComparer.Ordinal);
    }
}
