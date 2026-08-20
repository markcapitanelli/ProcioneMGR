# Revisione di tutti gli algoritmi — 2026-08-20

Richiesta del proprietario: rivedere **tutti** gli algoritmi, non solo quelli aggiunti dall'ondata di
integrazione, controllando anche la loro **integrazione** e la loro **configurabilità via UI**.

**Metodo**: otto revisori indipendenti, uno per famiglia di algoritmi, ciascuno con tre domande fisse
(correttezza · integrazione · configurabilità); poi tre scettici per ogni ritrovamento, incaricati di
demolirlo. 53 ritrovamenti grezzi.

---

> **Stato al 2026-08-20, sera: chiuso.** Le undici decisioni sono state prese dal proprietario e
> tutte e dieci le correzioni che ne discendono sono in codice (A5 si è chiuso correggendo A1). Il
> dettaglio, con le due tesi che la verifica ha ribaltato, è in *«Gli undici che aspettavano una
> decisione»* più sotto. **Due numeri di gate cambiano**: la soglia di significatività del
> comparatore ensemble e i costi applicati alla fase di selezione.

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

### Gli undici che aspettavano una decisione — CHIUSI il 2026-08-20

Undici ritrovamenti non erano stati toccati perché ognuno richiedeva una scelta non tecnica. Prima di
portarli al proprietario sono stati **ri-ancorati al codice** — un investigatore per ritrovamento, e
per i cinque di gravità alta uno scettico incaricato di demolire la caratterizzazione. **Tutti e
undici erano ancora veri a HEAD**, ma la verifica ha cambiato due cose sostanziali e ne ha corretta
una terza.

**A5 non era una decisione: era A1 visto dal lato del consumatore.** Il trigger contestuale legge
`ForecastVolatility24`, cioè proprio il campo che A1 riempiva con un numero giornaliero. Ed era più
grave di come era stato scritto: con banda 1,5 il ramo «compressione» era **vero per aritmetica** su
ogni timeframe intraday (r/f = 0,20 su 1h, 0,41 su 4h, 0,026 su 1m), quindi il braccio volatilità era
una costante TRUE etichettata «compressione»; il ramo «espansione», il caso per cui la funzione fu
scritta, chiedeva un'esplosione di 7,3× invece di 1,5×. E ogni sveglia bypassava il `BackoffHours`
della campagna. Le decisioni erano quindi **dieci, non undici**.

**A2 era sopravvalutato, e la sua correzione «minima» non era neutra.** Lo scettico ha reimplementato
il CSCV e simulato in Monte Carlo: su un pannello misto (1 serie 1d + 9 serie 1h troncate) il PBO
medio viene **0,505**, non ≈0 — cioè il valore di un pannello di rumore, esattamente ciò che il PBO
deve dire. La tesi «così il gate non scatta mai» è falsa, e lo dimostra anche un fatto già scritto:
un batch reale è stato bloccato con PanelPbo 0,619. Il difetto vero non è di cecità ma di
**validità**: il verdetto poggia sulla finestra comune (misurato: dal 4% al 24% della più lunga) e le
serie lunghe vengono tagliate in **coda**, senza riallineamento per calendario. Soprattutto:
«lanciare invece di troncare» **spegne un gate bloccante** appena una serie ha una candela in meno —
un listing recente, un buco di ingestione — e quindi *apre* gambe verso le corsie. È il contrario di
una scelta prudente.

**M3 partiva da una premessa sbagliata.** `FundingHistory` *è* popolata, ma solo dalla pagina
/backtest e solo con leva > 1; nel percorso pipeline la leva resta 1, quindi entrambe le fasi girano
sulla costante e la differenza è secca: **selezione 0, validazione 0,01%/8h**. Il difetto non era
inerte.

Due precisazioni che cambiano il peso di altrettanti punti: `CurrentAllocation` **non è inerte** — è
già il peso con cui si calcola lo Sharpe medio che alimenta il gate di sostituzione, sulle sole
corsie 0-2 (`AutoApplyLaneFootprint = 3`); e la sotto-copertura del bracket a fette è
**permanente**, non transitoria, perché il trigger non viene ri-armato nemmeno a piano completato.

#### Le decisioni prese, una per riga

