using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeRecordStrategyClosedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TradeRecords_StrategyId_ClosedAtUtc",
                table: "TradeRecords",
                columns: new[] { "StrategyId", "ClosedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradeRecords_StrategyId_ClosedAtUtc",
                table: "TradeRecords");
        }
    }
}
