using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrailingStopToOpenPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BestPriceSinceEntry",
                table: "OpenPositions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TrailingStopPercent",
                table: "OpenPositions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BestPriceSinceEntry",
                table: "OpenPositions");

            migrationBuilder.DropColumn(
                name: "TrailingStopPercent",
                table: "OpenPositions");
        }
    }
}
