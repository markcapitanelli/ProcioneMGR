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
| AF4c | **Pesi ERC fra corsie, SOLO advisory** (riuso `Services/Portfolio`), `Fleet:CapitalWeights:Enabled=false` | aperto | L2: PnL bianco ⇒ ~equal-weight |
| AF5 | **Continuità H24**: heartbeat incrociato a DB (`HostHeartbeats`, una riga per host, monitor una-notifica-per-transizione, assenza=ignoranza≠guasto; pannello con salvataggio doppio guscio+motore via gRPC, sezione nel contratto `EngineConfigSections`), `scripts/watchdog.ps1` (Task Scheduler 5′, Telegram diretto, auto-riparazione del port-forward prima di gridare, `-Register`), `scripts/bringup.ps1` idempotente (Docker→socat→nodo→pod→port-forward→guscio, `-Register` al logon con ripiego automatico sulla cartella Esecuzione automatica). **Digest giornaliero fatto**: `DailyDigestWorker` (ora LOCALE, `Notifications:Digest` default OFF, pannello dentro Notifiche), sezioni raccolte ognuna in proprio try/catch, chiusura che dichiara l'assenza-come-allarme; anti-doppione in memoria (un doppione raro batte una tabella in più) | **fatto (2026-08-02)** — deploy 5.1-5.3 eseguito la sera stessa | ✅ L1: transizioni heartbeat + scheduling digest (prima/dopo l'ora, già-inviato, avvio tardivo); L3: upsert senza duplicati su Postgres; L4 deploy: watchdog registrato e VERIFICATO, bring-up al logon, il rimedio socat del bringup è servito DAL VIVO al primo deploy (IP del nodo kind cambiato). Fix post-deploy: i `-Register` ora verificano l'esito invece di rassicurare (bug trovato sul campo: Register-ScheduledTask non terminante + messaggio di successo) |

**Non-obiettivi**: auto-Live in ogni forma; LLM che genera strategie/parametri; Hurst come
pilastro; validazione più permissiva (F5 resta a click umano); terzo host. Ordine: AF0 → AF1 →
AF5(1-3) → AF2(DryRun) → AF4a → AF2(esecuzione) → AF5(digest) → AF4b → AF3 → AF4c.
