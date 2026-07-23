using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddKlineExtendedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "QuoteVolume",
                table: "OhlcvData",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TakerBuyQuoteVolume",
                table: "OhlcvData",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TakerBuyVolume",
                table: "OhlcvData",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TradeCount",
                table: "OhlcvData",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuoteVolume",
                table: "OhlcvData");

            migrationBuilder.DropColumn(
                name: "TakerBuyQuoteVolume",
                table: "OhlcvData");

            migrationBuilder.DropColumn(
                name: "TakerBuyVolume",
                table: "OhlcvData");

            migrationBuilder.DropColumn(
                name: "TradeCount",
                table: "OhlcvData");
        }
    }
}
