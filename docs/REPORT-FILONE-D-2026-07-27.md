# Report — Filone D eseguito: D1 (SHAP) e D2 (deriva dei fattori)

*2026-07-27. Esecuzione dei primi due item del Filone D della [ROADMAP](ROADMAP.md), nati dalla
valutazione del report esterno sulla scoperta di pattern
([PRD](PRD-SCOPERTA-PATTERN-ANTIOVERFITTING.md)).*

## In una riga

D1 e D2 sono fatti, verificati contro riferimenti indipendenti e provati dal vivo nel browser su
dati reali. Nessuna riga sul percorso di trading. I test hanno trovato **due difetti reali nel mio
codice** prima che arrivassero in UI, ed è la parte di questo lavoro che vale la pena leggere.

*(Aggiornamento 2026-07-28: la frase «nessuna modifica di schema», vera quando questo report è stato
scritto, non vale più — la persistenza dell'IC decisa dal proprietario ha aggiunto la tabella
`FactorIcWindows`. Vedi §5.2.)*

**Suite: 1641/1641 verdi** (23 test nuovi: 11 su TreeSHAP, 12 sulla deriva dei fattori), build senza
warning nuovi.

## 1. Cosa è stato costruito

### D1 — TreeSHAP in ML Lab

| File | Ruolo |
|---|---|
| [`Services/ML/Shap/ShapTree.cs`](../ProcioneMGR/Services/ML/Shap/ShapTree.cs) | Rappresentazione neutra di un ensemble di alberi, indipendente da ML.NET |
| [`Services/ML/Shap/MlNetTreeExtractor.cs`](../ProcioneMGR/Services/ML/Shap/MlNetTreeExtractor.cs) | Estrazione da FastForest/LightGBM + misura della copertura dei nodi |
| [`Services/ML/Shap/TreeShapExplainer.cs`](../ProcioneMGR/Services/ML/Shap/TreeShapExplainer.cs) | L'algoritmo di Lundberg (tempo polinomiale), spiegazione locale e sintesi globale |
| [`Services/ML/Shap/ShapAnalysis.cs`](../ProcioneMGR/Services/ML/Shap/ShapAnalysis.cs) | Campionamento, rottura per contesto di volatilità |
| `MlLabService` + `MlLab.razor` | Stato di sessione, pannello UI |

**Il punto tecnico che rendeva la cosa fattibile o no**: TreeSHAP path-dependent ha bisogno della
*copertura* di ogni nodo (quanti campioni lo attraversano), e ML.NET **non la espone**. La si
ricava passando le righe di training attraverso gli alberi e contando — che è la definizione stessa
di copertura, quindi il risultato è esatto, non stimato. Verificato con una sonda prima di scrivere
una riga di algoritmo: `TrainedTreeEnsemble` espone `LeftChild`/`RightChild`/
`NumericalSplitFeatureIndexes`/`NumericalSplitThresholds`/`LeafValues`, che bastano.

### D2 — Deriva dei fattori in Feature Selection

| File | Ruolo |
|---|---|
| [`Services/Alpha/FactorDriftAnalyzer.cs`](../ProcioneMGR/Services/Alpha/FactorDriftAnalyzer.cs) | IC finestra per finestra, pavimento di rumore, verdetto |
| `FeatureSelection.razor` | Pannello con sparkline SVG e banda di rumore |

## 2. Come è stato verificato (la parte che conta)

Un algoritmo di attribuzione può girare, produrre numeri plausibili ed essere completamente
sbagliato. Servono riferimenti calcolati in modo **indipendente**:

1. **Shapley esatto per forza bruta.** Su pochi fattori si enumerano tutti i 2ⁿ sottoinsiemi, si
   calcola l'attesa condizionata per ciascuno e si applica la formula di Shapley. È il valore vero,
   ottenuto per una strada completamente diversa da TreeSHAP. I due coincidono entro 1e-8 su
   entrambi i tipi di modello. **Questo è il test che dice che l'implementazione è corretta**; tutto
   il resto è contorno.
