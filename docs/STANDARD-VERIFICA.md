# Standard di verifica per ogni fase della roadmap

*Nato il 2026-07-27 da un'osservazione del proprietario: «servono più tipi di test per convalidare
le integrazioni». Aveva ragione su un caso concreto — C4 aveva 32 test unitari verdi ma **nessun
consumatore**: era codice registrato in DI e mai chiamato da nessuno. Verde a livello di classe,
inesistente a livello di prodotto.*

## I quattro livelli

Una fase non è "fatta" finché non ha tutti e quattro. Sono livelli diversi perché **falliscono in
modi diversi**: nessuno dei quattro sostituisce gli altri.

### 1. Unità — contro un riferimento indipendente

Non basta che il codice giri: serve un secondo modo, calcolato **diversamente**, di sapere la
risposta giusta.

- SHAP (D1) → confronto con i valori di Shapley calcolati per **forza bruta** su tutti i 2ⁿ
  sottoinsiemi.
- Etichettatura (C4) → serie costruite in modo che l'esito sia noto (tocca solo il profitto, solo lo
  stop, nessuno dei due, entrambi nella stessa barra).
- Estrazione alberi (D1) → **ricostruzione** delle predizioni confrontata col modello ML.NET vero.

*Cosa becca che gli altri non beccano*: la matematica sbagliata. La foresta che media invece di
sommare produceva valori SHAP errati di ~96× che **soddisfacevano comunque l'efficienza**, perché
sbagliavano in modo coerente. Solo il confronto con la struttura vera l'ha visto.

### 2. Controllo — il rumore non deve accendere niente

Ogni strumento che emette un **verdetto** va puntato contro dati privi di segnale, e deve tacere.
Un singolo seme non basta: si misura il **tasso** di falsi positivi su molti semi.

- Deriva dei fattori (D2) → 40 semi di puro rumore, zero allarmi ammessi.
- Meta-modello (C4) → 20 semi di puro rumore, al più 3 verdetti positivi.
- Edge **piantato** (C4, e la fase `control` di PlatformExpand) → il complemento: se l'edge c'è, la
  catena deve trovarlo. Senza questo, un esito negativo direbbe solo "gli strumenti non funzionano".

*Cosa becca che gli altri non beccano*: le soglie che stanno sotto il rumore, **e i nulli
sbagliati**. Tre volte in un giorno:
- la soglia IC a 0,02 su finestre da 300 (pavimento reale 0,058) etichettava il caso come «segno
  invertito»;
- `IsImprovement` dichiarava miglioramento per una precision da 0,477 a 0,529 su dati casuali;
- **il più insidioso**: in D4 il placebo a *date casuali* assolveva pattern casuali su rumore puro
  8 volte su 15, perché selezionare finestre per FORMA induce da sola una deriva nelle barre
  successive e un nullo a date casuali non riproduce quel meccanismo. **Un nullo deve conservare
  il meccanismo di selezione che si vuole smentire** — altrimenti confronta mele con pere e assolve
  tutto. È la stessa lezione del t = 141 sugli asset correlati, in veste nuova.

Corollario emerso lo stesso giorno: **significatività e rilevanza sono due domande diverse**. Un
effetto dello 0,48% può battere il suo nullo ed essere comunque inutile, perché fee e slippage di
andata e ritorno valgono ~0,30%. Ogni verdetto operativo ha bisogno anche di un pavimento
economico, non solo statistico.

### 3. Integrazione — i pezzi veri, montati insieme

Componenti **reali**, non mock, con i percorsi di fallimento inclusi. È il livello che mancava a C4:
`MetaLabelingAnalysisServiceTests` monta strategia vera + fattori veri + indicatori veri e verifica
sia il giro completo sia i rifiuti motivati (troppe poche candele, strategia che opera troppo poco,
serie vuota).

*Cosa becca che gli altri non beccano*: che i pezzi non si parlino. Una libreria corretta e mai
chiamata passa il livello 1 a pieni voti.

### 4. Operativo — dal vivo, nel browser, su dati veri

La funzione dev'essere **raggiungibile e corretta nell'app che gira**, con l'utente reale e i dati
reali. Screenshot o estrazione del testo del pannello, più il controllo di console ed errori server.

*Cosa becca che gli altri non beccano*: tutto ciò che vive fra il servizio e l'occhio. In questa
sessione: un verdetto negativo mostrato in un banner **verde** di successo (la barra di stato
segnalava "ha girato", non "è andata bene"); la finestra di deriva da 250 barre, tecnicamente
corretta ma che rendeva il pannello inutile perché nessun fattore reale superava il pavimento; e
in `/trading` **un banner d'errore che sopravviveva alla propria causa** — caduto il tunnel verso
il core, la valutazione delle promozioni falliva; ristabilito il tunnel tutto tornava a funzionare
ma il rosso restava a schermo, perché il messaggio non veniva azzerato al nuovo tentativo. Nessun
test unitario avrebbe potuto vedere nessuno dei tre.

