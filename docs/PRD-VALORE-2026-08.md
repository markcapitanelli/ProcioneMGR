# PRD — Valore: costo del calcolo, voce della validazione, igiene dei verdetti (2026-08)

*Undicesima ondata. Nasce dall'[audit di valore 2026-08-01](../AUDIT-VALORE.md) (statica + test
dal vivo sull'app reale col motore in-cluster) e dal benchmark esterno in §1. È il dettaglio del
**Filone F** di [ROADMAP.md](ROADMAP.md). Ogni fase rispetta lo
[standard di verifica a 4 livelli](STANDARD-VERIFICA.md); le fasi di ricerca (F12-F13) passano
dalla macchina di validazione come ogni altra idea — nessuna implementazione a scatola chiusa.*

---

## §0 — La domanda e la risposta

Domanda del proprietario: *l'espansione ha creato valore o bloat/rigidità/lentezza?*
Risposta dell'audit, confermata dal benchmark esterno: **valore sì, con tre debiti precisi** —
(1) spreco di calcolo misurato (2K backtest/15s in Ensemble, count da 4-7s, group-by da 15,2s);
(2) superfici sopravvissute ai propri verdetti (/metrics orfana del processo, DTW sul banco,
catalogo muto sui propri no); (3) una validazione statisticamente corretta ma **muta
sull'aritmetica**, che genera il sospetto di rigidità dove c'è solo matematica del campione.
Questo PRD converte i tre debiti in fasi eseguibili.

## §1 — Benchmark esterno (ricerca 2026-08-01)

### 1a. La catena di validazione è allineata allo standard della letteratura — e il sospetto di rigidità è il sintomo previsto