2. **Ricostruzione delle predizioni.** La struttura estratta deve riprodurre le predizioni del
   modello ML.NET vero. Senza, ogni valore SHAP sarebbe spazzatura ben formattata.
3. **Efficienza.** `baseline + Σφ == predizione`, entro 1e-6.
4. **Feature inerte.** Un fattore che il modello non usa riceve esattamente zero.
5. **Puro rumore, 40 semi diversi** (D2): zero allarmi. Un tasso di falsi allarmi anche solo del
   10% renderebbe il pannello inutile.

## 3. I due difetti che i test hanno trovato

Vale la pena riportarli perché sono il motivo per cui questi test esistono.

### 3.1 La foresta media, non somma (D1)

Il test di ricostruzione ha fallito con `modello=1,680 ricostruito=161,288`. Il rapporto era
esattamente **96,0**, cioè il numero di alberi effettivamente presenti: ML.NET tiene i pesi a 1 e
**media** gli alberi al momento della predizione, mentre LightGBM li somma. Senza quel test la
libreria avrebbe prodotto, per Random Forest, valori SHAP tutti sbagliati di due ordini di
grandezza — plausibili a occhio, e con l'efficienza comunque *soddisfatta* (perché sbagliavano in
modo coerente fra loro). Sarebbe stato invisibile a qualunque controllo meno severo della
ricostruzione contro il modello vero.

### 3.2 La soglia stava sotto il pavimento di rumore (D2)

La prima versione giudicava l'IC contro la soglia fissa 0,02 — la stessa che
`/feature-selection` usa da sempre — senza guardare l'ampiezza della finestra. Ma l'errore standard
di una correlazione attorno a zero vale `1,96/√n`: su 300 osservazioni è **0,058**. Un |IC| di 0,04
è quindi rumore puro, e la soglia fissa lo promuoveva a segnale.

Conseguenza concreta, vista nei test: una serie di **puro rumore** veniva etichettata "segno
invertito", l'allarme più grave del pannello, perché il caso aveva girato da una parte nella prima
metà e dall'altra nella seconda. Il monitor avrebbe fabbricato allarmi dal nulla.

La correzione è `soglia = max(minimo economico, 1,96/√n)`, con il pavimento mostrato in UI. È la
stessa lezione già scritta in
[`ricerca-dosaggio`](archive/ROADMAP-MACCHINA-RICERCA.md) sulla significatività fabbricata, in una
veste nuova: **una soglia sensata in astratto diventa un generatore di falsi positivi se applicata a
un campione troppo piccolo per sostenerla.**

## 4. Misure dal vivo (browser, dati reali)

App avviata sul worktree con login reale, BTC/USDT 1h, tutto lo storico disponibile.

### D1 — modello Gradient Boosting, 5 fattori, 18.825 righe di training

Sintesi globale (|SHAP| medio):

| Fattore | \|SHAP\| medio | SHAP con segno | coerenza direzionale |
|---|---|---|---|
| RsiFactor | 0,00014 | −0,00005 | 37% |
| RealizedVol | 0,00013 | −0,00004 | 26% |
| RelativeVolume | 0,00012 | −0,00001 | 6% |
| Momentum | 0,00011 | +0,00004 | 39% |
| MacdFactor | 0,00011 | +0,00001 | 12% |

Due osservazioni oneste:

- **La coerenza direzionale è bassissima** (6–39%). Significa che ogni fattore spinge in su e in giù
  a seconda del contesto, senza una direzione stabile. È il ritratto, visto da dentro il modello, di
  ciò che le otto cacce hanno concluso da fuori: su questa classe di dati non c'è un edge
  direzionale da estrarre. SHAP non lo dimostra — lo *illustra*.
- **Tutti i fattori pesano 2-3× di più nel contesto turbolento** (es. RSI: 0,00007 calmo →
  0,00025 turbolento). Atteso — i rendimenti sono più grandi lì — ma è la conferma che la lente per
  contesto funziona e dice qualcosa.

