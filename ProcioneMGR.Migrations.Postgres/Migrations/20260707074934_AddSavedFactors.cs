using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedFactors : Migration
    {
        // Migrazione scritta a mano (gemella PostgreSQL della migrazione SQLite omonima): il generatore
        // EF `migrations add` è bloccato su Postgres da un drift pre-esistente snapshot↔modello. Le
        // operazioni qui sono puramente additive (CREATE TABLE SavedFactors + FK utente + indice) e
        // rispecchiano la configurazione Fluent di SavedFactor in ApplicationDbContext.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedFactors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Expression = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ForwardHorizon = table.Column<int>(type: "integer", nullable: false),
                    SelectionIc = table.Column<double>(type: "double precision", nullable: false),
                    HoldoutIc = table.Column<double>(type: "double precision", nullable: true),
                    Observations = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedFactors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedFactors_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedFactors_UserId",
                table: "SavedFactors",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedFactors");
        }
    }
}
