# AUDIT DI VALORE — ProcioneMGR (2026-08-01)

Domanda a cui risponde: *l'aggiornamento massiccio ha creato valore o feature bloat, rigidità e
lentezza?* Metodo: analisi statica mirata (superficie funzionale vs verdetti di ricerca già
misurati, catena di validazione, hotspot di calcolo) + **test dal vivo sull'app reale**
(profilo `procione-main`, porta 5199, motore in-cluster collegato): navigazione Home / Ensemble /
Backtest / Trading / Metriche, un backtest eseguito, log server e console monitorati.

Esito del giro dal vivo, in una riga: **zero errori console, zero errori server, backtest su
3.576 candele completato in <4s, battito del motore corretto («0 barre indietro»)** — la
piattaforma è sana. I problemi trovati sono di *costo* e di *superfici sopravvissute ai propri
verdetti*, non di rottura.

---

## 🔴 Cosa TOGLIERE o DISATTIVARE

### R1 — La pagina `/metrics` così com'è: mostra zeri che non significano «niente da dire»
Verificato dal vivo: **tutti i contatori a zero** (trade, job, promozioni, latenze, slippage)
mentre la piattaforma ha 3 corsie Paper attive e 159 trade a storico. Causa strutturale: il
`MetricsCollector` è **in-process nel guscio**, ma dal core-caldo (PR #55) il motore gira
**in-cluster** — i trade accadono nell'altro processo. La pagina è orfana della sua fonte dati:
è esattamente la classe «controllo che rassicura» (qui: rassicura che non succede niente, mentre
succede altrove). **O si ricollega al processo giusto o si toglie dal menu**: una dashboard di
osservabilità che osserva il processo sbagliato è peggio di nessuna dashboard.
*Fix minimo:* etichetta a tutta pagina «metriche del SOLO guscio — il motore emette le proprie su
:18092/metrics» + link. *Fix vero:* leggere i contatori del motore (endpoint /metrics già esposto
dal core, o tabella condivisa).

### R2 — Il pulsante «▲ Promuovi a Live» permanentemente disabilitato (Trading.razor:118)
È sempre `disabled`, per scelta giusta (Live solo dai controlli di modalità con conferma). Ma un
pulsante rosso che non può MAI attivarsi non è un pulsante: nel testo della pagina si legge come
azione disponibile, e insegna a ignorare i pulsanti disabilitati. Sostituire con testo statico
(«Live: solo manuale, dai controlli qui sotto») o rimuovere — il titolo della tabella lo dice già.

### R3 — Il muro di alert di deriva in Home
Dal vivo: «19 fattori in deriva», 6 righe elencate, **tutte DOGE/BTC**. L'informazione vera è
«DOGE/BTC 4h/1d ha i fattori spenti» = 1 riga, non 6+13 nascoste. Raggruppare per serie con
conteggio; il dettaglio resta in Feature Selection. Un alert che occupa metà Home per dire una
cosa sola è rumore che copre il prossimo alert vero.

### R4 — DTW pattern matching dal menu di ricerca attiva
Il verdetto D4 della *tua stessa macchina* (roadmap Scoperta-Pattern): **negativo contro il nullo
per forma**. Tenerlo in `/market-analysis` come strumento di ricerca corrente invita a riesumare
una strada già chiusa con metodo. Non cancellare il codice (è il registro del NO, con i suoi
test): spostare la sezione sotto un'intestazione esplicita «Tecniche misurate e scartate» con il
numero del verdetto, o dietro un toggle. Vale la pena farlo UNA volta come pattern, perché di
verdetti negativi ne produrrete altri: il posto dei no è visibile ma separato dal banco di lavoro.

**Cosa NON togliere, contro l'istinto:** le 13 strategie direzionali-tecniche del catalogo.
Il direzionale-tecnico ha collezionato dieci no, ma quelle strategie sono (a) i mattoni di
`RegimeConditional`/`Composite` — l'unica famiglia con sopravvissuti in ensemble, (b) il gruppo di
controllo che ha RESO POSSIBILI i dieci no, (c) referenziate da 17 strategie salvate e config di
corsia: rimuoverle romperebbe dati vivi. Vedi 🟡 M5 per cosa farne davvero.

---

## 🟡 Cosa MODIFICARE o REFACTORARE

### M1 — PERFORMANCE, il più grave: la pagina Ensemble ricalcola due simulazioni complete ogni 15s
`Ensemble.razor` fa polling a 15s → `EnsemblePageService.RefreshAsync` →
`GetStatusAsync()` (**SimulateAsync su 120 giorni**: backtest COMPLETO di ogni gamba) **+**
`GetPerformanceAsync(-90gg)` (**seconda SimulateAsync su 90 giorni**: tutti i backtest di nuovo).
Con K gambe sono ~2K backtest interi ogni 15 secondi per ogni pagina Ensemble aperta, senza
cache. È CPU del server bruciata per ridisegnare numeri che cambiano solo quando cambiano config
o candele.
*Fix:* (1) una sola simulazione per refresh — lo status è derivabile dalla stessa corsa; (2) cache
in `EnsembleManager` con chiave (hash config, ultima candela della serie): invalida solo su nuova
candela o config cambiata → il poll diventa una lettura; (3) poll a 60s — il timeframe minimo
delle corsie è 1h, a 15s si ricalcola 240 volte per candela nuova.

### M2 — Le due query killer sul DB (misurate dal vivo)
- [Home.razor:189](ProcioneMGR/Components/Pages/Home.razor): `OhlcvData.LongCountAsync()` senza
  filtro = **4–7 secondi** di seq scan su 12,65M righe, a ogni apertura della Home, per la
  statistica-vetrina «Candele in archivio». Fix: stima da `pg_class.reltuples` (ms, precisione
  più che sufficiente per una vetrina) o cache col TTL di 15 min.
- [SeriesFreshnessWatchWorker.cs:67](ProcioneMGR/Services/Ingestion/SeriesFreshnessWatchWorker.cs):
  `GROUP BY Symbol, Timeframe → max(TimestampUtc)` = **15,2 secondi** misurati, ogni 15 minuti.
  L'indice per-serie ESISTE già (le query `ORDER BY TimestampUtc DESC LIMIT n` per serie girano
  in 1–67ms): un loop sulle 221 serie della watchlist con `max` indicizzato costa ~1s totale,
  15 volte meno, e scala col numero di serie invece che con le righe.

