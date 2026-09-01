# Le decisioni prese, e quelle che restano al proprietario

> **Data:** 2026-09-01 · **Filone K, Fase 2** · Delega esplicita del proprietario:
> *«prendi le decisioni più logiche e razionali, procedi pure; se ritieni di non poterlo fare da
> solo, allora passa alla fase successiva e affronteremo le decisioni assieme»*.
>
> Base di misura: [`36_RITIRO_CORSIA_NUMERI_2026-09-01.md`](36_RITIRO_CORSIA_NUMERI_2026-09-01.md)
> (4 misure + 4 avversari) e una seconda ondata di **6 misure + 3 avversari**, che ha demolito tre
> affermazioni del primo documento — comprese due mie.

---

## 0. Il ribaltamento che ha cambiato l'ordine di tutto

Stavo per tarare l'inedia. **La seconda ondata ha mostrato che tarare adesso sarebbe stato tarare
contro un fantasma**, e che sotto c'erano cinque difetti più gravi della taratura.

### I timestamp di `TradeRecords` non sono un processo di arrivo vivo

Sono **tempi di candela scritti a blocchi in differita**. Prove misurate:

- **35 righe precedono la creazione delle gambe a cui appartengono.** La corsia 4 porta 27 trade con
  `OpenedAtUtc` dall'11 al 27 agosto, mentre fino al 31/08 20:22 era configurata su **XRP/USDT**.
- L'unica riga della corsia 6 (`Id 425`, +15,11) è la **replica identica** della prima riga della
  corsia 4 (`Id 398`), stessi istanti di apertura e chiusura.
- `Id 425` è **l'ultima riga di tutta la tabella**: dalle 21:13 del 31/08 la flotta non ha prodotto
  un solo trade nuovo.

**Trade di forward test per corsia: 0 · 0 · 1 · 0 · 0.** Contro 27 righe di replay sulla sola
corsia 4. Ogni «ritmo realizzato», ogni fattore di Fano, ogni falso allarme empirico del documento
36 misura il comportamento di una strategia su candele storiche — cioè la stessa cosa che l'holdout
già misura, non il rischio che la regola corre in esercizio.

> **Conseguenza sulla decisione:** la taratura dell'inedia non è rimandata per prudenza. È rimandata
> perché **il numero su cui si taranterebbe non esiste ancora**.

---

## 1. Ciò che ho deciso e fatto

Sono tutti **difetti**, non tarature. Nessuno di questi cambia una soglia di rischio.

### K33 — La stessa ipotesi non occupa due corsie

`ProcioneMGR/Services/Fleet/HypothesisGuard.cs` (nuovo), innestata in `GreyDeployer.DeployAsync` e in
`EnsemblePageService.AddFromGreyAsync` — cioè in **tutte e tre** le porte che schierano un grigio.

Predicato **a due gradini**, e il secondo gradino è misurato, non prudenziale: delle 16 proposte
grigie schierabili, **una sola** collide per identità esatta ma **tre** collidono per terna
(strategia, coppia, timeframe) — due sono `MacdTrend AAVE/USDT 4h` con `FastPeriod` identico e
`SlowPeriod` 26 e 31 contro il 21 **già in corsa sulla corsia 3**. Una guardia sulla sola
`PipelineCandidateKey` fermerebbe un caso su tre e lascerebbe passare gli altri due, che sono lo
stesso segnale sullo stesso strumento con una manopola spostata.

- Replica **esatta** → rifiuto, senza manopola: non ha lettura alternativa.
- Stessa **terna** → rifiuto governato da `Fleet:BlockDuplicateTriple` (default acceso). Spento,
  passa **e il motivo finisce a journal**: una scelta senza traccia è indistinguibile da un
  incidente, che è precisamente ciò che è successo il 31/08.

### K38 — Il tetto grigio non perde più pezzi del proprio denominatore

`greyRunning` contava dentro `FleetLanes`, che esclude le corsie intoccabili — e `FleetStateReader`
marca `EmergencyStopped` **ogni corsia di cui non riesce a leggere lo stato**. Una corsia illeggibile
usciva dal conteggio e **il tetto si allargava da solo, in silenzio, proprio nel momento in cui il
sistema sapeva di meno**.

