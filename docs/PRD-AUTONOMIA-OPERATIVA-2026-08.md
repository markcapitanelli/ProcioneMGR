# PRD — Dall'aritmetica all'operatività: autonomia senza abbassare la barra (diciassettesima ondata, 2026-08-25)

*Nasce da un ragionamento del proprietario: «per passare dai calcoli matematici all'operatività reale
non serve abbassare le barriere di sicurezza operativa, ma agire sulla manovra dei gate statistici e
sull'attivazione dei sottosistemi di autonomia già presenti ma inerti o in dry-run».*

*La tesi di fondo — **automatizzare la validazione invece di abbassare la barra** — è corretta e questo
piano la adotta senza riserve. Le quattro azioni concrete che il ragionamento propone sono invece state
messe alla prova contro il codice vivo, contro la configurazione viva e contro il database reale del
2026-08-25, e **tre su quattro non fanno quello che si crede**: una è già eseguita da settimane, una non
produrrebbe alcun effetto, una descrive strade già percorse e misurate. La quarta — la fascia grigia —
regge, ma il suo ostacolo è in un punto diverso da quello indicato.*

*Il metodo è quello del Filone I: prima le verifiche, poi il piano che ne discende.*

---

## 1. La scoperta che cambia la diagnosi

**La piattaforma non è trattenuta da un interruttore. È ferma perché la macchina della ricerca si è
spenta e nessuno se n'è accorto.**

L'ultimo run di pipeline completato dell'intera piattaforma è del **2026-08-23 04:25**. Al momento
della scrittura sono passate più di quarantott'ore e non ne è partito un altro. La campagna 2
(«Caccia continua 1h+4h, universo largo») è in `Status='WaitingForTrigger'` dal 2026-08-23 04:27 con
`LastOutcome` = «Rotazione esaurita senza ensemble schierato», e da quello stato **il planner non esce
a tempo**: `ProcessCampaignAsync` chiama `TryStartNextConfigAsync` solo se `Status == Rotating`, quindi
il backoff di 12 ore non riarma più nulla. L'unica uscita è `WakeAsync`, cioè un cambio di regime
rilevato da `RegimeChangeDetector` — o l'operatore.

E non esiste una seconda sorgente: **tutte e tredici le `PipelineConfigurations` hanno
`ScheduleEnabled = false`** e `NextRunAt = NULL`, compresa la configurazione 8 che porta ancora scritto
un cron `0 3 * * *` morto. Chi apre lo scheduler per capire cosa sta girando vede tredici configurazioni
spente e conclude che non gira niente; chi legge `Campaign:Enabled = true` conclude l'opposto. Nessuna
delle due letture è la verità, e nessuno strumento la dice.

Questa è la ragione per cui ogni numero sulla ricerca — compreso «il DSR non passa mai» — sta oggi
descrivendo **un archivio che ha smesso di crescere**.

---

## 2. Quattro proposte contro quattro verifiche

### 2.1 «Attivare `Campaign:Enabled = true`» — **già fatto, e non è servito a questo**

Il flag è `true` nel file vivo (`C:/Users/proci/Desktop/ProgettoP/ProcioneMGR/appsettings.json`) da
prima del 2026-08-18, quando il Filone I lo aveva già rilevato e documentato. Il `false` che si ricorda
vive in `appsettings.json.example`, che è documentazione, non stato.

Acceso, il planner ha lavorato: **94 run in 30 giorni** (10 con `Trigger='Campaign'`, 84 con
`Trigger='Event'`), tutti `Completed`, **9.723 candidati valutati, zero sopravvissuti, zero ensemble
schierati** (`ObservedLanes = 0`, `Status` mai passato a `Observing`).

Ma quei 94 run non sono 94 esperimenti. Sono **quattro**, rieseguiti novanta volte:

1. **Le finestre temporali non scorrono mai da sole.** `DateRangesJson` è una colonna statica di
   `PipelineConfigurations` e `PipelineEngine` non la ricalcola: l'unico uso del contesto è
   `Seed = config.Seed`. Le configurazioni 17 e 18 sono rimaste con `HoldoutTo = 2026-07-27` per
   **18 giorni e 90 esecuzioni**, e si sono mosse solo perché una mano umana le ha modificate il
   2026-08-21 alle 15:10 e 15:15. Oggi sono ferme a `HoldoutTo = 2026-08-21` e ci resteranno finché
   qualcuno non le tocca. Stesso universo, stesso `Seed = 42`, stesso backtest deterministico: **più
   cacce sulla stessa griglia congelata non producono informazione nuova**, producono la stessa
   informazione con un timestamp diverso.
