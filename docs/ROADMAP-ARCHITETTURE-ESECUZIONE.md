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
| 0 — Consolidamento base | ⬜ aperta |
| 1 — TCA + latenza | ⬜ aperta |
| 2 — Correlazione live | ⬜ aperta |
| 3 — Microstruttura (D1/D2/D3) | ⬜ aperta (fonte di verità: ROADMAP-PROFITTO-INTRADAY) |
| 4 — Regime router live | ⬜ aperta |
| 5a — Chandelier ATR | ⬜ aperta, gated |
| 5b — Grid nel catalogo | ⬜ aperta, gated |
