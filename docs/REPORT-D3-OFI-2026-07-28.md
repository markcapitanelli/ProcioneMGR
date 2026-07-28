# Report — D3: l'OFI vero, misurato senza il pilota di raccolta

*2026-07-28. Chiusura dell'ultimo item aperto del Filone D della [ROADMAP](ROADMAP.md), che era
scritto come dipendente dal pilota di microstruttura C5 (90 giorni di raccolta dal vivo prima di
poter rispondere).*

## In una riga

D3 è chiuso **senza aver aspettato i 90 giorni di raccolta di C5**, perché i dump pubblici di Binance
contengono già tape e profondità storici. Il verdetto è netto nei due sensi: lo **sbilanciamento di
profondità del book aggiunge informazione** oltre al proxy trade-flow — 3 simboli su 3, |IC parziale|
0,040-0,046 a 5 minuti, p-value 0,005 contro un nullo family-wise, primo segnale di microstruttura
non-rumore dopo nove esiti a zero — e allo stesso tempo **non paga i costi**: l'edge lordo vale
0,7 punti base contro 4 di andata e ritorno alla tariffa maker, cioè manca un fattore 6. Il tape
aggregato *sotto* il minuto non aggiunge nulla, e nemmeno l'OFI come *variazione*: informa lo **stato**
del book, non il suo cambiamento. La raccolta permanente resta **spenta**, e non è servito accenderla
per saperlo.

## 1. Il problema, e perché la dipendenza da C5 è caduta

D3 chiedeva l'**OFI vero** — imbalance firmato al top-of-book, stile Cont-Kukanov-Stoikov — da
confrontare col proxy che la piattaforma ha già gratis (`TakerImbalanceFactor`, calcolato dai campi
estesi delle klines). Il gate era quello di C5 §3.3: *il book aggiunge informazione predittiva oltre
al proxy trade-flow, o è ridondante?* E la risposta sembrava richiedere prima la raccolta: tabelle
nuove, sottoscrizioni `@depth`, 90 giorni di pilota, un costo permanente sulla piattaforma.

Non era necessario. Binance pubblica dump storici pubblici (`data.binance.vision`), e fra questi ci
sono sia il **tape** (`aggTrades`) sia la **profondità del book** (`bookDepth`). Si misura oggi, su
mesi di dati, senza accendere niente. E se il verdetto è negativo, non avremo pagato nulla per
scoprirlo — che è esattamente la logica con cui la piattaforma ha trattato la frontiera dei costi e
il gemello sintetico.

**Il limite, trovato sondando e non presumendo**: i file `bookTicker` (miglior bid/ask CON le
quantità) **non esistono** — 404 su tutte le date e i simboli provati, sia daily sia monthly. Quindi
il top-of-book tick per tick non è ricostruibile storicamente. Ciò che esiste è:

| Dato storico | Disponibilità | Granularità |
|---|---|---|
| `aggTrades` (tape completo, con lato aggressore) | ✅ spot e futures | ogni trade |
| `bookDepth` (notional per banda ±0,20%, ±1…5% dal mid) | ✅ **solo futures USD-M** | uno snapshot ogni 30 s |
| `klines` 1m (con `taker_buy_volume`) | ✅ spot e futures | 1 minuto |
| `bookTicker` (top-of-book con size) | ❌ **404** | — |

Da qui due conseguenze di metodo, entrambe dichiarate prima di guardare i numeri:

1. **La misura gira sui futures USD-M**, perché è il solo mercato con la profondità storica. Tape,
   book e klines vengono tutti dallo stesso mercato: mescolare tape spot e book perp avrebbe
   introdotto un artefatto fra venue proprio nella quantità in discussione.
2. **Il book misurato è la profondità a bande ogni 30 secondi, non il top-of-book.** È più debole
   della formula di D3 in tre modi precisi (bande relative al mid ⇒ un movimento di prezzo sposta la
   banda; a 30 secondi si vede il netto e non la sequenza; non esiste l'equivalente della convenzione
   di segno di CKS sul cambio del miglior prezzo). Un esito negativo qui dice che **quel** book non
   aggiunge nulla al proxy — non che l'OFI di CKS sia impossibile.