2. **L'89% delle sveglie era spurio.** Il commento `[A5]` del 2026-08-20 in `RegimeChangeDetector`
   documenta che dal 2026-07-26 al 2026-08-20 il ramo log-HAR scriveva un sigma **giornaliero** in
   `ForecastVolatility24` mentre il detector confronta una realizzata **per candela**: su ogni
   timeframe intraday il ramo «compressione» era vero per pura aritmetica. La firma nei dati è
   inequivocabile — 4-10 sveglie `Event` al giorno fino al 19 agosto, poi 2, 2, 1, 1 dopo la
   correzione, poi zero.

**Costo:** il tick a vuoto ogni 60 secondi è trascurabile (~2,4 ms di DB). Il costo vero sono **20,9 ore
di pipeline al mese**, spese per rifare lo stesso conto.

### 2.2 «Togliere `Fleet:DryRun` e la piattaforma inizierà a schierare» — **non produrrebbe alcun effetto**

Tre ragioni indipendenti, ciascuna sufficiente da sola:

1. **Il braccio che AVVIA una corsia non esiste.** È una costante dichiarata nel codice, accanto al
   ramo che dovrebbe implementarla:
   `FleetOrchestratorWorker.AssignmentArmImplemented = false`. Il ramo `Assign` del tick scrive nel
   journal `DryRun = true, Applied = false` **cablati**, qualunque sia il valore del flag. Solo il
   braccio che **ferma** una corsia esiste, dal 2026-08-19 (`RetirementArmImplemented = true`) —
   ed è l'ordine deciso allora dal proprietario: prima il ritiro, poi l'avvio, perché fermare si disfa
   con un clic e avviare ha già operato.
2. **`Fleet:ExecutionLanes` è assente dal file vivo**, quindi vale il default del POCO: la lista vuota.
   `WhyNotExecuted` rifiuta ogni corsia con «nessuna corsia autorizzata» **anche a dry-run spento**, e
   `CanExecute` pretende esplicitamente `ExecutionLanes.Count > 0`. È una lista e non un booleano
   apposta: un interruttore aprirebbe di colpo tutte le corsie di flotta.
3. **Non c'è dove schierare.** Le cinque corsie di flotta (3-7) sono tutte in corsa, in Paper, senza
   quarantene. Zero corsie libere.

Il codice si è già difeso dall'equivoco: se qualcuno spegne il dry-run con la lista vuota, il worker
**lo dice a voce alta** (`LogWarning`: «chi ha spento il dry-run si aspetta che qualcosa succeda, e
senza corsie autorizzate non succede — per progetto, non per guasto»).

### 2.3 «Schierare sistematicamente i grigi» — **la fascia esiste, l'ostacolo è altrove**

La fascia grigia **è reale e ha già funzionato**. Nell'archivio: 14.855 righe ma **788 `CandidateKey`
distinti** (il rapporto righe/chiavi è 18,9× — ogni caccia ri-registra la griglia, e qualunque conteggio
per righe è un artefatto); **73 grigi distinti** in tutta la storia, **49 dei quali entro i 30 giorni**
di `CandidateMaxAgeDays`. Cinque di essi riempiono **oggi** le cinque corsie di flotta, schierati a mano
fra il 3 e il 13 agosto: le sei righe `Assign` del journal portano tutte il marcatore `[F5, click umano]`.

Tre precisazioni che cambiano il piano:

- **Le due porte non sono equivalenti.** Dei 73 grigi, **67 sono passati dalla finestra corta e 6 dalla
  banda DSR** — e tutti e sei prima del 2026-08-09. Dopo la correzione del conteggio tentativi della
  deflazione entrata quel giorno, il DSR massimo mai osservato è **0,6737** su 263 candidati misurati:
  **zero in banda [0,70; 0,95)**. La porta DSR è murata. *Nota di igiene:* il sommario in testa a
  `GreyZone.cs` dice «non si è **mai** aperta», e si contraddice quaranta righe più sotto dove ammette
  correttamente i sei distinti. La formulazione vera è «chiusa **dal** 2026-08-09».
