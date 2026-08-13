# Il feed che gridava a vuoto su STX, e l'interruttore che non spegneva niente (2026-08-13)

## Il sintomo

Notifiche Telegram a raffica, in coppia, ogni 1-2 minuti:

> 🟡 **1 serie del feed real-time non rispondono** — Nessun tick/candela da oltre 60s su:
> Binance STX/USDT (ultimo: 11:25:07). Per queste serie gli stop reagiscono solo alla chiusura
> candela (percorso REST), con un ritardo che può arrivare a diversi minuti.
>
> ℹ️ **Feed real-time ripristinato per 1 serie** — Tornati gli eventi su: Binance STX/USDT.

…e, dopo un po', «(+2 notifiche soppresse dal rate-limit nell'ultima ora)». L'operatore aveva
**disabilitato le quattro serie STX/USDT dalla watchlist** proprio per farle tacere, senza alcun
effetto.

## Perché STX «si bloccava» — non si bloccava affatto

Il feed WebSocket giudica una serie *stale* se non riceve eventi per `StaleAfterSeconds` (60s).
**STX/USDT è illiquido**: silenzi di uno o due minuti sono il suo ritmo normale, non un guasto.
La soglia è tarata sui simboli liquidi — le altre corsie (DOT, LTC, XRP, ETC) non hanno prodotto
un solo allarme nella vita del pod. Ogni volta che STX taceva più di 60s partiva l'allarme; al
primo trade successivo partiva il «ripristinato». Due messaggi per ciclo, un ciclo ogni paio di
minuti.

Il testo del messaggio era per giunta **fuorviante**: diceva «per queste serie gli stop reagiscono
solo alla chiusura candela», ma con `DriveProtectiveExits=false` — il default **per misura** (B3:
uscire al tocco è peggio che a barra chiusa, 24 configurazioni su 24) — quello è il comportamento
normale e deliberato di *tutte* le serie, sempre. Annunciava come guasto la normalità dichiarata.

## Perché disabilitare la watchlist non serviva a niente

Sono **due meccanismi che non si parlano**:

| | Cosa governa | Da cosa si instrada |
|---|---|---|
| Watchlist (`TrackedSeries.Enabled`) | l'aggiornamento REST delle candele | la tabella della watchlist |
| Feed real-time | tick e candele in anticipo verso il motore | le **corsie in esecuzione** (`TradingEngineStates`) |

La **corsia 7 stava operando STX/USDT 4h in Paper**. Il feed continuava quindi a sottoscrivere STX
e a sorvegliarla, del tutto indifferente alla watchlist.

Peggio: disabilitando le serie, l'operatore ha ottenuto la combinazione peggiore delle due —
**una corsia viva su una serie che nessuno aggiorna più**. Le candele STX si erano fermate alle
06:15 UTC (15m) e 06:00 (1h), l'ora del toggle.

## Il danno vero: il budget del canale

Il dispatcher ha un rate-limit di 20 messaggi/ora, **condiviso da tutti i produttori**. Quel rumore
ne consumava ~40/ora, e infatti il canale stava già sopprimendo. Gli allarmi che contano davvero —
corsia in quarantena, posizioni orfane, corsia affamata — sarebbero stati soppressi insieme al
rumore. Un allarme che grida sempre non è solo fastidioso: **rende invisibili quelli veri**.

## Il fix

**1. Tre filtri sulla NOTIFICA** (`RealtimePriceWorker`), lasciando il log fitto com'era:
- **persistenza**: 3 controlli consecutivi oltre soglia, non un campione singolo;
- **azionabilità**: in sola osservazione un'intermittenza non ha conseguenze operative → resta nel
  log. Continua invece a notificare, anche in osservazione, il caso **strutturale**: uno stream che
  non ha *mai* consegnato è rotto o bloccato (è il blocco EEA/MiCA visto sulle liquidazioni);
- **cooldown** di un'ora per serie: un guasto che dura si ricorda, non si ripete a ogni giro.

Il rientro si annuncia solo a chi ha ricevuto l'allarme, e il testo dice la conseguenza vera
nell'assetto corrente.

**2. La divergenza watchlist ↔ corsie, detta a voce alta** (`/market/watchlist`): banner rosso in
cima quando una corsia in esecuzione opera una serie disabilitata, e avviso al momento del toggle —
dove l'equivoco nasce.

**Misura del fix**: nel test che simula il ritmo reale di STX nell'assetto peggiore (feed che guida
le uscite, quindi allarme legittimo), si passa da ~40 messaggi/ora a **1**.

## Cosa resta all'operatore

Una decisione che il software non deve prendere da solo: **la corsia 7 su STX/USDT 4h va tenuta o
fermata?**
- se serve → riabilitare le serie STX in watchlist, altrimenti opera su candele che non arrivano;
- se non serve → fermarla da `/trading`, e allora le serie possono restare disabilitate.

## La classe del difetto

Due interruttori che *sembrano* governare la stessa cosa («seguo STX» / «opero STX») e invece
governano metà del problema ciascuno, senza che nessuna schermata lo dica. È lo stesso stampo dei
«controlli che rassicurano a prescindere dalla realtà»: qui non era un controllo a mentire, ma la
mappa mentale che la UI suggeriva.
