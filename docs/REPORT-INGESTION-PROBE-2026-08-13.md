# Ingestion ferma in silenzio: probe a 1 secondo + worker senza budget — incidente e fix (2026-08-13)

## Il sintomo

`⚠️ 89 serie abilitate FERME` in /watchlist, più «Errore sync … (localhost:18080)» dal pulsante di
sync manuale. Nessun allarme prima del banner: le serie si erano accumulate oltre tolleranza nel
corso di ore.

## Cosa NON era (verificato con misura, non a intuito)

**Non era il rate-limit di Binance.** Dall'IP di casa (lo stesso dei pod, via SNAT): `HTTP 200`
con peso usato **2 su 6.000/min**; zero 429/418 nei log del pod — e l'`ExchangeRateLimitHandler`
(cablato anche nell'host ingestion, timeout per-tentativo 15 s) li loggherebbe come warning.
Prima di riorganizzare la sincronizzazione, misurare.

## Le due cause, concatenate

1. **Liveness/readiness senza `timeoutSeconds`** nel manifest ingestion → default Kubernetes di
   **1 secondo**, `failureThreshold 3`: su un nodo affollato (WSL a 2,8 GiB, cpu limit 1) basta un
   `/health` lento tre volte perché kubelet uccida il pod con SIGKILL. Otto riavvii in tre giorni,
   **sempre a metà recupero candele** («OK: 337 candele»), con l'arretrato che cresceva a ogni
   giro. È la stessa lezione già scritta nel deployment **trading** il 2026-08-06 — e mai portata
   su ingestion/ui/ml.
2. **`MarketDataSyncWorker` senza tetto di tempo per ciclo**: il `PeriodicTimer` riarma solo a
   corpo completato. L'incarnazione delle 00:12 ha sincronizzato ~30 secondi, poi una richiesta
   klines è rimasta **appesa senza risposta** (thread starvation nel pod: warning Kestrel
   «heartbeat lento») e il worker è rimasto muto **30 minuti senza un solo errore nei log** —
   IP verso Binance completamente silente, DB senza lock, egress funzionante.

## Il fix (commit `6d12e19`)

- `timeoutSeconds: 5` esplicito su liveness e readiness di **ingestion, ui e ml** (trading lo
  aveva già), col commento-incidente nel manifest. Applicato dal vivo a ingestion e ml
  (`kubectl apply -k`, immagini già pinnate identiche). **ui NON applicata di proposito**: è
  scalata a 0 perché il pod doppierebbe scheduler e sentiment del guscio locale — il manifest
  varrà al prossimo deploy deliberato. Mai `kubectl apply -k infra/k8s/ui` col guscio acceso.
- **Budget di ciclo** nel worker: `RunCycleAsync(budget = 2× intervallo)` — una chiamata appesa
  costa al massimo un ciclo, il budget scaduto si dichiara con warning, il cursore incrementale
  riprende dal punto raggiunto al tick successivo. Lo shutdown ripropaga (non è un timeout).
- Test: `MarketDataSyncWorkerTests` (ciclo appeso fermato al budget; ciclo sano completa;
  shutdown ripropagato). Regressione ingestione 9/9.

## Verifica dal vivo

Dopo il rollout: 40 serie sincronizzate in 2 minuti, **zero** serie non visitate da 30 minuti,
candele al passo (5m a −3 min dal tempo reale). Restano ferme solo le **7 serie in BREAK** già
note dal 2026-07-28 (MKR/TON): non recuperano per costruzione, disabilitarle resta una scelta
umana da /watchlist.

## La classe del difetto, per la prossima volta

«Il default che uccide un processo soltanto lento» + «un loop che aspetta il proprio corpo per
sempre». Quando una serie di pod muore con `Liveness probe failed: context deadline exceeded`
SEMPRE a metà lavoro, il sospetto è la probe, non il lavoro. E ogni worker con `PeriodicTimer`
deve rispondere alla domanda: *se una singola await non torna mai, chi me lo dice, e quanto mi
costa?*