- **Il serbatoio che la flotta vede non è di 49.** `FleetStateReader.ReadCandidatesAsync` non legge
  `ResearchCandidates`: parte dai run, emette **un solo** `FleetCandidate` per run (il grigio col miglior
  Sharpe holdout) e marca `AlreadyHandled` ogni run che abbia già una proposta a journal, propagando
  l'identità. Risultato misurato: nelle ultime dieci giornate al massimo **6 proposte distinte**, negli
  ultimi quattro giorni **una sola**.
- **Il collo di bottiglia è il clic, poi lo spazio.** 97 proposte grigie journalizzate in 20 giorni,
  6 clic. E da metà agosto non c'è più una corsia libera dove mettere la settima.

Ma il vero ostacolo allo *schieramento automatico* è di progetto, non di configurazione:
`FleetOrchestrator.AssignmentQueue` filtra `Band == "pass"`, e un grigio produce solo un
`ProposeGreyCandidate`, mai un'assegnazione. È **F5**, scritto in chiaro nel tipo:
*«fascia grigia: si propone al clic umano, MAI si assegna da soli»*, e ripetuto nel testo della
notifica. Automatizzarlo **non è configurare la flotta: è rovesciare una decisione deliberata.**
È una decisione che spetta al proprietario, e il ragionamento in oggetto la prende esplicitamente —
questo piano la esegue, ma nella metà sicura e con i freni che §2.5 spiega perché servono.

### 2.4 «Nuovi terreni di caccia» — **nessuno dei tre è nuovo; uno però è già costruito e spento**

| Proposta | Stato verificato |
|---|---|
| **Microstruttura nella selezione feature** | Chiusa il 2026-07-28 con esito **misurato**: lo sbilanciamento di profondità aggiunge IC (p 0,005, 3 simboli su 3) ma l'edge è **6-34× sotto il costo di andata e ritorno**. Non è cambiato nulla da allora, non esiste raccolta permanente, e il feed scarta le size che servirebbero. L'unico uso sensato resta l'**esecuzione**, dove il giro è già pagato. |
| **Monitoraggio permanente degli spread (pairs)** | **Già costruito, collaudato e amministrabile da UI** — e spento: la sezione `PairsWatch` è assente dal file vivo. Ha scritto zero righe. Peggio: siede su **174 artefatti `PairScreen`** prodotti fino al 2026-08-23 e **mai indicizzati** — `PairCandidates`, la tabella nata apposta per renderli interrogabili, è a **0 righe**. Il test di cointegrazione si paga ogni notte e si butta. |
| **Generazione di candidati via LLM (G3)** | Esiste come decisione accettata, non come codice. Non viola la regola 6 se resta un generatore vincolato *dentro* l'imbuto. Ma **peggiora l'unico vincolo misurato**: più tentativi alzano SR\*, cioè alzano la soglia che nessuno raggiunge già oggi. |

*Sul carry* — l'unica classe con edge storicamente positivo — c'è una scoperta che va detta: il file che
il proprietario chiama «vivo» **non è quello che lo comanda**. `CarryWorker` è registrato solo nel ramo
`!useRemote`, e `Trading:UseRemoteTrading = true`: il worker gira nel pod e legge la *sua* configurazione.
Il ConfigMap effettivamente montato porta una sola chiave (`Trading__LaneCount`), e quello che porta
`Carry__Enabled` **non è montato**. In più `WatchCarryAsync` — il guardiano del silenzio del carry, con
la sua manopola `CarrySilenceAlertHours` in UI — fa `GetService<CarryWorker>()`, che in topologia remota
restituisce sempre `null`: **non può scattare mai nell'assetto in cui la piattaforma gira**. Controllo che
rassicura, in forma pura.

### 2.5 Il percorso automatico che si vuole costruire **esiste già — ed è quello sbagliato**

Questa è la scoperta più importante del lavoro, e ribalta la direzione dell'intervento.

