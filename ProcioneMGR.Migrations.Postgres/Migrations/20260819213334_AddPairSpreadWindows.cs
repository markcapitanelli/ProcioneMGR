using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPairSpreadWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PairSpreadWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PairKeyValue = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    SymbolY = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SymbolX = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Estimator = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AdfStatistic = table.Column<double>(type: "double precision", nullable: false),
                    CriticalValue = table.Column<double>(type: "double precision", nullable: false),
                    IsStationaryWindow = table.Column<bool>(type: "boolean", nullable: false),
                    HedgeRatio = table.Column<double>(type: "double precision", nullable: false),
                    SpreadMean = table.Column<double>(type: "double precision", nullable: false),
                    SpreadStdDev = table.Column<double>(type: "double precision", nullable: false),
                    LastZScore = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairSpreadWindows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairSpreadWindows_PairKeyValue_Estimator_WindowEndUtc",
                table: "PairSpreadWindows",
                columns: new[] { "PairKeyValue", "Estimator", "WindowEndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PairSpreadWindows_Serie_Finestra",
                table: "PairSpreadWindows",
                columns: new[] { "PairKeyValue", "Estimator", "WindowSize", "WindowEndUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairSpreadWindows");
        }
    }
}