### M3 — VALIDAZIONE: non è troppo rigida — è muta sull'aritmetica. Due modifiche, nessuna soglia toccata
La domanda era: «sta scartando buone strategie per motivi formali?» Risposta secca: **no, e
abbassare le soglie sarebbe fabbricare false scoperte**. DSR>0,95 + PBO<0,5 + N effettivo +
gemello nullo (200 twins, 99°) è una catena statisticamente onesta, e l'esperimento di controllo
(edge piantato → DSR 1,00) dimostra che quando l'edge c'è, la macchina lo vede. Il vincolo
stringente non è la soglia: è la FINESTRA — su 4 mesi, un Sharpe ~1 non può superare il DSR per
aritmetica (servirebbero ~6 anni di dati). Già misurato e scritto nei tuoi stessi doc.

Il difetto reale è che la pipeline questa aritmetica non la DICE. Due modifiche flessibili-ma-sicure:

1. **Power check preventivo** (nuovo stage, prima dei backtest): dati finestra, timeframe e numero
   di candidati, calcola lo *Sharpe minimo rilevabile* per passare il DSR. Se nessun candidato
   realistico può passare, il run lo dichiara IN TESTA («con 4 mesi a 1h e 300 tentativi, passa
   solo Sharpe ≥ 2,4: questo run non può produrre promossi — allunga la finestra o riduci i
   tentativi») invece di bruciare ore di CPU per scrivere «0 sopravvissuti». Costo: una formula,
   zero backtest.