Verifica di efficienza fatta a mano sui numeri della pagina, riga 5001 (2024-01-26 17:00 UTC):
`+0,00029 − 0,00023 − 0,00023 − 0,00016 + 0,00006 = −0,00027`, esattamente `predizione − baseline`.

### D2 — 10 fattori, 26.929 candele

Alla finestra da 250 (il primo default che avevo scelto) il pavimento di rumore è **0,124** e
*nessuno* dei dieci fattori lo supera: il pannello diceva il vero ma non serviva a niente. Da qui la
**finestra adattiva** — proposta perché produca ~10 finestre sui dati disponibili, qui 2691 — che
porta il pavimento a **0,038** e rende il pannello utile senza taratura manuale:

| Fattore | riferimento | recente | verdetto |
|---|---|---|---|
| **MeanReversion** | 0,050 | 0,026 | **si è spento** |
| **RsiFactor** | −0,046 | −0,035 | **si è spento** |
| **Momentum** | −0,039 | −0,017 | **si è spento** |
| altri 7 | < 0,03 | — | non hanno mai superato il pavimento |

I tre fattori che informavano su questa serie sono scesi sotto il pavimento nel periodo recente. È
esattamente il tipo di cosa che D2 doveva rendere visibile e che prima non si vedeva da nessuna
parte: nella classifica full-sample MeanReversion, RSI e Momentum restano ai primi tre posti, con
|IC| 0,044 / 0,041 / 0,031 e consistenza di segno 83-94%.

### 4.3 Il rendimento effettivo, e perché è la parte più interessante

Rieseguito il flusso completo di ML Lab dopo le modifiche (train → SHAP → backtest OOS), stesso
modello, per verificare di non aver rotto il percorso esistente e per avere un numero vero:

| | |
|---|---|
| Correlazione **in-sample** | **0,460** |
| Rendimento **out-of-sample** (8.083 candele mai viste) | **−1,10%** |
| Win rate | 48,5% |
| Max drawdown | 3,26% |
| Operazioni | 33 (16W / 17L) |

Correlazione in-sample 0,46 e rendimento fuori campione negativo: la firma da manuale del
sovra-adattamento. Ma il punto non è il numero — è che **tre strumenti indipendenti hanno detto la
stessa cosa prima di vederlo**:

1. **SHAP** (D1): coerenza direzionale 6-39% su tutti e cinque i fattori — nessuna spinta stabile in
   una direzione.
2. **Deriva** (D2): i tre fattori che informavano su questa serie sono scesi sotto il pavimento di
   rumore nel periodo recente, cioè proprio quello su cui il backtest gira.
3. **Il backtest**: −1,10%.

È esattamente il valore che D1 e D2 dovevano avere: dire *prima*, guardando la struttura, ciò che il
backtest dice *dopo*, guardando il risultato. Nessuno dei due è un giudice — restano strumenti di
lettura, e il verdetto continua a spettare a DSR/PBO/holdout — ma su questo caso concreto la loro
diagnosi e l'esito realizzato coincidono.

## 5. Deviazioni dal PRD — le prime due sono state poi CHIUSE

*Questa sezione è aggiornata al 2026-07-28: due delle tre deviazioni non esistono più, e vale la pena
tenere scritto perché, invece di riscrivere la storia.*

1. ~~**La lente di D1 è la volatilità, non il regime K-means.**~~ **Chiusa (D1.a, 2026-07-27, secondo
   giro).** La matrice usa ora i regimi K-means di `/regimes` quando esiste un modello attivo della
   stessa serie, e ripiega sui terzili di volatilità altrimenti, dichiarando sempre quale lente è in
   uso. Uno dei due argomenti con cui avevo giustificato la sostituzione era **sbagliato**: «il gate
   C1 ha misurato che i regimi non discriminano» vale per il `LaneRegimeRouter`, che deve *decidere*;
   qui il regime è un asse di raggruppamento **descrittivo** e non deve superare alcun gate. L'altro
   argomento (il pannello resta vuoto quasi sempre) era vero e si risolve col ripiego, non con la
   rinuncia.