| # | ritrovamento | decisione | conseguenza |
|---|---|---|---|
| A1 | Il forecast di volatilità cambia unità sotto un contratto che dice «per-period» | **Riscalare la stima HAR al timeframe** (σ_candela = √(RV_giorno × min_tf/1440)) | i tre campi rientrano nel contratto; il `ratio` resta identico bit-per-bit, quindi Level, dosaggio e gate C3 **non cambiano**; i valori mostrati in UI si dividono per 4,90 su 1h e 2,45 su 4h |
| A2 | Il PBO di pannello mescola timeframe e tronca alla serie più corta | **Vietare gli universi a timeframe misti** in `ValidateInput`; l'allineamento per data resta aperto come lavoro a sé | zero cambiamenti a qualunque run esistente (le campagne sono già a timeframe singolo): si fissa per iscritto un'ipotesi finora implicita. Il troncamento residuo ora **si dichiara nel log del run** con la frazione di finestra su cui poggia il verdetto |
| A3 | I pesi di allocazione non dimensionano nulla | **Dichiararlo** | nessun ordine cambia taglia. La guida di /ensemble non promette più una divisione di capitale che non avviene, la colonna «Alloc %» dice cos'è, e `SignalOrderBuilder` porta scritto **perché** il peso non entra — così nessuno lo «corregge» senza sapere che tocca l'esposizione aggregata e la parità col backtest |
| A4 | Lo z-score del comparatore mescola Sharpe annualizzato e conteggio trade | **Correggere l'unità E ritarare la soglia nello stesso commit**: `HoldoutMonths` al denominatore, `MinSharpeSignificanceZ` da 1,0 a **0,35** | correggere l'unità da sola avrebbe chiuso di fatto la via-Sharpe della ri-applica (a 4 mesi servirebbe ΔSharpe ≥ 1,73). Con 0,35 serve Δ ≥ 0,61: circa 3× più stretto del punto operativo di prima, senza cadere nel gate che non passa mai |
| A5 | Il trigger contestuale confronta volatilità con basi temporali diverse | **Chiuso da A1**, senza toccare `RegimeChangeDetector` | il braccio volatilità torna a scattare quando la volatilità esce davvero dalla banda. In più la motivazione non dice più «forecast GARCH» quando il previsore è il log-HAR |
| M1 | `FactorDrift` governa un worker vivo senza pannello | **POCO `FactorDriftOptions` + pannello in /admin/autonomy + riga d'inventario + regole di validazione** | il pannello dichiara la **copertura**: con i default un giro completo sulla watchlist costa settimane, e «nessun allarme» poteva voler dire «non l'ho ancora guardata» |
| M2 | Due scrittori per `SavedMlModel.DeflatedSharpe` con N diversi | **Persistere N e provenienza; il gate «batti l'incumbent» rifiuta il confronto fra grandezze diverse** | /ml scrive un DSR a N=1 (non deflazionato, senza slippage né funding), la pipeline uno deflazionato su centinaia di tentativi coi costi pieni. Ora la promozione si **rifiuta** se i due N differiscono di oltre un ordine di grandezza o uno non è dichiarato. Inerte oggi (nessun Champion in carica), armato per il giorno che ce ne sarà uno |
| M3 | Il funding entra nella validazione ma non nella selezione | **Propagarlo alla selezione**, come [R2] fece per lo slippage negli stessi file | **cambia numeri che alimentano un gate**: gli Sharpe IS/OOS scendono per i long-biased e salgono per gli short, quindi cambia chi supera `minOosSharpe`. I run archiviati restano su un'altra base di costo |
| M4 | Con l'esecuzione a fette il bracket protegge solo la prima fetta | **Dichiarare e vietare la combinazione**: con gli stop resting attivi il piano a fette non si costruisce e si apre a quantità piena | regola 4, fail-closed: fra una protezione che sopravvive al processo e la riduzione d'impatto vince la protezione. Nessun numero a valle si muove; ogni soppressione finisce in log e in audit (`ExecutionPlanSkippedForRestingStops`) |
| M5 | Due convenzioni di risk-free nel decay monitor | **Dichiarare l'incomparabilità e mostrarne l'ampiezza** (rf/σ_annualizzata) | sottrarre lo stesso rf al realizzato sarebbe sbagliato di circa un ordine di grandezza: le due serie non condividono la base di capitale. Renderle davvero confrontabili richiede di persistere la taglia dell'holdout — lavoro a sé |
| M6 | Il Campaign Planner chiama «sopravvissuti» le gambe dell'ensemble | **Dichiarare entrambi i numeri**: gambe proposte, sopravvissuti pieni, e quante vengono dalla fascia grigia | un ensemble di sole gambe grigie non si legge più come un successo pieno. Nessun ramo decisionale cambia |

