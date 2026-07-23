# Report Frontiere — prima ondata di esecuzione della ROADMAP-FRONTIERE-PROFITTO

*2026-07-24. Esecuzione del "percorso consigliato" §5 della roadmap: le mosse senza rischio (F4,
F8, run `eventstudy`) più il cuore (I2, F1.b). Ogni numero di questo report viene da un run sul
database REALE della piattaforma, riproducibile con la fase indicata.*

---

## 1. Event-study sul campo (`eventstudy`) — F3 DECISO: si, ma solo Crash e Surge

Riproducibile: `dotnet run --project tools/PlatformExpand -- eventstudy <symbol> <tf>`
(finestre: stima 60 barre, gap 5, pre 5, post 10; placebo 500 campioni, seme 42).

| Serie | Evento | N | CAAR pre | CAAR post | t post | p placebo |
|---|---|---:|---:|---:|---:|---:|
| BTC 1d | Crash | 8 | −7,0% | **−12,8%** | −6,6 | **0,002** |
| BTC 1d | Surge | 14 | −0,9% | **+11,3%** | +4,9 | **0,004** |
| ETH 1d | Crash | 7 | −11,8% | **−16,7%** | −2,5 | **0,002** |
| ETH 1d | Surge | 9 | −5,5% | **+16,9%** | +3,3 | **0,002** |
| SOL 1d | Crash | 4 | −5,5% | **−38,3%** | −2,3 | **0,002** |
| SOL 1d | Surge | 7 | +2,7% | **+24,4%** | +2,7 | **0,004** |
| BTC 1h | Crash | 149 | +0,1% | **−1,94%** | −11,1 | **0,002** |
| BTC 1h | Surge | 129 | +0,3% | **+1,98%** | +11,5 | **0,002** |
| ETH 1h | Crash | 135 | −0,3% | **−2,66%** | −10,1 | **0,002** |
| ETH 1h | Surge | 126 | +0,2% | **+2,75%** | +11,8 | **0,002** |

**La lettura**: dopo un movimento estremo (|z| > 4σ) il mercato **CONTINUA nella stessa direzione**
— non rimbalza. Replicato su 3 simboli a 1d e 2 simboli a 1h, sempre con p placebo ≤ 0,004; a 1h
la finestra pre-evento è pulita (nessuna anticipazione) e i campioni sono grandi (126-149 eventi).
A 1h la continuazione media è ~2% in 10 barre: sopra i costi di un round-trip (~0,26%) con margine.

**Non promossi**: VolSpike (segno incoerente fra simboli: +0,4%, +1,0%, −19,3%) e VolumeBlowout
(p placebo 0,4-0,8: le date a caso reagiscono uguale).

**Conseguenza (F3 chiuso)**: due nuovi segnali nel catalogo, APPESI in coda — id **12 "Post-Crash"**
e **13 "Post-Surge"** (100 alla barra dell'evento, decadimento lineare a 0 in 20 barre, warm-up del
rilevatore = null). Il Composer può ora cacciare composizioni come "Post-Surge > 50 → long"; il
giudizio su OGNI composizione resta al gate (DSR/holdout/CPCV/permutation). VolSpike/VolumeBlowout
NON hanno un id: il criterio d'ingresso era il placebo, e non l'hanno passato.

---

## 2. Profilo del minuto (`minuteprofile`) — F8 MISURATO: drift no, struttura di liquidità sì

Sui 6 simboli con dati 1m (~525.000 barre consecutive ciascuno):

- **Drift per minuto: rumore.** 2 sole celle su 360 sopra la soglia Bonferroni (|t|>3,5), entrambe
  a :06 (ETH e BNB) e da ~0,4 bp: non tradabile e con ogni probabilità residuo. Coerente con R2:
  a 1m i costi dominano qualunque drift.
- **Volume e range hanno invece struttura sistematica**: i confini di quarto d'ora sono i minuti
  più agitati (:00 → range 1,17×, vol 1,20×; :30 → 1,18×/1,22×; :15/:31/:01 → ~1,1×), mentre i
  minuti tardo-quarto sono i più calmi (:58-:59 → range 0,77×; :29 → 0,79×; :44 → 0,84×).

