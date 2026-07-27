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
segnalava "ha girato", non "è andata bene"); e la finestra di deriva da 250 barre, tecnicamente
corretta ma che rendeva il pannello inutile perché nessun fattore reale superava il pavimento.
Nessun test unitario avrebbe potuto vedere né l'uno né l'altro.

## Stato per fase (aggiornato 2026-07-27)

| Fase | 1 Unità | 2 Controllo | 3 Integrazione | 4 Operativo |
|---|---|---|---|---|
| **D1** SHAP | ✅ Shapley per forza bruta + ricostruzione | ✅ feature inerte → esattamente 0 | ✅ via `MlLabService` | ✅ `/ml`, BTC/USDT 1h, efficienza verificata a mano sui numeri a schermo |
| **D2** Deriva fattori | ✅ serie a risposta nota | ✅ 40 semi di rumore, 0 allarmi | ✅ via pagina | ✅ `/feature-selection`, 3 fattori spenti trovati su dati reali |
| **C4** Triple-barrier + meta-labeling | ✅ 4 esiti + ambiguità intra-barra + pesi | ✅ edge piantato recuperato; 20 semi di rumore | ✅ `MetaLabelingAnalysisServiceTests` (componenti reali + fallimenti) | ✅ `/backtest`, 8.886 segnali reali analizzati |
| **D4** DTW pattern discovery | ✅ LB_Keogh vero limite inferiore su 3.000 coppie; dilatazione temporale; input degeneri | ✅ pattern piantato ritrovato **e** rumore respinto; fuzzing 300 prove; stress 50.000 barre | ✅ `DtwPatternAnalysisTests` (catena forma→occorrenze→event-study→verdetto) | ⏳ pannello UI da fare |

Le fasi precedenti a questa sessione non sono state ri-verificate contro questo standard: la tabella
dice quello che è stato controllato, non quello che si presume.

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
