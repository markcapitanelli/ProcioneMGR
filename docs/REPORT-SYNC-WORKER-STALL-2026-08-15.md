# Report — Worker di sync parcheggiato: 122 serie ferme per 6 ore (2026-08-15)

## L'incidente

Alle **04:05 UTC del 2026-08-15** la watchlist dichiarava **122 serie abilitate FERME** (ultima
candela chiusa oltre la tolleranza di 3 barre): 42×15m + 49×1h + 30×5m + 1×1m — l'intero parco
intraday. Le 4h e 1d sembravano sane solo perché la loro tolleranza (12 h / 3 giorni) non era
ancora scaduta: **il sync era fermo per tutte**.

La verità di fondo, dal DB: l'ultimo `LastSyncUtc` scritto dal ciclo era delle **22:44 UTC del
2026-08-14**. Il `MarketDataSyncWorker` dentro il pod ingestion si era fermato lì — e il pod è
rimasto «healthy» per 6 ore (`/health` statico, provava solo che Kestrel rispondeva). Le ~100
serie rimaste fresche erano quelle scritte dal feed real-time delle corsie, che è un percorso
indipendente.

Rimedio immediato: `kubectl rollout restart` alle 04:53 → **l'intero arretrato è stato drenato in
4 minuti** (ferme = 0 alle 04:57). Nessun rate-limit, nessun simbolo sospeso: solo il worker morto.

## La causa radice: la terza sorgente di OperationCanceledException

Il budget di ciclo del 2026-08-13 (commit `6d12e19`) era **presente e funzionante** nell'immagine
del pod. Ma distingueva solo DUE sorgenti di `OperationCanceledException`:

1. **shutdown** (`stoppingToken`) → esci dal loop;
2. **budget scaduto** (`cycleCts`) → warning, ciclo interrotto, riprova al tick dopo.

Ne esiste una **terza**: il timeout di rete travestito da cancellazione.
`ExchangeRateLimitHandler.SendOnceAsync` traduce il timeout per-tentativo (15 s) in una
`TaskCanceledException` **sintetica con `Token = CancellationToken.None`** (e il Timeout nativo di
HttpClient, 100 s, fa lo stesso). `TaskCanceledException : OperationCanceledException`, quindi:

```
timeout klines → TCE(Token=None)
  → MarketDataSyncService.SyncSeriesLockedAsync: catch(OCE){ throw; }   ← senza filtro sul token
  → MarketDataSyncWorker.RunCycleAsync: il filtro del budget NON matcha  (né budget né shutdown)
  → MarketDataSyncWorker.ExecuteAsync: catch(OCE){ break; }             ← letta come SHUTDOWN
  → «MarketDataSyncWorker fermato.» (Information) — worker morto, pod healthy, per sempre
```

Un singolo timeout di rete su UNA richiesta klines uccideva il worker con un rigo di log a
livello Information. I test del budget non lo coprivano: il finto «appeso» **onorava il token**.

## I fix (questo PR)

1. **Tassonomia delle OCE** — `MarketDataSyncService`: il rethrow filtra
   `when (ct.IsCancellationRequested)`; un timeout di rete cade nel percorso errore per-serie
   (`LastSyncStatus = "Errore: …"`, il ciclo prosegue). `MarketDataSyncWorker`: il `break` del
   loop filtra `when (stoppingToken.IsCancellationRequested)`; ogni altra OCE è un errore di
   ciclo (Error, ritento al tick). L'unico discriminatore affidabile è
   `ct.IsCancellationRequested` — MAI `ex.CancellationToken == ct` (la TCE sintetica ha Token=None).
2. **Backstop non cooperativo** — `RunCycleAsync` avvolge il ciclo in `WaitAsync(2× budget)`: se
   un anello futuro della catena smettesse di onorare il token, il ciclo viene ABBANDONATO con un
   log rumoroso invece di parcheggiare il worker.
3. **`/health/live` che vede il worker** — battito in-process del loop (`IngestionSyncHeartbeat`,
   scritto a ogni giro, anche a `Enabled=false` e a budget scaduto); loop muto oltre la soglia
   (max(30 min, 6× intervallo), ≫ del backstop) ⇒ **503** ⇒ la liveness K8s riavvia il pod da
   sola. Il riavvio è esattamente il rimedio che questa notte ha richiesto un umano.
   La **readiness** resta su `/health` (sempre 200): un worker parcheggiato non deve togliere il
   traffico al `POST /sync` manuale, che è proprio la via di rimedio.
