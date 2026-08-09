# Autonomia — `/admin/autonomy`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Admin/Autonomy.razor`](../../ProcioneMGR/Components/Pages/Admin/Autonomy.razor) (~645 righe) |
| **Route** | `/admin/autonomy` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize(Roles = Admin)]` — solo Admin |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Il **pannello unico di tutti gli automatismi** della piattaforma, prima controllabili solo
da `appsettings.json`. I valori vengono scritti nel file e ricaricati dall'app entro ~1
secondo (*reload on change*): i campi marcati ✅ valgono **a caldo**, quelli ⟳ (gli
intervalli dei timer) dal prossimo riavvio.

**Confini di sicurezza non modificabili da qui** (sono nel codice, non in configurazione):
nessun automatismo può portare una corsia in **Live**; l'AI è solo advisory e può al
massimo porre **veto**; la ri-applica scrive solo configurazione.

## Le card degli automatismi

| # | Card | Sezione config | Cosa controlla |
|---|---|---|---|
| 1 | Esecuzione live "a fette" | `Trading:LiveExecution` | Master switch (default OFF), finestra default, tick worker ⟳, grazia abbandono. Spezza le aperture Testnet/Live in TWAP/VWAP/Iceberg |
| 2 | Ri-applica automatica | `AutoReapply` | Dopo ogni run schedulato, se il nuovo ensemble batte il corrente (confronto con **hysteresis**) e il supervisore non pone veto, lo schiera da solo. Scrive SOLO configurazione |
| 3 | Promozione corsie | `PromotionEvaluator` | Auto-promozione Paper→Testnet e auto-retrocessione, con tutte le soglie: Sharpe minimo, trade minimi, settimane minime, MaxDD, DD hard-block, win rate, Sharpe di retrocessione, intervallo ⟳. **Mai Live** |
| 4 | Supervisione AI (advisory) | `Llm` | Enable, modello, max token, poll ⟳, timeout, soglia errori e cooldown del **breaker**, notifiche decisioni; badge presenza `ANTHROPIC_API_KEY`; bottone "Esegui supervisione ora" |
| 4b | Supervisore veto | `PipelineSupervisor` | Provider **Logging** (solo metriche) o **Claude** (stessa chiave e breaker dell'advisory), timeout. Su qualunque problema **approva subito e decidono le metriche**; hot-swap senza riavvio |
| 4c | Sentiment 2.0 | `Sentiment` | Enable (default **ON**: le serie Binance pubbliche esistono solo 30 giorni — spento = buchi irrecuperabili nei baseline), cadenze metriche/news ⟳, simboli CSV, soglia z estremi, soglie F&G, **Feature ML ⚠️** (opt-in esplicito: cambia cosa vedono i modelli); bottone "Esegui ora" |
| 5 | Drift monitor | `Drift` | Enable, intervallo ⟳, candele recenti, **ritiro automatico del Champion in alert** (solo governance dei record: nessun retrain automatico, nessun impatto sul trading), alert minimi per ritiro; tabella **ultimi 20 esiti** con severità e top feature; bottone "Esegui check ora" |
| 6 | Sync dati & retraining regime | `MarketData` / `MarketRegime` | Enable e cadenze del sync watchlist e del retraining del modello di regime (attivato solo se il Silhouette migliora) |
| 7 | Campagne + trigger contestuale | `Campaign` / `RegimeTrigger` | Gate globale del planner (default **OFF**: è IL cambio di natura da strumento ad agente) e tick ⟳; trigger che sveglia il planner al cambio di cluster K-means o all'uscita dalla banda di volatilità GARCH — non lancia mai run direttamente |
| 8 | Soglie di consolidamento | `EnsembleComparator` / `Registry` | Di quanto un ensemble nuovo deve battere il corrente (isteresi anti-rumore), gambe e simboli minimi, z di significatività; soglia assoluta di **Deflated Sharpe** per diventare Champion. Tutti ⟳: queste due sezioni si leggono una sola volta all'avvio |
| 9 | Carry forward test | `Carry` | Enable, modalità (**Paper/Testnet, mai Live**), simboli, cadenza ⟳, soglie di ingresso/uscita del funding annualizzato, eventi in media, size per gamba; **tabella dello stato per simbolo** e bottone "Valuta ora" |
| 10 | Accumulo liquidazioni | `Liquidations` | Enable (default **ON**: il dato non è ricostruibile a posteriori), flush su DB, soglia di silenzio, retry se l'endpoint risulta bloccato; badge di connessione e conteggio messaggi |
| 11 | Notifiche all'operatore | `Notifications` (×2) | **DUE canali distinti**, non uno: quello del guscio e quello del motore. Enable, provider **Logging/Telegram**, ChatId, rate-limit orario per ciascuno, e una prova per ciascuno che passa dal dispatcher vero (gate + rate-limit compresi). Vedi sotto |
| 12 | Diagnostica | `Ml` / `Observability` | Dual-read ML osservativo verso `procionemgr-ml` (log e metrica, **mai** una decisione) ed export OTLP opt-in — la dashboard `/metrics` funziona comunque a export spento |

Le protezioni che filtrano o fermano le **operazioni** — feed real-time, esposizione
correlata, router di regime, watchdog degli invarianti — stanno invece in
[Protezioni](admin-protections.md).

### Perché le notifiche sono DUE canali (2026-07-29)

Guscio e motore sono processi separati, con configurazioni e variabili d'ambiente proprie. Il
producer più importante della piattaforma — il watchdog che mette una corsia in **quarantena**
quando la sua contabilità diventa impossibile — vive in `procionemgr-trading`, non nel guscio.

Tenerli in un blocco solo sarebbe la trappola perfetta: si accende l'interruttore, si vede il
messaggio verde, e gli allarmi che contano restano muti. È quello che è successo davvero — il
guscio recapitava (dopo che gli era stato restituito il token, perso nel consolidamento del 27
luglio) mentre il motore non aveva **né** l'interruttore acceso **né** il token, e quindi gli
allarmi di quarantena non erano mai arrivati a nessuno. Nessuno poteva accorgersene: il dispatcher
per contratto non propaga gli errori di recapito ai producer, e non esisteva un modo di *chiedere*
al canale se funzionasse.

Da qui il disegno:

| | Legge/scrive | Prova |
|---|---|---|
| Canale del guscio | `IOptionsMonitor` + `IAppConfigWriter` locali | `NotificationDispatcher.SendDiagnosticAsync` in-process |
| **Canale del motore** | `IEngineConfigStore` → gRPC `Get/SetEngineConfig` | rpc `SendTestNotification`, eseguito **dal motore** |

Entrambe le prove attraversano la catena vera (gate, rate-limit condiviso, provider corrente) e ne
restituiscono l'**esito** — `Delivered` / `Disabled` / `RateLimited` / `UnknownProvider` / `Failed`
col motivo. La prima versione del pulsante mostrava un alert verde qualunque cosa fosse successo,
perché passava da `NotifyAsync`, che è `void` per progetto: una verifica che rassicura sempre è
peggio di nessuna verifica.

**[E5, 2026-07-31] La spia di guasto.** La prova è un gesto dell'operatore; fra una prova e l'altra
un recapito fallito (token scaduto, rete) viveva solo nei log. Ora ogni canale mostra la sua
**spia**: ultimo recapito riuscito, ultimo fallimento col motivo, e quanti fallimenti si sono
accumulati dall'ultimo recapito (rossa quando > 0: il canale sta perdendo messaggi ADESSO). Per il
guscio la legge `NotificationDispatcher.ChannelStatus`; per il motore l'rpc
`GetNotificationChannelStatus`, che non invia nulla — e distingue «canale mai usato da questo
avvio» (non un guasto) da «nessun canale composto nell'host» (un guasto grave, detto in rosso).

Il **token** di ciascun canale non passa da questa pagina e non sta nelle sezioni di
configurazione: per il guscio è `TELEGRAM_BOT_TOKEN` nell'ambiente del processo, per il motore la
stessa variabile fornita dal Secret `trading-secrets` (vedi `scripts/k8s-trading-secret.ps1`). Se
manca, la prova lo dice invece di tacere.

### Le sezioni ospitate dal motore (E2/E3, 2026-07-31)

Non solo Carry e Notifiche: anche **Esecuzione live** (`Trading:LiveExecution`, il worker che
avanza le fette vive nell'host del motore) e **Dual-read ML** (`Ml`, il confronto lo fa
`TradingEngine`) passano da `IEngineConfigStore`. Fino al 2026-07-31 quei due pannelli leggevano il
monitor del guscio e salvavano col writer del guscio: col trading remoto la manopola non muoveva
nulla — e per `Ml` era anche peggio, perché il Trading host non faceva nemmeno il binding della
sezione (toggle non collegabile da nessuna strada). Il salvataggio condiviso è
`SaveEngineSectionAsync`: validazione `AdminConfigRules`, scrittura sullo store, rilettura della
fotografia.

**[E4] Sync dati con ingestion remota.** Con `MarketData:UseRemoteIngestion=true` lo scheduling del
sync vive nel servizio ingestion in-cluster, che legge la *propria* configurazione: i tre campi
della card (interruttore, cadenza, backfill) non hanno alcun lettore nel guscio. Il pannello li
**disabilita e lo dichiara** (la manopola vera è la ConfigMap del deployment ingestion), invece di
mostrare un salvataggio verde che non muove nulla. Un canale di configurazione remota verso
l'ingestion non è stato costruito, con motivo dichiarato nella ROADMAP (Filone E).

## Come funziona (flusso del codice)

### Copie locali e salvataggio (righe 470–539, 550–560)
La pagina lavora su **copie locali** delle opzioni (clone via JSON round-trip): si parte da
`IOptionsMonitor<T>.CurrentValue` e si scrive solo al Salva — così i campi non "saltano
sotto le dita" quando il monitor ricarica il file appena scritto.
`IAppConfigWriter.SaveSectionAsync(section, model)` **sostituisce l'intera sezione** in
`appsettings.json`: per questo i POCO locali (`MarketDataConfig`) includono anche i toggle
non esposti nel form (`UseRemoteIngestion`, `RemoteIngestionUrl`) — senza il round-trip, un
salvataggio da questa pagina li cancellerebbe dal file.

Il round-trip però copre solo gli **scalari** che il POCO conosce, e questo non bastava: la
sezione `MarketData` contiene la **sottosezione** `Realtime` (l'intera configurazione del feed
WebSocket, compreso se i tick possono chiudere posizioni), che il POCO del form non modella.
Fino all'audit del 2026-07-29 il salvataggio della card 6 la **cancellava**, e il feed tornava
ai default in silenzio. Il writer ora preserva le sottosezioni che il payload non nomina —
un oggetto annidato che il POCO non conosce appartiene a un altro pannello, non è una
proprietà dimenticata.

### Validazione lato server
Ogni Salva passa da
[`AdminConfigRules.Validate`](../../ProcioneMGR/Services/Config/AdminConfigRules.cs) prima di
toccare il file: l'attributo `min=` dell'HTML non vincola `@bind`, quindi senza quel controllo
un `Llm:MaxTokens=0` o un `Drift:IntervalHours=0` (che fa esplodere il `PeriodicTimer` del
worker al riavvio successivo, uccidendo la funzione in silenzio) finivano dritti in
`appsettings.json`. Nessuna correzione silenziosa dei valori: un numero cambiato sotto le dita
di chi lo ha appena scritto è peggio di un rifiuto esplicito.

### Azioni "esegui ora"
Ogni worker espone `TickAsync` invocabile on-demand:
- `LlmWorker.TickAsync(forceProbe: true)` — supervisione advisory subito, ignorando il
  cooldown del breaker; messaggio differenziato se manca la API key.
- `SentimentWorker.TickAsync(forceNews: true)` — metriche+news subito, con conteggi.
- `DriftWorker.TickAsync` — check drift su tutti i modelli salvati, poi ricarica la tabella.

### Badge semantici (righe 617–632)
`Hot()` ✅ / `Restart()` ⟳ / `StateBadge` ATTIVO-SPENTO / `SeverityBadge` per il drift:
il pannello è auto-documentante su quale campo ha effetto quando.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IAppConfigWriter` | Scrittura sezioni di appsettings.json | [`Services/Config/AppConfigWriter.cs`](../../ProcioneMGR/Services/Config/AppConfigWriter.cs) |
| `IOptionsMonitor<T>` (×7) | Valori correnti con hot-reload | (framework + POCO opzioni nei rispettivi servizi) |
| `LlmSupervisorWorker` / `SentimentSyncWorker` / `FeatureDriftWorker` | I worker con tick on-demand | [`Services/Llm/`](../../ProcioneMGR/Services/Llm) · [`Services/Sentiment/`](../../ProcioneMGR/Services/Sentiment) · [`Services/Monitoring/Drift/`](../../ProcioneMGR/Services/Monitoring/Drift) |
| `AutoReapplyOptions` → `RunApplyEvaluator`/`EnsembleComparator` | Il ciclo di ri-applica governato dalla card 2 | [`Services/Pipeline/RunApplyEvaluator.cs`](../../ProcioneMGR/Services/Pipeline/RunApplyEvaluator.cs) |
| `PromotionEvaluatorOptions` → `PromotionWorker` | Il ciclo di promozione governato dalla card 3 | [`Services/Trading/PromotionWorker.cs`](../../ProcioneMGR/Services/Trading/PromotionWorker.cs) |
| `SupervisorAgentOptions` → `DelegatingSupervisorAgent` | L'hot-swap Logging/Claude del veto | [`Services/Agents/DelegatingSupervisorAgent.cs`](../../ProcioneMGR/Services/Agents/DelegatingSupervisorAgent.cs) |
| `DriftCheckResult` (entità) | Gli esiti in tabella | [`Services/Monitoring/Drift/DriftModels.cs`](../../ProcioneMGR/Services/Monitoring/Drift/DriftModels.cs) |

