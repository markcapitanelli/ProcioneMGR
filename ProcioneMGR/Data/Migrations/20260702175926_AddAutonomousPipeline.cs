using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomousPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PipelineArtifacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StageName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineArtifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExchangeName = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UniverseJson = table.Column<string>(type: "TEXT", nullable: false),
                    DateRangesJson = table.Column<string>(type: "TEXT", nullable: false),
                    StagesJson = table.Column<string>(type: "TEXT", nullable: false),
                    InitialCapital = table.Column<decimal>(type: "TEXT", nullable: false),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutionMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Schedule = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigurationId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ContextSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    StageSummariesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Conclusion = table.Column<string>(type: "TEXT", nullable: false),
                    RecommendationJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorLog = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineArtifacts_RunId",
                table: "PipelineArtifacts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineConfigurations_CreatedBy",
                table: "PipelineConfigurations",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ConfigurationId",
                table: "PipelineRuns",
                column: "ConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_StartedAt",
                table: "PipelineRuns",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PipelineArtifacts");

            migrationBuilder.DropTable(
                name: "PipelineConfigurations");

            migrationBuilder.DropTable(
                name: "PipelineRuns");
        }
    }
}
