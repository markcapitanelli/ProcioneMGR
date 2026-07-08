using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegimeDetectionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegimeModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExchangeName = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    TrainedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrainingDataFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrainingDataTo = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NumberOfRegimes = table.Column<int>(type: "INTEGER", nullable: false),
                    CentroidsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FeatureScalingJson = table.Column<string>(type: "TEXT", nullable: false),
                    RegimeProfilesJson = table.Column<string>(type: "TEXT", nullable: false),
                    SilhouetteScore = table.Column<double>(type: "REAL", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegimeModels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegimeModels_ExchangeName_Symbol_Timeframe_IsActive",
                table: "RegimeModels",
                columns: new[] { "ExchangeName", "Symbol", "Timeframe", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegimeModels");
        }
    }
}