**Conseguenza (decisione F8)**: nessuna strategia (come dichiarato in partenza). L'uso corretto è
un **offset di esecuzione**: gli ordini non-urgenti (aperture pipeline, slice TWAP/VWAP) dovrebbero
EVITARE i minuti :00/:30 e preferire la coda del quarto (:28-:29, :43-:44, :58-:59), dove il range
atteso — cioè lo slippage atteso di un market order — è ~30-40% più basso che sul confino d'ora.
Il beneficio stimato è di pochi bp per fill: il wiring nello slicing è rimandato a quando i volumi
operativi lo giustificano; la misura è agli atti e la fase è rilanciabile.

---

## 3. Carry sul funding storico (`carry`) — F1.b MISURATO: il flusso c'è e paga i costi

Simulazione delta-neutra (long spot + short perp) sul funding VERO dal 2019 (T0.2), 6 simboli.
Costi: 4 fill per episodio (spot 0,10% + slip 0,03%; futures 0,05% + slip 0,03%) = 0,42%/episodio.
Entrata: media 9 eventi (~3 giorni) annualizzata > soglia; uscita sotto la soglia d'uscita.

**Soglie 5% / 0%:**

| Sym | eventi | dal | episodi | tempo in pos | lordo % | costi % | netto % | netto %/anno |
|---|---:|---|---:|---:|---:|---:|---:|---:|
| BTC | 7.526 | 2019-09 | 45 | 81% | 80,9 | 18,9 | 62,0 | **9,0** |
| ETH | 7.292 | 2019-11 | 37 | 82% | 94,5 | 15,5 | 79,0 | **11,9** |
| SOL | 6.493 | 2020-09 | 65 | 62% | 60,2 | 27,3 | 32,9 | **5,6** |
| BNB | 7.067 | 2020-02 | 39 | 37% | 52,6 | 16,4 | 36,2 | **5,6** |
| XRP | 7.172 | 2020-01 | 53 | 73% | 101,7 | 22,3 | 79,4 | **12,1** |
| DOGE | 6.614 | 2020-07 | 72 | 77% | 78,1 | 30,2 | 47,9 | **7,9** |

**Robustezza**: con isteresi 10%/3% il netto resta 5,0-12,5%/anno con MENO tempo in posizione
(20-55%): l'edge non dipende dalla soglia — è strutturale, come atteso da un flusso.

**Letture oneste (stampate dalla fase stessa)**: è un limite superiore — ignora il rischio di base
spot/perp all'entrata/uscita e la capacità; l'A/B a funding zero dà lordo 0 e netto = −costi; il
vincolo MiCA (futures live limitati) resta: la strada operativa è Paper/Testnet prima, e
possibilmente la variante "short-only quando funding alto" che il backtest del motore sa già
valutare col funding firmato (T0.2). **Prossimo passo F1**: il segnale "Funding pct" nel catalogo
(F1.a) quando arriva il MarketContext (§4 della roadmap).

---

## 4. Gemello sintetico (`nulltwin`) — I2 COSTRUITO e già utile