- **Bailey & López de Prado**: il [Deflated Sharpe Ratio](https://en.wikipedia.org/wiki/Deflated_Sharpe_ratio)
  (JPM 2014) corregge esattamente per selection bias, lunghezza del campione e non-normalità — è
  ciò che la pipeline già applica (DSR>0,95 + PBO + N effettivo). La stessa coppia di autori
  formalizza il **Minimum Track Record Length**
  ([The Sharpe Ratio Efficient Frontier, 2012](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=1821643);
  [slide MinTRL/DSR](http://boston.qwafafew.org/wp-content/uploads/sites/4/2017/01/Lopez_de_Prado_Sharpe.pdf)):
  dato uno Sharpe osservato SR̂, una soglia SR\* e una confidenza α,
  **MinTRL = 1 + [1 − γ₃·SR̂ + ((γ₄−1)/4)·SR̂²] · (z_α / (SR̂ − SR\*))²** (in unità della
  frequenza dei rendimenti; γ₃ skew, γ₄ curtosi). È la formula che rende *calcolabile a priori*
  ciò che la piattaforma ha scoperto empiricamente («Sharpe 1,0 ⇒ ~6,2 anni»): oggi la pipeline
  lo scopre DOPO aver bruciato le ore di backtest, la formula lo dice PRIMA. → **F4**.
- **Harvey, Liu & Zhu** ([...and the Cross-Section of Expected Returns](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2249314);
  [Backtesting, Harvey](https://www.cmegroup.com/content/dam/cmegroup/education/files/backtesting.pdf)):
  con centinaia di fattori provati sugli stessi dati, una scoperta nuova richiede **t > 3,0**, e
  l'haircut corretto è **non lineare** (gli Sharpe marginali vanno quasi azzerati, i più alti
  penalizzati moderatamente — «il rule of thumb del 50% è un errore serio»). I «445k combinazioni
  → 0 significative» e il decimo no del direzionale-tecnico non sono la macchina rotta: sono
  **l'esito che la letteratura prevede** per quella classe su quelle finestre. Conferma anche la
  scelta di non abbassare le soglie: la flessibilità va cercata nell'instradamento (→ F5), non
  nell'ammorbidire il giudice.

### 1b. Carry da funding: reale, maturo, in compressione — misurarne la capacità è urgente

- L'evidenza di settore 2025-26 ([Gate Learn](https://www.gate.com/learn/articles/perpetual-contract-funding-rate-arbitrage/2166),
  [guida Arbitrage Scanner](https://arbitragescanner.io/blog/crypto-funding-rate-arbitrage-strategy-guide),
  [BYDFi/LUXUO 2026](https://www.luxuo.com/business/bydfi-perpetual-futures-why-funding-rates-matter-for-crypto-traders-in-2026.html))
  conferma la strategia come matura e delta-neutra (funding tipico positivo 0,01-0,03%/8h ≈
  10-33% annualizzato nei regimi favorevoli), **ma con capitale istituzionale in aumento e
  rendimenti in compressione: i basis trade che a inizio 2024 pagavano ~25% oggi pagano <5%**
  (fonte survey stat-arb sotto). Rischio principale documentato: il flip del funding a negativo —
  che la soglia d'ingresso (>5%) e l'assetto Paper della piattaforma già gestiscono. Implicazione
  operativa: l'edge positivo va **dimensionato ora** (capacità, decadimento, universo simboli su
  entrambi gli exchange) prima che la compressione lo raggiunga. → **F12**.

### 1c. Market-neutral su coppie: la letteratura concorda con la diagnosi interna

- Studi 2024-25 su coppie cointegrate crypto ([survey WNE 2025](https://www.wne.uw.edu.pl/application/files/5317/5698/2606/WNE_WP482.pdf),
  [copula pairs, Financial Innovation 2024](https://link.springer.com/article/10.1186/s40854-024-00702-7),
  [studio 2026](https://ijsra.net/sites/default/files/fulltext_pdf/IJSRA-2026-0283.pdf))
  riportano Sharpe 1,6-2,45 (BTC-ETH 2,45) con bassa correlazione al mercato. **Cautela
  dichiarata**: gli Sharpe accademici sono tipicamente pre-costi/pre-slippage e vanno rimisurati
  con i costi propri — la piattaforma ha già lo strumento (pairs Kalman passato in forward test,
  √-impact, turnover-aware). Il punto non è «implementare il paper»: è che la classe
  market-neutral — la diagnosi interna della roadmap intraday — è l'unica oltre al carry con
  supporto esterno indipendente. → **F13**.

### 1d. Cosa il benchmark NON cambia

Nessuna fonte giustifica: abbassare DSR/PBO, automatizzare oltre Paper, o fidarsi di uno Sharpe
non rimisurato coi propri costi. Le priorità di performance (cache, stima dei count, max
indicizzati) sono pratica standard e non richiedono riferimenti.

---

## §2 — Roadmap Filone F (tabella esecutiva)

Quattro gruppi, ordinati per rapporto valore/costo. Stati: `aperto` → `in corso` → `fatto`.

### F-P — Prestazioni (il calcolo che non compra niente)

| # | Cosa | Accettazione | Stato |
|---|---|---|---|
| F1 | **Cache della simulazione ensemble** + refresh a corsa unica: `GetStatusAsync`/`GetPerformanceAsync` derivano dalla STESSA `SimulateAsync`, cache in `EnsembleManager` con chiave (hash config, timestamp ultima candela), poll UI 15s→60s | refresh a cache calda senza alcun `RunBacktestAsync` nei log; invalidazione a candela nuova/config cambiata coperta da test | aperto |
| F2 | **Home senza seq scan**: `LongCountAsync()` → stima `pg_class.reltuples` (o cache 15 min) | Home apre senza query >100ms sul count; il numero mostrato dichiara «~» | aperto |
| F3 | **Freschezza per-serie indicizzata**: il `GROUP BY` full-table di `SeriesFreshnessWatchWorker` → loop sulle serie in watchlist con `max(TimestampUtc)` su indice (o tabella riassunto aggiornata dall'upsert) | durata del check da 15,2s a <2s, misurata nei log; stessi verdetti di freschezza sulle 221+7 serie reali (livello 3) | aperto |

### F-V — La validazione che parla (nessuna soglia toccata)

| # | Cosa | Accettazione | Stato |
|---|---|---|---|
| F4 | **Power check MinTRL** come stage iniziale della pipeline (e nota in UI Backtest/Optimization): con finestra, timeframe e N tentativi previsti, calcola lo Sharpe minimo che può superare DSR e lo DICHIARA prima dei backtest; se irraggiungibile, il run parte solo con conferma esplicita | riproduce l'ancora nota: Sharpe 1,0 su ~4 mesi ⇒ MinTRL ~6 anni (entro tolleranza); il report del run mostra la riga di potenza; test unitario contro la formula Bailey-LdP | aperto |
| F5 | **Fascia grigia → forward Paper**: candidati con gemello nullo battuto e DSR in [0,80–0,95) non finiscono nel cestino ma in una proposta esplicita «candidabile al forward test Paper» con bottone di assegnazione corsia; MAI automatico, MAI oltre Paper | il verdetto del run distingue tre esiti (promosso / fascia grigia proponibile / scartato); l'assegnazione richiede click umano e passa dai flussi corsia esistenti (lease, quarantena, isteresi invariati) | aperto |
| F6 | **Ordine del gemello nullo garantito**: verifica che `NullTwinValidation` (200 backtest/candidato) giri SOLO sui sopravvissuti dell'holdout; se già così, scriverlo nel doc dello stage e in un test d'ordine; se no, correggerlo | test che conta i candidati giudicati = sopravvissuti holdout, mai il batch intero | aperto |

### F-O — Osservabilità e igiene dei verdetti

| # | Cosa | Accettazione | Stato |
|---|---|---|---|
| F7 | **/metrics ricollegata al motore**: i contatori di trading vengono dal processo giusto (scrape dell'endpoint metrics del core via port-forward 18092, o lettura della tabella condivisa); sezione «guscio» separata e etichettata | con motore in-cluster e corsie attive la pagina mostra numeri ≠ 0 coerenti con l'audit log (livello 4: aperta sull'app vera) | aperto |
| F8 | **Micro-igiene /trading**: pulsante «Promuovi a Live» sempre-disabilitato → testo statico; riga KPI etichettata per ambito (sessione vs storico) | la pagina non contiene controlli permanentemente disabilitati; ogni KPI dichiara il suo ambito | aperto |
| F9 | **Alert di deriva raggruppati** in Home: per serie con conteggio («DOGE/BTC: 6 fattori spenti»), dettaglio in Feature Selection | max 1 riga per serie in Home; nessuna informazione persa (livello 4 sulla Home vera) | aperto |
| F10 | **Catalogo strategie con verdetti**: meta-strategie in testa ai selettori; sotto le basi direzionali la riga onesta «mai sopravvissuta ai gate da sola — utile come gamba di RegimeConditional/Composite» | i selettori di Backtest/Ensemble mostrano ordine e note; nessuna strategia rimossa (dati vivi le referenziano) | aperto |
| F11 | **DTW nell'archivio dei no**: la sezione in `/market-analysis` passa sotto intestazione «Tecniche misurate e scartate (D4)» con link al verdetto; pattern riusabile per i prossimi no | la sezione esiste, è ripiegata di default, il verdetto è linkato | aperto |

### F-E — L'edge positivo, dimensionato (ricerca dietro gate)

*Orizzonte operativo dichiarato (preferenza proprietario: operazioni rapide):* il carry è
posizione multi-giorno (mediana attesa 3-10 giorni, 2-6 operazioni/mese per gamba) — è la
componente «swing» del portafoglio; la reattività intraday resta affidata alle corsie e al feed.
F13 (pairs) punta a mediana 1-3 giorni, 10-30 operazioni/mese per coppia.

| # | Cosa | Accettazione | Stato |
|---|---|---|---|
| F12 | **Capacità e universo del carry**: misurare sul proprio storico funding (SentimentMetricPoints + dump) la persistenza del premio per simbolo/exchange (Binance+Bitget), la sensibilità alla soglia (5% è ottima o pigra?), e la capacità (a che size il costo di esecuzione mangia il funding, con √-impact già in casa); estendere l'universo Paper ai simboli tier-2 che reggono il gate | report con: premio per simbolo, frequenza dei flip, curva capacità-rendimento; ogni estensione parte in Paper con le regole correnti; verdetto scritto anche se negativo | aperto |
| F13 | **Estensione market-neutral sulle coppie**: dal pairs Kalman già validato a un piccolo universo cross-sectional (top coppie cointegrate per stabilità dello spread), rimisurando gli Sharpe accademici (1,6-2,45 dichiarati) coi costi PROPRI; passa da CPCV+gemello nullo+F4 come ogni idea | gli Sharpe rimisurati coi costi propri sono riportati accanto a quelli di letteratura; sopravvissuti → fascia grigia F5 o forward Paper; verdetto scritto anche se negativo | aperto |

---

## §3 — Dettaglio per fase (file, approccio, verifica)

### F1 — Cache simulazione ensemble
**File**: `Services/Ensemble/EnsembleManager.cs`, `EnsemblePageService.cs`, `Components/Pages/Ensemble.razor`.
**Approccio**: metodo interno `GetOrSimulateAsync(cfg, window)` con cache `(configHash, lastCandleTs, window)`
→ `EnsemblePerformance`; `GetStatusAsync` diventa una proiezione della stessa corsa;
`RefreshAsync` chiama UNA volta. Poll 60s (il timeframe minimo di corsia è 1h). Il
`EnsembleRebalanceWorker` (6h) beneficia gratis.
**Verifica**: L1 test cache (stessa chiave ⇒ zero backtest; candela nuova ⇒ ricalcolo);
L3 integrazione su Postgres reale; L4 browser: log server senza query OHLCV ripetute al poll.
**Nota**: la fee viva (G3, 2026-07-31) entra nell'hash della config — un cambio fee invalida.

### F2 — Home senza seq scan
**File**: `Components/Pages/Home.razor` (riga ~189).
**Approccio**: `SELECT reltuples::bigint FROM pg_class WHERE relname='OhlcvData'` (stima
aggiornata da autovacuum, errore <1% su tabella append-only) con fallback al count esatto se 0;
UI mostra «≈12,7M».
**Verifica**: L1 sul provider; L4 Home aperta, log senza il count da secondi.

### F3 — Freschezza per-serie
**File**: `Services/Ingestion/SeriesFreshnessWatchWorker.cs` (riga ~67), eventualmente `Watchlist`.
**Approccio**: leggere le serie della watchlist (221 righe), per ciascuna
`MAX(TimestampUtc)` filtrato su (Symbol,Timeframe) — usa l'indice esistente (misurato 1-67ms);
`SeriesFreshness` (la regola unica di B2.a) resta invariata: cambia solo COME si ottiene il max.
**Verifica**: L1 parità di verdetti col vecchio percorso su fixture; L3 sulle 228 serie reali
(7 disabilitate incluse); log con durata <2s.

### F4 — Power check MinTRL
**File**: nuovo `Services/Validation/MinTrackRecord.cs` (formula pura, testabile) + stage
`Services/Pipeline/Stages/` + nota in `Backtest.razor`/`Optimization.razor`.
**Approccio**: implementare MinTRL(SR̂, SR\*, α, γ₃, γ₄) e l'inversa (dato T, quale SR serve);
lo stage la valuta con N tentativi previsti dal run (per l'E[max] del DSR) e scrive la riga di
potenza nel report del run; sotto potenza ⇒ warning bloccante con override esplicito.
**Verifica**: L1 contro la formula pubblicata e l'ancora interna (~6,2 anni per SR 1,0);
L2 (rumore): su serie sintetiche senza edge il check non «promette» mai; L4: la riga appare nel
run report della pipeline vera.

### F5 — Fascia grigia → forward Paper
**File**: `Services/Pipeline/Stages/ModelStages.cs` (esiti), `PipelinePageService`/`Pipeline.razor`
(proposta+bottone), riuso di `LanePromoter`/flussi corsia esistenti.
**Approccio**: `OverfittingGate.Apply` già calcola i DSR: aggiungere la classificazione a tre vie
senza toccare le soglie; la proposta porta il candidato, la corsia suggerita e il capitale Paper;
l'assegnazione è un click che passa dai comandi corsia normali (lease/quarantena/isteresi intatti).
**Verifica**: L1 classificazione; L3 end-to-end su run reale; L4: bottone visibile solo in fascia
grigia; audit trail dell'assegnazione.

### F6 — Ordine del gemello nullo
**File**: `Services/Pipeline/PipelineStageCatalog.cs` / doc dello stage.
**Verifica**: L1 test d'ordine (candidati giudicati == sopravvissuti holdout).

### F7 — /metrics ricollegata
**File**: `Components/Pages/Metrics.razor`, `Services/Observability/MetricsCollector.cs`, client
verso il core (endpoint metrics già esposto dal motore; in locale via port-forward 18092).
**Approccio**: sezione «Motore (in-cluster)» alimentata dallo scrape; sezione «Guscio» com'è;
se il core non risponde, la pagina LO DICE (mai zeri muti — è la lezione del Filone E).
**Verifica**: L3 contro contatori noti; L4 sull'app vera con corsie attive: numeri ≠ 0 coerenti.

### F8–F11 — Igiene (micro-fasi)
Interventi da 30-90 minuti l'uno, dettagliati in §2; verifica L4 (pagina vera) per ciascuno.

### F12 — Capacità del carry
**File**: `Services/Carry/` (analisi), tool o pagina di report; nessun cambio al `CarryWorker`
finché il report non lo giustifica.
**Approccio**: dal proprio storico funding: distribuzione del premio per simbolo/exchange,
durata dei regimi positivi, frequenza dei flip; curva capacità (size → costo esecuzione con
√-impact vs funding raccolto); confronto soglia 5% vs alternative SOLO out-of-sample.
**Verifica**: L1 su fixture; L2 controllo sul rumore (funding permutato ⇒ nessun «premio»);
L3 su storico reale; verdetto scritto anche se l'esito è «la soglia attuale è già ottima».

### F13 — Market-neutral esteso
**File**: `Services/PairsTrading/` esistente; universo da `Services/TimeSeries/EngleGranger`+Kalman.
**Approccio**: selezione coppie per stabilità (metà campione), stima su CPCV, costi propri
(fee+slippage+√-impact), F4 power check PRIMA di allargare i tentativi; sopravvissuti → F5.
**Verifica**: standard completo dei 4 livelli; lo Sharpe di letteratura è il riferimento da
battere in onestà, non da eguagliare in valore.

---

## §4 — Ordine di esecuzione e non-obiettivi

**Ordine consigliato** (valore/costo, dipendenze):
1. **F1+F2+F3** (una giornata: si ripagano per sempre e liberano il server);
2. **F4** poi **F5** (la validazione che parla, poi che instrada; F5 dipende da F4 per il report a tre vie);
3. **F7** e micro-fasi **F8-F11** (igiene, indipendenti, parallelizzabili);
4. **F6** (verifica rapida, in coda a un giro pipeline);
5. **F12** poi **F13** (ricerca: prima dimensionare l'edge che c'è, poi cercarne uno adiacente).

**Non-obiettivi espliciti**:
- abbassare DSR/PBO o la soglia del gemello nullo (il benchmark esterno CONFERMA le soglie);
- qualunque automatismo oltre Paper (F5 è una proposta con click umano, non una promozione);
- implementare gli Sharpe di letteratura senza rimisurarli coi costi propri (F13 nasce per questo);
- rifare la UI in grande (M6 dell'audit — fusioni di pagine — resta opzionale e fuori da questo PRD);
- toccare il motore in-cluster per F1-F3 (sono tutte modifiche lato guscio).

**Definition of done del filone**: F1-F11 fatti con verifiche ai livelli dichiarati; F12-F13
hanno un VERDETTO scritto (positivo o negativo — un no pulito chiude la fase esattamente come un
sì); `docs/ROADMAP.md` aggiornata riga per riga; questo PRD marcato chiuso in testa.
