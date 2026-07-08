using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExperimentTracking : Migration
    {
        // Migrazione scritta a mano (gemella PostgreSQL della migrazione SQLite omonima). Il
        // generatore EF `migrations add` è attualmente bloccato su Postgres da un drift pre-esistente
        // fra lo snapshot e il modello (NullReferenceException nel differ su una colonna preesistente,
        // indipendente da questa feature che aggiunge solo due tabelle). Le operazioni qui sono
        // puramente additive (CREATE TABLE + indici) e rispecchiano esattamente la configurazione
        // Fluent di ExperimentRun/ExperimentArtifact in ApplicationDbContext.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExperimentArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    KindTag = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    ParametersHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetricsJson = table.Column<string>(type: "text", nullable: false),
                    ErrorLog = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentArtifacts_RunId",
                table: "ExperimentArtifacts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_Kind_StartedAt",
                table: "ExperimentRuns",
                columns: new[] { "Kind", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentRuns_ParametersHash",
                table: "ExperimentRuns",
                column: "ParametersHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExperimentArtifacts");

            migrationBuilder.DropTable(
                name: "ExperimentRuns");
        }
    }
}
