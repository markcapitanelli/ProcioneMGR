# 12 — Giro completo dell'applicazione, dal vivo

**Data:** 2026-08-04, 21:00–21:40 CEST · **Target:** `http://localhost:5199` · **Sessione:** autenticata
(cookie preesistente dell'utente; non ho mai inserito credenziali).

> A differenza del primo controllo ([07](07_BROWSER_CHECK_REPORT.md)), qui il **cluster kind è
> stato ripristinato**, quindi il core caldo risponde e l'applicazione è stata vista nel suo stato
> nominale. Tutti i numeri di questo documento sono letti a schermo, non dedotti dal codice.

---

## Come è stato ripristinato il cluster

Il black-out aveva lasciato l'API server irraggiungibile. **Causa individuata:** il container
`kind-apiproxy` (socat) inoltrava a `172.18.0.3:6443`, ma dopo il riavvio Docker aveva riassegnato
al control-plane l'indirizzo **`172.18.0.2`**. Il proxy puntava a un IP che non esisteva più.

```bash
docker rm -f kind-apiproxy && docker run -d --name kind-apiproxy --network kind -p 127.0.0.1:16443:6443 alpine/socat tcp-listen:6443,fork,reuseaddr tcp-connect:172.18.0.2:6443
```

Poi i due port-forward (`18092` trading, `18080` ingestion). Tutti i pod risultavano già `Running`:
il cluster non era mai caduto, era solo irraggiungibile dal client.

> **Da aggiungere alla procedura di ripristino documentata:** non basta rifare il proxy, va
> verificato **l'IP corrente del control-plane** — cambia a ogni riavvio di Docker.

## Comportamento verificato: il guscio si riconnette da solo

Domanda aperta dal primo audit: quando il core torna, serve riavviare l'applicazione?
**No.** Con l'app già in esecuzione, appena il port-forward è tornato disponibile il banner di dati
stantii è scomparso e `/trading` è passata a `RUNNING` senza alcun riavvio. Il canale gRPC
ristabilisce il subchannel da solo.

---

## Trading — `/trading`

Stato nominale: **`Trading Paper · SPOT · RUNNING · ultima candela 04/08 00:00 UTC · 0 barre indietro`**.

**Otto corsie**, tutte in Paper:

| Corsia | Simbolo | TF | Strategia | Osservazione |
|---|---|---|---|---|
| 0 | AAVE/USDT | 1d | RegimeConditional (Macd gated, holdout 0.80) — SL 17,03% / TP 33,94% | 8 gg |
| 1 | DOT/USDT | — | — | 8 gg, 1 trade |
| 2 | XLM/USDT | — | — | 8 gg |
| 3 | *non configurata* | — | — | libera |
| 4 | XRP/USDT | — | — | 1 gg |
| 5 | DOT/USDT | — | — | 1 gg, 1 trade |
| 6 | LTC/USDT | — | — | 1 gg |
| 7 | *non configurata* | — | — | libera |

Capitale corsia 0: **10.000,00** total / available, **0,00** used. PnL test 0,00, **0 trade** dall'avvio
del forward test (27/07/2026 14:26).

La tabella promozioni mostra per ogni corsia Sharpe, trade, DD, win rate, osservazione e stato —
con l'intestazione esplicita *«da Paper a Testnet (mai a Live in automatico)»*.

### Cosa ho trovato nello storico ordini

Il pannello "Ordini recenti (500)" è la sorpresa di questo giro. Due schemi ricorrenti:

**a) Ordini rifiutati per anti-spam, moltissimi.**

```
07-05 04:00  Buy  Market  513,14945  1,56  Filled
07-05 00:00  Buy  Market  511,50895  1,56  Rejected  Ordini troppo ravvicinati: 0,0s dall'ultimo, minimo 10s.
```

Quasi ogni ordine eseguito è accompagnato da un gemello rifiutato **con lo stesso timestamp** e
`0,0s dall'ultimo`. Il `SafetyChecker` sta funzionando come progettato (`MinOrderIntervalSeconds = 10`),
ma il fatto che debba intervenire così spesso suggerisce che **a monte vengano generati due ordini
per la stessa candela**. L'anti-spam li assorbe, quindi non si vede nulla di rotto — ma sta
mascherando una duplicazione di segnale. **DA VERIFICARE**: se sia l'ensemble con due gambe che
sparano insieme o una doppia valutazione della stessa barra.

**b) Ordini rifiutati dall'exchange per formato della quantità.**

```
06-30 12:00  Buy  Market  0,01370  58.412,00  Rejected
   HTTP 400: {"code":-1100,"msg":"Illegal characters found in parameter 'quantity';
   legal range is '^([0-9]{1,20})(\.[0-9]{1,20})?$'."}
```

