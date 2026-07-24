# R1 — Feed di prezzo real-time e uscite reattive

Data: 2026-07-20 · Suite: **1305/1305** · Feature **spenta di default**

## Perché

Prima di questa fase la catena dei prezzi era:

```
MarketDataSyncWorker (REST, ogni 5 min) → tabella OHLCV → TradingWorker (legge il DB ogni 2s) → TradingEngine
```

Il motore valutava già correttamente stop e target **intrabar** su High/Low della candela
(`TradingEngine.ProcessCandleAsync`), ma solo *quando la candela finalmente arrivava*. In
Testnet/Live questo significa che uno stop violato veniva rilevato **fino a 5+ minuti dopo**, e
l'ordine market di chiusura partiva allora — al prezzo di quel momento, non a quello dello stop.
Era il maggior rischio di perdita reale della piattaforma, ed è un problema di **sicurezza**, non
di velocità.

Origine: valutazione del PDF *"Da Analisi a Esecuzione"*. Delle sue cinque fasi, tre descrivevano
componenti già esistenti e più maturi (trading engine, risk manager, order execution). L'assenza
di WebSocket era invece un buco reale — verificato: zero occorrenze nel repo.

## Cosa è stato fatto

### 1. Decisione delle uscite estratta in una funzione pura

`Services/Trading/Internal/ProtectiveExitEvaluator.cs` — liquidazione, stop, take profit e
trailing. Estratta da `ProcessCandleAsync` **senza cambio di comportamento** (verificato: i 95 test
esistenti su stop/trading/futures passavano già prima di aggiungere qualsiasi cosa di nuovo).

Il tick viene passato come **barra degenere** `open = high = low = prezzo`. La degenerazione
produce spontaneamente la semantica giusta — il fill "esito peggiore fra livello e apertura"
collassa sul prezzo corrente di mercato — quindi non esiste alcun ramo speciale per il real-time,
e le due strade non possono divergere.

### 2. `ITradingEngine.ProcessPriceTickAsync`

Valuta **solo** le uscite protettive. Confini non negoziabili, coperti da test:

- **non apre mai** una posizione e non valuta mai un segnale di strategia — gli ingressi restano
  governati dalle candele chiuse, l'unico percorso che il backtest valida;
- **coalescenza via try-acquire del gate**: se il motore è occupato il tick è *scartato*, non
  accodato. Un tick vecchio non ha valore, e questo è anche il latch che impedisce a una raffica di
  emettere due chiusure sulla stessa posizione;
- prezzi non positivi ignorati (il testnet ha già mostrato di rispondere "prezzo 0", bug B1).

`RemoteTradingEngineClient` **lancia** di proposito: i tick non attraversano mai gRPC.

### 3. Feed WebSocket — `Services/MarketData/`

| File | Ruolo |
|---|---|
| `WebSocketPriceFeed` | Connessione, riconnessione con backoff esponenziale + **jitter**, resubscribe, salute |
| `IWebSocketTransport` / `ClientWebSocketTransport` | `System.Net.WebSockets` della BCL — nessuna dipendenza esterna aggiunta |
| `BinanceStreamMapper` | `@bookTicker` (tick) + `@kline_{tf}` con `k.x == true` (candele chiuse) |
| `BitgetStreamMapper` | canale `ticker` + ping applicativo |
| `RealtimePriceWorker` | Uno per flotta: sottoscrizioni dalle corsie attive, instradamento, watchdog |

Solo **market data pubblico**: nessuna API key, quindi nessun vincolo MiCA.

Il jitter sul backoff non è ornamentale: senza, tre corsie che perdono la connessione insieme
ritenterebbero in sincrono a ogni giro, martellando l'exchange proprio mentre è in difficoltà.

### 4. Disciplina REST — `ExchangeRateLimitHandler`, `ExchangeClock`

- Token bucket proattivo + ritiro su **429/418** rispettando `Retry-After`. Come
  `DelegatingHandler` vale per tutte le ~30 chiamate senza toccarle.
- **I 5xx non sono ritentati**, deliberatamente: lasciano lo stato dell'ordine incerto e i chiamanti
  hanno già `OrderReconciler` per quel caso. Un retry cieco potrebbe duplicare un ordine reale.
- `ExchangeClock` allinea il `timestamp` delle richieste firmate all'ora del server (evita il
  Binance `-1021`). Offset implausibile (> 5 min) rifiutato: è quasi sempre una misura sbagliata, e
  applicarlo farebbe rifiutare *tutte* le richieste invece di alcune.

