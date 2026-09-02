using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // [K51] La colonna nasce NULLABLE, si riempie, e solo dopo diventa obbligatoria: con
            // un defaultValue le 137 righe storiche prenderebbero una stringa vuota, cioe' un
            // quinto stato che non significa niente.
            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "OrchestratorDecisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Le righe precedenti si DERIVANO dal booleano che c'era, e va detto che e' una
            // derivazione e non una misura: il vecchio  portava tre significati schiacciati
            // insieme, quindi questa lettura e' la migliore possibile, non la verita' di allora.
            //   Applied = true            -> l'azione e' avvenuta
            //   Applied = false + Error   -> tentata e fallita
            //   Applied = false, no Error -> non tentata per una regola (gate, dry-run, budget)
            // Nessuna riga storica puo' diventare 'Intended' o 'Unknown': quegli stati non erano
            // esprimibili prima di oggi, e inventarli a posteriori sarebbe scrivere una storia che
            // nessuno ha osservato.
            migrationBuilder.Sql(@"
                UPDATE ""OrchestratorDecisions""
                SET ""Outcome"" = CASE
                    WHEN ""Applied"" THEN 'Applied'
                    WHEN ""Error"" IS NOT NULL THEN 'Failed'
                    ELSE 'Refused'
                END
                WHERE ""Outcome"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Outcome",
                table: "OrchestratorDecisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "OrchestratorDecisions");
        }
    }
}
