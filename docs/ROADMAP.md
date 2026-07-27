# ROADMAP — Integrazione, core caldo e scoperta pattern (viva, 2026-07-27)

*Questa è l'unica roadmap corrente. Le otto precedenti sono in `docs/archive/` — chiuse o
assorbite qui. Il dettaglio architetturale del filone B sta nel
[PRD-INTEGRAZIONE-CORE-CALDO](PRD-INTEGRAZIONE-CORE-CALDO.md); quello del filone D (nato dalla
revisione di un report esterno sull'utente) sta nel
[PRD-SCOPERTA-PATTERN-ANTIOVERFITTING](PRD-SCOPERTA-PATTERN-ANTIOVERFITTING.md).*

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
| A1 | **Giudice del gemello nullo unificato**: servizio `NullTwinJudge` (200 gemelli, soglia 99°) + stage pipeline `NullTwinValidation` (opt-in, sui soli sopravvissuti all'holdout) + le 4 fasi CLI rifatte sul giudice condiviso | in esecuzione | stessi verdetti del torchio sul caso SEI documentato; suite verde |
| A2 | Rimozione tool one-shot: TriggerVerify, RealtimeVerify (verifiche concluse e documentate nei report; la storia git li conserva). **SpotVerify RESTA** — scoperto in corso d'opera che il messaggio d'errore di `BitgetClient` lo indica come gate di verifica dal vivo della semantica spot, non ancora eseguita; stesso motivo per cui resta FuturesVerify | fatto | build solution verde |
| A3 | Rimozione `StickyHmmSmoother` + test (il candidato regimi è il jump model C1, che non lo riusa: ristima i cluster, non li ridecodifica) | in esecuzione | build+suite verdi |
| A4 | Allineamento `RegimeTrigger`/`Campaign` | **verificato: nessuna azione** — senza campagne abilitate `CheckAsync` esce alla prima query (costo ~1 query/30min); il default ON è documentato come additivo | — |
| A5 | Archivio docs: le 8 roadmap storiche in `docs/archive/`, questa come unica corrente; link aggiornati | in esecuzione | nessun link rotto nei doc attivi |
| A6 | Estrazione delle fasi di PlatformExpand (4.300 righe in un file) in libreria richiamabile anche dall'app | aperto | fase per fase, dopo A1 che ne estrae la prima |

## Filone B — Core caldo / guscio freddo (microservizi, per gradi e con gate)

Ogni passo è reversibile col suo toggle. Regola invariata: **ogni scrittore ha esattamente un
host**; mai auto-Live; l'AI resta advisory.

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| B0 | **Lease di esecuzione per corsia** (advisory lock Postgres): "mai due esecutori sulla stessa corsia" passa da patto operativo a invariante applicata — un deploy incoerente fallisce a voce alta invece di eseguire doppio | in esecuzione | test: due lease sulla stessa corsia confliggono; corsie diverse no |
| B1 | Monolite in K8s come baseline (rivalidare il percorso di Fase 3: immagini, GitOps, PVC keyring) | aperto | app raggiungibile dal cluster, login persistente al riavvio del pod |
| B2 | `MarketData:UseRemoteIngestion=true` | aperto | 7 giorni di sync senza buchi nelle candele |
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
| C2 | **Hedge ratio via filtro di Kalman** nel pairs, A/B contro la rolling OLS | beta più stabile, spread più stazionario, niente parametro di finestra (letteratura concorde) | stesso walk-forward del ri-test 1d majors: vince solo se migliora stazionarietà dello spread e coda dei drawdown |
| C3 | **HAR-RV dai dati 5m** come terzo contendente nel gate QLIKE esistente (GARCH vs EWMA) | sui crypto batte GARCH(1,1) su QLIKE a 1-5 giorni; è una OLS a 3 regressori; la GARCH Student-t resta per i quantili di coda | QLIKE out-of-sample migliore del vincitore attuale, o non entra |
| C4 | M1+M2 **insieme** (triple-barrier + meta-labeling sopra le barre informative già in `BarBuilder`) | la letteratura premia la combinazione, non i pezzi; aspettative tarate sul "modesto" | da roadmap intraday (archiviata, item assorbito qui) |
| C5 | Pilota microstruttura a termine (Fase 3 rivista: aggrega all'origine, misura il valore predittivo incrementale) | il tape grezzo di 3 simboli = 124× lo storico | da roadmap architetture (archiviata, item assorbito qui) |

## Filone D — Scoperta di pattern e interpretabilità (dal report anti-overfitting, guscio freddo)

Nato dalla revisione di un report esterno ("Dalla Ricerca di Pattern alla Validazione
Anti-Overfitting", 2026-07-27): confronto punto per punto e requisiti nel
[PRD-SCOPERTA-PATTERN-ANTIOVERFITTING](PRD-SCOPERTA-PATTERN-ANTIOVERFITTING.md). Verdetto sintetico:
9 delle ~14 tecniche proposte esistono già (alcune oltre il report — DSR, PBO, gemello sintetico
non sono nemmeno nominati); la proposta di punta (Walk-Forward condizionata ai regimi) è la stessa
domanda già chiusa dal gate C1 con esito negativo. Restano 3-4 pezzi genuinamente assenti. Nessun
item tocca il percorso live: tutto guscio freddo (ricerca/UI), nessun nuovo scrittore.

| # | Cosa | Priorità | Gate / verifica |
|---|---|---|---|
| D1 | **SHAP-lite** — TreeSHAP esatto sui modelli ad alberi di ML Lab, importanza globale con segno, matrice per contesto di volatilità, waterfall della singola barra | **FATTO 2026-07-27** | ✅ verificato contro **Shapley esatto per forza bruta** (enumerazione di tutti i 2ⁿ sottoinsiemi) e contro le predizioni del modello ML.NET vero; efficienza confermata anche dal vivo in browser su BTC/USDT 1h (Σφ = predizione − baseline al centesimo di millesimo) |
| D2 | **Factor drift monitor** — IC finestra per finestra, pavimento di rumore, verdetto stabile/spento/invertito in `/feature-selection` | **FATTO 2026-07-27** | ✅ 12 test, incluso "40 semi di puro rumore ⇒ zero allarmi"; misurato dal vivo: MeanReversion 0,050→0,027 e RSI −0,049→−0,029 si sono spenti su BTC/USDT 1h |
| D3 | **OFI vero**: formula (imbalance firmato al top-of-book) da innestare nello step 3.3 del pilota C5 già pianificato, confrontata contro il proxy `TakerImbalanceFactor` già esistente | eredita quella di C5 | stesso gate di C5: se non aggiunge IC oltre al proxy, si spegne a fine pilota — esito negativo valido |
| D4 | **DTW** pattern-matching su forma: genera trigger evento per il motore Discovery esistente, non una strategia propria | media, rischio alto (altro angolo di edge direzionale-tecnico, classe già a otto zeri) | **non negoziabile**: controllo con pattern sintetico piantato PRIMA di fidarsi di un risultato su dati reali (stesso principio della fase `control` di PlatformExpand); poi lo stesso collaudo CPCV+DSR+PBO+gemello di sempre |
| D5 | **SAX** + mining di sequenze, come pre-filtro economico di D4 (non un motore parallelo) | bassa, condizionata | si costruisce SOLO se D4 supera il controllo sintetico E mostra un segnale che sopravvive a holdout+DSR+PBO su un angolo non esaurito |

**Non-obiettivi di questo filone** (dettaglio nel PRD): non riaprire il regime-conditional (chiuso
da C1); non ri-cacciare pattern direzionali-tecnici su majors 1h/4h (otto zeri + consenso di
letteratura); non costruire LOB reale prima del verdetto di C5; DTW/SAX restano generatori di
candidati per il collaudo esistente, non una quarta pista di validazione.

**Due deviazioni dal PRD, entrambe misurate e documentate** (§ dettaglio nel
[report di esecuzione](REPORT-FILONE-D-2026-07-27.md)):

1. **La lente di D1 è la volatilità, non il regime K-means.** Il PRD prometteva la rottura SHAP per
   regime; in pratica il modello K-means dev'essere attivo E della stessa serie del modello ML
   (quasi mai vero, pannello vuoto quasi sempre), e il gate C1 ha già misurato che quei regimi non
   discriminano. I terzili di volatilità realizzata sono sempre disponibili e rispondono alla stessa
   domanda utile senza suggerire un significato che la misura non sostiene.
2. **D2 non persiste nulla.** L'IC storico è una funzione deterministica delle candele: salvarlo
   sarebbe una cache, non un'osservazione — con in più una migrazione da applicare al DB reale. Si
   ricalcola su richiesta. Nessuna modifica di schema in tutto il filone.

## Ordine di esecuzione

**Oggi (2026-07-26):** A1, A2, A3, A5, B0 — poi build+suite, PR.
**Poi, in ordine:** B1→B2→B3 (operativi, sulla macchina reale col cluster) · C1 (il più fondato sul
problema misurato) · C2 col ri-test pairs · C3 · B4/B5 · A6 · C4/C5.
**Filone D:** D1/D2 non hanno dipendenze, si possono fare in parallelo a qualunque punto sopra;
D3 segue i tempi di C5; D4 (e l'eventuale D5) dopo C4/C5, per non competere per attenzione con gli
item che hanno un problema misurato dietro.

*Il carry Paper resta ON (unica classe con edge misurato positivo). Il router di regime resta in
osservazione finché C1 non supera il suo gate.*