È l'unica combinazione compatibile con i due fatti persistiti della sera del 31/08: il ledger della
corsia 4 azzerato in quel tick (quindi il lettore l'aveva raggiunta) e lo schieramento finito sulla
**6** anziché sulla 4 (quindi la 4 non era né contata né libera). Il tetto `MaxGreyLanes = 3` non è
stato violato: **è stato scavalcato**, perché non è stato consultato.

Ora `GreyOccupied` conta ogni corsia oltre l'impronta che sta correndo con provenienza non
dimostrata — quarantena, emergency, illeggibile e modalità protetta **comprese**. Le azioni restano
su `FleetLanes`: *intoccabile* e *non conta* sono due cose diverse. E quando il tetto è saturo, il
journal dice **quante** delle grigie sono intoccabili, perché il rimedio è diverso.

### K34 — `/ensemble` diceva STOPPED sopra una corsia che stava operando

Il badge era guidato da `EnsembleConfiguration.IsEnabled`, **che non governa l'esecuzione**. Chi
governa è `TradingEngineStates.IsRunning`, e le due fonti divergevano su **tre corsie su otto**: al
2026-09-01 le corsie 4, 5 e 6 avevano `isEnabled=false` col motore in corsa che riceveva candele, e
la **6 teneva aperta una short DOGE/USDT da 799 USDT di nozionale**.

La pagina scriveva `STOPPED` a dieci centimetri dal `LaneSelector`, che disegnava il pallino verde
leggendo la fonte vera. Due verdetti opposti sullo stesso oggetto, nella stessa schermata.

Ora il badge grande è il **motore**; l'interruttore dell'ensemble è un badge suo, col suo nome
(«ribilanciamento attivo/spento», perché è ciò che governa davvero); e quando le due divergono la
pagina **lo dice** invece di scegliere una delle due in silenzio.

### K35 — Il tetto |PnL| non è più cieco alle perdite non realizzate

La riga di `OpenPositions` veniva riscritta **solo** quando avanzava `BestPriceSinceEntry`, e
`UpdateBestSinceEntry` esce subito senza trailing stop: su una posizione senza trailing la riga
restava con `CurrentPrice = EntryPrice` e `UnrealizedPnl = 0` **dall'apertura, per sempre**.

Misurato: delle tre posizioni aperte in piattaforma, **due** (corsia 3 AAVE/USDT Sell da 812 USDT,
corsia 6 DOGE/USDT Sell da 799) portavano `UnrealizedPnl = 0` contro un mark reale di **−12,09** e
**+1,16**. E `LaneInvariantChecker` somma i PnL **dalle righe**: una perdita non realizzata era
invisibile al tetto che esiste apposta per vederla.

È la stessa classe di difetto che **J20 dichiarava chiusa**: J20 ha corretto il ramo del fill fuso
(quando la riga *si* scrive, prezzo e PnL viaggiano insieme), non quello della marcatura (quando la
riga *non* si scrive). Ora la riga si scrive anche quando il mark si muove.

### K36 — Una posizione su una corsia ferma non è più invisibile

Il ciclo del watchdog salta le corsie ferme («uno stato corrotto a corsia ferma non può peggiorare, e
verrà comunque azzerato dal prossimo `StartAsync`») e `ReportOrphanPositionsAsync` guarda solo
`LaneId >= TradingLanes.Count`. Una posizione viva su una corsia **esistente ma ferma** cadeva
esattamente in mezzo.

E il ragionamento del ciclo si rovescia proprio lì: *«verrà azzerato dal prossimo `StartAsync`»* non
è la rassicurazione, **è il danno**. `StartAsync`, in Paper, esegue
`db.OpenPositions.Where(p => p.LaneId == laneId).ExecuteDeleteAsync()` — la riga sparisce **senza
`TradeRecord`, senza PnL, senza audit**. Nel frattempo stop e target non vengono più valutati, perché
le uscite protettive vivono dietro `if (!_state.IsRunning) return;`.

Nuovo `ReportStoppedLanePositionsAsync`: nessuna azione automatica (chiudere d'ufficio è il gesto
irreversibile, quindi quello sbagliato), notifica critica una volta per episodio, e si ri-arma quando
la corsia torna pulita.

### K37 — La provenienza viene dal run di schieramento, e gli stati sono tre

Due difetti nello stesso rigo di `SourceVerdictBackfill`:

- `Survived ? "Survived" : "Grey"` **schiacciava in due** gli stati che l'archivio tiene in tre: un
  candidato **bocciato in pieno** veniva etichettato «Grey», cioè *meglio* di come l'archivio lo
  giudica. Non è ipotetico: è ciò che sarebbe successo alla corsia 7 alla prima esecuzione, perché il
  suo candidato è stato retrocesso il 21/08 con «Sharpe holdout 0,11 < 0,5».
- Prendeva **l'ultimo run che ricapita sulla chiave**, non quello da cui la gamba è uscita. Misurato:
  **71 chiavi su 1.028 cambiano `IsGrey` fra un run e l'altro**. «Quale run leggi» sposta
  l'etichetta, e l'etichetta governa un tetto di rischio.

Ora la ricerca è vincolata al run dichiarato dal journal. Dove il journal tace o è stantio — e tace
su **2 corsie di flotta su 5** — la gamba resta **senza etichetta**, e il report dice quale delle due
cause. La copertura scende e la fiducia sale: è il verso voluto.

### Rettifiche a ciò che avevo pubblicato

Due affermazioni del documento 36 erano mie, ed erano false. Sono corrette in testa a quel
documento e nel commento di `SourceVerdictBackfill.cs`:

1. **«Il backfill non ha trovato il candidato delle corsie 5 e 7».** Era una deduzione dallo stato
   osservato. Il backfill **non è mai stato eseguito** (l'assembly che lo contiene è posteriore alle
   etichette); le chiavi combaciano carattere per carattere; le etichette `Grey` le ha scritte
   `GreyDeployer` allo schieramento.
2. **«`SourceVerdict` lo scrive `GreyDeployer`».** Gli scrittori sono **tre** — il terzo,
   `EnsemblePageService.AddFromGreyAsync`, non registrava nulla a journal e ora porta la guardia.

E una del 2026-08-28, già rettificata due volte: **il 4-5 settembre non succede niente**.

### Fuori lista: `DriveProtectiveExits` rimesso a `false`

Nel file vivo era `true` contro la regola 7. Il default del POCO era già corretto e presidiato da
`SecurityDefaultsTests`: era la macchina ad essere andata alla deriva dopo una prova. Inerte finché
il realtime è spento, ma un flag inerte contro una regola è una mina.

---

## 2. Ciò che ho deciso di NON fare, e perché

### D2 — La taratura dell'inedia: **non si tocca**

Non è prudenza, sono tre ragioni misurate.

1. **Il dato su cui si taranterebbe è replay** (§ 0). L'unico ritmo su orologio di parete che esista
   in questo database è quello del ledger, e vale **1 trade in 12,33 giorni-corsia**. L'intervallo di
   Poisson su un evento è `[0,06 ; 13,7]` trade/mese: inutilizzabile, ed è il punto.
2. **L'effetto netto della proposta migliore sarebbe stato**: corsia 3 invariata, corsie 4+6 (una
   sola ipotesi) +11 giorni, **corsia 5 resa permanentemente non ritirabile**. Si pagherebbe un
   cambio di regola per rendere più raro un evento **mai accaduto** — zero righe `Retire` in 118
   decisioni.