## Dati letti / scritti

- **Legge**: `appsettings.json` (via IConfiguration/monitor), `DriftCheckResults` (ultimi 20).
- **Scrive**: `appsettings.json` (sezioni intere); i tick on-demand producono artifact/esiti.

## Collegamenti con le altre pagine

- [Supervisione AI](admin-ai-supervisor.md) — stato/recupero del layer advisory.
- [Sentiment](sentiment.md) — la dashboard del mood che questa pagina alimenta.
- [Trading](trading.md) — promozioni e esecuzione a fette agiscono lì.
- [Pipeline](pipeline.md) / [Campagne](campaign.md) — i run su cui operano ri-applica e veto.
- [Registry](registry.md) — il ritiro automatico del Champion in alert.

## Note di design

- Il pattern "copia locale + salva sezione intera + round-trip dei campi nascosti" è la
  lezione appresa per convivere con l'hot-reload di configurazione senza perdere campi.
- La distinzione ✅/⟳ per campo evita la domanda ricorrente "serve riavviare?".
- Default deliberati: esecuzione a fette OFF (rischio), Sentiment ON (dati deperibili).

## Topologia e infrastruttura (Fase 3, 2026-08-09)

Il card in fondo alla pagina espone le chiavi che prima erano «deliberatamente senza UI»
(mandato: l'amministratore governa tutto dall'interfaccia). Tutte ⟳ (valgono dal riavvio):

