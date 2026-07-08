using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExperimentTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExperimentArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KindTag = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Timeframe = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ParametersHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MetricsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorLog = table.Column<string>(type: "TEXT", nullable: true)
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
