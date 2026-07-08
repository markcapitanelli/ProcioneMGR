using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAltDataPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AltDataPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SymbolsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SentimentScore = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AltDataPoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AltDataPoints_DedupeKey",
                table: "AltDataPoints",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AltDataPoints_TimestampUtc",
                table: "AltDataPoints",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AltDataPoints");
        }
    }
}