Non passa dalla flotta. Passa dalla **campagna**, e la catena è verificata pezzo per pezzo:

```
DecisionStages          riempie i posti liberi dell'ensemble con gambe GRIGIE  (includeGreyZone=true)
        ↓
RunApplyEvaluator       applica qualunque raccomandazione con EnsembleLegs.Count > 0
                        — NON guarda i sopravvissuti  (a differenza di FleetStateReader, che
                          per la banda «pass» pretende survivors > 0)
        ↓
CampaignPlanner         chiama quella catena dopo ogni run
        ↓
StartPaperLanesAsync    AVVIA in Paper le corsie 0 … LanesUsed-1  ← l'IMPRONTA, non la flotta
```

Le tre condizioni di configurazione sono **già tutte vere**: `Campaign:Enabled = true`,
`AutoReapply:Enabled = true`, e la campagna 2 ha `AutoStartPaperLanes = true`.

Che non sia teorico lo mostra un run reale: configurazione 19 (5m), 2026-08-21, **`Survivors = 0` ed
`EnsembleLegs = 3`** — tre gambe tutte di fascia grigia.

**Perché non ha ancora sparato**, e perché è una salvezza per coincidenza e non per progetto:
`includeGreyZone` è `false` di default, e le uniche due configurazioni che lo hanno acceso — la **19**
(5m) e la **20** (15m), cioè proprio il terreno intraday dove il proprietario vuole andare — **non sono
nella rotazione della campagna**: hanno girato quattro volte in tutto, tutte con `Trigger='Manual'`, il
2026-08-21. E gli artefatti `AutoReapplyDecision` contano **una decisione al giorno con zero applicate**,
fermi al 2026-08-20.

Il punto per il piano è netto: la richiesta «fai schierare i grigi da sola» ha **due implementazioni
possibili già presenti**, e sono asimmetriche.

| | Percorso **campagna** (armato oggi) | Percorso **flotta** (braccio mancante) |
|---|---|---|
| Corsie toccate | **0-2, l'impronta storica**, per indice | 3-7, il recinto della flotta |
| Guardia sui sopravvissuti | nessuna (`EnsembleLegs > 0`) | `Band == "pass"` |
| Guardia di esposizione correlata | no | `MaxLanesWithoutExposureGuard` |
| Arbitrato del comitato AI | no | sì, sui pareggi |
| Isteresi / conferma | comparatore + veto supervisore | `RetireConfirmTicks`, budget 1 azione/tick |
| Ampiezza | tutte le corsie usate, in blocco | lista esplicita `ExecutionLanes` |

**Aggiungere `includeGreyZone` alle config in rotazione sarebbe la strada di un'ora e la scelta
peggiore.** Costruire il braccio nella flotta costa di più ed è l'unica che finisce dentro i freni.

---

## 3. E i gate? La geometria vera dell'imbuto

Il ragionamento del proprietario dice: non abbassare i gate, aggirali con la fascia grigia. La
conclusione è giusta; la ragione è più interessante di come è formulata, e ha una conseguenza operativa
che va scritta prima di toccare qualsiasi soglia.

**Il DSR è insuperabile per aritmetica, non per severità.** Con i parametri veri della campagna 17
(N efficace 12.892 tentativi, T = 887 barre, dispersione degli Sharpe di selezione 0,73), SR\* vale già
**2,65-2,86 annualizzato**: per DSR 0,95 servirebbe uno Sharpe **≈ 5,2-5,5**. Il massimo mai prodotto dal
2026-08-09 è **1,901** (DSR 0,317).

**Ma «è il DSR che blocca tutto» è a sua volta un controllo che rassicura.** `OverfittingGate.Apply`
salta il calcolo per ogni candidato già bocciato: il DSR **esiste solo per chi ha già passato Sharpe e
conteggio trade**, cioè per circa il 4% dell'archivio. La riga di log introdotta il 2026-08-22 dichiara
onestamente «DSR massimo del run: X su N candidati **misurati**», ma non dichiara che i candidati con lo
Sharpe più alto sono stati rimossi *prima* di essere misurati — il massimo Sharpe holdout 4h
dell'archivio è **3,1949** (Supertrend ADA/USDT 4h, 17 trade) e **non ha un DSR**, perché 17 < 20.

