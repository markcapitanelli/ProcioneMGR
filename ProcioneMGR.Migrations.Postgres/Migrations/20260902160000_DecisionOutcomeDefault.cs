using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcioneMGR.Data;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <summary>
    /// [K53, 2026-09-02] <b>Il DEFAULT che mancava a <c>OrchestratorDecisions.Outcome</c>, e che ha
    /// tenuto ferma la Regina per cinque ore e mezza.</b>
    ///
    /// <para><b>Il fatto.</b> <c>AddDecisionOutcome</c> è stata applicata al database <b>vivo</b>
    /// mentre il codice che usa quella colonna stava ancora in un ramo non fuso. Il guscio in
    /// esecuzione — compilato da master — non conosce la proprietà <c>Outcome</c>, quindi la sua
    /// INSERT non la elenca affatto; senza un default la colonna riceve NULL e il vincolo la
    /// respinge:</para>
    /// <code>23502: il valore nullo nella colonna "Outcome" viola il vincolo non nullo</code>
    /// <para>Risultato: journal di flotta fermo alla riga 137 delle 07:46 UTC, tick abortito,
    /// nessuno schieramento e nessun ritiro. È la stessa forma di guasto di K45 presa dall'altro
    /// verso: là la colonna era troppo <i>stretta</i> per la stringa, qui troppo <i>severa</i> per
    /// il binario che la scrive.</para>
    ///
    /// <para><b>La regola che ne discende</b>, e vale per ogni migrazione futura: con le migrazioni
    /// applicate come passo separato e un database condiviso, fra l'istante in cui lo schema cambia
    /// e quello in cui il codice nuovo entra in servizio c'è <b>sempre</b> una finestra in cui il
    /// binario vecchio scrive sullo schema nuovo. Una colonna che nasce obbligatoria dev'essere
    /// scrivibile <b>anche da chi non sa che esiste</b>. Il guardiano è
    /// <c>ColonnaObbligatoriaScrivibileK53Tests</c>.</para>
    ///
    /// <para><b>Scritta a mano, e la ragione che avevo dato era sbagliata.</b> Avevo concluso che
    /// <c>dotnet ef migrations add</c> «produce migrazioni sbagliate in questo repository», perché
    /// rigenerava le ultime cinque migrazioni comprese <c>CreateTable</c> di tabelle esistenti. La
    /// causa vera è banale: <b><c>dotnet ef</c> senza <c>--configuration</c> usa DEBUG</b>, e
    /// <c>ProcioneMGR/bin/Debug/</c> conteneva un assembly delle migrazioni fermo al 2026-08-22 —
    /// trenta migrazioni su trentanove. Diffare contro quello produce esattamente quella
    /// scaffoldatura. Con <c>--configuration Release</c> il tooling è corretto e dice
    /// «<i>No changes have been made to the model since the last migration</i>».</para>
    ///
    /// <para>Questa resta scritta a mano perché ormai c'è ed è idempotente, ma <b>non c'è nessun
    /// motivo per non usare il generatore</b>: basta passargli la configurazione giusta.</para>
    /// </summary>
    // [2026-09-02] I DUE ATTRIBUTI NON SONO CERIMONIA: senza, EF non vede la migrazione.
    // Le migrazioni generate li ricevono nel file `.Designer.cs`; questa e' scritta a mano e la
    // prima versione li aveva dimenticati. Effetto misurato all'avvio del guscio:
    //     «Nessuna migrazione pendente (38 note)»   ...su 39 file presenti.
    // Nessun errore, nessuna riga rossa sul file: semplicemente non esisteva. L'ha scoperta il
    // guardiano di DatabaseMigrator, che confronta modello e snapshot e RIFIUTA di dichiarare lo
    // schema allineato — cioe' il controllo che si e' rifiutato di rassicurare.
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260902160000_DecisionOutcomeDefault")]
    public partial class DecisionOutcomeDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotente di proposito: il default è già stato messo a mano sul database vivo il
            // 2026-09-02 per rimettere in moto la flotta. `SET DEFAULT` è comunque ripetibile.
            migrationBuilder.Sql(
                """ALTER TABLE "OrchestratorDecisions" ALTER COLUMN "Outcome" SET DEFAULT 'Applied';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "OrchestratorDecisions" ALTER COLUMN "Outcome" DROP DEFAULT;""");
        }
    }
}
