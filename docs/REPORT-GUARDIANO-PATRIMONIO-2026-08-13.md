# Guardiano di profondità delle serie-patrimonio — report di costruzione e collaudo (2026-08-13)

Chiude il chip aperto dalla chiusura dell'ondata Risanamento
(`REPORT-RISANAMENTO-CHIUSURA-2026-08.md` §1 e §4): *«una serie-patrimonio può sparire senza che
nessun controllo lo dica: serve un guardiano sulla PROFONDITÀ delle serie esenti, non solo
sull'esenzione dalla purge»*.

## 1. Perché esiste

Le tre serie di `SentimentMetricPoints` ESENTI dalla purge di `SentimentSyncWorker` sono patrimonio
storico, non cache:

| Serie | Chiave | Perché è patrimonio |
|---|---|---|
| Funding storico | Source=BinanceFutures, Metric=FundingRate | backfill profondo dal 2019 (T0.2), alimenta carry e backtest a leva |
| Fear & Greed | Source=FearGreed | baseline lungo (~3.100 punti dal 2018-02), cicli interi |
| Liquidazioni | Source=BinanceLiquidations | accumulo F4: il dato NON è ricostruibile a posteriori |

La storia del funding è andata persa **due volte in silenzio** (2026-07-24 costruendo F4;
2026-08-11 alla rimisura del carry) **con l'esenzione dalla purge al suo posto**: la finestra
residua combaciava con `MetricRetentionDays` (400 gg), il sospetto è una perdita della tabella con
re-backfill della sola finestra del worker. Carry e backtest a leva leggevano ~14 mesi credendoli
7 anni, e nessun controllo lo diceva. L'esenzione protegge *dal worker*; il guardiano misura che la
storia *ci sia*, qualunque sia stata la via della perdita.

## 2. Cosa è stato costruito

Tutto in `ProcioneMGR/Services/Sentiment/SentimentHeritageGuard.cs` (commit `9f66edc`):

- **`SentimentHeritageGuardWorker`** (BackgroundService nel guscio, ogni 6 h, primo giro ~2 min dopo
  il boot, `RunOnceAsync` pubblico per test e «Controlla ora»): misura `min(TimestampUtc)` e
  conteggio per serie — funding **per simbolo**, F&G e liquidazioni sull'intera fonte — contro le
  soglie dichiarate in `Sentiment:HeritageGuard`.
- **Su violazione**: log **Error a ogni giro** (guasto attivo, non evento passato) + **una** notifica
  Critical aggregata **per transizione** (pattern `SeriesFreshnessWatchWorker`: un guasto nuovo suona
  una volta, il rientro riarma in silenzio, una nuova perdita risuona), col comando di ripristino nel
  corpo: `dotnet run --project tools/PlatformExpand -- fundingbackfill`.
- **`SentimentHeritageSnapshot`** (singleton, sostituzione atomica): card «Profondità delle
  serie-patrimonio» in `/sentiment` (tabella misurato-contro-dichiarato, badge, «Controlla ora»,
  timestamp dell'ultimo controllo) e **alert rosso in Home** (assenti per prime, zero I/O al
  rendering). Una serie **assente** è la violazione più grave, non un caso da saltare.
- **Pannello soglie** nel blocco Sentiment di `/admin/autonomy` (mandato tutto-da-UI), regole
  server-side in `AdminConfigRules`: intervallo ≥ 1 h, soglie ≥ 1 punto, date-àncora nel passato
  (nel futuro sarebbero una violazione perpetua).
- **Nessuna azione automatica**: il ripristino resta una scelta umana.

### Chiavi di configurazione (`Sentiment:HeritageGuard`)