Il cancello che uccide di più fra i candidati **in guadagno** è quindi a monte, ed è il **conteggio
trade assoluto** (`minHoldoutTrades = 20` nelle campagne vive): 1.402 righe / **67 chiavi distinte**, tutte
con rendimento holdout positivo e Sharpe medio 1,12. Il DSR ne boccia 487 / 31. Ed è mal dimensionato per
costruzione: è un intero, non una frequenza — sui 4h chiede ~4 trade/mese a candidati che ne fanno 2,3.

**La conseguenza che cambia il piano, e che va detta prima di eseguirlo:** rendere quel gate relativo
alla frequenza attesa **non produce Champion**. Sposta 669 righe dal gate del conteggio a quello del DSR,
dove muoiono comunque — e **riduce la fascia grigia**, perché un bocciato per DSR sotto 0,70 non è grigio,
mentre un bocciato per «Solo N trade» lo è. Va fatto lo stesso, perché un gate mal dimensionato è un
difetto a prescindere dall'esito, ma **va fatto sapendo che il saldo netto sulla fascia grigia è
negativo**, e misurandolo.

---

## 4. I difetti trovati verificando, che nessuno stava cercando

Otto rilievi emersi dalla revisione avversaria. I primi tre bloccano il piano; gli altri sono debito che
il piano incontrerebbe comunque.

1. **L'orologio del ritiro si azzera a ogni riavvio.** `FleetStateReader` calcola
   `observation = now − StartedAtUtc`, e tutte e otto le corsie hanno `StartedAtUtc = 2026-08-23 18:04`.
   Con `RetireMinWeeks = 3` servono 21 giorni di uptime ininterrotto del motore. **La finestra continua
   più lunga mai raggiunta in tutta la vita della flotta è 20 giorni e 3 ore** (corsie 4 e 6, dal
   2026-08-03 al 2026-08-23): venti ore sotto la soglia. Nella stessa finestra quelle corsie avevano
   chiuso 17 e 7 trade contro i 20 richiesti. **Il criterio per Sharpe non ha mai avuto occasione di
   esprimersi** — non ha giudicato e assolto: non ha potuto guardare.
2. **Il ritiro per inedia è circolare.** Pretende `ExpectedTradesPerMonth`, che è `null` su **ogni gamba
   di ogni corsia**, e che viene scritto **solo** da un nuovo schieramento. Per liberare una corsia serve
   un ritiro → per il ritiro serve l'atteso → per l'atteso serve uno schieramento → per lo schieramento
   serve una corsia libera. L'unico modo di rompere il cerchio oggi è una mano umana.
3. **La cadenza nuova è in perdita.** I 69 trade dal 19/08 valgono **−779,81 in Paper**, con **6 corsie su
   7 negative** (la 4 a −260,27 su 20 trade; solo la 2 positiva, +77,59). Durata mediana delle posizioni
   fra 2,6 e 14 ore — intraday/swing breve, coerente con l'orizzonte dichiarato. Con
   `RetireSharpeThreshold = 0` sono esattamente i candidati che il criterio condannerebbe, se l'orologio
   non fosse azzerato.
4. **La corsia 0 è morta da sette settimane e lo grida nei log.** `IsRunning = true`, `Symbol` vuoto,
   `ActiveStrategiesJson = '[]'`, ultimo trade **2026-07-05**, e nel pod 98 occorrenze di
   «Corsia 0: timeframe "" non riconosciuto, nessuna candela alimentata». Qualunque piano che parta da
   «8 corsie Paper in forward test» ne sta contando 7.
5. **`UnrealizedPnl` congelato a zero mentre il prezzo si è mosso.** Corsia 1, DOT/USDT, Sell, ingresso
   0,916 contro corrente 0,894 → `UnrealizedPnl = 0`, internamente incoerente col prezzo persistito nella
   stessa riga. Conta perché `LaneInvariantChecker` calcola il PnL totale come
   `RealizedPnl + Σ UnrealizedPnl`.