Questi sono ordini realmente inviati a Binance e **rifiutati**. Analisi del percorso di codice in
[13_DEEP_DIVE_CODE.md](13_DEEP_DIVE_CODE.md#quantita): il ramo di fallback che arrotonda a 5 decimali
vale **anche per Testnet/Live quando i filtri del simbolo non sono stati caricati**, e in quel caso
salta pure il controllo dei minimi. È il candidato più probabile.

---

## Protezioni — `/admin/protections`

La pagina si apre dichiarando la propria fonte di verità:

> *«Stai configurando il motore remoto (Trading:UseRemoteTrading = true). Questi valori sono letti da
> `procionemgr-trading` via gRPC e sono quelli che sta applicando adesso; salvando si scrive là.»*

**Questo chiude un dubbio che avevo sollevato nel piano di test**: temevo che la pagina mostrasse la
configurazione del guscio invece di quella del motore. Non è così, e lo dice da sola.

Tre gruppi di protezioni, tutti `ATTIVO`:

1. **Feed di prezzo real-time (WebSocket)** — *solo osservazione*. Il pannello spiega che i tick
   **non aprono mai posizioni** e che «guida le uscite» è spento perché misurato peggiore
   (24 casi su 24, 2026-07-28). Backoff riconnessione 1000 ms → 60000 ms.
2. **Sentinella d'ombra delle uscite protettive** — registra il confronto feed vs candela, senza
   chiudere nulla.
3. **Limite di esposizione correlata fra corsie** — somma con segno (una copertura genuina non viene
   punita), non blocca se la correlazione non è stimabile, isola per modalità (una posizione Paper
   non vincola una corsia Testnet). Taratura 2026-07-25: in spot vale ~8% del capitale aggregato,
   quindi oggi non scatta mai; diventa vivo con la leva.

---

## Autonomia — `/admin/autonomy`

La pagina più densa della piattaforma (107,6 KB di markup), con indice interno a 13 sezioni.

| Automatismo | Stato |
|---|---|
| Esecuzione a fette (TWAP/VWAP/Iceberg) | **SPENTO** (master switch) |
| Ri-applica automatica dell'ensemble | ATTIVO |
| Promozione corsie Paper→Testnet | ATTIVO |
| Retrocessione di sicurezza Live→Testnet | presente, **dry-run** |
| Orchestratore di flotta (Queen Bee) | **ATTIVO in DRY-RUN** — ultimo tick 04/08 21:14, 0 azioni |
| Comitato AI | opzionale |

L'orchestratore dichiara il proprio perimetro: gestisce le corsie **dalla 3 in su**, mai l'impronta
storica dell'auto-apply, mai Live/Testnet/quarantene/campagne. Le corsie libere offerte per la
fascia grigia sono esattamente **3 e 7** — coerente con lo strip di `/trading`.

Ogni decisione è journalizzata: `04/08 20:29 · ProposeGrey · run c1be5750 · rules · dry-run`.

---

## Supervisione AI — `/admin/ai-supervisor`

| Voce | Valore |
|---|---|
| Provider attivo | **Nvidia** — `meta/llama-3.3-70b-instruct` |
| Modelli disponibili | 102 |
| Chiavi a database (cifrate) | Nvidia, Groq, Gemini, HuggingFace |
| Chiave Anthropic | **assente** |
| Catena di failover | Nvidia → Groq → Gemini → HuggingFace |
| Advisory riusciti | **31** |
| Advisory in errore | **19** |
| Run in attesa (7 gg) | 0 |

**19 errori su 50 tentativi = 38% di fallimento.** Il failover automatico c'è e funziona, ma un
tasso simile merita un'occhiata: vedi [14 — R17](09_RISKS_AND_TECH_DEBT.md).

Esiste anche un "secondo parere" opzionale che interroga un secondo provider (raddoppia il costo,
best-effort).

---

## Campagne — `/campaign`

Una campagna attiva: *«Caccia continua 1h+4h (universo largo)»*, backoff 12 h, ultima azione
04/08 18:22 UTC.

| # | Configurazione | Ultimo esito | Ultimo run | Tentativi |
|---|---|---|---|---|
| 1 | Caccia 1h universo largo (34 serie) | **0 sopravvissuti** | 04/08 16:30 | 5 |
| 2 | Caccia 4h universo largo (34 serie) | **0 sopravvissuti** | 04/08 18:15 | 5 |

Stato: *«Rotazione esaurita senza ensemble schierato: in attesa di un trigger contestuale».*
Dieci run, zero sopravvissuti. È il risultato negativo della piattaforma, in tempo reale.

---

## Esperimenti — `/experiments`

**196 run** tracciati, filtrabili per tipo (AlphaMining, Backtest, Discovery, Execution, MlTraining,
Optimization, OptimizationCpcv, Pipeline), con **hash di riproducibilità**.

```
2026-08-04 18:21  Pipeline · 34 serie  Candidates=144 · PanelPbo=0,619 · Rejected_ContoTrade=21 · Rejected_Dsr=4   4f83c430
2026-08-04 16:46  Pipeline · 34 serie  Candidates=64  · PanelPbo=0,0794 · Rejected_ContoTrade=3  · Rejected_Dsr=4   fbffdd40
2026-08-04 10:04  Pipeline · 34 serie  Candidates=144 · PanelPbo=0,619 · Rejected_ContoTrade=21 · Rejected_Dsr=4   4f83c430
```

**Lo stesso hash per la stessa configurazione a giorni diversi**: il determinismo dichiarato è
verificabile a colpo d'occhio, e regge. Un PBO di 0,619 su 144 candidati significa "il migliore è
probabilmente rumore", ed è scritto lì senza edulcoranti.

---

## Registry modelli — `/registry`

Modelli RandomForest raggruppati per simbolo/timeframe. Per **ogni** gruppo:
**«Nessun Champion attivo»**, DSR `—` (non calcolato). Decine di modelli fermi in `Staging`.

Coerente con la soglia `Registry:MinChampionDeflatedSharpe = 0.95`: nessun modello la raggiunge.

---

## Sentiment — `/sentiment`

Market mood composito, calcolato 2026-08-04 21:14:

| Voce | Valore |
|---|---|
| Fear & Greed | **25 — Extreme Fear** (Δ7g −4) |
| Mood composite | +0,01 |
| News 24h | −0,03 |

Per sei simboli: funding, long/short ratio con z-score, taker, OI 24h. Con letture contrarian
esplicite: *«SOL: posizionamento short degli account a un estremo storico (z=−2,1)»*.

### Salute delle fonti — correzione a un mio rilievo precedente

Nel primo audit avevo scritto che le fonti rotte fallivano "in silenzio". **Sbagliato.** La pagina ha
un pannello *Salute delle fonti* con un badge per fonte. Estratti i badge dal DOM:

| Fonte | Badge | Tooltip |
|---|---|---|
| BinanceFutures | 🟢 | ultimo ok 19:14 UTC (30 elementi) |
| CoinDesk, Cointelegraph, Decrypt, FearGreed, FXSSI, FXStreet, MyFxBook, TheBlock | 🟢 | ultimo ok (0 elementi) |
| **ForexFactory** | 🔴 | errore 18:15 UTC: **403 (Forbidden)** |
| **FXStreet-CentralBanks** | 🔴 | errore 18:15 UTC: **404 (Not Found)** |

Il difetto reale è più stretto di come l'avevo scritto: le fonti rotte **sono visibili**, con
l'errore esatto. Manca solo un allarme *proattivo* — bisogna aprire la pagina per accorgersene.

Nota secondaria: quasi tutte le fonti verdi riportano *(0 elementi)*. Probabile deduplica di elementi
già visti, ma **DA VERIFICARE** che il flusso notizie non sia di fatto vuoto.

---

## Bot — `/bot`

La "modalità semplice": due scelte e un pulsante. Stato **IN FUNZIONE · Simulazione**.
Tre profili (Prudente / Equilibrato / Dinamico) con la traduzione concreta di ciascuno:

> Equilibrato — 8% del capitale per operazione (800 USDT), max 40% in gioco, fino a 3 posizioni,
> stop a 4% in un giorno o 15% dal massimo, leva fino a 2x, max 0,75 operazioni al giorno,
> **commissioni a pieno regime ~6,6% del capitale all'anno**.

Dichiarare il costo annuo delle commissioni accanto al profilo di rischio è una scelta onesta e
rara. La pagina avverte anche di una incoerenza reale: *«La strategia in corsia lavora su candele da
1d, mentre "Equilibrato" è pensato per 1h o 4h»*.

---

## Le mie strategie — `/strategies`

17 strategie salvate. In testa, un avviso che vale la pena riportare:

> *«Molti nomi contengono lo Sharpe con cui la strategia è stata selezionata — quanto ha reso sui
> dati usati per sceglierla. Non è una previsione. Nella ricerca del 2026-07-20 i sei migliori
> candidati su 445.280 combinazioni avevano Sharpe di selezione fra 1,28 e 1,61, e su dati mai visti
> sono finiti fra −0,79 e −4,75.»*

È la tesi del progetto scritta dentro l'interfaccia, nel punto esatto in cui l'utente rischierebbe
di illudersi.

---

## Watchlist — `/market/watchlist`

221 serie. Campione verificato: AAVE/USDT su 5 timeframe — 167.272 candele (5m), 55.758 (15m),
27.140 (1h), 10.061 (4h), 2.120 (1d). Tutte `Abilitata`, stato `OK`, ultima sync 04/08 ~19:22 UTC,
ritardo `aggiornata`.

**L'ingestione in-cluster funziona.** Questa pagina è anche l'unica in cui ho catturato uno stato di
**loading** (`Caricamento…`) prima del popolamento della tabella — lacuna del primo audit ora colmata.

---

## Regimi — `/regimes`

Modello K-means **ATTIVO** su AAVE/USDT 1h, 27.140 candele (2023-07-01 → 2026-08-04),
K=4, **Silhouette 0,400**.

| ID | Label | Campioni | Volatility | Trend dir | RSI |
|---|---|---|---|---|---|
| 0 | Trend Up High-Vol | 88 | 1,106 | 1,00 | 62,2 |
| 1 | Bear Low-Vol | 195 | 0,745 | −0,88 | 44,7 |
| 2 | Bear High-Vol | 207 | 1,050 | −0,96 | 43,0 |
| 3 | Trend Up Low-Vol | 190 | 0,824 | 0,83 | 53,2 |

Con la matrice Sharpe medio strategia × regime (RsiOversold 3,04 in Trend Up High-Vol; MacdTrend
−1,32 in Bear High-Vol). **Attenzione**: 88 campioni sul regime più piccolo — è esattamente il
motivo per cui `RegimeRouting:DriveDecisions` resta `false`.

---

## Altre pagine

| Pagina | Stato osservato |
|---|---|
| `/discovery` | universo di **51 coppie × 7 timeframe × 14 strategie**, modalità creativa, preset per utente |
| `/pipeline` | **11 configurazioni salvate**, tutte con schedulazione "Non configurata" (girano via campagne) |
| `/ensemble` | corsia 0 con una gamba al 100%; SL/TP/trailing per gamba; 5 modalità di esecuzione |
| `/ml` | 6 famiglie di modelli; catalogo fattori **Alpha158 completo** (KMID, KLEN, ROC, MA, STD, BETA, RSQR, RESI, MAX, MIN, QTLU…) |
| `/portfolio` | MV / ERC / HRP su 51 simboli, componenti PCA, vincoli peso min/max |
| `/execution` | modello di costo **SquareRoot**, impatto 0,1, mezzo spread 0,050%, max 12 fette; ripristina l'ultima configurazione usata |
| `/settings/exchanges` | **3 credenziali reali**: Binance Main, Bitget Main, Bitget Test — API key mostrate mascherate nella forma `[REDACTED]******[REDACTED]`, nessun allarme master key |
| `/admin/users` | 2 utenti, entrambi Admin: `procionemgr@gmail.com`, `claude-notte@local` |
| `/admin/backup` | **«Nessun backup presente»** |
| `/metrics` | 6 contatori runtime, auto-refresh 5 s |

Pagine confermate raggiungibili e protette dalla sonda HTTP ma non ispezionate a schermo in questo
giro: `/backtest`, `/optimization`, `/feature-selection`, `/alpha-mining`, `/pairs-trading`,
`/volatility`, `/market-analysis`, `/market/bars`.

---

## Incoerenze di interfaccia rilevate

| # | Osservazione |
|---|---|
| I1 | Su `/trading`, lo strip corsie mostra la **3 come "non configurata"**, mentre la tabella promozioni della stessa pagina le attribuisce **DOT/USDT** (stato "ferma"). Due pannelli, due verità: probabilmente lo strip legge la configurazione del motore e la tabella l'ultimo simbolo storico. |
| I2 | `/ensemble` mostra 6 corsie più un chip "+2"; `/trading` le mostra tutte e 8, incluse le non configurate. Due rappresentazioni diverse dello stesso insieme. |
| I3 | Le corsie "non configurata" occupano spazio nello strip senza indicare cosa farne (già segnalato come R13). |

## Cose che il giro ha confermato come ben fatte

1. **Il degrado si dichiara** — banner su `/trading`, badge rossi su `/sentiment`, «Nessun backup
   presente» invece di una lista vuota ambigua.
2. **Ogni pannello dichiara la propria fonte di verità** — `/admin/protections` dice di leggere dal
   motore remoto; `/experiments` mostra l'hash; `/registry` dice «Nessun Champion attivo».
3. **L'onestà è scritta nell'interfaccia**, non solo nei report: l'avviso su `/strategies` e il costo
   annuo delle commissioni su `/bot` sono i due esempi migliori.
4. **I numeri negativi non sono nascosti** — `0 sopravvissuti`, `PanelPbo=0,619`, `Nessun Champion`.