L'OFI vero è comunque **implementato e verificato** (`OrderFlowImbalance.TopOfBookOfi`), perché è la
formula che consumerebbe il collettore se il pilota si accendesse dal vivo: `{sym}@bookTicker`, cui
il feed R1 è **già** sottoscritto, porta bid/ask con le size, e
[`BinanceStreamMapper`](../ProcioneMGR/Services/MarketData/BinanceStreamMapper.cs) oggi le butta via
tenendo solo i prezzi. Armare la raccolta costerebbe una tabella e un'aggregazione in memoria, non
una nuova sottoscrizione.

## 2. Cosa è stato costruito

| File | Ruolo |
|---|---|
| [`Services/Microstructure/MicrostructureModels.cs`](../ProcioneMGR/Services/Microstructure/MicrostructureModels.cs) | Trade del tape, barra aggregata, miglior quote, snapshot di profondità |
| [`BinanceDumpParser.cs`](../ProcioneMGR/Services/Microstructure/BinanceDumpParser.cs) | Lettura dei tre formati reali, con le loro differenze |
| [`BinanceDumpDownloader.cs`](../ProcioneMGR/Services/Microstructure/BinanceDumpDownloader.cs) | Scarico con cache fuori dal repo, checksum SHA-256, giorno mancante ≠ errore |
| [`TapeAggregator.cs`](../ProcioneMGR/Services/Microstructure/TapeAggregator.cs) | Aggregazione a 10 s (C5 §9.2: aggregare all'origine), griglia regolare |
| [`OrderFlowImbalance.cs`](../ProcioneMGR/Services/Microstructure/OrderFlowImbalance.cs) | OFI di Cont-Kukanov-Stoikov + variante su bande di profondità |
| [`IncrementalIcGate.cs`](../ProcioneMGR/Services/Microstructure/IncrementalIcGate.cs) | Il giudice: IC **parziale** sopra il proxy, nullo a rotazione del migliore, pavimento economico |
| `tools/PlatformExpand` fase `ofi` | La misura: scarico → integrità → candidati → verdetto |

Nessuna entità EF, nessuna migrazione, nessun servizio registrato in DI: la microstruttura vive per
la durata di una misura da riga di comando. Nessuna riga su un percorso di trading.

### La domanda posta bene: IC *parziale*, non IC

Un IC diverso da zero non basta a giustificare la raccolta del book. Se l'OFI dicesse le stesse cose
che dice già il proxy, raccoglierlo sarebbe pagare per un doppione. Quindi il gate misura la
**correlazione parziale di Spearman** fra candidato e rendimento futuro, tenuto conto del proxy:
quanto informa il candidato *dopo* aver rimosso ciò che il proxy spiega già.

Il proxy di controllo è lo sbilanciamento taker **della candela** (`TakerImbalanceFactor` con
Lookback 1 — il fattore vero della piattaforma, non una riscrittura per l'occasione): cioè
esattamente l'informazione che le klines regalano. I candidati sono tre dal tape **sotto** il minuto
e tre dal book:

| Candidato | Cosa chiede |
|---|---|
| `tape ultimi 10s` | il flusso più recente dentro la candela conta più della media? |
| `tape dispersione` | un minuto tutto d'un pezzo è diverso da uno che sbanda avanti e indietro? |
| `tape accelerazione` | gli ultimi 20 s contro tutto il minuto: il flusso sta accelerando? |
| `book imbalance 0,2%` | com'è messo il book vicino al touch alla fine della candela? |
| `book imbalance 1%` | e un po' più in profondità? |
| `book OFI 0,2%` | quanto è cambiata la liquidità ai due lati durante la candela? |

### Due controlli, non uno — e perché il secondo è stato aggiunto in corsa

La prima versione controllava solo per il proxy trade-flow. Guardando il primo risultato di BTC
(positivo) è emersa una spiegazione alternativa che il gate non escludeva: lo sbilanciamento di
profondità **cambia quando il prezzo si muove**, quindi un candidato di book può risultare informativo
solo perché è il **rendimento recente travestito** — cioè il reversal di brevissimo periodo, un
effetto noto, già misurato da questa piattaforma e già non redditizio dopo i costi.

Quindi il gate è stato esteso a più controlli (ortogonalizzati fra loro), e la misura rifatta con il
**rendimento del minuto appena chiuso** come secondo controllo. Vale la pena dire che l'artefatto
c'era poco: il legame fra sbilanciamento di book a ±1% e rendimento dello stesso minuto, misurato a
parte con uno script indipendente, è **−0,016** — praticamente nulla. E infatti i numeri sono quasi
identici (BTC, book ±1% a 5 minuti: |IC parziale| 0,0406 con un controllo → **0,0401** con due). Ma
l'ordine giusto è questo: prima si esclude l'artefatto, poi si guarda il numero.

## 3. Le tre guardie del giudice (e il difetto che hanno trovato)

1. **Nullo a rotazione circolare**, come nel gate C1.b: ruotare la serie del candidato conserva la
   sua autocorrelazione e distrugge solo l'allineamento col futuro. Su serie con memoria un
   permutation test ingenuo darebbe una soglia troppo permissiva.
2. **Nullo del MIGLIORE (family-wise)**: si provano 6 candidati × 2 orizzonti = 12 test, e il massimo
   di 12 misure rumorose è più grande di ciascuna. A ogni giro si ruota tutta la famiglia con lo
   stesso spostamento e si tiene il massimo |IC parziale|: la soglia diventa quella del migliore.
   È la stessa correzione che il DSR applica al numero di tentativi — e la piattaforma ci è già
   cascata una volta (t = 141 su asset correlati).
3. **Pavimento economico e statistico**: soglia = max(0,02 ; 1,96/√n), la stessa regola del monitor
   di deriva D2. Con decine di migliaia di barre il pavimento statistico è minuscolo, quindi a
   vincolare resta quello economico: un IC che non paga i costi non è un segnale.

**Il difetto trovato dal test del rumore.** La prima versione decideva confrontando l'|IC parziale|
col **99° percentile di 200 giri** del nullo. Su 30 semi di puro rumore ha prodotto **1 falso
positivo (3,3%, oltre tre volte il livello nominale dell'1%)**. La causa non è nel concetto ma nella
stima: un 99° percentile ricavato da 200 giri sta fra il 2° e il 3° valore più grande, quindi è una
stima rumorosissima, e quando cade basso il confronto passa. Sostituito dal **p-value empirico con
correzione +1** (Phipson-Smyth): usa tutti i giri, non può mai valere zero, e con 200 giri richiedere
p ≤ 0,01 significa che *nessun* giro del nullo raggiunge l'osservato. Più severo e, soprattutto,
stabile.

Una nota su come è stato chiuso il test, perché è una distinzione che vale in generale: **un test al
livello dell'1% produce falsi positivi nell'1% dei casi, per definizione.** Pretendere zero su 30
semi sarebbe pretendere dal giudice più di quanto dichiara, e renderebbe il test fragile (fallirebbe
a caso una volta su quattro). L'asserzione finale controlla che il *tasso* resti compatibile col
livello nominale.

## 4. Verifica su dati veri: il parser contro un riferimento indipendente

Il pezzo di verifica più utile non è un test unità: è il confronto fra due file diversi, prodotti da
due pipeline diverse di Binance. Il volume taker-buy ricostruito minuto per minuto **dal tape** deve
coincidere con quello dichiarato **dalle klines**.

Su BTCUSDT perp, **43.200 minuti** (30 giorni, zero giorni mancanti, zero righe malformate):

| Misura | Valore |
|---|---|
| errore relativo mediano | **0,0000%** |
| 95° percentile | 0,0003% |
| minuti entro lo 0,1% | 97,9% |

Il 2,1% di minuti che sfora merita di essere spiegato invece di essere lasciato lì, perché la prima
ipotesi (sono i minuti a volume basso, dove un errore assoluto minuscolo diventa un errore relativo
grande) si è rivelata **falsa**: quei minuti hanno un volume taker medio di 25,6 BTC contro una
mediana complessiva di 7,5. Rifacendo il confronto su una giornata con uno script `awk` indipendente
dal codice C# — terza strada, nessuna riga condivisa — il quadro è netto:

- sull'**intera giornata** tape e klines coincidono **esattamente**: 21.283,36 BTC di volume taker e
  46.247,634 di volume totale su entrambi i lati, scarto relativo **0,00000000%**;
- gli scarti per-minuto arrivano in **coppie adiacenti di segno opposto e pari ampiezza** (minuto *i*
  in eccesso di 0,586 BTC, minuto *i+1* in difetto di 0,586).

Cioè: un singolo trade a cavallo del confine del minuto, che Binance attribuisce a un minuto e la mia
aggregazione all'altro, con **conservazione perfetta** del totale. Su una misura a barre da un minuto
sposta un trade fra due barre adiacenti e non tocca alcuna conclusione — ma saperlo, e sapere che non
è un trade perso, è la differenza fra una verifica e una rassicurazione.

Se avessi sbagliato la convenzione di `is_buyer_maker` (chi era l'aggressore) o l'unità del timestamp
(i dump spot sono in **microsecondi**, quelli futures in **millisecondi**), questo confronto lo
avrebbe detto immediatamente. Nessun controllo statistico a valle avrebbe smascherato un order flow
col segno invertito: avrebbe prodotto un IC perfettamente plausibile, col segno sbagliato.

## 5. La misura

**30 giorni (2026-06-26 → 2026-07-25), futures USD-M, barre da 1 minuto.** Per ciascun simbolo:
43.200 candele, 259.200 barre di tape da 10 s, 86.373 snapshot di book, **zero giorni mancanti, zero
righe malformate**. **979 MB** di dump (270 file) scaricati e messi in cache fuori dal repo; niente
entra nel database e niente nel repository.

Il candidato migliore, per simbolo (il dettaglio completo dei 12 test per simbolo è nell'output della
fase):

| Simbolo | Candidato migliore | h | \|IC parziale\| | p-value | Esito |
|---|---|---|---|---|---|
| **BTCUSDT** | book imbalance ±1% | 5 min | **0,0401** | 0,0050 | AGGIUNGE |
| **BTCUSDT** | book imbalance ±0,2% | 5 min | 0,0333 | 0,0050 | AGGIUNGE |
| **ETHUSDT** | book imbalance ±1% | 5 min | **0,0464** | 0,0050 | AGGIUNGE |
| **ETHUSDT** | book imbalance ±1% | 1 min | 0,0325 | 0,0050 | AGGIUNGE |
| **SOLUSDT** | book imbalance ±1% | 5 min | **0,0419** | 0,0050 | AGGIUNGE |
| **SOLUSDT** | book imbalance ±1% | 1 min | 0,0282 | 0,0050 | AGGIUNGE |

**La banda ±1% passa su 3 simboli su 3** a 5 minuti (0,0401 / 0,0464 / 0,0419) e su 2 su 3 a un
minuto: è il risultato più solido della misura, perché la replica su simboli diversi è l'unica cosa
che distingue un effetto da una coincidenza. La banda **±0,2%** invece passa solo su BTC, e su SOL ha
perfino segno opposto (−0,0168, non significativo): vicinissimo al touch il segnale non è stabile.

Il p-value 0,0050 è il minimo ottenibile con 200 giri: **nessuna rotazione del nullo arriva dove
arriva il dato vero**. Con il nullo del migliore su 12 test, non del singolo.

Cosa **non** ha aggiunto niente, su nessun simbolo:

- i tre candidati dal **tape sotto il minuto** (ultimi 10 s, dispersione del flusso dentro il minuto,
  accelerazione): |IC parziale| fra 0,000 e 0,021 con p-value da 0,03 a 1,00. Il proxy per candela
  contiene già tutto ciò che il tape ha da dire — che è esattamente ciò che C5 §3.3 chiedeva di
  verificare, ed è un risultato utile: **aggregare a 10 secondi invece che a un minuto non serve**;
- il **book OFI** (la variazione di liquidità ai due lati durante la candela, la variante più vicina
  in spirito alla formula di Cont-Kukanov-Stoikov): sempre non significativo. Informa lo **stato** del
  book, non la sua variazione a 30 secondi.

### E adesso la parte che conta: quei numeri valgono qualcosa?

No. E il tool ora lo dice da sé, invece di lasciarlo dedurre:

| Simbolo | h | σ dei rendimenti | edge lordo al segnale 1σ | \|IC\| che servirebbe (maker 4 bp) | Quanto manca |
|---|---|---|---|---|---|
| BTCUSDT | 1 min | 5,23 bp | 0,12 bp | 0,765 | **34×** |
| BTCUSDT | 5 min | 11,35 bp | 0,45 bp | 0,353 | **9×** |
| ETHUSDT | 1 min | 6,83 bp | 0,22 bp | 0,585 | **18×** |
| ETHUSDT | 5 min | 14,99 bp | 0,70 bp | 0,267 | **6×** |
| SOLUSDT | 1 min | 7,91 bp | 0,22 bp | 0,506 | **18×** |
| SOLUSDT | 5 min | 16,93 bp | 0,71 bp | 0,236 | **6×** |

Un IC di 0,04 su 43.000 barre è statisticamente solidissimo (t ≈ 8) e **economicamente irrilevante**:
l'edge lordo vale meno di un punto base, contro 4 bp di andata e ritorno alla tariffa maker e 10 alla
taker. Non si salva nemmeno operando solo sulla coda del segnale — un ordine di grandezza, sotto
ipotesi di linearità: sull'1% più estremo (E[z | z > 2,33] ≈ 2,66) l'attesa sale a **1,2 bp su BTC** e
**1,9 bp su ETH**, ancora sotto il costo maker, prima di qualunque slippage.

Va detto che questa è anche una **correzione al pavimento del gate**: la soglia 0,02 arriva da
`/feature-selection`, dove le barre sono ore o giorni. A orizzonte di minuti il pavimento vero è
1-2 ordini di grandezza più alto, e un «AGGIUNGE» letto senza la tabella qui sopra sarebbe stato
frainteso come «si può operare».

> **Correzione applicata subito dopo (2026-07-28, consolidamento).** Nella prima versione questa
> tabella la produceva la **fase CLI**, dieci righe sotto il verdetto del gate: due risposte separate,
> lette a distanza di un rigo, e chi si fermava alla prima capiva «si può operare». Ora la traduzione
> in punti base è **dentro il gate** e il verdetto è **a due livelli**: `NEGATIVO` /
> `INFORMA MA NON È OPERABILE` / `POSITIVO E OPERABILE`. Su questi dati il verdetto stampato è il
> secondo, che è la sola lettura corretta e la sola che sopravvive alla lettura frettolosa di fra sei
> mesi. Ogni riga della tabella espone anche σ, edge lordo e |IC| richiesto dai costi. Ecco com'è
> uscito rilanciando su BTC (finestra scorsa di un giorno, quindi numeri leggermente diversi da quelli
> qui sopra — è la stessa misura, non un altro esperimento):
>
> ```
> candidato                h  IC grezzo  IC parziale   p-value   soglia   edge bp  serve |IC|  esito
> book imbalance 1%        5     0,0388       0,0378    0,0050   0,0200      0,41       0,370  aggiunge, non paga
> book imbalance 0,2%      5     0,0358       0,0319    0,0050   0,0200      0,34       0,370  aggiunge, non paga
> book OFI 0,2%            5     0,0090       0,0018    1,0000   0,0200      0,02       0,370  -
> sigma dei rendimenti: h=1: 4,97 bp · h=5: 10,80 bp
>
> VERDETTO: INFORMA MA NON È OPERABILE: book imbalance 1% aggiunge informazione oltre i controlli,
> però l'edge lordo (0,41 bp) non paga il giro (4 bp). Utile per l'ESECUZIONE, dove il giro è già
> pagato; non per decidere un ingresso.
> ```

## 6. Cosa significa, e cosa non significa

**Significa** che il book porta informazione che il flusso taker non ha: lo sbilanciamento di
profondità è l'unico candidato che sopravvive, replica su più simboli e su due orizzonti, resiste al
controllo per il rendimento appena avvenuto, e batte un nullo family-wise. Dopo nove esiti a zero
consecutivi (le otto cacce direzionali e D4), è il primo segnale di microstruttura misurato che non è
rumore. Il dettaglio più informativo è **quale** forma funziona: lo **stato** del book, non la sua
variazione — l'opposto di dove la formula di CKS mette l'accento.

**Non significa** che ci sia un edge. L'effetto è 6-34 volte sotto il costo di un giro completo,
quindi non esiste alcuna strategia che lo monetizzi entrando ed uscendo per catturarlo. Le due strade
sensate, in ordine di costo:

1. **Come segnale di esecuzione, non di ingresso.** Se una corsia ha già deciso di comprare, il costo
   del giro è già pagato: scegliere *quando* dentro i prossimi minuti è gratis. È esattamente il posto
   dove la piattaforma ha già un motore (QLIB-5, esecuzione adattiva), e l'unico uso in cui un edge
   da 0,5 bp non viene mangiato dalle commissioni. **Questa è la raccomandazione.**
2. **Riaprire il tema solo con dati migliori.** Il book misurabile storicamente è la profondità a
   bande ogni 30 secondi. Il top-of-book tick per tick — quello di D3 — non è ricostruibile dai dump,
   ma il feed R1 lo riceve già e lo butta via: armare la raccolta costa una tabella e
   un'aggregazione in memoria, e il proprietario ha già deciso che, se si fa, vive nel **core caldo**.
   Ora c'è un argomento misurato per farlo, che prima non c'era — ma è un argomento per *misurare
   meglio*, non per operare.

**Il gate di C5 §3.3 è quindi soddisfatto con una risposta chiara**: il book aggiunge IC oltre al
proxy (sì), il tape aggregato sotto il minuto non aggiunge nulla (no), e nessuno dei due paga i costi
a orizzonte di minuti. La raccolta permanente resta **spenta**: non serviva accenderla per saperlo.

## 7. Test

**53 test nuovi** per D3 (49 + 4 aggiunti col consolidamento), più 32 per la persistenza di D2, suddivisi per livello secondo
[STANDARD-VERIFICA](STANDARD-VERIFICA.md):

| Livello | Cosa |
|---|---|
| 1 — riferimento indipendente | OFI di CKS caso per caso contro il calcolo a mano (bid fermo / migliorato / ritirato e i tre simmetrici sull'ask), antisimmetria bid↔ask, somma degli eventi = OFI accumulato; correlazione parziale per **due strade** (formula chiusa vs residui di regressione); conservazione dei volumi nell'aggregazione; le righe reali dei tre formati di dump |
| 2 — controllo sul rumore | 30 semi di rumore puro col tasso di falsi positivi al livello nominale; un candidato che è solo il proxy travestito viene respinto; un edge statisticamente reale ma sotto il minimo economico viene respinto; e un test che dimostra **perché serve il secondo controllo** — un candidato costruito come miscela di proxy e rendimento recente passa con un solo controllo e viene respinto con due (se non passasse nel primo caso, il test non dimostrerebbe niente) |
| 3 — integrazione | tape-vs-klines su 43.200 minuti di dati veri (più il controllo `awk` indipendente sui totali di giornata); lettura di uno zip reale con rilascio dell'archivio; 404 = giorno mancante e non errore |
| 4 — operativo | la fase `ofi` su 3 simboli × 30 giorni di dump reali, con verdetto e traduzione in punti base |

## 8. Come rifare la misura

```bash
dotnet run --project tools/PlatformExpand -- ofi BTCUSDT,ETHUSDT,SOLUSDT 30
```

**Un minuto a simbolo, non un quarto d'ora** (consolidamento 2026-07-28). La prima versione del nullo
rifaceva a ogni giro tutto il lavoro — ranghi dei controlli, del rendimento e del candidato ruotato —
cioè ~14.000 ordinamenti su 43.000 barre per simbolo. Adesso: base dei controlli e residui del
rendimento calcolati **una volta sola**, e soprattutto l'osservazione che **i ranghi di una serie
ruotata sono i ranghi ruotati** (il rango dipende solo dall'ordine relativo, e una rotazione è una
permutazione), quindi nel ciclo non si ordina più niente. Misurato: **62 secondi** contro ~15 minuti,
con numeri **identici** — e c'è un test che confronta il nullo veloce con quello ingenuo, perché
un'ottimizzazione che cambia il risultato è un bug, non un'ottimizzazione.

**Perché la misura non vive in `/feature-selection`**, come invece chiedeva il PRD §5d: i dati di
microstruttura non stanno nel database — non persisterli è la conclusione della misura stessa, non una
scorciatoia. La pagina però **dichiara la deviazione** e riporta l'esito con il comando qui sopra: chi
arriva lì con quella domanda trova la risposta invece del silenzio.

La cache dei dump sta fuori dal repo (variabile `PROCIONE_MICROSTRUCTURE_CACHE`, default nella
temp di sistema): rilanciare non riscarica, e i file restano ispezionabili a mano. Il repo è pubblico
e la sua igiene è già costata una pulizia da 7,9 GB: nessun byte di dump entra nel repository.
