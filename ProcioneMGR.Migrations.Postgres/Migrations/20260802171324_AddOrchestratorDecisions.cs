using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestratorDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrchestratorDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LaneId = table.Column<int>(type: "integer", nullable: true),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    VotesJson = table.Column<string>(type: "text", nullable: false),
                    Applied = table.Column<bool>(type: "boolean", nullable: false),
                    DryRun = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrchestratorDecisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrchestratorDecisions_AtUtc",
                table: "OrchestratorDecisions",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OrchestratorDecisions_RunId_Kind",
                table: "OrchestratorDecisions",
                columns: new[] { "RunId", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrchestratorDecisions");
        }
    }
}
