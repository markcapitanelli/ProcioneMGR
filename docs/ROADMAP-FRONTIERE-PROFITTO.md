# ProcioneMGR — Roadmap "Frontiere di Profitto": domande nuove per una macchina diventata onesta

*2026-07-23. Quarta roadmap di metodo, scritta a valle della chiusura di ROADMAP-MACCHINA-RICERCA.
Ogni item dichiara: l'ipotesi economica (PERCHÉ dovrebbe esserci un edge), l'evidenza esterna,
i dati necessari (già in casa / da accumulare), l'aggancio architetturale verificato sul codice,
l'effort e il criterio di validazione. Nessun item promette rendimento: promette una risposta.*

---

## 0. Premessa: da dove si riparte

Le tre roadmap precedenti hanno costruito una macchina che oggi sa dire la verità: embargo nel
walk-forward, funding FIRMATO dal 2019, slippage onesto nella selezione, CPCV con distribuzione di
percorsi, permutation test temporale, event-study con placebo, e un esperimento di controllo che
dimostra che quando l'edge c'è (piantato) la pipeline lo trova con DSR 1,00.

La stessa macchina ha anche prodotto una lezione costosa e preziosa: **cinque angoli di ricerca
"classica" (cacce a strategia singola su OHLCV, pairs) sono TUTTI negativi su 45 coppie**, e il
filo conduttore è il rapporto fra edge lordo piccolo e costi (REPORT-RICERCA-2026-07). Ripetere
quelle domande è tempo perso — lo dice il §"Cosa NON vale la pena rifare" di quel report.

Questa roadmap quindi cambia **classe di domande**. Tre direttrici:

1. **Dati che gli altri non usano**: la piattaforma possiede serie che il retail medio scarta —
   order flow taker dal 2020, funding firmato dal 2019, eventi rilevati su sei anni di OHLCV,
   breadth interna, coppie /BTC. Gli edge sopravvivono dove pochi guardano.
2. **Rendimenti che non dipendono dalla direzione**: carry (il funding è un FLUSSO, non una
   scommessa), risk premia, overlay di rischio. Dove il rendimento atteso è strutturale, il
   problema si sposta dal "prevedere" al "raccogliere con disciplina".
3. **Il metodo stesso come vantaggio**: quasi nessun retail ha un gate con edge piantato, CPCV e
   permutation. Industrializzarlo (gemello sintetico, I2) trasforma l'onestà in velocità: più
   ipotesi provate per unità di tempo, a parità di rigore.

**Il gate non si negozia** (principio §6 della roadmap precedente): edge piantato → DSR → holdout
→ replica su finestre disgiunte → permutation. Anche per gli item di questa roadmap. Soprattutto
per gli item di questa roadmap.

---

## 1. Inventario degli asset UNICI già in casa (ciò su cui costruire)

| Asset | Profondità | Chi lo usa oggi | Potenziale non estratto |
|---|---|---|---|
| Order flow taker (TakerBuyVolume, TradeCount, QuoteVolume) | 1,7M candele, dal 2020 su 1d/4h/1h × 46 simboli | 2 fattori alpha (IC>0 misurato, ρ≈0 col volume) | segnali di catalogo, crowding per-simbolo (I1) |
| Funding rate FIRMATO | dal 2019-09, ~7.400 eventi × 6 simboli | costo nel backtest (T0.2) | **segnale di posizionamento + carry (F1)** |
| Fear & Greed | dal 2018-02, 3.088 punti | z-score sentiment | feature di regime, ingrediente I1 |
| OI / long-short / taker ratio futures | in accumulo dal 2026-07 (30gg retention → estesa) | z-score sentiment | crowding (F4/I1) — il valore cresce col tempo |
| Coppie /BTC (ETH,SOL,BNB,XRP,DOGE) | 217k candele, 1d/4h dal 2020 | serie di prima classe | **cross-section senza rischio USD (F2)** |
| Eventi di mercato (crash/surge/vol/volume) | derivabili su TUTTI i sei anni | event-study T2.7 | filtri di strategia (F3) |
| Breadth interna (% sopra SMA50) | derivabile su tutta la storia | feature regime opt-in | timing di esposizione (F2/I1) |
| Feed WebSocket real-time (R1) | infrastruttura pronta, default-off | uscite protettive | **stream liquidazioni: dato nuovo gratis (F4)** |
| Gate anti-overfitting completo | — | validazione | **gemello sintetico: ricerca industrializzata (I2)** |

