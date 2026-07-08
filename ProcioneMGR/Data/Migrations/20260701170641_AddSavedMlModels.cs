using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedMlModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedMlModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ModelType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    TrainingDataFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrainingDataTo = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ForwardHorizon = table.Column<int>(type: "INTEGER", nullable: false),
                    FactorsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModelBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TrainRowCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TrainCorrelation = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedMlModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedMlModels_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedMlModels_UserId",
                table: "SavedMlModels",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedMlModels");
        }
    }
}
