using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AuditHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MaxDrawdownPercent",
                table: "TradingEngineStates",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "OpenedInMode",
                table: "OpenPositions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StopOrderId",
                table: "OpenPositions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TakeProfitOrderId",
                table: "OpenPositions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DriftCheckResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModelId = table.Column<int>(type: "integer", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TotalFeatures = table.Column<int>(type: "integer", nullable: false),
                    DriftingFeatures = table.Column<int>(type: "integer", nullable: false),
                    AlertFeatures = table.Column<int>(type: "integer", nullable: false),
                    Overall = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TopFeaturesJson = table.Column<string>(type: "text", nullable: true),
                    ChampionRetired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriftCheckResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DriftCheckResults_CheckedAtUtc",
                table: "DriftCheckResults",
                column: "CheckedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DriftCheckResults_ModelId_CheckedAtUtc",
                table: "DriftCheckResults",
                columns: new[] { "ModelId", "CheckedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DriftCheckResults");

            migrationBuilder.DropColumn(
                name: "MaxDrawdownPercent",
                table: "TradingEngineStates");

            migrationBuilder.DropColumn(
                name: "OpenedInMode",
                table: "OpenPositions");

            migrationBuilder.DropColumn(
                name: "StopOrderId",
                table: "OpenPositions");

            migrationBuilder.DropColumn(
                name: "TakeProfitOrderId",
                table: "OpenPositions");
        }
    }
}