2. ~~**D2 non persiste nulla.**~~ **Chiusa (2026-07-28, decisione del proprietario).** Tabella
   `FactorIcWindows`, una riga per finestra, scritta dal job. L'argomento originale — l'IC storico è
   deterministico dalle candele, quindi salvarlo è una cache — era vero **e incompleto**, per due
   ragioni che si vedono solo guardando come la piattaforma vive: (a) il guscio si riavvia di
   continuo, ed è il senso stesso di «core caldo / guscio freddo», quindi una fotografia in memoria
   muore proprio nei minuti in cui uno apre la Home; (b) le candele non sono eterne, e quando la
   finestra fine verrà ruotata quella storia non sarà più ricalcolabile — allora è un'osservazione.
   È l'unica modifica di schema del filone, additiva.
3. **SHAP solo dopo un addestramento in sessione.** (Resta.) Un modello ricaricato da disco non porta
   con sé la distribuzione di training, che è il riferimento rispetto a cui "feature assente" ha
   significato. Usare le candele di test come background sarebbe stato possibile ma incoerente:
   meglio dirlo in UI che produrre numeri con un riferimento diverso da quello dichiarato.

## 6. Cosa NON è stato toccato

- Nessuna riga in `TradingEngine`, `ExecutionJob`, o su un percorso di scrittura Live/Testnet.
- Nessun default operativo cambiato: entrambi i pannelli si attivano solo su click esplicito.
- Nessun nuovo criterio di validazione: D1 e D2 sono strumenti di lettura, non giudici.
- *(Aggiornamento 2026-07-28: la riga «nessuna migrazione EF» non vale più — la persistenza dell'IC
  ha aggiunto la tabella `FactorIcWindows`. È additiva, non tocca nessuna tabella esistente e non
  compare su alcun percorso di trading.)*

## 7. Stato del Filone D (aggiornato al 2026-07-28)

- **D3** (OFI vero) — **misurato**, senza aspettare i 90 giorni del pilota C5: i dump pubblici di
  Binance contengono tape e profondità storici. Verdetto e metodo nel
  [report di D3](REPORT-D3-OFI-2026-07-28.md).
- **D4** (DTW) — **fatto, esito negativo** (nono zero consecutivo): il pattern si trova 26 volte al
  mese su SOL/USDT 15m, ma il rendimento successivo rientra in quello che producono forme qualunque.
  Dettaglio in ROADMAP D4.a-D4.c.
- **D5** (SAX) — **non si fa**: la sua condizione era che D4 mostrasse un segnale sopravvissuto ai
  gate. Il controllo sintetico l'ha superato, il segnale no.

La nota di priorità scritta il 27 si è rivelata giusta: la coerenza direzionale al 6-39% sui fattori
di questa serie era un argomento in più per non aspettarsi molto da D4, e D4 non ha dato niente.

## 8. Come rivedere il lavoro

App sulla porta **5199** (profilo `procione-main`, l'unica da tenere accesa dal consolidamento del
2026-07-27: esegue il repo principale per percorso assoluto).

- **D1**: `/ml` → modello Gradient Boosting o Random Forest → *2. Addestra* → *Calcola SHAP*.
  Lo slider in fondo spiega una singola barra. La matrice per contesto usa i regimi K-means se
  `/regimes` ha un modello attivo per quella serie, altrimenti i terzili di volatilità.
- **D2**: `/feature-selection` → il pannello **«storia registrata dal job»** si vede subito, senza
  calcolare nulla; *Valuta fattori* → *Analizza deriva* ricalcola su richiesta.
- **D3**: è una misura da riga di comando, non una pagina:
  `dotnet run --project tools/PlatformExpand -- ofi BTCUSDT,ETHUSDT,SOLUSDT 30`.
