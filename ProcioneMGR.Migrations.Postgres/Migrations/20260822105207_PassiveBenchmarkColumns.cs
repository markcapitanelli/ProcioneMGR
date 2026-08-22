using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PassiveBenchmarkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DominantDirection",
                table: "ResearchCandidates",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExcessHoldoutSharpe",
                table: "ResearchCandidates",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NetExposure",
                table: "ResearchCandidates",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PassiveHoldoutSharpe",
                table: "ResearchCandidates",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TimeInMarketFraction",
                table: "ResearchCandidates",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DominantDirection",
                table: "ResearchCandidates");

            migrationBuilder.DropColumn(
                name: "ExcessHoldoutSharpe",
                table: "ResearchCandidates");

            migrationBuilder.DropColumn(
                name: "NetExposure",
                table: "ResearchCandidates");

            migrationBuilder.DropColumn(
                name: "PassiveHoldoutSharpe",
                table: "ResearchCandidates");

            migrationBuilder.DropColumn(
                name: "TimeInMarketFraction",
                table: "ResearchCandidates");
        }
    }
}
