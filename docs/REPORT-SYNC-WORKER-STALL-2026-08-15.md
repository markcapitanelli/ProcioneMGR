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

## Verifica

- Unit: `MarketDataSyncWorkerTests` (5, inclusi timeout-di-rete e catena-che-ignora-il-token),
  `SyncPulseTests` (13). Integrazione Postgres: `MarketDataSyncSeriesGateTests` (2, il gate che
  non affama il ciclo), `SeriesFreshnessWatchWorkerTests` (9, con i 3 casi di diagnosi),
  `WatchlistPageServiceTests` (13). Suite completa verde.
- Dal vivo: pod riavviato, drenaggio osservato (122 → 0 in 4 minuti); poi immagine nuova nel kind,
  `/health/live` col battito, timbro `ingestion-sync` che avanza, pagina verificata nel browser
  (livello 4 dello standard).
