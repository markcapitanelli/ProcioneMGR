# B2 — Il gate non sapeva vedere una serie ferma (2026-07-28)

*Chiude una cecità del gate B2 del [PRD-INTEGRAZIONE-CORE-CALDO](PRD-INTEGRAZIONE-CORE-CALDO.md),
trovata guardando il database invece dei documenti.*

## 1. Il difetto

Il gate B2 chiede «7 giorni di sync senza buchi nelle candele», con scadenza 2026-08-02. Entrambi
gli strumenti che dovevano misurarlo erano ciechi **per costruzione**:

1. **Lo stato di sync.** `MarketDataSyncService` scriveva `OK: {CandlesProcessed} candele`, cioè
   deduceva la salute dal numero di righe toccate. Su una serie che ha smesso di avanzare, il
   cursore incrementale riparte dall'ultima candela nota, l'exchange gliela restituisce e l'upsert
   la riscrive: **`OK: 1 candele` a ogni giro, per sempre**. MKR/USDT lo ha dichiarato per dieci
   mesi.
2. **L'audit di copertura** (`PlatformExpand coverage`) misurava le candele presenti sull'intervallo
   `[prima, ultima]` della serie **stessa**. Una serie ferma ha copertura 100% del proprio passato:
   non poteva accorgersene nemmeno in linea di principio.

Il riferimento sbagliato era comune a entrambi: si guardava la serie contro sé stessa, e una serie
morta è perfettamente coerente con sé stessa.

## 2. La regola

`SeriesFreshness` misura il ritardo contro **adesso** — l'unico riferimento che non si sposta
insieme al guasto — ed è **una sola**, condivisa da sync e audit. Due regole darebbero due verdetti
sulla stessa serie: è il difetto già trovato e corretto in D2 col monitor di deriva, e non valeva la
pena rifarlo.

Il riferimento è l'ultima barra **chiusa**, non quella in formazione. La differenza sembra un
dettaglio: la barra in formazione a database c'è solo se il ciclo di sync è passato mentre era
aperta, quindi una serie sana la ha o non la ha a seconda del momento in cui la si guarda, e il
ritardo oscillerebbe fra 0 e 1 senza che sia successo niente. Un allarme che lampeggia da solo è
rumore, cioè il modo migliore per non farlo leggere.

Tolleranza di default **3 barre**: l'exchange pubblica con un ritardo suo e il ciclo gira ogni 5
minuti. Configurabile con `MarketData:StaleAfterBars`.

Il worker segnala con `LogWarning` ma **non disabilita da solo**: spegnere una serie è una scelta
umana, e uno stop dell'exchange può essere temporaneo.

## 3. Cosa ha trovato: 7 serie ferme su 228

```
MKR/USDT  15m  ultima 2025-09-15 02:45   30.388 barre indietro   [OK: 1 candele]
MKR/USDT  1h   ultima 2025-09-15 02:00    7.597 barre indietro   [OK: 1 candele]
MKR/USDT  4h   ultima 2025-09-15 00:00    1.899 barre indietro   [OK: 1 candele]
MKR/USDT  1d   ultima 2025-09-15 00:00      315 barre indietro   [OK: 1 candele]
TON/USDT  1h   ultima 2026-06-30 02:00      685 barre indietro   [OK: 1 candele]
TON/USDT  4h   ultima 2026-06-30 00:00      171 barre indietro   [OK: 1 candele]
TON/USDT  1d   ultima 2026-06-30 00:00       27 barre indietro   [OK: 1 candele]
```

Fra parentesi quadre lo stato che la piattaforma dichiarava fino a oggi, riga per riga accanto al
ritardo vero.

**Verificato all'origine**: `GET /api/v3/exchangeInfo` di Binance riporta `MKRUSDT` e `TONUSDT` in
stato **`BREAK`** (scambi sospesi), contro `TRADING` di `BTCUSDT` usato come controllo. Le serie non
sono riparabili con un backfill: non c'è nulla da scaricare.

Le altre 221 serie abilitate risultano fresche.

## 4. Cosa resta da fare (azione umana)

Disabilitare le 7 serie morte — 7 clic su `/watchlist`, oppure:

```sql
UPDATE "TrackedSeries"
SET "Enabled" = false,
    "LastSyncStatus" = 'FERMA: disabilitata 2026-07-28 — Binance riporta stato BREAK (scambi sospesi)'
WHERE "Enabled" AND "Symbol" IN ('MKR/USDT','TON/USDT');
```

Reversibile: riabilitarle è un clic, e se Binance le rimette in `TRADING` il backfill le riprende dal
punto in cui si sono fermate.

**Finché restano abilitate il gate B2 non è verde**, e adesso lo dice a voce alta invece di lasciarlo
dedurre.

Una scelta che resta al proprietario e che NON ho preso: MKR è stato rinominato **SKY**. Aggiungere
`SKY/USDT` alla watchlist non è una riparazione ma un allargamento dell'universo di ricerca, e quella
è una decisione sua.

## 5. Come rifare la verifica

```bash
dotnet run --project tools/PlatformExpand -c Release -- coverage
```

La sezione «Serie ABILITATE ferme» in fondo all'audit OHLCV. Read-only.