4. **Timbro del ciclo a DB** — riga `HostHeartbeats` con ruolo `ingestion-sync` scritta a fine di
   ogni ciclo (esito **e cadenza** inclusi): «l'ultimo giro di sync è delle HH:mm» ora è un dato,
   non una deduzione forense. L'intervallo viaggia col timbro perché chi giudica (guscio) e chi
   timbra (pod) hanno `appsettings` indipendenti. Anche a worker **spento** il timbro si scrive:
   «spento» è una scelta di configurazione, non un guasto da cercare nei pod.
5. **Watchlist rivista** — vedi `docs/pagine/watchlist.md`: stato del sync in testa con verdetto
   (vivo/fermo/mai visto), banner delle ferme con DIAGNOSI (sync fermo ⇒ «non disabilitare, si
   drena da solo»; sync vivo ⇒ verifica BREAK con un bottone), badge «in recupero» per il
   drenaggio, auto-refresh 60 s, conteggi fuori dal percorso critico (la GROUP BY da 15 s misurati
   non si paga più a ogni apertura).
6. **Notifica con diagnosi** — la guardia di freschness ora dice DOVE guardare (`SyncPulse.DescribeCause`):
   «è il SYNC fermo (ultimo giro HH:mm)» vs «probabile BREAK del simbolo».

## Cosa ha trovato la review avversaria del fix (stesso giorno)

Tre lenti indipendenti sul diff, due scettici per finding: **10 difetti confermati**, tutti
corretti prima del merge. I tre che valgono come lezione:

1. **Il backstop creava una cecità nuova.** Un ciclo abbandonato lascia il `SemaphoreSlim` della
   serie in volo preso da un task zombie, per sempre. Con l'attesa illimitata di prima, ogni ciclo
   successivo si bloccava lì fino al budget e **tutte le serie successive nell'elenco non venivano
   più sincronizzate** — con battito fresco, timbro fresco e liveness che non riavviava nulla:
   l'incidente in versione parziale e non auto-riparabile. Cura: nel ciclo il gate si prende con
   attesa breve e, se occupato, **la serie si salta**. Una serie bloccata costa una serie.
2. **`TimeoutException` attribuita al backstop qualunque fosse la sua origine** (es. pool Npgsql
   esaurito): la forense sarebbe finita nella direzione sbagliata. Ora il filtro distingue in base
   a `cycle.IsCompleted`.
3. **Il 503 sulla stessa `/health` avrebbe tolto la readiness** proprio durante l'incidente, cioè
   proprio quando il `POST /sync` manuale serve. Da qui i due endpoint separati.

Gli altri sette: intervallo di giudizio preso dalla config del processo sbagliato; «spento» letto
come guasto; corsa tick-vs-azione sul service Scoped (lost update); colonne `LastSyncUtc`/
`LastSyncStatus` congelate all'apertura; «verifica completata: 0 sospese» anche con **tutte** le
chiamate agli exchange fallite; timeout di rete nel percorso UI che uccideva il circuito Blazor;
badge di stato vecchi presentati sotto un timestamp di verifica nuovo.

## Cosa ha trovato la verifica nel browser (2026-08-16)

Un difetto che né i test né la review avevano visto, perché lo si vede solo aprendo la pagina
vera con i dati veri: **la colonna «Candele» restava «…» per sempre**.

La revisione aveva tolto la `GROUP BY` da 15 s dal percorso critico sostituendola con un `COUNT`
per serie sull'indice, in background. Misurato sull'archivio reale: **417 ms per serie × 234 serie
≈ 97 secondi a passata** — e una passata per ogni caricamento di pagina, tutte accavallate
(pagavo sei volte tanto il problema che volevo risolvere). La forma giusta era la terza: la
`GROUP BY` unica — che è il totale più basso — fatta in background, **una alla volta nel processo**
e **condivisa fra i circuiti** con validità di 10 minuti (`SeriesCandleCountCache`, singleton).

Misura dopo la cura: **una passata da 7,4 secondi** per 239 serie, e le aperture successive di
pagina a costo zero. Il tooltip della colonna dichiara l'ora del conteggio: è un valore condiviso
che può avere qualche minuto, e dirlo è la Regola 5.

La lezione di metodo: i test e la review avversaria hanno preso dieci difetti di logica, ma il
costo reale di una query si vede solo sui dati veri. **Il livello 4 dello standard non è una
formalità.**

## Coda: la soglia insoddisfacibile della 1m (2026-08-16, dopo il merge)

Col codice nuovo in esecuzione, la pagina ha mostrato «1 serie abilitata FERMA» su XRP/USDT **1m**
— e poi il banner è sparito da solo, senza ricaricare (prova che la fotografia stantia del 13
agosto è chiusa). Campionando il ritardo ogni 45 secondi: **oscilla fra 0 e 4 barre**, a cavallo
della tolleranza di 3.

