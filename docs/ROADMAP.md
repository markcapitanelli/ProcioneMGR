# ROADMAP — Integrazione e core caldo (viva, 2026-07-26)

*Questa è l'unica roadmap corrente. Le otto precedenti sono in `docs/archive/` — chiuse o
assorbite qui. Il dettaglio architetturale del filone B sta nel
[PRD-INTEGRAZIONE-CORE-CALDO](PRD-INTEGRAZIONE-CORE-CALDO.md).*

## Da dove nasce (audit 2026-07-26)

Scansione completa dei 14 progetti (758 tipi, raggiungibilità di ciascuno; registrazioni DI;
superficie dei flag). Verdetto: **il cablaggio è sano** — quasi tutto ciò che esiste è registrato e
raggiungibile; i 4 punti rossi dell'audit di luglio risultano chiusi nel codice. Il debito reale è
di **governo**, non di collegamento:

1. **giudici di validazione difformi** — la pipeline usa DSR+PBO; i tool CLI usano il gemello
   nullo, ma in 3 fasi su 4 con la soglia debole (15 gemelli / 95°) che ha già prodotto un falso
   positivo (SEI/USDT, smascherato solo dal torchio);
2. **doppio binario monolite/microservizi** mantenuto ma mai esercitato (tutti i toggle a `false`);
3. **codice morto**: `StickyHmmSmoother` (0 riferimenti app — il gate HMM è fallito nei cluster,
   non nella decodifica), 3 tool di verifica one-shot;
4. **34 documenti** fra cui 8 roadmap, senza un indice vivo.

**Decisione di fondo** (proprietario, 2026-07-26): il criterio che pesa è l'**operatività continua
durante lo sviluppo** — oggi ogni riavvio dell'app uccide motore, uscite protettive, feed e carry.
Quindi: committment ai microservizi nella forma **"core caldo / guscio freddo"** (il servizio di
trading sempre acceso; il monolite come guscio riavviabile di UI+ricerca), non congelamento.

---