3. **La copertura di riserva non esiste.** Congelare la soglia a «zero trade» lascia scoperto il caso
   «1 trade e poi silenzio», e il criterio Sharpe che dovrebbe raccoglierlo pretende 20 trade, cioè
   **118-174 giorni osservati** contro una vita mediana d'identità di 27 giorni di calendario. Non lo
   lascia scoperto *in attesa*: lo lascia scoperto **per sempre**.

**Il criterio di sufficienza, dichiarato adesso:** servono **~30 trade vivi cumulati** perché il
rapporto fra ritmo reale e ritmo atteso abbia un errore relativo del 18%. A f≈3,8 e duty 0,88 sono
~8,6 mesi-corsia, cioè **~2,2 mesi di calendario su quattro corsie**. Prima di allora nessuna
taratura dell'inedia è una misura.

### D3 — Il taglio su `MinTradesPerMonth`: **non ancora**, ma il numero è pronto

Il taglio dipende da `f`, che è una stima da 14-18 trade di *backtest*, e il suo rapporto col ritmo
vivo è ignoto. Ma la riga che deciderà è già misurata e va detta senza addolcirla:

> **La `f` massima della fascia grigia, tolti i cloni, è 5,16.** Con una regola capace di ritirare
> una corsia che opera **una volta e poi tace**, *nessun candidato grigio di oggi è giudicabile
> dentro la vita di un'identità*. Non è un problema di taratura dell'inedia: è che si stanno
> schierando ipotesi troppo rade per l'orizzonte dichiarato (intraday/swing breve).

### D7 — La soglia Sharpe per timeframe: **rimandata, non archiviata**

Il difetto è reale (`√2190 = 46,8` contro `√35040 = 187,2`: la stessa soglia significa quattro cose
diverse) ma **non morde oggi** — il criterio pretende 20 trade e 21 giorni osservati, e nessuna
corsia ci arriva. Cambiare l'unità di un criterio di ritiro mentre il suo numeratore è contaminato da
replay significherebbe correggere la scala di uno strumento che sta misurando la cosa sbagliata.
Va dopo la marcatura del replay.

