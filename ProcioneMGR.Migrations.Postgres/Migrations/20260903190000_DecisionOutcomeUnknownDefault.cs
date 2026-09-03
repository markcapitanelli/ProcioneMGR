using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcioneMGR.Data;

#nullable disable

namespace ProcioneMGR.Migrations.Postgres.Migrations
{
    /// <summary>
    /// [Revisione del filone K, 2026-09-03] <b>Il default di <c>OrchestratorDecisions.Outcome</c>
    /// passa da <c>'Applied'</c> a <c>'Unknown'</c>, e le righe che quel default ha etichettato
    /// male vengono riclassificate.</b>
    ///
    /// <para><b>Il fatto.</b> <c>DecisionOutcomeDefault</c> (2026-09-02) ha messo <c>'Applied'</c>
    /// come default per rendere la colonna scrivibile da un binario che non la conosce — giusto il
    /// gesto, sbagliato il valore: chi scrive senza dichiarare l'esito <b>non lo sa</b>, e lo stato
    /// di successo non può essere ciò che si ottiene per omissione. In più sette scrittori del
    /// worker di flotta non impostavano <c>Outcome</c>, quindi ogni riga <c>Blocked</c>, ogni
    /// rifiuto del gate e ogni ritiro FALLITO risultava «eseguita» nel pannello (visto dal vivo il
    /// 2026-09-03: righe <c>Blocked</c> ogni quindici minuti col badge verde).</para>
    ///
    /// <para><b>La riclassificazione</b> tocca SOLO le righe contraddittorie — <c>Outcome='Applied'</c>
    /// con <c>"Applied"=false</c> — e usa la stessa derivazione onesta di <c>AddDecisionOutcome</c>:
    /// <c>Error</c> valorizzato ⇒ <c>Failed</c>; <c>Blocked</c>/<c>RetirePending</c> ⇒ <c>Noted</c>
    /// (annotazioni, non azioni); altrimenti ⇒ <c>Refused</c>. Le righe con <c>"Applied"=true</c>
    /// non si toccano. Idempotente: al secondo giro non trova nulla da riclassificare.</para>
    ///
    /// <para><b>Espandi-poi-contrai</b>: il default resta presente (cambia solo il valore), quindi
    /// un binario vecchio continua a scrivere senza errori nella finestra fra migrazione e rilascio.</para>
    /// </summary>
    // I DUE ATTRIBUTI NON SONO CERIMONIA: senza, EF non vede la migrazione (2026-09-02, #131).
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260903190000_DecisionOutcomeUnknownDefault")]
    public partial class DecisionOutcomeUnknownDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "OrchestratorDecisions" ALTER COLUMN "Outcome" SET DEFAULT 'Unknown';""");

            migrationBuilder.Sql(
                """
                UPDATE "OrchestratorDecisions"
                SET "Outcome" = CASE
                    WHEN "Kind" = 'Retire' AND ("Error" LIKE 'corsia già ferma%' OR "Error" LIKE 'corsia in %non Paper%') THEN 'Refused'
                    WHEN "Error" IS NOT NULL AND "Error" <> '' THEN 'Failed'
                    WHEN "Kind" IN ('Blocked', 'RetirePending') THEN 'Noted'
                    ELSE 'Refused'
                END
                WHERE "Outcome" = 'Applied' AND "Applied" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Si ripristina il default precedente. La riclassificazione NON si inverte: riportare a
            // 'Applied' righe che dichiarano Applied=false ricreerebbe la contraddizione di partenza.
            migrationBuilder.Sql(
                """ALTER TABLE "OrchestratorDecisions" ALTER COLUMN "Outcome" SET DEFAULT 'Applied';""");
        }
    }
}