Fase: `nulltwin <symbol> <tf> <N> [--planted]`. Nullo = stationary block bootstrap dei rendimenti
(blocchi geometrici ~24 barre, volume accoppiato) + **segno i.i.d. per barra**: il clustering di
|r| sopravvive, OGNI struttura direzionale muore (anche intra-blocco — il sign-flip a blocchi del
PermutationTest, pensato per un altro scopo, l'avrebbe lasciata viva). Generatore in
`Services/Validation/NullTwinGenerator` con test di contratto in suite.

**Selfcheck (edge piantato, +30bp nella direzione della barra precedente)**: il reale finisce
OLTRE il P95 nullo (batte 19/20 gemelli) — il controllo positivo funziona, reso ripetibile.

**Run veri (9.972 candele 4h, 20 gemelli, mini-caccia su 13 strategie)**:

| Serie | Best REALE | Nullo: min / mediana / **P95** / max | Batte | Verdetto |
|---|---:|---|---:|---|
| BTC 4h | 0,63 | −0,03 / 0,45 / **0,70** / 1,20 | 16/20 | dentro il nullo |
| ETH 4h | 0,89 | −0,07 / 0,52 / **0,97** / 1,29 | 18/20 | dentro il nullo |

**Verdetto**: il miglior candidato reale è DENTRO la distribuzione nulla su entrambe le serie —
serie costruite per non contenere nulla producono "migliori" altrettanto belli. È la **seconda
conferma indipendente** dei negativi di luglio (445k combinazioni → 0 sopravvissuti), ottenuta con
un metodo diverso. Costo: ~1-2 secondi per serie a 4h — avvolgere cacce più grandi è ora economico.

---

## 5. Accumulo liquidazioni — F4 COSTRUITO (il valore matura col tempo)

- `LiquidationSyncWorker` (default **ON**, sezione `Liquidations`): stream pubblico keyless
  `!forceOrder@arr` (un socket per tutto il listino futures USDT-M), aggregazione per
  (simbolo, ora, lato) → `SentimentMetricPoints` (nozionale + conteggio, long e short separati),
  flush idempotente ogni 5', riconnessione con backoff, canale silente >120s = guasto.
- **Verifica dal vivo**: connessione all'endpoint reale riuscita (2 tentativi, canale stabile);
  nessuna liquidazione transitata nelle due finestre da 60s — mercato calmo, sul flusso
  market-wide è normale. Il parsing è fissato dai test sul payload documentato; fase
  `liquidationsverify` rilanciabile in qualunque momento.
- Due difetti di design trovati e corretti PRIMA della produzione, grazie ai test: prune in
  tempo-evento (non a orologio: un backlog avrebbe fatto ripartire i secchi da un parziale) e
  upsert monotono (un flush tardivo non può regredire un totale già definitivo).

### ⚠️ Bug latente critico trovato di passaggio (e corretto)

La retention del sentiment (`SentimentSyncWorker.PurgeAsync`) cancellava le metriche oltre 30
giorni esentando SOLO FearGreed: **al primo tick dell'app, il backfill del funding dal 2019
(T0.2) sarebbe stato raso a 30 giorni** — il tool scriveva la storia, il worker l'avrebbe
cancellata. Il backtest col funding firmato e il carry di questo report sarebbero diventati
irriproducibili in silenzio. Ora sono esenti: FearGreed, **FundingRate** e
**BinanceLiquidations**; il vecchio test che sanciva la cancellazione del funding è stato
riscritto nel senso giusto. *(Il DB reale è intatto: la finestra di rischio non si è mai aperta
perché l'app non ha girato dopo il backfill.)*

---

## 6. Stato della roadmap dopo la prima ondata

| Item | Stato |
|---|---|
| F1.b carry | ✅ MISURATO: netto 5-12%/anno sul funding vero, robusto alle soglie |
| F3 eventi→filtri | ✅ CHIUSO: segnali 12-13 promossi (solo Crash/Surge, criterio placebo rispettato) |
| F4 liquidazioni | ✅ COSTRUITO: accumulo ON di default, purge esente, canale verificato |
| F8 minuti | ✅ MISURATO: drift no; offset di esecuzione documentato (wiring rimandato con motivo) |
| I2 gemello sintetico | ✅ COSTRUITO + selfcheck + primo run vero (conferma indipendente dei negativi) |
| F1.a, F7, I1-st.2 | in attesa del MarketContext (§4) — ora giustificato da F1.b e F3 |
| F2, F5, F6, I1-st.1 | prossima ondata |

**Prossimo passo con più valore atteso**: il **MarketContext** (§4) — è l'unico pezzo di
infrastruttura che sblocca tre item, e ora due dei suoi consumatori (funding-segnale dopo il
carry positivo; crowding dopo che l'accumulo OI/liquidazioni è partito) hanno numeri dalla loro.
Poi la caccia nuova con Post-Crash/Post-Surge e MFI/OBV nel pool, avvolta nel gemello sintetico.
