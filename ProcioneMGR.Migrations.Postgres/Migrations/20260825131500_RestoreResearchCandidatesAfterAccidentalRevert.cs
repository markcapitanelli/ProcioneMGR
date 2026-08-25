using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcioneMGR.Data;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <summary>
    /// [INCIDENTE 2026-08-25, riparazione] Un <c>dotnet-ef migrations remove --force --no-build</c>
    /// eseguito con l'assembly delle migrazioni STANTIO (build Debug ferma al 2026-08-14) ha
    /// revertito sul database VIVO l'ultima migrazione che quell'assembly conosceva —
    /// <c>AddResearchCandidates</c> — droppando la tabella, togliendo la sua riga di history e
    /// cancellando i suoi file dal progetto. La tabella è DERIVATA e ricostruibile dagli artifact
    /// («ValidatedCandidates», intatti), quindi nessun dato primario è andato perso.
    ///
    /// <para>La riparazione segue la catena standard: i file di <c>AddResearchCandidates</c> sono
    /// ripristinati da git (ricreano la tabella nella forma del 2026-08-14, 26 colonne) e QUESTA
    /// migrazione aggiunge le sei colonne introdotte dopo (WalkForwardProvenance e
    /// PassiveBenchmarkColumns, già in history e quindi non ri-applicabili). <b>Idempotente per
    /// costruzione</b> (ADD COLUMN IF NOT EXISTS): su un database fresco che rigioca l'intera
    /// catena, le sei colonne esistono già quando si arriva qui, e questa migrazione non fa nulla.</para>
    ///
    /// <para>La lezione, per non ripeterla: <c>migrations remove --force</c> decide «qual è
    /// l'ultima migrazione» leggendo l'ASSEMBLY, e con <c>--no-build</c> l'assembly può essere
    /// vecchio di settimane — il revert colpisce allora una migrazione arbitraria del passato, sul
    /// database a cui la startup punta. Mai <c>--force</c> con <c>--no-build</c>.</para>
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260825131500_RestoreResearchCandidatesAfterAccidentalRevert")]
    public partial class RestoreResearchCandidatesAfterAccidentalRevert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "ResearchCandidates" ADD COLUMN IF NOT EXISTS "WalkForwardSource" character varying(32);
                ALTER TABLE "ResearchCandidates" ADD COLUMN IF NOT EXISTS "DominantDirection" character varying(16);
                ALTER TABLE "ResearchCandidates" ADD COLUMN IF NOT EXISTS "NetExposure" numeric;
                ALTER TABLE "ResearchCandidates" ADD COLUMN IF NOT EXISTS "TimeInMarketFraction" numeric;
                ALTER TABLE "ResearchCandidates" ADD COLUMN IF NOT EXISTS "PassiveHoldoutSharpe" numeric;
                ALTER TABLE "ResearchCandidates" ADD COLUMN IF NOT EXISTS "ExcessHoldoutSharpe" numeric;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nessun Down: le colonne appartengono alle migrazioni originali (20260822103114,
            // 20260822105207); revertire da qui creerebbe due proprietari della stessa colonna.
        }
    }
}
