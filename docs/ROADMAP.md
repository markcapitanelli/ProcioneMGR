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
| B3 | `Trading:UseRemoteTrading=true` con **feed R1 acceso dentro il servizio** (prima in osservazione: `DriveProtectiveExits=false`, confronto tick-vs-candle) | **eseguito (2026-07-26), osservazione in corso**: motore nel servizio in-cluster (3 corsie auto-riprese: 2 Paper + 1 Testnet demo), feed R1 connesso a Binance+Bitget in SOLO OSSERVAZIONE, **carry spostato nel core** (registrato in `AddTradingLanes` ramo `!useRemote`, come feed e watchdog: alla prima valutazione ha aperto BTC Paper, funding 6,0%>5,0%) e guscio locale su `UseRemoteTrading=true` (gRPC via port-forward 18092, aperto da run-postgres.ps1). **Chaos test eseguito**: kill brutale del guscio → in 4 minuti di orfanità 2.713 valutazioni di corsia, 0 errori/STALE, 0 restart (posizione carry in-memory intatta), corsie `running=True` interrogate via gRPC a guscio morto; guscio riavviato e riconnesso. Onestà: al momento del kill le posizioni aperte erano quelle del carry (le corsie erano a 0 posizioni), quindi "lo stop scatta a guscio morto" è dimostrato a livello di macchina delle uscite sempre attiva, non di stop realmente scattato — lo dimostrerà l'operatività dei prossimi giorni | ~~chaos test~~ ✓ · ~~drill di restore~~ ✓ · resta: confronto tick-vs-candle nelle metriche → poi `DriveProtectiveExits=true` (R1 pieno) |
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
| C4.a | **Nucleo di etichettatura FATTO 2026-07-27**: `TripleBarrierLabeler` (tre barriere, barriere derivate dalle escursioni reali via `ExcursionAnalyzer`, pesi per unicità media AFML §4.3) + `MetaLabeler` (filtro dei falsi positivi). 23 test, incluso l'**edge piantato** che la catena recupera. Due onestà cablate: l'ambiguità intra-barra si risolve sempre a favore dello stop (lettura pessimistica), e le barre in coda restano senza etichetta invece di riceverne una troncata | fatto — nucleo | — |
| C4.c | **Consumo FATTO (stesso giorno, dopo l'osservazione del proprietario sui livelli di test)**: `MetaLabelingAnalysisService` fa il giro completo su dati veri — estrae i segnali barra per barra da una strategia reale, li etichetta, addestra il meta-modello e produce un verdetto — con pannello in `/backtest`. Prima C4 era una libreria corretta e **mai chiamata da nessuno**: verde a livello di classe, inesistente a livello di prodotto. Aggiunti test di integrazione con componenti reali (percorsi di fallimento inclusi) e verifica dal vivo. **Misura su BTC/USDT 1h, 8.886 segnali**: il filtro alza la precision da 5,7% a 19,2% e batte una selezione casuale di **7,05 σ** — ma conserva il **2%** dei segnali e il rendimento medio PEGGIORA (+0,036% → −0,091%). Il verdetto lo respinge correttamente: senza la guardia sul campione superstite, quel 7,05 σ sarebbe stato un falso positivo convincentissimo. Lo standard di verifica è ora scritto in [STANDARD-VERIFICA](STANDARD-VERIFICA.md) | fatto | — |
| C4.b | **Meta-modello CHIUSO (stesso giorno)**: `MetaModelTrainer` addestra davvero il classificatore e produce probabilità **out-of-fold purgate** — prima arrivavano da fuori e nessuno le generava. Purga mai più corta dell'orizzonte della barriera (l'etichetta di *i* si risolve fino a *i+H*: una purga più corta farebbe vedere il futuro), pesi di unicità nel training, e campione insufficiente o classe unica ⇒ si dichiara invece di ripiegare su probabilità in-sample. **Il verdetto è stato irrigidito da un test fallito**: su dati di puro rumore diceva "miglioramento" (precision 0,477→0,529 su 280) perché confrontava stime puntuali. Ora `IsImprovement` pretende anche che il filtro batta una **selezione casuale della stessa numerosità** (z ipergeometrico ≥ 1,96), e il test del rumore gira su 20 semi misurando il *tasso* di falsi positivi invece di una sola estrazione | fatto | resta il **consumo**: decoratore di `IStrategy`, sizing per probabilità, barre informative (M2). Il nucleo va provato su un edge che abbia già passato i gate, mai su una regola morta — il meta-labeling amplifica, non crea |
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
| D1 | **SHAP-lite** — TreeSHAP esatto sui modelli ad alberi di ML Lab, importanza globale con segno, matrice per contesto, waterfall della singola barra | **FATTO 2026-07-27** | ✅ verificato contro **Shapley esatto per forza bruta** (enumerazione di tutti i 2ⁿ sottoinsiemi) e contro le predizioni del modello ML.NET vero; efficienza confermata anche dal vivo in browser su BTC/USDT 1h (Σφ = predizione − baseline al centesimo di millesimo) |
| D1.a | **Lente K-means COMPLETATA (2026-07-27, secondo giro)** — la matrice usa ora i **regimi K-means** di `/regimes` quando esiste un modello attivo della stessa serie, e ripiega sui terzili di volatilità altrimenti, dichiarando sempre in UI quale lente è in uso. Chiude una divergenza dal PRD §5a che avevo introdotto: avevo sostituito la lente adducendo anche che «il gate C1 ha misurato che i regimi non discriminano» — argomento **sbagliato in questo contesto**, perché qui il regime è un asse di raggruppamento descrittivo e non deve superare alcun gate (differenza sostanziale col `LaneRegimeRouter`). Etichettatura con l'overload **per serie** di `LabelFeaturesAsync`: quello generico classificherebbe le candele coi centroidi di un'altra coppia. Un guasto della lente non fa mai fallire il calcolo SHAP: si ripiega e lo si dichiara | fatto | ✅ 8 test unità + 4 integrazione; dal vivo: BTC/USDT 1h mostra i 4 regimi reali (Trend Up Low-Vol, Choppy/Volatile, Trend Up High-Vol, Sideways) e **RealizedVol pesa 4-5× di più nel regime Choppy** — informazione che i terzili non davano; SOL/USDT 1h (senza modello attivo) ripiega e spiega come ottenere la vista per regime |
| D2 | **Factor drift monitor** — IC finestra per finestra, pavimento di rumore, verdetto stabile/spento/invertito in `/feature-selection` | **FATTO 2026-07-27** | ✅ 12 test, incluso "40 semi di puro rumore ⇒ zero allarmi"; misurato dal vivo: MeanReversion 0,050→0,027 e RSI −0,049→−0,029 si sono spenti su BTC/USDT 1h |
| D3 | **OFI vero**: formula (imbalance firmato al top-of-book) da innestare nello step 3.3 del pilota C5 già pianificato, confrontata contro il proxy `TakerImbalanceFactor` già esistente | eredita quella di C5 | stesso gate di C5: se non aggiunge IC oltre al proxy, si spegne a fine pilota — esito negativo valido |
| D4 | **DTW** pattern-matching su forma: genera trigger evento per il motore Discovery esistente, non una strategia propria | media, rischio alto (altro angolo di edge direzionale-tecnico, classe già a otto zeri) | **non negoziabile**: controllo con pattern sintetico piantato PRIMA di fidarsi di un risultato su dati reali (stesso principio della fase `control` di PlatformExpand); poi lo stesso collaudo CPCV+DSR+PBO+gemello di sempre |
| D4.a | **Motore FATTO 2026-07-27, gate SUPERATO**: `DtwMatcher` (z-normalizzazione obbligatoria, banda di Sakoe-Chiba, pruning LB_Keogh, occorrenze non sovrapposte). 22 test: il **pattern piantato viene ritrovato** anche dilatato nel tempo, LB_Keogh verificato come vero limite inferiore su 3.000 coppie casuali (se non lo fosse, il pruning scarterebbe in silenzio le corrispondenze migliori), 50.000 barre scansionate in tempo utile, 300 prove di fuzzing | fatto | — |
| D4.b | **Misura FATTA, con una correzione di metodo importante**: `DtwPatternAnalysisService` collega le occorrenze all'`EventStudy` già esistente. **I test hanno smascherato un difetto grave**: col solo placebo a date casuali, pattern casuali su rumore puro venivano dichiarati "segnale" **8 volte su 15** — perché selezionare finestre per FORMA induce da sola una deriva nelle barre successive, e un placebo a date casuali non conserva quel meccanismo di selezione. Introdotto il **nullo per forma** (si ripete l'intera procedura con pattern casuali, a parità di numero di occorrenze) e un **pavimento di rilevanza economica** dello 0,5% (un effetto dello 0,48% era stato dichiarato significativo: vero, ma sotto i costi di andata e ritorno). Con entrambi: edge piantato trovato, rumore respinto | fatto | — |
| D4.c | **Pannello UI + misura su dati reali FATTA**: pannello in `/market-analysis` (si sceglie una finestra della serie come modello). Prima misura, **SOL/USDT 15m, 54.984 candele fino al 2026-07-27**: il pattern si trova 500 volte (**26,2 occorrenze/mese**, frequenza adeguata all'obiettivo intraday), ma il rendimento anormale successivo (−0,22%) rientra in quello che producono forme qualunque (p 0,366, 95° percentile del nullo 0,42%). **Nono esito negativo, coerente con gli altri otto.** Dettaglio istruttivo: la CAAR PRE-evento è +0,23% contro −0,22% post — il pattern *descrive* un movimento già in corso invece di anticiparlo, esattamente l'artefatto meccanico che il nullo per forma esiste per smascherare | fatto — esito negativo | D5 (SAX) resta **non giustificato**: la sua condizione era che D4 mostrasse un segnale sopravvissuto ai gate, e non l'ha mostrato |
| D5 | **SAX** + mining di sequenze, come pre-filtro economico di D4 (non un motore parallelo) | ~~bassa, condizionata~~ **NON SI FA** | La condizione era: D4 supera il controllo sintetico **E** mostra un segnale che sopravvive ai gate. Il controllo l'ha superato, il segnale no (D4.c). SAX serviva ad *accelerare* una ricerca che si è rivelata senza premio: costruirlo ora sarebbe ottimizzare la velocità di una strada che non porta da nessuna parte. Si riapre solo se D4 trova un angolo che regge |

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

**Fatto il 2026-07-26:** A1, A2, A3, A5, B0 (PR #54) · C1 chiuso senza cablaggio (gate fallito
sulla seconda gamba) · **B1 fatto e B2 acceso** (stessa giornata, cluster reale; gate B2 in
osservazione fino al 2026-08-02) · **drill di restore eseguito** (dump 2,6 GB → server vergine,
conteggi identici; prerequisito B3) · **C2 passato e adottato** (Kalman default nel pairs) ·
**C3 passato nella variante log e cablato** (log-HAR classifica il regime di volatilità) ·
**B3 eseguito con chaos test superato** (motore+feed+carry nel core in-cluster, guscio locale
remoto; 4 minuti di orfanità senza un errore; resta il confronto tick-vs-candle prima di
`DriveProtectiveExits=true`).
**Fatto il 2026-07-27** (giornata di esercizio operativo, tutto in Paper):
- **Ripresa dopo un fermo macchina** e un guasto che si ripresenterà: dopo un riavvio di
  Windows/Docker `kubectl` non raggiunge più il cluster perché Windows **riserva** la porta
  dell'API server e Docker non ripristina il binding (`docker restart` non basta). Rimedio senza
  ricreare il cluster: container `socat` che ripubblica 6443 su una porta libera +
  `kubectl config set-cluster`. **Recupero automatico verificato**: nessun buco nelle candele
  (36/36 attese), corsie auto-riprese, lease riacquisiti, carry ripartito.
- **Bug del suggeritore SL/TP corretto**: la pagina Backtest usava le escursioni della SINGOLA
  candela mentre `SuggestAdaptiveBracket` (MAE/MFE sull'orizzonte, R1.5) esisteva già ed era
  cablato solo nella pipeline. Ora l'orizzonte è la **durata mediana dei trade**. Su DOT 15m il
  suggerimento passa da 0,38%/1,12% (che trasformava +24,6% in −45,8%) a 3,72%/10,32%.
  **Regola imparata**: il bracket automatico va sempre verificato col backtest — su AAVE migliora,
  su DOT è neutro e protettivo, su XLM peggiora (stop troppo stretto) e va allargato.
- **Corsie riorganizzate**: rimosse le tre PostSurge 1h (BTC in perdita e ferma, DOGE mai un trade,
  ETH Testnet con lo storico avvelenato dal fill patologico). Restano tre forward test Paper con
  bracket verificati: `0` RegimeConditional **AAVE 1d** (~2 trade/mese), `1` RsiOversold **DOT 15m**,
  `2` RegimeConditional **XLM 1h** (~9,8 trade/mese). Preferenza del proprietario registrata:
  **sempre intraday/swing**, il giornaliero solo come controllo.
- **Sei cacce, zero promozioni automatiche, due candidati in forward test.** Intraday 15m/5m, ALT
  15m, 30m, 1d majors, 1d largo (34 serie), 4h largo, 1h largo: sempre 0 sopravvissuti ai gate. I
  due schierati **non hanno passato l'anti-overfitting** (XLM lo ha fallito col DSR a 0,677 sotto
  la penalità di 64 tentativi; AAVE non l'ha nemmeno affrontato perché già fuori per conteggio
  trade) e stanno in Paper proprio per questo: il forward test è l'unico giudice immune al
  multiple testing. Lezione trasversale: **guardare selezione e holdout insieme** — un holdout che
  esplode su una selezione piatta (INJ 4h: 0,06 → 1,85) è un allarme, non una promozione.

**Fatto il 2026-07-27 (secondo blocco): Filone D, D1 e D2.** SHAP esatto sui modelli ad alberi di
ML Lab (verificato contro Shapley per forza bruta) e monitor di deriva dei fattori con pavimento di
rumore in `/feature-selection`. Suite 1641/1641, nessuna modifica di schema, nessuna riga sul
percorso di trading. Due difetti trovati **dai test prima della UI** — la foresta che media invece
di sommare, e una soglia IC sotto il pavimento di rumore che etichettava il puro caso come "segno
invertito" — nel [report](REPORT-FILONE-D-2026-07-27.md).

**Poi, in ordine:** osservazione B3 (tick-vs-candle → R1 pieno) · confronto forward-vs-predizione
sulle tre corsie (AAVE/XLM holdout coerente, DOT dato per perdente dal CPCV) · B4/B5 · A6 · C4/C5.
**Filone D residuo:** D3 segue i tempi di C5; D4 (e l'eventuale D5) dopo C4/C5 — e dopo la misura di
D1, che dà un argomento in più per non aspettarsi molto dal direzionale (coerenza direzionale 6-39%
su tutti i fattori della serie provata).

*Il carry Paper resta ON (unica classe con edge misurato positivo). Il router di regime resta in
osservazione per misura (esito C1.b), non per prudenza.*
