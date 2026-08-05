using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTradePostMortems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TradePostMortems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    TradeRecordId = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StrategyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PnlPercent = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    Cause = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FactsJson = table.Column<string>(type: "text", nullable: false),
                    Narrative = table.Column<string>(type: "text", nullable: false),
                    ModelUsed = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradePostMortems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TradePostMortems_LaneId_CreatedAtUtc",
                table: "TradePostMortems",
                columns: new[] { "LaneId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TradePostMortems_TradeRecordId",
                table: "TradePostMortems",
                column: "TradeRecordId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TradePostMortems");
        }
    }
}
