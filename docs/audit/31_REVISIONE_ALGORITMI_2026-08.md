# Revisione di tutti gli algoritmi — 2026-08-20

Richiesta del proprietario: rivedere **tutti** gli algoritmi, non solo quelli aggiunti dall'ondata di
integrazione, controllando anche la loro **integrazione** e la loro **configurabilità via UI**.

**Metodo**: otto revisori indipendenti, uno per famiglia di algoritmi, ciascuno con tre domande fisse
(correttezza · integrazione · configurabilità); poi tre scettici per ogni ritrovamento, incaricati di
demolirlo. 53 ritrovamenti grezzi.

---

## Il risultato in tre righe

1. **La matematica regge.** Nessun look-ahead in 14 strategie, nella catena feature→modello, nei
   modelli di volatilità, nella cointegrazione o nella libreria di rigore statistico. Diverse aree
   sono descritte come «sopra la media del progetto».
2. **I difetti stanno al CONFINE fra i pezzi**, non dentro i pezzi: cose calcolate bene che non
   arrivano dove servono, o che arrivano in un'unità diversa da quella attesa.
3. **Il difetto più grave era vivo su due corsie**, ed è corretto: il backtest validava una strategia
   e il motore ne operava un'altra.

---

## 1. Il difetto grave, verificato e corretto

### Il backtest validava una strategia, il motore vivo ne operava un'altra

Quattro strategie tengono uno stato interno che rispecchia la posizione aperta — i loro stessi
commenti lo dicono: *«specchio della posizione del motore»*, *«mirrors the engine position»*.

- Nel **backtest** il motore istanzia la strategia **una volta** e scorre le candele: lo specchio si
  mantiene.
- Nel **trading vivo** `TradingEngine` crea un'istanza **nuova a ogni candela** e chiama
  `InitializeAsync`, che azzera lo specchio.

Conseguenza: i rami di uscita che dipendono da `_side != 0` **non si raggiungevano mai**.

| strategia | stato | effetto dal vivo |
|---|---|---|
| `GridMeanReversion` | `_side`, `_entryPrice` | apriva, e **non prendeva mai il proprio profitto** |
| `DonchianBreakout` | `_side` | le uscite dal canale non scattavano |
| `EventTrigger` | `_openIndex` | l'uscita a tempo — «priorità assoluta» nel suo commento — mai |
| `RegimeConditional` | `_lastBucket` | **`Close` a ogni barra, apertura MAI** |

`RegimeConditional` è il caso estremo: `_lastBucket` parte da −1, e con un'istanza nuova per candela
`bucket != -1` è sempre vero — la strategia non poteva aprire una posizione.

**Al 2026-08-20 `GridMeanReversion` girava su due corsie Paper vive**: la 4 (XRP/USDT) e la 5
(UNI/USDT). La corsia 4 aveva chiuso **un solo trade in sedici giorni** — il dato che nella misura di
I12 sembrava semplice inedia.

**È la peggior specie di difetto d'integrazione**: ogni conclusione tratta da quelle corsie era su un
oggetto diverso da quello validato, e nessuna superficie lo diceva.

### La correzione

Due forme diverse per due problemi diversi.

- **`RegimeConditional`: si toglie lo stato.** Il cambio di regime è un fatto dei *dati*, non della
  sessione: si confronta il bucket della barra corrente con quello della **precedente**. I due motori
  ora danno lo stesso risultato **per costruzione**, non per fortuna. È la correzione migliore delle
  due, perché non lascia niente da tenere allineato.
- **Le altre tre: il motore ridice alla strategia ciò che già sa.** Nuova interfaccia
  `IPositionMirroringStrategy`: dopo `InitializeAsync`, `TradingEngine` rimette lo specchio in pari
  con la posizione **vera**, che è la sorgente autorevole.

*Perché non cachare l'istanza*: `InitializeAsync` va comunque richiamato a ogni barra (ricalcola gli
indicatori sulla finestra che si allunga), quindi l'azzeramento resterebbe — con in più uno stato
lungo da gestire.

I test riproducono **il ciclo del motore vivo**, non quello del backtest: un test con una sola
istanza sarebbe verde anche col difetto presente. Uno di essi mostra lo stesso scenario con e senza
il ripristino, e i due danno due strategie diverse.

---

## 2. Configurabilità: da 19 chiavi scoperte a zero

Il guardiano esistente (`ConfigurationUiCoverageTests`) pretende che ogni **sezione** abbia una
pagina. È il guardiano giusto per la domanda che pone, ma lascia un buco: **una chiave nuova dentro
una sezione già mappata passa senza pannello e nessun test protesta**.

Lo spoglio meccanico di tutte le proprietà dei POCO di configurazione — POCO annidati compresi — ha
trovato **19 chiavi su 190** governabili solo editando `appsettings.json` a mano.