- `Trading:UseRemoteTrading` — motore in-process vs Deployment `procionemgr-trading`; il warning
  accanto spiega la regola «un solo scrittore»: l'interruttore va cambiato INSIEME al deployment.
- `Ml:RemoteUrl` — canale gRPC del dual-read ML (si crea una volta sola a startup; vuoto = spento).
- `Http:DisableHttpsRedirection` — proprietà dell'hosting; sbagliarla in locale può rendere la UI
  irraggiungibile (recovery: correzione a mano nel file + riavvio).
- `Database:AutoMigrate` + `LockTimeoutSeconds` — migrate-on-startup; l'esito si legge nel log di avvio.
- `FactorCache:MaxEntries` — solo memoria (invariante cache == ricalcolo).

Nel pannello Sync è comparsa la **topologia ingestion** (`MarketData:UseRemoteIngestion` +
`RemoteIngestionUrl`), col suo bottone separato che resta attivo anche a ingestion remota accesa —
altrimenti dalla UI non si potrebbe mai tornare indietro. Nel pannello Retraining regime sono
esposti `MarketRegime:Model` (KMeans/Jump) e `JumpLambda`, con la nota del contratto C1: il
confronto delle transizioni sta nei log di ogni training, si cambia dopo averlo letto.
Gli scalari si salvano con `SaveValueAsync` (scrittura chirurgica): niente POCO dell'intera
sezione, niente chiavi cancellate per dimenticanza.