## Filone A — Un solo giudice, una sola ricerca (consolidamento)

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| A1 | **Giudice del gemello nullo unificato**: servizio `NullTwinJudge` (200 gemelli, soglia 99°) + stage pipeline `NullTwinValidation` (opt-in, sui soli sopravvissuti all'holdout) + le 4 fasi CLI rifatte sul giudice condiviso | fatto (PR #54) | stessi verdetti del torchio sul caso SEI documentato; suite verde |
| A2 | Rimozione tool one-shot: TriggerVerify, RealtimeVerify (verifiche concluse e documentate nei report; la storia git li conserva). **SpotVerify RESTA** — scoperto in corso d'opera che il messaggio d'errore di `BitgetClient` lo indica come gate di verifica dal vivo della semantica spot, non ancora eseguita; stesso motivo per cui resta FuturesVerify | fatto | build solution verde |
| A3 | Rimozione `StickyHmmSmoother` + test (il candidato regimi è il jump model C1, che non lo riusa: ristima i cluster, non li ridecodifica) | fatto (PR #54) | build+suite verdi |
| A4 | Allineamento `RegimeTrigger`/`Campaign` | **verificato: nessuna azione** — senza campagne abilitate `CheckAsync` esce alla prima query (costo ~1 query/30min); il default ON è documentato come additivo | — |
| A5 | Archivio docs: le 8 roadmap storiche in `docs/archive/`, questa come unica corrente; link aggiornati | fatto (PR #54) | nessun link rotto nei doc attivi |
| A6 | Estrazione delle fasi di PlatformExpand (4.300 righe in un file) in libreria richiamabile anche dall'app | aperto | fase per fase, dopo A1 che ne estrae la prima |

## Filone B — Core caldo / guscio freddo (microservizi, per gradi e con gate)

Ogni passo è reversibile col suo toggle. Regola invariata: **ogni scrittore ha esattamente un
host**; mai auto-Live; l'AI resta advisory.

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| B0 | **Lease di esecuzione per corsia** (advisory lock Postgres): "mai due esecutori sulla stessa corsia" passa da patto operativo a invariante applicata — un deploy incoerente fallisce a voce alta invece di eseguire doppio | fatto (PR #54) + visto dal vivo in B1: il servizio trading in-cluster acquisisce i 3 lease all'avvio | test: due lease sulla stessa corsia confliggono; corsie diverse no |
| B1 | Monolite in K8s come baseline (rivalidare il percorso di Fase 3: immagini, GitOps, PVC keyring) | **fatto (2026-07-26)**: cluster ricreato, tag pinnati allo sha, stack completo sincronizzato via ArgoCD, keyring su PVC verificato con 2 riavvii del pod (stessa chiave ricaricata + cookie antiforgery pre-riavvio accettato dal pod nuovo), NetworkPolicy Calico rivista sul test 8080, backup pg_dump 290MB dal cluster (leggibile). **2 bug trovati SOLO eseguendo**: podSubnet Calico conteneva host.docker.internal → niente SNAT, nessun pod raggiungeva il DB (fix: 10.244.0.0/16); pg_dump 16 di distro vs server Postgres 18.4 (fix: client 18 PGDG pinnato). Fino a B3, ui e trading restano **scalati a 0** (il guscio operativo è ancora l'app locale: il pod trading terrebbe i lease, il pod ui doppierebbe scheduler/sentiment) — rivalidarli è un click di Sync | app raggiungibile dal cluster, login persistente al riavvio del pod ✓ (resta la conferma umana col proprio account) |
| B2 | `MarketData:UseRemoteIngestion=true` | **acceso (2026-07-26), gate in osservazione**: worker di sync nel servizio in-cluster (228/228 serie OK al primo ciclo, 0 errori, candele 5m fresche), `POST /sync/{id}` verificato; monolite locale delegato (toggle nell'appsettings reale + port-forward 18080 best-effort in run-postgres.ps1). Richiede Docker Desktop attivo; i buchi da downtime si auto-riparano al ciclo successivo (cursore incrementale) | 7 giorni di sync senza buchi nelle candele — scadenza 2026-08-02 |
| B3 | `Trading:UseRemoteTrading=true` con **feed R1 acceso dentro il servizio** (prima in osservazione: `DriveProtectiveExits=false`, confronto tick-vs-candle) | aperto | **chaos test**: kill del pod UI con posizioni Paper aperte → stop/trailing scattano lo stesso; drill di restore dal backup |
| B4 | ML remoto *solo se* il dual-read osservativo dimostra parità (`procione.ml.comparisons`) | aperto | N settimane di confronti senza divergenze decisionali; altrimenti resta in-process e non è un fallimento |
| B5 | Ritiro del ramo di hosting in-process del motore nel monolite (la semplificazione che il committment compra) | aperto | dopo B3 stabile; suite adattata |

**Non-obiettivi espliciti** (dove i microservizi a scala uno-operatore diventano tassa): spaccare
il database per servizio, estrarre pipeline/supervisor/sentiment, event bus, più repliche del
motore. Confermate le bocciature: RL (QLIB-5), SOR (una venue sola).

## Filone C — Algoritmi (stato dell'arte verificato 2026-07-26, ciascuno dietro gate)

| # | Cosa | Perché | Gate |
|---|---|---|---|
| C1 | **Statistical jump model** per i regimi (cluster + penalità di salto λ in CV; variante sparse per pesare le feature) | i nostri K-means durano 2,2 giorni mediani ("non operabile"); l'HMM sopra i cluster è già fallito perché *ridecodificava* cluster deboli — il jump model ristima cluster e persistenza **congiuntamente** (Nystrup–Kolm–Lindström; applicazione 2024 al downside risk) | persistenza mediana ≥ ~3 settimane E stabilità per-regime della performance; altrimenti non sostituisce niente |
| C1.a | **Prima gamba MISURATA (2026-07-26)**: `JumpModel` implementato (fit DP + decodifica causale, 8 test su regimi piantati) + fase `jumpmodel` in PlatformExpand. Verdetto onesto (fit 70%, causale sull'ultimo 30% mai visto): su **1h il gate FALLISCE a ogni λ** (a λ=200 train 27 gg ma OOS 3,7 — persistenza *fittata*); su **1d PASSA** su BTC (λ 5-100, OOS 25-46 gg, 4 stati, coerente col train) e replica su ETH (λ 5-100, OOS 26-54 gg). I regimi persistenti esistono ma vivono sul GIORNALIERO | fatto | seconda gamba (stabilità per-regime della performance su 1d) prima di qualunque cablaggio nel detector; il router resta in osservazione |
| C1.b | **Seconda gamba MISURATA (stesso giorno) — GATE FALLITO: C1 si chiude SENZA cablaggio.** Fase `jumpstability`: profilo strategia×regime per-barra (rendimento di t condizionato al regime di t−1, mai look-ahead), metà 1 contro metà 2, verdetto contro un nullo a rotazione circolare delle etichette (200 giri, stesso `Evaluate` del gemello). BTC 1d: Spearman fra le metà **−0,29** (18° percentile del nullo), segni concordi 46%. ETH 1d: **0,18** (75° percentile), segni 54%. **I regimi durano ma non discriminano**: sapere che una strategia rende nel regime X nella metà 1 non dice nulla sulla metà 2 — un'etichettatura lenta qualunque farebbe altrettanto. Il `JumpModel` resta in libreria come strumento misurato; il router resta in osservazione **per misura, non per prudenza**. Riaprirebbe il tema solo un set di feature diverso (es. breadth/volume, o feature macro) che superi ENTRAMBE le gambe | fatto — esito negativo | — |
| C2 | **Hedge ratio via filtro di Kalman** nel pairs, A/B contro la rolling OLS — **fatto (2026-07-26): gate PASSATO, Kalman adottato come default** (`KalmanPairsSpreadAnalyzer`, stesso motore/segnale/z-score; OLS resta selezionabile in `/pairs` e nei preset storici) | beta più stabile, spread più stazionario, niente parametro di finestra (letteratura concorde) | stesso walk-forward del ri-test 1d majors, MISURATO sulle 5 coppie operabili in selezione (holdout 2026-03→oggi): spread OOS più stazionario in **5/5** (mediana ΔADF −0,98, stabile per δ 1e-5…1e-3), MaxDD minore in **5/5** (mediana −0,9 pt). Onestà: pochi trade nell'holdout (la gamba forte è l'ADF); la classe pairs resta NON schierata (0/5 sopravvissuti) |
| C3 | **HAR-RV dai dati 5m** come terzo contendente nel gate QLIKE esistente (GARCH vs EWMA) — **fatto (2026-07-26): gate PASSATO nella variante log, cablata**. Sequenza onesta (fase `volgate`): il HAR sui LIVELLI **fallisce** il gate sui 6 majors di sviluppo (3/6, mediana peggiore — i salti di RV su BNB/XRP/DOGE devastano la OLS); la variante **log-HAR** (stessa OLS a 3 regressori su ln RV, smearing, zero parametri in più) li domina 6/6, e siccome era stata scelta GUARDANDO quei numeri è stata **confermata sui 24 simboli con 5m mai usati per la scelta: 24/24 a 1g** (mediana QLIKE 0,134 vs 0,228 del migliore attuale, −41%; 19/24 a 5g). Cablato in `VolatilityRegimeStage`: il Level lo classifica il log-HAR quando i 5m bastano (`volForecaster=auto`, fallback GARCH esplicito e loggato); GARCH resta per persistenza/parametri e code Student-t | sui crypto batte GARCH(1,1) su QLIKE a 1-5 giorni; è una OLS a 3 regressori; la GARCH Student-t resta per i quantili di coda | QLIKE out-of-sample migliore del vincitore attuale, o non entra — ✓ nella specificazione log, su set di conferma vergine |
| C4 | M1+M2 **insieme** (triple-barrier + meta-labeling sopra le barre informative già in `BarBuilder`) | la letteratura premia la combinazione, non i pezzi; aspettative tarate sul "modesto" | da roadmap intraday (archiviata, item assorbito qui) |
| C5 | Pilota microstruttura a termine (Fase 3 rivista: aggrega all'origine, misura il valore predittivo incrementale) | il tape grezzo di 3 simboli = 124× lo storico | da roadmap architetture (archiviata, item assorbito qui) |

## Ordine di esecuzione

**Fatto il 2026-07-26:** A1, A2, A3, A5, B0 (PR #54) · C1 chiuso senza cablaggio (gate fallito
sulla seconda gamba) · **B1 fatto e B2 acceso** (stessa giornata, cluster reale; gate B2 in
osservazione fino al 2026-08-02) · **drill di restore eseguito** (dump 2,6 GB → server vergine,
conteggi identici; prerequisito B3) · **C2 passato e adottato** (Kalman default nel pairs) ·
**C3 passato nella variante log e cablato** (log-HAR classifica il regime di volatilità).
**Poi, in ordine:** B3 (chaos test, sposta il carry nel core) · B4/B5 · A6 · C4/C5.

*Il carry Paper resta ON (unica classe con edge misurato positivo). Il router di regime resta in
osservazione per misura (esito C1.b), non per prudenza.*
