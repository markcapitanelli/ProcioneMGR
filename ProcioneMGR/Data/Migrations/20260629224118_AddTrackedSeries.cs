using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedSeries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Exchange = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedSeries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedSeries_Exchange_Symbol_Timeframe",
                table: "TrackedSeries",
                columns: new[] { "Exchange", "Symbol", "Timeframe" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackedSeries");
        }
    }
}