6. **I run a universo misto già archiviati non sono marcati.** `HoldoutValidationStage` dal 2026-08-20
   rifiuta gli universi a timeframe misti (il PBO di pannello confronta Sharpe per barra su partizioni per
   indice), ma i 29 run già prodotti dalla configurazione 8 restano in `ResearchCandidates` **senza alcun
   marcatore** e alimentano `/research` e le letture della fascia grigia insieme ai run validi.
7. **`PowerCheckStage` giudica con `All` e stampa il peggiore.** `Underpowered = Series.All(...)` mentre
   il riepilogo riporta `Series.Max(...)`: su un universo misto basta che le serie 1h stiano sotto soglia
   perché lo stage scriva «Potenza OK: minimo rilevabile 8,91 ann.», una frase che si contraddice da sola.
8. **Cinque righe di `TradeRecords` hanno `ClosedAtUtc < OpenedAtUtc`** (scarti da 19 a 30 giorni). Tutte
   precedenti al 19/08, quindi non inquinano il conteggio recente, ma inquinano ogni statistica di durata
   sull'intero storico.

*Due letture correnti da rettificare, per non finanziare interventi inutili:* `Committee:Providers: []`
**non** significa «nessun votante» — `EffectiveProviders()` ricade su `[Nvidia, Groq, Gemini]` e la sonda
conferma «3/3 provider con chiave, quorum 2». Il comitato tace perché **non gli è mai stata posta una
domanda** (arbitra i pareggi, e la coda «pass» è vuota da 15 giorni), non perché non possa rispondere.

---

## 5. Il piano

Cinque fasi, in ordine di dipendenza. Ogni fase è reversibile e nessuna tocca `SafetyChecker`, il confine
verso Live, o le soglie statistiche di sopravvivenza.

### Fase 0 — Rimettere in moto la macchina *(precondizione di tutto: senza run non c'è niente da orchestrare)*

| # | Cosa | Perché |
|---|---|---|
| **J1** | **La caccia non deve poter tacere.** Uscita a tempo da `WaitingForTrigger` (il backoff riarma la rotazione anche fuori da `Rotating`) **più** una sorgente indipendente dal trigger di regime, così che un detector guasto non fermi la ricerca | oggi 43+ ore di silenzio senza che nulla lo dica, e l'unica uscita è un evento che il bug di unità del log-HAR rendeva finto |
| **J2** | **Le finestre devono scorrere.** `DateRangesJson` ancorato a «adesso» invece che a date assolute, con la finestra dichiarata nel run | 90 dei 94 run del mese hanno rieseguito lo stesso backtest deterministico: 20,9 ore/mese per zero informazione |
| **J3** | **Sonda «la ricerca è viva»** in Home: run completati nelle ultime 24h, **candidati distinti nuovi** (non righe), estremo destro dell'ultima finestra di holdout, età dell'ultimo run | serve il numero che distingue «non ha trovato» da «non ha cercato» — oggi non esiste, ed è per questo che due giorni di fermo sono passati inosservati |
| **J4** | **Marcare i run a universo misto** già archiviati e escluderli dalle letture aggregate | 29 run che lo stage oggi rifiuta continuano ad alimentare `/research` e la fascia grigia senza marcatore |

*Gate di fase:* sette giorni consecutivi senza un buco di ricerca superiore a 12 ore, **e** due run
consecutivi della stessa configurazione che producono insiemi di candidati diversi (prova che la finestra
si è mossa).

### Fase 1 — Spostare il terreno di caccia *(dove l'obiettivo è già dichiarato)*

| # | Cosa | Perché |
|---|---|---|
| **J5** | **5m e 15m nella rotazione della campagna** | copertura misurata: **100% su 1h e 4h** (34 serie di soli major), **0% su 5m/15m**, benché 75 serie intraday siano già tracciate e fresche. L'orizzonte dichiarato dal proprietario è intraday/swing breve |
| **J6** | **Gate del conteggio trade relativo alla frequenza attesa** invece che intero assoluto | chiude il debito aperto il 2026-07-28. **Da eseguire dichiarando il saldo**: sposta ~669 righe al gate DSR e *riduce* la fascia grigia — si fa perché il gate è mal dimensionato, non perché produrrà Champion |
| **J7** | **Indicizzare i 174 artefatti `PairScreen`** in `PairCandidates` (la tabella esiste ed è a zero righe) e accendere `PairsWatch` | il market-neutral è la direzione che il proprietario indica, e prima di finanziarne di nuova vale la pena leggere ciò che 174 notti hanno già prodotto e buttato |

