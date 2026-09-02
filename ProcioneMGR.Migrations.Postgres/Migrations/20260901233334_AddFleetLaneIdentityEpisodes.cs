using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddFleetLaneIdentityEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FleetLaneIdentityEpisodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LaneId = table.Column<int>(type: "integer", nullable: false),
                    Identity = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ObservedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    NextIdentity = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetLaneIdentityEpisodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FleetLaneIdentityEpisodes_LaneId_FirstSeenUtc",
                table: "FleetLaneIdentityEpisodes",
                columns: new[] { "LaneId", "FirstSeenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FleetLaneIdentityEpisodes");
        }
    }
}
