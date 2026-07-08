using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTradingLaneSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "TradingEngineStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "TradingAuditLogs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "TradeRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "OpenPositions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "EnsembleStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LaneId",
                table: "EnsembleRebalanceHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "TradingEngineStates");

            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "TradingAuditLogs");

            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "TradeRecords");

            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "OpenPositions");

            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "EnsembleStates");

            migrationBuilder.DropColumn(
                name: "LaneId",
                table: "EnsembleRebalanceHistory");
        }
    }
}
