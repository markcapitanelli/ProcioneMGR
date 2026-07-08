using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFuturesTrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Leverage",
                table: "TradingEngineStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "MarketType",
                table: "TradingEngineStates",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "Spot");

            migrationBuilder.AddColumn<int>(
                name: "Leverage",
                table: "TradeRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "MarketType",
                table: "TradeRecords",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "Spot");

            migrationBuilder.AddColumn<bool>(
                name: "WasLiquidated",
                table: "TradeRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Leverage",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "MarketType",
                table: "Orders",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "Spot");

            migrationBuilder.AddColumn<int>(
                name: "Leverage",
                table: "OpenPositions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "LiquidationPrice",
                table: "OpenPositions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarginBalance",
                table: "OpenPositions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "TradingEngineStates");

            migrationBuilder.DropColumn(
                name: "MarketType",
                table: "TradingEngineStates");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "TradeRecords");

            migrationBuilder.DropColumn(
                name: "MarketType",
                table: "TradeRecords");

            migrationBuilder.DropColumn(
                name: "WasLiquidated",
                table: "TradeRecords");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "MarketType",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Leverage",
                table: "OpenPositions");

            migrationBuilder.DropColumn(
                name: "LiquidationPrice",
                table: "OpenPositions");

            migrationBuilder.DropColumn(
                name: "MarginBalance",
                table: "OpenPositions");
        }
    }
}
