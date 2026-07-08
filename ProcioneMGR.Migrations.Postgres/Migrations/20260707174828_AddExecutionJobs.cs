using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    StrategyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PositionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MarketType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TotalQuantity = table.Column<decimal>(type: "numeric", nullable: false),
                    FilledQuantity = table.Column<decimal>(type: "numeric", nullable: false),
                    EntryPriceWeightedAvg = table.Column<decimal>(type: "numeric", nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    SlicesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_LaneId_Status",
                table: "ExecutionJobs",
                columns: new[] { "LaneId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJobs_PositionId",
                table: "ExecutionJobs",
                column: "PositionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionJobs");
        }
    }
}