## Decisioni deliberate

**Bitget: solo tick, niente candele.** Il canale candele di Bitget non ha un flag "barra chiusa"
come il `k.x` di Binance: pubblica ripetutamente la candela in corso, e dedurne la chiusura è
un'inferenza fragile (un buco di connessione o un riordino la sbagliano). Il premio sarebbe
anticipare di qualche minuto un *ingresso*; il valore vero del feed sono le *uscite*, che vivono
sui tick e ci sono tutte. Le candele Bitget continuano ad arrivare dal ciclo REST.

**Prezzo = mid, non il lato.** Usare bid per chiudere i long e ask per gli short sarebbe più
"onesto", ma farebbe scattare gli stop di long e short in momenti diversi sullo stesso mercato e
renderebbe il livello sensibile a un allargamento momentaneo del book. Il prezzo di *esecuzione*
resta comunque quello riportato dall'exchange. Tick con book incrociato o spread abnorme (> 2%)
sono scartati.

**Nessun "fallback" da attivare.** Il feed è *additivo*: `MarketDataSyncWorker` e `TradingWorker`
restano attivi e indipendenti, quindi quando il WebSocket cade il percorso a candele non ha mai
smesso di funzionare. Quello che serviva, e che c'è, è non *credersi* aggiornati quando non lo si è:
la watchdog di staleness allerta via `INotifier` (one-shot per transizione, non a ogni giro).

## Bug trovato scrivendo i test

`JsonElement.TryGetProperty` su una radice che **non è un oggetto** (es. un frame con array in
radice) non ritorna `false`: **lancia** `InvalidOperationException`, che non essendo una
`JsonException` sfuggiva al `catch` del parser. Un frame irrilevante avrebbe abbattuto la
connessione, silenziando gli stop. Corretto con un controllo esplicito su `ValueKind`, e il caso è
ora una regressione esplicita in `RealtimeStreamMapperTests`.

## Verifica

**Test: 1305/1305** (erano 1243; +62). Nuovi file:
`TradingEngineTickExitTests`, `RealtimeStreamMapperTests`, `WebSocketPriceFeedTests`,
`ExchangeRateLimitAndClockTests`, più 2 test in `TradingServiceCollectionExtensionsTests` che
proteggono la regola "un scrittore, un host" anche per il feed.

**Dal vivo** (`dotnet run --project tools/RealtimeVerify -- --seconds 75`), stream reali:

| Exchange | Tick | Candele chiuse | Riconnessioni | Ultimo prezzo |
|---|---|---|---|---|
| Binance | 6761 | 1 | 0 | 64668,005 |
| Bitget | 243 | — (per scelta) | 0 | 64670,745 |

Scarto fra le due venue: **0,004%** — conferma che i mapper leggono i campi giusti. I 75s senza
disconnessione su Bitget confermano che il ping applicativo funziona (senza, chiude dopo ~30s).

**Cadenza: ~90 tick/s da Binance, contro un ciclo REST da 300s.** È la misura del ritardo tolto
agli stop.

## Come si accende

`appsettings.json`, sezione `MarketData:Realtime` (default `Enabled: false`):

```jsonc
"Realtime": {
  "Enabled": true,
  "DriveProtectiveExits": false,  // prima passata: SOLA OSSERVAZIONE
  "StaleAfterSeconds": 60,
  "MaxSpreadPercent": 2
}
```

Percorso consigliato:

1. `Enabled: true`, `DriveProtectiveExits: false` — il feed gira, logga e misura, ma non decide
   nulla. Stesso spirito del dual-read ML della Fase 2a.
2. Confronto della metrica `procione.trading.protective_exits` fra `source=tick` e `source=candle`.
3. `DriveProtectiveExits: true` su una corsia **Paper**.
4. Corsia **Testnet** su Bitget Demo.
5. Live: solo dopo aver visto i punti 3-4 comportarsi come atteso.

## Resta da fare

- Verifica in Paper/Testnet con flag acceso (passi 3-4 sopra): richiede l'app avviata con DB.
- Prova di riconnessione dal vivo staccando la rete a metà corsa (la logica è coperta dai test
  unitari, ma non è ancora stata vista contro un socket reale caduto).
- Fase R2: ingestione 1m sul sottoinsieme operativo, con validazione dell'edge **netto da fee**.
