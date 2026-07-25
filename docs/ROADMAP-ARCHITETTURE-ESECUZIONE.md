# Roadmap Architetture di Esecuzione — 2026-07

**Sesta roadmap.** Origine: studio del report *"Architetture di Trading Algoritmico per Crypto e Forex: Dalla Logica Quantitativa all'Esecuzione Avanzata"* (PDF, 29 pagine, fornito il 2026-07-25), confrontato tema per tema con lo stato reale della piattaforma.

**Posizionamento**: questa roadmap è il **complemento ingegneristico** di `docs/ROADMAP-PROFITTO-INTRADAY-2026-07.md`, che resta la roadmap dell'*edge* (dove cercare il profitto). Qui si tratta di *come* la piattaforma esegue, misura e protegge — infrastruttura, qualità d'esecuzione, rischio di portafoglio. Nessun item dei tier D/F/E/M viene duplicato: dove i due documenti si toccano (Fase 3), questa roadmap rimanda a quella.

> **Nota sulla base di codice.** Salvo diversa indicazione, "esiste" significa: esiste sul branch di ricerca `claude/roadmap-macchina-ricerca-ad7c3b` (+64 commit su master al 2026-07-25). `master` è indietro di cinque roadmap di lavoro (feed real-time R1, costi onesti R2, modalità semplice R3, macchina-ricerca, frontiere). Da qui la Fase 0.

---

## 1. Origine e metodo

Il PDF è un survey generalista di buona qualità: strategie (analisi tecnica, pattern/ORB, ML, RL, regime-switching, grid, order flow), infrastruttura (WebSocket, latenza e percentile P99.9, costi maker/taker, Smart Order Routing), gestione del rischio (sizing 1-2% per trade, stop ATR-based, kill-switch, limiti di correlazione) e validazione (Walk-Forward, metriche risk-adjusted, anti-overfitting, backtest con costi realistici).

Il metodo di questa roadmap è lo stesso delle precedenti: **prima il verdetto onesto** su cosa la piattaforma copre già (spesso oltre il livello del PDF), **poi le azioni** solo sui gap reali, verificati nel codice — non sui titoli dei capitoli. La verifica è stata fatta file per file il 2026-07-25; i percorsi citati sotto sono quelli reali.

---

## 2. Il verdetto tema per tema

| Tema del PDF | Stato piattaforma | Azione |
|---|---|---|
| Validazione: WFA, metriche, anti-overfitting | ✅ FATTO e oltre (CPCV, DSR, PBO, holdout, gemello sintetico) | Nessuna — conferma le scelte fatte |
| Sizing 1-2%, kill-switch, limiti di perdita | ✅ FATTO (`SafetyChecker`, `LaneSafetyMonitor` per corsia, failsafe anti-Live su 5 livelli) | Nessuna |
| WebSocket real-time vs REST | ✅ FATTO su branch (feed R1, default-off, **mai verificato acceso**) | **Fase 0** |
| Costi realistici nel backtest (slippage, spread, maker/taker) | ✅ FATTO su branch (R2 slippage onesto + fill maker consapevole della coda) | **Fase 0** |
| Latenza dell'order path, percentile P99 | ❌ ASSENTE — zero occorrenze di "latency" nell'intero repo | **Fase 1** |
| TCA / slippage misurato sui fill reali | ⚠️ PARZIALE — implementation shortfall solo sugli execution job a fette (`TradingEngine`); gli ordini di corsia catturano `FilledPrice` ma lo usano solo come guardia (`FillSanityCheck`), mai come misura di costo | **Fase 1** |
| Restrizioni di correlazione tra posizioni aperte | ❌ ASSENTE — solo limiti scalari; oggi 3 corsie su altcoin altamente correlate a BTC senza alcuna protezione aggregata | **Fase 2** |
| Order flow: order book depth, trade tape, volume profile | ⚠️ PARZIALE — solo taker-buy imbalance dalle klines (`OrderFlowFactors`) + liquidazioni (F4); niente depth, niente tape | **Fase 3** (= tier D roadmap intraday) |
| Regime-switching → selezione strategia | ⚠️ PARZIALE — `RegimeConditionalStrategy` è un router vero ma vive solo nel backtest e usa un proxy SMA, non il `RegimeDetector` K-means; il `TradingEngine` ignora il regime | **Fase 4** |
| Stop/sizing ATR-based diretti | ⚠️ PARZIALE — l'ATR c'è; gli stop passano dai percentili MAE/MFE *condizionati* al regime di ATR (approccio probabilmente superiore al multiplo fisso del PDF) | **Fase 5a** (solo confronto misurato) |
| Grid trading | ❌ ASSENTE — e i numeri del PDF stesso sono deboli (Sharpe 0,38) | **Fase 5b** (opzionale, gated) |
| Smart Order Routing multi-exchange | ⛔ Non applicabile | Respinto (§4) |
| RL/DRL per segnali e risk control | ⛔ Già valutato e scartato | Respinto (§4) |
| Forex e diversificazione crypto-forex | ⛔ Fuori scope per decisione (2026-07-25) | Respinto (§4), direzione futura |

---

## 3. Cosa il PDF conferma (nessuna azione)

Questi capitoli del PDF descrivono cose che la piattaforma fa già, in alcuni casi a un livello più rigoroso di quanto il PDF raccomandi. Vale la pena elencarli perché **confermano scelte architetturali fatte**, non perché richiedano lavoro:

- **Validazione**: il PDF raccomanda la Walk-Forward Analysis come tecnica di punta. La piattaforma ha WFA, CPCV in `/optimization`, Deflated Sharpe Ratio, PBO gate sul genetico, holdout, e un gemello sintetico di controllo (`docs/REPORT-FRONTIERE-2026-07.md`, item I2) che ha *dimostrato* che la pipeline trova un edge piantato quando esiste. Il monito del PDF sull'ORB "+433%/anno" come probabile curve-fitting è esattamente la filosofia già codificata nel gauntlet anti-overfitting.
- **Gestione del rischio multilivello**: sizing frazionale, kill-switch su equity, limiti giornalieri, filtri di robustezza sul MDD — tutti presenti (`ProcioneMGR/Services/Trading/SafetyChecker.cs`, `SafetyConfiguration`, `LaneInvariantChecker`, profili Prudente/Equilibrato/Dinamico di R3). L'unico buco reale è la correlazione (Fase 2).
- **Metriche di performance**: Sharpe, Sortino, MDD, Profit Factor, Calmar, win rate — tutte già calcolate e usate come obiettivi di ottimizzazione multi-criterio.
- **Stop dinamici basati sulla volatilità**: il PDF propone SL = k×ATR. La piattaforma fa di meglio: `ExcursionAnalyzer` (`ProcioneMGR/Services/Analysis/ExcursionAnalyzer.cs`) calcola SL/TP dai percentili di MAE/MFE *condizionati al regime di volatilità* (terziali dell'ATR% causale d'ingresso), applicati in produzione da `PipelineApplier.ComputeAutoBracketAsync`. Il multiplo fisso di ATR è un caso particolare più rozzo di questo.
- **WebSocket e architettura a eventi**: il feed R1 (`ProcioneMGR/Services/MarketData/WebSocketPriceFeed.cs` + `RealtimePriceWorker`, branch ricerca) sottoscrive `bookTicker` + `kline` su Binance e `ticker` su Bitget, con uscite protettive reattive. Resta solo la verifica dal vivo (Fase 0).
- **Costi di transazione onesti**: R2 ha corretto il difetto (selezione che girava senza slippage) e misurato che il cost drag dipende dal turnover, non dalla risoluzione; il fill maker F-queue (2026-07-24) modella la coda. Il capitolo "costi nascosti" del PDF è già dottrina di casa.
- **Pairs trading e cointegrazione**: implementati, irrigiditi dopo l'audit (log-prezzi, ADF), e misurati onestamente — esito negativo sulle coppie testate. Il PDF li propone; la piattaforma li ha già consumati come ipotesi.

---

## 4. Cosa viene respinto, e perché