---

## 2-bis. CHIUSURA — 2026-09-01 sera

Il proprietario ha deciso, e le quattro voci del § 3 sono state applicate. Con esse si chiudono le
quattro cose che restavano in sospeso della Fase 2.

### Le decisioni, applicate

| decisione | scelta | fatto |
|---|---|---|
| **R1** tetto grigio | ritirare il doppione **+** `MaxGreyLanes = 4` | corsia 4 fermata dalla UI (audit Id 2249); tetto a 4 nel file vivo, col motivo scritto accanto |
| **R2** quale gemella | la **4**, non la 6 | la 4 era piatta; la 6 aveva una posizione aperta, che ha poi chiuso da sola alle 18:30 con un trade vero |
| **R3** `AutoPromoteToTestnet` | **spento** | la valutazione resta e la UI mostra «pronta», ma il passaggio torna a essere un click |
| **R4** corsia 7 | **liberata** | gambe rimosse (0), corsia disponibile per il prossimo grigio |

### I quattro interventi che chiudono la fase

- **K42 — la condanna a metà strada si vede.** Il ramo del verdetto non confermato dall'isteresi
  «si annotava solo nel log». La riga di journal ora si scrive sui **cambi di serie** (0→1, 1→2,
  condanna→assoluzione), la serie si **ferma alla conferma**, e il pannello mostra «N condanne in
  corso» con corsia e conferme. È K20/D8, e la conferma del difetto è arrivata dall'esercizio: un
  piano con un'azione ogni quindici minuti e zero righe per sei ore.
- **K43 — il trade si conta una volta.** Deduplica per chiave d'entità nel ritiro di flotta e nel
  monitor di decadimento, con le repliche **dichiarate** in pagina. Vince la **prima scritta**, e va
  detto perché non è neutro: 25 gruppi su 301 hanno repliche con `Pnl` diverso.
- **K44 — la soglia ha una sola unità.** Si giudica sullo **Sharpe per operazione**, che non porta
  il fattore `√PeriodsPerYear` (46,8 a 4h contro 187,2 a 15m). Attraversa il filo col suo
  **conteggio di campioni**, perché in proto3 assente e zero coincidono e uno Sharpe zero è un
  verdetto: a zero campioni il criterio **si astiene**.
- **K41 — l'ora di parete** accanto all'ora di candela (§ 4 del documento 38), che è il prerequisito
  di ogni taratura futura.

### Cosa resta inerte finché il motore non è aggiornato

`SharpePerTradeSamples` arriva dal pod. Finché il motore gira un'immagine precedente al campo,
risponde **zero campioni** e il criterio per Sharpe **si astiene su ogni corsia** — che è il verso
prudente, ma va saputo: **K44 è attivo solo dopo il redeploy del motore.**

### Cosa NON è stato deciso, e resta con il suo criterio

`MinTradesPerMonth` resta a **0,5**, in attesa della marcatura del replay: `f` è un numero di
backtest e il suo rapporto col ritmo vivo è ignoto. Il criterio di sufficienza è dichiarato — **~30
trade vivi cumulati, circa due mesi su quattro corsie** — e ora è misurabile, perché da K41 ogni
riga nuova porta la sua ora di parete.

---

## 3. Ciò che resta al proprietario

Sono decisioni di **rischio** e di **capitale**: non le prendo io.

### R1 — `MaxGreyLanes`, e cosa sblocca davvero la coda

Il tetto è **già sforato**: 4 corsie grigie in corsa su 3. E il tetto è espresso in corsie, ma la
grandezza che governa è **capitale**: tutte le dotazioni valgono 10.000, quindi alzarlo di uno
significa **+10.000 USDT nominali, il 20% del capitale di flotta**, su ipotesi bocciate per finestra
corta.

| mossa | `greyRunning` | slot | sblocca? | corsie grigie dopo |
|---|---|---|---|---|
| etichettare 5 e 7 (D5, primo corno) | 4 | −1 | **no** — è un no-op | 4 |
| ritirare un doppione (4 o 6) | 3 | 0 | **no** | 3 |
| `MaxGreyLanes = 4` | 4 | 0 | **no** | 4 |
| **ritirare un doppione + `MaxGreyLanes = 4`** | **3** | **1** | **sì** | **4** |
| `MaxGreyLanes = 5` | 4 | 1 | sì | **5** (tutte) |

Contesto per decidere: **nessuna gamba attiva, in tutta la piattaforma, porta l'etichetta
`Survived`**. Il capitale non-grigio-per-certezza è zero.