| Chiave | Default | Note |
|---|---|---|
| `Enabled` | true | per-tick (hot); «Controlla ora» gira comunque |
| `CheckIntervalHours` | 6 | letto al boot ⟳ |
| `FundingSymbols` | vuota → BTC, ETH, SOL, BNB, XRP, DOGE | vuota nel POCO per la trappola del binder che APPENDE le liste ai default |
| `FundingMinStartUtc` | **2020-10-01** | vedi §3.1 |
| `FundingMinEventsPerSymbol` | 5000 | ~3/giorno: dal 2019 sono ~7.000 |
| `FearGreedMinStartUtc` / `FearGreedMinPoints` | 2019-01-01 / 2000 | la fonte parte dal 2018-02 |
| `LiquidationsEnforced` | **true** | vedi §3.2 |
| `LiquidationsMinStartUtc` / `LiquidationsMinPoints` | 2026-08-01 / 100 | àncora dell'accumulo F4 |

## 3. Il collaudo a browser (profilo `procione-reale`, DB vero) ha trovato due cose

Classe di difetto già nota: *trovabili SOLO aprendo la pagina sull'app vera*.

### 3.1 L'àncora del funding deve stare dopo il LISTING del mercato

Col default iniziale 2020-01-01, **quattro serie COMPLETE risultavano VIOLATE**: una serie non può
precedere il listing del suo mercato USDS-M. Date misurate sul DB vero: BTC 2019-09-10,
ETH 2019-11-27, XRP 2020-01-06, BNB 2020-02-10, DOGE 2020-07-10, SOL 2020-09-13. Default corretto a
**2020-10-01** (dopo SOL, il più tardo): il taglio dell'incidente vero (storia dal 2025-06) resta
preso con quasi cinque anni di margine. Dopo la taratura: 6 funding OK, F&G OK.

### 3.2 Le liquidazioni sono a ZERO punti totali — violazione vera, ma perpetua da questa postazione

Non è la perdita della tabella: dalle postazioni EEA lo stream futures Binance è **muto** (blocco
MiCA sul market-data derivati, documentato in `LiquidationSyncWorker` dal 2026-07-24 — handshake OK,
zero frame) e **l'accumulo non è mai partito**. L'allarme resta acceso di default perché è la
verità; per le postazioni strutturalmente bloccate esiste l'interruttore **«Sorveglia liquidazioni»**
(`LiquidationsEnforced=false`): la riga resta MISURATA e mostrata come «NON SORVEGLIATA» — mai un OK
finto. La decisione di spegnerlo è del proprietario.

## 4. Verifica (4 livelli)

| Livello | Esito |
|---|---|
| L1 unità | snapshot (ordinamento, assenti prime), default simboli, regole admin — 3+3 test |
| L2 controllo sul rumore | su serie profonde il guardiano TACE per tre giri consecutivi; soglia 0 punti e data futura RIFIUTATE dalla validazione |
| L3 integrazione (Postgres/Testcontainers) | 8 casi: troncatura tipo incidente 2026-08-11 (notificata UNA volta), serie assente, conteggio sotto soglia con storia profonda, ciclo perdita→ripristino→seconda perdita (riarmo), 4 violazioni = 1 notifica aggregata, filtri Source+Metric+Symbol (200 punti di OpenInterest non salvano il funding), interruttore liquidazioni; + binding dall'example |
| L4 browser (app viva, DB vero) | card /sentiment, «Controlla ora», alert Home in entrambe le configurazioni, pannello /admin/autonomy; le due scoperte del §3 vengono da qui |

Suite mirata alla chiusura: 78/78 (guardiano + binding + regole admin + copertura UI).

## 5. Come si risponde a un allarme

1. **Non toccare le soglie**: dicono cosa DEVE esserci, non cosa c'è.
2. Funding corto/assente → `dotnet run --project tools/PlatformExpand -- fundingbackfill`
   (idempotente), poi «Controlla ora» in `/sentiment` e verificare le righe OK.
3. F&G corto → la fonte (alternative.me) serve la storia intera: un tick del worker sentiment la
   ricostruisce.
4. Liquidazioni assenti → NON ricostruibili. Se la postazione è bloccata (MiCA), la scelta onesta è
   l'interruttore «Sorveglia liquidazioni» OFF, non alzare le soglie.
