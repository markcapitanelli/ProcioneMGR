using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddPairCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PairCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunCompletedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SymbolY = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SymbolX = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    PairKeyValue = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    AdfStatistic = table.Column<double>(type: "double precision", nullable: false),
                    IsCointegrated = table.Column<bool>(type: "boolean", nullable: false),
                    HedgeRatio = table.Column<double>(type: "double precision", nullable: false),
                    IsHedgeRatioPlausible = table.Column<bool>(type: "boolean", nullable: false),
                    AlignedCandles = table.Column<int>(type: "integer", nullable: false),
                    IsTradeable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairCandidates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairCandidates_PairKeyValue_RunCompletedUtc",
                table: "PairCandidates",
                columns: new[] { "PairKeyValue", "RunCompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PairCandidates_Run_Coppia",
                table: "PairCandidates",
                columns: new[] { "RunId", "PairKeyValue" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairCandidates");
        }
    }
}
