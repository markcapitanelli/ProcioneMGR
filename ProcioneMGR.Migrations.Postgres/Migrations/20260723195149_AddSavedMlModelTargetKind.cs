using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedMlModelTargetKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetKind",
                table: "SavedMlModels",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Backfill retro-compatibile: tutti i modelli salvati prima del campo predicevano rendimenti.
                defaultValue: "ForwardReturn");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetKind",
                table: "SavedMlModels");
        }
    }
}

