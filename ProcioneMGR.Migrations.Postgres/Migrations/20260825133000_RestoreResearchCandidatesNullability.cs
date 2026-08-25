using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcioneMGR.Data;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <summary>
    /// [INCIDENTE 2026-08-25, riparazione — seconda coda] La tabella ricreata rigiocando
    /// <c>AddResearchCandidates</c> porta la forma del 2026-08-14, dove <c>WalkForwardOosSharpe</c>
    /// era NOT NULL; la migrazione che l'ha resa nullable (<c>WalkForwardProvenance</c>,
    /// 2026-08-22) è già in history e non si rigira. Senza questo ALTER l'indexer falliva
    /// l'inserimento di TUTTI i run storici bonificati (il cui WalkForward è null e DICHIARATO
    /// tale, per scelta del 2026-08-22) con «23502 violazione del vincolo non nullo» — 156 run
    /// «illeggibili» che illeggibili non erano. Idempotente: DROP NOT NULL su una colonna già
    /// nullable non fa nulla.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260825133000_RestoreResearchCandidatesNullability")]
    public partial class RestoreResearchCandidatesNullability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "ResearchCandidates" ALTER COLUMN "WalkForwardOosSharpe" DROP NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nessun Down: la nullability appartiene a WalkForwardProvenance (20260822103114).
        }
    }
}