**Corollario sull'infrastruttura**: al livello 4 appartiene anche *come si avvia l'app*. Dopo uno
spegnimento improvviso, `/trading` è rimasta in errore perché il profilo di avvio non apriva il
port-forward verso il core in-cluster — logica che esisteva solo dentro `scripts/run-postgres.ps1`.
Ora è estratta in `scripts/ensure-trading-portforward.ps1`, richiamabile da chiunque.

## Stato per fase (aggiornato 2026-07-28)

| Fase | 1 Unità | 2 Controllo | 3 Integrazione | 4 Operativo |
|---|---|---|---|---|
| **D1** SHAP | ✅ Shapley per forza bruta + ricostruzione; lente iniettabile e ripiego | ✅ feature inerte → esattamente 0; lente a stato unico → nessuna colonna fantasma | ✅ `MlLabServiceTests`: modello attivo → K-means, assente → ripiego, detector che solleva → ripiego senza far fallire SHAP | ✅ `/ml`, BTC/USDT 1h con 4 regimi reali **e** SOL/USDT 1h in ripiego |
| **D2** Deriva fattori | ✅ serie a risposta nota; snapshot e ordinamento allarmi | ✅ 40 semi di rumore, 0 allarmi; job che **trova** un fattore piantato che si spegne | ✅ `FactorDriftWorkerTests` con Postgres vero (tetto serie, serie disabilitate, candele insufficienti) | ✅ `/feature-selection` (3 fattori spenti) **e** widget in Home alimentato dal job (RsiFactor su ETH/USDT 1h) |
| **C4** Triple-barrier + meta-labeling | ✅ 4 esiti + ambiguità intra-barra + pesi | ✅ edge piantato recuperato; 20 semi di rumore | ✅ `MetaLabelingAnalysisServiceTests` (componenti reali + fallimenti) | ✅ `/backtest`, 8.886 segnali reali analizzati |
| **D4** DTW pattern discovery | ✅ LB_Keogh vero limite inferiore su 3.000 coppie; dilatazione temporale; input degeneri | ✅ pattern piantato ritrovato **e** rumore respinto; fuzzing 300 prove; stress 50.000 barre | ✅ `DtwPatternAnalysisTests` (catena forma→occorrenze→event-study→verdetto) | ✅ `/market-analysis`, SOL/USDT 15m su 54.984 candele fino a oggi: 500 occorrenze, verdetto **nessun segnale** (p 0,366) |
| **D2.b** Persistenza IC | ✅ verdetto ricostruito dalla tabella **identico** a quello calcolato dalle candele (4 scenari); soglia presa dall'ampiezza registrata, non dalla config; job e pannello propongono la **stessa finestra** sulla stessa numerosità | ✅ rumore puro: la storia si registra ma non nasce alcun allarme; upsert che non duplica; quantizzazione stabile a +500 candele | ✅ `FactorDriftWorkerTests` + `FactorIcHistoryStoreTests` su Postgres vero, incluso il **riavvio del guscio** (fotografia vuota → `HydrateAsync` → stessi allarmi), la **rotazione** (due giri toccano serie diverse) e la fotografia che conserva i giri precedenti | ✅ `/feature-selection` e Home sulla 5199 dopo migrazione applicata al DB reale |
| **D3** OFI / microstruttura | ✅ formula CKS caso per caso contro il calcolo a mano (bid fermo/migliorato/ritirato + i tre simmetrici) e antisimmetria bid↔ask; correlazione parziale per **due strade indipendenti**; conservazione dei volumi nell'aggregazione | ✅ 30 semi di rumore puro: tasso di falsi positivi al livello nominale — **e un difetto trovato qui**: il 99° percentile di 200 giri dava 3,3% di falsi positivi, sostituito dal p-value empirico con correzione +1 | ✅ integrità su **dati veri**: il volume taker ricostruito dal tape coincide con quello dichiarato dalle klines (due file, due pipeline diverse di Binance) | ✅ misura da riga di comando (`ofi`) su 3 simboli × 30 giorni di dump reali — dettaglio in [REPORT-D3-OFI](REPORT-D3-OFI-2026-07-28.md) |