#### Restano aperti, e sono scelte non rinvii

- **L'allineamento del pannello PBO per DATA** invece che per indice. Chiude la metà del difetto che
  il divieto sui timeframe misti non copre (buchi di copertura fra simboli a parità di timeframe), ma
  cambia il numero di un gate bloccante su **tutte** le configurazioni in uso: va misurato prima su un
  run archiviato, non applicato al buio.
- **Il bracket che si ri-arma a ogni fetta**, cioè la correzione «vera» di M4. Prezzo: fino a 44
  round-trip firmati in più per piano, e il rischio di trigger orfano moltiplicato per il numero di
  fette — su un percorso che nessuno ha mai visto scattare dal vivo.
- **Il confronto realizzato-vs-atteso su una base di capitale comune** (M5 opzione C): colonna nuova,
  migrazione, e un percorso di degrado dichiarato per le gambe già schierate.
- **Collegare i pesi di allocazione alla taglia** resta possibile, ma dopo aver chiuso la parità col
  backtest: oggi `EnsembleManager.BuildBtConfig` gira ogni gamba a `PositionSizePercent = 100` e non
  conosce pesi per gamba. Collegarli senza allineare il backtest rifarebbe la stessa classe di
  difetto corretta poche ore prima sullo specchio della posizione.

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

---

## 5. Come sono state verificate le dieci correzioni

Lo standard del progetto chiede quattro livelli (`docs/STANDARD-VERIFICA.md`). Ecco cosa è stato
fatto e — punto più importante — **cosa non si è potuto fare e perché**.

**Livello 1, unità contro un riferimento indipendente.** `RevisioneAlgoritmi2026_08Tests` più
l'aggiornamento di `EnsembleComparatorTests`: 50 casi. Diversi sono scritti apposta per essere
*falsificabili*, cioè per fallire se l'unità sbagliata rientrasse in silenzio:

- lo z del comparatore è provato contro il calcolo a mano `z = ΔSharpe × √(anni)` su quattro coppie,
  e un caso separato verifica che il **conteggio trade da solo non accenda più il gate** — senza
  quello, l'unità vecchia potrebbe tornare senza che nessun test protesti;
- A1 è provato sul **fattore di scala** (4,899 su 1h, 2,449 su 4h, 1,000 su 1d) e non su un valore,
  più un caso che dimostra l'invarianza del `ratio` — cioè la ragione per cui la correzione non
  tocca il gate C3;
- A5 ha un caso che riproduce **il difetto**, non la correzione: con forecast giornaliero contro
  realizzata oraria, `Evaluate` dichiara «compressione»; con le due misure sulla stessa base, tace.

**Livello 2, controllo sul rumore.** Non applicabile: nessuna di queste dieci correzioni introduce
una stima nuova da cui si possa estrarre un edge. Le due che spostano numeri di gate (A4, M3) li
spostano per **aritmetica dichiarata**, non per una misura da validare.

**Livello 3, integrazione reale.** Suite completa sui Testcontainers: **2969/2971 al primo giro**,
**2978/2978 sul codice finale**.

Dei due rossi del primo giro, uno era un timeout di connessione Npgsql sotto carico
(`LaneRiskProfileEndToEndTests`, verde da solo) e **uno era una regressione vera della correzione
M2**: il test di promozione a Champion confrontava due DSR di provenienza non dichiarata, che è
esattamente ciò che il gate nuovo rifiuta. È stato aggiornato **il test, non il gate**, e sono stati
aggiunti i tre casi che mancavano: il rifiuto per N incomparabili (0,97 su 1 tentativo contro 0,60 su
800), il rifiuto per N non dichiarato, e la **prima promozione in assoluto** — che non deve
richiedere la provenienza, perché senza incumbent non c'è alcun confronto da rifiutare. Senza
quest'ultimo caso, la correzione avrebbe potuto bloccare per sempre la prima promozione.

### Due difetti trovati rileggendo il proprio diff

Vale la pena scriverli, perché sono entrambi della classe «la correzione fa più di quanto dichiara».