---

## 2. Gli item

### F1 — Il funding come SEGNALE e come CARRY ⭐ (il candidato più forte)

**Ipotesi economica.** Sui perpetual il funding è un flusso pagato dal lato affollato al lato
scarico. È positivo nella stragrande maggioranza del tempo (la letteratura misura >90%: bias
strutturale long del retail crypto). Due conseguenze sfruttabili: (a) **estremi di funding =
affollamento** → rendimento atteso asimmetrico contro la folla (evidenza: BIS "Crypto carry",
Granger-causalità dei funding estremi sui movimenti successivi); (b) **lo short incassa il
funding** → una posizione corta ha un vento in poppa strutturale che il backtest ora misura
correttamente (T0.2), e una posizione delta-neutra (long spot + short perp) lo incassa SENZA
rischio direzionale (cash-and-carry).

**Cosa c'è già**: la serie firmata dal 2019 per 6 simboli in `SentimentMetricPoints`;
`FundingRateLookup`/`FundingHistoryProvider` già nel motore di backtest; il macchinario two-leg
del pairs trading (`PairsBacktestEngine`) per la variante neutra.

**Cosa manca**:
- **F1.a (M)** — segnale di catalogo **"Funding pct"** (id 12): percentile causale del funding
  corrente del simbolo. Aggancio: `SignalCatalog` legge da una serie ESTERNA alle candele — serve
  il piccolo passo architetturale di un contesto opzionale (stessa esigenza di F7: vedi nota
  architetturale §4). Con il segnale in catalogo, il `StrategyComposer` può cacciare ipotesi tipo
  "short SOLO quando il funding è nel decile alto" — la composizione passa dal gate come tutte.
- **F1.b (M/L)** — strategia **FundingCarry** delta-neutra: long spot + short perp sullo stesso
  simbolo quando il funding annualizzato supera una soglia, chiusura quando rientra. Rendimento =
  funding incassato − costi delle due gambe. Aggancio: il backtest riusa la contabilità two-leg
  del pairs + `FundingRateLookup`; l'esecuzione live è VINCOLATA dalla realtà MiCA (Binance
  Futures inutilizzabile per l'utente; Bitget demo) → si valida in Paper/Testnet e si dichiara
  il vincolo, come per tutto il percorso Live.

**Validazione**: backtest 2019→oggi sui 6 simboli col funding storico VERO; l'A/B è naturale
(stessa strategia a funding zero deve rendere ~0 al netto dei costi); gate standard su F1.a.
**Onestà**: il carry lordo medio è noto (~5-15% annuo nei periodi normali, picchi nei rialzi);
il punto aperto è quanto ne sopravvive ai costi delle due gambe — è ESATTAMENTE il tipo di
domanda che la piattaforma ora sa giudicare (frontiera dei costi, R2).

### F2 — Cross-section sulle coppie /BTC: momentum e reversal SENZA rischio dollaro (M)

**Ipotesi economica.** La letteratura trova momentum trasversale a 2-4 settimane e reversal oltre
il mese nelle crypto ("metabolismo più veloce" — Dobrynskaya). Le cacce single-symbol della
piattaforma NON hanno mai posto questa domanda: è una domanda di PANNELLO (comprare i relativi
forti, vendere i relativi deboli). Farlo sulle coppie **/BTC** (già ingerite, 4.9) ha due vantaggi
strutturali: il rischio di mercato (BTC) si elide in gran parte, e il ranking è direttamente il
"chi sta vincendo dentro il mercato interno".

**Cosa c'è già**: 15 serie /BTC di prima classe; la fase `xsection` di PlatformExpand (momentum
trasversale già misurato una volta sul paniere USDT); breadth interna come stato del mercato.

**Cosa manca (M)**: un backtest di ROTAZIONE — ogni N barre, rank delle 5 (poi più) coppie /BTC
per rendimento a lookback L, long top-k / short bottom-k, ribilanciamento con costi pieni.
Aggancio: nuova fase `rotation` in PlatformExpand (riusa `BacktestEngine` per gamba o la
contabilità pairs); NON serve toccare il motore live finché il gate non parla.

