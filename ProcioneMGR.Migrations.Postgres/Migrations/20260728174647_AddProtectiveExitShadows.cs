using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddProtectiveExitShadows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProtectiveExitShadows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    PositionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Side = table.Column<int>(type: "integer", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DetectedPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    DetectedReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ShadowFillPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualExitAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ActualFillPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LeadSeconds = table.Column<double>(type: "double precision", nullable: false),
                    DelayCostBps = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectiveExitShadows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtectiveExitShadows_LaneId_ActualExitAtUtc",
                table: "ProtectiveExitShadows",
                columns: new[] { "LaneId", "ActualExitAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtectiveExitShadows");
        }
    }
}