- **Le tre proprietà calcolate di `FactorDriftOptions` sarebbero finite dentro `appsettings.json`.**
  `AppConfigWriter.SaveSectionAsync` riscrive la sezione serializzando il POCO **intero**, e le sue
  opzioni non ignorano le proprietà in sola lettura: al primo «Salva» dal pannello nuovo, tre chiavi
  inventate nel file vivo. Convertite in **metodi**, che è la ragione per cui
  `DriftMonitorOptions.EffectiveStages()` è un metodo. Il guardiano per chiavi non protegge da
  questo: salta le sole-lettura, quindi resta verde mentre il file si sporca.
- **Il divieto di M4 spegneva le fette anche sullo Spot**, dove il bracket resting non viene mai
  piazzato (vive solo in `ExecuteFuturesOpenAsync`). Sarebbe stata un'esecuzione peggiore in cambio
  di nessuna protezione. Il predicato ora richiede `MarketType.Futures`, e c'è il caso che lo prova.

**Livello 4, la superficie che l'operatore legge.** Fatto con i test di rendering, **non sull'app
viva**, e la ragione va scritta: l'istanza principale era in esecuzione sulla porta 5199 con il
codice precedente, e avviarne una seconda da questo worktree avrebbe messo **due scrittori sullo
stesso database** — regola 2. Tre casi nuovi in `RegistryPageRenderTests` provano che la colonna
«Misurato su» distingue i tre stati (pipeline con N, ml-lab con N, metro ignoto) e che la guida
dichiara il rifiuto del gate.

Per il pannello di M1 la prova è più forte di uno screenshot: `ConfigurationKeyUiCoverageTests`
pretende che **ogni chiave** di ogni POCO di configurazione sia nominata da almeno una pagina, e
passa. Che non passi per compiacenza è stato verificato inserendo una proprietà fantasma in
`FactorDriftOptions`: il test è diventato rosso **nominandola**, e rimuovendola è tornato verde.

