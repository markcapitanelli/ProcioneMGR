-- [J21, PRD-AUTONOMIA-OPERATIVA-2026-08 §4 rilievo 8] Bonifica dei TradeRecords col tempo invertito.
--
-- IL FATTO (misurato il 2026-08-25): 5 righe (Id 159, 248, 269, 283, 292) hanno
-- ClosedAtUtc PRECEDENTE a OpenedAtUtc, con scarti di 18-29 giorni e Duration NEGATIVA
-- persistita. Tutte Paper, tutte chiusure protettive (TakeProfit/StopLoss), tutte precedenti
-- al 2026-08-19.
--
-- LA CAUSA (dimostrabile dalle righe stesse): l'uscita protettiva è stata valutata su una
-- CANDELA STORICA riconsegnata durante un recupero dati, e ClosedAtUtc ha preso il timestamp
-- della candela vecchia — un trade "chiuso" su un mercato che precede la sua stessa apertura.
-- Il codice ora lo impedisce due volte (guardia in TradingEngine.ApplyProtectiveExitsAsync:
-- una candela più vecchia dell'apertura non riguarda la posizione; cintura in
-- PositionCloser.ClampCloseTimestamp): questo script bonifica SOLO lo storico già scritto.
--
-- LA SCELTA: si CORREGGE (clamp di ClosedAtUtc a OpenedAtUtc, Duration a zero) invece di
-- limitarsi a marcare, perché la causa è dimostrabile dalla riga stessa; ma il timestamp vero
-- di chiusura è irrecuperabile, quindi la correzione è dichiarata nell'ExitReason — chi legge
-- la riga deve sapere che quella durata è un limite inferiore convenzionale, non una misura.
--
-- ESEGUIRE IN TRANSAZIONE. Prima il backup, poi la correzione. Idempotente: al secondo giro
-- il WHERE non trova righe.

BEGIN;

-- 1. Quarantena: copia integrale delle righe PRIMA di toccarle.
CREATE TABLE IF NOT EXISTS "TradeRecordsQuarantena2026_08"
    (LIKE "TradeRecords" INCLUDING ALL);

INSERT INTO "TradeRecordsQuarantena2026_08"
SELECT * FROM "TradeRecords"
WHERE "ClosedAtUtc" < "OpenedAtUtc"
  AND "Id" NOT IN (SELECT "Id" FROM "TradeRecordsQuarantena2026_08");

-- 2. La correzione, dichiarata nella riga stessa.
UPDATE "TradeRecords"
SET "ClosedAtUtc" = "OpenedAtUtc",
    "Duration"    = INTERVAL '0',
    "ExitReason"  = COALESCE("ExitReason", '') || ' [J21 2026-08-25: ClosedAtUtc era ' ||
                    to_char("ClosedAtUtc", 'YYYY-MM-DD HH24:MI') ||
                    ', precedente all''apertura (candela storica riconsegnata): bloccato all''apertura, durata non misurabile]'
WHERE "ClosedAtUtc" < "OpenedAtUtc";

-- 3. Verifica: zero righe residue col tempo invertito, e le quarantenate ci sono.
SELECT
    (SELECT count(*) FROM "TradeRecords" WHERE "ClosedAtUtc" < "OpenedAtUtc")   AS residue_invertite,
    (SELECT count(*) FROM "TradeRecordsQuarantena2026_08")                       AS in_quarantena;

COMMIT;
