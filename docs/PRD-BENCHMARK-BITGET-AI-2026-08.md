# PRD — Benchmark Bitget/AI (tredicesima roadmap, 2026-08-04)

*Nato dal settimo PDF esterno («Architettura di un Bot di Trading AI per Altcoin: Guida allo
Sviluppo in C# su Bitget e all'Integrazione con Groq, Gemini e NVIDIA») e dalla richiesta del
proprietario di aggiornare e potenziare ProcioneMGR tenendo il documento come riferimento.*

## 0. Natura del documento

Il PDF è una **guida introduttiva generalista**: descrive come costruire, da zero, un bot di
trading AI in C# su Bitget con Groq/Gemini/NVIDIA — architettura modulare, backtesting
event-driven, gestione del rischio, integrazione AI multi-provider — con un piano a 5 fasi che va
dal "Setup Ambientale" al live trading. Non cita ProcioneMGR: è scritto per chi parte dal foglio
bianco. Come per i sei PDF esterni precedenti, il metodo resta: **confronto punto-per-punto col
già-costruito, poi solo il genuinamente nuovo**. La maggior parte del piano a 5 fasi del documento
corrisponde a lavoro chiuso da tempo (Fasi 1-8 dell'architettura originale, poi ampiamente superato
dai filoni C/D/E/F/AF).

## 1. Confronto punto-per-punto

### Fondamenta tecnologiche

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| C#/.NET + architettura modulare, Adapter Pattern per gli exchange (`IExchangeClient`) | `IExchangeClient`/`IFuturesExchangeClient` + `BinanceClient`/`BitgetClient`, dalla Fase 2 originale (2026-06-30) | già fatto |
| `JK.Bitget.Net` / `DigitalRuby.ExchangeSharp` come SDK di terze parti | Client REST diretto proprietario, scelta deliberata (niente rischio supply-chain su un repo pubblico, controllo pieno su trigger-order/plan-order, HMAC verificato con test indipendenti, provato su Bitget Demo/Binance Testnet) | non adottare — nessun vantaggio dimostrato su codice già verificato |
| Microsoft Agent Framework / `IChatClient` unificato multi-provider | `ILlmClient` + `DelegatingLlmClient` (failover) + `ILlmClientResolver` + `ModelAutoSelector`, 5 provider (Nvidia/Gemini/Groq/HuggingFace/Anthropic) via `OpenAiCompatibleLlmClient`, comitato a scelta vincolata | già fatto, con guardie che il framework generico non offre di suo (menù vincolato, doppia validazione anti-injection, breaker, budget) |
| Backtester event-driven custom (l'alternativa a NinjaScript) | `BacktestEngine` event-driven: fee/slippage/funding/liquidazione, fill maker consapevole della coda (F-queue), walk-forward, CPCV, DSR/PBO, gemello sintetico (NullTwin) | già fatto, oltre lo stato dell'arte descritto nel documento |

### Bitget e altcoin

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| Analisi liquidità/slippage/rate-limit di Bitget (leadership su ETH/SOL, slippage basso, 0.01% maker/taker) | Dato di mercato coerente con una scelta già presa: Bitget è l'**unico** exchange a leva utilizzabile da IT/UE (Binance Futures bloccato da MiCA dal 2026-07-01); rate limit già gestiti (throttling/backoff/429) | nessuna azione — conferma la direzione, non porta codice nuovo |
| Canali WebSocket order book (`books`/`books5`/`books15`, 200ms spot / 50ms futures) | **Non catturato oggi**: nessun order book L2 in casa, solo OHLCV + tape/liquidazioni | copre esattamente l'item **D2** già scritto nella roadmap Profitto-Intraday (rimandato: 171,6 GB/anno di tape grezzo per 3 simboli, serve server dedicato) — nessuna azione nuova |

### Strategie

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| Market making (spread capture) | **Non costruito**, dipende da D2 (order book) + server dedicato — item **E4** della roadmap Profitto-Intraday, dichiarato "endgame 2027" | nessuna azione nuova, il PDF conferma un piano già scritto |
| Arbitraggio cross-exchange | Richiederebbe un secondo exchange con leva, che non esiste per l'utente (MiCA limita a Bitget) | **respinto** |
| Momentum/breakout, mean reversion | Già in catalogo (12 strategie: RSI/EMA/MACD/Bollinger/Momentum/Composite/GridMeanReversion/pairs/carry/eventi Post-Crash-Surge...); la classe "direzionale-tecnico su OHLCV" ha **10 esiti negativi consecutivi control-validati** (il controllo con edge piantato dimostra che gli strumenti funzionano, non che siano tarati per bocciare tutto) | il documento non aggiunge nulla che le cacce non abbiano già escluso |

### Intelligenza artificiale

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| Groq per sentiment/validazione rapida di segnali pre-esecuzione | Groq è uno dei 5 provider attivi (comparazione + failover + comitato); il pattern specifico "Groq come guardia di sicurezza nel percorso d'ordine" | **riconsiderato e RICONFERMATO respinto il 2026-08-04** (§2bis, punto 2) — violerebbe "l'AI non entra mai nel meccanismo" |
| Gemini per auto-generazione/adattamento di strategie | — | **riconsiderato il 2026-08-04** (§2bis, punto 1): accettato SOLO in forma scoped come generatore di candidati dentro l'imbuto esistente (G3), mai adattamento libero di parametri operativi |
| Sistema multi-agente supervisionato da un LLM (agenti specializzati + orchestratore) | `FleetOrchestrator` (Queen Bee **deterministica**, journal, fuzz 20k) + comitato LLM a scelta vincolata (mai generativo, doppia validazione anti-injection, quorum) | stessa idea del PDF, esecuzione più prudente — mantenere l'architettura attuale (confermato in sede di riconsiderazione, §2bis punto 2) |
| NVIDIA NIM self-hosted (fine-tuning, deployment su GPU) | Nessuna GPU dedicata; l'inferenza cloud NVIDIA (integrate.api.nvidia.com) copre il ruolo di provider LLM, l'inferenza locale ONNX-CPU copre il caso "modello proprio, filiera 100% C#" (pilota L1) | non pertinente salvo hardware nuovo |
| RAG su documenti finanziari/report per contestualizzare le decisioni | Nessuna pipeline di recupero documentale esiste oggi | **genuinamente nuovo** — dettaglio in §2 (G1) |

### Ottimizzazione e gestione del rischio

| Proposta del PDF | Stato reale | Verdetto |
|---|---|---|
| Sharpe/MaxDD/win-rate/profit-factor, walk-forward | Già presenti e superati: DSR, PBO, CPCV, MinTRL power-check, gemello sintetico | nessuna azione |
| Position sizing dinamico su volatilità | Kelly empirico su distribuzione reale + GARCH Student-t (code grasse) | nessuna azione |
| Stop-loss/take-profit dinamici | Auto SL/TP data-driven da percentili di escursione (MAE/MFE), bracket automatico | nessuna azione |
| Pause dopo perdite consecutive | `SafetyChecker` (10 controlli: MaxDailyLoss critico→emergency stop, cooldown, MinOrderInterval, ecc.) | nessuna azione |
| Ottimizzazione multi-obiettivo (frontiera di Pareto, NSGA-II) | **Non implementato esplicitamente**: oggi selezione single-objective (Sharpe OOS) sotto gate DSR/PBO bloccanti, con risk parity separata a livello di portafoglio (Ledoit-Wolf + ERC) | candidato marginale, priorità bassa — dettaglio in §2 (G2) |

## 2. Cosa è genuinamente nuovo

### G1 — RAG-lite documentale per l'advisory (priorità: bassa/opzionale)

**Motivazione**: il proprietario porta spesso PDF esterni (sette finora) che oggi vengono letti e
confrontati manualmente sessione per sessione da Claude; l'advisory e il comitato AI non hanno
accesso a questi documenti né ai report propri (`REVISIONE-STATO-ARTE`, `ROADMAP`, `REPORT-*`) —
ragionano solo su dati di mercato e mood. Un retrieval semplice potrebbe ancorare l'advisory a
contenuti reali.

**Design** (dentro i vincoli già stabiliti):
- Corpus: documenti caricati dal proprietario (testo estratto) + report/roadmap propri del repo,
  opt-in esplicito (non l'intera `docs/` di default).
- Retrieval: BM25/TF-IDF puro C#, **nessun nuovo modello di embedding scaricato senza gate** —
  stessa disciplina del pilota ONNX (parità come gate di pubblicazione, filiera controllata).
  Evita anche una dipendenza da un provider esterno solo per indicizzare.
- Uso: il testo recuperato si inietta nel prompt come CONTESTO aggiuntivo, mai come istruzione;
  resta soggetto alla stessa doppia validazione anti-injection già presente nel comitato (AF3).
- Confine: resta advisory-only. Nessun canale nuovo tocca il meccanismo di esecuzione.

**Gate/verifica**: L1 (ranking di retrieval corretto su corpus noto); L2 (contenuto malevolo
iniettato in un documento non altera l'esito a scelta vincolata — stessa property testata sul
comitato); L3 (integrazione reale su Postgres); L4 (browser: caricare un report vero, verificare
che l'advisory lo citi).

**Non è urgente**: nessuna fase aperta oggi lo richiede. Da avviare SOLO dopo la chiusura degli
item già aperti (§4) e solo su conferma esplicita del proprietario.

### G2 — Ottimizzazione multi-obiettivo (Pareto Sharpe/MaxDD) — priorità: bassa, backlog

Non costruire ora. Il beneficio atteso è marginale rispetto al costo: il sistema attuale ottiene
già un effetto simile per un'altra via — il gate DSR/PBO scarta le soluzioni fragili a monte, e
Ledoit-Wolf + ERC pesa per rischio a livello di portafoglio, separatamente. Un vero fronte di
Pareto avrebbe senso se emergesse un caso concreto in cui le due tecniche divergono in modo
misurabile — non osservato oggi. Resta in backlog, nessuna fase assegnata.

## 2bis. Tre regole riconsiderate (2026-08-04, su richiesta del proprietario)

Il proprietario ha chiesto di riconsiderare tre non-obiettivi presi con la roadmap Autonomia
Finanziaria (§4 del PRD-AUTONOMIA-FINANZIARIA-2026-08), chiedendo un'opinione prima di
modificarli. Opinione data, consenso registrato, regole aggiornate come segue.

**1. AI che genera strategie → accettato, in forma scoped (diventa G3).**

La piattaforma genera già candidati algoritmicamente (`GeneticAlphaMiner`,
`CreativeDiscoveryStage`, `StrategyComposer`) e li fa passare TUTTI dallo stesso imbuto:
selezione → holdout → DSR/PBO/CPCV → gemello sintetico → forward test Paper. Se "l'AI genera
strategie" significa *un'altra fonte di candidati che entra nello stesso imbuto, zero
scorciatoie*, il rischio è basso — concettualmente identico ad aggiungere un'euristica in più al
miner genetico.

**Aspettativa onesta dichiarata**: il valore atteso è probabilmente basso. La diagnosi della
roadmap Profitto-Intraday non è "mancano idee" — sono 10 esiti negativi consecutivi,
control-validati, su 445mila combinazioni sistematiche + ricerca genetica sulla classe
direzionale-tecnica. Un LLM che propone altri incroci di indicatori difficilmente trova quello
che una ricerca sistematica non ha trovato. Il valore vero atteso è più a monte: suggerire QUALE
classe di edge o regime esplorare (leggendo letteratura/documenti, collegabile a G1), non
inventare la logica del segnale.

**Design G3**: candidato AI in JSON strutturato e vincolato (solo componenti dal catalogo
esistente — indicatori, filtri di regime, range di parametri già in `Discovery` — mai codice
libero), rivalidato contro whitelist (stesso principio anti-injection del comitato AF3: il
chiamante rivalida sempre l'output contro il menù/catalogo consentito). Entra nell'imbuto
ESISTENTE (`CreativeDiscoveryStage`/`GeneticAlphaMiner`), stessi gate, stesse soglie, nessuna
corsia riservata. Se l'AI è spenta o fallisce, la pipeline è bit-identica a oggi.

**2. AI che entra nel meccanismo e nel percorso d'esecuzione → RIFIUTATO, linea rossa
confermata.**

Qui l'opinione è stata di dissenso netto, non di cautela. È il principio su cui è costruito tutto
il layer di autonomia: la Queen Bee (`FleetOrchestrator`) è deterministica e fuzzata 20mila volte
apposta; il comitato AI non decide, spareggia dentro un menù pre-validato con un fallback
deterministico su QUALUNQUE fallimento (timeout, JSON rotto, fuori menù, quorum non raggiunto) —
proprietà testata "provider spazzatura al 100% ⇒ comitato spento".

Due motivi concreti, non teorici: (a) il layer AI ha GIÀ causato incidenti operativi restando
solo advisory — settimane di fallimento silenzioso per credito API esaurito, un bug SDK che
abbatteva l'intero tick; nel percorso reale d'ordine lo stesso tipo di guasto diventa un ordine
sbagliato o uno stop-loss non eseguito, con capitale vero in gioco; (b) non si può fuzzare lo
spazio di risposta di un LLM come si fa col codice deterministico — l'intera piattaforma vale per
essere provabilmente sicura su ogni stato raggiungibile, e un LLM nel loop caldo rompe questa
proprietà.

**Via sanzionata per dare più peso all'AI senza rompere la linea rossa**: ampliare i tipi di
decisione arbitrabili dal comitato (sempre a menù vincolato, sempre fallback deterministico, mai
su Live, mai nel loop caldo di piazzamento ordini). Nessun item concreto richiesto oggi — resta
un principio disponibile per fasi future, non una fase in sé.

**3. AI che studia le operazioni fallite o negative → accettato, diventa G4.**

Rischio basso, valore potenziale alto: un post-mortem testuale su trade/corsie chiusi in perdita
(che regime, che contesto, cosa è andato storto) è analisi che riusa esattamente l'infrastruttura
advisory di oggi (`ILlmClient`/`LlmCallGuard`, budget AF1, breaker del layer).

**Estensione richiesta dal proprietario nella stessa sessione**: il post-mortem non resta solo
testo per un umano — il suo ESITO strutturato (non il testo libero: una classificazione/
segnalazione, es. "causa probabile: regime avverso" o "candidato a ritiro") diventa anche CONTESTO
aggiuntivo per il comitato a scelta vincolata (AF3, `FleetAssignmentMenu`) quando quel comitato è
chiamato a decidere su quella corsia/candidato. Il vincolo che il proprietario stesso ha posto,
testuale: **"mai una scorciatoia che tocca un parametro da sola"** — rispettato per costruzione:
il post-mortem non decide, aggiunge contesto a un processo di decisione che resta quello di AF3
(menù pre-validato, quorum, parità → default deterministico, doppia validazione anti-injection).
Se l'AI del post-mortem fallisce o è spenta, il comitato si comporta esattamente come oggi (nessun
contesto extra, non un errore).

## 2ter. Altri usi delle AI (G5-G9, 2026-08-04)

Il proprietario ha chiesto: «in che altro modo potrei sfruttare le AI?». Criterio di selezione
usato: **risolve un attrito reale e osservato**, riusa infrastruttura esistente, e sta dentro il
confine advisory senza eccezioni. Scartate le idee che suonavano bene ma non superavano il
criterio (agente che riscrive i prompt da solo, AI che sceglie l'universo di simboli, AI che
riassume i log — nessuno di questi risolve un attrito che qualcuno ha davvero sentito).

### G6 — Spiegazione dei candidati bocciati dai gate (rischio nullo, priorità alta fra i G)

**Attrito reale**: quando un run boccia 57 candidati, i numeri (DSR 0,81 / PBO 0,52 / Sharpe
holdout 1,43 contro P95 nullo 2,9) sono corretti ma muti — capire QUALE giudice ha respinto e di
quanto richiede oggi di leggere l'artifact a mano o di chiederlo a Claude.

**Design**: dopo il verdetto, per i candidati respinti si chiede al provider attivo 2-3 righe in
italiano piano — quale giudice, quale numero, cosa avrebbe dovuto raggiungere. **Rischio nullo per
costruzione**: il candidato è già respinto e non è mai stato schierato; non esiste percorso di
codice per cui quel testo torni a toccare una decisione. Il prompt contiene solo numeri oggettivi
già calcolati.

### G4 — Post-mortem sulle operazioni fallite (design, da costruire)

**Forma comune a G6**: dati oggettivi → LLM → testo → UI, col deterministico che vive da solo e
l'AI che aggiunge solo parole. Stessa disciplina, stesso `LlmCallGuard`, stesso budget AF1.

**I fatti (deterministici, da `TradeRecord`)**: prezzo di entrata e uscita, PnL assoluto e
percentuale, durata, motivo di uscita (`ExitReason`: stop-loss, take-profit, segnale,
liquidazione), simbolo, timeframe, strategia, corsia, modalità. Tutto già in tabella.

**La classificazione a MENÙ CHIUSO** — è il pezzo che alimenta il comitato, e per questo non è
testo libero. Voci previste: `RegimeAvverso`, `StopStretto`, `SegnaleDegradato`, `CostiDominanti`,
`RumoreNormale`, `Inconcludente`. Regole:
- l'AI sceglie UNA voce del menù; fuori menù, JSON rotto, timeout o assenza ⇒ `Inconcludente`
  (default deterministico, esattamente come il comitato AF3);
- dove la causa è **calcolabile**, la calcola il codice e l'AI non viene nemmeno interpellata su
  quel punto (esempio già identificato: PnL lordo positivo e netto negativo ⇒ `CostiDominanti`,
  è aritmetica, non interpretazione);
- la prosa resta libera ma vive solo in journal/digest, mai nella classificazione.

**L'innesto sul comitato** (l'estensione chiesta dal proprietario): quando il comitato AF3 è
interpellato su una corsia o un candidato, il `Context` della `CommitteeQuestion` porta anche il
conteggio delle classificazioni recenti di quella corsia («3 post-mortem su 5: RegimeAvverso»). È
**contesto in più dentro il menù esistente**, non un canale nuovo: il menù, il quorum, la doppia
validazione e il default deterministico restano quelli di AF3. Vincolo del proprietario, verbatim:
*«mai una scorciatoia che tocca un parametro da sola»* — rispettato perché il post-mortem non
decide, informa una decisione che resta di `Decide` e del comitato.

**Perché non è stato costruito nella prima ondata (2026-08-05)**: serve una tabella nuova
(`TradePostMortems`) — `PipelineArtifact` è agganciato a un `RunId` che i trade non hanno, e usare
il journal della flotta sarebbe piegare uno schema al posto sbagliato. Una tabella nuova è una
migrazione, e la migrazione si applica al database vivo al primo riavvio dell'app
(`DbInitializer` chiama `MigrateAsync`). È additiva e reversibile, ma se sbagliata lascia il
guscio giù: **si fa con il proprietario sveglio**, non durante una sessione notturna. La fase è
progettata e pronta; manca solo il momento giusto per applicarla.

### G5 — Bozza automatica del confronto col PDF esterno (dipende da G1)

**Attrito reale**: sette PDF esterni finora, ognuno confrontato a mano punto-per-punto contro
roadmap e report — è il lavoro che ha prodotto questo stesso documento. Con G1 (corpus indicizzato)
un provider può produrre la PRIMA STESURA della tabella «proposta → stato reale → verdetto».

**Confine**: è una BOZZA da rivedere insieme, mai un verdetto pubblicato da solo. Un verdetto
sbagliato in un documento di pianificazione non muove denaro, ma può far perdere tempo o far
respingere un'idea buona: per questo la revisione umana resta obbligatoria e dichiarata.

### G8 — Domande e risposte sui documenti propri (dipende da G1)

Estensione naturale di G1: chiedere «cosa abbiamo scoperto sul pairs trading a 1d?» e ottenere una
risposta ancorata ai report veri con citazione del documento. **Regola dura**: risposta senza
fonte = rifiutata; domanda fuori corpus = «non lo so», mai un'invenzione (è il fallimento tipico
di questi sistemi, e qui costerebbe fiducia su verdetti che sono stati pagati con settimane di
misure).

### G9 — Narrativa di sintesi nel digest giornaliero (polish)

Un paragrafo leggibile sopra i dati strutturati che il `DailyDigestWorker` già invia. **Additivo
per costruzione**: se l'AI è spenta o fallisce, il digest è esattamente quello di oggi — la sua
assenza non è un guasto e non va notificata come tale.

### G7 — Parere del comitato accanto al click Live (il più delicato, per ultimo)

Al momento della promozione Testnet→Live il comitato mostra un parere dentro menù vincolato
(promuovere ora / aspettare / non promuovere + motivazione). **Il gate non si sposta di un
millimetro**: il click resta umano, il parere non abilita né disabilita nulla, è informazione in
più accanto al pulsante.

**Perché per ultimo, nonostante sia utile**: è l'unico punto della lista che vive accanto alla
decisione che muove capitale reale. Anche un parere puramente informativo, se mostrato male, può
diventare pressione psicologica su un click che deve restare del proprietario. Si tocca solo
quando tutto il resto è stabile, con test che dimostrano l'indipendenza del pulsante dallo stato
del parere.

## 3. Non-obiettivi (motivati)

1. **`JK.Bitget.Net`/`ExchangeSharp`** — nessun vantaggio dimostrato su un client REST già
   verificato, testato in demo/testnet e in produzione.
2. **Arbitraggio cross-exchange** — manca un secondo venue con leva (vincolo MiCA, Bitget-only).
3. **Market making immediato** — dipende da D2 (order book) e un server dedicato, già pianificato
   e rimandato (costo storage misurato: 171,6 GB/anno per 3 simboli).
4. **LLM che scrive codice libero o tocca un parametro operativo fuori gate** — resta vietato;
   G3 (§2bis) è l'UNICA eccezione, ed è scoped (candidato che passa dagli stessi gate di tutto il
   resto, mai un canale privilegiato, mai codice libero — solo composizione da whitelist).
5. **LLM nel percorso di esecuzione** (es. Groq come guardia pre-trade) — **riconsiderato e
   RICONFERMATO come linea rossa il 2026-08-04** (§2bis): violerebbe l'invariante "l'AI non entra
   mai nel meccanismo", vera per Queen Bee, comitato e advisory. Via sanzionata per dare più peso
   all'AI: ampliare i tipi di decisione arbitrabili dal comitato, mai il loop caldo di esecuzione.
6. **NVIDIA NIM self-hosted** — manca hardware GPU dedicato; da rivalutare solo se il proprietario
   acquisisce infrastruttura GPU.
7. **Multi-agente generativo stile "trading firm"** — la Queen Bee deterministica + comitato a
   scelta vincolata è la versione più prudente della stessa idea, e è già in produzione.
8. **Validazione più permissiva** — le soglie DSR/PBO non si toccano.

## 4. Priorità reale (ordine consigliato)

Nessun item di questo documento è bloccante. L'ordine di lavoro resta quello già scritto nelle
roadmap aperte, che hanno precedenza:

1. **Filone F (Valore)** — F5 fascia grigia → forward Paper, F6 gemello sui soli sopravvissuti,
   F7 `/metrics` ricollegata al motore in-cluster, F8-F11 igiene verdetti, F12 capacità del carry,
   F13 market-neutral esteso sulle coppie.
2. **Filone AF (Autonomia Finanziaria)** — AF2 esecuzione della flotta (dopo la settimana di
   osservazione DryRun), AF4c pesi ERC advisory fra corsie.

Ordine interno ai G, per rischio e costo crescenti:

3. **G6** (spiegazione bocciati) — rischio nullo per costruzione, self-contained, valore immediato.
4. **G4** (post-mortem + contesto al comitato) — riusa la stessa fondazione di G6.
5. **G9** (narrativa digest) — piccolo, additivo.
6. **G1** (RAG-lite) — fondazione di G5 e G8.
7. **G5** + **G8** — consumatori di G1.
8. **G3** (AI genera candidati) — aspettativa dichiarata modesta; meglio dopo G1.
9. **G7** (parere al click Live) — il più delicato: solo a piattaforma stabile.
10. **G2** (Pareto) — backlog, nessuna scadenza.

**Invariante comune a tutti i G**: default-off, configurazione propria nella UI (mai un flag che
vive solo in appsettings), e a interruttore spento comportamento bit-identico a prima —
verificato al livello 2 dello standard, non promesso a parole.

## 5. Metodo

Confermato ancora una volta: mai implementare un PDF esterno a scatola chiusa. Ogni idea nuova
passa dal confronto col già-misurato prima di diventare codice — è il motivo per cui, su sette
documenti esterni portati dal proprietario, la stragrande maggioranza delle proposte risultava
già fatta, già superata, o già respinta con un motivo scritto.