**Validazione**: gate standard + la lezione anti-t141: le finestre di valutazione devono essere
temporalmente disgiunte, MAI panieri correlati dentro la stessa finestra. Reversal e momentum
vanno testati come ipotesi SEPARATE (lookback corto vs lungo), non ottimizzati insieme.
**Onestà**: con 5 coppie il cross-section è povero; l'item include l'estensione dell'universo
/BTC (fase `relative` già pronta: è un parametro) prima di giudicare.

### F3 — Eventi come filtri di strategia (S/M — sblocco naturale di T2.7)

**Ipotesi economica.** Crash e vol-spike hanno dinamiche post-evento documentate (rimbalzo a
breve, poi regime di vol alta persistente). T2.7 ha costruito il rilevatore e il giudice
(event-study con placebo); questo item chiude il cerchio SOLO SE il run sul campo giustifica.

**Percorso**: (1) run `eventstudy` su BTC/ETH/SOL a 1d e 1h → CAAR pre/post e p placebo per tipo
di evento; (2) SOLO per i tipi con p < 0,05 e CAAR economicamente sensata dopo i costi, segnale di
catalogo **"Barre dall'ultimo evento X"** (percentile causale della distanza) — il Composer può
allora cacciare "compra il rimbalzo post-crash solo se ...". Aggancio: `MarketEventDetector` è già
causale, il segnale è O(n).

**Validazione**: è l'item più disciplinato — il criterio d'ingresso È il risultato dell'event-study.
Se il placebo dice rumore, l'item muore lì e lo si scrive nel report.

### F4 — Liquidazioni: il dato nuovo che si accumula GRATIS da domani (M)

**Ipotesi economica.** Le cascate di liquidazione sono il meccanismo micro dei crash crypto
(evidenza: leva concentrata + funding estremo → fragilità; la letteratura 2025-26 documenta il
ciclo OI-crescente → funding sbilanciato → cascata). Chi ha la SERIE STORICA delle liquidazioni
per simbolo può: (a) datare le cascate per l'event-study; (b) costruire feature di fragilità
(liquidazioni cumulate vs OI). Il dato è GRATIS sul WebSocket Binance (`forceOrder` stream) — ma
non è storico: **vale solo se si inizia ad accumularlo adesso** (stessa logica dell'accumulo
OI/long-short già attivo).

**Cosa c'è già**: l'infrastruttura WebSocket di R1 (`WebSocketPriceFeed`, `BinanceStreamMapper`,
`RealtimePriceWorker`) con riconnessione e test; `SentimentMetricPoints` come sink naturale.

**Cosa manca (M)**: sottoscrizione `forceOrder` nel mapper Binance + persistenza aggregata per
simbolo/ora (conteggio, nozionale, lato) + retention config. Consumo: fra 3-6 mesi, feature di
fragilità nel composite sentiment e nell'event-study.

**Validazione**: per ora solo igiene del dato (conteggi coerenti con le fonti pubbliche); il
consumo passa dal gate quando c'è abbastanza storia. **Questo item è un investimento nel 2027.**

### F5 — Flussi stablecoin e on-chain keyless (M)

**Ipotesi economica.** L'offerta di stablecoin sugli exchange è potere d'acquisto in attesa;
l'evidenza recente (arXiv 2411.06327) misura che i netflow USDT predicono i rendimenti BTC/ETH a
1-2 ore. Fonti keyless/gratuite esistono (DeFiLlama stablecoins API per l'offerta aggregata,
alternative.me già in uso; The Block/bgeometrics per metriche BTC) — stessa classe di integrazione
del Sentiment 2.0: fonti verificate dal vivo, worker in accumulo, composite.

**Cosa c'è già**: l'intero pattern Sentiment 2.0 (`SentimentSyncWorker`, `SentimentMetricPoints`,
composite z-score con estremi contrarian, feature ML opt-in default OFF).