| **Audit backend↔frontend** (2026-07-29/30) | ⚠️ **parziale, e per una ragione**: qui non c'è matematica contro cui confrontarsi — è impianto e politica. L'analogo più vicino c'è: i default di fabbrica devono passare **tutte** le regole di validazione (una regola che rifiuta la configurazione di fabbrica è un bug della regola), e la tabella di verità di `ShouldAlertStale` è coperta caso per caso | ⚠️ **adattato**: «il rumore non accende niente» diventa «nessun falso allarme», coperto sui due percorsi di allerta toccati (grazia della staleness; nessun warning di override quando il file è l'unica sorgente). Non applicabile al resto — nessuno di questi strumenti emette un verdetto statistico | ✅ `EngineConfigGrpcTests`: endpoint serviti dall'host **reale** `ProcioneMGR.Trading` su HTTP/2, coi rifiuti distinguibili sul filo (PermissionDenied ≠ InvalidArgument) e la prova che i segreti non escono nemmeno se richiesti esplicitamente. Più `TradingServiceCollectionExtensionsTests`: locale/remoto scelti **per costruzione**. *Aggiunti rileggendo questo documento DOPO aver dichiarato il lavoro finito: mancavano, ed è la regola 1* | ✅ il livello che ha trovato **tutto**: sei difetti, nessuno visibile ai test. Config letta dal motore via gRPC, scrittura verificata **dentro il pod**, interruttore del feed provato nei due versi sul core in esercizio, notifica del motore `Delivered` con **conferma di ricezione dal proprietario** |

Le fasi precedenti a questa sessione non sono state ri-verificate contro questo standard: la tabella
dice quello che è stato controllato, non quello che si presume.

## Cosa ha aggiunto l'audit backend↔frontend (2026-07-30)

Il livello 4 non ha trovato *qualche* difetto: ha trovato **tutti** quelli di quella sessione, sei su
sei, e nessuno era visibile ai livelli 1-3. Il motivo è che condividevano una forma sola:

> **un controllo che dice la cosa rassicurante indipendentemente dalla realtà.**

Badge verde «connesso · 0 msg» quando lo zero *è* il guasto. Un pulsante di prova che mostrava
successo perché passava da un metodo `void`. Pannelli che configuravano un processo che non li
leggeva. L'avviso sul blocco EEA/MiCA con la condizione «non connesso», che lo escludeva dall'unico
caso per cui era stato scritto. Un test unitario non può vedere niente di tutto questo: gira su
configurazioni sintetiche, mentre il guasto vive **fra due processi**, o fra l'interfaccia e la
realtà.

Due regole pratiche in più, pagate care:

5. **Una verifica che non può fallire non è una verifica.** Prima di aggiungere un pulsante «prova»,
   chiedersi *come fa a dire di no*. Se la catena sotto non restituisce un esito — perché il
   contratto è `void`, perché le eccezioni sono assorbite per progetto — quel pulsante mentirà
   sempre, e mentirà proprio quando serve.
6. **Chi scrive una configurazione remota deve verificare che l'altro l'abbia LETTA.** Scrivere il
   file non basta: `reloadOnChange` non attraversa un mount PVC, e il motore continuava a rispondere
   col valore vecchio. La scrittura riusciva, l'effetto no, e dall'esterno i due casi sono
   indistinguibili.

## Regole pratiche

1. **Il verde a livello di classe non è integrazione.** Se nessuno chiama il codice nuovo, la fase
   non è finita — al massimo è una libreria.
2. **Ogni verdetto ha bisogno del suo nullo.** Prima di scrivere «migliora / decade / è
   significativo», scrivere il test che lo punta contro il rumore.
3. **Verificare nel browser prima di dichiarare fatto.** Due difetti su tre di questa sessione, a
   livello di prodotto, sono emersi solo lì.
4. **I percorsi di fallimento sono funzionalità.** «Troppo pochi segnali», «campione crollato»,
   «modello non addestrabile» sono ciò che l'operatore vede più spesso: vanno testati e devono
   spiegare il motivo.
5. **Un verdetto statistico non è un verdetto operativo, e vanno detti insieme.** «Significativo» e
   «conviene» sono due domande diverse: se la risposta economica sta in un'altra riga — o peggio in un
   altro file — chi legge la prima capisce la seconda. Il gate di D3 le ha fuse in un verdetto a due
   livelli dopo che su dati veri le due risposte erano opposte (informa con p 0,005, edge 9 volte
   sotto i costi).
6. **Chiedersi sempre: ho controllato per l'alternativa ovvia?** In D3 il primo risultato positivo
   poteva essere il rendimento recente travestito; l'ho escluso aggiungendo un secondo controllo, ma
   ci sono arrivato **pensandoci**, non perché un test me lo imponesse. Prima di dichiarare un
   risultato, scrivere quale spiegazione alternativa banale è stata esclusa e come.
