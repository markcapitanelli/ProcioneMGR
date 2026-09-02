using Microsoft.EntityFrameworkCore.Migrations;

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
    /// <para><b>Scritta a mano, e perché.</b> <c>dotnet ef migrations add</c> in questo repository
    /// produce migrazioni sbagliate — rigenera le ultime cinque, comprese <c>CreateTable</c> di
    /// tabelle che esistono già — perché l'app non referenzia l'assembly delle migrazioni (scelta
    /// deliberata di <c>Program.cs</c>, evita un ciclo di progetti). Applicare quella scaffoldatura
    /// al database vivo lo romperebbe. Finché il tooling non è a posto, le migrazioni di questo
    /// repository si scrivono a mano — ed è il motivo per cui questa è idempotente.</para>
    /// </summary>
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