E la testa della coda è `e0cec50f`, cioè **una terza copia** dell'ipotesi già sulle corsie 4 e 6.
Con K33 attivo non può più passare — ma senza K33 il primo slot che si apre sarebbe andato lì.

### R2 — Il doppione: quale fermare, e a che condizione

Le corsie 4 e 6 sono la stessa ipotesi: **20.000 USDT nominali su una stima da 14 trade di holdout**.
Fermarne una non compra uno slot da sola (vedi R1), ma toglie l'unica ridondanza certa della flotta.

> **Se si ferma, si ferma la 4, non la 6.** La 4 è **piatta**; la **6 tiene una posizione aperta**
> (short DOGE/USDT, 799 USDT di nozionale). `StopAsync` lascia la posizione aperta, le uscite
> protettive smettono di essere valutate, nessun watchdog la copriva fino a K36, e al prossimo
> `StartAsync` in Paper la riga viene **cancellata senza `TradeRecord` né PnL**.
> Il criterio «fermare la più giovane» non discrimina: la differenza di osservazione fra le due è di
> **16 minuti su ~5 ore**, il 5%.
> Se dev'essere proprio la 6, prima si chiude la posizione da `/trading` (uscita d'emergenza, che
> registra), poi si ferma la corsia.

### R3 — `PromotionEvaluator:AutoPromoteToTestnet = true`

Nel file vivo esiste un percorso automatico **Paper→Testnet** ed è acceso: `PromotionWorker` gira
ogni 6 ore e, superate cinque soglie, ferma la corsia, la flatta e la riavvia in Testnet. È
**conforme alla regola 3** (verso Live nessun percorso automatico: il confine è stato ri-verificato
su sei livelli ed è intatto), ma è una porta aperta per configurazione.

Quanto è lontana: la corsia più avanzata è la 5, a **8,35 giorni su 21** e **1 trade su 30**, e il
denominatore riparte a ogni riavvio del motore. Il cancello è chiuso oggi. La manopola per chiuderlo
davvero è `AutoPromoteToTestnet = false`, che lascia la valutazione e mostra «pronto» in UI senza
agire.

### R4 — La corsia 7, ferma da 12 ore e invisibile al governo

`FleetOrchestrator` itera su `.Where(l => l.IsRunning)`: una corsia ferma **non viene esaminata da
nessun criterio di merito, per nessun numero di giorni**. Non blocca la flotta — è anzi *la* corsia
libera, e non consuma slot grigi — ma sparisce da essa. Va decisa: riavviarla, riassegnarla, o
dichiararla fuori servizio.

Il modo in cui si è fermata è a sua volta da decidere: il 31/08 alle 14:12:17-18 UTC **quattro corsie
sono state fermate in 1,83 secondi**, con `Details = {}`, `UserId = null` e **zero righe di journal**.
Non è una mano umana (0,14 s fra due click) e non è l'orchestratore. La forma corrisponde a
`tools/LaneControl`, che non scrive né journal né motivo.

---

## 4. Il difetto di fondo, che nessuna di queste decisioni chiude

**Il journal di flotta non è un registro degli schieramenti**, e la scrittura è l'ultimo passo, fuori
da qualunque transazione con la scrittura della configurazione e con l'avvio del motore
(`GreyDeployer.cs`: config → start → *poi* journal).

Misurato sulla sola giornata del 31/08: **2 schieramenti su 4 e 4 arresti su 4** sono fuori dal
registro.

Conseguenza che tocca tutto il resto: l'affermazione **«zero righe `Retire` ⇒ nessun ritiro è mai
stato deciso»** non è più sostenuta dalla fonte che dovrebbe sostenerla. E la vita mediana di
un'identità — i 27,0 giorni su cui si taranterebbe ogni soglia — è ricostruita *dal journal*, che ha
perso proprio gli eventi rapidi e fuori procedura, cioè quelli che **accorciano** la vita di
un'identità. Il campione è censurato nel verso che fa sembrare raggiungibili soglie che non lo sono.

**I due interventi che vengono prima di ogni taratura**, in ordine:

1. **Marcare il replay.** `TradeRecords` non distingue una riga scritta da un recupero di candele
   storiche da una vera. Finché è così, il braccio armato (`DryRun=false`,
   `ExecutionLanes=[3,4,5,6,7]`) può fermare una corsia sulla base di un replay.
2. **Il journal scritto per primo, come intento**, e la storia append-only degli episodi di identità
   (D1) — che **esiste già in embrione** dal 13/08 in `TradingAuditLogs`, ed è la fonte da cui farla
   crescere invece di partire da zero.

Sono la Fase 3.
