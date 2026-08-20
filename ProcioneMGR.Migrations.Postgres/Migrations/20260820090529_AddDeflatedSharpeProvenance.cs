using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDeflatedSharpeProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeflatedSharpeSource",
                table: "SavedMlModels",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeflatedSharpeTrials",
                table: "SavedMlModels",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeflatedSharpeSource",
                table: "SavedMlModels");

            migrationBuilder.DropColumn(
                name: "DeflatedSharpeTrials",
                table: "SavedMlModels");
        }
    }
}