| gruppo | chiavi | perché conta |
|---|---|---|
| `Trading:Safety` | 3 | banda di plausibilità del **prezzo** e della **quantità** di fill, e stop piazzati sull'exchange. La banda esiste per il bug B1, dove un testnet rispondeva «Filled @ 0» e il PnL segnava **−1,8 milioni** |
| `Sentiment` pesi | 5 | decidono quanto ogni fonte pesa nel composite: cambiarli cambia il segnale |
| `Llm` | 6 | catena di **failover**, timeout per provider, endpoint. Il layer AI si è già fermato in silenzio una volta |
| `PromotionEvaluator` | 2 | settimane minime di retrocessione (la metà mancante della soglia già esposta) e audit dei cambi di modalità |
| `Sentiment` altre | 3 | baseline, retention metriche, percorso del modello ONNX |

**Tutte esposte.** Il percorso ONNX non è una manopola ma un vincolo di deploy: si **mostra** accanto
al badge «non addestrato», così un comportamento inspiegabile ha una spiegazione a portata di mano.

### E il guardiano ora vede le chiavi

`ConfigurationKeyUiCoverageTests` scende nei POCO annidati e pretende che **ogni chiave** sia nominata
da almeno una pagina. Verificato che **può fallire**: inserita una proprietà fantasma in
`FleetOptions`, il test è diventato rosso nominandola; rimossa, è tornato verde.

*(Il primo tentativo di sonda l'ho messo in `CarryConfiguration` e il test **non** è fallito — perché
quella non è una sezione di configurazione ma un oggetto di dominio. Il guardiano aveva ragione a
ignorarla.)*

---

## 3. Che cosa è risultato SANO

Vale la pena scriverlo, perché una revisione che elenca solo i difetti non dice quanto è grande il
resto.

- **Strategie e indicatori**: nessun look-ahead in 14 strategie. EMA con seed SMA, RSI/ATR con
  smoothing di Wilder, Donchian con deque monotone, VWAP ancorato alla sessione UTC. Donchian e Grid
  confrontano la close col canale/ancoraggio della barra **precedente** — deliberatamente. Catalogo e
  factory allineati 1:1, nessun orfano in nessuna delle due direzioni.
- **ML e feature**: catena causale integra, target strettamente futuro, normalizzazione **dentro** la
  pipeline ML.NET (nessun leakage di scala), split train/holdout rispettato in entrambi i percorsi,
  parità train/serve reale sulle finestre di sequenza.
- **Validazione statistica**: PSR, DSR, E[max], MinTRL riproducono fedelmente Bailey–López de Prado.
  Il difetto D-01 sul conteggio dei tentativi **è rimasto corretto**. Il permutation test è a blocchi
  di segno lungo il tempo, coerente con l'errore già pagato il 2026-07-20.
- **Rischio**: `SafetyChecker` è ancora statico e puro (**regola 1 regge**). Il difetto noto
  sull'esposizione aggregata margine-vs-nozionale **è stato corretto**, con test dedicati.
  Ledoit-Wolf secondo il paper, ERC come fixed-point esatto, HRP con distanza di Mantegna.
- **Esecuzione**: `ProtectiveExitEvaluator` puro e condiviso fra percorso a candela e a tick, con
  precedenza allo stop e fill sempre all'esito peggiore. La sentinella d'ombra B3 è rigorosamente in
  sola lettura. Il difetto noto sullo slippage della pagina Pairs **è chiuso**.
- **Serie storiche**: la cointegrazione usa i valori critici di **MacKinnon per cointegrazione**, non
  gli ADF semplici — il punto più facile da sbagliare, ed è giusto.
- **Autonomia**: il confine anti-Live regge su **tre livelli indipendenti**, i run automatici saltano
  le configurazioni Live in tutti e tre i percorsi, e il braccio della flotta è armato solo per
  **fermare**.
- **Regole 3 e 7 rispettate**: nessun percorso automatico verso Live; `DriveDecisions=false` è
  riconosciuto come risultato di una misura e non «corretto».

---

## 4. Gli altri ritrovamenti, dopo la confutazione

I 22 ritrovamenti di gravità alta e media sono passati per tre scettici ciascuno, incaricati di
demolirli. **13 confermati, 9 respinti** — e i respinti non sono sfumature: fra loro c'erano
«il sizing di Kelly non raggiunge mai una corsia», «il regime one-hot etichetta con il modello di
un'altra coppia» e «la promozione non consulta la quarantena», tutti smontati aprendo il codice o
trovando il test che già copriva il caso.

### Corretti in questa sessione (oltre allo specchio della posizione)

Due erano **codice mio della notte precedente**, ed erano entrambi della classe che l'ondata
bonificava altrove:

