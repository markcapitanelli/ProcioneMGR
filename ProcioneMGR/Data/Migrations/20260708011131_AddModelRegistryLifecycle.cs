using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelRegistryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DeflatedSharpe",
                table: "SavedMlModels",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExperimentRunId",
                table: "SavedMlModels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromotedAtUtc",
                table: "SavedMlModels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetiredAtUtc",
                table: "SavedMlModels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetiredReason",
                table: "SavedMlModels",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetrainRequestedAtUtc",
                table: "SavedMlModels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "SavedMlModels",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Staging");

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "SavedMlModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SavedMlModels_Symbol_Timeframe_Stage",
                table: "SavedMlModels",
                columns: new[] { "Symbol", "Timeframe", "Stage" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedMlModels_Symbol_Timeframe_Stage",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "DeflatedSharpe",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "ExperimentRunId",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "PromotedAtUtc",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "RetiredAtUtc",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "RetiredReason",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "RetrainRequestedAtUtc",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SavedMlModels");
        }
    }
}
