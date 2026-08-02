using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddHostHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostHeartbeats",
                columns: table => new
                {
                    Host = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostHeartbeats", x => x.Host);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostHeartbeats");
        }
    }
}