- **L'estimatore della sorveglianza spread era un'etichetta.** Il worker chiamava *sempre* la rolling
  OLS e poi scriveva sulla riga `PairsWatch:Estimator`: scegliere «Kalman» dal pannello dava OLS con
  scritto Kalman sopra. Non è cosmetico — i due estimatori danno due spread diversi per costruzione,
  ed è la ragione per cui l'estimatore sta nella *chiave* della tabella. Ora l'estimatore decide il
  calcolo, il δ è quello dichiarato dall'analizzatore stesso (lo stesso del backtest), e la riga porta
  l'etichetta di ciò che è stato **davvero** usato.
- **Il dedup dei grigi comprimeva solo dentro un tick.** «Già gestito» era per *run*, non per
  identità: il giorno dopo un run nuovo con la stessa coppia strategia/serie/parametri tornava a
  proporsi come la prima volta. È esattamente il meccanismo dietro le **91 proposte per sei cose
  distinte** misurate il 2026-08-19 — quello dentro il tick non lo toccava. Ora l'identità eredita lo
  stato entro la finestra dei candidati.

### Confermati e NON corretti — decisione del proprietario

Undici. Non li ho toccati perché ognuno richiede una scelta che non è mia: due modificano numeri che
alimentano un gate, tre toccano il dimensionamento reale degli ordini, e per uno la correzione ha due
forme entrambe difendibili.

| gravità | ritrovamento | perché serve una decisione |
|---|---|---|
| alta | **Il forecast di volatilità cambia unità sotto un contratto che dice «per-period»** | il log-HAR produce una stima *giornaliera* che sovrascrive campi dichiarati per-candela. Due correzioni possibili: far guidare al log-HAR solo il `Level`, oppure riscalare la stima al timeframe. Il `ratio` (e quindi il gate C3) non cambia in nessuna delle due |
| alta | **Il PBO di pannello mescola timeframe e tronca alla serie più corta** | e il PBO può bloccare l'intero batch. Minimo: lanciare invece di troncare in silenzio (i due chiamanti già catturano e si astengono). Pieno: un PBO per gruppo omogeneo |
| alta | **I pesi di allocazione non dimensionano nulla** | `CurrentAllocation` è calcolato, persistito e mostrato, ma il motore apre tutte le gambe alla stessa taglia. Due strade: **dichiararlo** (la colonna «Alloc %» dice che serve al confronto, non agli ordini) oppure **collegarlo** — e collegarlo tocca il sizing reale |
| alta | **Lo z-score del comparatore mescola Sharpe annualizzato e conteggio trade** | il denominatore dev'essere l'ampiezza del campione *nella stessa unità* dello Sharpe. Il dato serve e c'è già: `PipelineRecommendation.HoldoutMonths`, introdotto da I11 e oggi ignorato da `SummarizeRecommendation` |
| alta | **Il trigger contestuale confronta volatilità con basi temporali diverse** | stessa radice del primo: scatta o non scatta per un fattore √(barre al giorno) |
| media | **`FactorDrift` governa un worker vivo senza pannello** | ed è una *sezione* che il guardiano per sezione non elenca. Va aggiunta all'inventario **e** dotata di pannello |
| media | **Due scrittori per `SavedMlModel.DeflatedSharpe` con N diversi** | il registry ordina modelli per un DSR calcolato con conteggi di tentativi incoerenti |
| media | **Il funding entra nella validazione ma non nella selezione** | costi diversi fra le due fasi: un candidato può essere scelto senza funding e bocciato con |
| media | **Con l'esecuzione a fette il bracket protegge solo la prima fetta** | una posizione costruita in cinque fette resta scoperta per l'80%. Tocca il percorso di protezione: va fatto con cura, non di notte |
| media | **Due convenzioni di risk-free nel decay monitor** | realizzato e atteso non sono confrontabili come sono |
| media | **Il Campaign Planner chiama «sopravvissuti» le gambe dell'ensemble** | etichetta sbagliata su un numero mostrato all'operatore: correzione banale, ma cambia ciò che un pannello dichiara |

### Respinti dagli scettici — da non rifare

`SR* sottostimato dalla varianza cross-trial` (1/3) · `il buffer del motore riparte vuoto`
(1/3) · `il pavimento di numerosità protegge solo il pannello` (0/3) · `il regime one-hot etichetta
con un'altra coppia` (0/3) · `il composite non dichiara l'età del dato` (0/3) · `il sizing di Kelly
non raggiunge una corsia` (0/3) · `il sizing usa il Kelly binario` (0/3) · `i parametri di /execution
non raggiungono il pianificatore` (0/3) · `la promozione non consulta la quarantena` (0/3).

### I 29 di gravità bassa

Non sono passati per la confutazione e restano **ipotesi**. Riguardano soprattutto etichette e
documenti che divergono dal codice, limiti dichiarati e mai letti, e strumenti raggiungibili solo dal
tool CLI. L'elenco grezzo è nel journal del workflow.
