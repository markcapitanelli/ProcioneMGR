# ROADMAP — Integrazione, core caldo e scoperta pattern (viva, 2026-07-28)

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
| B2 | `MarketData:UseRemoteIngestion=true` | **acceso (2026-07-26), gate in osservazione**: worker di sync nel servizio in-cluster (228/228 serie OK al primo ciclo, 0 errori, candele 5m fresche), `POST /sync/{id}` verificato; monolite locale delegato (toggle nell'appsettings reale + port-forward 18080 best-effort in run-postgres.ps1). Richiede Docker Desktop attivo; i buchi da downtime si auto-riparano al ciclo successivo (cursore incrementale) | 7 giorni di sync senza buchi nelle candele — scadenza 2026-08-02, **ma il gate era cieco: vedi B2.a** |
| B2.a | **Il gate B2 non sapeva vedere una serie ferma (2026-07-28).** Entrambi gli strumenti che dovevano misurarlo erano ciechi per costruzione: lo stato di sync scriveva `OK: N candele` contando le righe processate, e su una serie morta il cursore incrementale ri-chiede l'ultima candela nota, l'exchange la restituisce, l'upsert la riscrive ⇒ `OK: 1 candele` **a ogni giro, per sempre**; l'audit `coverage` misurava la densità sull'intervallo `[prima, ultima]` della serie stessa, e una serie ferma è densa al 100% del proprio passato. Introdotta `SeriesFreshness`: **una sola regola**, condivisa da sync e audit (due regole darebbero due verdetti sulla stessa serie — il difetto già visto in D2), che misura il ritardo contro **adesso**, l'unico riferimento che non si sposta insieme al guasto. Riferimento = ultima barra **chiusa**, non quella in formazione: quest'ultima a database c'è solo se il ciclo di sync è passato mentre era aperta, e misurare contro di essa farebbe oscillare l'allarme fra 0 e 1 senza che sia successo niente. Scoperte **7 serie ferme su 228**: MKR/USDT (4 tf, ultima 2025-09-15 — 30.388 barre indietro sui 15m) e TON/USDT (3 tf, ultima 2026-06-30). Verificato su Binance: entrambi in stato **BREAK**, scambi sospesi — non riparabili. Il worker le segnala con `LogWarning` ma **non le disabilita da solo**: spegnere una serie è una scelta umana, e un `BREAK` può essere temporaneo | fatto (codice) · **resta da eseguire**: disabilitare le 7 serie (SQL in [REPORT-B2-FRESCHEZZA](REPORT-B2-FRESCHEZZA-2026-07-28.md), o 7 clic in `/watchlist`) | ✅ 11 test, fra cui la riproduzione dei due casi reali, la soglia verificata esattamente dove è scritta, e il caso-trappola: serie vuota e timeframe ignoto **non** valgono "aggiornata" (un `null` in un confronto numerico si comporta da zero, e zero significa fresca) |
| B3 | `Trading:UseRemoteTrading=true` con **feed R1 acceso dentro il servizio** (prima in osservazione: `DriveProtectiveExits=false`, confronto tick-vs-candle) | **eseguito (2026-07-26), osservazione in corso**: motore nel servizio in-cluster (3 corsie auto-riprese: 2 Paper + 1 Testnet demo), feed R1 connesso a Binance+Bitget in SOLO OSSERVAZIONE, **carry spostato nel core** (registrato in `AddTradingLanes` ramo `!useRemote`, come feed e watchdog: alla prima valutazione ha aperto BTC Paper, funding 6,0%>5,0%) e guscio locale su `UseRemoteTrading=true` (gRPC via port-forward 18092, aperto da run-postgres.ps1). **Chaos test eseguito**: kill brutale del guscio → in 4 minuti di orfanità 2.713 valutazioni di corsia, 0 errori/STALE, 0 restart (posizione carry in-memory intatta), corsie `running=True` interrogate via gRPC a guscio morto; guscio riavviato e riconnesso. Onestà: al momento del kill le posizioni aperte erano quelle del carry (le corsie erano a 0 posizioni), quindi "lo stop scatta a guscio morto" è dimostrato a livello di macchina delle uscite sempre attiva, non di stop realmente scattato — lo dimostrerà l'operatività dei prossimi giorni | ~~chaos test~~ ✓ · ~~drill di restore~~ ✓ · ~~confronto tick-vs-candle~~ ✓ **fatto 2026-07-28, esito NEGATIVO: `DriveProtectiveExits` resta `false` per misura** (vedi B3.a) |
| B3.b | **Sentinella d'ombra dal vivo (2026-07-28, richiesta del proprietario).** Il replay ha dato il verdetto, ma non può vedere tre cose: il momento VERO del tick (il replay data la scoperta alla chiusura della barra fine — un limite inferiore), il prezzo davvero disponibile al tocco (il replay assume il livello), e soprattutto **l'evento che nella finestra 2025-01 → 2026-07 non c'è stato**, cioè un crollo con gap. Quindi i tick non vengono più scartati: il motore li **osserva**, e quando il percorso a candele chiude davvero quella posizione nasce **un** confronto (tabella `ProtectiveExitShadows`, additiva, già applicata al DB reale). **Non è una misura, è una sentinella**: tre corsie fanno 3-6 uscite protettive al mese, troppo poche perché una mediana significhi qualcosa — quella domanda è chiusa dal replay su migliaia di posizioni. Il meccanismo è la **soglia**: sopra 200 bps *a sfavore del ritardo* si allerta sul caso SINGOLO, perché a quel punto non si sta misurando l'effetto dell'ombra sullo stop ma un salto di prezzo dentro la barra. Sotto soglia, e nel caso opposto (il ritardo conviene), silenzio: è il verdetto già noto e notificarlo sarebbe rumore. Per la potenza statistica resta il replay, da ri-eseguire sui dati freschi — 30 simboli invece di 3, zero rischio (**fatto**: CronJob `exitlag-monthly`, che fallisce se il verdetto si rovescia) | **fatto e IN FUNZIONE** — redeploy `562b359` la sera stessa: core ripartito, 3 lease riacquisiti, carry riavviato, feed che dichiara «i tick osservano soltanto» | ✅ 8 test, e quello che conta è **l'inerzia**: i tick non chiudono nulla e non toccano `BestPriceSinceEntry` — se lo toccassero, il livello di trailing del percorso a candele si sposterebbe col ritmo dei tick e il feed deciderebbe le uscite senza che nessun toggle lo dica, con un effetto visibile solo come uno stop scattato «un po' prima» del previsto, cioè non visibile affatto |
| B3.a | **Il gate era CIRCOLARE, e la sua seconda gamba è FALLITA (2026-07-28).** Il gate chiedeva di confrontare `source=tick` e `source=candle` su `procione.trading.protective_exits`, ma in osservazione i tick sono scartati e quella serie non può esistere: il confronto che doveva autorizzare l'accensione ne presupponeva una già fatta (e il commento in `trading-config.env` descriveva una strumentazione inesistente). Chiusa **offline** con `ProtectiveExitLagAnalyzer`: le candele fini (5m, 15m dove 5m non c'è) fanno da surrogato dei tick, stesso `ProtectiveExitEvaluator` del motore su entrambi i percorsi, stati del trailing separati, bracket veri letti dagli `EnsembleStates`. **Esito: uscire al tocco è PEGGIO che uscire a barra chiusa su 24 configurazioni su 24** (3 corsie × 8 larghezze di stop) — è lo stop preso sull'ombra. Costo mediano del ritardo: AAVE 1d −77,4 bps, DOT 15m −6,1, XLM 1h −5,2 (negativo = aspettare conviene). Il feed resta acceso per le altre due funzioni (consegna anticipata delle candele chiuse, watchdog di staleness). Dettaglio, limiti e comando in [REPORT-B3-EXITLAG](REPORT-B3-EXITLAG-2026-07-28.md) | fatto — esito negativo | ✅ 13 test, fra cui il **controllo sul rumore** (su passeggiata aleatoria senza deriva il costo del ritardo è zero entro 3 errori standard, su 12 semi, con barre di corsia aggregate esattamente da quelle fini) e il controllo di identità (stessa risoluzione ⇒ anticipo esattamente zero). Riapre solo una corsia **Futures a leva**: sulla liquidazione l'asimmetria è reale e va misurata a parte |
| B4 | ML remoto *solo se* il dual-read osservativo dimostra parità (`procione.ml.comparisons`) | **bloccato, non aperto** (vedi B4.a) | N settimane di confronti senza divergenze decisionali; altrimenti resta in-process e non è un fallimento |
| B4.a | **Il cronometro di B4 non può partire, e non per il toggle (2026-07-28).** La sezione `Ml` è assente da `appsettings.json`, quindi `MlComparison` è spento e `RemoteUrl` vuoto — ma accenderli non produrrebbe nulla. `TradingEngine.FireAndForgetMlComparison` scatta **solo** quando la strategia in valutazione è il Champion e si risolve in una `MlStrategy` ([TradingEngine.cs:514](../ProcioneMGR/Services/Trading/TradingEngine.cs)), e oggi mancano entrambe le condizioni: le tre corsie vive girano `RegimeConditional` (×2) e `RsiOversold`, e nel registry **non esiste un solo modello Champion** — tutti e 53 i `SavedMlModels` sono in `Staging`. Accendere il toggle darebbe una metrica ferma a zero che *sembra* osservazione in corso: esattamente il `OK: 1 candele` di B2, in un altro punto. Quindi **non è stato acceso**. Sbloccarlo richiede una decisione di prodotto che non è mia: promuovere un modello a Champion e dedicargli una corsia. La domanda *tecnica* («il binario remoto calcola le stesse cose?») si può invece chiudere offline come B3 e D3 — confronto locale-contro-remoto su vettori di feature reali, scegliendo il modello per `model_id` (che nel servizio ha precedenza sullo stage, quindi funziona anche su uno `Staging`) | fatto — diagnosi | il pod `procionemgr-ml` gira da 2 giorni servendo zero richieste |
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
| C5.a | **Il passo 3.3 è stato MISURATO senza fare 3.1 e 3.2 (2026-07-28).** Il piano metteva la raccolta prima della misura per necessità apparente; i dump pubblici di Binance contengono tape e profondità storici, quindi la domanda «il book aggiunge IC oltre al proxy?» si è potuta chiudere su 30 giorni × 3 simboli **senza accendere alcuna raccolta e senza lasciare un costo permanente**. Esito: lo **sbilanciamento di profondità** aggiunge IC (0,033-0,046 a 5 minuti, p 0,005, replica su BTC/ETH), il **tape sotto il minuto non aggiunge nulla** (aggregare a 10 s invece che a 1 minuto non serve), e l'edge è **6-34× sotto il costo di andata e ritorno**. Quindi: 3.1/3.2 restano **non fatti e non giustificati per operare**; l'unico uso sensato del segnale è l'esecuzione (dove il giro è già pagato). Dettaglio in [REPORT-D3-OFI](REPORT-D3-OFI-2026-07-28.md) | fatto — misura, non cablaggio | ✅ 49 test; integrità del parser verificata contro le klines su 43.200 minuti (totali di giornata identici); un difetto del giudice trovato dal test del rumore |

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
| D2.a | **Job periodico + widget in Home COMPLETATI (2026-07-27, dopo ri-audit del PRD §5e)** — l'audit riga per riga ha trovato che mancavano due requisiti espliciti: l'alert «accanto al widget decadimento-strategia in Home» e il «job di calcolo periodico» che lo alimenta. Il pannello rispondeva solo a chi andava a cercarlo, mentre il senso del monitor è accorgersi di un fattore che si spegne **senza doverci pensare**. `FactorDriftWorker` calcola sulle serie della watchlist. Solo gli 8 fattori scritti a mano, tetto sulle serie, nessuna azione automatica | fatto | ✅ 10 test, incluso il controllo che il job **trovi davvero** un fattore piantato che si spegne, e che il tetto sulle serie e le serie disabilitate siano rispettati |
| D2.b | **Persistenza della storia dell'IC (2026-07-28, decisione del proprietario)** — tabella `FactorIcWindows` (una riga per finestra per fattore per serie, indice unico ⇒ upsert idempotente), scritta dal job e **riletta all'avvio**: l'alert in Home c'è già al primo caricamento dopo un riavvio del guscio, invece di comparire dopo il primo giro (2 minuti + calcolo). Il verdetto sulla storia registrata passa dallo **stesso `Judge`** del calcolo fresco — due strade che possono divergere sarebbero due monitor diversi con lo stesso nome. Ampiezza della finestra **quantizzata** a passi di 250: una serie storica la cui finestra si sposta a ogni giro non è una serie, sarebbe una collezione di misure con pavimenti di rumore diversi. Nuovo pannello «storia registrata dal job» in `/feature-selection`, visibile senza calcolare nulla | fatto | ✅ 32 test nuovi (53 in tutto su D2, dopo il consolidamento): verdetto ricostruito **identico** a quello calcolato dalle candele su 4 scenari, soglia presa dall'ampiezza registrata e non dalla config, upsert che non duplica, riavvio del guscio che ritrova l'allarme, serie uscita dalla watchlist che **non** resuscita, rumore puro che non inventa allarmi, orizzonti forward che non si mescolano, quantizzazione stabile a +500 candele; migrazione applicata al DB reale e verificata in browser |
| D3 | **OFI vero**: formula (imbalance firmato al top-of-book) da innestare nello step 3.3 del pilota C5 già pianificato, confrontata contro il proxy `TakerImbalanceFactor` già esistente | eredita quella di C5 | stesso gate di C5: se non aggiunge IC oltre al proxy, si spegne a fine pilota — esito negativo valido |
| D3.a | **FATTO 2026-07-28, senza aspettare C5** (vedi C5.a). `Services/Microstructure/`: lettura dei dump storici, aggregazione del tape a 10 s, **OFI di Cont-Kukanov-Stoikov esatto** (implementato e verificato caso per caso — è la formula che consumerebbe il collettore dal vivo, perché `bookTicker` porta già le size che `BinanceStreamMapper` oggi scarta) e la variante su bande di profondità, che è la sola forma di book **esistente storicamente**: i file `bookTicker` non esistono, 404 su tutte le date provate. Il gate misura l'**IC parziale** sopra due controlli (flusso taker della candela **e** rendimento appena chiuso — il secondo aggiunto in corsa per escludere che il book fosse il reversal travestito), con nullo a rotazione del **migliore della famiglia** e traduzione dell'IC in punti base contro i costi | fatto | ✅ verdetto chiaro nei due sensi: il book **aggiunge** informazione (statisticamente solidissima, t≈8) e **non paga i costi** (6-34×). La raccolta permanente resta spenta |
| D4 | **DTW** pattern-matching su forma: genera trigger evento per il motore Discovery esistente, non una strategia propria | media, rischio alto (altro angolo di edge direzionale-tecnico, classe già a otto zeri) | **non negoziabile**: controllo con pattern sintetico piantato PRIMA di fidarsi di un risultato su dati reali (stesso principio della fase `control` di PlatformExpand); poi lo stesso collaudo CPCV+DSR+PBO+gemello di sempre |
| D4.a | **Motore FATTO 2026-07-27, gate SUPERATO**: `DtwMatcher` (z-normalizzazione obbligatoria, banda di Sakoe-Chiba, pruning LB_Keogh, occorrenze non sovrapposte). 22 test: il **pattern piantato viene ritrovato** anche dilatato nel tempo, LB_Keogh verificato come vero limite inferiore su 3.000 coppie casuali (se non lo fosse, il pruning scarterebbe in silenzio le corrispondenze migliori), 50.000 barre scansionate in tempo utile, 300 prove di fuzzing | fatto | — |
| D4.b | **Misura FATTA, con una correzione di metodo importante**: `DtwPatternAnalysisService` collega le occorrenze all'`EventStudy` già esistente. **I test hanno smascherato un difetto grave**: col solo placebo a date casuali, pattern casuali su rumore puro venivano dichiarati "segnale" **8 volte su 15** — perché selezionare finestre per FORMA induce da sola una deriva nelle barre successive, e un placebo a date casuali non conserva quel meccanismo di selezione. Introdotto il **nullo per forma** (si ripete l'intera procedura con pattern casuali, a parità di numero di occorrenze) e un **pavimento di rilevanza economica** dello 0,5% (un effetto dello 0,48% era stato dichiarato significativo: vero, ma sotto i costi di andata e ritorno). Con entrambi: edge piantato trovato, rumore respinto | fatto | — |
| D4.c | **Pannello UI + misura su dati reali FATTA**: pannello in `/market-analysis` (si sceglie una finestra della serie come modello). Prima misura, **SOL/USDT 15m, 54.984 candele fino al 2026-07-27**: il pattern si trova 500 volte (**26,2 occorrenze/mese**, frequenza adeguata all'obiettivo intraday), ma il rendimento anormale successivo (−0,22%) rientra in quello che producono forme qualunque (p 0,366, 95° percentile del nullo 0,42%). **Nono esito negativo, coerente con gli altri otto.** Dettaglio istruttivo: la CAAR PRE-evento è +0,23% contro −0,22% post — il pattern *descrive* un movimento già in corso invece di anticiparlo, esattamente l'artefatto meccanico che il nullo per forma esiste per smascherare | fatto — esito negativo | D5 (SAX) resta **non giustificato**: la sua condizione era che D4 mostrasse un segnale sopravvissuto ai gate, e non l'ha mostrato |
| D5 | **SAX** + mining di sequenze, come pre-filtro economico di D4 (non un motore parallelo) | ~~bassa, condizionata~~ **NON SI FA** | La condizione era: D4 supera il controllo sintetico **E** mostra un segnale che sopravvive ai gate. Il controllo l'ha superato, il segnale no (D4.c). SAX serviva ad *accelerare* una ricerca che si è rivelata senza premio: costruirlo ora sarebbe ottimizzare la velocità di una strada che non porta da nessuna parte. Si riapre solo se D4 trova un angolo che regge |

**Non-obiettivi di questo filone** (dettaglio nel PRD): non riaprire il regime-conditional (chiuso
da C1); non ri-cacciare pattern direzionali-tecnici su majors 1h/4h (otto zeri + consenso di
letteratura); non costruire LOB reale prima del verdetto di C5 — **verdetto ora dato (C5.a/D3.a) e il
non-obiettivo resta in piedi**: il book informa ma non paga i costi, quindi costruire una raccolta
permanente per operarci non è giustificato (per *misurare* il top-of-book vero, quello è un altro
discorso e la decisione sul dove è già presa: core caldo); DTW/SAX restano generatori di candidati per
il collaudo esistente, non una quarta pista di validazione.

**Le due deviazioni dal PRD sono state entrambe CHIUSE** (2026-07-27/28; § dettaglio nel
[report di esecuzione](REPORT-FILONE-D-2026-07-27.md)):

1. ~~La lente di D1 è la volatilità, non il regime K-means.~~ **Chiusa da D1.a**: la matrice usa i
   regimi K-means quando esiste un modello attivo della stessa serie e ripiega sui terzili di
   volatilità altrimenti, dichiarando in UI quale lente è in uso. L'argomento con cui avevo
   sostituito la lente («i regimi non discriminano, l'ha misurato C1») era sbagliato in quel
   contesto: lì il regime è un asse di raggruppamento descrittivo e non deve superare alcun gate.
2. ~~D2 non persiste nulla.~~ **Chiusa il 2026-07-28** (decisione del proprietario): tabella
   `FactorIcWindows`, una riga per finestra, scritta dal job e riletta all'avvio. L'argomento
   originale — «l'IC è deterministico dalle candele, salvarlo è una cache» — era vero e incompleto:
   il guscio si riavvia di continuo (e l'alert in Home restava vuoto proprio nei minuti in cui uno
   guarda la Home) e le candele non sono eterne (quando la finestra fine verrà ruotata, quella
   storia non sarà più ricalcolabile — allora è un'osservazione). È **l'unica modifica di schema del
   filone D**, additiva e già applicata al DB reale.

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
rumore in `/feature-selection`. Nessuna riga sul percorso di trading. Due difetti
trovati **dai test prima della UI** — la foresta che media invece di sommare, e una soglia IC sotto
il pavimento di rumore che etichettava il puro caso come "segno invertito" — nel
[report](REPORT-FILONE-D-2026-07-27.md).

**Fatto il 2026-07-28: FILONE D CHIUSO** (D2.b persistenza + D3 misurato).

- **D2.b**: la persistenza della storia dell'IC ha aggiunto l'**unica tabella** del filone
  (`FactorIcWindows`, additiva, applicata al DB reale). Provato dal vivo sulla 5199: il job scrive
  72 finestre × 5 serie della watchlist, il pannello «storia registrata» mostra 8 fattori con
  9 finestre da 2000 osservazioni (pavimento 0,044) senza calcolare nulla, e **dopo un riavvio del
  guscio l'alert in Home è presente in ~10 secondi** — con l'unico log «fotografia ricostruita dalla
  storia registrata (5 serie, 1 in allarme)», cioè prima che il job abbia girato. Era esattamente il
  buco che la persistenza doveva chiudere.
- **D3**: non ha più aspettato C5. I dump pubblici di Binance contengono tape e profondità storici,
  quindi la domanda «il book aggiunge IC oltre al proxy?» è stata misurata su 30 giorni × 3 simboli
  (43.200 candele ciascuno, 979 MB di dump in cache fuori dal repo) **senza accendere alcuna
  raccolta**. Risposta: **sì** per lo sbilanciamento di profondità a ±1% (3 simboli su 3, |IC
  parziale| 0,040-0,046 a 5 minuti, p 0,005 col nullo del migliore su 12 test), **no** per il tape
  sotto il minuto e per l'OFI come variazione — e **l'edge è 6-34× sotto il costo di andata e
  ritorno**, quindi non è operabile: l'unico uso sensato è l'esecuzione, dove il giro è già pagato.
  Verdetto, metodo e limiti nel [report di D3](REPORT-D3-OFI-2026-07-28.md); il difetto trovato dal
  test del rumore (il 99° percentile di 200 giri dava 3,3% di falsi positivi) è documentato lì.

**Consolidamento (stessa giornata, su richiesta del proprietario: chiudere i debiti prima di
proseguire).** Quattro difetti del lavoro appena fatto, trovati rileggendolo invece che aspettando
che li trovasse l'uso:
1. **Due regole per l'ampiezza della finestra** — il job quantizzava, il pannello no: sulla stessa
   serie uscivano soglie diverse e quindi verdetti che si contraddicevano ("si è spento" contro "non
   ha mai informato"). Ora la regola è una sola (`FactorDriftAnalyzer.SuggestWindowSize`), con un test
   che lo tiene tale; la differenza residua e legittima (il job guarda le ultime `MaxCandles` candele)
   è **dichiarata in UI** invece che subita.
2. **Il monitor guardava 5 serie su 228, per sempre le stesse** — un «nessun allarme» che voleva dire
   «non ho guardato». Ora il job **ruota** ordinando per ultimo calcolo (le mai viste per prime), la
   fotografia si ricostruisce dalla tabella a fine giro (altrimenti la rotazione farebbe sparire gli
   allarmi del giro precedente) e la Home **dichiara la copertura**.
3. **Verdetto del gate a due livelli** — la traduzione in punti base era nella fase CLI, dieci righe
   sotto il verdetto: ora è dentro il gate, che dice `NEGATIVO` / `INFORMA MA NON È OPERABILE` /
   `POSITIVO E OPERABILE`. Un «AGGIUNGE» non si può più leggere come «si può operare».
4. **Orizzonte dichiarato** nel pannello della storia registrata, con avviso quando il form sopra è
   impostato su un orizzonte diverso: i due numeri non sarebbero confrontabili.
5. **Finestre sovrapposte nella storia registrata** — trovato guardando la pagina viva, ed è il più
   grave dei cinque: ETH/USDT 1h mostrava «18 × 2000» finestre là dove in 20.000 candele ce ne stanno
   9. Il job carica le *ultime* N candele, quindi a ogni giro la griglia scivola e la tabella accumula
   finestre della stessa ampiezza ma sfalsate: punti che condividono dati, cioè correlati per
   costruzione — esattamente ciò che l'analizzatore evita, aggirato dalla persistenza a sua insaputa.
   E non era cosmetico: la media su griglie mescolate **nascondeva un allarme vero** (RsiFactor su
   ETH/USDT 1h, riapparso appena corretto). Il lettore ora costruisce la catena dal presente
   all'indietro tenendo solo ciò che non si accavalla.
6. **Misura di D3 15 volte più veloce** (62 s contro ~15 min per simbolo) precalcolando ciò che nel
   nullo non cambia e sfruttando il fatto che i ranghi di una serie ruotata sono i ranghi ruotati.
   Numeri identici, con un test che confronta il nullo veloce con quello ingenuo.
7. **La deviazione dal PRD §5d è dichiarata invece che nascosta**: `/feature-selection` spiega perché
   l'OFI non si misura da lì (i dati di book non sono nel database) e riporta l'esito col comando per
   rifare la misura.

**Fatto il 2026-07-28 (secondo blocco): ripresa del filone B, e il filo conduttore è uno solo.**
Chiusa la Fase D, il PRD del core caldo è stato ripreso misurandolo invece che rileggendolo — e
**tre gate su quattro non potevano essere soddisfatti per come erano scritti**, ciascuno perché
nessuno aveva costruito lo strumento che li misura:

- **B3** chiedeva un confronto tick-vs-candela che l'assetto osservativo non poteva produrre.
  Chiuso offline, **esito negativo**: uscire al tocco è peggio che uscire a barra chiusa, 24
  configurazioni su 24 (B3.a).
- **B2** aveva due strumenti entrambi ciechi alle serie ferme, perché guardavano la serie contro sé
  stessa. Ora la freschezza si misura contro adesso: **7 serie morte su 228** trovate al primo giro,
  MKR e TON, entrambe in stato `BREAK` su Binance (B2.a).
- **B4** non è aperto ma **bloccato**: il dual-read scatta solo su una `MlStrategy` Champion, le
  corsie vive girano altro e nel registry non esiste alcun Champion. Accendere il toggle avrebbe
  dato una metrica ferma a zero che *sembra* osservazione (B4.a).
- **§6 del PRD** (smoke e2e) fatto per la metà che conta: 9 asserzioni verdi contro il cluster vero,
  fra cui la prova che la NetworkPolicy davanti agli ordini Live è **davvero applicata** e non solo
  accettata dall'API server.
- Trovata e chiusa anche una **posizione Paper orfana** sulla corsia 3, che non esiste più da quando
  `LaneCount` è tornato a 3: stop e trailing configurati e nessun motore a valutarli. Il watchdog non
  poteva vederla perché itera sulle corsie *configurate*; ora guarda anche fuori.

La lezione trasversale, che vale per i gate futuri: **un gate va scritto insieme allo strumento che
lo misura.** Tre di questi sono rimasti fermi per giorni senza che nessuno potesse dire perché, e in
due casi lo stato dichiarato era attivamente falso.

**Chiuso in serata (2026-07-28):** le 7 serie morte sono **disabilitate** (221 attive, `coverage`
dice «tutte fresche»: **gate B2 verde**) · **sentinella d'ombra** costruita e schema applicato
(B3.b) · su B4 la decisione è **non promuovere alcun Champion**: promuovere per sbloccare un gate
sarebbe la stessa forma-senza-sostanza corretta tre volte oggi.

**Messo in esercizio la sera stessa (2026-07-28), master `562b359`:** `trading`, `ingestion` e
`strategyhunter` bumpati e sincronizzati via ArgoCD (trading a mano, com'è scritto che debba essere).
Il core è ripartito con i 3 lease riacquisiti, il carry riavviato e il feed che dichiara «i tick
osservano soltanto». **Il guardiano nuovo delle posizioni orfane ha suonato da solo**, sulla corsia 3
vera, appena il pod è tornato su — verifica dal vivo, contro il caso reale che l'aveva motivato,
della modifica scritta quella mattina. Aggiunta la **CronJob mensile `exitlag-monthly`**, che
fallisce di proposito se il verdetto di B3 si rovescia: un controllo periodico che si limita a
stampare finisce in log che nessuno rilegge.

**Fatto in serata (2026-07-28), revisione richiesta dal proprietario.** Due cose.

*Integrazione.* Tutto il filone D risultava correttamente in pagina; **le tre cose costruite di corsa
sui gate no** — la sentinella d'ombra scriveva su una tabella che nessuna query leggeva, l'allarme
sulle posizioni orfane viveva solo nei log del pod, l'analizzatore del ritardo era raggiungibile solo
da CLI. È la forma di C4 prima del suo consumo: verde a livello di classe, inesistente a livello di
prodotto. Chiuso con un pannello unico in `/trading`. Aggiungere il servizio ha fatto **fallire 10
render test** della pagina, trovati solo eseguendo le prove d'interfaccia.

*Ricerca — perché non consolida mai.* Risposta doppia, in
[REPORT-PERCHE-NON-CONSOLIDA](REPORT-PERCHE-NON-CONSOLIDA-2026-07-28.md). L'imbuto ricostruito su
**3.472 candidati**: il 90,5% muore col Sharpe holdout medio a **−1,87** (e lì i gate hanno ragione:
la fascia «appena sotto soglia» è 88 candidati su 3.472, e lo Sharpe crolla di 2,06 punti dalla
selezione all'holdout — overfitting della selezione, non gate cattivo). Ma i ~100 che **guadagnano**
incontrano un gate che sull'holdout di 4 mesi è **insuperabile per aritmetica**: invertendo
numericamente il DSR, un edge di Sharpe 1,0 richiede **6,2 anni** di fuori campione, uno Sharpe 3 ne
richiede 0,7. Non è severo, è mal dimensionato rispetto alla finestra — il che rende il **forward
test in Paper non un ripiego ma l'unico giudice disponibile** a questa scala di dati. In più: il gate
dei 10 trade butta via **232 candidati che guadagnano** (Sharpe +1,01), il 70% di quelli che passano
il gate di Sharpe; e la ricerca spende il **4,4%** dello sforzo sui timeframe (5m/15m) che sono
l'obiettivo dichiarato.

**Poi, in ordine:** spostare la ricerca su 5m/15m (dove il gate del conteggio trade non morde e dove
si vuole operare) · rendere il gate dei trade relativo alla frequenza invece che assoluto ·
chiudere/archiviare la posizione orfana della corsia 3 · misura di parità ML offline su un modello
`Staging` (chiude la domanda tecnica di B4 senza toccare le corsie) · confronto
forward-vs-predizione sulle tre corsie · B5 · A6 · C4/C5.

*Il carry Paper resta ON (unica classe con edge misurato positivo). Il router di regime resta in
osservazione per misura (esito C1.b), non per prudenza.*

---

## Audit backend↔frontend (2026-07-29)

Scansione della mappatura 1:1 fra ciò che il backend fa e ciò che l'interfaccia permette di
governare — la regola d'oro della piattaforma. Il codice è risultato **sano**: nessuno stub,
nessuna funzione vuota, nessun servizio orfano (i tre candidati sospetti erano consumati
internamente). Il debito era tutto di **esposizione**.

**Quattordici sezioni di configurazione governavano funzioni vive e si toccavano solo editando
`appsettings.json` a mano** — fra queste il feed real-time con il suo potere di chiudere posizioni,
il limite di esposizione correlata fra corsie, il router di regime con le sue regole, il watchdog
degli invarianti, il canale Telegram, il forward test del carry. È la stessa forma del difetto
trovato la sera del 28: verde a livello di classe, inesistente a livello di prodotto.

**Un bug di perdita dati.** Il pannello *Sync dati* di `/admin/autonomy` salva la sezione
`MarketData` con un POCO di tre scalari; `MarketData:Realtime` è una sottosezione che quel POCO non
modella, e il writer — che sostituisce la sezione intera — **la cancellava**. Salvare la cadenza
del sync azzerava la configurazione del feed, in silenzio. I test coprivano le sezioni sorelle e le
sottosezioni sorelle, non il padre che ne contiene una.

| Cosa | Dove |
|---|---|
| Sottosezioni non modellate preservate dal writer | `AppConfigWriter` + 2 test |
| Validazione lato server dei pannelli admin (i `min=` HTML non vincolano `@bind`) | `AdminConfigRules` + 22 test |
| Feed real-time, sentinella d'ombra, esposizione correlata, router di regime (con editor delle regole), watchdog invarianti | **nuova** `/admin/protections` |
| Campagne+trigger, soglie di consolidamento, carry (con stato dal vivo), liquidazioni, notifiche (con invio di prova), dual-read ML, export OTLP | `/admin/autonomy`, da 6 a 13 pannelli |
| Modello di costo (impatto, spread, fette) | pannello in `/execution` |
| Interruttori resi davvero a caldo (prima uscivano a startup e restavano morti) | `CarryWorker`, `LiquidationSyncWorker` |

La regressione è chiusa da `ConfigurationUiCoverageTests`, che scandisce i sorgenti e pretende che
ogni sezione letta dal codice abbia **o** una pagina che la espone **o** una ragione dichiarata per
non averla. Sette sezioni restano deliberatamente fuori dalla UI: sei scelgono la topologia del
processo (chi ospita cosa, vincolato al deploy — `Trading:UseRemoteTrading` col valore sbagliato
significa due esecutori sulla stessa corsia) e una è solo memoria (`FactorCache`).

### Verifica dal vivo (2026-07-29, sera)

Lavoro integrato in master, guscio riavviato, **provato nel browser sul cluster vero**. La prova ha
trovato cinque difetti che i test non potevano vedere, perché girano su configurazioni sintetiche
mentre il guasto stava nel rapporto fra due processi.

| Difetto | Perché i test non lo vedevano |
|---|---|
| Badge liquidazioni verde «connesso · 0 msg» mentre quello **è** il guasto (blocco EEA/MiCA) | l'avviso aveva la condizione «acceso E **non** connesso»: non compariva mai nel caso per cui era stato scritto |
| Simboli carry e sentiment duplicati nei campi CSV | il binder delle liste .NET **appende** ai default; i worker deduplicavano già, mentiva solo la vista |
| Telegram muto dal 2026-07-27 | il consolidamento aveva perso il caricamento di `TELEGRAM_BOT_TOKEN`; il dispatcher per contratto non propaga l'errore, quindi **nessuno poteva accorgersene** |
| «Invia notifica di prova» dichiarava successo mentre il recapito falliva | passava da `NotifyAsync`, `void` per progetto — una verifica che rassicura sempre è peggio di nessuna verifica |
| I pannelli configuravano un motore che non li leggeva | il file su PVC, unico canale previsto, resta vuoto quando il guscio non gira nel cluster |

**Il difetto strutturale è l'ultimo**, e ha richiesto un contratto nuovo: `GetEngineConfig` /
`SetEngineConfig`, con allow-list chiusa lato server. Provandolo è emerso il difetto sotto il
difetto: **`reloadOnChange` non funziona su un mount PVC** (inotify non lo attraversa), quindi il
motore scriveva la configurazione e continuava ad applicare quella vecchia. Rimedio: rilettura
esplicita dopo la scrittura — chi scrive sa di aver scritto, e non serve alcun watcher.

Conseguenza misurata: `Trading:CorrelatedExposure` e `Trading:RegimeRouting` erano `Enabled=true`
nel file del guscio e **spenti nel motore**. Impatto pratico nullo — il limite di correlazione è
inerte in spot sotto il 30%, il router era comunque in sola osservazione, e le soglie di
`Trading:Safety` coincidevano coi default — ma la postura dichiarata non era quella reale.
Riallineate dal pannello e verificate dentro il pod.

L'interruttore del feed è stato provato **in entrambi i versi sul core in esercizio**: spento →
«connessioni chiuse, code svuotate» entro 5s; riacceso → feed su 2 exchange. Senza riavviare il
pod. Il ricontrollo dei log ha poi trovato un ultimo difetto: la watchdog di staleness allertava a
ogni accensione («ultimo: mai»), cioè un falso allarme su Telegram a ogni toggle — corretto
distinguendo «non ha ancora cominciato» da «ha smesso», senza perdere il caso dell'endpoint
perennemente muto.

### Chiusura dei tre punti sospesi (2026-07-30, notte)

**Immagine del motore.** Non serviva alcuna credenziale locale: `docker-build.yml` esisteva già e
pubblica con `GITHUB_TOKEN` dopo la guardia che rifiuta immagini contenenti un `appsettings.json`
reale. Pushato master, entrambe le pipeline verdi — e `ci.yml` è la prima verifica della suite su
una macchina che non è quella di sviluppo. Motore ora pinnato **sul digest**
`sha256:300ca20d…`, non su un tag: un tag si può spostare, il digest *è* l'immagine. Il rollout
l'ha scaricata da GHCR, quindi la provenienza è verificabile.

**Port-forward stantio.** Il controllo era «la 18092 è in ascolto», insufficiente: alla
sostituzione del pod kubectl resta in ascolto e instrada verso un pod morto — capitato due volte lo
stesso giorno. Il rimedio non è una sonda di rete (una connessione verso un forward morto viene
accettata in locale e muore dopo, quindi ogni euristica sul socket è fragile) ma registrare **quale
pod** il tunnel serve e confrontarlo con quello vivo. Verificato sul caso reale.

**Notifiche del motore — e la trappola che c'era sotto.** `Notifications` entra nell'allow-list
scrivibile e nasce l'rpc `SendTestNotification`, eseguito *dal motore*. Al primo click ha risposto
**notifiche SPENTE**: il motore non aveva né l'interruttore né il token, quindi gli allarmi di
**quarantena** — i più importanti che la piattaforma emetta — non erano mai arrivati a nessuno. Il
guscio recapitava, quindi sembrava tutto a posto.

Ma il piano per accenderle era sbagliato, e lo si è visto solo aprendo il pannello: la scheda
Notifiche leggeva e scriveva il canale del **guscio**, e il messaggio d'errore diceva «abilitale qui
sopra e salva, si scrive sul motore» — falso. Metà del buco era chiusa: si sapeva diagnosticare il
canale del motore, non configurarlo. Seguendo quelle istruzioni si sarebbe salvato sul guscio,
visto un messaggio verde, e il motore sarebbe rimasto muto. Da qui un blocco **«Canale del
motore»** separato: due canali distinti e non uno con due pulsanti, perché tenerli insieme è
esattamente ciò che fa credere di aver acceso là accendendo qui.

Chiuso end-to-end: token nel Secret (mai transitato per la UI né per un argomento di processo),
pod riavviato, config scritta via gRPC e riletta, `Delivered`, **messaggio confermato ricevuto dal
proprietario**.

Suite **2005/2005** al momento del push; 2010 con i test aggiunti dopo.

---

## Filone E — Bonifica della classe «controlli che rassicurano» (2026-07-31)

L'audit backend↔frontend si è chiuso con un bilancio netto: **sette difetti su sette trovati dal
livello 4**, nessuno visibile ai test unitari, tutti della stessa forma — *un controllo che dice la
cosa rassicurante indipendentemente dalla realtà*. Le due regole nuove sono in
[STANDARD-VERIFICA](STANDARD-VERIFICA.md) (una verifica che non può fallire non è una verifica; chi
scrive una configurazione remota deve verificare che l'altro l'abbia LETTA). Questo filone applica
quelle regole **al resto della piattaforma**: i sette difetti sono corretti puntualmente, ma la
classe non è bonificata finché ogni superficie che emette un verdetto non è stata interrogata.

**Metodo del censimento** (2026-07-31, tre domande per ogni superficie):
1. *Come fa a dire di no?* — se la catena sotto non può restituire un esito negativo, il controllo
   mente sempre, e mente proprio quando serve.
2. *Misura contro la realtà o contro sé stesso?* — «OK: 1 candele» era denso al 100% del proprio
   passato; il riferimento che non si sposta col guasto è **adesso**.
3. *Chi scrive, raggiunge chi legge?* — un pannello che salva sul processo sbagliato è una manopola
   che non muove nulla, peggio di una manopola assente.

**Censite**: le 33 pagine con indicatori di stato, i 17 pannelli di configurazione (Autonomy,
Protections, ExecutionLab, Trading, Bot), i 4 endpoint `/health`, i probe K8s, i watchdog
(invarianti, staleness feed, master key), il contratto delle notifiche, i percorsi di scrittura
remota (motore gRPC, ingestion REST, GitOps). Superfici interrogate e giudicate **sane** (con la
ragione): `MasterKeyProbe` (sa dire di no, allerta, banner che si aggiorna); il pannello Modalità
Semplice (passa da DB + motore keyed, mai da file); ExecutionLab → sezione `Execution` (il
consumatore è il guscio stesso: `ExecutionSlicePlanner` del motore costruisce i suoi parametri da
sé); Carry e Notifiche-motore in Autonomy (già sullo store del motore); il banner di staleness di
`/trading` (si azzera al nuovo tentativo — era il difetto, è stato corretto); il `catch {}` di
`LaneExecutionLease.DisposeAsync` (dispose documentato, il rilascio è garantito dal server);
liveness/readiness K8s su `/health` incondizionato — **dichiarato, non difetto**: il probe misura
il processo, non il motore; la salute del motore la misura E6, nel posto dove un operatore guarda.
Cambiare la semantica del probe rischierebbe crash-loop per guasti che non c'entrano col processo.

### Le istanze vive trovate

| # | Cosa | Perché è la classe | Correzione | Gate / verifica |
|---|---|---|---|---|
| E1 | **Il pannello Sicurezza di `/trading` parla col processo sbagliato.** `ReloadSafety` legge `IOptionsMonitor` del guscio, `SaveSafetyAsync` scrive il file del guscio («attiva entro pochi secondi») — ma la `SafetyChecker` che applica quelle soglie ordine per ordine vive nel motore in-cluster, e `Trading:Safety` è **già** nell'allow-list del motore, il cui commento dice testualmente «cambiarle sul guscio non avrebbe alcun effetto». È l'unica superficie UI per le soglie di sicurezza, ed è il difetto n. 5 dell'audit in un'altra pagina | regola 6: chi scrive non raggiunge chi legge | `TradingPageService` passa a `IEngineConfigStore` (lettura dal motore, scrittura con rilettura); `ISafetyConfigWriter` rimosso (sarebbe codice morto che invita al riuso sbagliato) | test: il servizio legge/scrive dallo store e riporta il warning di override; render test della pagina; livello 4 sull'app vera |
| E2 | **Il pannello «Esecuzione live» di `/admin/autonomy` idem**: legge `LiveExecMonitor` (guscio) e salva via `ConfigWriter` (guscio), ma `ExecutionWorker`/`ExecutionSlicePlanner` girano accanto al motore. `Trading:LiveExecution` è già nell'allow-list | regola 6 | il pannello passa a `EngineConfig.WriteAsync`/`Bind` (stesso pattern del Carry, due righe sopra nello stesso file) | test su carico e salvataggio; livello 4 |
| E3 | **Il dual-read ML non è collegabile, e il pannello non può saperlo.** Il pannello scrive la sezione `Ml` del guscio; il consumatore (`TradingEngine.FireAndForgetMlComparison`) vive nel core, dove la sezione **non è nemmeno nel binding** (`Program.cs` del Trading host non la configura): anche a scrivere il file giusto a mano, il core resterebbe coi default. Accendere il toggle dal pannello mostra «salvato» e non muove nulla — due bugie impilate | regola 6, più un binding assente | bind di `Ml` nel Trading host; `Ml` nell'allow-list (senza segreti: `RemoteUrl` e toggle, mai chiavi); pannello sullo store del motore | test di integrazione gRPC: la sezione si legge e si scrive sul host reale; il toggle scritto è quello che il motore rilegge |
| E4 | **Il pannello «Sync dati» configura un worker che non esiste più in questo processo.** Con `MarketData:UseRemoteIngestion=true` (l'assetto corrente) lo scheduling vive nel servizio ingestion in-cluster, che legge la SUA config; il pannello scrive quella del guscio e promette «valgono entro ~1s». Tutti e tre gli scalari (Enabled, cadenza, backfill) sono letti solo dal servizio remoto | regola 6 | **onestà, non API nuova**: quando l'ingestion è remota il pannello disabilita i campi e dice dove vive la manopola vera (ConfigMap del deployment ingestion). L'API di configurazione remota dell'ingestion NON si costruisce, con motivo dichiarato: la cadenza non si cambia da settimane, il gate B2 è già chiuso, e un canale di scrittura verso un altro processo è superficie di sicurezza che si paga — la si costruisce quando il bisogno è reale | test: con `UseRemoteIngestion=true` i campi risultano disabilitati e il testo dice il posto giusto; con `false` tutto funziona come oggi |
| E5 | **Il canale d'allarme non ha una spia di guasto.** `NotifyAsync` assorbe l'esito per contratto (giusto per i producer), quindi un recapito fallito vive solo nei log — è esattamente ciò che ha tenuto Telegram muto per due giorni. Se il token scade domani, gli allarmi di quarantena falliscono in silenzio di nuovo, e il canale rotto non può auto-denunciarsi via notifica per definizione | regola 5: il guasto del canale non ha nessun posto dove dire di no | il dispatcher registra in memoria ultimo recapito e ultimo fallimento (esito, motivo, quando); il pannello Notifiche del guscio li mostra; per il MOTORE, rpc `GetNotificationChannelStatus` che riporta lo stato del suo dispatcher — leggibile senza inviare nulla | test: dopo un fallimento la spia lo mostra col motivo; dopo un recapito torna pulita; integrazione gRPC sul host reale |
| E6 | **`running=true` è un flag d'intento, non una prova di attività.** Se il DB muore o le candele smettono di arrivare, la corsia resta `running`, `/health` risponde ok, ArgoCD è verde — e stop/trailing non vengono valutati da nessuno. Nessun campo dice QUANDO il motore ha valutato l'ultima candela: è «OK: 1 candele» sul motore | regola 5 + riferimento sbagliato (il flag misura sé stesso) | `TradingEngineStatus.LastProcessedCandleUtc` (+ proto, additivo); `/trading` mostra il battito e lo colora **contro adesso** con la stessa regola unica di `SeriesFreshness` (due regole = due verdetti, il difetto di D2); il watchdog nel host del motore allerta UNA volta per transizione quando una corsia running è affamata — allarme, non quarantena: la corsia non è corrotta, è a digiuno | test: candela processata ⇒ battito aggiornato; corsia affamata oltre soglia ⇒ un solo allarme; corsia ferma ⇒ silenzio; livello 4 |
| E7 | **La freschezza delle serie non è mai arrivata a livello di prodotto.** B2.a ha costruito la regola (`SeriesFreshness`) ma il suo esito vive in `LogWarning` e nel tool CLI `coverage`: `/watchlist` mostra «attiva» per una serie che può essere ferma da mesi (MKR lo è stata per dieci). È il verde a livello di classe, di nuovo | regola 3 dello standard + la lezione D2.a: accorgersi «senza doverci pensare» | colonna «ultima candela / ritardo» in `/watchlist` calcolata con la STESSA `SeriesFreshness`; worker di guardia nel guscio che notifica la TRANSIZIONE a stantia (una volta per serie, non a ogni giro) | test: serie ferma ⇒ una notifica sola; serie che torna fresca ⇒ reset; la colonna usa la regola condivisa, non una copia |

**Ordine di esecuzione**: E1→E2→E3 (stesso pattern, stessa infrastruttura — il canale di
configurazione del motore); E4 (onestà); E6 (battito); E5 (spia del canale); E7 (freschezza in
prodotto). I primi quattro chiudono la regola 6 su tutta la piattaforma; gli ultimi tre danno alla
regola 5 le superfici che le mancavano.

### Esecuzione (2026-07-31, stessa giornata)

Tutti e sette gli item **eseguiti in codice con test** (livelli 1-3 dello standard):

- **E1**: `TradingPageService` sullo store del motore; `ISafetyConfigWriter` rimosso; scoperto e
  chiuso in corsa un **difetto adiacente**: `ReloadSafety` copiava una lista fissa di campi e i due
  parametri B1 (deviazione fill) non c'erano — un salvataggio dal pannello li avrebbe azzerati ai
  default, la stessa famiglia del bug `MarketData:Realtime`. Il `Bind` dal JSON del motore lo chiude
  per costruzione. Aggiunta anche la **regola server-side** per `Trading:Safety` in
  `AdminConfigRules`: prima non esisteva, e la garanzia «un valore rifiutato in UI non entra da
  un'altra porta» per le soglie del motore era vuota. Test: store finto condiviso
  (`FakeEngineConfigStore` estratto in Infrastructure), motore irraggiungibile ⇒ default DICHIARATI,
  warning di override riportato all'operatore, default di fabbrica che passano la regola nuova.
- **E2/E3**: pannelli su `SaveEngineSectionAsync` (helper condiviso); `Ml` nell'allow-list, binding
  **e client gRPC** aggiunti al Trading host (mancavano entrambi: il core riceveva un client null e
  opzioni ai default del costruttore).
- **E4**: con `UseRemoteIngestion=true` la card Sync dati disabilita i campi e dichiara dove vive la
  manopola vera.
- **E5**: `NotificationDispatcher.ChannelStatus` (ultimo recapito, ultimo fallimento col motivo,
  fallimenti accumulati — Disabled e RateLimited NON contano come guasti: un falso allarme è la
  stessa classe del falso verde); rpc `GetNotificationChannelStatus` per il motore; spie in
  entrambi i blocchi del pannello Notifiche. 5 test.
- **E6**: battito `LastProcessedCandleUtc` + `Timeframe` nello status e nel proto (campi 22/23,
  additivi); badge in `/trading` giudicato con la regola unica di `SeriesFreshness`; allarme di
  inedia nel `LaneInvariantWatchdog` col discriminatore **stantio-E-fermo** (il replay di avvio è
  vecchio ma avanza: nessun falso critico a ogni riavvio). 5 test, incluso il replay che non
  allerta e il riarmo dopo il recupero.
- **E7**: colonna «Ultima candela / ritardo» in `/watchlist` + banner di conteggio ferme;
  `SeriesFreshnessWatchWorker` nel guscio (15 min) che notifica la TRANSIZIONE, aggregata. 6 test,
  inclusi il caso-trappola della serie vuota e «più serie ferme insieme ⇒ un solo messaggio».

**Livello 4 ESEGUITO la sera stessa (2026-07-31), sull'app vera collegata al cluster**, dopo aver
messo in esercizio il motore nuovo (digest `df9899c3`, build di GitHub Actions sul commit del
filone; il rilevatore di port-forward stantio ha suonato da solo sul cambio di pod — verifica dal
vivo, sul caso reale, della modifica del 30/07):

- **E1**: pannello sicurezza di `/trading` con header «letta e scritta sul MOTORE via gRPC ·
  sorgente: default del codice»; `MinOrderIntervalSeconds` 10→11 dal pannello e **verificato dentro
  il pod** (`/app/appsettings.json`: 11, con tutti i 19 campi — inclusi i due B1 che il vecchio
  pannello avrebbe azzerato), poi ripristinato a 10 e ri-verificato.
- **E6**: badge del battito accanto a RUNNING su corsia 1d («ultima candela 31/07 00:00 UTC · 0
  barre indietro») **e** su corsia 15m («31/07 18:30 UTC · 0 barre indietro»).
- **E2/E3**: entrambi i pannelli dichiarano l'host del motore e la sorgente, letti dal motore vero.
- **E4**: card Sync dati con l'avviso di ingestion remota e i tre campi disabilitati.
- **E5**: spia del guscio con recapito reale registrato; «Prova dal motore» → **CONSEGNATA** (rpc
  nuovo sul filo, Telegram ricevuto) e spia del motore aggiornata all'istante («ultimo recapito
  31/07 18:37 UTC»).
- **E7**: colonna di freschezza viva sulle 221 serie reali (tutte «aggiornata», nessun banner); le
  7 disabilitate dicono «non sincronizzata». Sistemata anche la **nota storica** di quelle 7
  (`LastSyncStatus` iniziava con «FERMA: disabilitata…», scritto il 28/07 quando quello era l'unico
  posto dove dirlo): ora dice solo il perché («disabilitata 2026-07-28 — Binance riporta stato
  BREAK»), senza contraddire il verdetto vivo della colonna accanto.

### La caccia densa, rieseguita con l'holdout allungato (2026-07-31)

Con l'occasione la fase `huntdense` è stata rilanciata **estendendo l'holdout dal 2 al 31 luglio**:
il mese di luglio non era mai stato visto da nessuna caccia, quindi l'estensione non costa nulla in
purezza e regala un mese di trade in più sopra la soglia minima. Esito, in 27 minuti su 30 simboli:

- **15m: zero candidati** oltre selezione+holdout (il timeframe dell'obiettivo dichiarato non
  produce nemmeno un sopravvissuto ai primi due giudici);
- **1h: 15 candidati** con 32-65 trade nell'holdout (la correzione del conteggio funziona), Sharpe
  holdout 0,30-1,58 — e il **gemello sintetico li boccia tutti e otto** i top: su questi mesi il
  puro caso arriva a Sharpe 2,2-2,9 al 99° percentile, e nessun candidato reale supera 1,58.

È il **decimo esito negativo consecutivo** della classe direzionale-tecnica, stavolta con la
finestra più lunga e il conteggio trade più onesto mai usati. La diagnosi della roadmap intraday
regge: l'edge non è in questa classe. Ciò che ha edge misurato positivo e opera **oggi**: il carry
delta-neutro (al riavvio del core ha riaperto BTC e BNB Paper a funding 8,9% annualizzato > soglia
5%) e le tre corsie di forward test Paper, che restano l'unico giudice immune al multiple testing.

**Non-obiettivi dichiarati**: cambiare la semantica dei probe K8s (vedi sopra); costruire l'API di
configurazione remota dell'ingestion (E4, motivo nella riga); toccare i verdetti della RICERCA — i
gate statistici hanno già i loro nulli per costruzione (livello 2 dello standard), e questo filone
riguarda i controlli OPERATIVI, dove il «no» non è una soglia ma un guasto da vedere.

---

## Filone F — Valore: costo del calcolo, voce della validazione, igiene dei verdetti (2026-08-01)

Nasce dall'**audit di valore 2026-08-01** ([AUDIT-VALORE.md](../AUDIT-VALORE.md): statica + test
dal vivo sull'app reale) più un **benchmark esterno** (Bailey–López de Prado MinTRL/DSR,
Harvey–Liu–Zhu t>3,0, evidenza 2025-26 su carry in compressione e market-neutral). Dettaglio,
accettazione e verifica per fase nel [PRD-VALORE-2026-08](PRD-VALORE-2026-08.md). Tre verità
dall'audit: la piattaforma è sana (zero errori runtime) ma spreca calcolo misurabile; alcune
superfici sono sopravvissute ai propri verdetti; la validazione è corretta ma muta
sull'aritmetica del campione — e il benchmark esterno CONFERMA le soglie, non le ammorbidisce.

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| F1 | Cache simulazione ensemble + refresh a corsa unica + poll 60s (prima: ~2K backtest ogni 15s per pagina aperta) | **fatto (2026-08-01)**, verificato dal vivo: a cache calda il poll costa due `SELECT max()` da 1-11ms, zero ricaricamenti di candele, zero backtest; 3 test nuovi (parità ⇒ mai due volte; candela nuova/config cambiata ⇒ invalida; fee viva nell'hash) | ✅ refresh a cache calda senza `RunBacktestAsync` nei log |
| F2 | Home: count 12,7M righe (4-7s misurati) → stima `pg_class.reltuples` | **fatto (2026-08-01)**, verificato dal vivo: 1-7ms, UI dichiara «≈» (scarto osservato ~3,7%, si riallinea con autovacuum); fallback al count esatto se reltuples=-1 | ✅ Home senza query >100ms sul count |
| F3 | Freschezza serie: group-by full-table (15,2s misurati) → max per-serie su indice | **fatto (2026-08-01)**, verificato dal vivo: «221 serie in 274ms» nel log del worker (55× più veloce); stessa regola `SeriesFreshness`, cambia solo come si ottiene il max | ✅ <2s, stessi verdetti |
| F4 | **Power check MinTRL**: lo Sharpe minimo che può passare il DSR dichiarato PRIMA dei backtest (formula Bailey-LdP; ancora interna: SR 1,0 ⇒ ~6,2 anni) | **fatto (2026-08-01)**: `MinTrackRecord` (MinTRL, inversa per bisezione, E[max] sotto il nullo) + stage `PowerCheck` subito dopo l'ingestione (additivo: `enforce` default false; a `true` ferma il run con la spiegazione e il rimedio F5). Le ancore sono TEOREMI ora: SR 1,0 vs zero ⇒ ~2,7 anni; 4 mesi × 300 tentativi ⇒ serve SR>2 annualizzato (l'aritmetica dei dieci «0 sopravvissuti», la stessa che huntdense ha toccato col caso a 2,2-2,9) | ✅ 10 test formula+stage; 87/87 pipeline invariati |
| G2 (audit) | Watchdog del feed per-SERIE: l'ultimo evento utile per simbolo con grazia dalla sottoscrizione — la staleness per-feed era il complice che ha reso invisibile C1 (un simbolo vivo copriva il silenzio degli altri) | **fatto (2026-08-01)**, dopo il merge di C1 (PR #57): notifiche aggregate per giro, simboli tolti dal set dimenticati | ✅ 3 test nuovi, 26/26 feed |
| F5 | **Fascia grigia DSR [0,80–0,95) → proposta forward Paper** con click umano (mai automatico, mai oltre Paper): la flessibilità sta nell'instradare al giudice giusto, non nell'abbassare le soglie | aperto | verdetto a tre vie nel run; assegnazione via flussi corsia esistenti |
| F6 | Gemello nullo SOLO sui sopravvissuti holdout: garantito da test d'ordine | aperto | candidati giudicati == sopravvissuti |
| F7 | `/metrics` ricollegata al motore in-cluster (oggi: tutti zeri col motore altrove — dashboard che osserva il processo sbagliato) | aperto | numeri ≠ 0 coerenti con l'audit log, sull'app vera |
| F8-F11 | Igiene: pulsante Live sempre-disabilitato → testo; KPI con ambito; alert deriva raggruppati; DTW nell'«archivio dei no» (verdetto D4) | aperto | L4 pagina per pagina |
| F12 | **Capacità e universo del carry** (l'edge positivo, da dimensionare ORA: benchmark esterno = basis 25%→<5% in due anni): premio per simbolo/exchange, flip, curva capacità con √-impact | aperto | report con verdetto scritto anche se «la soglia attuale è già ottima» |
| F13 | Market-neutral esteso sulle coppie (Kalman già validato → piccolo universo cross-sectional), Sharpe di letteratura (1,6-2,45) rimisurati coi costi PROPRI, via CPCV+gemello+F4 | aperto | sopravvissuti → F5/forward Paper; verdetto scritto comunque |

**Ordine**: F1-F3 (una giornata, si ripagano per sempre) → F4-F5 → F7+F8-F11 → F6 → F12 → F13.
**Non-obiettivi**: abbassare soglie di validazione; automatismi oltre Paper; implementare
letteratura senza rimisura coi costi propri; fusioni di pagine (M6, opzionale fuori PRD).

---

## Layer AI multi-provider (2026-08-01, richiesta del proprietario)

Filone parallelo al F, con PRD proprio: [PRD-AI-MULTIPROVIDER-2026-08](PRD-AI-MULTIPROVIDER-2026-08.md).
Fase A (fondazione) eseguita: chiavi cifrate a DB (`AiCredentials` + pannello in
/admin/ai-supervisor), provider come dato hot-reload (`Llm:Provider`), client NVIDIA
OpenAI-compatible con base URL parametrico, «Prova collegamento» che chiama davvero. Confine
advisory non negoziabile in ogni fase.

### Il quinto PDF esterno e il confronto punto-per-punto (2026-08-01)

Il proprietario ha portato "Architetture AI per Trading in C#" (deep research Qwen: ibridi
LSTM+XGBoost, ONNX Runtime, RL per l'esecuzione, risk management AI) chiedendo parere, ricerca
propria e roadmap. Come per i quattro PDF precedenti, la risposta è il confronto col già-misurato:

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| Ibridi LSTM+XGBoost per predire prezzo/direzione | Stacking out-of-fold (`ReturnPredictorCatalog`: Linear/RF/**LightGBM**/MLP) + `AttentionReturnPredictor` C#-puro già esistenti; il bersaglio direzionale-tecnico ha **10 conferme negative** control-validate | già superato in generalità; un modello più sofisticato non risolve un problema di segnale |
| LTR-Net / ibridi Transformer | nessun segnale ha margine che giustifichi la complessità | non fare |
| ONNX Runtime come pilastro MLOps | il local-first qui è PIÙ forte (training E inferenza in C#, bivio QLIB-4 deciso contro TorchSharp) | non come default; adottato **stretto**: pilota sentiment (sezione sotto) |
| RL per esecuzione ordini (PPO vs VWAP/TWAP) | **già respinto DUE volte** (QLIB-5: sim-to-real gap contro il proprio simulatore; audit tensortrade); sostituito da `AdaptiveExecutionAlgorithm` chiuso-forma + √-impact + fill queue-aware | resta respinto; la ricerca 2025-26 conferma il sim-to-real gap come problema aperto |
| Risk: previsione volatilità | log-HAR batte GARCH/EWMA del −41% QLIKE (24 simboli), cablato | già fatto, già oltre |
| Risk: regimi di mercato | K-means + router onestamente in osservazione; upgrade jump-model C1 ha fallito il gate | già fatto, limite misurato |
| Risk: anomaly detection | `MarketEventDetector` (p 0,002 dal vivo) + `FillSanityCheck`; il resto richiede book (2027) | parziale, dipendenza dichiarata |
| Latenza µs / GPU / FPGA / TensorRT | piattaforma intraday/swing su REST, non colocata (preferenza dichiarata) | non pertinente |

Il genuinamente nuovo coincideva coi candidati che il §3 del PRD aspettava — ed è stato eseguito:

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| Fase B | **Sentiment via LLM**: `ISentimentScorer` async, `LlmSentimentScorer` (provider attivo, path guard "sentiment", fallback interno al lessico, batch per il replay), `DelegatingSentimentScorer` hot-reload (default Keyword = zero costi), harness di confronto sul `FactorEvaluator` esistente + pannello «3. Scorer del sentiment» in /sentiment | **fatto (2026-08-01)** | ✅ test: parsing difensivo, fallback su ogni esito non-Ok, batch disallineato = fallback di batch, elemento non scorabile SALTATO e ritentato (mai uno zero inventato); confronto = stesse notizie, stesse candele, stesso giudice |
| Fase C | **Secondo parere multi-provider**: dopo l'advisory primaria riuscita, stessa analisi al provider di confronto (`Llm:ComparisonEnabled` default off), artifact con Kind proprio `LlmAdvisoryCompare`, best-effort FUORI dal breaker condiviso, affiancato nella card del run | **fatto (2026-08-01)** | ✅ test: fallimento del confronto non tocca la primaria; provider coincidente/ignoto/senza chiave = skip a voce; l'anti-join del worker ignora i Kind di confronto |
| — | Il gate per l'uso live del sentiment (qualunque scorer) resta l'IC OOS oltre i costi, misurato nel pannello: il pannello confronta, non promuove | — | — |
| Fase D | **Tre provider in un colpo** (chiavi procurate dal proprietario): `OpenAiCompatibleLlmClient` base astratta (era NvidiaLlmClient), sottoclassi Gemini/Groq/HuggingFace da 5 righe, classificatore errori sul contratto generico `<PROVIDER> HTTP <code>:`, resolver e pannello a 5 — il §1.2 del PRD provato tre volte; con 5 provider il secondo parere (Fase C) ha coppie utilizzabili anche senza credito Anthropic | **fatto (2026-08-02)** | ✅ test: endpoint/modello di default per provider, chiave assente col rimedio env giusto, tassonomia per codice HTTP, delegante che instrada su tutti e 5; L4: «Prova collegamento» per provider sull'app vera |

## Pilota inferenza locale (ONNX Runtime) — 2026-08-01

Sezione non-lettered come il layer AI (un pilota singolo non è un programma multi-item; si
promuove a Filone solo se cresce). PRD proprio:
[PRD-ONNX-SENTIMENT-PILOT-2026-08](PRD-ONNX-SENTIMENT-PILOT-2026-08.md) — separato dal
multi-provider perché NON è un consumatore di `ILlmClient`.

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| L1 | **Filiera 100% C#**: ML.NET (SDCA, iperparametri espliciti) → `ConvertToOnnx` (potato al solo "Score") → `Microsoft.ML.OnnxRuntime` CPU in-process; `HashingTextVectorizer` (FNV-1a, mai GetHashCode) condiviso train/inferenza; `OnnxSentimentScorer` con introspezione, `LastLoadError`, fallback al lessico; addestramento dal pannello /sentiment con **parità come gate di pubblicazione** (oltre 1e-3 il file si elimina) | **fatto (2026-08-01)** | ✅ parità ML.NET↔ORT ≤1e-3 su holdout; direzione conservata su frasi mai viste; corpus di test corretto quando il fit passava da scorciatoie degeneri (token unici per riga); modello gitignored (repo pubblico) |
| L2 | Modello pre-addestrato esterno (FinBERT-class): tokenizer con parità verificata, licenza per-modello, download pin+checksum | gated, non impegnato | si apre SOLO se L1 mostra IC che giustifichi lo sforzo nel pannello di confronto |

**Non-obiettivi** (dettaglio nel PRD): ONNX come pipeline di default; GPU/TensorRT; RL esecuzione
e ibridi direzionali del PDF (già misurati e respinti).

---

## Filone AF — Autonomia Finanziaria (2026-08-02, dodicesima roadmap)

Nato dal sesto PDF esterno (framework multi-agente per l'autonomia) e dalla richiesta del
proprietario: piattaforma quasi completamente autonoma, H24/7, su più corsie, fino alla promozione
finale e dopo. PRD proprio con confronto punto-per-punto col già-costruito:
[PRD-AUTONOMIA-FINANZIARIA-2026-08](PRD-AUTONOMIA-FINANZIARIA-2026-08.md). Le decisioni chiave del
proprietario: gate Testnet→Live resta un click umano (i 5 failsafe intatti); la «Queen Bee» è
codice deterministico col comitato LLM in consulenza; potere AI = scelta vincolata a menù;
H24 sul PC attuale con watchdog. Del PDF si scarta Hurst-come-pilastro (10 no del
direzionale-tecnico) e si tengono i due buchi veri: orchestratore di flotta e budget AI.

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| AF0 | **Flotta da 3 a 8 corsie** (3..7 dormienti): `Trading:LaneCount=8` nei due host; `LaneCountCoherenceProbe` (monolite remoto, LogCritical+notifica sul disallineamento, MAI un allarme costruito sull'ignoranza); impronta auto-apply della pipeline FERMA a 3 (`AutoApplyLaneFootprint`: oltre schiera solo l'orchestratore) | **fatto (2026-08-02)** | ✅ L1: sonda (coerente/disallineata/ignoranza/valore illeggibile) + impronta applier (flotta 8→3, flotta 2→2); L3: `PromotionEvaluator` su 8 corsie di cui 5 mai avviate senza eccezioni né azioni; L2/L4 al deploy: zero scritture sulle dormienti in 24h, /trading e /ensemble a 8 corsie nel browser |
| AF1 | **Tracking token/costi + budget**: `usage` dei provider → `ILlmUsageSink` (provider = chi ha SERVITO, non quello attivo; il `path` fluisce via AsyncLocal dal guard), `Llm:Budget` (TrackingEnabled=false default) con `SkippedBudgetExhausted` nel guard (breaker fermo, `forceProbe` non bypassa), righe aggregate `LlmUsageRecords` con ripresa dei totali al riavvio, pannello «Consumo e budget» in /admin/ai-supervisor | **fatto (2026-08-02)** | ✅ L1: parsing usage (anche col reasoning che svuota la risposta: i token si contano lo stesso), aritmetica su orologio finto, rollover mezzanotte/mese; L2: tracking spento ⇒ bit-identico; L3: Postgres — flush idempotente, riavvio che riprende il budget dal DB; L4 al deploy |
| AF2 | **Queen Bee** `Services/Fleet/`: core puro `Decide` (fuzz 20k) + `FleetStateReader` sola-lettura (candidati SOLO con trade/mese derivato dall'holdout; la durata mediana NON è derivabile a run-level — trade list non persistita — la misura il forward test) + worker 15′ con isteresi ritiri e journal `OrchestratorDecisions`; opera SOLO oltre l'impronta auto-apply (corsie ≥3); fascia grigia = solo proposta (F5); carry sorvegliato, non gestito. **Incrementi 1-2 fatti (DryRun)**; esecuzione (AF2b: start/stop reali + `targetLanes` nell'applier) dopo ~1 settimana di journal osservato | **DryRun fatto (2026-08-02)**; esecuzione aperta | ✅ fuzz 20k (mai impronta/Live/Testnet/quarantena/campagne/emergency, mai doppia assegnazione, mai candidato sotto frequenza); 100 tick sani ⇒ zero azioni; pannello journal in /admin/autonomy |
| AF3 | **Comitato a scelta vincolata** (`Services/Llm/Committee/`): voto parallelo multi-provider via resolver (semantica opposta al failover), contratto JSON severo (fuori menù/JSON rotto/timeout = ASTENSIONE, mai errore), quorum `MinValidVotes=2`, parità ⇒ default deterministico, doppia validazione del verdetto contro il menù (anti prompt-injection dai dati di mercato), budget AF1 controllato PRIMA di ogni giro. Innesto SOLO sui pareggi di `Decide` (`FleetAssignmentMenu`): il worker può sostituire la scelta dentro il recinto, `Source`+`VotesJson` a journal. Scostamento dal PRD: NIENTE guard keyed dedicato — l'isolamento (nessun breaker condiviso) si ottiene senza breaker del comitato: un giro fallito = verdetto di default, e basta. Pannelli: /admin/ai-supervisor (sezione Committee) + interruttore `Fleet:UseCommittee` nella card flotta | **fatto (2026-08-02)** | ✅ proprietà L2 verificata: spazzatura al 100% ⇒ ≡ comitato spento (default, zero eccezioni); parse su fences markdown/scelte inventate/JSON rotto; parità⇒default; sotto-quorum⇒default; spento/budget esaurito⇒zero chiamate; menù senza default⇒ArgumentException |
| AF4a | **Retrocessione Live→Testnet** (sola direzione di sicurezza): fuzz aggiornato PRIMA del codice (invariante 3′ flag-off bit-identico + inv. 4-5: da Live solo Testnet, mai col dry-run), opzioni default-off + `DemoteLiveDryRun=true` (annuncia senza agire, `WouldDemoteLive` nel verdetto), whitelist del worker irrigidita con la modalità di PARTENZA, notifica Warning; `LanePromoter` invariato; pannello in /admin/autonomy | **fatto (2026-08-02)** | ✅ fuzz 2×20k; caso nominale a mano (degradata/dry-run/giovane/sana); worker: Live→Paper diretto e "promozioni da Live" rifiutati a LogError; L4 = dry-run in produzione |
| AF4b | **Guardie di flotta**: >`Fleet:MaxLanesWithoutExposureGuard` (3) corsie attive richiedono `CorrelatedExposureGuard` acceso — precondizione DENTRO `Decide`, blocco journalizzato col rimedio | **fatto (2026-08-02)** (vol targeting = attivazione via config al deploy) | ✅ test: guardia spenta + 3 attive ⇒ zero assegnazioni + blocco con "CorrelatedExposure" nel motivo |
| AF2c | **Criteri emersi dalla prima revisione MANUALE delle corsie** (2026-08-05, dettaglio nel PRD): (1) 🔴 **la regola di ritiro non può scattare su una corsia che non opera** — chiede `Sharpe<0` dopo 3 settimane **E ≥20 trade**, e chi produce zero trade non arriva mai a 20: la corsia 0 è rimasta 9,3 giorni a zero senza che nulla la toccasse. Serve un secondo criterio **per inedia**, sui trade attesi dall'holdout; (2) il conteggio va fatto sul **simbolo attuale**, non sulla corsia (le corsie hanno vite precedenti: la 0 aveva 159 trade storici, tutti su altri simboli); (3) **il timeframe 1d non produce un verdetto in tempo utile** (~2 trade/mese ⇒ 10 mesi per 20 trade) — non è un divieto, ma va dichiarato allo schieramento; (4) **diversificare i simboli** (3 corsie su 6 erano su DOT); (5) **dedup dei grigi riproposti** (lo stesso candidato proposto 7 volte in 2 giorni mentre era già schierato — task #12); (6) il pannello dei grigi **offre solo i run che la flotta ha proposto**, e il candidato migliore misurato (Supertrend ADA/USDT 4h, Sharpe 3,19 su 17 trade) non era schierabile; (7) le corsie 0-2 restano dell'impronta auto-apply, non assegnabili a mano | aperto — da incorporare in AF2b | ognuno con la sua prova sul campo del 2026-08-05 |
| AF4c | **Pesi ERC fra corsie, SOLO advisory** (riuso `Services/Portfolio`), `Fleet:CapitalWeights:Enabled=false` | aperto | L2: PnL bianco ⇒ ~equal-weight |
| AF5 | **Continuità H24**: heartbeat incrociato a DB (`HostHeartbeats`, una riga per host, monitor una-notifica-per-transizione, assenza=ignoranza≠guasto; pannello con salvataggio doppio guscio+motore via gRPC, sezione nel contratto `EngineConfigSections`), `scripts/watchdog.ps1` (Task Scheduler 5′, Telegram diretto, auto-riparazione del port-forward prima di gridare, `-Register`), `scripts/bringup.ps1` idempotente (Docker→socat→nodo→pod→port-forward→guscio, `-Register` al logon con ripiego automatico sulla cartella Esecuzione automatica). **Digest giornaliero fatto**: `DailyDigestWorker` (ora LOCALE, `Notifications:Digest` default OFF, pannello dentro Notifiche), sezioni raccolte ognuna in proprio try/catch, chiusura che dichiara l'assenza-come-allarme; anti-doppione in memoria (un doppione raro batte una tabella in più) | **fatto (2026-08-02)** — deploy 5.1-5.3 eseguito la sera stessa | ✅ L1: transizioni heartbeat + scheduling digest (prima/dopo l'ora, già-inviato, avvio tardivo); L3: upsert senza duplicati su Postgres; L4 deploy: watchdog registrato e VERIFICATO, bring-up al logon, il rimedio socat del bringup è servito DAL VIVO al primo deploy (IP del nodo kind cambiato). Fix post-deploy: i `-Register` ora verificano l'esito invece di rassicurare (bug trovato sul campo: Register-ScheduledTask non terminante + messaggio di successo) |

**Non-obiettivi**: auto-Live in ogni forma; LLM che scrive codice libero o tocca un parametro
senza gate (**reconsiderato in parte 2026-08-04** per la sola generazione di candidati dentro
l'imbuto esistente — vedi Filone G, item G3); Hurst come pilastro; validazione più permissiva (F5
resta a click umano); terzo host. Ordine: AF0 → AF1 → AF5(1-3) → AF2(DryRun) → AF4a →
AF2(esecuzione) → AF5(digest) → AF4b → AF3 → AF4c.

---

## Filone G — Benchmark Bitget/AI: confronto col settimo PDF esterno (2026-08-04, tredicesima roadmap)

Nato dal settimo PDF esterno («Architettura di un Bot di Trading AI per Altcoin: Guida allo
Sviluppo in C# su Bitget e all'Integrazione con Groq, Gemini e NVIDIA») e dalla richiesta del
proprietario di aggiornare/potenziare ProcioneMGR tenendo il documento come riferimento. Confronto
punto-per-punto completo nel [PRD-BENCHMARK-BITGET-AI-2026-08](PRD-BENCHMARK-BITGET-AI-2026-08.md).

Il documento è una **guida introduttiva generalista** (setup ambiente → Bitget → backtesting →
AI → produzione, come se si partisse dal foglio bianco): quasi tutto il suo piano a 5 fasi
corrisponde a lavoro chiuso da tempo e ampiamente superato. Nessuna riga di codice nuova nasce
dal confronto stesso — solo verdetti, in gran parte "già fatto" o "già respinto con un motivo".

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| G1 | **RAG-lite documentale per l'advisory** (BM25/TF-IDF C# puro, nessun embedding esterno nuovo; il testo recuperato è CONTESTO, mai istruzione, stessa doppia validazione anti-injection del comitato AF3) — utile perché il proprietario porta spesso PDF esterni che oggi solo Claude confronta manualmente | aperto, priorità bassa/opzionale, **non urgente** | L1 ranking su corpus noto; L2 iniezione malevola non altera la scelta vincolata; L3 Postgres reale; L4 citazione visibile nell'advisory dal vivo |
| G2 | Ottimizzazione multi-obiettivo (fronte di Pareto Sharpe/MaxDD, tipo NSGA-II) | **backlog, nessuna fase assegnata** — il gate DSR/PBO + risk parity ERC/Ledoit-Wolf ottengono già un effetto simile per altra via; nessun caso misurato in cui divergono | — |
| G3 | **AI come generatore di candidati aggiuntivo** (2026-08-04, regola reconsiderata su richiesta del proprietario): un provider AI propone candidati STRUTTURATI (JSON vincolato: solo componenti dal catalogo esistente — indicatori, filtri di regime, range di parametri già in `Discovery` — mai codice libero), rivalidati contro la whitelist (stesso principio anti-injection del comitato AF3). Il candidato entra nell'imbuto ESISTENTE di `CreativeDiscoveryStage`/`GeneticAlphaMiner`: selezione → holdout → DSR/PBO/CPCV → gemello sintetico → forward Paper, **zero scorciatoie, zero soglie diverse, zero corsia riservata** | aperto, priorità bassa — **aspettativa onesta dichiarata**: dato lo storico di 10 esiti negativi consecutivi control-validati sulla classe direzionale-tecnica dopo 445mila combinazioni sistematiche, la probabilità che l'AI trovi ciò che la ricerca sistematica non ha trovato è bassa; il valore vero atteso è restringere lo spazio (quale classe/regime esplorare), non inventare segnali | L1 nessun componente fuori whitelist supera il parsing; L2 AI spenta ⇒ pipeline bit-identica a oggi; L3 un candidato AI riceve DSR/PBO reali in un run; L4 verdetto scritto anche se negativo |
| G4 | **Post-mortem AI su operazioni fallite/negative** (2026-08-04, nuova regola; **estesa lo stesso giorno** su richiesta del proprietario): dopo un trade chiuso in perdita oltre soglia o il ritiro di una corsia, un provider AI scrive un'analisi testuale da dati oggettivi + una **classificazione a menù chiuso** (`RegimeAvverso`/`StopStretto`/`SegnaleDegradato`/`CostiDominanti`/`RumoreNormale`/`Inconcludente`, con `Inconcludente` come default deterministico su ogni fallimento, e le cause CALCOLABILI derivate dal codice senza interpellare l'AI). **In più**: la classificazione alimenta il `Context` della domanda al comitato AF3 quando quello decide su quella corsia/candidato — contesto in più DENTRO il menù esistente, mai un canale che lo bypassa, mai una scorciatoia che tocca un parametro da sola | **fatto (2026-08-05)**: `TradePostMortem` + migrazione `AddTradePostMortems` (additiva: una CREATE TABLE e due indici, zero ALTER), `PostMortemAnalyzer` puro per fatti e cause calcolabili, `PostMortemService` col menù chiuso, worker 30′, pannello «Perché le operazioni sono andate male» in /admin/ai-supervisor, contesto innestato sul `Context` della domanda al comitato | ✅ L1 22 test: causa aritmetica senza AI, menù che non offre all'AI ciò che il codice calcola, verdetto fuori menù ⇒ Inconcludente (anche una voce vera ma non offribile), riassunto deterministico a parità; ✅ L2 AI spenta ⇒ contesto vuoto e comitato identico a prima; ✅ L3 migrazione provata su Postgres 18 VERGINE prima del database vero, poi applicata (212 trade e 12,7M candele invariati); ✅ **L4 sull'app reale**: acceso senza AI, «Analizza adesso» → 5 operazioni vere analizzate, tutte `Inconcludente/default` — che è la risposta ONESTA senza AI e senza causa aritmetica, non un'invenzione. **Nota di design registrata**: con soglia 1% e fee 0,1% la causa «costi che hanno mangiato il lordo» resta dormiente (serve una perdita più piccola del round-turn) |
| G5 | **Bozza automatica del confronto col PDF esterno**: quando il proprietario porta un documento (sette finora), un provider AI produce la PRIMA STESURA della tabella «proposta del PDF → stato reale → verdetto» pescando dal corpus di G1; la stesura è una BOZZA da rivedere insieme, mai un verdetto pubblicato da sola | aperto, dipende da G1 | L1 la bozza cita solo documenti realmente nel corpus (nessuna fonte inventata); L2 AI spenta ⇒ nessuna bozza, il confronto resta manuale come oggi; L4 bozza generata su un PDF vero e confrontata con quella scritta a mano |
| G6 | **Spiegazione dei candidati bocciati dai gate**. Costruito in DUE strati, ed è il punto: (a) `RejectionDigestBuilder` **deterministico e senza AI** — classifica ogni bocciatura leggendo il `RejectReason` del motore, conta per causa, riporta i migliori scartati coi numeri veri e la fascia grigia (filtro condiviso `FleetStateReader.IsGrey`, non uno nuovo); (b) `RejectionNarrator` che aggiunge SOLO la prosa (path guard `explain`, budget AF1, artifact Kind proprio `LlmRejectionExplain`). La prosa è mostrata ACCANTO ai numeri veri, mai al loro posto: un numero sbagliato si vede a occhio. Note legate ai candidati per CHIAVE, chiavi inventate scartate e **contate in pagina**. Pannello in /admin/ai-supervisor con interruttore, tetto 1-20 e pulsante «Spiega con l'AI» per singolo run | **fatto (2026-08-05)** | ✅ L1 44 test: classificatore sui messaggi VERI del motore (fallisce se il motore li cambia — meglio che contarli sotto l'etichetta sbagliata), raggruppamento, topN che limita il dettaglio ma non i conteggi, ordine deterministico; ✅ L2 «un modello che inventa TUTTO non produce nemmeno una nota», e ogni esito non-Ok del guard ⇒ nessuna prosa, zero eccezioni, digest intatto; ✅ L3 11 test su Postgres vero: digest senza mai sfiorare l'AI, idempotenza, force che sostituisce, artifact corrotto ⇒ vuoto dichiarato, Kind nuovo che non disturba advisory e verdetti; ✅ **L4 sull'app reale**: pannello vivo sui run del 05/08 (144 candidati classificati «119 Sharpe sotto soglia / 21 pochi trade / 4 DSR», 21 in fascia grigia, i numeri veri accanto a ogni riga) **col layer AI che NON ha risposto** — cioè la prova sul campo che il riassunto vive senza AI; salvataggio verificato (tetto 5→6→5, hot-reload senza riavvio, e le altre 23 chiavi della sezione `Llm` intatte, sottosezione `Budget` compresa). ⚠️ Il ramo di SUCCESSO della prosa non è dimostrato dal vivo: Nvidia è andata in timeout due volte (vedi il difetto del failover qui sotto); coperto da test unità+integrazione |
| G9 | **Narrativa di sintesi nel digest giornaliero**: un paragrafo in italiano SOPRA i dati strutturati che il `DailyDigestWorker` già invia (`DigestNarrator`, path guard `digest`, tetto 600 caratteri col taglio dichiarato). Interruttore in /admin/autonomy accanto all'ora del digest | **fatto (2026-08-05)** | ✅ L1/L2 17 test, fra cui la **proprietà regina**: senza narrativa il messaggio è IDENTICO carattere per carattere a quello di prima della funzione (non «equivalente»: identico), la chiusura col dead-man's-switch resta l'ultima riga, e ogni fallimento dell'AI ⇒ digest invariato senza segnalare nulla (l'assenza della sintesi non è un guasto); ✅ L4 interruttore «Sintesi AI in cima» visibile e spento in /admin/autonomy accanto all'ora del digest |
| G7 | **Parere del comitato accanto al click Live**: al momento della promozione Testnet→Live (che **resta il click umano intoccabile**), il comitato AF3 mostra lì un parere dentro menù vincolato (promuovere ora / aspettare / non promuovere + motivazione). **Mai bloccante, mai automatico, non sposta il gate di un millimetro**: è informazione in più accanto al pulsante | aperto, priorità bassa — **il punto più delicato del sistema**, da trattare con lo standard più severo | L1 il parere non può in nessun caso abilitare/disabilitare il pulsante (test che lo dimostra sullo stato del DOM); L2 comitato spento/spazzatura ⇒ pulsante e flusso identici a oggi; L3 fuzz sulla macchina a stati promozioni invariata; L4 il click Live resta umano e funzionante col parere presente e col parere assente |
| G8 | **Domande e risposte sui documenti propri**: estensione naturale di G1 una volta costruito — chiedere «cosa abbiamo scoperto sul pairs trading a 1d?» e ottenere una risposta ANCORATA ai report veri, con citazione del documento, invece di scavare a mano | aperto, dipende da G1 | L1 ogni risposta cita il documento da cui viene (risposta senza fonte = rifiutata); L2 domanda su cosa non è nel corpus ⇒ «non lo so», mai un'invenzione; L4 tre domande vere di cui una fuori corpus |

---

## Filone H — Prestazioni e risorse (2026-08-05, quattordicesima roadmap)

Dalla richiesta del proprietario di ottimizzare processi e uso di risorse «senza peggiorare, senza
causare danni». Misure e dettaglio in [PRD-PRESTAZIONI-2026-08](PRD-PRESTAZIONI-2026-08.md).

**La scoperta che cambia la diagnosi**: il guscio consuma **111 MB e l'1,2% di un core** — è già
magro. La macchina soffoca (317 MB liberi su 7.835) per **il cluster**: il nodo kind sta a 2,35 GiB
con `kube-scheduler` a **418 riavvii** e `kube-controller-manager` a **413**, perché non
raggiungono l'API server entro il timeout del lease e escono. Non è un guasto: è fame di risorse
che si auto-alimenta. Sopra ci girano 7 pod ArgoCD per un cluster locale mono-nodo.

| # | Cosa | Stato | Numeri |
|---|---|---|---|
| H1 | `random_page_cost` 4 → 1,1 sul database (il `4` è il valore da disco a piatti; qui c'è un **NVMe**) | **fatto** | stessa query, connessione nuova: **Bitmap Heap Scan 92,3 ms → Index Scan 25,4 ms**; su un'altra serie il piano diventa *Index Only Scan*. Il guadagno durevole è il PIANO, non il millisecondo |
| H6 | Log SQL e HTTP da `Information` a `Warning` | **fatto** | prima 37 righe/s di SQL a riposo e 4 righe per richiesta HTTP: una lettura dei log rendeva 147.000 caratteri. **Onestà**: il guadagno di CPU è piccolo (l'app era all'1,2%), il valore è l'osservabilità |
| H2+H3 | ArgoCD a 0 (7 pod) + pod morto rimosso, poi `.wslconfig` con **`autoMemoryReclaim=gradual`** | **fatti** | nodo kind **2,306 → 1,66 GiB (−28%)**, **CPU a vuoto 57% → 26%**, riavvii del control plane FERMI. **La lezione sta nell'ordine**: dopo il solo H2 la RAM libera dell'host era PEGGIORATA (435→291 MB) — la memoria era stata liberata dentro la VM e lì era rimasta. Senza H3, H2 non bastava |
| H4 | Memoria di Postgres | **fatto a metà, di proposito** | `effective_cache_size` 4 GB → 1536 MB e `work_mem` 4 → 16 MB via `ALTER DATABASE`. **`shared_buffers` NON toccato**: il cache hit al 70% era il CUMULATO storico; sul carico vivo è **99,95%** (15 blocchi da disco contro 32.215 da cache in 75s). Alzarlo avrebbe richiesto elevazione e riavvio del servizio per un guadagno misurato pari a zero |

**Errore mio da ricordare**: un commento messo dentro `Logging:LogLevel` impedisce l'avvio — lì
ogni chiave dev'essere un livello valido. Va accanto a `LogLevel`, dentro `Logging`, che è dove lo
tiene già `appsettings.Development.json`.

---

### Migrate-on-startup (2026-08-05) — `MigrateAsync` esiste finalmente

Fino a quel giorno lo schema si applicava SOLO a mano (`dotnet ef database update`): il commento in
`DbInitializer` lo dichiarava come scelta («l'app non referenzia l'assembly delle migrazioni, quindi
niente migrate-on-startup»). Funziona finché qualcuno si ricorda — e la sera prima non se n'era
ricordato nessuno, con la tabella nuova assente e l'errore che sarebbe uscito a metà giornata.

`DatabaseMigrator` (`Database:AutoMigrate`, default **true**; `LockTimeoutSeconds` 120) applica le
migrazioni pendenti prima dei ruoli. Tre cose non ovvie, tutte scoperte facendolo:

1. **Il ciclo di progetti non era il vero ostacolo.** EF risolve l'assembly delle migrazioni per
   NOME: basta che la DLL sia accanto all'eseguibile. Un target `CopyMigrationsToApp` nel progetto
   delle migrazioni ce la porta, nella direzione che NON crea il ciclo.
2. **Copiare la DLL non basta.** Un'app framework-dependent risolve gli assembly dal proprio
   `deps.json`, non dai file presenti nella cartella: la DLL c'era, mezzo mega, e `Assembly.Load`
   continuava a dire «file non trovato». Serve un resolver esplicito su
   `AssemblyLoadContext.Default.Resolving`, ristretto a QUEL nome.
3. **Il lock serve davvero.** Monolite e servizi in cluster condividono il database e possono
   partire insieme: un advisory lock di PostgreSQL li serializza, e chi arriva secondo rilegge
   dentro il lock e non trova nulla da fare.

Non fallisce mai l'avvio: assembly assente (gli host satelliti non lo ricevono), lock occupato o
interruttore spento producono una riga di log e la prosecuzione. Il migrate-on-deploy resta
possibile con `Database:AutoMigrate=false`. 4 test; L4: «Schema del database già allineato:
nessuna migrazione pendente» all'avvio dell'app vera.

### Incongruenze della pagina /trading trovate col proprietario (2026-08-05)

Segnalate da lui («continuo a riscontrare qualche incongruenza»), verificate sui dati veri del
database, non a occhio. **La causa principale non era nella pagina**:

1. **🔴 Port-forward STANTIO — la causa vera.** Il container del pod di trading era ripartito 8 ore
   prima (`RESTARTS 2`). `kubectl port-forward` restava in ascolto sulla 18092, ma il tunnel era
   morto: l'handshake HTTP/2 falliva e il guscio riceveva `Unavailable` a ogni chiamata. La pagina
   mostrava **zero corsie e «Avvia trading»** mentre il motore in cluster stava operando.
   **`ensure-trading-portforward.ps1` diceva «già attivo»**: confrontava il NOME del pod, che un
   restart del container non cambia — il controllo era cieco proprio al caso più frequente.
   **Corretto**: l'identità del tunnel è ora la coppia NOME + CONTEGGIO RESTART. Provato sul
   cluster vero forzando il marcatore: «STANTIO (serviva …#99, ora c'è …#2) — lo ricreo». Corretto
   anche il profilo `procione-reale` di launch.json, che a differenza di `procione-main` non
   apriva il tunnel e quindi partiva ogni volta con /trading cieca.
   *Osservazione operativa*: il container del motore esce con **codice 0 «Completed»** e viene
   riavviato da Docker (3 restart in 2 giorni) — non è un crash dell'applicazione, ma è il motivo
   per cui questo caso si presenta spesso.
2. **Corsia svuotata descritta in due modi nella STESSA schermata**: la scheda diceva «2 non
   configurata», la tabella promozioni sotto «2 XLM/USDT · 9gg». `ClearLaneAsync` azzera la
   configurazione ma non lo stato del motore, che conserva l'ultimo simbolo girato. **Corretto**:
   la tabella legge ora la configurazione (stessa fonte delle schede) e mostra «non configurata»
   con le metriche a «—», perché quei numeri appartengono a un test che non esiste più.
3. **`AVAILABLE` maggiore di `TOTAL CAPITAL`** sulla corsia 4 (10.799,20 contro 10.000,00): non era
   un dato corrotto ma una posizione SHORT che accredita a cassa il ricavato — aritmetica giusta
   con etichette che non la spiegavano, e per giunta mancava il numero che conta. **Corretto**: le
   carte ora sono «Equity ora» (per prima), «Capitale iniziale», «Cassa» (che dichiara il perché
   quando supera il capitale) e «Impegnato».
4. **`PNL (TEST) 0,00` con `MAX DD 0,74%` e zero trade**: il PnL del test conta solo le operazioni
   CHIUSE, il drawdown segue anche l'equity delle posizioni APERTE. **Corretto**: «PnL realizzato
   (test)», «Operazioni chiuse (test)» con quante ne restano aperte, e il drawdown che dichiara
   quando viene da una posizione ancora aperta.
5. **`PF 999,00`** nelle attese di holdout: è il sentinella «nessuna operazione perdente», non un
   rapporto 999:1. **Corretto**: si scrive a parole. Stessa cura per `TP 0,00% / TSL 0,00%`, che
   non sono protezioni «a zero» ma protezioni ASSENTI → «non impostato».
6. **Attese di holdout vuote** sulle corsie pre-flotta 0-2. **Corretto**: «non registrate (corsia
   configurata a mano)» invece di tre trattini muti — quelle corsie non hanno un holdout con cui
   confrontare il forward test, e dirlo è il punto.
7. **`/metrics` tutta a zeri** (item F7): la pagina legge i contatori del processo in cui gira, e
   il motore vive altrove. **Corretto il fraintendimento**, non ancora il collegamento: un banner
   dichiara che quei contatori resteranno a zero per costruzione finché il motore è remoto, e
   rimanda a /trading. Il collegamento vero resta F7.

### Difetto pre-esistente trovato dal vivo col collaudo di G6 (2026-08-05) — CORRETTO

**Il rimedio è stato applicato** su decisione del proprietario: `Llm:PerProviderTimeoutSeconds`
(default 25s, 0 = spento) dà a ogni anello della catena il proprio budget di tempo. Scaduto quello,
il provider è considerato appeso e si passa al successivo; se invece è il token ESTERNO a essere
cancellato (shutdown vero, o budget complessivo esaurito) non si prova nessun altro — comportamento
storico invariato. 13 test scritti PRIMA del codice, compresi «cancellazione esterna ⇒ niente
failover» e «budget a 0 ⇒ comportamento storico». Il testo qui sotto resta come racconto del
difetto e di come si è manifestato.

### Come si era manifestato

Provando il pulsante «Spiega con l'AI» sull'app reale, la chiamata a
`integrate.api.nvidia.com/v1/chat/completions` è andata in **timeout** due volte di fila (60s), e
**il failover non è mai partito** — pur avendo Groq, Gemini, HuggingFace e Anthropic con chiave
valida e raggiungibili (i cinque `GET /models` avevano risposto 200 pochi secondi prima).

**Il meccanismo, nei dettagli.** `LlmCallGuard` crea un token *linked* (token del chiamante +
timeout) e passa QUELLO al client; il guard poi distingue correttamente le due cause
(`ct.IsCancellationRequested` = shutdown vero ⇒ non tocca il breaker;
`timeoutCts.IsCancellationRequested` ⇒ «timeout», ritentabile). Ma un livello più sotto,
`DelegatingLlmClient.CompleteAsync` vede **solo il token linked**, che risulta cancellato in
ENTRAMBI i casi; il suo filtro `catch (OperationCanceledException) when (ct.IsCancellationRequested)`
scatta anche sul timeout e **rilancia senza provare il provider successivo**.

**Perché conta**: un provider che *si appende* è il modo più comune in cui un provider gratuito
smette di funzionare — ed è esattamente il caso per cui la catena di failover esiste. Oggi quel
caso la scavalca. Riguarda TUTTI i percorsi AI (advisory, sentiment, comitato, e le funzioni
nuove), non solo G6.

**Nota operativa di quella notte**: il breaker del layer era rimasto a **2 errori consecutivi su 3**
— la soglia non fu raggiunta di proposito (al terzo si sarebbe aperto per 30 minuti anche per
l'advisory della piattaforma). Si richiude da solo al primo successo.

### La regola sull'AI riconsiderata (2026-08-04)

Il proprietario ha chiesto di riconsiderare tre regole prese in precedenza. Risposta data PRIMA
del codice, con opinione esplicita, poi consenso registrato:

1. **AI che genera strategie** → **accettato in forma scoped** (G3 sopra): solo come fonte di
   candidati che passa dagli stessi gate di tutto il resto, mai un canale privilegiato.
2. **AI che entra nel meccanismo e nel percorso d'esecuzione** → **RIFIUTATO, linea rossa
   confermata**. È il principio su cui è costruita la Queen Bee (deterministica, fuzzata 20k
   volte) e il comitato (scelta vincolata a menù, fallback deterministico su ogni fallimento,
   proprietà "provider spazzatura al 100% ⇒ comitato spento"). L'AI ha già causato incidenti
   operativi restando solo advisory (credito Anthropic esaurito in silenzio per settimane, bug SDK
   che abbatteva un intero tick): nel percorso reale d'ordine lo stesso guasto diventerebbe un
   ordine sbagliato o uno stop-loss mancato. Resta anche il motivo epistemico: non si può fuzzare
   lo spazio di risposta di un LLM come si fa col codice deterministico, e tutta la piattaforma
   vale per essere dimostrabile su ogni stato raggiungibile. **Via sanzionata per dare più peso
   all'AI**: ampliare i tipi di decisione arbitrabili dal comitato (sempre a menù vincolato,
   sempre fallback deterministico, mai su Live, mai nel loop caldo di piazzamento ordini) — nessun
   item concreto richiesto oggi, resta un principio disponibile per fasi future.
3. **AI che studia le operazioni fallite o negative** → **accettato** (G4 sopra); **esteso lo
   stesso giorno** su richiesta del proprietario: oltre a journal/digest, l'esito strutturato
   alimenta anche il comitato a scelta vincolata (AF3) come contesto — mai una scorciatoia che
   tocca un parametro operativo DA SOLA, sempre dentro il menù esistente.

**Non-obiettivi** (motivati uno per uno nel PRD): `JK.Bitget.Net`/ExchangeSharp; arbitraggio
cross-exchange (manca un secondo venue con leva, MiCA Bitget-only); market making immediato
(dipende da D2 + server, già rimandato); LLM che scrive codice libero o parametri fuori gate
(G3 è l'unica eccezione, scoped); **LLM nel percorso di esecuzione — linea rossa confermata
2026-08-04** (violerebbe "l'AI non entra mai nel meccanismo"); NVIDIA NIM self-hosted (manca GPU
dedicata); multi-agente generativo stile "trading firm" (la Queen Bee deterministica + comitato a
menù è già la versione più prudente della stessa idea); validazione più permissiva.

**Priorità reale**: nessun item di questo filone è bloccante per Filone F (F5→F13) e Filone AF
(AF2 esecuzione, AF4c), che restano il lavoro di sostanza. Ordine interno ai G, per rischio
crescente e costo crescente:

**G6** (rischio nullo, self-contained, valore immediato) → **G4** (post-mortem + contesto al
comitato, riusa advisory+comitato) → **G9** (polish sul digest, piccolo) → **G1** (RAG-lite, è la
fondazione di G5 e G8) → **G5**+**G8** (entrambi consumatori di G1) → **G3** (bassa aspettativa
dichiarata, meglio dopo G1) → **G7** (il più delicato: si tocca solo quando tutto il resto è
stabile) → **G2** backlog senza scadenza.

**Invariante comune a tutti i G**: ognuno è default-off, ognuno ha la sua configurazione nella UI
(mai un flag che vive solo in appsettings), e ognuno spento deve lasciare il comportamento
bit-identico a prima — verificato al livello 2, non promesso.

---

## Filone R — Archivio della ricerca: leggere i 6.554 candidati che già abbiamo (2026-08-06)

Nasce da una domanda del proprietario: *«penso abbia senso salvare anche le varie campagne con i
vari candidati e le varie proposte, per fare una ricerca più approfondita»*. **La premessa è
sbagliata in modo utile: è già tutto salvato.** Misurato sul database vero:

| Cosa | Quanto | Da quando |
|---|---|---|
| `ValidatedCandidates` (candidato per candidato, **24 metriche** + parametri + motivo di scarto) | 84 run, 5,8 MB, **6.554 candidati** | 2026-07-02 |
| `FactorIc`, `RegimeProfile`, `PairScreen`, `FeatureImportance` | 86 run ciascuno, ~9,4 MB | 2026-07-02 |
| `EnsembleProposal` (le proposte) | **9, l'ultima il 2026-07-09** | — |
| `LlmAdvisory` | 77 su 75 run | 2026-07-08 |
| Campagne (`VettingCampaigns`) con rotazione, esito e run collegati | 1 attiva, `WaitingForTrigger` | — |

Le 9 sole proposte **non sono un guasto**: sono la realtà. Da metà luglio nessun run produce un
ensemble perché nessun candidato passa.

**Il buco vero non è la scrittura: è la lettura.** Ogni run è un blob JSON separato, quindi non
esiste nessuna domanda che attraversi i run — ed è esattamente la domanda che serve per
«analizzare le strategie, confrontare i candidati, migliorare la ricerca». Tre query scritte a
mano il 2026-08-06 (secondi di esecuzione, nessuna riga di codice nuova) tirano fuori questo:

**Il fatto più duro**: dal 13 luglio, **66 run e 5.131 candidati, ZERO sopravvissuti**. È
l'undicesimo «no» del direzionale-tecnico, ma stavolta con la scala visibile in una riga.

**Il fatto più utile**: fra i bocciati, **702 hanno Sharpe fuori campione MEDIO POSITIVO** —
532 fermati da «troppi pochi trade» (Sharpe medio **+1,10**) e 170 dal DSR (**+1,07**). Non sono
stati bocciati perché perdono: sono stati bocciati perché la finestra è corta per la loro
frequenza, o perché i tentativi erano troppi rispetto ai dati. Contro i 5.814 che perdono davvero,
con Sharpe medio −1,86. È la stessa diagnosi del power check MinTRL (Filone F), ma qui è
**quantificata**: 702 candidati.

**Il fatto per la Queen Bee**: il tasso di passaggio per famiglia è misurabile e disomogeneo —
`Composite` 12 su 2.212 (0,5%), `PriceSmaCross` 7 su 46 (**15%**), `Supertrend` 6 su 406,
`EventTrigger` 0 su 1.415, `RegimeConditional` 0 su 1.096, `Ml` 0 su 19 con Sharpe medio **−9,66**.
Oggi la caccia spende lo stesso sforzo su famiglie con rese di due ordini di grandezza diverse.

| # | Cosa | Perché | Costo |
|---|---|---|---|
| R1 | Vista di lettura trasversale sui candidati archiviati (motivi di scarto aggregati, tasso di passaggio per famiglia/simbolo/timeframe, andamento nel tempo) — pagina `/research` | Rende interrogabile ciò che già c'è. Le query esistono, provate a mano | piccolo, rischio nullo (sola lettura) |
| R2 | Indicizzare i candidati in righe invece che in blob JSON (tabella derivata, ricostruibile dagli artefatti — nessun dato nuovo da raccogliere) | Oggi ogni domanda costa una scansione con `jsonb_array_elements` su 5,8 MB; con l'archivio che cresce non regge | medio |
| R3 | Portare il tasso di passaggio per famiglia dentro la scelta della prossima caccia (`CampaignPlanner`) | La rotazione oggi è cieca alla resa storica delle famiglie che ruota | medio, dietro gate |
| R4 | Riesaminare i 702 «bocciati per potenza statistica» con la finestra giusta, come esperimento dichiarato | Sono l'unico bacino di candidati con Sharpe medio positivo mai prodotto dalla piattaforma | medio, è ricerca |

**Onestà su R4**: non è una scorciatoia al profitto. Ripescare candidati già bocciati è
esattamente il gesto che fabbrica falsa significatività se fatto male (lezione del 2026-07-20,
445k combinazioni → 0). Va fatto come esperimento con la sua ipotesi scritta prima, e col controllo
sul rumore del [Standard di verifica](STANDARD-VERIFICA.md) — altrimenti non si fa.

> **[2026-08-14] R1/R2 ripresi ed estesi, più un secondo filo nuovo — ED ESEGUITI (Fasi 0-4).**
> Il proprietario ha richiesto (in chat) esattamente ciò che R1/R2 avevano già diagnosticato
> l'8/6 e che non era mai stato costruito, più un'idea nuova: comporre più strategie di fascia
> grigia sulla stessa coppia in un'unica corsia. Misurato che il pezzo di composizione esiste
> già (`EnsembleAssemblyStage`, Pipeline stage 11, HRP di default) ma era fermo dal 2026-07-09
> perché ammetteva solo candidati "sopravvissuti pieni" — zero da un mese. Piano ED esecuzione
> in [PRD-MEMORIA-CACCIA-COMPOSIZIONE-2026-08](PRD-MEMORIA-CACCIA-COMPOSIZIONE-2026-08.md):
> **R1+R2+R5 FATTI** (pagina `/research` su indice a righe derivato `ResearchCandidates`,
> giudice unico `GreyZone.IsGrey` promosso da FleetStateReader), **T1-T5 FATTI**
> (`includeGreyZone` default-off sull'assemblaggio, correlazione dell'HRP dichiarata invece che
> scartata, quinta fonte gamba in `/ensemble` col badge Grigia, semantica lorda dell'esposizione
> fissata dai test). Review avversaria multi-lente: 15 finding confermati e corretti (1 HIGH),
> 3 refutati. Nessun automatismo nuovo verso Live; R3/R4 del filone restano aperti.

---

## Filone S — Distribuzione sul server: immagini private e build in casa (2026-08-06)

Nasce da un fatto e da una decisione del proprietario. Il fatto: rendendo **privata** la repo il
2026-08-06, il cluster ha smesso di poter scaricare immagini nuove — nessun workload ha mai avuto
un `imagePullSecrets`, perché finché le immagini erano pubbliche non serviva. Il proprietario ha
rimesso pubbliche le immagini per sbloccare, dichiarando che **è un ripiego che non gli piace** e
che il progetto sarà presto trasferito su un server dove dovrà girare per intero.

**Stato attuale, misurato**: cinque namespace tirano da `ghcr.io/markcapitanelli` — `trading`,
`ui`, `ml`, `ingestion`, più i Job di `pipeline` e `supervisor`. Il pacchetto GHCR è pubblico
anche ora che la repo è privata: la visibilità dei pacchetti è indipendente da quella del
repository, quindi chiunque scarica ancora i binari della piattaforma.

**La domanda che decide la forma**: il server deve funzionare *senza* GitHub, o gli basta
raggiungerlo al momento del deploy? Finché non c'è risposta, S2 resta indecidibile.

| # | Cosa | Perché | Note |
|---|---|---|---|
| S1 | `imagePullSecrets` da PAT fine-grained con **solo** `read:packages`, attaccato al ServiceAccount `default` di ogni namespace | Copre Deployment, Job e CronJob **senza toccare un manifest**. È il pezzo che manca per poter chiudere i pacchetti | piccolo; script nuovo accanto ai `k8s-*-secret.ps1` esistenti, stessa convenzione di nomi |
| S2 | Chiudere i pacchetti GHCR | Oggi i binari sono scaricabili da chiunque | **dopo S1, mai prima**: invertire l'ordine ferma gli aggiornamenti del cluster |
| S3 | Runner **self-hosted** sul server | Con la repo privata i minuti Actions si pagano e il workflow costruisce **6 immagini a ogni push**. Toglie il costo, accorcia build→deploy, e rende il deploy indipendente dai runner di GitHub — che il 2026-08-06 sono stati giù un'intera serata bloccando questo stesso lavoro | medio. **Cautela**: girerebbe sulla macchina del motore di trading, quindi limiti di risorse obbligatori e mai codice di PR non fidate (con repo privata a un autore la condizione è già soddisfatta) |
| S4 | Registro locale sul server | Solo se l'autonomia **offline** è un requisito vero | Il pin sul digest continua a valere, ma si perde il legame diretto immagine↔commit su GitHub, che è la garanzia a cui `infra/k8s/trading/kustomization.yaml` tiene di più. Non farlo per abitudine |

**Raccomandazione**: S1 + S2 subito (chiudono il buco vero), S3 quando si passa al server. S4 solo
se la risposta alla domanda qui sopra è «senza GitHub».

> **[2026-08-11] Tavola S superata dalla Fase 4 del Risanamento**: il cluster non fa PIÙ pull da
> ghcr (build locali + `imagePullPolicy: Never` + kustomization pinnati a `local-<sha>`). S2
> (chiudere i pacchetti) non blocca più nulla e si può fare quando si vuole; S1 serve solo a chi
> tornasse al flusso CI; S4 è risolto senza registro (import diretto nel containerd del nodo).

---

## Ondata Risanamento (2026-08-08 → 2026-08-11) — CHIUSA

Quattordicesima ondata (la numerazione segue il PRD), nata dall'audit in cinque passaggi
(`docs/audit/` 00-28) e dal mandato del
proprietario: bug, sincronizzazione, config↔UI, indipendenza da GitHub, portabilità. Registro
completo in [PRD-RISANAMENTO-2026-08](PRD-RISANAMENTO-2026-08.md); verifiche finali in
[REPORT-RISANAMENTO-CHIUSURA-2026-08](REPORT-RISANAMENTO-CHIUSURA-2026-08.md).

| Fase | Esito |
|---|---|
| 0 Segreti | rotazione COMPLETA dei tre segreti (master key via keyring multi-chiave 7/7, gRPC, password PG); guardia CI sui file segreti |
| 1 Bug di merito | 7 fix: conteggio DSR sulle prove vere (D-01), esposizione futures nozionale, default B3, funding storico nel backtest, range obbligati, stoppini specchiati, seed dichiarati |
| 2 Sincronizzazione | ISymbolCatalog, DI morte rimosse, gate anti-ridondanza raggiungibile (2.6), JumpModel dietro flag col contratto C1 (2.7), optimizer per nome (2.8), deriva sui fattori del Champion (2.9) |
| 3 Config↔UI | **mandato: tutto amministrabile da UI** — DeliberatelyNotExposed SVUOTATA, card Topologia, misure accanto agli interruttori, N del gate visibile |
| 4 No-GitHub | build locali + import in kind + `imagePullPolicy: Never`; il motore in-cluster aggiornato con le Fasi 1-3; prova senza pull |
| 5 Compose | `docker compose up -d` autosufficiente su macchina pulita; **bug grave chiuso**: migrate-on-startup rotto in silenzio da versioni EF disallineate (guardia + test guardiano) |
| 6 Chiusura | suite 2.474/2.474; carry RIMISURATO e riprodotto (netto 5,5-11,9%/anno, tabella nel report); serie funding dal 2019 ripristinata (+35.006 punti) dopo la SECONDA perdita silenziosa — chip per il guardiano di profondità; doc meccanici 21-27 rigenerati (+426/−150 = changelog) |

**Lezione dell'ondata da portare avanti**: tre difetti gravi trovati non dall'analisi ma dal
PRIMO USO REALE di un percorso nuovo (DB vergine → migrazioni mute; rimisura → serie sparita;
compose → pipe PowerShell che corrompe i tar). I guardiani nuovi trasformano ciascuno in suite
rossa alla prossima occorrenza.

> **[2026-08-13] Chip del guardiano di profondità ESEGUITO.** `SentimentHeritageGuardWorker`
> misura ogni 6h la profondità delle tre serie-patrimonio esenti dalla purge (funding per simbolo,
> Fear & Greed, liquidazioni) contro soglie dichiarate in `Sentiment:HeritageGuard` (pannello in
> `/admin/autonomy`, regole server-side). Su violazione: log Error a ogni giro, notifica Critical
> aggregata sulla transizione (pattern `SeriesFreshnessWatchWorker`), tabella con «Controlla ora»
> in `/sentiment` e alert rosso in Home. Test: fotografia+opzioni a unità, 8 casi su Postgres
> (troncatura tipo incidente 2026-08-11, serie assente, conteggio, riarmo dopo ripristino,
> aggregazione, filtri Source+Metric+Symbol); su serie profonde il guardiano tace (controllo sul
> rumore). **Verifica a browser eseguita la sera stessa (profilo procione-reale, DB vero) — e il
> primo giro reale ha trovato due cose**: (1) l'àncora di default 2020-01-01 marcava VIOLATE
> quattro serie funding COMPLETE, perché una serie non può precedere il listing del suo mercato
> (XRP 2020-01, BNB 2020-02, DOGE 2020-07, SOL 2020-09) → default corretto a **2020-10-01**;
> (2) violazione VERA: **l'accumulo liquidazioni è a ZERO punti totali** — dalle postazioni EEA lo
> stream futures è muto (blocco MiCA, già noto dal 2026-07-24) e l'accumulo non è mai partito.
> L'allarme resta acceso di default (è la verità); per le postazioni bloccate esiste ora
> l'interruttore «Sorveglia liquidazioni» che declassa la riga a «NON SORVEGLIATA» dichiarata —
> mai un OK finto. Home verificata con l'alert rosso in entrambe le configurazioni.

---

## Filone I — Integrazione onesta dei sottosistemi accesi (2026-08-18, sedicesima ondata)

Nasce dalla richiesta del proprietario di validare e integrare **sei componenti sviluppate e tenute
scollegate o spente per configurazione**. Dettaglio, verifiche riga per riga e accettazione nel
[PRD-INTEGRAZIONE-SOTTOSISTEMI-2026-08](PRD-INTEGRAZIONE-SOTTOSISTEMI-2026-08.md).

**La validazione ha ribaltato la premessa su tre punti su sei, ed è il fatto da cui discende tutto il
resto: quei tre non sono spenti, sono accesi e non producono nulla.** Il `false` che si ricorda vive
in `appsettings.json.example`; il file che l'app carica davvero (repo principale, non tracciato) dice
`Campaign:Enabled=true`, `Fleet:Enabled=true` (DryRun) con `UseCommittee=true`, `Committee:Enabled=true`.
Misurato sul database vero: il comitato è armato da sedici giorni e **non ha mai votato** — zero righe
con `Path='committee'`, tutte e 89 le decisioni della flotta con `VotesJson` vuoto. Non è un guasto:
arbitra i pareggi di `Decide`, e la flotta non produce assegnazioni perché la coda è sempre vuota. A
monte c'è l'inedia — le corsie di flotta 3-7 hanno chiuso **da uno a sei trade ciascuna sul simbolo attuale (5, 1, 5, 6, 3) in 6-16 giorni**,
contro `RetireMinTrades=20`: non saranno mai ritirabili, quindi non si libera mai una corsia.

E c'è una **trappola armata**: la sezione `Drift` è *assente* dal file vivo, quindi accendendo la
spunta dal pannello entrerebbe in vigore il default del POCO `RetireChampionOnAlert=true`. Il ciclo
chiuso partirebbe insieme al monitor. Oggi è inerte solo perché non esiste alcun Champion: è una
salvezza per coincidenza, non per progetto.

**Quindi il debito di questi sei non è di collegamento** — la classe di difetto «regola 6» qui non
c'è: sono tutti e sei del guscio, e in tutti e sei manopola e consumatore coincidono. **È di capacità
di dire di no e di dichiarazione di copertura.**

### Le sei descrizioni contro le sei verifiche

| Punto | Come era descritto | Che cosa dice il codice del 2026-08-18 |
|---|---|---|
| 1 Concept drift | «scritto ma non gira; attivarlo mostra l'allarme in Home» | il worker **gira** (hosted service incondizionato, ciclo a vuoto a interruttore spento) e ha già pannello, persistenza, metrica e «Esegui ora». In Home **non c'è nulla**: `Home.razor` legge il monitor **omonimo ma diverso** (deriva dell'IC dei fattori, filone D2). Il ciclo chiuso non è «robusto ma inattivo»: è **senza soggetto** (zero Champion) |
| 2 Controlli a caldo | «PerformanceControl e LeverageAdvisor solo in `/backtest`» | esatto — ma delle due modalità **quella nominata** (`ApplyEquityMovingAverageControl`) non ha **alcun** chiamante fuori dai test; il gemello a caldo `StrategyDecayMonitor` è **già nel motore** e misura, si ferma a un `LogWarning`. E «prima del rebalance» presuppone che il rebalance raggiunga la corsia viva: **non la raggiunge**, il motore legge la configurazione una volta sola in `StartAsync` |
| 3 Sentiment ML | «fattore macro/sentiment reale; il rischio era look-ahead» | è **news-only** (funding e F&G non lo alimentano); il look-ahead è **risolto e testato dal 2026-07**. Il rischio vero è un altro: `AltDataPoints` è l'unica serie sentiment con una **cancellazione attiva** (180 giorni) e **senza guardiano** — la classe che il funding ha già pagato due volte. E il vocabolario copre 7 ticker: fuori da quelli il fattore è `null` su ogni barra |
| 4 Campaign Planner | «spento» | **acceso.** Trattenuto solo dallo stato `WaitingForTrigger` della campagna. Difetto trovato leggendo: **un annullamento umano fa ripartire la rotazione entro 60s** |
| 5 Pairs | «ottimi risultati, manca la persistenza» | la persistenza manca davvero (`PairCandidate` esiste solo come proposta in `docs/audit/`), e gli 86 artefatti `PairScreen` sono scritti dal 2026-07-02 e **mai letti**. Ma i «risultati» girano a **slippage 0** e il grafico z-score usa un estimatore **diverso** da quello dell'engine: due verità sulla stessa pagina. Verdetto misurato invariato: **0/5 sopravvissuti** |
| 6 Comitato AI | «pronto ad assistere l'orchestratore» | **acceso e mai votato** (§sopra). Le sue proprietà di sicurezza sono vere e pinnate dai test; il problema è che **nessuno può leggere un voto** — il journal mostra `Source` e non espande `VotesJson` |

### Le quattro decisioni del proprietario (2026-08-18)

1. **Bersaglio**: rendere misurabile e sicuro ciò che c'è. Unica eccezione a caccia di valore: **F12**.
2. **Flotta**: si sblocca l'esecuzione **partendo dal ritiro per inedia**, ancora in DryRun; start/stop
   reali solo dopo che il journal mostra corsie liberate.
3. **Champion**: confermato che non se ne promuove nessuno; le quattro funzioni inerti lo **dichiarano**
   con un conteggio calcolato e si riabilitano da sole il giorno che cambia.
4. **Corpus notizie**: esentato dalla purge, con la sua riga nel guardiano di profondità.

### Gli item

| # | Cosa | Stato | Gate / verifica |
|---|---|---|---|
| I1 | **Sonda di stato degli agenti** (`AgentStateProbe`): quattro stati per ognuno dei quattro agenti del guscio — `Spento`, `AccesoInerte`, `AccesoOperante`, `NonDeterminabile` — e card in cima a `/admin/autonomy`. **Due deviazioni dichiarate** (motivo nel PRD): non notifica (lo stato è una condizione, non un evento, e il budget notifiche è condiviso), e «acceso a zero soggetti» **non** è silenzio ma lo stato più insidioso dei quattro | **fatto (2026-08-18)**, 26 test · L4 da fare | ✅ **L2, il livello che conta**: soggetti in abbondanza coi gate spenti ⇒ nessun agente acceso (se fallisse, la sonda leggerebbe i dati come attività); ✅ caso-trappola: fonte muta ⇒ `NonDeterminabile`, mai `Spento` (vale per keyring e journal); ✅ lo stato reale del 18/08 ricostruito dai numeri veri, come test di regressione della premessa sbagliata; ✅ tre test di regressione sulle bugie trovate dalla revisione (sotto) |
| I2 | **«Champion in carica: 0», calcolato**: nella card del drift, con l'avviso che l'interruttore del ritiro risulta ARMATO per il default del POCO. `null` ⇒ «non determinabile», mai `0`. Le manopole restano **modificabili apposta** (vedi sotto) | **fatto (2026-08-18)** per la card del drift; restano gli altri tre punti (union dei fattori §2.9, dual-read B4, corsie `MlChampion`) | il numero coincide con la `count(*)` sul registry — due strade, stesso numero; il giorno che passa a 1 la riga cambia **senza toccare codice** |

> **La revisione avversaria della Fase 0 ha trovato tre bugie nella sonda, e un difetto mio della
> classe che questa ondata esiste per bonificare** (2026-08-18; tabella completa nel PRD). Tre lenti
> indipendenti coi rispettivi confutatori: 17 finding proposti, **8 sopravvissuti**, metà delle
> gravità declassate dai confutatori. La sonda nata per dire con precisione che cosa agisce da solo
> mentiva in tre punti, **sempre nella stessa direzione — sistema più autonomo del vero**:
> `Fleet:DryRun=false` era letto come «esecuzione attiva» mentre AF2b non esiste (ora la capacità è
> una costante dichiarata **accanto al ramo che la implementerà**, `ExecutionArmImplemented`); il
> comitato coi flag a posto era «operante» proprio nello stato in cui non vota da sedici giorni (ora
> il verdetto si misura sui **voti nel journal**, finestra dichiarata di 14 giorni); e la campagna
> non in rotazione prometteva un wake che per le campagne in `Observing` non avviene mai.
>
> Il difetto mio: con zero Champion **disabilitavo** le due manopole del ritiro automatico, nella
> stessa card in cui l'avviso dichiara che l'interruttore è ARMATO. Ragionamento invertito due volte
> — l'unico momento utile per disarmarlo è proprio quello, e il blocco **non impediva nulla** («Salva»
> resta attivo e `SaveAsync` serializza l'intero POCO: si sarebbe potuto persistere `true` ma non più
> toglierlo). Un controllo che toglie il rimedio e lascia passare il rischio. Corretto: manopole
> sempre modificabili, tutto il contributo di I2 nel testo dell'avviso.
>
> *Difetto pre-esistente segnalato a parte*: un modello `Retired` non è ripromuovibile dalla UI, quindi
> un ritiro accidentale sarebbe irreversibile senza toccare il database.
| I3 | **Il tetto di spesa AI che esiste davvero**: `TrackingEnabled` è già `true`, ma i tre limiti sono a **0** e un limite a 0 non si applica — il badge «tracking attivo» si legge come «la spesa è sorvegliata» mentre `CheckBudget` **risponde sempre sì**. Ora il pannello lo dichiara e dà i numeri per scegliere un tetto (chiamate e token di oggi, media al giorno del mese) | **fatto (2026-08-18)** | la condizione si legge da `BudgetMonitor.CurrentValue` e **non dai campi del form**: quelli sono la modifica che si sta digitando, e mostrarli come stato in vigore è la forma dei pannelli che dichiaravano applicata una config mai salvata |
| I4 | **Il budget delle notifiche non è infinito**: `MaxPerHour=20` è **condiviso** fra otto sorveglianti, e le 79 riproposte grigie di 15 giorni ci sono già dentro — il primo che sbaglia soglia zittisce gli altri. `NotificationRateLimitPressure`: quanti nell'ultima ora, quanti ne restano prima del silenzio, quanti soppressi **in attesa** e quanti **da questo avvio** | **fatto (2026-08-18)**, 7 test · deviazione dichiarata (sotto) | ✅ leggere la spia **non consuma slot**; ✅ la finestra scorre anche senza nuovi invii (altrimenti mostrerebbe come occupati slot liberi da un'ora); ✅ il totale **non si azzera mai** — senza, un'occhiata un minuto dopo la tempesta direbbe che non è successo niente |
| I5 | **F7 ridotto all'osso**: `MetricsCollector` aveva una lista **hardcoded** di tre istogrammi ⇒ un istogramma nuovo veniva scartato in silenzio e ogni gate nella forma «il numero compare in `/metrics`» era insoddisfacibile per costruzione. Scoperta dinamica; i tre attesi restano pre-registrati anche a zero misure | **fatto (2026-08-18)**, 4 test | ✅ **la verifica che contava**: i due test che asseriscono il comportamento nuovo sono stati eseguiti contro il codice precedente e **falliscono**, gli altri due passano in entrambi — una verifica che non può fallire non è una verifica. F7 per intero resta il suo item |

> **Deviazione dichiarata su I4**: la pressione del rate-limit resta **sul guscio** e non passa dal
> canale gRPC. Il contratto porta cinque campi e non questo: mapparlo comunque farebbe leggere
> **«0 soppresse» da qualunque motore**, cioè la rassicurazione falsa che l'ondata bonifica. Il
> pannello **dichiara il buco** al posto dello zero — ed è giusto anche nel merito, perché tutti e
> otto i sorveglianti che si contendono il tetto vivono nel guscio.
| I6 | **Drift acceso in sola segnalazione, capace di fallire, visibile in Home**: (a) strumento del costo *prima* dell'interruttore; (b) `SkipReason` — finestra sovrapposta al training, candele insufficienti e modello senza feature producevano tutti `Overall=None`, cioè il verdetto rassicurante a prescindere; (c) `Enabled=true` **e `RetireChampionOnAlert=false` scritti esplicitamente** (chiude la trappola); (d) `FeatureDriftSnapshot` sul pattern D2, ricostruito all'avvio, con l'etichetta che distingue le due derive; (e) le soglie PSI/KS/Page-Hinkley in configurazione e in pannello — oggi si cambiano **solo ricompilando** | **(a), (b), (d) ed (e) fatti (2026-08-18)**, 27 test + migrazione additiva `AddDriftSkipReason` · **resta solo (c), che è operativo** | ✅ i tre rami del salto, ciascuno col suo caso; ✅ **il controllo nella direzione opposta**: un modello senza data di training NON fa saltare il check — un rifiuto costruito sull'ignoranza è un guasto quanto un verde falso, e un monitor che si rifiuta di guardare è inutile quanto uno che dice sempre bene; ✅ la UI **legge** la colonna: una riga saltata perde il badge di gravità invece di mostrarlo accanto al motivo (accanto si leggerebbe comunque il verde) |

> **(d) l'allarme che ti viene incontro, sul pattern D2 invece che reinventato.**
> `FeatureDriftSnapshot` è il gemello deliberato di `FactorDriftSnapshot`: scritto a fine tick,
> **ricostruito all'avvio** dall'ultimo tick registrato — e l'idratazione non è una cache, è la stessa
> ragione che vinse in D2.b: il guscio si riavvia di continuo, e senza di essa l'allarme mancherebbe
> proprio nei minuti in cui uno guarda la Home. Si prende **un solo tick**, il più recente, e non «le
> ultime N righe»: righe di tick diversi mescolate darebbero una fotografia che non è mai esistita,
> con lo stesso modello contato due volte a stati diversi (è la lezione delle finestre sovrapposte).
> In Home è un blocco **indipendente**, non un ramo `else` di quello dei fattori: le due derive
> possono allarmare insieme, e incatenarle nasconderebbe la seconda ogni volta che scatta la prima —
> difetto che ho introdotto scrivendolo e corretto rileggendolo. Le etichette sono esplicite perché
> i due monitor sono **omonimi e diversi**, e due verdetti indistinguibili sullo stesso schermo sono
> peggio di un verdetto solo. ✅ Il caso-trappola: un modello saltato ha `Overall=None` per
> costruzione, quindi il filtro degli allarmi guarda `IsVerdict` e non solo la gravità; ✅ la
> copertura dichiara **quanti sono stati saltati**, perché «0 allarmi su 53» si legge come via libera
> anche quando 50 non sono stati guardati.
>
> **(e) chiudeva una violazione del mandato, non un miglioramento.** Le soglie dei tre rilevatori
> (PSI 0,2/0,25 · KS p<0,05/0,01 · Page-Hinkley 25/50) vivevano **solo** nei default del codice: si
> cambiavano **ricompilando**, senza chiave e senza pannello. Ora sono in `Drift:Thresholds` con la
> loro card, e con la regola server-side che conta: **l'alert non può essere più permissivo del
> warning** — un livello di allarme che non può scattare è peggio di un livello assente, perché
> *sembra* esserci (è la classe dell'avviso EEA/MiCA con la condizione «non connesso», che lo
> escludeva dall'unico caso per cui era stato scritto). Per KS «più severo» significa p-value più
> **piccolo**, al contrario delle altre due famiglie: è l'inversione che si sbaglia più facilmente, e
> ha il suo test. ✅ L2: senza configurazione le soglie sono **esattamente** quelle di prima, campo
> per campo — «configurabile» non doveva significare «cambiate».
>
> **Perché (c) non è stato fatto insieme al resto.** Accendere `Drift:Enabled` è un'azione
> **operativa** sul processo che gira, non una riga di codice: va fatta guardando il costo che (a)
> ora misura, e insieme alla scrittura esplicita di `RetireChampionOnAlert=false` nel file vivo —
> che è l'unico modo di chiudere la trappola della sezione assente. Va nella sessione di livello 4,
> non in un commit.
>
> **La seconda revisione avversaria ha trovato IL QUARTO MODO DI DIRE VERDE — dentro la correzione
> che ne eliminava tre** (2026-08-18, tre lenti + confutazione: 7 finding sopravvissuti su 17).
> I tre rilevatori restituiscono `None` **anche quando non hanno potuto misurare**: se le osservazioni
> valide — dopo il warm-up del fattore e dopo lo scarto dei null — sono sotto `MinObservations`,
> rispondono «dati insufficienti». Il worker vedeva `reports.Count > 0` e persisteva un **giudizio
> verde costruito su rilevatori che avevano dichiarato di non aver guardato**. Il dato per
> distinguerli era già lì e inutilizzato: `ReferenceCount` e `CurrentCount` sul report.
>
> È **dormiente con la configurazione di fabbrica** e si apre alla prima taratura delle soglie che
> (e) ha appena reso amministrabili: basta alzare «Osservazioni min» sopra le osservazioni
> disponibili perché ogni riga diventi un falso «pulito». Il confutatore ha smontato tre dei quattro
> scenari proposti e ne ha trovato uno raggiungibile davvero — attraverso le manopole nuove.
> Corretto con `IsMeasured`, e il **denominatore del verdetto** sono ora le feature davvero misurate:
> «0 su 12» quando 9 non erano misurabili è un rapporto che rassicura contando ciò che nessuno ha
> guardato. Una regola resa più corretta strada facendo: **un allarme è una misura per definizione**,
> perché i rilevatori rispondono `None` quando non guardano — quindi un Warning o un Alert può venire
> solo da un confronto eseguito.
>
> **E `/ensemble` giudicava con un metro diverso.** `EvaluateDriftAsync` chiamava il monitor senza
> soglie (quindi coi default di fabbrica) mentre il worker usa quelle configurate, e non applicava
> affatto la regola del salto: due superfici, due verdetti sullo stesso modello. Peggio, teneva una
> **propria costante** per la finestra recente (200) accanto a `Drift:RecentCandles` — due definizioni
> della stessa finestra, che da amministrabili potevano divergere fino a un rifiuto permanente
> inspiegabile. Ora la pagina passa da `opt.Thresholds`, riusa `DescribeSkip` e `IsMeasured`, e legge
> la finestra dalla configurazione. *Trovato dal test che si è rotto, non dall'analisi.*
>
> Altri tre finding minori corretti: il doc-comment di `DriftCoverage` finito sul metodo sbagliato
> (e proprio sulla coppia che la modifica dichiara di voler tenere distinguibile); il contatore
> `candles_read` che ne contava metà — rinominato `recent_candles_read`, perché il termine dominante
> è la rilettura del periodo di training e il costo totale sta in `tick_ms`; e un test il cui
> commento dichiarava di verificare la lettura a caldo del tetto senza mai cambiarlo, cioè una falsa
> assicurazione scritta nel codice.
>
> **I quattro test che si sono rotti erano un'informazione, non un fastidio.** La guardia nuova ha
> fatto cadere quattro prove d'integrazione del worker, e la ragione è che montavano un monitor finto
> e **zero candele**: stavano esercitando il percorso di persistenza su un check che nella realtà non
> sarebbe mai avvenuto. La correzione giusta era rendere le fixture realistiche (semina di candele
> recenti, `DriftTestData`), non indebolire la guardia — se avessi allentato la soglia per far
> passare i test avrei riportato dentro esattamente il verde falso che l'item rimuove.
>
> **E hanno fatto emergere un difetto di disegno mio**: controllavo `FactorsJson` nel worker per
> decidere «il modello ha feature valutabili?», mentre a quella domanda risponde già il monitor.
> Erano **due regole sulla stessa domanda**, che possono divergere sullo stesso modello — il difetto
> già pagato in D2 e con `SeriesFreshness`. Ora il caso si dichiara **dopo** la valutazione, da un
> report vuoto: una regola sola, e sta dove vive la conoscenza.
>
> **Trappola trovata costruendo (b), e vale per chiunque tocchi lo schema da un worktree**:
> `dotnet ef migrations add` ha generato una migrazione che oltre alla colonna voluta conteneva
> `CREATE TABLE ResearchCandidates` e `AddColumn LastCandleUtc` — **due migrazioni già esistenti e
> già applicate al database vero**: applicarla sarebbe fallita su «relation already exists». La causa
> non è EF: `dotnet build` dell'app **non ricostruisce** il progetto delle migrazioni (è il disegno
> anti-ciclo del migrate-on-startup), quindi in un worktree la sua DLL resta ferma alla data di
> creazione del worktree — qui il 5 agosto, contro migrazioni del 14 e del 17 — ed EF diffa contro
> *quello* snapshot. Controprova rapida: se `git diff` sullo snapshot mostra solo la tua modifica
> mentre il `Up()` ne contiene altre, la migrazione è stata generata contro un assembly vecchio.
> Rimedio: costruire esplicitamente `ProcioneMGR.Migrations.Postgres` e rigenerare con `--no-build`.
| I7 | **Un annullamento umano è un ordine**: `Cancelled` distinto da `Failed`, campagna **in pausa** (`PausedUntilUtc`, migrazione additiva) invece della rotazione che riparte entro 60s. E il percorso campagna **rispetta** `AutoReapply:Enabled`, che prima scavalcava | **fatto (2026-08-19)**, 5 test | ✅ annullato ⇒ pausa e **nessun run** al tick dopo, config marcata `Cancelled` e non `Failed`; ✅ **controllo sul rumore**: pausa a 0 ⇒ sequenza storica identica, e `Failed` non mette in pausa (senza, «la pausa funziona» sarebbe soddisfatto da un planner che non riparte mai); ✅ ri-applica spenta ⇒ l'applier **non viene mai invocato** e i sopravvissuti restano registrati, ri-applica accesa ⇒ percorso di sempre |

> **La decisione presa sulla domanda §7.1 del PRD**: il percorso campagna **rispetta**
> `AutoReapply:Enabled`. Era l'ipotesi dichiarata, ed è quella implementata — un interruttore che
> chiude una porta e ne lascia aperta un'altra è la stessa forma dei pannelli che scrivevano sul
> processo sbagliato, e qui la porta aperta **riscrive corsie**. I sopravvissuti non si perdono:
> restano registrati e notificati per un click umano. Reversibile con `Campaign:RespectAutoReapplyGate`.
| I8 | **Rendere leggibile il silenzio della flotta**: **(a)** i quattro numeri che lo spiegano (`FleetOrchestrator.Explain`), contati con gli **stessi predicati** della decisione — estratti e condivisi, non ricopiati; **(b)** `Source='default'` disambiguato in tre cause; **(c)** `VotesJson` espandibile nel journal, col JSON illeggibile **dichiarato** invece che nascosto; **(d)** «Prova il comitato» | **fatto (2026-08-19)**, 11 test · (d) «Prova il comitato» incluso | ✅ lo **stato reale del 18/08** ricostruito: 0 pass in coda, grigi, 0 corsie libere, 5 sotto governo — e la ragione lo nomina; ✅ il complemento (2 candidati + 1 corsia libera ⇒ il comitato **può** essere interrogato), senza il quale «non può» sarebbe soddisfatto da una diagnosi che dice sempre di no; ✅ le corsie intoccabili non contano come sotto governo; ✅ **le tre cause di «default» restano tutte distinguibili** — se due collassassero il difetto tornerebbe in forma ridotta |

> **La causa del silenzio, in una riga**: un menù — e quindi una domanda per il comitato — nasce
> **solo** con ≥2 candidati «pass» in coda **e** una corsia libera. Con la coda sempre vuota non c'è
> pareggio, quindi nessuna domanda. **Un comitato acceso che non vota non è guasto: non gli si sta
> chiedendo nulla** — ed è la frase che sedici giorni di righe identiche non dicevano. Sul `default`
> la distinzione che conta è fra «ha deliberato e la maggioranza non si è formata» e «non ha
> funzionato»: nel primo caso il default è la risposta, nel secondo è un ripiego su un guasto.
>
> I tre predicati (corsie di flotta, coda di assegnazione, corsie libere) sono stati **estratti da
> `Decide` e condivisi** con `Explain`, non ricopiati: due definizioni di «coda» darebbero un
> pannello che spiega un silenzio diverso da quello vero — il difetto di D2 e `SeriesFreshness` nel
> posto peggiore per ripeterlo.
| I9 | **Sentiment: lo strumento misura ciò che il modello vede** — (b) il pannello IC smette di contare come `0` le notizie che la via ML **esclude**; (c) pavimento di numerosità (`MinObservations`) nella selezione per IC, con la manopola in `/feature-selection` | **fatto (2026-08-19)**, 4 test · (a) copertura per simbolo inclusa | ✅ L2: `MinObservations=0` ⇒ selezione **identica** a prima; ✅ un fattore quasi sempre nullo entra col pavimento spento ed **esce** quando sale — se non cambiasse nulla la manopola sarebbe inerte; ✅ il controllo opposto: un fattore denso **sopravvive** al pavimento (un filtro che scarta tutto è inutile quanto uno che non scarta nulla); ✅ il pavimento **non nasconde** i candidati dalla classifica, altrimenti l'esclusione diventerebbe invisibile |

> **(b) era una doppia verità della stessa famiglia dei pairs.** Il pannello mappava le notizie con
> `SentimentScore ?? 0m` — cioè **inventava un punteggio neutro** per qualcosa che nessuno ha
> valutato — mentre `SentimentNewsProvider`, che alimenta il fattore quando lo usa un modello,
> filtra `SentimentScore != null`. Le due strade misuravano **fattori diversi sulla stessa serie**:
> il pannello dice se il sentiment informa, e lo diceva su un fattore che il modello non vedrà mai.
> È anche la regola già scritta per lo scorer LLM nella Fase B — *un elemento non scorabile si
> SALTA, mai uno zero inventato*: uno zero non è «neutro», è «non lo so» travestito. Ora le escluse
> si contano e si dichiarano.
>
> **(c) chiude un modo di premiare il rumore.** `Observations` era già sul risultato della
> valutazione, popolato, e non lo guardava nessuno: un fattore null sulla stragrande maggioranza
> delle barre — il sentiment su un simbolo fuori dal vocabolario dei ticker, una feature con warm-up
> lunghissimo — può avere |IC| altissimo su una manciata di punti e **vincere l'ordinamento** contro
> fattori misurati su migliaia. L'IC non è confrontabile fra numerosità diverse.
| I10 | **Pairs: una sola verità, e i costi in chiaro** — (a) il motore **espone l'analisi che ha deciso** e la pagina disegna quella (niente più ricalcolo con estimatore fisso); (b) `SlippagePercent` e `StopZScore` esposti, con lo slippage al **default di piattaforma** invece che a zero | **fatto (2026-08-19)**, 4 test · (c) `ExperimentRun` incluso | ✅ L1 contro riferimento indipendente: l'analisi esposta coincide **punto per punto** con l'analizzatore chiamato direttamente; ✅ **la prova che il difetto era visibile**: le due curve sono diverse — se fossero uguali il test non potrebbe fallire, cioè non sarebbe una verifica; ✅ **lo slippage morde** (capitale finale minore), altrimenti esporlo sarebbe una manopola che non muove nulla |

> **Il difetto (a) era una doppia verità in pagina, entrata col C2** (adozione del Kalman,
> 2026-07-26) e trovata solo ora: la pagina passava al motore l'estimatore scelto ma disegnava lo
> z-score con un `RollingPairsSpreadAnalyzer` **fisso**. Scegliendo Kalman si vedeva la curva
> dell'OLS — **il grafico descriveva un backtest diverso da quello eseguito**, e `docs/pagine/pairs-trading.md`
> dichiarava esattamente che non poteva succedere («nessuna doppia verità»). Il rimedio non è
> ricalcolare meglio: è **non ricalcolare**. Un motore che non espone l'analisi ora fa sparire il
> grafico dichiarandolo, invece di disegnarne uno sbagliato.
>
> **E (b) ridimensiona gli «ottimi risultati»**: la pagina costruiva la configurazione senza
> `SlippagePercent`, quindi girava a **zero** mentre `/backtest` parte da 0,05%. Su una strategia a
> **due gambe** il costo si paga due volte per trade: lo sconto era il più grande possibile
> esattamente dove fa più danno, e i numeri di questa pagina non erano confrontabili con quelli di
> nessun'altra superficie della piattaforma.
| I11 | **«Trade attesi dall'holdout»: una regola sola, consumata da due** (ritiro per inedia e freno per gamba). Sul **simbolo attuale**, col **tempo-al-verdetto dichiarato**. Due regole per la stessa domanda è il difetto già pagato in D2 e con `SeriesFreshness` | **FATTO** | L1 ricostruisce il trade/mese che il run dichiara; L2 campo assente (corsie 0-2) ⇒ nessuno agisce e **lo si dichiara** |
| I12 | **Ritiro per inedia + dedup dei grigi per identità**, ancora in DryRun; poi AF2b (`targetLanes`, start/stop reali) **una corsia per volta, solo Paper**. Assorbe AF2c-1 e AF2c-5 | **FATTO**, L4 compreso | **L1 con lo stato reale**: condanna 3, 5, 6, 7 e **non** la 2 (troppo giovane) né l'impronta né le quarantenate; L2 soglia a 0 ⇒ piano bit-identico su 100 tick fuzzati; L4 le 40 riproposte diventano 1 |
| I13 | **Freno per gamba: prima la misura, poi l'azione** — (a) l'avviso di deriva esteso alle **gambe attive** (oggi una gamba disattivata continua a operare fino al riavvio della corsia e nessuno lo dice); (b) pannello di sola lettura sui trade veri; (c) **condizionato** all'esito di (b), freno dove si applica `mayOpen`, mai un `continue` che lasci posizioni orfane | **(a)+(b) FATTI**, (c) sospeso in attesa della misura | **il gate di (b) può chiudere il filone e va bene così**: a 2-6 trade/mese «≥20 trade sul simbolo attuale» può dare zero gambe misurabili — allora il pannello lo dice e (c) non si fa. L1 il riferimento indipendente **esiste già in repo** |
| I14 | **`PairCandidate` + `PairSpreadWindow` col loro lettore** — indice derivato dagli 86 artefatti mai letti, sul progetto di `ResearchCandidateIndex`; storia dello spread sul pattern `FactorIcWindows`; pannello in `/pairs-trading`. Sola lettura, nessuna decisione automatica | **FATTO** | **L2 decisivo**: su due random walk indipendenti il monitor non deve **mai** dichiarare cointegrazione; su una relazione piantata deve trovarla. L1 il rebuild combacia con l'aggregato SQL sugli artefatti |
| I15 | **Corpus notizie esentato dalla purge + riga nel guardiano di profondità** (decisione del proprietario); da spenta la riga resta **misurata** e mostrata come «non sorvegliata», mai un OK finto | **FATTO** | L1 profondità e conteggio combaciano col `SELECT` a mano; L2 corpus profondo ⇒ il guardiano tace per tre giri |
| I16 ≡ F12 | **Capacità e universo del carry**: l'unica classe con edge misurato positivo, l'unica che opera oggi, l'unica che nessuno sta dimensionando — mentre il basis è in compressione | **FATTO** (verdetto NEGATIVO, vedi `docs/audit/30_CARRY_CAPACITA_2026-08.md`) | **report con verdetto scritto anche se è «la soglia attuale è già ottima»**; trade/mese e durata mediana dichiarati |


### Fase 3 e Fase 4 eseguite (2026-08-19) — I11 e I12

**I11, il denominatore condiviso.** Il numero «quanti trade ci si aspetta da questa gamba» nasceva
nel lettore della flotta al momento della candidatura e **moriva lì**: una volta schierata, nessuno
sapeva più quanti trade quella gamba dovesse fare. Ora vive sulla gamba
(`EnsembleStrategy.ExpectedTradesPerMonth` + `ExpectedTradesSource`), scritto da **tutti e tre** i
percorsi di schieramento — l'applicatore della pipeline, il click della fascia grigia in `/fleet`,
l'aggiunta di una gamba grigia in `/ensemble` — e mostrato in `/trading` col **tempo-al-verdetto**:
*«~2 trade/mese attesi: servono ~10 mesi per i 20 trade che la regola di ritiro pretende»*.

Il rischio dell'item era ripetere il difetto che l'item stesso combatte, e si è materializzato tre
volte durante la scrittura: la finestra di holdout si calcolava in due posti (accorpata su
`PipelineDateRanges.HoldoutMonths()`), la query «holdout di questo run» stava per essere scritta due
volte (`HoldoutWindow`), e la soglia dei 20 trade stava per essere **ricopiata nel markup** accanto
alla manopola che la definisce — `Fleet:RetireMinTrades`. L'ultimo è quello che un test verde non
avrebbe mai rivelato: `LaneStory_TempoAlVerdetto_SegueLaSogliaConfigurata` prova la stessa gamba con
due soglie diverse, e sarebbe verde su una e rosso sull'altra.

**I12, la capacità di liberare una corsia.** Il ritiro per Sharpe pretende `RetireMinTrades` trade e
**chi non opera non ci arriva mai**: al 2026-08-19 le corsie di flotta 3-7 avevano chiuso *da uno a sei
trade ciascuna sul simbolo attuale* in 6-16 giorni (misurato: 5, 1, 5, 6, 3), quindi non erano ritirabili per nessuna via. Una corsia che non si
libera mai blocca la flotta, e a monte il comitato — che riceve una domanda solo quando esiste una
corsia libera con due candidati che se la contendono. **I sedici giorni senza un voto avevano lì la
loro causa**, non nel comitato.

Il criterio confronta col ritmo **atteso nel periodo osservato**, non con un conteggio assoluto: 30
trade/mese fermi da due settimane sono un guasto, 2 trade/mese con un trade in due settimane sono la
norma. E dove il ritmo atteso non è noto — corsie 0-2, gambe configurate a mano — **non si condanna**:
l'ignoranza non condanna, e una conoscenza *parziale* (una gamba su tre che dichiara) è ignoranza
travestita, quindi la somma è parziale-o-niente.

**Il dedup dei grigi**: le proposte nascevano per run, e la caccia rigira gli stessi parametri sugli
stessi mercati — 83 proposte in journal, ognuna una notifica. Ora una per **identità canonica**
(`PipelineCandidateKey`), col numero dei run che l'hanno ritrovata in coda al messaggio. Sopravvive
il run **più recente**, non il più vecchio: un grigio è una proposta di forward test, e il forward
test si fa sull'ipotesi vista sui dati più freschi — l'opposto della coda «pass», che è FIFO perché
lì il criterio è non far invecchiare nessuno.

**AF2b, il braccio esecutivo — metà.** L'orchestratore sa **fermare** una corsia; continua a non
saperla **avviare**. È l'ordine deciso dal proprietario: fermare libera una corsia, non impegna
capitale e si disfa con un click; avviare mette in corsa una strategia scelta da una macchina, e
quando te ne accorgi ha già operato. Quattro condizioni tutte necessarie perché un'azione avvenga —
braccio presente, dry-run spento, corsia **elencata** in `Fleet:ExecutionLanes` (vuota di default),
budget del tick non esaurito — e la modalità **riletta dal motore nell'istante dell'azione**: il
piano è deciso su una fotografia che può avere minuti, e se nel frattempo la corsia è passata a
Testnet non si tocca. Fail-closed: modalità non leggibile ⇒ non si tocca.

`Fleet:ExecutionLanes` è una **lista e non un interruttore** di proposito: un booleano aprirebbe di
colpo tutte le corsie di flotta, e il primo tick dopo l'accensione potrebbe fermarne quattro insieme.
La lista rende l'ampiezza esplicita, reversibile togliendo un numero, e permette il collaudo che il
PRD chiede — *una corsia per volta, solo Paper*.

La sonda degli agenti è stata corretta di conseguenza, ed è la stessa correzione della revisione
avversaria del 2026-08-18 **spostata di un flag**: con il braccio implementato e il dry-run spento,
`Fleet:ExecutionLanes` vuota significa che la macchina non può toccare nulla — dichiarare
«esecuzione attiva» sarebbe stata di nuovo la classe «controllo che rassicura».

> **Resta da fare su I12**: il livello 4 sull'app vera (le 40 riproposte che diventano 1 nel journal
> reale, e un ritiro per inedia osservato su una corsia autorizzata in Paper). Il codice è pronto e
> **inerte**: `ExecutionLanes` è vuota, quindi in produzione oggi non cambia nulla.


#### I13(a) — la spunta «Attiva» non era un interruttore

Il motore fotografa `IsActive` all'**avvio** della corsia (`TradingEngine._active`, riga 272):
togliere la spunta a una gamba mentre la corsia gira **non ferma nulla** fino al riavvio. La tabella
di `/ensemble` mostrava la gamba spenta, il motore continuava ad aprirci posizioni, e le due verità
non si incontravano da nessuna parte — l'operatore credeva di aver fermato qualcosa che stava ancora
operando.

Non si è cambiato il comportamento del motore, si è reso **visibile**: applicare la disattivazione a
caldo lascerebbe posizioni orfane, che è precisamente il pericolo che il punto (c) di questo stesso
item mette in guardia. La regola qui è la quinta della piattaforma — *degradare dicendolo*.

Lo stato del motore porta ora `RunningStrategyIds` (campo 25 del contratto gRPC, additivo): gli
`StrategyId` delle gambe che sta **davvero** eseguendo. `/ensemble` confronta la configurazione con
quel fatto e dichiara entrambi i versi — spenta-ma-in-corsa (giallo, con l'istruzione di riavviare)
e accesa-ma-non-ancora-in-corsa (grigio). E quando il motore non risponde **non accusa nessuno**:
dice che il confronto non è stato possibile, invece di leggere il silenzio come «tutto allineato»,
che sarebbe la classe «controllo che rassicura» nel caso in cui più si vorrebbe sapere.


#### I13(b) — il pannello che può chiudere il filone, e un difetto trovato scrivendolo

Il monitor di decadimento misurava il realizzato per `StrategyId` **senza filtrare il simbolo**. Le
corsie hanno vite precedenti: una riassegnazione, o una coppia cambiata a mano in `/ensemble` senza
riscrivere le gambe, faceva nascere lo Sharpe «realizzato» di una gamba da trade fatti su **due
mercati diversi** — e nessuna riga lo diceva. Ora il filtro c'è (`t.Symbol == cfg.Symbol`, la regola
AF2c-2: il criterio è il simbolo *attuale*) e i trade scartati vengono **contati e dichiarati**: un
conteggio più basso senza spiegazione si legge come un guasto.

Sopra le schede c'è ora il **verdetto di misurabilità**: *«N gambe su M misurabili»*, e per quelle
che non lo sono **quanto manca** al ritmo che dichiarano — «alle altre servono fino a ~3,2 mesi»,
oppure «il ritmo atteso non è dichiarato: quando lo saranno non è derivabile». Un «non misurabile»
senza una data è un'informazione a metà.

> **Questo verdetto è il gate del punto (c), e ci si aspetta che lo chiuda.** Al 2026-08-19 le corsie
> di flotta 3-7 avevano da uno a sei trade sul simbolo attuale: con «≥20 trade» la risposta
> quasi certa è **zero gambe misurabili**, e allora il freno automatico per gamba **non si fa**.
> Misurare prima di agire vuol dire anche accettare che la misura dica di non agire. Il numero vero
> si legge in `/ensemble` sull'app reale — è un livello 4, non una deduzione.
>
> Senza il gate esplicito, il pannello avrebbe mostrato «Sharpe realizzato 0,00 vs atteso 1,20 ⇒
> ALERT» su gambe con un solo trade: la classe «controllo che rassicura» al contrario — allarmare su
> un numero che non esiste.



### Fase 6 (2026-08-20) — I16 ≡ F12: il carry misurato, e il verdetto è negativo

Report completo: **`docs/audit/30_CARRY_CAPACITA_2026-08.md`**.
Riproducibile: `dotnet run --project tools/PlatformExpand -- carrycapacity all`.

**Negli ultimi 365 giorni il carry non è profittevole su nessuno dei sei mercati, a nessuna soglia
che apra e a nessuna taglia.** Non è una questione di parametri: il premio non c'è più.

La compressione, misurata sui nostri 42.644 eventi di funding reale: la frazione di tempo in cui il
funding paga abbastanza per aprire è passata dall'**82,6% del 2021** al **19,9% del 2026** — e nel
2026 si è **negativo il 35,9% del tempo**. Il benchmark esterno della roadmap (basis 25% → <5% in due
anni) è confermato e superato.

**Il numero che spiega tutto è il pareggio.** Un round trip costa 0,420% del nozionale (quattro fill,
due gambe). A 5% annualizzato si incassano 0,0137% al giorno: servono **30,7 giorni in posizione solo
per non perdere**. La durata mediana degli episodi misurata è di 3-14 giorni su quattro mercati su
sei — si paga il round trip più volte di quante lo si ripaghi.

Ne segue che **la soglia a 5% era sbagliata anche negli anni buoni**: sulla storia intera l'ottimo è
12-20% su tutti e sei, e a 5% si lasciano da 1,5 a 14 punti di netto annualizzato. E la **capacità**,
quando l'edge c'era, era di **1-5 milioni per gamba** su BTC/ETH, un milione su SOL/XRP, centomila su
DOGE — un vincolo da conoscere prima di dimensionare.

**Due difetti trovati dalla misura stessa, entrambi della classe già bonificata in questa ondata.**
Il primo: il verdetto presentava come «soglia migliore» una soglia che *non apre mai*, perché su un
periodo in perdita lo zero batte tutto — cioè incoronava l'astenersi. Il secondo: quando la migliore
soglia perde, la frase «non c'è niente da cambiare» contraddiceva quella immediatamente precedente.
Entrambi corretti, entrambi con il loro test.

**Non si propone nulla di automatico.** Il carry Paper resta acceso: produce osservazioni a costo
zero ed è la sorgente che dirà se e quando il premio torna.

> **Un limite dichiarato**: solo Binance. Bitget — l'unico exchange a leva utilizzabile da IT/UE dopo
> la restrizione MiCA — non ha storia di funding in questo database. Il premio potrebbe esservi
> diverso, e non lo sappiamo.

### Fase 5 (2026-08-19) — I15 fatto, I14 a metà

**L'ordine è invertito rispetto alla numerazione, e il motivo è misurabile**: la purge delle notizie
gira a ogni tick del worker del sentiment (default 30 minuti, worker acceso di fabbrica). Il corpus
si stava accorciando *mentre leggevo il codice*, e le notizie cancellate non tornano. I14 non perde
nulla ad aspettare: gli 86 artefatti dello screening sono in database dal 2026-07 e nessuna
retention li tocca.

#### I15 — l'esenzione è MIRATA, e non basta da sola

Esente dalla purge è la notizia **con punteggio**: quella che uno scorer ha già valutato, e che ha
quindi un costo di produzione. Le notizie grezze restano potabili — esentare tutta la tabella la
farebbe crescere senza limite per conservare righe che nessun consumatore guarda. Il predicato vive
in `NewsCorpus` e lo **condividono** purge e guardiano: se fossero due, il guardiano misurerebbe la
profondità di un insieme diverso da quello protetto, e direbbe «tutto a posto» di righe che il worker
sta cancellando.

E l'esenzione da sola non chiude l'item: **la storia del funding è andata persa due volte con
l'esenzione al suo posto** (drop, restore parziale, re-backfill). L'esenzione protegge dal worker,
non da tutto il resto — perciò esce nello stesso cambiamento della riga nel guardiano.

`NewsEnforced` nasce **false**, e non è timidezza: la purge ha girato da sempre, quindi oggi il
corpus non *può* essere più profondo di `NewsRetentionDays`, e qualunque àncora plausibile
scatterebbe al primo giro. Un allarme perpetuo smette di essere letto. Da spenta la riga resta
**misurata** e mostrata «non sorvegliata» — che è testualmente ciò che il gate chiede. Si accende
dopo aver letto il minimo vero: è la storia dell'àncora del funding, spostata da gennaio a ottobre
2020 solo *dopo* la misura sul database reale.

**Due difetti preesistenti trovati scrivendolo**, entrambi della classe «due regole per la stessa
domanda»: la frase «che cosa ci si aspetta da questa serie» era costruita a mano in *due* punti — la
riga sorvegliata e quella non sorvegliata — e al primo cambio di formato la pagina avrebbe mostrato
due attesi diversi per due righe equivalenti, senza che nessun test se ne accorgesse perché entrambe
sarebbero state giuste ognuna per sé. E il messaggio «serie ASSENTE» nominava
`SentimentMetricPoints` come costante: sulla riga delle notizie — che vive in `AltDataPoints` —
avrebbe mandato a cercare la perdita nel posto sbagliato.

#### I14(a)+(b) — 86 artefatti che nessuno aveva mai riletto

Non esisteva **una sola query** nel repo che filtrasse `Kind == "PairScreen"`. Ogni run testava la
cointegrazione di tutte le combinazioni dell'universo e scriveva il risultato in un blob che nessuna
superficie apriva. Ora `PairCandidate` li indicizza a righe — tabella **derivata** e ricostruibile,
sul progetto di `ResearchCandidateIndex` — e `/pairs-trading` ha il suo pannello di sola lettura.

**La trappola che avrebbe reso il pannello muto**: nel payload `IsTradeable` è una property
*get-only*, quindi System.Text.Json la **scrive** ma la **ignora** in deserializzazione. Un
indicizzatore che la mappasse dal blob scriverebbe `false` su ogni riga, e il filtro «solo quelle che
hanno passato» sarebbe sempre vuoto — con l'aria di funzionare. Si ricalcola, e un test serializza un
payload con `IsTradeable: true` per dimostrare che rileggerlo non lo restituisce.

**Un difetto trovato dal mio stesso test**: avevo scritto che uno screening senza coppie «si conta
come indicizzato, altrimenti l'incrementale lo ripescherebbe per sempre». Il test è diventato rosso:
un run senza righe non lascia traccia nella tabella, quindi **viene riletto comunque**. La promessa
non era mantenuta. Ora i run vuoti si contano a parte e il fatto è dichiarato — il danno vero sarebbe
stato far dire al pulsante «indicizzato 1 run» per sempre su un archivio dove non c'è più niente da
fare.

Il pannello dichiara ciò che NON è: «operabile» vuol dire «ha passato lo screening», non «pronta a
partire» — nessuna coppia è schierata, le corsie sono mono-simbolo, e l'elasticità β mostrata è
*full-sample*, non il β walk-forward del backtest qui sopra. Senza quelle righe sarebbe stata la
classe «controlli che rassicurano a prescindere dalla realtà» che questa ondata esiste per bonificare.

> `PairSpreadWindow` è stato poi fatto — vedi il blocco I14(c) qui sotto.




#### I14(c) — `PairSpreadWindow`, e il gate che sarebbe stato insoddisfacibile

Il pezzo con **carico di scrittura permanente**, l'unico dell'ondata. Un worker registra ogni 12 ore
lo spread delle coppie sorvegliate su finestre **non sovrapposte**; `/pairs-trading` ne legge la
storia. Sola lettura: non apre, non chiude, non tocca una corsia.

**Le coppie le sceglie una persona**, come dice il testo dell'item. L'alternativa — alimentarle da
ciò che lo screening marca operabile — sceglierebbe fra centinaia di test ADF per timeframe **senza
correzione per test multipli**: al 5%, su 190 coppie ne «trova» una decina per puro rumore e le
sorveglierebbe come relazioni. È il primo cugino dell'errore già pagato randomizzando su asset
correlati, che fabbricava falsa significatività.

**Il gate L2 era insoddisfacibile alla lettera, e andava visto prima di scrivere.** «Su due random
walk indipendenti il monitor non deve **mai** dichiarare cointegrazione»: ma un test ADF al 5%
dichiara stazionario il 5% delle finestre di puro rumore — *per costruzione, non per difetto*. Un
verdetto per-finestra avrebbe quindi detto «cointegrata» su rumore una volta su venti, e il gate
sarebbe stato impossibile da soddisfare onestamente. È la classe «gate senza strumento», e ci si
accorge di averla addosso solo dopo aver scritto tutto.

La risposta: **il verdetto è una proprietà della SERIE, non della finestra.** Si guarda la frazione
di finestre non sovrapposte stazionarie contro una soglia alta (0,6). Sotto il nullo quella frazione
vale ~0,05 e perché venti finestre arrivino al 60% servirebbe un evento dell'ordine di 10⁻¹²; su una
relazione vera vale ~1. La distanza fra le due è ciò che rende il gate **verificabile** invece che
aspirazionale.

E la **rottura si definisce come perdita di uno stato precedente**: una coppia è rotta se *era*
persistentemente stazionaria e non lo è più. Sotto il nullo la persistenza non c'è mai stata, quindi
nessuna rottura è dichiarabile — **per costruzione, non per fortuna**. È la forma che rende vera la
seconda metà del gate.

Il test del nullo gira su venti semi e non trova mai né relazione né rottura. Accanto c'è la misura
onesta: il **tasso** di falsi positivi per finestra su 400 finestre di rumore, che deve stare dove ci
si aspetta. Serve a due cose — se fosse molto più alto il test di cointegrazione sarebbe rotto e la
frazione poggerebbe sul nulla; se fosse **zero** il test sarebbe cieco, e un test cieco supera il
gate del nullo senza dimostrare niente.

**Il carico, dichiarato in numeri e nel pannello**: per coppia il primo giro scrive 20 righe, dal
secondo in poi **una sola** perché l'upsert è idempotente. Con cinque coppie ogni 12 ore fanno ~10
righe al giorno, ~3.700 in un anno. È poco *perché* le coppie le sceglie una persona. Il worker nasce
spento e con l'elenco vuoto: due condizioni, entrambe necessarie.

Le finestre sovrapposte si tolgono **in lettura** (stesso `SelectDominantGrid` della storia dell'IC):
il worker taglia la griglia dalla candela più recente all'indietro e a ogni giro la griglia scivola —
punti che condividono dati sono correlati per costruzione e gonfierebbero proprio la frazione su cui
il verdetto si esprime.

### Revisione avversaria delle Fasi 3-5 (2026-08-19)

Cinque lenti indipendenti sul codice appena scritto, e per ogni ritrovamento **tre scettici
incaricati di demolirlo**: 29 candidati, **17 sopravvissuti** alla confutazione (alcuni sono lo
stesso difetto trovato da lenti diverse), 12 respinti. Tre dei difetti confermati li avevo introdotti
io **negli item che esistono proprio per eliminarli**.

**1. L'esenzione «mirata» delle notizie è totale — misurato, non dedotto.** Avevo scritto nel codice
che l'esenzione riguarda le sole notizie con punteggio e che «le grezze restano potabili». Sul
database vero: **22.777 notizie, 22.777 con punteggio, ZERO grezze**. Vero nel codice, falso nei
fatti — la definizione esatta della classe che l'ondata bonifica, scritta da me dentro I15.
Conseguenza reale: `NewsRetentionDays` non limita più quella tabella, e **tre lettori che caricavano
l'intero archivio erano sicuri solo perché la purge lo teneva a 180 giorni**. Il peggiore girava a
ogni tick del sync, per sempre. Corretto: la verità è dichiarata, e i tre caricamenti hanno la loro
finestra esplicita (l'indice unico su `DedupeKey` resta la garanzia vera contro i duplicati).

**2. Il ritiro per inedia costruiva il verdetto su due fotografie diverse.** I trade li conta il
motore; il ritmo atteso lo somma la configurazione — e I13(a), scritto lo stesso giorno, stabilisce
che le due divergono finché la corsia non riparte. Bastava **aggiungere una gamba** da 30 trade/mese
a una corsia sana e salvare per farle emettere «Corsia in INEDIA» al tick dopo; col braccio armato,
per fermarla davvero. Ora quando le due fotografie non concordano il ritmo atteso vale `null` e il
criterio **rinuncia**: l'ignoranza non condanna, applicata con coerenza.

**3. «Non te lo so dire» letto come «non sto eseguendo nulla».** In proto3 un `repeated` assente si
deserializza **vuoto, mai null**: un motore con un'immagine precedente al campo delle gambe in
esecuzione risponde con una lista vuota *mentre esegue*. Il ramo «non determinabile» che avevo
scritto pretendeva `null` e non poteva scattare mai — e il difetto produceva la bugia peggiore dei
due versi: nessun avviso sulle gambe spente ma ancora operate, **più** l'affermazione falsa che tutte
le attive «non sono eseguite».

**4. «Il prossimo tick le ritira e libera il posto»** — promessa fatta da una funzione pura che non
conosce né `DryRun` né `ExecutionLanes`. Nel default della piattaforma il ritiro non arriva, e
l'operatore avrebbe aspettato. Ora la frase dice ciò che è vero in ogni assetto (il verdetto c'è) e
rimanda a dove si legge se verrà eseguito.

**5. Il pannello delle coppie contava su una finestra troncata** e presentava i numeri come totali:
con 86 artefatti e C(n,2) coppie per run, il taglio a 4.000 righe lasciava passare meno di dieci run
— e la pagina avrebbe dichiarato «21 run indicizzati» due centimetri sotto un alert che diceva
«indicizzati 86 run». Ora i conteggi si fanno sul database. E «l'indice è già allineato agli
artefatti» veniva detto **anche quando ogni run era stato scartato** per payload illeggibile: un
alert azzurro rassicurante su un guasto totale, con l'unica traccia nei log del server.

**6. Due aritmetiche del mese ai due lati della stessa disuguaglianza**: l'atteso nasceva da 30,44
giorni/mese, il tempo trascorso con cui veniva riproporzionato da 30,0. Lo scarto cresceva dell'1,5%
per ogni mese di osservazione, sempre **contro** la corsia. Ora `TradeFrequency.DaysPerMonth` è una
costante sola — la classe esiste per non avere due regole per la stessa domanda, e ne aveva due per
la stessa *unità*.

**7. Le quattro manopole nuove non passavano dalla validazione lato server.** `StarvationFraction`
è l'unica della sezione che può fare danno restando «valida» per il binder: sopra 1 condanna *ogni*
corsia, compresa quella che opera esattamente quanto promesso.

> Dodici ritrovamenti sono stati **respinti** dagli scettici e non toccati — fra questi «il taglio a
> 4000 cade dentro un run», «il badge OK verde mentre una serie non è sorvegliata» e «il verdetto di
> misurabilità conta le gambe disattivate»: tutti smontati aprendo il codice o trovando il test che
> già copriva il caso.

### Livello 4 della Fase 4 (2026-08-19, app vera, database e motore reali)

**Il dedup dei grigi, misurato sul journal vero.** 91 righe `ProposeGrey`, 91 run distinti — e
**sei** cose distinte. Due ne spiegano 87:

| proposte | candidato |
|---|---|
| **44** | `Composite DOT/USDT 1h` — Sharpe holdout 1,93 su 7 trade |
| **43** | `GridMeanReversion XRP/USDT 4h` — Sharpe holdout 2,10 su 15 trade |
| 1 | `Composite LTC/USDT 15m` · `Composite LTC/USDT 30m` · `RegimeConditional AAVE/USDT 1d` · `Supertrend ADA/USDT 4h` |

Il gate diceva «le 40 riproposte diventano 1»: la realtà era **44 → 1** e **43 → 1**. Il pannello ora
dichiara «1 grigi **distinti**», non il conteggio dei run.

**Correzione a un numero che avevo scritto.** Nel commit precedente ho affermato che le corsie di
flotta 3-7 avevano «un trade ciascuna o zero in 13-15 giorni». Sul database vero i trade **sul
simbolo attuale** sono: corsia 3 → 5 (13,8 gg), 4 → **1** (16,0 gg), 5 → 5 (5,9 gg), 6 → 6 (16,0 gg),
7 → 3 (13,8 gg). Non «uno o zero»: da uno a sei. **La conclusione non cambia** — nessuna arriva
neanche vicino ai 20 trade che il ritiro per Sharpe pretende, quindi nessuna era ritirabile — ma il
numero illustrativo era sbagliato e va corretto dove l'ho scritto.

**Il difetto di I13(b), visto dal vivo e più grave di come l'avevo descritto.** La corsia 3 ha **24
trade totali ma solo 5 sul simbolo attuale**: diciannove appartengono a vite precedenti della corsia.
La corsia 0 ne ha **159 in totale e ZERO su AAVE/USDT**, che è la sua coppia attuale. Prima di I13(b)
il monitor di decadimento avrebbe calcolato uno «Sharpe realizzato per AAVE/USDT» da 159 trade fatti
su altri mercati. Non era un'ipotesi: era lo stato in produzione.

**I13(a), un'istanza viva sulla prima corsia guardata.** La corsia 3 porta una gamba
`RsiOversold (fascia grigia, run 1d5cd47e)` **attiva in configurazione e non eseguita dal motore** —
aggiunta dopo l'avvio. Prima di oggi nessuna superficie lo diceva: la pagina la mostrava attiva e
l'operatore l'avrebbe creduta in funzione.

**Il verdetto di misurabilità, e la sua conseguenza.** `/ensemble` sulla corsia 3 dice
*«0 / 1 misurabili — NESSUNA misura è interpretabile in questo momento»*, con 5 trade su 20 richiesti
e il ritmo atteso non dichiarabile. **Questo chiude I13(c)**: il freno automatico per gamba non si
costruisce su una misura che non esiste. Misurare prima di agire vuol dire anche accettare che la
misura dica di non agire.

**I11 sull'app vera**: la gamba della corsia 3 dichiara *«frequenza attesa non derivabile (finestra
di holdout assente)»* — schierata prima che il campo esistesse. È esattamente il gate L2: campo
assente ⇒ nessuno agisce, **e lo si dichiara**.

**AF2b, le tre condizioni provate insieme.** Scritto `7, 5, 5, pippo, -3` nel campo delle corsie
autorizzate e salvato: sul disco è finito `[5, 7]` — duplicato rimosso, testo scartato, negativo
scartato, ordinato. Un refuso non può allargare i permessi. E con due corsie autorizzate ma il
dry-run acceso la sonda continua a dire **DRY-RUN**, non «esecuzione attiva»: le tre condizioni sono
davvero congiunte, e la bugia del 2026-08-18 non si è ripresentata spostata di un flag. Il campo è
stato rimesso a vuoto a fine collaudo.

**E un risultato che vale il livello 4 di I6**: con `Drift:Enabled=true` e il filtro per stage, la
sonda dichiara *«acceso ma NESSUN modello negli stage sorvegliati (Champion/Challenger)»* — zero
allarmi invece dei 151 su 153 di prima. Il gate senza soggetto ora si accende **insieme** al suo
soggetto.

### Livello 4 eseguito (2026-08-19, sull'app vera col database e il motore reali)

`procione-main` fermato, worktree avviato col profilo `procione-reale`, tutto ripristinato a fine
sessione. **La riga che vale l'intera Fase 0**, dal log d'avvio sui dati veri:

> `AGENTI AUTONOMI ATTIVI in questo processo — Campaign Planner: ACCESO E OPERANTE — 1 campagne
> abilitate, 1 in attesa di trigger · Orchestratore di flotta: ACCESO E OPERANTE — 5 corsie sotto
> governo, in DRY-RUN · Comitato AI: ACCESO MA INERTE — 3/3 provider con chiave, ma ZERO voti negli
> ultimi 14 giorni · Drift feature ML: SPENTO — ritiro automatico del Champion ARMATO ma SENZA
> SOGGETTO (Champion in carica: 0)`

Ogni numero coincide con quanto l'analisi aveva dedotto dal database. **E poi una conferma che non
avevo cercato**: pochi minuti dopo, la card diceva «1 in rotazione» invece di «1 in attesa di
trigger» — la campagna si era **svegliata da sola** fra le due letture. È esattamente il
comportamento che la sonda esiste per rendere visibile, dimostrato dal vivo.

| Item | Esito al livello 4 |
|---|---|
| I1 · I2 | ✅ card viva, quattro stati corretti, «Champion in carica: 0» col conteggio vero, manopole del ritiro **modificabili** (la correzione della revisione) |
| I3 | ✅ «NESSUN TETTO IN VIGORE» coi numeri reali: 310.805 token nel mese, **≈17.267 al giorno** — la base per scegliere un tetto, che prima non esisteva |
| I4 | ✅ «Budget del canale: 2/20 nell'ultima ora (18 prima del silenzio)» — e quei 2 erano le notifiche del watchdog per il mio stop/start: il contatore segue la realtà |
| I6 (a) | ✅ **un tick costa ~39 secondi** su 155 modelli, 31.000 candele recenti, 620 feature. Ogni 6 ore, sul database condiviso con motore e ingestion |
| I6 (b) | ⚠️ **il ramo di salto non si è attivato**: sui dati veri nessun modello va saltato. È l'esito desiderato (la guardia non spara a vuoto) ma significa che la capacità di fallire resta provata dai test, non dal campo |
| I6 (d) | ✅ blocco in Home con «151 modelli ML con feature in deriva», **accanto** a quello dei fattori — la prova che i due sono indipendenti e non incatenati |
| I6 (e) | ✅ card delle soglie viva |

**Due difetti trovati SOLO qui, e nessuno dei due dai test.**

1. **Il migrate-on-startup dichiarava lo schema allineato mentre mancava una colonna.** L'app gira in
   **Release**, e in `bin/Release` la DLL delle migrazioni era ferma al 5 agosto: `GetPendingMigrations`
   sottrae le applicate da quelle **note all'assembly**, quindi la differenza era vuota e il migratore
   ha scritto «Schema del database già allineato». Il guardiano esistente copriva «assembly che espone
   ZERO migrazioni» (versioni EF disallineate) ma non «assembly che ne espone un insieme VECCHIO».
   Aggiunta la terza discriminante, che non è un conteggio ma il **modello**:
   `HasPendingModelChanges()` — se il modello differisce dallo snapshot dell'ultima migrazione nota, o
   manca una migrazione o l'assembly è indietro, e in entrambi i casi lo schema **non** si dichiara
   allineato. È la terza volta che la stessa causa morde in un punto nuovo (dopo `migrations add` e
   `--no-build`), ed è la prima volta che avrebbe potuto rompere la produzione.
2. **Il gate di I5 era vero nel collettore e falso nel prodotto.** «Un istogramma nuovo compare in
   pagina senza modificare alcuna lista»: il collettore lo raccoglieva, ma `/metrics` rendeva solo
   card scritte a mano, quindi `procione.drift.tick_ms` non compariva da nessuna parte. Aggiunta la
   card **«Altre misure raccolte»**, che elenca ogni strumento senza card dedicata — e ora
   `tick_ms` compare da solo. Verificato: 1 istogramma e 15 contatori, nessuna lista da aggiornare.

**Il fatto operativo che il livello 4 ha prodotto, e che va deciso:** eseguito «Esegui check ora» sui
dati veri, **151 modelli su 153 risultano in Alert**. È la prima misura che questo monitor abbia mai
prodotto, e dice che le soglie di prassi generica sono troppo sensibili per queste serie — cioè
esattamente ciò per cui (e) le ha rese amministrabili. **Tararle è la precondizione di (c)**:
accendere il worker con queste soglie produrrebbe un allarme permanente su quasi tutto, che è un
altro modo di non dire nulla.

### Chiusura delle Fasi 0-2 (2026-08-19)

**Le tre code chiuse nell'ultimo giro**, tutte della stessa natura — una superficie che non poteva
dire di no:

- **I8(d) «Prova il comitato»**: una domanda sintetica a menù chiuso, coi voti reali provider per
  provider e **la causa di ogni astensione**. Serve perché il comitato arbitra i pareggi, e un
  pareggio potrebbe non arrivare mai: *una verifica che si può fare solo quando serve non è una
  verifica*. Non tocca la flotta e non entra nel journal; passa dallo stesso guard, dallo stesso
  budget e dallo stesso contratto dei giri veri — provarne una copia dimostrerebbe altro. Zero voti
  validi è colorato come **guasto**, non come esito.
- **I9(a) copertura sentiment per simbolo**: la riga dichiarava una copertura **globale**, e il
  commento diceva che bastava «perché il filtro per ticker avviene al calcolo». È vero, ed è
  esattamente il motivo per cui non bastava: su un simbolo fuori dal vocabolario (BTC, ETH, SOL, BNB,
  XRP, DOGE, ADA) il fattore è nullo su **ogni** barra mentre la pagina prometteva migliaia di
  notizie. Ora il conteggio è per ticker, col confronto **per elemento** e non per sottostringa —
  una `LIKE` conterebbe BTC dentro WBTC, cioè gonfierebbe la copertura proprio dove serve la verità.
- **I10(c) `ExperimentRun` di Kind `Pairs`**: i numeri di `/pairs-trading` morivano col circuito
  Blazor. Il run si apre **prima** del calcolo, così un'esplosione lascia la traccia del tentativo
  invece del nulla, e un run che non ha potuto misurare si chiude **dichiarandolo**.

**Ordine**: I1-I2 (dichiarazioni) → I3-I5 (gli strumenti, prima degli interruttori) → I6-I10 (la
capacità di dire di no: il cuore) → I11 (il denominatore condiviso) → I12-I13 (le azioni, e solo
dietro il denominatore) → I14-I15 (persistenza, insieme al suo lettore) → I16.

> **Fasi 0, 1 e 2 CHIUSE** (2026-08-19), tranne l'accensione del drift che è operativa e non di
> codice. Restano le Fasi 3-6: I11 (il denominatore condiviso «trade attesi dall'holdout»),
> I12-I13 (ritiro per inedia, dedup dei grigi, AF2b, freno per gamba), I14-I15 (`PairCandidate` col
> suo lettore, corpus notizie esentato dalla purge), I16 ≡ F12 (capacità del carry).

### Le due cose operative, ESEGUITE (2026-08-19) — e una decisione cambiata dai fatti

**⚠️ Questo blocco supera quanto scritto sopra sullo stato del file vivo: la sezione `Drift` ORA
ESISTE.**

**`Llm:Budget` valorizzato e in vigore**: `DailyCallLimit=150`, `DailyTokenLimit=250.000`,
`MonthlyTokenLimit=2.000.000`. Sono **tripwire, non budget stretti**: su 18 giorni misurati il giorno
peggiore ha fatto **11 chiamate e 36.341 token** (media ~5,4 e ~18.000). Devono fermare una fuga —
un comitato in loop farebbe migliaia di chiamate — e **mai mordere nell'uso normale**, perché il
layer AI si è già fermato in silenzio una volta per credito esaurito e un tetto stretto ricreerebbe
quel guasto in un'altra forma. Scritti dal pannello e verificato che il processo li abbia **riletti**:
il badge «NESSUN TETTO IN VIGORE» è sparito.

**La sezione `Drift` ora esiste nel file vivo, con `RetireChampionOnAlert=false` esplicito.** Questo
chiude la trappola della sezione assente (il default del POCO è `true`) **indipendentemente da tutto
il resto**, ed è il guadagno immediato.

**`Drift:Enabled` resta `false`, e la ragione ribalta una conclusione precedente di questo
documento.** Verificato sul database prima di accendere: **158 modelli, TUTTI in `Staging`**, e
**nessuna delle 8 corsie ha un riferimento ML**. Quindi i 151 allarmi su 153 erano probabilmente
**CORRETTI** — modelli vecchi di mesi hanno feature davvero derivate. *Il difetto non era la soglia:
era il soggetto.* Ricalibrare le soglie su quella popolazione avrebbe adattato il metro a un campione
irrilevante — ed è quello che stavo per fare.

Rimedio: **`Drift:MonitorStages`** (vuoto = `Champion,Challenger`, col default nel codice per la
trappola del binder che appende gli array). Un gate senza soggetto diventa **un gate che si accende
insieme al soggetto**: oggi 0 modelli sorvegliati, tick a costo trascurabile, e al primo modello
promosso parte da solo su quello. Il pannello dichiara «Modelli sorvegliati: N su M salvati», e la
sonda dello stato agenti conta i **sorvegliati** — «acceso su 158» mentre ne guarda zero sarebbe la
solita rassicurazione.

`Enabled` si accende **insieme al filtro**, cioè al merge della PR e a un riavvio del guscio:
accenderlo sul codice deployato avrebbe fatto proprio ciò che il filtro evita, e infatti **un tick
non filtrato è partito** nei secondi in cui è stato acceso.

> **Trappola nuova della famiglia «appsettings vivo ≠ example»**: i pannelli avviati col profilo
> `procione-reale` scrivono l'`appsettings.json` **del worktree**, non quello del repo principale che
> carica `procione-main`. Le due configurazioni vanno riportate a mano, con un diff chiave-per-chiave
> prima di copiare (backup in `appsettings.json.bak-preI6c`).

Le Fasi 0-2 sono tutte a rischio nullo o basso e **non cambiano una sola decisione operativa**. La
Fase 4 è la sola che ne cambia una, ed è dietro I11 di proposito.

**Non-obiettivi** (motivazione per ciascuno nel PRD): promuovere un Champion per dare un soggetto al
ciclo chiuso (ragionare al contrario, già respinto il 2026-07-28) · `LeverageAdvisor` a caldo (gate
senza soggetto; riapre con ≥1 corsia Futures a ≥20 trade) · esecuzione pairs a due gambe (corsie
mono-simbolo; riapre con F13 superato) · consumo del meta-labeling (misurato: conserva il 2% dei
segnali e **peggiora** il rendimento — amplifica, non crea) · cablaggio dell'OFI (6-34× sotto i
costi) · **G7, parere del comitato accanto al click Live** (il comitato non ha ancora votato una
volta e il click Live non è mai stato esercitato: fuori per intero) · R4 dentro questa ondata (la
finestra si fissa **prima**, o è fabbrica di significatività) · API di configurazione remota per
questi sei (non serve: sono tutti del guscio) · F7 per intero.

**La somma dei carichi**, che nessuna analisi per sottosistema può vedere: Postgres condiviso (I14 è
l'unica proposta con **scrittura permanente**), un solo thread pool per tutti e sei i worker, e le due
risorse che nessuno aveva contato — il budget delle notifiche (I4) e il budget AI (I3), entrambi
condivisi e oggi **entrambi senza tetto vero**. È il motivo per cui stanno in Fase 1.

*Nota di copertura da dichiarare, non da subire*: il guscio in cluster è a **zero repliche**; i worker
periodici vivono nel processo locale avviato al logon. La copertura è **uptime dell'host**, non
«sessioni di lavoro» — ma la gamba di riparazione del watchdog passa da `run-postgres.ps1`, che muore
col cluster giù, cioè proprio quando il watchdog scatta.

---

## Revisione di tutti gli algoritmi (2026-08-20) — le undici decisioni, CHIUSE

Richiesta del proprietario: rivedere **tutti** gli algoritmi, non solo quelli dell'ondata di
integrazione, controllando anche integrazione e configurabilità da UI. Il metodo e i risultati stanno
in `docs/audit/31_REVISIONE_ALGORITMI_2026-08.md`; qui resta ciò che è stato **deciso** e ciò che
resta aperto.

Il difetto grave — il backtest validava una strategia e il motore vivo ne operava un'altra, su due
corsie Paper vive — era già corretto nel commit del giorno prima. Restavano **undici ritrovamenti
confermati e non corretti**, ciascuno perché richiedeva una scelta non tecnica.

### Prima di decidere: ri-ancorare al codice

Undici investigatori, uno per ritrovamento, più uno scettico per ciascuno dei cinque di gravità alta,
incaricato di demolire la caratterizzazione. **Tutti e undici ancora veri a HEAD.** Ma:

- **A5 non era una decisione**: è A1 visto dal lato del consumatore, e si chiude correggendo A1. Le
  decisioni erano dieci.
- **A2 era sopravvalutato e la sua correzione «minima» non era neutra.** Monte Carlo sul CSCV
  reimplementato: su pannello misto il PBO viene 0,505, cioè il valore giusto per un pannello di
  rumore — il gate scatta eccome (un batch reale è stato bloccato a 0,619). Il difetto è di
  **validità**, non di cecità. E «lanciare invece di troncare» spegnerebbe un gate bloccante appena
  una serie ha una candela in meno, cioè aprirebbe gambe verso le corsie.
- **M3 partiva da una premessa sbagliata**: `FundingHistory` è popolata, ma solo da /backtest a leva
  > 1. Nella pipeline entrambe le fasi girano sulla costante, quindi la differenza è secca — selezione
  0, validazione 0,01%/8h — e il difetto non è inerte.

### Le dieci correzioni

| # | decisione | tocca numeri? |
|---|---|---|
| A1+A5 | riscalare la stima log-HAR al timeframe (σ_candela = √(RV_giorno × min_tf/1440)) | i valori mostrati sì (÷4,90 su 1h); il `ratio`, e quindi Level, dosaggio e gate C3, **no** |
| A2 | vietare gli universi a timeframe misti; dichiarare nel log del run la frazione di finestra su cui poggia il PBO | **no** — le campagne sono già a timeframe singolo |
| A3 | dichiarare che «Alloc %» è un peso di CONFRONTO e non dimensiona gli ordini | **no** |
| A4 | `HoldoutMonths` al denominatore dello z **e** `MinSharpeSignificanceZ` 1,0 → **0,35** nello stesso commit | sì, un gate: serve ΔSharpe ≥ 0,61 invece di ≈0,19 |
| M1 | POCO `FactorDriftOptions` + pannello + inventario + regole; il pannello dichiara la **copertura** | no |
| M2 | persistere N e provenienza del DSR; il gate «batti l'incumbent» **rifiuta** confronti fra grandezze diverse | sì, un gate — inerte oggi (nessun Champion) |
| M3 | propagare il funding alla selezione, come [R2] fece per lo slippage | **sì**: gli Sharpe IS/OOS scendono per i long-biased e salgono per gli short |
| M4 | con gli stop resting attivi il piano a fette non si costruisce (fail-closed, regola 4) | no |
| M5 | dichiarare l'incomparabilità del risk-free e mostrarne l'ampiezza (rf/σ) | no |
| M6 | dichiarare gambe **e** sopravvissuti pieni, e quante vengono dalla fascia grigia | no |

**Due numeri di gate cambiano davvero, e vanno tenuti a mente leggendo i run futuri**: la soglia di
significatività del comparatore (A4) e i costi della selezione (M3). I run archiviati restano su
un'altra base di costo: **due generazioni di numeri non confrontabili fra loro.**

### Aperti, e sono scelte non rinvii

- **PBO allineato per DATA** invece che per indice: chiude la metà del difetto che il divieto sui
  timeframe misti non copre, ma cambia il numero di un gate bloccante su tutte le configurazioni in
  uso. Va misurato su un run archiviato **prima** di essere applicato.
- **Bracket ri-armato a ogni fetta**: la correzione vera di M4. Prezzo: fino a 44 round-trip firmati
  in più per piano e il rischio di trigger orfano moltiplicato per le fette, su un percorso che
  nessuno ha mai visto scattare dal vivo.
- **Confronto realizzato-vs-atteso su base di capitale comune** (M5 pieno): colonna nuova, migrazione,
  degrado dichiarato per le gambe già schierate.
- **Collegare i pesi di allocazione alla taglia**: possibile, ma **solo dopo** la parità col backtest.
  Oggi `EnsembleManager.BuildBtConfig` gira ogni gamba a `PositionSizePercent = 100` e non conosce
  pesi per gamba: collegarli senza allineare il backtest rifarebbe la stessa classe di difetto
  corretta poche ore prima sullo specchio della posizione.

### Collaudo in browser (2026-08-20, app vera dopo il merge) — e tre migrazioni ferme da un giorno

Mergiata la PR #99 e riavviato `procione-main`, il collaudo delle sei superfici ha trovato **più di
quanto cercava**: al primo riavvio il migratore ha rifiutato di dichiarare lo schema allineato perché
la DLL delle migrazioni accanto all'eseguibile era **vecchia** (`dotnet build` dell'app non la
ricostruisce). Ricostruito il progetto in `-c Release`, le migrazioni note sono passate da 20 a 24 e
ne sono state applicate **quattro**: la nuova di M2 più **tre ferme dal 19 agosto**
(`AddCampaignPausedUntil`, `AddPairCandidates`, `AddPairSpreadWindows`). Le tabelle
`PairSpreadWindows` e `PairCandidates` **non esistevano nel database vero**: i sottosistemi del Filone
I giravano contro tabelle assenti, per un giorno intero, e l'unica traccia era un `fail:` in un log.

> **Regola operativa nuova**: dopo *ogni* merge che contenga una migrazione,
> `dotnet build ProcioneMGR.Migrations.Postgres -c Release`, poi riavviare, poi **leggere la riga
> `DatabaseMigrator`**. La spia è il numero fra parentesi: se «N note» è più basso del numero di file
> in `Migrations/`, la DLL è vecchia e le migrazioni nuove non verranno applicate.

Le sei superfici sono tutte a posto, coi numeri veri: la copertura del monitor di deriva dichiara
**22,5 giorni su 222 serie**, la z del comparatore è **0,35** letta dalla configurazione viva, gli
esiti delle campagne dicono «nessuna gamba» e la motivazione porta la provenienza («0 sopravvissuti
pieni su 64 candidati»). Zero errori in console, zero errori server.

**Un'osservazione da misurare a parte**: su **164 modelli salvati, nessuno ha un Deflated Sharpe**.
Il gate di M2 è quindi inerte per due ragioni, non una (niente Champion *e* niente DSR), e lo
scrittore della pipeline `PersistMlDeflatedSharpeAsync` sembra non aver mai scritto nulla pur essendo
i modelli quasi tutti chiamati `Pipeline <hash>`. È della famiglia dei gate senza soggetto: va
misurato, non dedotto.

#### Le due anomalie del collaudo, risolte in giornata

**«164 modelli, nessun DSR» non era un guasto.** 50 candidati `Ml` sono stati regolarmente validati e
**tutti bocciati** sull'holdout (Sharpe da −1,06 a −61,89); il DSR si calcola solo per chi supera quel
primo esame, quindi non è mai esistito un numero da scrivere. Il difetto era **una frase**: il commento
del metodo dichiarava di persistere il DSR «anche per i candidati scartati». Ora la **provenienza** si
scrive anche senza numero e /registry mostra «scartato prima del gate» invece di un trattino ambiguo.
**Il DSR resta null di proposito**: calcolarlo anche per gli scartati allargherebbe l'insieme che
alimenta `GreyZone.IsGrey`, cioè ciò che può finire su una corsia — una correzione che sistema una
colonna e apre una porta sul trading non è una correzione.

**«Campagna 2 in WaitingForTrigger» era lo stato giusto, con una promessa sbagliata accanto.** I due
bracci del trigger sono armati (regime nello snapshot + modello attivo su AAVE/USDT 1h; forecast
presente), quindi la campagna può ripartire. Ma `AgentStateProbe` lo deduceva dal solo flag
`RegimeTrigger:Enabled`: con entrambi i bracci ciechi avrebbe continuato a dire «un wake la rimette in
rotazione da solo» di una campagna ferma per sempre. **Il rischio è nato oggi**: fino a stamattina il
braccio volatilità scattava per l'errore di unità di [A5], quindi le sveglie arrivavano comunque e una
cecità non si sarebbe vista. Ora il rilevatore dichiara l'armamento dei bracci e la sonda declassa a
inerte, con la ragione.

*Misura da rifare fra qualche giorno*: nei dieci giorni precedenti il ritmo era di ~6 run/giorno contro
i 4 consentiti dal solo backoff. Se scende verso 4, l'eccedenza era davvero delle sveglie spurie.

#### Backfill del registry e la fascia grigia misurata (2026-08-20, sera)

**Backfill eseguito** sulle 164 righe storiche di `SavedMlModels`, ricostruendo l'esito dagli snapshot
dei run: **50 «validato e scartato prima del gate»**, **114 «mai proposto»** (la correlazione di test
non ha superato `minTestCorrelation`, quindi non sono mai diventati candidati). Additivo e idempotente:
scritto solo dove `DeflatedSharpeSource` era null, nessun DSR toccato. Da qui in avanti la provenienza
la scrive il codice — anche per i modelli che non diventano candidati, che sono la maggioranza.

**«Finestra corta» misurata**: è la bocciatura per **meno di 20 trade nell'holdout di ~5 mesi**, cioè
per mancanza di prove, non di merito — ed è la ragione per cui quei candidati sono *grigi* (`IsGrey`
pretende Sharpe positivo: un grigio che perde è bocciato nel merito). Sui 30 giorni: 1.127 bocciati
così, con 2-19 trade.

**Sono gli unici grigi perché la banda DSR è IRRAGGIUNGIBILE.** 402 candidati sono arrivati al gate
DSR e il **massimo prodotto è 0,773**, contro un pavimento di **0,80**: la seconda porta della fascia
grigia è sopra il tetto di ciò che questo assetto genera. Gate senza strumento, e nessuna superficie lo
diceva. Ora ogni run che misura dei DSR senza raggiungere il pavimento **lo dichiara nel log**.

*Tre leve, misurate e NON applicate* (cambiano cosa arriva su una corsia): abbassare il pavimento
(a 0,75 entrerebbero 3 candidati/mese, a 0,70 quarantanove, sotto è allentare la sicurezza);
**ridurre la griglia di ricerca** — 8.160 combinazioni per run schiacciano ogni DSR via SR\*, ed è
l'unica leva che alza i numeri senza spostare un criterio; allungare l'holdout.

#### Fascia grigia: pavimento a 0,70 (2026-08-20, delega del proprietario)

Delegata la scelta fra le leve, ne è stata applicata **una sola**: `GreyZone.DsrFloor` da **0,80 a
0,70**. Misurato prima di cambiarlo, perché «49 candidati in più» non dice se qualcuno verrebbe scelto:

| porta della fascia grigia | nel pool | **schierati davvero** | trade medi | Sharpe WF medio |
|---|---|---|---|---|
| finestra corta (l'unica di prima) | 1.127 | 290 | 10,7 | 1,43 |
| **nuova: banda DSR 0,70–0,95** | 49 | **24** | **25,2** | 1,10 |

**Non è un allentamento, è uno scambio di qualità delle prove**: più del doppio delle osservazioni al
prezzo di uno Sharpe walk-forward più basso — lo scambio che la storia della piattaforma raccomanda,
visto che i campioni sottili con Sharpe alto non sopravvivono al forward test. Un test nuovo
(`IlPavimento_RestaSottoIlMassimoCheLaMacchinaProduce`) diventa rosso se il pavimento risale sopra
0,773, cioè il tetto misurato: è il guardiano che mancava quando la banda è morta in silenzio.

*Sospetto verificato e smontato*: l'ordinamento del pool per Sharpe walk-forward **non** premia i
campioni sottili (10,0 trade medi fra i primi tre contro 11,0 fra gli altri). Non è il problema.

**NON è stata ridotta la griglia di ricerca**, che la prima stesura indicava come «la leva più
onesta» — correzione: ridurre N alza tutti i DSR *per costruzione*, ed è legittimo solo se la
riduzione ha una ragione propria (ridondanza misurata). Farlo per spostare un gate è fabbricare
significatività, l'errore già pagato il 2026-07-20. **NON è stato reso configurabile il pavimento**:
`GreyZone` esiste per essere l'unica definizione, e passarla ai cinque consumatori di fretta ricrea il
difetto che quella classe chiude.

---

## La notte intraday (2026-08-21) — la prima caccia dove il proprietario vuole operare

Richiesta: *«sfrutta la piattaforma al massimo … per investimenti possibilmente intraday»*. Traccia
completa in `docs/audit/32_NOTTE_INTRADAY_2026-08-21.md`.

**La diagnosi che ha riorientato tutto.** In sette giorni l'intera flotta ha chiuso **UN trade**: sei
corsie su otto sono a 4h, e il forward test — l'unico giudice che questa piattaforma riconosca — era a
digiuno. E l'archivio di ricerca, su 13.814 candidati, ne aveva **162 intraday**, l'ultimo di un mese
fa. L'intraday non era stato provato e bocciato: **non era stato provato**, pur avendo 5,16 M candele
a 5m fresche e trenta serie in watchlist.

**Fatto**: due configurazioni nuove (19 = 5m, 20 = 15m), universo ristretto a **10 majors** per tenere
basso N, finestra fissata *prima*, holdout esteso al 2026-08-20, walk-forward riadattato (con i 18
mesi di in-sample delle cacce 1h non ci sarebbe stata nemmeno una finestra).

**Esito**: **0 sopravvissuti su 15 (5m) e su 27 (15m)** — l'undicesimo e il dodicesimo no. Ma tre cose
valgono più dello zero:

1. **La cadenza intraday c'è**: 79, 55, 38, 28 trade nell'holdout contro i 10-20 di 1h/4h. Il problema
   dell'intraday non è la frequenza, è l'edge.
2. **L'holdout ha lavorato benissimo**: `GridMeanReversion` BTC 5m aveva il walk-forward OOS più alto
   del run (**2,98**) e sull'holdout ha fatto **−2,31**. Chi guarda solo il walk-forward schiera la
   peggiore.
3. **Ridurre la ricerca non salva nessuno** — ed è la risposta misurata alla leva rifiutata il giorno
   prima: N è sceso da 6.120 a **3.744** (−39%) e il DSR migliore resta **0,267**. A questi livelli di
   Sharpe il vincolo non è N, è il segnale.

**Corretto strada facendo**: `AltDataSyncStage` faceva fallire l'INTERO run per una violazione di
chiave univoca nel sync delle notizie — metà dottrina applicata (lo snapshot era già protetto, la sync
no). Fail-open sulla diagnostica, regola 4.

**Nessuna corsia è stata modificata.** Avevo preparato tre bracci di forward test intraday, ma due si
basano su candidati che il gate DSR ha respinto: portarli su una corsia, anche solo in Paper, è
erodere un gate con l'argomento «tanto non costa nulla» — la stessa mossa del ridurre la griglia per
spostare una soglia, con un'altra maschera. La lista corta, coi parametri esatti e il verdetto atteso,
è nel documento: è una decisione del proprietario.

---

## La flotta era viva e non poteva aprire (2026-08-21) — difetto D1/D2/D3

*Trovato mentre liberavo e correggevo le corsie. Dettaglio completo in
[`docs/audit/33_FLOTTA_INERTE_2026-08-21.md`](audit/33_FLOTTA_INERTE_2026-08-21.md); il codice è
nella PR #102.*

**Cinque corsie «in esecuzione», feed all'ultima candela, un solo ordine in tutta la flotta in sette
giorni.** Non era il mercato: sulla sola corsia 1 (`RsiOversold` DOT 15m, soglia 20) l'RSI a 14 è
sceso sotto soglia **57 volte** nei 25 giorni dall'avvio.

`_active`, `_creds` e `_filters` li valorizzava **solo `StartAsync`**. `EnsureLoadedAsync` — la
strada di ogni riavvio del processo — restaurava stato, posizioni e piani di esecuzione, ma nessuno
dei tre. La corsia ripartiva viva, riceveva candele, marcava a mercato, onorava gli stop di ciò che
era già aperto, e `foreach (var strat in _active)` girava a vuoto. Per sempre, in silenzio.

Verificato **sul motore vivo**: nei log del pod, cinque `stato ripristinato dal DB (running=True…)`
e zero `Trading engine avviato in modalità…`. Il pod ha ~8 restart al giorno: la flotta non è stata
inerte per sette giorni, lo è stata quasi ininterrottamente.

| | |
|---|---|
| **D1** | la sessione porta le proprie gambe a database (`ActiveStrategiesJson`), **congelate** come già lo sono `Symbol`/`Timeframe`; righe precedenti ⇒ ripiego dalla configurazione, **dichiarato** |
| **D2** | una chiusura Testnet/Live **senza credenziali non si finalizza** più solo localmente: era fail-**open** sulla strada che non può esserlo |
| **D3** | credenziali e filtri del simbolo tornano anch'essi alla ripresa |
| **C1b** | il fill rotto della corsia 2 (−227.340%) resta in tabella: tetto dichiarato a 1000%, si scarta e **si conta** |

**Il rilevatore esisteva e gli era stato insegnato a tacere.** I13a confrontava configurato-vs-
eseguito, ma una nota affermava che «in corsa + lista vuota è impossibile, altrimenti non sarebbe
partito». `IsRunning` lo *restaura* `EnsureLoadedAsync`: quello stato non era impossibile, era il
guasto — e il ramo lo convertiva nel riquadro grigio che rassicura. È il filone E in forma nuova:
non un controllo mancante, un controllo **istruito a scartare il proprio segnale**.

### Il punto che vale più della correzione

**L'immagine del core è ferma al 17 agosto** (`local-9a3e8dbe`). Non contiene
`IPositionMirroringStrategy`: **la correzione dello specchio della posizione non sta girando**, e le
corsie 4 e 5 — lasciate intatte apposta come «unico test pulito» — eseguono la versione rotta.
Tutto ciò che è stato corretto fra il 18 e il 21 agosto vive in master e nel guscio, **non nel
processo che opera**.

Il guscio si aggiorna con l'app, il core si promuove a mano: è il prezzo dell'architettura core
caldo/guscio freddo, e finora non lo pagava nessuno perché nessuno lo guardava. **Serve un
controllo che confronti la revisione del core viva con `HEAD` e lo dica in `/trading`.**

### Aperto

- [ ] **promuovere l'immagine del core** (`build-images-local.ps1 -Targets procionemgr-trading`,
      poi rollout) — senza questo nulla di quanto corretto in quattro giorni sta operando
- [ ] applicare la migrazione `SessionActiveStrategies` (una colonna, additiva, retrocompatibile)
- [ ] riavviare le corsie una per una da `/trading`: serve uno `StartAsync` per lasciare la
      fotografia sulla riga
- [ ] **una sonda «core stantio»**: revisione del pod vs `HEAD`, visibile in `/trading`
- [ ] decidere sulle 5 righe con chiusura precedente all'apertura (`TradeRecords` 159, 248, 269,
      283, 292): causa già corretta il 2026-08-17, i dati restano sporchi. Non le ho toccate —
      bonificare dati di produzione è una decisione del proprietario

---

## La riassegnazione che non si può fare (2026-08-21) — il dodicesimo no

*Documento completo: [`docs/audit/34_RIASSEGNAZIONE_CORSIE_2026-08-21.md`](audit/34_RIASSEGNAZIONE_CORSIE_2026-08-21.md).*

Richiesta: togliere le vecchie coppie dalle corsie e metterci le nuove strategie. Fatto il lavoro —
censimento su cinque assi, tre proposte di allocazione da angoli diversi, tre scettici per proposta,
sintesi. **Dei 738 candidati distinti dell'archivio ne sopravvive zero.**

| passo | criterio | superstiti |
|---|---|---|
| 0 | candidati **distinti** (13.893 righe) | **738** |
| 1 | la terza finestra deve **esistere** | — |
| 2 | tre finestre positive, ≥60 trade sel., ≥18 hold. | **16** |
| 3 | costo ≥2× al denominatore della **barra** | **2** |
| 4 | provenienza risolvibile, non respinta da un gate | **1** |
| 5 | tenuta sui **25 giorni mai visti** | **0** |

È più informativo degli undici no precedenti perché non dice «lo Sharpe non è significativo»: dice
che **tre strumenti di misura non misuravano**.

1. **Il walk-forward non è un walk-forward.** 9.665 righe su 13.893 (69,6%) hanno
   `WalkForwardOosSharpe = round(SelectionSharpe, 2)` — sulla cfg 18 è il **100%**. Per due terzi
   dell'archivio la terza finestra è la prima, arrotondata.
2. **Il PBO è uno scalare di run**, non del candidato: 0 run su 162 hanno più di un valore. Il PBO
   0,079 è condiviso da tutti i 64 candidati di quel run, compresi quelli con holdout −7,78.
3. **Mancava il benchmark banale.** Sei gambe su nove fra quelle proposte non battono «tieni la
   stessa direzione e non fare niente» sulla loro stessa finestra. E quattro su sette erano
   **short-only su corsie Spot**, senza che la proposta se ne accorgesse.

**Il test che ha ucciso l'ultimo superstite**: le finestre di holdout finivano il 2026-07-27. Dal
27/07 a oggi, **14 simboli su 14 sono positivi** (CRV +51%, ADA +30%, XRP +27%…). L'intero menu è
stato selezionato su un mercato che scendeva.

### Fatto

Migrazione applicata · immagine del core `local-a422f7f8` costruita, importata e verificata con
crictl, `newTag` bumpato · **otto corsie ferme, zero posizioni aperte** (obbligatorio: con
`ActiveStrategiesJson` NULL su 8 su 8, il rollout avrebbe risvegliato cinque corsie con gambe
short-biased del regime precedente) · cfg 20 ha finalmente `includeGreyZone` · **finestre di 17 e 18
scongelate** (holdout 26/03 → **21/08**, larghezza invariata) e caccia 1h rilanciata sui 25 giorni
vergini.

### Rettifica su una decisione del 2026-08-20

Il pavimento DSR era stato abbassato a 0,70 con la motivazione «a 0,70 entrano 49 candidati al mese,
24 schierati». **Quelle 49 sono righe, non candidati**: i `CandidateKey` distinti mai in banda sono
**sei**, e due soli ne producono 42. Peggio, quei due erano usciti dalla banda **undici giorni
prima**, il 2026-08-09, quando è entrata la correzione del conteggio tentativi: −0,089 esatti con
Sharpe e trade invariati. Da allora il DSR massimo è **0,659**. L'abbassamento **non ha ammesso
nessuno**. Valore invariato (a entrambe le altezze la porta è chiusa), commento rettificato.

### Aperto

- [ ] `kubectl apply -k infra/k8s/trading` — immagine pronta e pin committato, manca l'apply
- [ ] la **sonda di fedeltà** sulla corsia 5: previsione esatta di 5 trade chiusi nel replay
- [ ] riparare `WalkForwardOosSharpe`
- [ ] **benchmark passivo come gate** della fascia grigia (toglie 6 gambe su 9)
- [ ] `GreyDeployer` deve risolvere sulla `CandidateKey`, non sulla terna
- [ ] cancello di costo al denominatore della barra (oggi sottostimato da 5× a 40×)
- [ ] sonda «core stantio»: le immagini erano indietro di 4, 5 e **11 giorni**

---

## I tre strumenti di misura, riparati (2026-08-22)

*Diagnosi profonda + due scettici per patch + sintesi che doveva risolvere ogni fatale. Dettaglio in
[`docs/audit/34_RIASSEGNAZIONE_CORSIE_2026-08-21.md`](audit/34_RIASSEGNAZIONE_CORSIE_2026-08-21.md).*

### C — la gamba schierata non era quella cliccata (PR #104)

`GreyDeployer` risolveva sulla terna `(strategia, simbolo, timeframe)`, che non è una chiave. Sul run
`b49a4c8c` la riga **preselezionata** (Composite XLM/USDT 4h, Sharpe 1,29 su 8 trade) avrebbe
schierato l'altra specifica della stessa terna, 0,53 su 3 trade. Ora si risolve per
`PipelineCandidateKey`, **fail-closed** su zero e su più di una corrispondenza.

Due rettifiche, entrambe contro di me: le «119 terne ambigue» sono **12 distinte** ricomparse 119
volte (lo stesso errore righe-vs-distinti del pavimento DSR), e **nessuno schieramento sbagliato è
mai avvenuto** — il valore è prospettico.

### A — il walk-forward non era un walk-forward

**Non un campo copiato: un calcolo giusto su un input sbagliato.** `StrategyComposer` passava al
motore l'intera lista di candele, e quell'overload **ignora `config.From/To`**: le N finestre erano N
esecuzioni identiche sul range intero. 9.665 righe su 9.665 della scoperta creativa, zero su 3.833
della discovery classica.

**La conseguenza che nessuno aveva visto: il gate di conferma era una tautologia.** Tutte le campagne
vive hanno `minOosSharpe == minScreenSharpe`, quindi quella fase **non ha mai respinto nulla**.

La cura ovvia — affettare le candele — aveva tre difetti fatali (cache per-istanza del
`SignalCatalog` distrutta; warm-up dei segnali percentile troncato, 125 osservazioni contro le ~122
di una finestra 1d di quattro mesi; zero non neutro reintrodotto). Si **segmenta la curva di equity**
dello screening, che c'era già ed era buttata via: i tre spariscono per costruzione e gli N backtest
**spariscono** invece di moltiplicarsi.

Con essi: la **provenienza** del numero (`WalkForwardSource`, quattro costanti), il campo diventa
nullable — «non misurato» non è 0, e uno zero batte qualunque Sharpe negativo nell'ordinamento — e la
**bonifica dello storico dentro l'indicizzatore**, non in una UPDATE una-tantum: la tabella è
derivata, e `RebuildAsync` la rifà dagli artifact che contengono lo stesso numero falso.

**Ordinamento della riserva grigia → Sharpe di holdout.** Non è una deroga a «holdout = solo
verdetto», che protegge la *promozione*: i grigi sono già bocciati, e il giudice successivo è il
forward test in Paper. È il precedente già documentato due volte (`RejectionDigestBuilder`; il PRD
Memoria-Caccia) e allinea la quarta verità — gli altri tre consumatori del grigio ordinavano già così.

### B — il benchmark passivo: la misura sì, il gate no

Il gate come l'avevo proposto era indifendibile: i numeri che lo giustificavano erano contaminati da
due artefatti **più grandi del margine su cui avrebbe deciso**.

1. **Il funding** è applicato *firmato* — con tasso positivo il long paga e lo short **incassa** — ed
   è una costante inventata (`FundingHistory` non è popolata da nessuno). Il passivo sta a mercato il
   100% della finestra: gli avrebbe regalato ~0,21 Sharpe.
2. **Il risk-free** al 2%/anno è sottratto al **capitale intero** mentre ne è investito il 10%. Il
   drag `rf/σ` vale **0,6-1,6 Sharpe** per il candidato contro ~0,4 per il passivo: da 0,2 a 1,2
   Sharpe di handicap fabbricato.

Corretti quei due, **i due «ribaltamenti» del rapporto 34 evaporano** (+0,15/−0,03 e +0,12/−0,02).
Entra quindi solo la **misura**, a rf = 0 su entrambe le gambe e col passivo senza funding, in un
`try/catch` dedicato: un guasto sulla misura accessoria non deve toccare il verdetto del candidato —
sarebbe la classe del worker morto su un'OCE.

### Aperto, in ordine di valore

- [ ] **Il risk-free al 2% sul capitale intero con il 10% investito distorce OGNI Sharpe della
      piattaforma** di `1,8%/σ` — da 0,3 a 1,5 a seconda della strategia. Toccarlo sposta
      `minHoldoutSharpe`, il pavimento DSR e ogni soglia storica: va misurato su un run archiviato
      **prima** di cambiarlo. È il più grande dei difetti rimasti.
- [ ] **Lo Sharpe del percorso classico è un MASSIMO** su centinaia di combinazioni (media 1,788 in
      archivio contro un holdout medio di −1,350): anche dopo A, l'ordinamento confronta un massimo
      con una media.
- [ ] **`ResearchCandidates` va indicizzata dal run**, non all'apertura di `/research`. È il
      prerequisito per calibrare il gate di B: i due run sui 25 giorni in salita **non sono
      indicizzati**.
- [ ] `CreativeDiscoveryStage` non espone manopole di costo: cambiare i costi del run non cambia la
      scoperta creativa.
- [ ] `BuildOosWindows` scarta la coda più corta di mezza finestra (53 giorni su cfg 18, i più
      recenti). Prima di A non era una perdita, dopo lo diventa.
- [ ] **MKR/USDT non è «serie assente»**: 54.055 candele su 4 timeframe, ma il massimo timestamp è
      **2025-09-15**. Ingestione ferma da 11 mesi senza che nessuno se ne sia accorto.
- [ ] `PipelineApplier` riscrive le corsie `0..lanesUsed−1` **per indice**: se una proposta scende da
      3 gruppi a 1, la corsia 0 viene riscritta e le 1-2 restano con la configurazione vecchia, senza
      che nessuna riga lo dichiari.
- [ ] Il `DisplayName` della gamba grigia non porta l'impronta: in `/trading` due gambe della stessa
      terna su corsie diverse restano indistinguibili.

---

## Il risk-free sottratto a capitale che non lavorava (2026-08-22)

*Documento completo: [`docs/audit/35_RISK_FREE_2026-08-22.md`](audit/35_RISK_FREE_2026-08-22.md).
Trovato lavorando sul benchmark passivo; misurato con tre indagini indipendenti, ciascuna passata
sotto uno scettico, e una sintesi che ha dovuto risolvere sei difetti fatali.*

`Statistics.SharpeRatio` sottraeva un risk-free del 2% ai rendimenti della **curva di equity**, ma è
investito solo `PositionSizePercent` del capitale e il cash non rende nulla. Si addebitava il
costo-opportunità del capitale **intero** a rendimenti che il capitale intero non ha prodotto.

**Perché è doppio conteggio e non prudenza**: accreditare rf a tutta l'equity e poi sottrarlo — la
convenzione contabilmente corretta — dà **esattamente lo stesso Sharpe di rf = 0** (`r'ᵢ = rᵢ + rf_pp`
⇒ `(media′ − rf_pp)/σ = media/σ`), verificato a quattro decimali. Quindi rf = 0 **è** quella
convenzione, a costo zero.

### Quanto costava

Dazio `rf/σ` su 12.967 candidati: mediana **0,545 punti di Sharpe**, q1 0,362, q3 0,749 — **più
dell'intero gate `minHoldoutSharpe = 0,5`** che li giudicava. E **non uniforme**: dentro lo stesso
run, RegimeConditional 0,625 e Composite 0,618 contro Stochastic 0,310 e PriceSmaCross 0,345.
**Penalizzava il doppio proprio il profilo selettivo intraday che questa piattaforma cerca**, quindi
non spostava solo la soglia — **cambiava la classifica**.

Conseguenza più netta: il quintile a σ più bassa aveva **1 sopravvissuto su 2.166**; con rf = 0 ne
avrebbe 92. La piattaforma era **cieca a un quinto del proprio spazio di ricerca**.

**25 chiamanti su 25** prendevano il default. L'unico punto del repo che passava un rf esplicito era
un **test**, e lo passava a **zero**, «per far tornare due numeri che altrimenti non tornavano». E due
convenzioni si incontravano **nella stessa invocazione**: `SelectionValidator` riceve gli Sharpe dei
tentativi a rf = 2% e ricalcola l'osservato a rf = 0.

### La parte non negoziabile

`AutoReapply` e la campagna sono accesi, e riscrivono le corsie da soli. Senza freno, al primo run il
comparatore avrebbe confrontato **due generazioni di numeri**: le 8 gambe schierate valgono 1,934
congelate e ~2,395 ri-misurate (**+23,9% contro un'isteresi del 10%**), e il gate z scatta a 0,55
contro un dazio mediano di 0,545 — **margine del 2%, cioè una coincidenza**. Quindi nello stesso
commit: timbro di convenzione sulla gamba (`ExpectedSharpeAtUtc`) e **rifiuto fail-closed** del
comparatore, che si sblocca ri-applicando l'ensemble.

### Lo storico

Niente bonifica, niente colonna: una **data di taglio** (`MetricsConvention`). La tabella è derivata e
si autodistrugge al primo «Ricostruisci»; la data che serve (`RunCompletedUtc`) c'è già ed è stabile
fra i rebuild; e riscrivere i numeri con l'identità di correzione produrrebbe, dentro una tabella di
misure, valori che nessun run ha mai prodotto — ed è cieca su 926 righe.

### Aperto — decisioni del proprietario, rispondibili con un numero

- [ ] **`minHoldoutSharpe`** (0,5): lasciarla — allargo del 50%, righe oltre il gate da 1.775 a
      **2.670** — o portarla a **0,97** (iso-numerosità: stesso numero di ammessi, ma l'89,6% è la
      stessa identità, 185 righe cambiano perché è cambiato il metro)?
- [ ] **`MinSharpeRealized` 0,8 / `DemoteSharpeThreshold` 0,5**, entrambi con automazione accesa: lo
      Sharpe di corsia sale di **0,36–1,01**. Promuovere diventa più facile, retrocedere più
      difficile — entrambe permissive. Fermi o ritarati?
- [ ] **`Fleet.RetireSharpeThreshold`**: si annunceranno meno ritiri.
- [ ] Il **DSR** non si dichiara e non si ritara: l'osservato era già a rf = 0 e si sposta solo SR*,
      il cui segno non è deducibile per inversione. Si legge sul primo run vero dal log `dsrMax`, ora
      reso incondizionato.

### Una coda che solo il merge poteva far vedere (2026-08-23)

Il benchmark passivo (#105) e il risk-free zero (#106) erano corretti ciascuno per conto suo, e
ciascuno verde sul proprio ramo. Il merge li ha fatti incontrare e un test è caduto:
`EccessoCalcolatoARiskFreeZERO_SuEntrambeLeGambe` chiudeva pretendendo che l'eccesso calcolato a
rf = 0 **differisse** da quello calcolato col *default* — vero quando il default era 2%, tautologia
falsa da quando il default è 0.

Il test aveva ragione a cadere, e la tentazione era di addomesticarlo. Non è stato fatto: il rischio
che quel guardiano copre non è mai stato «il default vale 2%», è **«qualcuno rimette un risk-free
non nullo da qualche parte»**. Quindi il confronto si è spostato su un rf esplicitamente non nullo —
che resta diverso qualunque cosa faccia il default — e si è aggiunta l'asserzione che oggi le due
strade coincidono, così se il default cambiasse di nuovo la riga cadrebbe di nuovo.

Lezione registrata: **la CI verde su due rami non è la CI verde sul merge**, e un'asserzione
formulata contro un *default* invece che contro il *fatto* che vuole proteggere invecchia insieme a
quel default.

---

## Le automazioni dentro un solo programma (2026-08-23)

*Richiesta del proprietario: «il fatto che gli script funzionino tutti separatamente mi infastidisce
un po', soprattutto vedere una finestra PowerShell che si esegue ogni x minuti aprendosi sopra tutti
gli altri lavori».*

### Cosa c'era davvero

Non due meccanismi, **tre**, tutti fuori dalla plancia di comando che pure esisteva già:

| | Cosa | Cosa si vedeva |
|---|---|---|
| task `ProcioneMGR Watchdog` | `powershell.exe -File watchdog.ps1`, `PT5M`, `LogonType=Interactive` | una console davanti a tutto, **288 volte al giorno** |
| task `ProcioneMGR Backup DB` | `powershell.exe -File db-backup.ps1`, 03:30 | idem, una volta a notte |
| `Startup\ProcioneMGR-BringUp.cmd` | `start /min powershell -File bringup.ps1` | una finestra minimizzata a ogni logon |

Il terzo non era nemmeno un'attività pianificata: era il **ripiego non elevato** di
`bringup.ps1 -Register`, depositato in Esecuzione automatica il 2026-08-02 e da allora mai più
guardato da nessuno. È il motivo per cui `procione stato` diceva «BringUp: non registrata» pur
girando a ogni accensione.

### La forma della correzione

La plancia **non riscrive** gli script: li chiama. Quel principio regge anche qui — gli stessi
`watchdog.ps1` e `db-backup.ps1`, gli stessi argomenti, la stessa cadenza. Cambia **chi** li chiama:
un supervisore residente dentro `procione`, che li esegue con l'output **catturato**
(`CreateNoWindow`). Nessuna finestra nasce più da sola.

Il fastidio era la parte visibile. La parte seria è che quegli esiti si potevano leggere **solo**
aprendo il Task Scheduler — ed è esattamente così che il dump notturno poté fallire **sei notti di
fila** (2026-08-17) senza che nessuno se ne accorgesse: il task usciva `1`, e quel codice non lo
leggeva nessuno. Ora l'esito di ogni giro sta nel quadro accanto a tutto il resto, e c'è un log solo
(`procione log supervisore`) dove prima non ce n'era nessuno.

| # | Cosa | Stato |
|---|---|---|
| P1 | La plancia consolidata su `master` (era lavoro non committato in un worktree, su base più vecchia) | fatto |
| P2 | `Schedule` — cadenza a intervallo, giornaliera, all'avvio; **funzioni pure, l'orologio è un parametro** | fatto, 12 test |
| P3 | `Supervisor` — ciclo residente, esclusione a mutex, battito osservabile, log con rotazione, arresto pulito a evento | fatto, verificato dal vivo |
| P4 | `procione attivita migra` — da tre meccanismi a **uno**: registra `ProcioneMGR Plancia` al logon, poi ritira i vecchi | fatto |
| P5 | Copertura completa: nessuno dei 19 script resta fuori (`segreti`, `postgres`, `veglia`, `argocd installa/ripunta`, `esegui`, `strumenti`) | fatto |
| P6 | I verdetti nuovi: supervisore, per-lavoro, doppione, backup | fatto, 21 test |

### Tre decisioni che valeva la pena scrivere

**Il recupero avviene una volta sola.** Un'occorrenza persa perché il PC era spento non si salta
(è la regola `-StartWhenAvailable` del Task Scheduler, e la ragione per cui esiste), ma si tiene
l'**ultima** esecuzione, non l'elenco delle mancate: dopo sei notti spente parte **un** backup, non
sei. E l'ultima esecuzione del backup si legge dal **disco** — il dump più recente — non dalla
memoria del supervisore: è il dato osservabile, e copre il backup fatto a mano, quello fatto dal
vecchio task prima della migrazione, e la macchina che ha cambiato repository.

**Il verdetto non deve mentire nemmeno per eccesso di zelo.** La prima stesura diceva «veglia e
backup NON stanno girando» ogni volta che il supervisore era spento. Su una macchina non ancora
migrata è **falso**: girano, dal Task Scheduler, ed è l'unica cosa che veglia sulla piattaforma. Il
verdetto ora distingue i due casi, e chiama il task vecchio **DOPPIONE** solo quando il supervisore
c'è davvero. Un rosso su una cosa che funziona è il modo più rapido di rendere inutile un quadro.

**Togliere qualcosa che funzionava è un peggioramento travestito da pulizia.** Il bring-up al logon
c'era; il lavoro `avvio` nasce spento perché dura minuti e non è ciò che ci si aspetta aprendo una
console. Quindi la migrazione lo **accende** — ma solo se sta togliendo un bring-up al logon che
esisteva già. E «all'avvio» significa una volta per **sessione**, non per processo: altrimenti
riaprire la plancia farebbe ripartire un bring-up da venti minuti, e la si imparerebbe a non aprire.

### Il lampo che resta

Un'applicazione console avviata dal Task Scheduler riceve una console **dall'host**, e non esiste
flag di avvio che la sopprima: l'unico modo è che il processo la nasconda da sé
(`ShowWindow(GetConsoleWindow(), SW_HIDE)`, in `--muto`). Resta quindi un lampo di qualche
millisecondo, **una volta al logon**, al posto di una finestra ogni cinque minuti per sempre.

---

## Filone J — Dall'aritmetica all'operatività (2026-08-25, diciassettesima ondata)

*Dettaglio, verifiche e decisioni aperte nel
[PRD-AUTONOMIA-OPERATIVA](PRD-AUTONOMIA-OPERATIVA-2026-08.md). Nasce da un ragionamento del
proprietario: automatizzare la validazione invece di abbassare la barra, accendendo i sottosistemi di
autonomia già presenti. La tesi è adottata; le quattro azioni proposte sono state verificate contro il
codice vivo, la configurazione viva e il database reale, e **tre su quattro non fanno quello che si
crede**.*

### La scoperta che cambia la diagnosi

**La piattaforma non è trattenuta da un interruttore: la macchina della ricerca si è fermata e nessuno
se n'è accorto.** Ultimo run completato **2026-08-23 04:25**, oltre 43 ore di silenzio. La campagna 2 è
in `WaitingForTrigger` e da lì **il planner non esce a tempo** (`TryStartNextConfigAsync` è chiamata solo
se `Status == Rotating`): l'unica uscita è un cambio di regime, o l'operatore. E non c'è una seconda
sorgente — **tutte e 13 le `PipelineConfigurations` hanno `ScheduleEnabled = false`**, compresa la 8 che
porta ancora un cron `0 3 * * *` morto.

Quattro premesse corrette dai fatti:

| Il ragionamento dice | Il fatto verificato |
|---|---|
| «attivare `Campaign:Enabled = true`» | **già `true`** da prima del 2026-08-18. Acceso ha prodotto 94 run/30 giorni, 9.723 candidati, **zero sopravvissuti** — ma sono **4 esperimenti rieseguiti 90 volte**: `DateRangesJson` è statico (holdout fermo a 2026-07-27 per 18 giorni) e l'89% delle sveglie era spurio (bug di unità del log-HAR, corretto il 2026-08-20). Costo: **20,9 ore di pipeline/mese** per rifare lo stesso conto |
| «togliendo `DryRun` la flotta schiera» | **nessun effetto**, per tre ragioni indipendenti: `AssignmentArmImplemented = false` (il braccio che *avvia* non è mai stato scritto — esiste solo quello che *ferma*, dal 2026-08-19); `Fleet:ExecutionLanes` assente dal file vivo ⇒ lista vuota ⇒ `CanExecute` falso comunque; e le 5 corsie di flotta sono tutte occupate |
| «schierare sistematicamente i grigi» | la fascia **esiste**: 73 grigi distinti su 788 chiavi (14.855 righe — rapporto 18,9×, il conteggio per righe è un artefatto), **49 freschi**. Ma **67 dei 73 sono passati dalla finestra corta e 6 dalla banda DSR**, tutti prima del 2026-08-09: dopo la correzione della deflazione il DSR massimo è **0,6737**, zero in banda. E lo schieramento automatico non è configurazione: è il **rovesciamento di F5** (`AssignmentQueue` filtra `Band == "pass"`) |
| «nuovi terreni: microstruttura, pairs, LLM» | nessuno è nuovo. Microstruttura **chiusa il 2026-07-28** (il book informa, p 0,005, ma è 6-34× sotto il costo del giro). Pairs: il monitoraggio permanente è **già costruito e spento**, e siede su **174 artefatti `PairScreen` mai indicizzati** (`PairCandidates` a 0 righe). G3 non viola la regola 6, ma alza SR\*, cioè peggiora l'unico vincolo misurato |

### Il percorso automatico esiste già, ed è quello sbagliato

Non passa dalla flotta: passa dalla **campagna**. `DecisionStages` riempie i posti liberi con gambe
grigie (`includeGreyZone`) → `RunApplyEvaluator` applica qualunque raccomandazione con
`EnsembleLegs.Count > 0` e **non guarda i sopravvissuti** → `CampaignPlanner.StartPaperLanesAsync`
**avvia le corsie 0…LanesUsed-1**, cioè l'**impronta**, senza comitato, senza guardia di esposizione e
senza lista esplicita. Le tre condizioni di configurazione sono già tutte vere (`Campaign:Enabled`,
`AutoReapply:Enabled`, `AutoStartPaperLanes`), e un run reale lo dimostra: config 19 (5m), 2026-08-21,
**`Survivors = 0`, `EnsembleLegs = 3`**, tutte grigie. Non ha sparato solo perché `includeGreyZone` è
`false` di default e le uniche due config che lo accendono (19 e 20, 5m e 15m) **non sono in rotazione**:
salvezza per coincidenza, non per progetto.

### E sui gate: il DSR blocca, ma «è il DSR che blocca tutto» è un controllo che rassicura

Il DSR è insuperabile per aritmetica — SR\* 2,65-2,86, servirebbe Sharpe ≈ 5,2-5,5, il massimo prodotto
dal 2026-08-09 è 1,901. Ma `OverfittingGate` **salta i non sopravvissuti**: il DSR esiste per ~4%
dell'archivio, e il massimo Sharpe holdout 4h (**3,1949**, Supertrend ADA/USDT, 17 trade) **non ha DSR**
perché 17 < 20. Il cancello che uccide di più fra i candidati **in guadagno** è il conteggio trade
assoluto (67 chiavi distinte, Sharpe medio 1,12). Renderlo relativo va fatto — ma **sposta le righe al
DSR e riduce la fascia grigia**, e va misurato prima e dopo.

### Il piano

| Fase | # | Cosa | Stato |
|---|---|---|---|
| **0 — rimettere in moto** | J1 | Uscita a tempo da `WaitingForTrigger` + sorgente indipendente dal trigger di regime | aperto |
| | J2 | `DateRangesJson` ancorato ad «adesso»: le finestre devono scorrere | aperto |
| | J3 | Sonda «la ricerca è viva» in Home (run/24h, **candidati distinti** nuovi, età dell'ultimo run) | aperto |
| | J4 | Marcare i 29 run a universo misto già archiviati, oggi indistinguibili dai validi | aperto |
| **1 — spostare il terreno** | J5 | 5m e 15m in rotazione (oggi **0%** contro 100% su 1h/4h, con 75 serie intraday fresche) | aperto |
| | J6 | Gate del conteggio trade relativo alla frequenza attesa, **dichiarando il saldo negativo sui grigi** | aperto |
| | J7 | Indicizzare i 174 artefatti `PairScreen`; accendere `PairsWatch` | aperto |
| **2 — ritiro esigibile** | J8 | Osservazione cumulata persistita: oggi si azzera a ogni riavvio, e la finestra continua più lunga mai raggiunta è **20g 3h contro 21g** | aperto |
| | J9 | Rompere la circolarità dell'inedia: `ExpectedTradesPerMonth` retroattivo | aperto |
| | J10 | Armare il ritiro su **una** corsia: `ExecutionLanes = [7]`, `DryRun = false` | aperto |
| | J11 | La sonda deve dire **perché** non ritira, non solo che è accesa | aperto |
| **3 — il braccio mancante** | J12 | **Chiudere il percorso campagna → impronta prima di aprirne uno voluto** | aperto |
| | J13 | Scrivere `AssignmentArmImplemented`: solo banda `pass`, solo 3-7, una per tick, fail-closed | aperto |
| | J14 | **Il rovesciamento di F5**: schieramento automatico dei grigi nella flotta, con tetto e comitato — subordinato a J8-J10 | aperto, **decisione del proprietario** |
| **4 — onestà degli strumenti** | J15-J21 | Sommario di `GreyZone.cs` falso · «DSR massimo» su campione censurato · `PowerCheckStage` giudica con `All` e stampa il `Max` · `WatchCarryAsync` non può scattare in topologia remota · **corsia 0 morta dal 2026-07-05 con `IsRunning = true`** · `UnrealizedPnl` congelato a 0 su posizioni vive · 5 `TradeRecords` con `ClosedAtUtc < OpenedAtUtc` | aperti |

**Contesto operativo da tenere presente:** i 69 trade dal 19/08 valgono **−779,81 in Paper**, con 6 corsie
su 7 negative e durata mediana fra 2,6 e 14 ore. Con `RetireSharpeThreshold = 0` sono esattamente i
candidati che il criterio condannerebbe — se l'orologio non fosse azzerato (J8).

**Due letture da rettificare, per non finanziare interventi inutili:** `Committee:Providers: []` **non**
significa «nessun votante» (`EffectiveProviders()` ricade su `[Nvidia, Groq, Gemini]`, e la sonda conferma
3/3 con quorum 2) — il comitato tace perché **non gli è mai stata posta una domanda**. E il `Carry` del
file vivo del guscio **non comanda il carry**: con `Trading:UseRemoteTrading = true` il worker gira nel
pod, e il ConfigMap montato porta la sola chiave `Trading__LaneCount`.

### Non-obiettivi

Non si abbassano DSR, PBO o la soglia del gemello nullo (il problema è aritmetico, non di severità) · non
si costruisce raccolta permanente di microstruttura (verdetto 2026-07-28: informa, non paga i costi) ·
non si apre G3 finché il DSR è murato (più tentativi alzano SR\*) · non si tocca `SafetyChecker` e non si
automatizza nulla verso Live · **non si aggiunge `includeGreyZone` alle config in rotazione finché J12
non ha chiuso il percorso campagna → impronta**.
