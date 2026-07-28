# PRD — Integrazione "core caldo / guscio freddo" (2026-07-26)

*Documento di prodotto del filone B della [ROADMAP](ROADMAP.md). Le fasi A (consolidamento) e C
(algoritmi) hanno i loro gate direttamente in roadmap; qui vive l'architettura di destinazione,
gli invarianti e il piano di prova del committment ai microservizi.*

## 1. Problema e criterio di decisione

La piattaforma gira da monolite: motore di trading, uscite protettive, feed realtime, carry e UI
vivono nello stesso processo. In sviluppo intenso — che è lo stato permanente di questo progetto —
**ogni riavvio dell'app uccide l'operatività**: posizioni gestite, stop, trailing, accumulo
liquidazioni. Con carry Paper acceso e feed R1 in arrivo, questo costo non è più estetico.

I tre servizi estratti (Ingestion, Ml, Trading) + contratti + K8s/GitOps/observability esistono e
funzionano, ma con tutti i toggle a `false`: un binario mantenuto e mai esercitato — la forma più
costosa di ridondanza (paghi la sincronizzazione, non incassi il beneficio).

**Decisione** (proprietario, 2026-07-26): il criterio che pesa è l'operatività continua durante lo
sviluppo. Quindi committment: il binario remoto diventa quello *vero*, per gradi e con gate,
finché il ramo in-process del motore nel monolite può essere ritirato (B5).

## 2. Chiamare le cose col loro nome

Con DB Postgres condiviso e assembly condiviso questo è un **monolite distribuito**, non
"microservizi da manuale" — ed è la taglia giusta per un operatore singolo. I confini di questo
PRD sono quindi anche confini di *non-fare*:

- **NON** si spacca il database per servizio;
- **NON** si estraggono pipeline, supervisore AI, sentiment, scheduler (restano nel guscio);
- **NON** si introduce un event bus né più repliche del motore (replicas:1 + Recreate);
- **NON** cambia alcun patto di sicurezza: mai auto-Live, conferma manuale per ordini Live,
  AI advisory-only, promozione Testnet→Live solo umana.

## 3. Architettura di destinazione

```
┌─ CLUSTER K8S (kind) ────────────────────────────────────────────────┐
│                                                                     │
│  CORE CALDO (sempre acceso)          GUSCIO FREDDO (riavviabile)    │
│  ┌──────────────────────────┐        ┌──────────────────────────┐   │
│  │ procionemgr-trading      │  gRPC  │ procionemgr (monolite)   │   │
│  │  TradingEngine ×N corsie │◄───────│  UI Blazor + Identity    │   │
│  │  TradingWorker/ExecWorker│ +secret│  Pipeline + Discovery    │   │
│  │  Feed R1 (WebSocket)     │        │  EnsembleRebalance (SCRIVE│  │
│  │  LaneInvariantWatchdog   │        │   i pesi: resta qui)     │   │
│  │  Carry worker            │        │  Supervisore AI, Telegram│   │
│  └────────────┬─────────────┘        └────────────┬─────────────┘   │
│               │                                   │                 │
│  ┌────────────▼───────────────────────────────────▼─────────────┐   │
│  │ PostgreSQL (unico, condiviso — con LEASE advisory per corsia)│   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                     │
│  procionemgr-ingestion (sync OHLCV schedulato)                      │
│  procionemgr-ml (inferenza gRPC — SOLO dopo parità dual-read)       │
└─────────────────────────────────────────────────────────────────────┘
```

Collocazioni non negoziabili (già codificate nei commenti di `TradingServiceCollectionExtensions`):
il feed R1 vive **nello stesso host del motore** (un tick per gRPC reintrodurrebbe la latenza che
il feed toglie); il watchdog invarianti vive dove vive il motore; l'`EnsembleRebalanceWorker`
resta nel guscio (unico scrittore dei pesi); il carry — che oggi è un worker del monolite — si
sposta nel core con B3, perché è operatività che deve sopravvivere ai riavvii del guscio.

## 4. Invarianti di sicurezza (rafforzati, mai indeboliti)

