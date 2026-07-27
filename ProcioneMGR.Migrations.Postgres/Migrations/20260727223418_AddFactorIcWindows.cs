using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFactorIcWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FactorIcWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FactorName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ForwardHorizon = table.Column<int>(type: "integer", nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false),
                    WindowStartUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    WindowEndUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    InformationCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactorIcWindows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FactorIcWindows_Serie_Fattore_Finestra",
                table: "FactorIcWindows",
                columns: new[] { "Symbol", "Timeframe", "FactorName", "ForwardHorizon", "WindowSize", "WindowEndUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FactorIcWindows_Symbol_Timeframe_WindowEndUtc",
                table: "FactorIcWindows",
                columns: new[] { "Symbol", "Timeframe", "WindowEndUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FactorIcWindows");
        }
    }
}
