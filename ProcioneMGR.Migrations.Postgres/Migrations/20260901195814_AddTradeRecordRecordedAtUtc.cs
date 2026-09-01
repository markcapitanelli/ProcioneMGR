using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTradeRecordRecordedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // [K41] DUE PASSI, E L'ORDINE È IL PUNTO.
            //
            // `ADD COLUMN ... DEFAULT (now() at time zone 'utc')` in un colpo solo **riempie anche
            // le righe esistenti**: `now()` è STABLE, quindi Postgres la tratta come una costante e
            // le 369 righe storiche di questa tabella dichiarerebbero tutte di essere state scritte
            // nell'istante della migrazione. Sarebbe esattamente la bugia che questa colonna nasce
            // per impedire — e la peggiore delle due, perché farebbe passare per «viva» ogni riga
            // di replay già in archivio.
            //
            // Quindi: prima la colonna NUDA (le righe storiche restano NULL, cioè «non lo so», che
            // è la verità), poi il default, che da lì in avanti vale solo per le righe NUOVE.
            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                table: "TradeRecords",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.Sql(
                "ALTER TABLE \"TradeRecords\" ALTER COLUMN \"RecordedAtUtc\" SET DEFAULT (now() at time zone 'utc');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                table: "TradeRecords");
        }
    }
}