2. **Fascia grigia → forward Paper, non cestino**: oggi il gate è binario. I candidati con DSR
   sotto soglia ma sopra una fascia (es. 0,80–0,95) e gemello nullo battuto finiscano in una
   proposta esplicita: «non promuovibile per statistica, candidabile al forward test Paper» — con
   bottone che lo mette in corsia Paper. È il TUO verdetto («il forward test Paper è l'unico
   giudice sotto i 6 anni di dati») trasformato in flusso: la flessibilità sta nell'instradare
   verso il giudice giusto, non nell'ammorbidire quello sbagliato. Mai oltre Paper in automatico,
   come sempre.

Più una verifica di coerenza: il gemello nullo (200 backtest per candidato!) deve girare SOLO sui
sopravvissuti degli altri gate, mai sull'intero batch — se l'ordine degli stage già lo garantisce,
scriverlo nel doc dello stage; se no, è un ordine da correggere.

### M4 — KPI di `/trading` che mescolano ambiti
Dal vivo: «TOTAL PNL 0,00» accanto a «TRADES 159» — il PnL è di sessione (azzerato al riavvio
corsia), i trade sono lifetime dal DB. Due ambiti nella stessa riga senza etichetta = il lettore
deduce che 159 trade hanno prodotto zero. Etichettare («sessione» / «storico») o separare.

### M5 — Il catalogo strategie non porta i propri verdetti
14 strategie proposte alla pari nel dropdown di Backtest/Ensemble, come se fossero equivalenti.
La piattaforma SA che non lo sono: ha 10 verdetti negativi sul direzionale-tecnico puro e
sopravvissuti solo nella famiglia meta (RegimeConditional). Portare il verdetto NEL selettore:
ordinamento con le meta in testa, e sotto ogni voce base una riga onesta («base: mai sopravvissuta
ai gate da sola — utile come gamba di RegimeConditional»). Il costo è una stringa; il beneficio è
che lo strumento smette di suggerire implicitamente la strada che ha già misurato come morta.

### M6 — 33 pagine: consolidamento leggero, non amputazione
La nav a 6 sezioni con descrizioni regge bene il numero — non c'è il «mare in cui perdersi» che
temevi, la command palette e le guide per pagina aiutano. Ma tre coppie sono fusioni naturali
quando capiterà di metterci mano: `market/bars` dentro `market-analysis` (è un'analisi statistica
dei dati, stessa sezione), `volatility` dentro `regimes` (la banda GARCH è già input del trigger
di regime), `experiments` dentro `registry` (due viste dello stesso ciclo di vita ML). Da 33 a 30,
zero funzioni perse.

---

## 🟢 Cosa MANTENERE e POTENZIARE

- **La macchina di validazione è il prodotto.** DSR + PBO + N effettivo + CPCV + gemello nullo +
  esperimento di controllo: è ciò che ha trasformato «445k combinazioni» in «zero false
  promozioni» e ha prodotto dieci NO puliti più UN sì (carry). Una piattaforma che non si fa
  mentire dai propri backtest è rara. Non toccarla se non per farla *parlare* (M3).
- **I guardrail testuali nella UI.** Dal vivo: «il meta-labeling amplifica un edge, non lo crea»,
  l'half-Kelly consigliato con la ragione, «mai a Live in automatico», le barriere «dalle
  escursioni reali, non da numeri a mano». È cultura di rischio scritta dentro lo strumento, ed è
  ciò che distingue questa piattaforma da un generatore di illusioni. Estenderla (M5 è esattamente
  questo pattern).
- **Carry delta-neutro + forward test Paper 3 corsie + sentinella d'ombra B3.** L'unico edge
  positivo misurato e il suo giudice, con la strumentazione per decidere sui tick. Questo è il
  filone da POTENZIARE: più capitale di attenzione qui, meno ovunque altro.