*Gate di fase:* le cacce intraday producono candidati distinti con frequenza dichiarata ≥ 4 trade/mese,
e il conteggio dei grigi per porta viene misurato **prima e dopo** J6.

### Fase 2 — Rendere esigibile il ritiro *(la corsia libera è il prerequisito di ogni schieramento)*

| # | Cosa | Perché |
|---|---|---|
| **J8** | **L'osservazione non si azzera al riavvio**: osservazione cumulata persistita, non `now − StartedAtUtc` | il criterio per Sharpe non ha mai potuto esprimersi in tutta la vita della flotta — 20g 3h contro 21g |
| **J9** | **Rompere la circolarità dell'inedia**: `ExpectedTradesPerMonth` ricostruito retroattivamente per le corsie già schierate, dall'holdout del run di provenienza | il campo è `null` ovunque ed è scritto solo da un nuovo schieramento; senza, `IsStarving` tace per sempre e «l'ignoranza non condanna» diventa «nessuno viene mai ritirato» |
| **J10** | **Armare il ritiro su UNA corsia**: `Fleet:ExecutionLanes = [7]`, `DryRun = false`. Una per volta, solo Paper, budget 1 azione/tick | è il collaudo che il PRD di AF2b chiede; fermare è l'azione reversibile della coppia |
| **J11** | **La sonda deve dire perché non ritira**: quante corsie hanno un atteso dichiarato, quante hanno raggiunto la finestra minima | `AgentStateProbe` oggi scrive «ACCESO E OPERANTE … non esegue» e leggerebbe **identica** su una flotta i cui criteri sono entrambi irraggiungibili |

*Gate di fase:* un ritiro vero eseguito sulla corsia 7, journal con `Applied = true`, corsia fermata e
riavviabile con un clic. Se in 14 giorni nessun ritiro matura, **dirlo con il numero** invece di
concludere che va tutto bene.

### Fase 3 — Il braccio di assegnazione, nella metà sicura *(AF2b, la parte mai scritta)*

| # | Cosa | Perché |
|---|---|---|
| **J12** | **Chiudere il percorso campagna → impronta**, o renderlo dichiarato: `RunApplyEvaluator` deve sapere se sta applicando **gambe grigie** e rifiutarsi, oppure dirlo e chiedere. Oggi guarda solo `EnsembleLegs.Count > 0` | è l'unico percorso automatico che oggi può schierare e avviare grigi, e lo fa **sulle corsie 0-2** senza comitato, senza guardia di esposizione e senza lista esplicita. Va chiuso **prima** di aprirne uno voluto, altrimenti se ne avranno due |
| **J13** | **`AssignmentArmImplemented`: scrivere il braccio.** Solo banda `pass`, solo corsie 3-7, una assegnazione per tick, solo su corsie verificate Paper al momento dell'azione (fail-closed come il ritiro) | è la metà mancante di AF2b, ed è la strada che finisce dentro i freni |
| **J14** | **Lo schieramento automatico dei grigi — il rovesciamento di F5**, richiesto esplicitamente dal proprietario. Nella flotta, **non** nella campagna: tetto sul numero di corsie grigie contemporanee, arbitrato del comitato sui pareggi, e **subordinato a J8-J10** (senza un ritiro che funziona si riempiono cinque corsie una volta sola e non si liberano più) | F5 diceva «mai automatico» perché il forward test Paper è l'unico giudice immune al multiple testing e va speso con parsimonia. La decisione del proprietario lo rovescia: il piano la esegue mettendoci i freni che la campagna non ha |

*Gate di fase:* una assegnazione automatica su una corsia di flotta, con la catena completa a journal
(candidato → menù → comitato o default deterministico → assegnazione → avvio), e la prova che la stessa
catena **rifiuta** un candidato grigio quando il tetto è raggiunto.

### Fase 4 — Onestà degli strumenti *(il debito che il piano incontra comunque)*