**Cosa manca (M)**: 1-2 ingestor keyless (offerta stablecoin totale + variazione 7gg; se
disponibile, netflow exchange aggregato), metrica nel composite, feature opt-in. Da verificare DAL
VIVO la reale disponibilità keyless dei netflow (l'offerta aggregata lo è; i netflow per-exchange
spesso no — in tal caso si accumula l'offerta e si dichiara il limite).

**Validazione**: IC della feature a orizzonte 1h-1d sui 6 simboli maggiori; gate standard per ogni
consumo decisionale. **Onestà**: a cadenza giornaliera l'evidenza esterna è più debole che a 1-2h;
la piattaforma opera soprattutto 1h-1d, quindi l'IC va misurato PRIMA di costruire consumo.

### F6 — Variance risk premium via DVOL (M, opportunistico)

**Ipotesi economica.** La vol implicita (Deribit DVOL, API pubblica) sta sistematicamente sopra
la realizzata; il premio (VRP = DVOL² − RV²) è il prezzo dell'assicurazione. SENZA opzioni in
piattaforma non si può "vendere vol" — ma il VRP è un TERMOMETRO: spike del VRP precedono
movimenti ampi (evidenza sul Bitcoin VIX), e VRP compresso segnala compiacenza.

**Cosa manca (M)**: ingestor DVOL (keyless, BTC/ETH), metrica `SentimentMetricPoints`, VRP =
DVOL² − RV² con la RV già calcolabile; consumo come feature di regime/vol-targeting (si aggancia
alla fase 2 di 1.V: un'altra baseline per il forecast di vol, e un input al `LeverageAdvisor`).

**Validazione**: prima SOLO misura (correlazione VRP → vol futura e → drawdown futuri sui dati
propri); consumo dopo il gate. Nessuna promessa direzionale.

### F7 — Lead-lag BTC → alt intraorario (M/L, dipende dal passo architetturale)

**Ipotesi economica.** La letteratura trova prevedibilità cross-crypto fino a ~6 ore: i ritardi
di BTC anticipano gli alt (BTC "risponde prima" all'informazione). La piattaforma ha 1h × 46
simboli dal 2020/2023 — il pannello per misurarlo C'È.

**Il gap è architetturale, ed è lo stesso di F1.a**: il contratto `IAlphaFactor` è single-series
(`value[i] ← candles[0..i]` DELLA STESSA serie). Serve il **contesto di mercato opzionale** (vedi
§4): un fattore che a barra i legga anche `btcCandles[0..i]`. Fatto quello, il fattore
"BtcLaggedReturn" è banale e il suo IC si misura con `FactorEvaluator` in un pomeriggio.

**Validazione**: IC sui 45 alt a 1h con orizzonti 1-6 barre; ρ con il momentum proprio del
simbolo (dev'essere bassa, o è la stessa informazione); poi gate. **Onestà**: l'effetto è
documentato soprattutto sotto le 6 ore e si consuma in fretta; i costi a 1h sono già misurati
(3,4% drag) — il margine è stretto e va dichiarato.

### F8 — Stagionalità di esecuzione: i minuti, non le ore (S)

**Ipotesi economica.** L'effetto "quarter-hour" (arXiv 2607.09426): pattern periodici ai minuti
:00/:15/:30/:45 nei futures crypto, legati al trading algoritmico programmato. Con R2 la
piattaforma ha già stabilito che sotto i 15m NON si genera alpha al netto dei costi — quindi
questo item NON è una strategia: è un **overlay di esecuzione**. Se il minuto dentro l'ora ha
un bias sistematico di pressione, gli ordini della piattaforma (aperture pipeline, slicing
TWAP/VWAP di QLIB-5) possono scegliere il minuto migliore.

**Cosa c'è già**: 1m × 6 simboli (~525k candele/simbolo); `ExecutionJob`/`ExecutionWorker` con
slicing; il pattern del segnale Ora-UTC per la misura.

**Cosa manca (S)**: fase `minuteprofile` in PlatformExpand — rendimento/volume/spread-proxy medio
per minuto-dell'ora sui 6 simboli, con placebo (minuti casuali, riusa EventStudy). Se il bias
c'è: parametro di offset nello slicing. Beneficio atteso: bp di esecuzione, misurabili, senza
nuove posizioni.

---

## 3. Le due idee originali (il "200%")

### I1 — L'Indice di Affollamento per-simbolo ⭐ (l'idea di prodotto)

**L'idea.** La piattaforma possiede QUATTRO misure indipendenti di posizionamento della folla,
nessuna delle quali è usata come segnale per-simbolo: (1) funding firmato (chi paga chi, dal
2019); (2) taker imbalance (chi attraversa lo spread, dal 2020 — IC>0 già misurato); (3) OI e
long/short ratio (in accumulo); (4) Fear & Greed (dal 2018, market-wide). L'idea è fonderle in un
**Crowding Index per-simbolo**: z-score composito del "quanto è affollato il lato lungo di QUESTO
simbolo adesso", costruito con la stessa disciplina del composite Sentiment 2.0 (z-score robusti,
pesi dichiarati, estremi contrarian).

**Perché è un edge plausibile e non l'ennesima caccia**: ogni componente ha evidenza indipendente
(funding estremi → Granger-causano i movimenti; taker imbalance → IC positivo misurato IN CASA;
OI+funding → fragilità documentata). La fusione riduce il rumore delle singole. E soprattutto:
l'ipotesi non è "prevedo la direzione" ma "**agli estremi di affollamento la distribuzione dei
rendimenti futuri è asimmetrica**" — una proprietà di coda, che si sposa con ciò che la
piattaforma sa già fare bene (risk overlay, non stock-picking).

**Consumo a tre stadi, dal più difensivo al più ambizioso** (ogni stadio passa dal gate da solo):
1. **Overlay di rischio** (il più onesto): crowding estremo sul lato della posizione → il
   moltiplicatore di esposizione scende (aggancio: `VolatilityScaler`/`LaneSafetyMonitor`, la
   manopola drawdown esiste già — qui diventa condizionale al crowding, non solo alla vol);
2. **Filtro del Composer**: segnale di catalogo "Crowding pct" (richiede il contesto §4) —
   le composizioni possono rifiutare ingressi long su crowding alto;
3. **Contrarian esplicito**: solo se 1-2 mostrano qualcosa, una strategia dedicata agli estremi.

**Aggancio**: `Services/Sentiment` (composite esistente) + una tabella/vista per-simbolo;
i pesi INIZIALI uguali e dichiarati, ricalibrati SOLO su finestre disgiunte.
**Effort**: M (stadio 1), +M (stadi 2-3).
**Validazione**: per lo stadio 1 il criterio è di RISCHIO (drawdown/vol delle corsie con overlay
vs senza, in Paper, a rendimento non peggiore); per 2-3 il gate pieno.

### I2 — Il Gemello Sintetico: industrializzare il controesempio (l'idea di metodo)

**L'idea.** L'esperimento di controllo del 2026-07 (edge piantato → la pipeline lo trova) è stato
il momento che ha reso credibili tutti i negativi. Ma è stato fatto UNA volta, a mano. L'idea è
renderlo un ORGANO PERMANENTE della ricerca: per ogni caccia/validazione, la piattaforma genera
automaticamente N **mercati gemelli nulli** — serie sintetiche costruite dai rendimenti reali con
lo stationary block bootstrap (T1.5, GIÀ in casa), che preservano vol clustering e code grasse ma
DISTRUGGONO ogni struttura prevedibile — e fa girare la stessa identica caccia su ognuno.

Il verdetto diventa comparativo e automatico: **il candidato è accettabile solo se il suo esito
sui dati veri è più estremo del 95° percentile dei suoi esiti sui gemelli nulli.** È un permutation
test "a livello di intera pipeline di ricerca": include nella correzione anche il selection bias
del processo (quante combinazioni ha provato la caccia, come ha scelto, che holdout ha usato),
cosa che DSR e PBO approssimano analiticamente e il gemello misura empiricamente.

**Perché è originale qui**: richiede esattamente ciò che questa piattaforma ha e altri no — un
motore di caccia interamente scriptabile (`hunt`/`discover`/CPCV), il bootstrap a blocchi già
testato, e la cultura del controllo. Trasforma il costo dell'onestà in un vantaggio competitivo:
si possono provare DIECI ipotesi nuove al mese, perché il costo marginale di validarle onestamente
crolla.

**Aggancio**: `MonteCarloSamplingMode.StationaryBlock` per la generazione; nuova fase
`nulltwin <fase>` in PlatformExpand che avvolge una fase esistente; report automatico
"reale vs distribuzione nulla".
**Effort**: M.
**Validazione**: auto-validante per costruzione — il gemello con edge piantato DEVE risultare
estremo, quello nullo no (sono i due lati del controllo già fatto a mano, resi ripetibili).

---

## 4. Nota architetturale trasversale: il "contesto di mercato" dei segnali

Tre item (F1.a, F7, I1-stadio-2) sbattono sullo stesso muro: `SignalCatalog` e `IAlphaFactor`
vedono UNA serie di candele e nient'altro (contratto anti-look-ahead `value[i] ← candles[0..i]`).
Il passo abilitante è UNO e piccolo: un **`MarketContext` opzionale** (serie di riferimento BTC,
serie di funding, crowding per-simbolo) passato accanto alle candele, con lo STESSO contratto
causale (`context[j ≤ i]`). Additivo: i segnali esistenti non lo vedono nemmeno; i nuovi lo
dichiarano. Le strategie Composite salvate restano valide (id append-only, come sempre).

È l'unico investimento infrastrutturale di questa roadmap, sblocca tre item, e ha un test
naturale: il valore di ogni segnale contestuale alla barra i dev'essere identico su serie piena
e troncata — l'invariante già usata per Ora-UTC e MFI/OBV.

---

## 5. Tabella riassuntiva e percorso

| Item | Effort | Dati | Dipende da | Criterio di validazione / **esito** |
|---|---|---|---|---|
| F1.a Funding come segnale | M | ✅ in casa (2019+) | contesto §4 | IC + composizioni al gate |
| F1.b Carry delta-neutro | M/L | ✅ in casa | — | ✅ **MISURATO 2026-07-24**: netto 5-12%/anno sul funding vero, robusto alle soglie (REPORT-FRONTIERE) |
| F2 Rotazione /BTC | M | ✅ in casa (+universo) | — | gate su finestre disgiunte, mai panieri correlati |
| F3 Eventi come filtri | S/M | ✅ in casa | run `eventstudy` | ✅ **CHIUSO 2026-07-24**: continuazione post-Crash/Surge p=0,002 replicata → segnali 12-13; VolSpike/VolumeBlowout bocciati dal placebo |
| F4 Stream liquidazioni | M | ⏳ da accumulare | — | ✅ **COSTRUITO 2026-07-24**: worker ON di default, purge esente, canale verificato dal vivo |
| F5 Stablecoin/on-chain | M | ⏳ keyless da verificare | — | IC a 1h-1d prima di ogni consumo |
| F6 VRP via DVOL | M | ⏳ keyless (Deribit) | — | correlazioni misurate; nessuna promessa direzionale |
| F7 Lead-lag BTC→alt | M/L | ✅ in casa | contesto §4 | IC 1-6 barre, ρ bassa col momentum proprio |
| F8 Minuti di esecuzione | S | ✅ in casa (1m×6) | — | ✅ **MISURATO 2026-07-24**: drift nullo; range −30/40% nei minuti tardo-quarto → offset di slicing documentato, wiring rimandato |
| **I1 Crowding Index** | M+M | ✅ 3/4 in casa | (stadio 2: §4) | stadio 1 su metriche di RISCHIO; 2-3 gate pieno |
| **I2 Gemello sintetico** | M | ✅ in casa | — | ✅ **COSTRUITO 2026-07-24**: selfcheck con edge piantato oltre P95; run vero BTC 4h DENTRO il nullo (conferma indipendente dei negativi) |

**Percorso consigliato** (tre mosse, ciascuna autosufficiente):

1. **Subito, in parallelo e senza rischio**: F4 (le liquidazioni si accumulano solo se si comincia
   oggi) + F8 (piccolo, paga in bp di esecuzione) + run `eventstudy` (decide F3 da solo).
2. **Il cuore**: I2 (il gemello sintetico) PRIMA delle cacce nuove — così F1/F2 nascono già dentro
   il metodo industrializzato — poi F1 (il candidato economicamente più solido: il carry è un
   flusso, non una previsione) e F2.
3. **Il contesto §4 quando serve davvero**: lo sblocca il primo fra F1.a, F7 e I1-stadio-2 che
   supera la propria misura preliminare.

E in parallelo a tutto: la **caccia nuova col motore di oggi** (già raccomandata a chiusura della
roadmap precedente) — embargo, funding firmato, Ora-UTC, MFI/OBV nel pool, CPCV e permutation.
È il modo più economico di scoprire se i miglioramenti di metodo hanno già cambiato la risposta.

---

## 6. Fonti esterne consultate (2026-07-23)

- Carry/funding: [BIS Working Paper 1087 — Crypto carry](https://www.bis.org/publ/work1087.pdf);
  [Risk/return of funding-rate arbitrage CEX/DEX (ScienceDirect)](https://www.sciencedirect.com/science/article/pii/S2096720925000818);
  [struttura a due livelli dei funding markets (MDPI)](https://www.mdpi.com/2227-7390/14/2/346);
  [bias positivo persistente dei funding (BitMEX Research, via FinancialContent)](https://markets.financialcontent.com/ms.intelvalue/article/breakingcrypto-2025-10-14-bitmex-research-uncovers-persistent-positive-bias-in-crypto-funding-rates-signaling-new-era-of-market-stability)
- Cross-section: [Dobrynskaya — Cryptocurrency Momentum and Reversal (SSRN)](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=3913263);
  [momentum TS vs CS 2024-25 (ResearchGate)](https://www.researchgate.net/publication/406476873_Momentum_Trading_in_Cryptocurrencies_A_Comparative_Study_of_Time-Series_and_Cross-Sectional_Strategies);
  [reversal cross-section e incertezza (ScienceDirect)](https://www.sciencedirect.com/science/article/abs/pii/S154461232501058X)
- Lead-lag: [Cross-cryptocurrency return predictability (ScienceDirect)](https://www.sciencedirect.com/science/article/abs/pii/S0165188924000551);
  [lead-lag BTC/ETH orario e giornaliero (ResearchGate)](https://www.researchgate.net/publication/333787469_Lead-Lag_Relationship_between_Bitcoin_and_Ethereum_Evidence_from_Hourly_and_Daily_Data)
- Derivati/liquidazioni: [microstruttura futures BTC: cascate, regimi di funding, OI (XT/Medium)](https://medium.com/@XT_com/bitcoin-futures-market-microstructure-liquidation-cascades-funding-regimes-and-open-interest-978b107b4889);
  [segnali derivati integrati (Gate Wiki)](https://www.gate.com/crypto-wiki/article/how-to-interpret-crypto-derivatives-market-signals-funding-rates-open-interest-and-liquidation-data-explained-20251227)
- Order flow/flussi: [Order Flow and Cryptocurrency Returns (EFMA)](https://www.efmaefm.org/0EFMAMEETINGS/EFMA%20ANNUAL%20MEETINGS/2025-Greece/papers/OrderFlowpaper.pdf);
  [on-chain flows per return/vol forecasting, netflow USDT 1-2h (arXiv 2411.06327)](https://arxiv.org/pdf/2411.06327);
  [order flow toxicity e salti di prezzo (ScienceDirect)](https://www.sciencedirect.com/science/article/pii/S0275531925004192)
- VRP: [Bitcoin VIX e variance risk premium (ResearchGate)](https://www.researchgate.net/publication/346500941_The_Bitcoin_VIX_and_Its_Variance_Risk_Premium);
  [risk premia nel mercato Bitcoin (arXiv 2410.15195)](https://arxiv.org/pdf/2410.15195)
- Stagionalità: [quarter-hour effect nei futures crypto (arXiv 2607.09426)](https://arxiv.org/html/2607.09426v2);
  [finestre orarie UTC di Bitcoin (Quantpedia)](https://quantpedia.com/are-there-seasonal-intraday-or-overnight-anomalies-in-bitcoin/);
  [intraday/daily dynamics (ScienceDirect 2024)](https://www.sciencedirect.com/science/article/pii/S1059056024006506)
- Dati keyless: [DeFiLlama/Smart Money aggregators](https://smartmoneyapi.com/onchain);
  [bgeometrics BTC on-chain API free](https://charts.bgeometrics.com/bitcoin_api.html);
  [CryptoQuant docs (netflow stablecoin)](https://dataguide.cryptoquant.com/stablecoin-exchange-flows-indicators/stablecoin-exchange-in-outflow-and-netflow)

*Avvertenza d'uso delle fonti: i numeri promozionali (es. "115% in sei mesi" dagli articoli
exchange) NON sono evidenza — sono marketing. L'evidenza utilizzabile è quella accademica/BIS e,
sopra tutto, quella che la piattaforma misurerà sui PROPRI dati col PROPRIO gate.*
