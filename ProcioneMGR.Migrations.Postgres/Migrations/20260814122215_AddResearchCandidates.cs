using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddResearchCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunCompletedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    StrategyName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CandidateKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: false),
                    WalkForwardOosSharpe = table.Column<decimal>(type: "numeric", nullable: false),
                    SelectionSharpe = table.Column<decimal>(type: "numeric", nullable: false),
                    SelectionReturn = table.Column<decimal>(type: "numeric", nullable: false),
                    SelectionMaxDrawdown = table.Column<decimal>(type: "numeric", nullable: false),
                    SelectionTrades = table.Column<int>(type: "integer", nullable: false),
                    HoldoutSharpe = table.Column<decimal>(type: "numeric", nullable: false),
                    HoldoutReturn = table.Column<decimal>(type: "numeric", nullable: false),
                    HoldoutMaxDrawdown = table.Column<decimal>(type: "numeric", nullable: false),
                    HoldoutProfitFactor = table.Column<decimal>(type: "numeric", nullable: false),
                    HoldoutTrades = table.Column<int>(type: "integer", nullable: false),
                    Survived = table.Column<bool>(type: "boolean", nullable: false),
                    RejectReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DeflatedSharpe = table.Column<double>(type: "double precision", nullable: true),
                    PanelPbo = table.Column<double>(type: "double precision", nullable: true),
                    PermutationPValue = table.Column<double>(type: "double precision", nullable: true),
                    NullTwinPercentile = table.Column<double>(type: "double precision", nullable: true),
                    BestStopVariant = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsGrey = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchCandidates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchCandidates_Run_Candidato",
                table: "ResearchCandidates",
                columns: new[] { "RunId", "CandidateKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchCandidates_Symbol_Timeframe_RunCompletedUtc",
                table: "ResearchCandidates",
                columns: new[] { "Symbol", "Timeframe", "RunCompletedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResearchCandidates");
        }
    }
}
