using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExchangeCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ExchangeName = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: false),
                    ApiSecret = table.Column<string>(type: "TEXT", nullable: false),
                    Passphrase = table.Column<string>(type: "TEXT", nullable: true),
                    IsTestnet = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeCredentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeCredentials_UserId",
                table: "ExchangeCredentials",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeCredentials");
        }
    }
}