- **Smart Order Routing multi-exchange**. Il PDF lo presenta come requisito per la frammentazione crypto. Presuppone più venue utilizzabili: qui la venue operativa è **una** (Bitget; Binance Futures è inutilizzabile dall'EU per MiCA dal 2026-07-01). Un SOR con una sola gamba non instrada nulla. La best execution mono-venue è già coperta dallo slicing TWAP/VWAP/Iceberg/Adaptive con impatto √. Da riesaminare solo se tornasse una seconda venue reale.
- **RL/DRL**. Il PDF lo indica come frontiera per segnali e risk control adattivo. La piattaforma l'ha valutato e **scartato consapevolmente** in QLIB-5 (2026-07-08): rischio di sim-to-real overfitting su un simulatore che non può essere fedele; al suo posto, esecuzione adattiva Almgren-Chriss in forma chiusa. Il PDF non porta argomenti nuovi che cambino quel verdetto.
- **Forex**. Deciso il 2026-07-25: la piattaforma resta crypto-only. Le strategie "trasferibili dal forex" del PDF (mean reversion, trend following, pairs) sono già state portate su crypto e misurate. Aggiungere forex significa nuovi broker, nuove API, nuovi dati e nuova superficie regolamentare, mentre il problema attuale è l'edge, non l'asset class. Resta annotato come **direzione futura** se la diversificazione diventasse prioritaria.
- **Pattern "miracolosi" (ORB +433%, Harris +107%)**. Il PDF stesso li accompagna con l'avvertenza sul curve-fitting. La piattaforma ha già un generatore di candidati (discovery creativa + alpha mining) e un gauntlet che li falsifica: aggiungere pattern specifici a mano sarebbe un passo indietro di metodo.

---

## 5. Le fasi (un PRD ciascuna)

**Invarianti trasversali, validi per ogni fase**: ogni funzione nuova nasce **default-off**; nulla tocca il percorso Live automaticamente (failsafe anti-Live intatto su tutti i livelli); il supervisore AI resta advisory-only; la suite di test resta verde a ogni fase; ogni misura nuova finisce nell'osservabilità esistente (Prometheus/Grafana/Tempo), non in silos nuovi.

---

### Fase 0 — Consolidamento della base

**Obiettivo.** Una sola base di codice: tutto il lavoro di ricerca in `master`, feed real-time verificato acceso.

**Perché.** Cinque roadmap di lavoro (R1/R2/R3, macchina-ricerca, frontiere) vivono su `claude/roadmap-macchina-ricerca-ad7c3b` (+64 commit) mentre `claude/procione-trading-bot-roadmap-faa381` (+38) è divergente. Ogni fase successiva presuppone quel codice. Costruire sopra due rami divergenti moltiplica i conflitti a ogni giorno che passa.

**Scope (in).**
1. Verificare cosa contiene `faa381` che non sia già in `ad7c3b` (in particolare i 4 commit "tre punti sospesi": rimozione LastOrderUtc, cointegrazione sui log, modello di fill maker — potrebbero essere già ricostruiti in `ad7c3b`); riconciliare cherry-pickando l'eventuale delta.
2. PR di `ad7c3b` verso `master`, suite completa su Testcontainers (serve Docker attivo).
3. Applicare al DB reale la migration `TargetKind` (generata sulla roadmap macchina-ricerca, mai applicata).
4. Verifica dal vivo del feed R1 con flag acceso in Paper (dichiarata "da fare" in `docs/REPORT-REALTIME-FEED-R1.md`): sottoscrizioni attive, candele chiuse coerenti con il REST, uscite protettive reattive che scattano.

**Scope (out).** Nessuna funzione nuova.

**Criteri di accettazione.** `master` contiene le cinque roadmap; suite verde; migration applicata; un log di sessione Paper con feed acceso e almeno un'uscita protettiva esercitata (o simulata a mercato aperto).

**Rischi.** Conflitti di merge se `master` è avanzato nel frattempo (le PR #29/#30/#31 sono già dentro `ad7c3b`? — da verificare al momento del merge); Docker richiesto per la suite.

**Dipendenze.** Nessuna. **Prerequisito di tutte le fasi successive.**

---

### Fase 1 — Qualità d'esecuzione misurata (TCA + latenza)

**Obiettivo.** Ogni ordine reale (Paper/Testnet/Live) produce una misura di *costo* (slippage vs arrival price) e di *latenza* (invio→ack→fill), aggregate a percentili e confrontate col modello di costo usato in selezione.

**Perché (PDF §infrastruttura).** Il PDF insiste su due punti che il repo oggi non copre: i costi nascosti si misurano sui fill reali, non solo si simulano; e la latenza va guardata ai percentili alti (P99), perché è lì che si perde. Stato attuale: l'implementation shortfall esiste **solo** per gli execution job a fette (`ProcioneMGR/Services/Trading/TradingEngine.cs` ~927-932, histogram `procione.execution.slippage_bps`, arrival price fissato in `ProcioneMGR/Services/Trading/Internal/ExecutionSlicePlanner.cs`); gli ordini di corsia normali catturano `FilledPrice` (fills Binance, lookup Bitget) ma lo usano soltanto come guardia di sicurezza (`ProcioneMGR/Services/Trading/Internal/FillSanityCheck.cs`). Latenza: **zero strumentazione** in tutto il repo, `ProcioneMetrics.cs` ha 12 counter e 1 solo histogram.

**Scope (in).**
1. **Arrival price universale**: al momento della decisione di aprire/chiudere, fissare il prezzo di riferimento (ultimo mid/close noto) su *ogni* ordine di corsia, riusando il pattern dell'`ExecutionSlicePlanner`.
2. **Shortfall universale**: a fill avvenuto, emettere `(fill − arrival)/arrival` in bps, con tag corsia/exchange/lato/tipo ordine, sullo stesso histogram esistente (o gemello `procione.trading.slippage_bps`).
3. **Latenza order path**: histogram `procione.trading.order_latency_ms` con fasi separate invio→ack e ack→fill; percentili P50/P95/P99 in Grafana. Strumentare `BinanceClient`/`BitgetClient` al punto di submit.
4. **Modellato vs realizzato**: pannello in `/metrics` che confronta lo slippage assunto in selezione (`PipelineCosts.DefaultSlippagePercent` + fill model F-queue) con quello realizzato per corsia — è il collaudo permanente del modello di costo di R2.
5. Soglia di allerta (riuso canale Telegram esistente): se lo slippage realizzato mediano supera il modellato di un fattore configurabile, notifica one-shot.

**Scope (out).** Ottimizzazione della latenza (co-location, riscritture di client): prima si misura. Nessuna modifica al comportamento degli ordini.

**Criteri di accettazione.** In Paper con feed acceso: ogni ordine chiuso ha shortfall e latenza registrati; `/metrics` mostra percentili e confronto modellato-vs-realizzato; test unitari sul calcolo (segno corretto lato short compreso); suite verde.

**Rischi.** Bassi — è strumentazione pura, nessun path decisionale toccato. Attenzione al segno dello shortfall sugli short (già gestito nel codice degli execution job: copiarne la convenzione).

**Dipendenze.** Fase 0 (il confronto col fill model F-queue richiede il branch ricerca; la verifica seria richiede il feed R1 acceso).

---

### Fase 2 — Rischio di portafoglio correlazione-aware

**Obiettivo.** Un limite runtime sull'esposizione aggregata *correlata* tra corsie: N posizioni su asset che si muovono insieme contano come una posizione grande, non come N piccole.

**Perché (PDF §rischio).** Il PDF elenca le "restrizioni sulla correlazione tra posizioni aperte" tra i controlli built-in di una strategia sistematica. Stato attuale: **tutti i limiti live sono scalari e ciechi alla correlazione** — `MaxTotalExposurePercent` (50%) e `MaxOpenPositions` (5) in `ProcioneMGR/Services/Trading/SafetyChecker.cs`, `MaxExposureCapitalMultiple` (2x) per singola corsia in `LaneInvariantChecker`. Le corsie attive girano su altcoin ad alta correlazione con BTC: un solo movimento avverso di BTC colpisce tutte insieme, e oggi niente lo intercetta. La matematica c'è già, ma solo in ricerca (classe `Correlation` — Pearson/Spearman/Newey-West — in `ProcioneMGR/Services/Alpha/AlphaModels.cs`).

**Scope (in).**
1. Servizio che mantiene la matrice di correlazione dei rendimenti (finestra rolling configurabile, es. 90 giorni di barre 1h/4h dei simboli con corsie attive), ricalcolata su schedule — riuso della classe `Correlation` esistente, non nuova matematica.
2. Nuovo controllo in `SafetyChecker`: **esposizione correlata effettiva** = somma delle esposizioni pesate per correlazione col candidato (ρ sopra soglia, es. |ρ|>0,7 ⇒ peso pieno); se l'apertura porterebbe l'aggregato oltre `MaxCorrelatedExposurePercent`, l'ordine è rifiutato con audit event dedicato (`CorrelatedExposureRejected`), come già avviene per gli altri rigetti.
3. Fallback conservativo: se la matrice non è calcolabile (dati insufficienti), il controllo **non** blocca ma logga — coerente col principio fail-safe emerso dall'audit del SafetyChecker (mai fail-open su capitale, mai blocco cieco su dati mancanti).
4. Config nei profili di R3 (Prudente più stretto, Dinamico più largo), default-off al primo rilascio, poi opt-in.
5. Badge in `/trading` (o pagina corsie) che mostra l'esposizione correlata corrente vs limite.

**Scope (out).** Ribilanciamento automatico delle posizioni esistenti (il limite agisce solo sulle *aperture*); hedging.

**Criteri di accettazione.** Test unitari: apertura rifiutata quando l'aggregato correlato sfora, accettata sotto soglia, fallback su dati mancanti; audit event visibile; simulazione in Paper con due corsie su simboli correlati che dimostra il rigetto; suite verde.

**Rischi.** Correlazioni crypto sono instabili e tendono a 1 nei crash — proprio per questo il controllo serve; la soglia va scelta per non paralizzare le corsie (calibrare sui dati storici delle corsie attive prima di accenderlo).

**Dipendenze.** Fase 0. Indipendente dalla Fase 1 (possono procedere in parallelo).

---

### Fase 3 — Dati di microstruttura (delega al tier D intraday)

**Obiettivo.** Portare in casa i dati che il PDF chiama "order flow": order book depth, trade tape, volume profile. Questa fase **non ridefinisce** gli item: è l'esecuzione dei tier **D1/D2/D3** di `docs/ROADMAP-PROFITTO-INTRADAY-2026-07.md`, che resta la fonte di verità.

**Perché (PDF §microstruttura).** Il PDF attribuisce all'order flow potere predittivo superiore ai fattori fondamentali per le crypto. La piattaforma oggi ha solo il surrogato aggregato (taker-buy imbalance dalle klines in `ProcioneMGR/Services/Alpha/OrderFlowFactors.cs` + liquidazioni F4). Il feed R1 sottoscrive soltanto `bookTicker` + `kline` (`ProcioneMGR/Services/MarketData/BinanceStreamMapper.cs`): niente depth, niente tape. La roadmap intraday l'aveva già diagnosticato (D2 = "investimento 2027", prerequisito di E4 market making); il PDF lo conferma da fonte indipendente — questo alza la fiducia nella diagnosi, non l'urgenza.

**Scope (in).**
1. **D1** (quick win ⭐ già marcato): completare l'ingestione 1m/5m/15m con i campi order-flow (taker buy volume) sulle coppie con corsie attive.
2. **D2, primo gradino**: estendere il feed R1 con sottoscrizioni `@depth` (snapshot top-N livelli a cadenza fissa, non il full book) e `aggTrade`, persistite su tabelle dedicate con retention configurabile — partendo da 2-3 simboli, per misurare volumi di storage reali prima di allargare. Attenzione alla lezione della purge sentiment (una retention generica ha quasi raso il funding storico): retention *per tipo di dato*, esplicita.
3. **Volume profile** derivato dal tape come vista/servizio di analisi (input per la discovery, non segnale diretto).
4. **D3**: allineamento del funding alle barre intraday.

**Scope (out).** Qualsiasi *strategia* su questi dati (OBI market making = E4, resta gated in roadmap intraday); full order book L3; latenza HFT.

**Criteri di accettazione.** Dopo una settimana di raccolta su simboli pilota: tabelle popolate senza buchi > soglia, storage entro budget dichiarato, un report di qualità dati (gap, duplicati, profondità media). I criteri di *edge* restano quelli della roadmap intraday.

**Rischi.** Volume dati (il tape su coppie liquide è pesante — da qui il pilota ristretto); MiCA può limitare stream Binance dall'EU (come già scoperto con `!forceOrder`: prevedere degrado grazioso e priorità a Bitget dove possibile).

**Dipendenze.** Fase 0 (estende il feed R1). La Fase 1 aiuta (stesso lavoro di strumentazione WebSocket) ma non è bloccante.

---

### Fase 4 — Regime router live

**Obiettivo.** Le corsie possono (opt-in) accendere/spegnere o commutare la strategia in base al regime di mercato rilevato dal `RegimeDetector` K-means vero — non dal proxy SMA.

**Perché (PDF §sintesi progettuale).** Il framework ibrido che il PDF raccomanda — classifica il regime, poi seleziona la strategia — esiste in piattaforma solo a metà: `ProcioneMGR/Services/Backtesting/RegimeConditionalStrategy.cs` è un router vero ma (per suo stesso commento di testa) usa un proxy DB-free (pendenza SMA, dead zone ±0,5%) perché le strategie della factory sono dependency-free by design; e vive solo nel backtest. A runtime il regime agisce solo indirettamente: feature ML opt-in, trigger di ri-caccia (`RegimeChangeDetector`/`RegimeChangeTriggerWorker`), calibrazione bracket (`ExcursionAnalyzer`), attribuzione ensemble, contesto per il supervisore AI. Il `TradingEngine` non consulta mai il regime.

**Scope (in).**
1. **Colmare il divario proxy↔detector**: esporre la classificazione del `RegimeDetector` K-means come servizio interrogabile a runtime per (simbolo, timeframe) con caching e isteresi (stesso pattern dell'`EnsembleComparator`: niente flip-flop su regimi di confine).
2. **Router di corsia**: componente opt-in per corsia con mappa regime→azione (strategia A / strategia B / stand-aside). Le azioni riusano il meccanismo di applicazione configurazioni esistente (`PipelineApplier`), non un percorso nuovo; il cambio avviene solo a posizione piatta o alla chiusura naturale, mai tagliando una posizione aperta.
3. **Validazione prima del Paper**: il valore del routing va dimostrato col gauntlet standard — backtest CPCV della politica di routing (con costi onesti e col K-means fittato solo sull'in-sample, mai sul futuro) contro le stesse strategie senza router. **Se il router non batte il non-router al netto dei costi, la fase si chiude con un report negativo e il codice resta default-off.** Vista la storia delle cacce (cinque esiti negativi onesti), questo esito è concretamente possibile e va messo in conto come esito *valido*.
4. Telemetria: regime corrente per corsia visibile in UI e nel prompt del supervisore (già predisposto).

**Scope (out).** Routing su Live (mai automatico, come da invarianti); regimi nuovi (il K-means esistente basta); RL per la selezione (respinto, §4).

**Criteri di accettazione.** Report di validazione con verdetto esplicito (adotta / non adotta); se adottato: corsia Paper con router acceso per un ciclo di regime completo senza flip-flop (isteresi verificata), audit trail dei cambi; suite verde.

**Rischi.** Il rischio principale è metodologico: leakage del K-means (fit su dati che includono il periodo di test) fabbricherebbe un edge di routing finto — il fit va fatto walk-forward. Rischio secondario: latenza di classificazione dei regimi K-means su barre chiuse (accettabile: il router non è HFT).

**Dipendenze.** Fase 0. Beneficia della Fase 1 (i costi reali misurati rendono onesto il confronto router vs non-router).

---

### Fase 5 — Opzionali gated (solo se le misure li giustificano)

Due candidati a bassa priorità, entrambi con un gate esplicito: si adottano solo se battono l'esistente in un confronto misurato. In caso contrario, il report negativo *è* il deliverable.

**5a — Trailing stop k×ATR (chandelier) vs bracket a percentili.**
Il PDF spinge gli stop ATR-based come "S-Tier". La piattaforma ha già di più (bracket MAE/MFE condizionati al regime ATR), ma il *trailing* dinamico intra-trade a k×ATR non esiste (il trailing attuale è a percentuale). Lavoro: implementare il chandelier come variante di trailing nel backtest, confrontarlo sul gauntlet (stessi costi, stesse strategie) contro i bracket attuali. Adottare solo se migliora in modo robusto; l'ATR e il true range ci sono già (`ProcioneMGR/Services/Indicators/TechnicalIndicatorsService.cs`, `SupertrendStrategy` come riferimento d'uso).

**5b — Strategia Grid nel catalogo.**
Il PDF la cita con numeri modesti (BTC: 8,39%, Sharpe 0,38). Come strategia direzionale è poco promettente; come candidata per regimi range-bound dentro il router di Fase 4 potrebbe avere un senso. Lavoro: aggiungerla a `ProcioneMGR/Services/Backtesting/StrategyFactory.cs` (14ª strategia: griglia di livelli, cicli finiti e restartabili, budget per ciclo) e lasciarla falsificare dal gauntlet come ogni altra. Nessuna aspettativa: è completezza di catalogo, non una scommessa.

**Dipendenze.** 5a: Fase 0. 5b: idealmente dopo la Fase 4 (il suo habitat naturale è il regime range-bound).

---

## 6. Sequenza e priorità

```
Fase 0 (consolidamento)  ── prerequisito di tutto ──────────────► subito
Fase 1 (TCA + latenza)   ── economica, alto valore, zero rischio ► subito dopo la 0
Fase 2 (correlazione)    ── economica, chiude un buco di rischio reale ► parallela alla 1
Fase 3 (microstruttura)  ── media, delega ai tier D intraday ────► dopo 0-1
Fase 4 (regime router)   ── media, esito incerto per design ─────► dopo 0, meglio dopo 1
Fase 5 (opzionali gated) ── bassa, solo su evidenza ─────────────► quando capita
```

Le Fasi 1 e 2 sono il miglior rapporto valore/sforzo dell'intera roadmap: strumentazione e sicurezza, zero dipendenza dall'esistenza di un edge. Le Fasi 3 e 4 costruiscono capacità; il loro ritorno dipende da ciò che le cacce troveranno — coerentemente con `docs/STATO-DELLA-PIATTAFORMA.md`: la piattaforma è matura come strumento di misura, e queste fasi la rendono uno strumento di misura *anche di sé stessa*.

---

## 7. Stato di avanzamento

| Fase | Stato |
|---|---|
| 0 — Consolidamento base | 🟡 **PR #46 aperta** (fast-forward, 64 commit); riconciliazione `faa381` CHIUSA (§7.1); restano migration al DB reale e verifica dal vivo del feed R1 |
| 1 — TCA + latenza | ✅ **FATTA** 2026-07-25 — §8 |
| 2 — Correlazione live | ✅ **FATTA** 2026-07-25, default-off da calibrare — §8 |
| 3 — Microstruttura (D1/D2/D3) | 🔁 **RIVISTA** dopo la misura del costo (§9): come scritta era 124× lo storico esistente |
| 4 — Regime router live | ✅ **FATTO** 2026-07-25, default-off — §10 |
| 5a — Chandelier ATR | ✅ **FATTO e MISURATO**: il gate dice **no** — §10.2 |
| 5b — Grid nel catalogo | ✅ **FATTO**, con una correzione di sostanza — §10.3 |

### 7.1 Esito della riconciliazione dei branch

Verificato il 2026-07-25, e l'esito **toglie un dubbio** che pesava sulla pianificazione:
`claude/roadmap-macchina-ricerca-ad7c3b` è discendente diretto di `master`
(`git rev-list --left-right --count master...ad7c3b` → `0  64`), quindi il merge è un
**fast-forward**: nessun conflitto è possibile.

Quanto a `claude/procione-trading-bot-roadmap-faa381`, che sembrava divergente: contiene **un solo**
commit formalmente assente (`da66473`, potatura dei pool Npgsql per il `53300 too many clients`), e
il suo contenuto è **già ricostruito** nel ramo di ricerca — `ProcioneMGR.Tests/Infrastructure/PostgresFixture.cs`
è identico fra i due rami. Tutte le altre differenze sono versioni più vecchie di file poi evoluti.
Nessun cherry-pick da fare: la **PR #32 va chiusa come confluita**, non mergiata.

---

## 8. Prima ondata eseguita (2026-07-25)

### 8.1 Fase 1 — quello che si è scoperto misurando

Il lavoro ha confermato la diagnosi e ne ha aggiunte due che non erano nella roadmap.

**Confermato**: l'implementation shortfall esisteva solo per gli ordini a fette; gli ordini di
corsia catturavano il fill e lo usavano solo come guardia (`FillSanityCheck`), mai come costo.

**Scoperta 1 — la chiusura non aveva una misura mancante, ne aveva una distrutta.** Su quel percorso
`exitPrice` (il prezzo di riferimento della decisione) veniva **sovrascritto** dal prezzo di fill
prima di finire nell'ordine persistito: il termine di paragone spariva prima che qualcuno potesse
usarlo, e nessuna analisi a posteriori avrebbe potuto ricostruirlo. Da qui la scelta di un campo
`ArrivalPrice` esplicito invece di riusare `Price`, che ha già due significati (limite oppure
riferimento).

**Scoperta 2 — in Paper la misura di costo è una tautologia.** Il fill Paper è per costruzione
uguale al prezzo di riferimento: lo slippage onesto che R2 ha portato nella *selezione* non esiste
sul percorso Paper del motore. Per questo `ArrivalPrice` resta null in Paper e la metrica non
registra — un campione di zeri trascinerebbe verso lo zero proprio la statistica che deve dire
quanto costa eseguire. **Resta aperto**: dare a Paper un modello di fill onesto (il F-queue esiste
già nel backtest) è un lavoro a sé, non fatto qui, e va a finire nel backlog della roadmap intraday.

Cosa è entrato: `Order.ArrivalPrice` + `SubmitLatencyMs` (migration `AddOrderExecutionQuality`, due
colonne nullable), `ExecutionQuality` (shortfall segnato come costo su entrambi i lati + misura
della durata della chiamata), cablaggio sui 4 percorsi reali (apertura/chiusura × Spot/Futures),
latenza con esito (`ok`/`rejected`/`network_uncertain`) inclusa di proposito l'attesa del
rate-limiter, `MetricsCollector` generalizzato a più istogrammi con percentili, e in `/metrics` il
pannello **"assunto vs pagato"** più la latenza a P50/P95/P99.

Il verdetto in UI è **asimmetrico di proposito**: un costo reale *minore* dell'assunto è una buona
notizia (la selezione è stata prudente), uno *maggiore* invalida le decisioni già prese — solo il
secondo merita un allarme.

### 8.2 Fase 2 — il limite di correlazione

`CorrelatedExposureGuard` in `ProcioneMGR/Services/Risk/`, agganciato al percorso di apertura dopo i
limiti scalari (l'I/O non si paga per un ordine già rifiutato) e **solo sulle nuove esposizioni**:
le fette 2..K di un piano di esecuzione non ripassano di qui, perché il piano intero è già stato
valutato alla prima.

Due decisioni di merito, entrambe difese da test:

1. **Somma con segno, non in valore assoluto.** Due long correlati sommano rischio, un long e uno
   short correlati lo compensano. Sommare i nozionali in valore assoluto avrebbe fatto risultare una
   copertura genuina come il doppio del rischio: un limite che punisce le coperture spinge verso il
   portafoglio più rischioso, cioè è peggio del non averlo.
2. **Fail-safe verso il permesso.** Se la correlazione non è stimabile (storico corto, simbolo
   nuovo) il guard dichiara la misura non disponibile e lascia passare. È l'opposto della scelta
   fatta sul capitale ≤ 0 nel `SafetyChecker`, e volutamente: lì il dato mancante rende indecidibile
   *ogni* limite percentuale, qui ne rende indecidibile *uno* mentre tutti gli altri reggono.

**Default OFF, e va lasciato OFF finché non è calibrato.** La soglia (50% del capitale aggregato) è
ereditata dal tetto di esposizione per singola corsia, non misurata sulle corsie reali: accenderla a
scatola chiusa rischia di impedire alla seconda corsia di aprire. Il passo mancante è osservare
l'audit `CorrelatedExposureRejected` in simulazione prima di dargli potere.

### 8.3 Cosa resta da fare a mano

- Verifica dal vivo del feed R1 acceso in Paper.
- Calibrare la soglia di correlazione prima di accendere la Fase 2.

*(Le migration `TargetKind` e `AddOrderExecutionQuality` sono state applicate al DB reale il
2026-07-25, dopo il merge di PR #46 e #47: `dotnet ef migrations list` non riporta più pendenze.)*

---

## 9. Fase 3 rivista: il costo misurato cambia il piano

**La Fase 3 come scritta al §5 non si può fare.** Non per prudenza generica: per un numero.

### 9.1 La misura

Aggiunta la fase `tapecost` a `tools/PlatformExpand` (2026-07-25). Non stima nulla a occhio: usa
`OhlcvData.TradeCount`, che è il numero di trade *davvero* avvenuti in ogni barra — cioè esattamente
quante righe produrrebbe uno stream `aggTrade` non aggregato. Il dato era già nel database, gratis,
da quando T0.3 ha smesso di scartare i campi estesi delle klines.

Esito su 30 simboli reali, a fronte di un intero storico OHLCV che pesa **12,4 milioni di righe,
~1,4 GB**:

| Cattura | Costo/anno | Rapporto con tutto lo storico attuale |
|---|---|---|
| Tape grezzo, 3 simboli più liquidi | **171,6 GB** | **124×** |
| Tape grezzo, 30 simboli | 244 GB (2.189 GB ai ritmi di picco) | 174× |
| Tape aggregato a 1s, 30 simboli | 79,3 GB | 57× |
| **Tape aggregato a 10s, 30 simboli** | **7,9 GB** | 5,6× |
| Depth top-5 ogni 1s, 30 simboli | 229,1 GB | 164× |
| **Depth top-5 ogni 10s, 30 simboli** | **22,9 GB** | 16× |

Il tape grezzo dei soli tre simboli più liquidi vale **124 volte** tutto ciò che la piattaforma ha
raccolto in anni. A quel punto non è un'estensione: è un secondo sistema di dati, con i suoi problemi
di backup (il `pg_dump` attuale diventerebbe impraticabile), di vacuum, di indici e di query. Farlo
scivolare dentro come "una tabella in più" degraderebbe la piattaforma esistente — che oggi fa girare
backtest e cacce su un database piccolo e veloce.

### 9.2 Cosa cambia nel piano

Tre correzioni, tutte figlie della tabella sopra.

**1. Aggregare all'origine, non archiviare tick.** Un tape aggregato in barre da 10 secondi costa
7,9 GB/anno su 30 simboli invece di 244. E il punto forte non è il fattore medio (~31×): è che la
compressione **cresce proprio quando il mercato accelera**, cioè quando il tick grezzo esplode. È
questo che rende il costo *prevedibile* — un tetto, non una scommessa sulla volatilità futura.
L'informazione che serve a un segnale di order flow (imbalance firmato, conteggio, volume ai due
lati) sopravvive all'aggregazione; quella che si perde è la sequenza esatta dei tick, che serve solo
a strategie di latenza che questa piattaforma ha già deciso di non fare.

**2. Depth a cadenza fissa, non a ogni aggiornamento.** `@depth` in streaming manda un aggiornamento
per ogni modifica del book: migliaia al secondo. Uno snapshot top-5 ogni 10 secondi costa 22,9 GB/anno
su 30 simboli e risponde alla stessa domanda (com'è messo il book adesso). Su un pilota di 3 simboli:
~2,3 GB/anno, cioè un costo confrontabile con quello che la piattaforma già sostiene.

**3. Pilota a termine con verifica di valore PRIMA di allargare.** Qui c'è un problema di uovo e
gallina onesto: non si può sapere se la microstruttura aggiunge informazione senza raccoglierla, ma
raccoglierla per tutti i simboli per sempre è il costo di cui sopra. La via d'uscita è la stessa
metodologia che la piattaforma applica a ogni ipotesi: **raccogliere il minimo indispensabile per un
tempo definito, poi misurare**.

### 9.3 Fase 3 rivista — piano operativo

| Passo | Cosa | Costo | Gate |
|---|---|---|---|
| **3.0** | Fase `tapecost` per misurare il costo | fatto ✅ | — |
| **3.1** | Tabelle `TradeTapeBars` (10s) e `OrderBookSnapshots` (top-5, 10s), con retention **per tipo** dichiarata esplicitamente | schema | Retention obbligatoria alla creazione: la lezione della purge sentiment (che avrebbe raso il funding storico) dice che una retention generica applicata a dati eterogenei è un bug latente |
| **3.2** | Estensione del feed R1: sottoscrizione `aggTrade` + `depth`, **aggregazione in memoria**, scrittura periodica | ~2,3 GB/anno su 3 simboli | Default-off; pilota su 3 simboli e **90 giorni**, non "sempre" |
| **3.3** | **Misura di valore**: l'imbalance a 10s aggiunge informazione predittiva *oltre* al `TakerBuyVolume` per-candela che già abbiamo? Stesso gauntlet degli altri fattori (IC, CPCV, placebo) | compute | **Se non aggiunge, la Fase 3 si chiude qui con un report negativo** e le tabelle si svuotano. È l'esito più probabile a giudicare dalle cinque cacce precedenti, e va messo in conto come risultato valido |
| **3.4** | Solo se 3.3 è positivo: allargamento simboli + E4 (market making OBI) come da roadmap intraday | da rivalutare | — |

**Il passo 3.3 è il vero contenuto della fase.** Costruire la cattura senza di esso significherebbe
pagare un costo permanente per un'ipotesi mai verificata — esattamente l'errore che la piattaforma
ha imparato a non fare quando ha costruito il gemello sintetico e la frontiera dei costi.

### 9.4 Ordine di lavoro rivisto

Sequenza aggiornata dopo la misura:

1. **Fase 5a** (chandelier ATR) e **5b** (grid) — piccole, isolate nel backtest, nessun rischio per
   la piattaforma: si fanno subito.
2. **Fase 4** (regime router live) — nessun rischio di volume, tocca il percorso decisionale ma
   default-off e validata prima dell'uso.
3. **Fase 3 rivista** (3.1→3.3) — dopo le altre, perché è la sola che lascia un costo permanente
   sulla piattaforma e va decisa con il risultato di 3.3 in mano.




---

## 10. Seconda ondata eseguita (2026-07-25)

### 10.1 Fase 4 — il router di regime, finalmente col K-means vero

Il PDF mette al centro del suo framework ibrido una sequenza precisa: classifica il regime, poi
attiva la strategia adatta a quel regime. In piattaforma esisteva solo a metà —
`RegimeConditionalStrategy` è un router vero ma vive nel backtest e usa un surrogato (pendenza di
una SMA con dead zone), mentre il motore live il regime non lo consultava affatto.

Il commento in testa a quella strategia spiegava anche il perché: le strategie qui sono
*dependency-free* per scelta (la factory è `new`-based), quindi una strategia legata al DB non
potrebbe girare dentro gli sweep dell'ottimizzatore. Quel commento indicava che serviva "nuovo
plumbing". `LaneRegimeRouter` è quel plumbing, costruito però **fuori** dalla strategia: al livello
della corsia, dove il DB c'è già e dove la domanda "chi opera adesso" appartiene naturalmente.

Tre scelte di progetto, tutte difese da test:

1. **È un filtro, non una mutazione.** Non tocca `EnsembleStrategy.IsActive` né alcuno stato:
   risponde a una domanda quando gliela si fa. Mutare la configurazione dell'ensemble avrebbe
   significato litigare col ribilanciamento dell'`EnsembleManager` e lasciare la corsia in uno stato
   che nessuno dei due possiede davvero.
2. **Non tocca le posizioni aperte.** Il filtro agisce solo sulle *aperture*: una posizione già in
   essere va alla sua uscita naturale anche se il regime le cambia sotto. Chiuderla d'imperio
   sarebbe una decisione di trading presa dal router — un'altra cosa da quella che gli si chiede.
3. **Fallisce verso il permesso.** Nessun modello attivo, modello di un'altra serie, candele
   insufficienti, feature non calcolabili, guasto qualsiasi ⇒ regime "non noto" ⇒ tutte le strategie
   operano. Un filtro che fallisse verso il blocco trasformerebbe un'assenza di informazione in una
   decisione di trading, e fermerebbe l'intera corsia per un modello mancante.

Distinzione che vale la pena avere esplicita: una regola con lista di strategie **vuota** significa
"in questo regime la corsia sta ferma" — una decisione — ed è cosa diversa da un regime **senza
regola**, che è un'assenza di configurazione e resta permissivo per default. Sono i due casi che un
router mal disegnato confonde, e la confusione si paga in silenzio.

L'isteresi anti flip-flop non è riscritta: arriva da `IRegimeDetector.LabelFeaturesAsync`, che
applica già la conferma a più candele di `RegimeAssignment`. Un router che cambiasse idea a ogni
barra di confine spegnerebbe e riaccenderebbe le strategie sul rumore.

**Default OFF.** Prima di dare a un K-means il potere di spegnere una strategia dal vivo, quel
potere va guadagnato in validazione — e la validazione richiede un modello di regime attivo sulla
stessa serie della corsia, che oggi va addestrato da `/regimes`.

### 10.2 Fase 5a — il gate ha detto no, ed è un risultato

Il chandelier (trailing a k×ATR) è stato costruito **e misurato**, che era il punto: la roadmap lo
ammetteva solo se avesse battuto l'esistente. Fase `trailcompare` in `tools/PlatformExpand`:
6 simboli × 4 strategie su 4h, costi onesti, 6 varianti di trailing. Criterio dichiarato prima di
guardare i numeri: **frequenza di vittoria fra le combinazioni**, non il caso migliore — a forza di
provare, qualcosa vince sempre.

| Variante | Vittorie su 24 | Rendimento medio | Drawdown medio |
|---|---|---|---|
| nessun trailing | **11** | −16,1% | 72,4% |
| percentuale 3% | 5 | −51,2% | **62,4%** |
| percentuale 5% | 5 | −43,0% | 64,2% |
| chandelier 2×ATR | 1 | −55,2% | 68,2% |
| chandelier 3×ATR | 2 | −49,5% | 70,1% |
| chandelier 4×ATR | 0 | −38,3% | 71,4% |

**Il "S-Tier" del PDF non regge su questi dati**: 3 vittorie su 24 contro le 10 del trailing
percentuale. Il chandelier resta nel motore come opzione disponibile alla caccia — tenerlo non costa
nulla e l'ottimizzatore può sempre sceglierlo — ma non c'è ragione di preferirlo per default.

Il risultato secondario è più interessante del primo: **il trailing fa quello che promette** e lo
paga. Riduce il drawdown medio (72% → 62%) a costo del rendimento. Non è "il trailing è inutile": è
che sta tagliando anche i trade buoni. Con l'onestà d'obbligo: su un insieme di strategie che
perdono tutte, questa graduatoria misura soprattutto *quale stop taglia meglio le perdite*, e andrà
rifatta se e quando una strategia guadagnerà davvero.

### 10.3 Fase 5b — e la correzione che il codice ha imposto

Costruendo la strategia grid è emerso un vincolo che la roadmap non aveva visto: **il motore è a
posizione singola** (`Portfolio` ha un solo stato flat/long/short), mentre un grid vero appoggia
molti ordini limite simultanei e porta più posizioni insieme. Il grid multi-ordine **non è
esprimibile** in questo motore.

A quel punto c'erano due strade oneste: non farlo, oppure farlo e chiamarlo col suo nome. La seconda,
purché il nome non menta — da cui `GridMeanReversion` e non "Grid". Cattura il ciclo finito e
restartabile che è il cuore economico dell'idea (entra a N gradini dall'ancoraggio, raccoglie un
gradino, ricomincia); non cattura la media dei prezzi su più gradini né l'inventario simultaneo, che
sono poi proprio ciò che rende il grid pericoloso quando il laterale finisce. I numeri del PDF
(8,39%, Sharpe 0,38) **non si trasferiscono** e non vanno attribuiti a questa strategia.

Vale la pena averla accanto alla `BollingerMeanReversion` per una differenza deliberata: là la banda
è *adattiva* alla volatilità, qui il gradino è *fisso*. Quale delle due funzioni meglio è una
domanda empirica, ed è la caccia a doverla decidere.

### 10.4 Stato dopo la seconda ondata

Fatte: 0 (merge in master + migration applicate), 1, 2, 4, 5a (con verdetto negativo), 5b.
Aperta: **3 rivista** (§9), che è ora l'unica fase rimasta e la sola che lasci un costo permanente
sulla piattaforma.
