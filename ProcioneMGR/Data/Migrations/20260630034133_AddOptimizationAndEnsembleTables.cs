using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOptimizationAndEnsembleTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOptimized",
                table: "SavedStrategies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "OptimizationDate",
                table: "SavedStrategies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OptimizationSharpe",
                table: "SavedStrategies",
                type: "TEXT",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EnsembleRebalanceHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AllocationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnsembleRebalanceHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnsembleStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConfigurationJson = table.Column<string>(type: "TEXT", nullable: false),
                    StatusJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnsembleStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnsembleRebalanceHistory_Timestamp",
                table: "EnsembleRebalanceHistory",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnsembleRebalanceHistory");

            migrationBuilder.DropTable(
                name: "EnsembleStates");

            migrationBuilder.DropColumn(
                name: "IsOptimized",
                table: "SavedStrategies");

            migrationBuilder.DropColumn(
                name: "OptimizationDate",
                table: "SavedStrategies");

            migrationBuilder.DropColumn(
                name: "OptimizationSharpe",
                table: "SavedStrategies");
        }
    }
}