> **Resta da fare sull'app vera**, ed è del proprietario: dopo il merge e il riavvio dell'istanza
> unica, guardare `/admin/autonomy` (card «Deriva dei fattori alpha», e la nota sulla z sotto le
> soglie di consolidamento), `/ensemble` (il riquadro su «Alloc %»), `/registry` (colonna «Misurato
> su») e `/trading` (l'avviso sulla spunta degli stop resting). La **migrazione**
> `AddDeflatedSharpeProvenance` aggiunge due colonne nullable a `SavedMlModels` e si applica
> all'avvio.

---

## 6. Livello 4 eseguito sull'app vera (2026-08-20, dopo il merge)

Mergiata la PR #99, il repo principale riportato su `master` e riavviato `procione-main`. Le
superfici sono state guardate una per una sul database e sul motore reali. **Il collaudo ha trovato
più di quanto cercava.**

### Tre migrazioni erano ferme dal 19 agosto, e nessuno poteva saperlo

Al primo riavvio il migratore ha rifiutato di dichiarare lo schema allineato:

> «Nessuna migrazione pendente (**20 note**), ma il MODELLO differisce dallo snapshot dell'ultima
> migrazione conosciuta … l'assembly `ProcioneMGR.Migrations.Postgres` accanto all'eseguibile è
> VECCHIO — `dotnet build` dell'app non lo ricostruisce.»

È la trappola nota della DLL delle migrazioni, ma vista dal lato che non era mai stato pagato: non
sbaglia la *generazione*, impedisce l'**applicazione**. Ricostruito il progetto nella stessa
configurazione con cui gira l'app (`-c Release`), le migrazioni note sono passate da 20 a 24 e ne
sono state applicate **quattro**:

| migrazione | quando | cosa mancava a database |
|---|---|---|
| `AddCampaignPausedUntil` | 19/08 | colonna della pausa campagne |
| `AddPairCandidates` | 19/08 | **tabella `PairCandidates`** |
| `AddPairSpreadWindows` | 19/08 | **tabella `PairSpreadWindows`** |
| `AddDeflatedSharpeProvenance` | 20/08 | le due colonne di M2 |

Le due tabelle del Filone I **non esistevano**: i sottosistemi mergiati il giorno prima giravano
contro tabelle assenti, per un giorno intero. Il migratore ha fatto il suo mestiere — fail-closed sul
*verdetto*, non sull'avvio — ma l'unica traccia era un `fail:` in un log che nessuno guarda.
**Regola nuova: dopo ogni merge che contenga una migrazione, `dotnet build
ProcioneMGR.Migrations.Postgres -c Release`, e leggere la riga `DatabaseMigrator`.** La spia è il
numero fra parentesi: se «N note» è più basso del numero di file in `Migrations/`, la DLL è vecchia.

### Le sei superfici, con i numeri veri

| dove | esito |
|---|---|
| `/admin/autonomy#p-factordrift` | card presente e **ATTIVO**, quattro manopole editabili coi vincoli giusti (serie 1..30, candele 1.000..200.000). La riga di copertura dice, sui dati veri: *«con 5 serie ogni 12 ore, un giro completo su **222 serie** richiede circa **22,5 giorni** (45 giri)»*. È esattamente il numero che nessuno poteva conoscere prima |
| `/admin/autonomy#p-thresholds` | **z di significatività = 0,35** letta dalla configurazione viva: il default nuovo è in vigore. La nota con la tabella ΔSharpe per l'holdout a 4 mesi è al suo posto |
| `/registry` | la colonna «Misurato su» rende. **164 modelli, ZERO con un DSR**: tutte le righe mostrano «—», non «metro ignoto». Vedi sotto |
| `/ensemble` | il riquadro su «Alloc %» e la voce di glossario ci sono; il tooltip sull'intestazione dice *«NON dimensiona gli ordini»*. La corsia 4 (XRP/USDT) ha una gamba sola al 100%, di fascia grigia |
| `/trading` | l'avviso sulla spunta degli stop resting rende per intero, **inclusa la qualificazione sullo Spot** aggiunta dopo la rilettura del diff. La spunta è spenta. La configurazione di sicurezza è letta dal MOTORE via gRPC: il port-forward regge |
| `/campaign` | gli esiti reali dicono **«nessuna gamba»** invece di «0 sopravvissuti», su entrambe le configurazioni |

Zero errori in console dopo il riavvio, zero errori server.

### Il difetto di M2 è inerte per una ragione in più di quella prevista

Si sapeva che il gate «batti l'incumbent» non poteva scattare perché **non esiste alcun Champion**.
Il collaudo ne aggiunge una seconda, più profonda: su **164 modelli salvati, nessuno ha un Deflated
Sharpe**. La colonna è vuota ovunque, quindi non c'è nemmeno il numero da confrontare — e
`PersistMlDeflatedSharpeAsync`, lo scrittore della pipeline, a quanto pare non ha mai scritto nulla,
pur essendoci 164 modelli quasi tutti chiamati `Pipeline <hash>`. **È un'osservazione, non una
diagnosi**: va misurata a parte, ed è della stessa famiglia dei gate senza soggetto. Conseguenza per
il collaudo: i tre badge di provenienza non sono osservabili sull'app vera, e restano coperti dai soli
test di rendering.

### Il codice nuovo ha già girato da solo

Dopo il riavvio, senza che nessuno lo chiedesse:

- il **worker della deriva** è partito leggendo il POCO nuovo — `FactorDriftWorker avviato (ogni 12h,
  Enabled=True, 5 serie/giro)` — e ha registrato finestre nuove per DOT/USDT;
- un **run di pipeline** ha attraversato `HoldoutValidation` **senza essere saltato**: il divieto di
  A2 sui timeframe misti non ha rotto le configurazioni reali, che sono a timeframe singolo. Il gate
  ha girato con N = 6.120 tentativi effettivi (64 candidati, 8.160 combinazioni, collasso 0,75), e la
  riga nuova sul troncamento del PBO **non è comparsa** — perché su quel pannello le serie avevano
  tutte la stessa lunghezza. La dichiarazione parla solo quando c'è qualcosa da dichiarare;
- il **planner** ha scritto la motivazione nella forma nuova, con la provenienza: *«Campagna 2:
  Nessuna gamba schierabile (config 18 — **0 sopravvissuti pieni su 64 candidati**): prossima config
  della rotazione»*. Prima avrebbe detto «0 sopravvissuti all'holdout», che è un altro numero.

La campagna 2 è in **`WaitingForTrigger`** — «rotazione esaurita senza ensemble schierato, in attesa
di un trigger contestuale». È precisamente lo stato che il braccio volatilità difettoso svegliava a
vuoto fino a quattro volte al giorno, bypassando il backoff di 12h: da adesso quella sveglia arriva
solo se la volatilità esce davvero dalla banda.