Non è un guasto, è aritmetica: il ciclo parte ogni 5 minuti e ne impiega ~4 per il giro delle 222
serie, quindi una serie a un minuto **non può** stare entro tre minuti di freschezza. La soglia
era insoddisfacibile in questo assetto — la classe di difetto «gate senza strumento» del
2026-07-28 — e produceva allarmi ricorrenti che erodevano il budget di 20 notifiche/ora condiviso
con quelli veri: lo stesso meccanismo dell'inondazione STX del 13 agosto.

Cura (decisione del proprietario): la tolleranza dipende dalla **cadenza di sync** —
`EffectiveToleranceBars` = max(3 barre, 2× intervallo in barre). Due giri e non uno, perché uno
solo verrebbe sfiorato a ogni ciclo un po' più lento. Col default la 1m passa a 10 barre;
**15m/30m/1h/4h/1d restano a 3, invariate**: la cura tocca solo i timeframe più fini della
cadenza, che sono gli unici a soffrirne. Un blocco vero sulla 1m si vede comunque in dieci minuti.

Nell'occasione il verdetto per riga è stato spostato dal markup al page service (`Row.IsStale`):
la pagina e il servizio calcolavano la freschezza per conto proprio, e con una tolleranza che ora
dipende dalla cadenza sarebbero diventati due verdetti sulla stessa serie.

## Le lezioni

- **Un catch di `OperationCanceledException` senza filtro sul token è un bug dormiente**: le TCE
  sintetiche dei timeout HTTP sono OCE a tutti gli effetti e non portano il token di nessuno.
- **La liveness deve coprire il LAVORO, non il processo**: un `/health` statico ha tenuto in vita
  6 ore un pod il cui unico scopo era morto.
- **I test dei tetti di tempo devono includere il callee che ignora il token**: il test col finto
  che collabora prova il percorso felice del meccanismo, non il guasto che lo motiva.
- **Il dato «quando è passato l'ultimo giro» va scritto, non dedotto**: senza timbro, la diagnosi
  di questa notte ha richiesto query forensi sul MAX di `LastSyncUtc`.
- **Un tetto di tempo che abbandona lascia dietro qualcosa**: chi abbandona un lavoro deve
  chiedersi cosa resta preso (lock, gate, connessioni) e come il giro dopo ci convive. Il backstop
  senza la regola del salto avrebbe spostato il guasto, non tolto.
- **Un endpoint di salute che risponde a due domande diverse ne risponde male a una**: liveness
  («il lavoro è vivo?») e readiness («posso servire richieste?») hanno rimedi opposti.
- **Una soglia va confrontata con la cadenza di chi produce il dato**: chiedersi non solo «dove si
  legge il numero» ma «questo numero può stare sotto la soglia, in questo assetto?». Tre barre su
  una serie da un minuto, con un ciclo da cinque, è una soglia che nessuno può soddisfare — e un
  allarme che non può spegnersi insegna a ignorare gli allarmi.

## Verifica

- **Livello 1-2** (unità vs riferimento indipendente, controllo sul rumore):
  `MarketDataSyncWorkerTests` (5, inclusi timeout-di-rete e catena-che-ignora-il-token),
  `SyncPulseTests` (13).
- **Livello 3** (integrazione reale, Postgres): `MarketDataSyncSeriesGateTests` (2, il gate che
  non affama il ciclo), `SeriesFreshnessWatchWorkerTests` (9, con i 3 casi di diagnosi),
  `WatchlistPageServiceTests` (13). Suite completa **2577/2577**.
- **Livello 4** (la pagina vera): `WatchlistPageRenderTests` (5) rende `/market/watchlist` con
  bUnit e verifica ciò che l'utente LEGGE in ogni scenario — sync fermo («l'imputato è il sync»,
  niente consiglio BREAK), sync vivo (bottone «Verifica su exchange»), worker spento, nessun
  timbro, e serie sane (nessun banner rosso). Reso permanente in un test invece che guardato una
  volta in uno screenshot: il collaudo interattivo richiede un login che l'assistente non può fare.
- **Dal vivo, sul cluster**: pod riavviato → arretrato drenato (122 → 0 in 4 minuti). Poi immagine
  `local-43340a6c` importata nel kind e promossa: `/health` e `/health/live` rispondono col
  battito del loop, il timbro compare a DB (`ingestion-sync | ciclo ok · intervallo 5m`) e il log
  dichiara «Sync ciclo completato: 222 serie (0 saltate per gate occupato) in 00:03:47».
