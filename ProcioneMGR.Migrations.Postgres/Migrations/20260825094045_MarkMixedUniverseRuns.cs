using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class MarkMixedUniverseRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MixedTimeframeUniverse",
                table: "PipelineRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // [J4] Backfill dallo SNAPSHOT del run (la verità al momento del run, non la config di
            // oggi che può essere cambiata): un run è "misto" se il suo Universe contava più di un
            // timeframe distinto. Per-riga e con l'eccezione inghiottita PER QUELLA riga: uno
            // snapshot illeggibile lascia il default false invece di far fallire il
            // migrate-on-startup dell'app — è un marcatore di lettura, non un dato di sicurezza.
            migrationBuilder.Sql("""
                DO $$
                DECLARE r RECORD;
                BEGIN
                    FOR r IN SELECT "Id", "ContextSnapshotJson" FROM "PipelineRuns"
                             WHERE "ContextSnapshotJson" IS NOT NULL AND "ContextSnapshotJson" <> '{}'
                    LOOP
                        BEGIN
                            IF (SELECT count(DISTINCT elem->>'Timeframe')
                                FROM jsonb_array_elements((r."ContextSnapshotJson")::jsonb->'Universe') elem) > 1
                            THEN
                                UPDATE "PipelineRuns" SET "MixedTimeframeUniverse" = TRUE WHERE "Id" = r."Id";
                            END IF;
                        EXCEPTION WHEN OTHERS THEN
                            -- snapshot non parsabile: resta false, e il run non viene marcato.
                            NULL;
                        END;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MixedTimeframeUniverse",
                table: "PipelineRuns");
        }
    }
}