1. **Mai due esecutori sulla stessa corsia.** Oggi retto dalla registrazione condizionale +
   disciplina di deploy ("il Deployment non deve MAI essere vivo col toggle a false"). Diventa
   invariante applicata: **lease advisory Postgres per corsia** (`pg_try_advisory_lock` su chiave
   derivata dal LaneId, connessione dedicata tenuta aperta per la vita del worker). Chi non
   ottiene il lease NON alimenta il motore, lo dice con LogCritical e ritenta — un deploy
   incoerente fallisce a voce alta invece di eseguire doppio. (B0, si implementa subito: protegge
   anche l'assetto attuale.)
2. **Ogni scrittore ha esattamente un host** (regola di Fase 2b, invariata).
3. **Fail-fast di configurazione**: `RemoteUrl`/`GrpcSharedSecret` obbligatori col toggle acceso
   (già esistente, invariato).
4. **Il guscio degrada, il core no**: se il gRPC è giù la UI mostra errore e riprova; il core
   continua a gestire le posizioni da solo (già il design di Fase 2b, ora diventa il caso d'uso
   primario invece che teorico).

## 5. Fasi, gate e rollback

| Fase | Azione | Gate di uscita | Rollback |
|---|---|---|---|
| B0 | Lease advisory per corsia (codice, subito) | test di conflitto verdi | è additivo: nessuno |
| B1 | Monolite in K8s (baseline di Fase 3 rivalidata) | app su, login sopravvive al riavvio del pod (keyring su PVC), backup/restore provato | si spegne il cluster, si torna a `dotnet run` |
| B2 | Ingestion remota ON | 7 giorni senza buchi nelle candele (query di copertura già in PlatformExpand `stats`) | toggle a `false` + riavvio |
| B3 | Trading remoto ON, R1 nel core (prima `DriveProtectiveExits=false`) | **chaos test** ✓ (2026-07-26) · confronto tick-vs-candle ✓ (2026-07-28, **esito negativo**: vedi nota sotto) | toggle a `false` + spegnere il Deployment trading (mai entrambi vivi) |
| B4 | ML remoto | parità dual-read su N settimane (`procione.ml.comparisons`) — se non arriva, resta in-process **per misura, non per rinuncia** | toggle |
| B5 | Ritiro del ramo in-process del motore dal monolite | B3 stabile da ≥ 1 mese; suite adattata | git revert (il ramo è isolato in `TradingServiceCollectionExtensions`) |

### 5.a Nota su B3: il gate come era scritto non si poteva soddisfare

Il gate di B3 chiedeva il confronto `source=tick` contro `source=candle` sulla metrica
`procione.trading.protective_exits`. In assetto osservativo quel confronto **non è producibile**: i
tick vengono scartati prima di raggiungere il motore, e la metrica conta solo le uscite realmente
scattate — quindi `source=tick` compare solo dopo l'accensione che il confronto doveva autorizzare.

La domanda è stata chiusa il 2026-07-28 **offline**, con le candele a risoluzione fine come
surrogato dei tick (`ProtectiveExitLagAnalyzer`), e la risposta è **no**: uscire al momento del
tocco è risultato peggiore che uscire alla chiusura della barra su tutte e 24 le configurazioni
provate. `DriveProtectiveExits` resta quindi `false` **per misura, non in attesa di una misura** —
la stessa formula usata per il router di regime dopo C1. Metodo, numeri e limiti dichiarati in
[REPORT-B3-EXITLAG](REPORT-B3-EXITLAG-2026-07-28.md).

Lezione da portarsi dietro per B4: un gate va scritto insieme allo **strumento** che lo misura,
altrimenti resta aperto senza che nessuno sappia dire perché.

## 6. Piano di prova

- **Contract test** sui .proto (già esistono i test di composizione DI mutuamente esclusiva);
- **Smoke e2e su kind in CI** per la classe di bug che TestServer non vede (h2c, doppio worker,
  ConfigMap non applicato — tutti trovati solo eseguendo, mai dai test in-process);
- **Chaos test manuale scriptato** (B3): kill del guscio, verifica protezioni; kill del core,
  verifica che il guscio lo dichiari (banner UI + notifica) invece di fingere;
- **Drill di restore** dal backup pg_dump prima di B3.

## 7. Metriche di successo

1. Zero interruzioni dell'operatività (posizioni gestite, protezioni attive) causate da
   riavvii/deploy del guscio — misurabile dai gap in `procione.trading.protective_exits` e dai
   log del core;
2. Il lease per corsia non registra MAI un secondo contendente in condizioni normali (ogni
   contesa = un deploy sbagliato scoperto dal lock, non dal DB corrotto);
3. La suite resta verde a ogni fase; nessun nuovo percorso di scrittura concorrente.