| # | Cosa |
|---|---|
| **J15** | Il sommario di `GreyZone.cs` è falso e si contraddice quaranta righe più sotto: «chiusa **dal** 2026-08-09», non «mai aperta» |
| **J16** | La statistica «DSR massimo del run» dichiari che è calcolata su un campione **censurato dai gate a monte** (~4% dei candidati) |
| **J17** | `PowerCheckStage`: giudicare e riportare con lo stesso criterio |
| **J18** | `WatchCarryAsync` non può scattare in topologia remota — il guardiano del carry va spostato dove il carry vive, o dichiarato inapplicabile |
| **J19** | Corsia 0: `IsRunning = true` con timeframe vuoto e nessun trade dal 2026-07-05 |
| **J20** | `UnrealizedPnl` congelato a zero su posizioni aperte, e il PnL che `LaneInvariantChecker` ne deriva |
| **J21** | Le 5 righe `TradeRecords` con `ClosedAtUtc < OpenedAtUtc` |

---

## 6. Non-obiettivi dichiarati

- **Non si abbassano DSR, PBO o la soglia del gemello nullo.** Non per prudenza: perché il problema è
  aritmetico (SR\* cresce con la dimensione della ricerca) e abbassare la soglia produrrebbe candidati che
  passano un metro accorciato. La strada dichiarata resta il forward test Paper come secondo holdout.
- **Non si costruisce una raccolta permanente di microstruttura.** Verdetto misurato il 2026-07-28: il
  book informa (p 0,005) e non paga i costi (6-34×). Resta valido l'uso in **esecuzione**.
- **Non si apre G3** (generatore di candidati via LLM) finché il DSR è murato: più tentativi alzano SR\*,
  cioè peggiorano l'unico vincolo misurato. Si riapre se J5/J6 mostrano un terreno dove il gate respira.
- **Non si tocca `SafetyChecker`**, non si automatizza nulla verso Live, e `PromotionEvaluator` resta
  com'è: Paper → Testnet automatico, Testnet → Live **solo umano**.
- **Non si aggiunge `includeGreyZone` alle configurazioni in rotazione** finché J12 non ha chiuso il
  percorso campagna → impronta.

---

## 7. Come si verifica

Valgono i quattro livelli di [STANDARD-VERIFICA](STANDARD-VERIFICA.md), e due di essi hanno qui una forma
specifica che va scritta prima di cominciare:

- **Controllo sul rumore.** Ogni criterio nuovo che *ritira* o *schiera* va provato contro una corsia
  sintetica che non ha alcun edge: se il criterio la ritira, è un criterio; se la tiene, va rivisto. E
  contro una corsia che ha un edge piantato: se la ritira, il criterio è rotto.
- **Integrazione reale.** Nessun item di Fase 2 e 3 si dichiara chiuso senza il journal della corsia vera:
  `Applied = true`, la corsia effettivamente ferma o avviata, e l'esito riletto dal motore — non dalla
  fotografia del guscio, che può avere minuti.

E una regola che questa ondata eredita dal 2026-07-28 e che qui morde più che altrove: **un gate va
scritto insieme allo strumento che lo misura.** J3 e J11 esistono apposta — sono gli strumenti dei gate di
Fase 0 e Fase 2, e senza di loro quelle fasi resterebbero ferme per giorni senza che nessuno sappia dire
perché.

---

## 8. Le decisioni che restano al proprietario

1. **F5.** Il ragionamento in oggetto rovescia una decisione deliberata: i grigi passano da «proposti a un
   clic» a «schierati da soli». Il piano la esegue in J14, nella flotta e con i freni. Il costo da mettere
   in conto: il forward test Paper è l'unico giudice immune al multiple testing, e riempirlo
   automaticamente lo consuma più in fretta di quanto si liberi — **per questo J14 dipende da J8-J10**.
2. **L'ampiezza di `ExecutionLanes`.** Il piano propone di partire da `[7]`. Allargare è togliere un
   numero da una lista; è la manopola che rende il collaudo graduale.
3. **Il tetto delle corsie grigie contemporanee** in J14: quante delle cinque corsie di flotta possono
   essere occupate da candidati che non hanno superato la validazione piena. Il piano non sceglie per il
   proprietario, ma raccomanda di non superare **tre su cinque**, lasciando due corsie alla banda `pass`
   per il giorno in cui il gate tornerà a produrne.