- **I flussi di lavoro cuciti**: handoff Backtest→Optimization precompilato, preset per-utente
  («Ripristinata l'ultima configurazione»), workflow guidato in Home, breadcrumb, command palette.
  Dal vivo si sente: la piattaforma accompagna invece di disperdere.
- **Il battito e i controlli che sanno dire di no** (Filone E): «ultima candela … 0 barre
  indietro», ordini respinti con la ragione («troppo ravvicinati, minimo 10s») visibili in
  tabella. Verificato funzionante col motore in-cluster.
- **L'isolamento per corsia** end-to-end (UI → engine → DB → lease): regge, ed è la base che rende
  possibile la modifica M3.2 (fascia grigia → corsia Paper) senza rischio.

---

## 📚 Benchmark esterno (aggiunto 2026-08-01, su richiesta)

Ricerca online per ancorare i giudizi a riferimenti indipendenti — dettaglio completo e fonti nel
[PRD-VALORE-2026-08 §1](docs/PRD-VALORE-2026-08.md):

- **La validazione è allineata allo standard della letteratura.** DSR+PBO+N effettivo è
  esattamente il framework Bailey–López de Prado (JPM 2014, SSRN 2012); Harvey–Liu–Zhu richiedono
  **t>3,0** per una scoperta nuova e dimostrano che l'haircut da multiple testing è non lineare.
  I dieci no del direzionale-tecnico e «445k→0» sono l'esito che la letteratura PREVEDE, non un
  sintomo di rigidità. La stessa letteratura dà lo strumento che manca: la formula **MinTRL**, che
  rende calcolabile a priori quale Sharpe può passare su quale finestra (→ power check, M3.1).
- **Carry: reale e in compressione.** Evidenza 2025-26: funding tipico 0,01-0,03%/8h nei regimi
  positivi, ma i basis trade sono passati da ~25% (inizio 2024) a **<5%** — l'edge positivo va
  dimensionato ora (capacità, universo, flip), non custodito.
- **Market-neutral su coppie: supporto esterno alla diagnosi interna.** Studi 2024-25 riportano
  Sharpe 1,6-2,45 su coppie cointegrate crypto — da rimisurare SEMPRE coi costi propri (gli
  Sharpe accademici sono tipicamente pre-costi), ma la classe coincide con la diagnosi della
  roadmap intraday.
- **Cosa il benchmark NON giustifica**: abbassare soglie, automatizzare oltre Paper, fidarsi di
  Sharpe non rimisurati.

## 🧠 Sintesi del «Perché»

**L'aggiornamento ha reso la piattaforma uno strumento migliore per trovare metodi di
investimento — e lo si vede proprio da ciò che NON ha trovato.** Dieci verdetti negativi puliti
sul direzionale-tecnico più uno positivo sul carry non sono un fallimento della macchina: sono la
macchina che funziona. Il valore aggiunto vero non è nelle 33 pagine, è nella catena che rende
ogni risultato difficile da falsificare.

Il debito accumulato dall'espansione è reale ma è di tre tipi precisi, tutti a costo di correzione
basso: **spreco di calcolo** (Ensemble che rifà 2K backtest ogni 15s, count da 7s, group-by da
15s — giorni di lavoro, non settimane), **superfici sopravvissute ai propri verdetti** (metriche
orfane del processo giusto, DTW ancora sul banco di lavoro, catalogo che tace i propri no), e
**una validazione che scarta correttamente ma non spiega perché** — il che spinge chi la usa a
sospettare rigidità dove c'è solo aritmetica, ed è il sospetto che ha generato questa stessa
domanda.

La risposta alla tua domanda fondamentale, senza filtri: **più complessa sì, più lenta in tre
punti misurati e correggibili, ma non più confusa — e decisamente più onesta.** Il rischio da
guardare non è il bloat: è continuare a dare superficie di lavoro uguale a strade che la tua
stessa macchina ha chiuso, pagando in attenzione ciò che non paghi più in CPU. Le priorità
operative in ordine: M1+M2 (una giornata, si ripaga per sempre), M3 (la validazione che parla),
R1 (metriche ricollegate al motore), M5+R3+R4 (l'igiene dei verdetti in UI).
