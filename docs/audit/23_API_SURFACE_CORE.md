# 23 — SUPERFICIE API: nucleo operativo

> Estrazione **esaustiva e meccanica** della superficie API dal sorgente: nessun campione,
> nessuna parafrasi. Ogni tipo e ogni membro pubblico (o di interfaccia) con la firma reale
> e il doc-comment che il codice gli associa.

Trading, esecuzione, rischio, carry, flotta, sicurezza: **il codice che può muovere denaro**. Ogni tipo e ogni membro con firma e doc-comment.

| | |
|---|---:|
| File coperti | 119 |
| Tipi | 277 |
| Membri (metodi, proprietà, costruttori, costanti) | 1036 |

**Legenda:** 🔌 interface · 📦 class · 🧾 record · 🔢 enum · ▫️ struct · `m` metodo · `p` proprietà · `c` costruttore · `k` costante

---

# `Services/Trading/`

## `ProcioneMGR/Services/Trading/Behaviors/LoggingBehavior.cs`

### 📦 `LoggingBehavior<TMessage, TResponse>` `(ILogger&lt;LoggingBehavior&lt;TMessage, TResponse&gt;&gt; logger)`

> Punto unico di logging per ogni comando/query di trading (Fase 1, PRD-CONSOLIDAMENTO- ARCHITETTURA.md §4.5): sostituisce, mano a mano che i verbi migrano a Mediator, le chiamate logger.LogInformation / LogWarning oggi sparse nei singoli metodi di .

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;TResponse&gt; Handle(TMessage message, MessageHandlerDelegate&lt;TMessage, TResponse&gt; next, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/ClosePositionCommand.cs`

### 🧾 `ClosePositionCommand` `(int LaneId, string PositionId) : IRequest;`

### 📦 `ClosePositionCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;ClosePositionCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(ClosePositionCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/ConfirmOrderCommand.cs`

### 🧾 `ConfirmOrderCommand` `(int LaneId, string OrderId, string? UserId) : IRequest;`

### 📦 `ConfirmOrderCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;ConfirmOrderCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(ConfirmOrderCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/EmergencyStopCommand.cs`

### 🧾 `EmergencyStopCommand` `(int LaneId, string Reason) : IRequest;`

### 📦 `EmergencyStopCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;EmergencyStopCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(EmergencyStopCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/RejectOrderCommand.cs`

### 🧾 `RejectOrderCommand` `(int LaneId, string OrderId, string? UserId) : IRequest;`

### 📦 `RejectOrderCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;RejectOrderCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(RejectOrderCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/SetStopLossTakeProfitCommand.cs`

### 🧾 `SetStopLossTakeProfitCommand` `(`

### 📦 `SetStopLossTakeProfitCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;SetStopLossTakeProfitCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(SetStopLossTakeProfitCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/StartLaneCommand.cs`

### 🧾 `StartLaneCommand` `(int LaneId, TradingMode Mode) : IRequest;`

### 📦 `StartLaneCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;StartLaneCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(StartLaneCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Commands/StopLaneCommand.cs`

### 🧾 `StopLaneCommand` `(int LaneId) : IRequest;`

### 📦 `StopLaneCommandHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;StopLaneCommand&gt;`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;Unit&gt; Handle(StopLaneCommand request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/DecimalValueMapper.cs`

### 📦 `DecimalValueMapper`

> Conversione fra di dominio e il wrapper di common.proto (convenzione google.type.Decimal-like:

| | Firma | Descrizione |
|---|---|---|
| `m` | `DecimalValue ToProto(decimal value)` | — |
| `m` | `DecimalValue? ToProtoNullable(decimal? value)` | Campo proto assente =&gt; null (has-bit dei message: distingue "non impostato" da 0). |
| `m` | `decimal FromProto(DecimalValue value)` | — |
| `m` | `decimal? FromProtoNullable(DecimalValue? value)` | — |
| `m` | `decimal FromProtoOrZero(DecimalValue? value)` | Per i campi non opzionali: un message assente sul wire vale 0 (default proto3). |

## `ProcioneMGR/Services/Trading/EngineConfigSections.cs`

### 📦 `EngineConfigSections`

> L'elenco CHIUSO delle sezioni di configurazione che il guscio può leggere e riscrivere sul motore via gRPC ( GetEngineConfig / SetEngineConfig ). Questa classe è il confine di sicurezza di quel canale, e vive in un file suo perché sia difficile allargarla per distrazione. SetEngineConfig scrive su un processo che firma ordini veri: senza un elenco chiuso sarebbe un'API di configurazione generica su una superficie che comprende la connection string del database, la master key con cui si decifrano le credenziali exchange, il segreto condiviso che autorizza il canale stesso e i toggle che decidono quale processo esegue gli ordini. Nessuno di questi è raggiungibile da qui — non perché il chiamante sia gentile, ma perché il server rifiuta tutto ciò che non è in . Il criterio per entrare: la sezione governa un COMPORTAMENTO OPERATIVO ospitato dal motore, che un operatore deve poter cambiare s…

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;string&gt; Writable` | Sezioni leggibili E scrivibili dal pannello. Tutte governano componenti che vivono nell'host del motore: cambiarle sul guscio non avrebbe alcun effetto. |
| `p` | `IReadOnlyList&lt;string&gt; ReadOnly` | Sezioni che il pannello può LEGGERE per mostrare il contesto, ma mai riscrivere. Non sono segreti — sono fatti sulla topologia che l'operatore ha diritto di vedere e che deve cambiare dal deploy. |
| `m` | `bool IsWritable(string section)` | Vero se la sezione può essere riscritta dal guscio. |
| `m` | `bool IsReadable(string section)` | Vero se la sezione può essere letta dal guscio (i scrivibili sono anche leggibili). |
| `m` | `IEnumerable&lt;string&gt; AllReadable()` | Tutte le sezioni leggibili, nell'ordine in cui hanno senso per un pannello. |

## `ProcioneMGR/Services/Trading/EngineConfigService.cs`

### 🧾 `EngineConfigSectionView` `(string Path, string Json, bool Writable, string Source);`

> Una sezione di configurazione del motore, come il motore la vede adesso. Percorso della sezione (es. Trading:Safety ). Valori EFFETTIVI (file + variabili d'ambiente + default, già fusi). Se il guscio può riscriverla. Provider che fornisce il valore, per spiegare perché salvare non basta.

### 🧾 `EngineConfigWriteResult` `(string AppliedJson, string? Warning);`

> Esito di una scrittura: la sezione riletta, più un eventuale avvertimento non bloccante.

### 📦 `EngineConfigService` `(`

> Legge e scrive le sezioni di configurazione OSPITATE DAL MOTORE. Vive nel progetto condiviso perché la usano entrambi gli host: ProcioneMGR.Trading la espone via gRPC, e il monolite la usa direttamente quando il motore gira in-process (stessa logica, nessun ramo speciale). Perché esiste, in una riga: quando il motore gira in un altro processo, il file che il guscio scrive non è quello che il motore legge — verificato dal vivo il 2026-07-29 su un PVC rimasto a {} . Da qui in poi il guscio non indovina più: chiede. Tre garanzie, nell'ordine in cui contano: si tocca solo ciò che è in — elenco chiuso; si valida con le STESSE regole dei pannelli ( ), così un valore rifiutato in UI non entra da un'altra porta; si dice la verità sulla SORGENTE: in Kubernetes le variabili d'ambiente della ConfigMap vincono su appsettings.json , quindi un salvataggio può riuscire e non cambiare nulla. Tacerlo sa…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ConfigPath` | Dove il motore scrive: lo stesso file che tocca. |
| `m` | `bool IsWritable()` | Il motore può riscrivere la propria configurazione? Falso quando il file non esiste o è in sola lettura (es. montato da ConfigMap). Il pannello lo dice PRIMA, invece di far scoprire il rifiuto al primo salvataggio. |
| `m` | `IReadOnlyList&lt;EngineConfigSectionView&gt; Read(IEnumerable&lt;string&gt;? sections = null)` | Legge le sezioni richieste (vuoto = tutte quelle note). Le sconosciute o proibite vengono SALTATE in silenzio in lettura: un pannello che chiede più del dovuto non deve far fallire l'intera schermata — mentre in SCRITTU… |
| `m` | `Task&lt;EngineConfigWriteResult&gt; WriteAsync(string section, string json, CancellationToken ct = default)` | Sostituisce una sezione. Rifiuta con ciò che non è scrivibile o non passa la validazione: il chiamante gRPC lo traduce in un codice di stato, il chiamante in-process lo mostra all'operatore. |

## `ProcioneMGR/Services/Trading/EngineConfigStore.cs`

### 🔌 `IEngineConfigStore`

> Da dove arrivano — e dove finiscono — le sezioni di configurazione OSPITATE DAL MOTORE. Stesso patto di IMarketDataSyncService : il pannello inietta l'interfaccia e ignora quale implementazione sia attiva. Col motore in-process si scrive il file di questo processo, che è anche quello che il motore legge; col motore remoto si parla con lui via gRPC, perché il suo file non è il nostro — è la lezione del 2026-07-29, quando il PVC che avrebbe dovuto condividerli era rimasto a {} e ogni soglia mostrata in UI era quella sbagliata.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsRemote` | Vero se il motore vive in un altro processo (cambia solo cosa dire all'operatore). |
| `m` | `Task&lt;EngineConfigSnapshot&gt; ReadAsync(IEnumerable&lt;string&gt;? sections = null, CancellationToken ct = default)` | Sezioni richieste (vuoto = tutte quelle note), coi valori EFFETTIVI del motore. |
| `m` | `Task&lt;EngineConfigWriteResult&gt; WriteAsync(string section, object options, CancellationToken ct = default)` | Sostituisce una sezione. Lancia con un messaggio leggibile se la sezione non è scrivibile, il valore non è valido, o il motore non risponde. |
| `m` | `Task&lt;NotificationResult&gt; SendTestNotificationAsync(CancellationToken ct = default)` | Prova il canale di notifica DEL MOTORE, non quello del guscio: sono due processi con variabili d'ambiente diverse, e il motore può essere muto mentre il guscio recapita. È il producer degli allarmi di quarantena, quindi… |
| `m` | `Task&lt;EngineNotificationChannelStatus&gt; GetNotificationChannelStatusAsync(CancellationToken ct = default)` | [E5] Spia di guasto del canale DEL MOTORE, letta senza inviare nulla: ultimo recapito, ultimo fallimento col motivo, fallimenti accumulati. La prova qui sopra è un gesto; questa è la memoria di ciò che è successo fra un… |

### 🧾 `EngineNotificationChannelStatus` `(`

> [E5] Spia del canale del motore come la vede il guscio. Falso se il motore remoto non ha risposto: non significa nulla. Falso se l'host del motore non ha composto alcun canale di notifica. Lo stato del canale, quando raggiungibile e composto. Motivo dell'irraggiungibilità.

### 🧾 `EngineConfigSnapshot` `(`

> Fotografia della configurazione del motore, con la diagnostica che la rende leggibile. Le sezioni lette. File su cui il motore scrive (vuoto se non lo sappiamo). Il motore può riscrivere la propria configurazione? Falso quando il motore remoto non risponde: il pannello mostra l'ultimo stato noto (i default) DICENDO che non è stato possibile chiederglielo, invece di spacciarlo per la verità. Motivo dell'irraggiungibilità, se è falso.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string? SourceOf(string section)` | Sorgente prevalente della sezione ("ConfigMap", "file", "default del codice"…). |
| `p` | `JsonSerializerOptions JsonOptions` | — |

### 📦 `LocalEngineConfigStore` `(`

> Motore in-process: il file di questo processo È quello che il motore legge, quindi si scrive direttamente. Nessuna rete, nessun caso di irraggiungibilità. OPZIONALE, come per ogni altro consumatore di notifiche in questa composizione: il LaneInvariantWatchdog risolve INotifier con GetService e non con GetRequired , perché una corsia deve poter girare in un host che non ha composto alcun canale. Pretenderlo qui avrebbe rovesciato quell'invariante per un pulsante di diagnostica — e fatto fallire l'avvio invece di dire che il canale non c'è.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsRemote` | — |
| `m` | `Task&lt;EngineConfigSnapshot&gt; ReadAsync(IEnumerable&lt;string&gt;? sections = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;EngineConfigWriteResult&gt; WriteAsync(string section, object options, CancellationToken ct = default)` | — |
| `m` | `Task&lt;NotificationResult&gt; SendTestNotificationAsync(CancellationToken ct = default)` | Motore in-process: il suo canale di notifica è questo, quindi si prova direttamente. |
| `m` | `Task&lt;EngineNotificationChannelStatus&gt; GetNotificationChannelStatusAsync(CancellationToken ct = default)` | Motore in-process: la spia è quella del dispatcher di questo host. |

### 📦 `RemoteEngineConfigStore` `(`

> Motore remoto: si chiede a lui. Gli errori di trasporto NON diventano eccezioni in faccia a chi apre la pagina — in lettura si degrada a "non raggiungibile" con i default, perché un pannello che esplode quando il core è giù è inutile proprio nel momento in cui serve guardarlo. In SCRITTURA invece si propaga: lì il silenzio farebbe credere di aver cambiato qualcosa.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsRemote` | — |
| `m` | `Task&lt;EngineConfigSnapshot&gt; ReadAsync(IEnumerable&lt;string&gt;? sections = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;EngineConfigWriteResult&gt; WriteAsync(string section, object options, CancellationToken ct = default)` | — |
| `m` | `Task&lt;NotificationResult&gt; SendTestNotificationAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;EngineNotificationChannelStatus&gt; GetNotificationChannelStatusAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Trading/ExecutionJobModels.cs`

### 📦 `ExecutionJob`

> Un piano di esecuzione live di un'apertura di posizione, distribuita in fette nel tempo reale (TWAP/VWAP/Iceberg) su Testnet/Live (rif. docs/archive/ROADMAP-QLIB.md §1.2 ). Una riga per corsia, persistita così che un piano sopravviva a un riavvio del processo e sia ispezionabile in UI. Le fette vivono in (blob, stesso pattern di PipelineRun.StageSummariesJson ): poche fette per job, pochi job attivi per corsia — nessun vantaggio relazionale a questo volume. INVARIANTE: solo le APERTURE guidate da segnale diventano un ExecutionJob; ogni chiusura resta sempre immediata. Un job viene creato SOLO dopo che la prima fetta ha effettivamente creato la posizione, così è sempre valido per i job Running.

| | Firma | Descrizione |
|---|---|---|
| `p` | `Guid Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default). |
| `p` | `string StrategyId` | — |
| `p` | `string PositionId` | PositionId della posizione aperta/accresciuta da questo piano (chiave di correlazione). |
| `p` | `string Symbol` | — |
| `p` | `MarketType MarketType` | — |
| `p` | `OrderSide Side` | — |
| `p` | `decimal TotalQuantity` | Quantità totale prevista dal piano. |
| `p` | `decimal FilledQuantity` | Quantità effettivamente riempita finora (somma dei fill delle fette). |
| `p` | `decimal EntryPriceWeightedAvg` | Prezzo medio ponderato di ingresso della posizione dopo i fill accumulati. |
| `p` | `string Algorithm` | "Twap" \| "Vwap" \| "Iceberg" (mai "Immediate": quello non genera un job). |
| `p` | `int WindowSeconds` | Ampiezza della finestra di esecuzione, in secondi. |
| `p` | `string Status` | "Running" \| "Completed" \| "Cancelled" \| "Failed". |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `DateTime? CompletedAtUtc` | — |
| `p` | `string? FailureReason` | — |
| `p` | `decimal ArrivalPrice` | Prezzo di arrivo/decisione (t0) del piano, per il calcolo dell'implementation shortfall alla chiusura del job. NON persistito (solo osservabilità, in-memory per la durata del job): un job che sopravvive a un riavvio lo … |
| `p` | `string SlicesJson` | JSON: List&lt;ExecutionJobSlice&gt; (le fette del piano con il loro stato). |

### 📦 `ExecutionJobSlice`

> Una fetta del piano: quantità da eseguire a un dato offset dalla creazione del job.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int OffsetSeconds` | Secondi dopo in cui la fetta è dovuta. |
| `p` | `decimal Quantity` | — |
| `p` | `string Status` | "Pending" \| "Filled" \| "MergedIntoNext" (dust assorbita) \| "Abandoned". |
| `p` | `string? ClientOrderId` | — |
| `p` | `decimal? FilledPrice` | — |
| `p` | `decimal? FilledQty` | — |

### 📦 `ExecutionJobSlices`

> Serializzazione delle fette dentro .

| | Firma | Descrizione |
|---|---|---|
| `m` | `string Serialize(IReadOnlyList&lt;ExecutionJobSlice&gt; slices)` | — |
| `m` | `List&lt;ExecutionJobSlice&gt; Deserialize(string? json)` | — |

## `ProcioneMGR/Services/Trading/ExecutionWorker.cs`

### 📦 `ExecutionWorker` `(`

> Avanza nel tempo reale le fette dei piani di esecuzione (TWAP/VWAP/Iceberg) di UNA corsia: ad ogni tick chiede al motore di piazzare le fette dovute. Uno per corsia (registrato nel loop per-corsia di Program.cs, stesso pattern di ). Rif. ROADMAP-QLIB §1.2. Default safe-off: se è false il tick è un no-op. Lo switch è riletto AD OGNI tick (IOptionsMonitor, hot-reload) — dev'essere spegnibile senza restart.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Trading/ITradingEngine.cs`

### 🔌 `ITradingEngine`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | Corsia di trading isolata a cui appartiene questa istanza (0 = corsia di default). |
| `m` | `Task&lt;TradingEngineStatus&gt; GetStatusAsync(CancellationToken ct = default)` | — |
| `m` | `Task StartAsync(TradingMode mode, CancellationToken ct = default)` | — |
| `m` | `Task StopAsync(CancellationToken ct = default)` | — |
| `m` | `Task EmergencyStopAsync(string reason, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;OpenPosition&gt;&gt; GetOpenPositionsAsync(CancellationToken ct = default)` | — |
| `m` | `Task ClosePositionAsync(string positionId, CancellationToken ct = default)` | Chiusura manuale di una posizione (market, al prezzo corrente). |
| `m` | `Task CloseAllPositionsAsync(string reason, CancellationToken ct = default)` | [M2] Chiude tutte le posizioni della corsia al miglior prezzo noto SENZA attivare l'emergency stop: flatten ordinato usato dalla promozione/retrocessione di corsia (LanePromoter) prima del cambio modalità. Best-effort: … |
| `m` | `Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct …` | Imposta/aggiorna stop loss, take profit e trailing stop (%) di una posizione aperta. Una modifica manuale ha sempre priorità: da qui in poi l'automatismo di apertura non ritocca più questi valori (si applica solo alla c… |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetPendingOrdersAsync(CancellationToken ct = default)` | Ordini Live in attesa di conferma manuale. |
| `m` | `Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)` | Conferma un ordine Live in coda: passa la safety e viene piazzato realmente. |
| `m` | `Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)` | Rifiuta un ordine Live in coda (non verrà piazzato). |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;TradingPerformance&gt; GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default)` | Elabora una candela (nuova o di replay storico): aggiorna posizioni/PnL, valuta i segnali delle strategie dell'ensemble, piazza/chiude ordini (con safety check) e aggiorna l'equity. Usata dal TradingWorker (live) e dal … |
| `m` | `Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default)` | [R1] Elabora un tick di prezzo real-time proveniente dal feed WebSocket: valuta SOLO le uscite protettive (liquidazione, stop loss, take profit, trailing) sulle posizioni aperte. Non valuta segnali e non apre MAI posizi… |
| `m` | `Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default)` | Avanza le fette dovute dei piani di esecuzione live (TWAP/VWAP/Iceberg) di questa corsia. Chiamato periodicamente dall'ExecutionWorker; no-op in Paper o se l'esecuzione a fette è disabilitata. Rif. docs/archive/ROADMAP-… |

## `ProcioneMGR/Services/Trading/Internal/AutoStopApplier.cs`

### 📦 `AutoStopApplier`

> Applica automaticamente stop-loss/take-profit/trailing validati nel backtest alla posizione appena aperta — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento: pura funzione di calcolo, nessuna dipendenza da I/O. Gira SOLO alla creazione della posizione: nessun altro punto del motore rimette mano a questi valori, quindi una modifica manuale successiva da /trading resta sempre l'ultima parola.

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Apply(OpenPosition pos, Order order, IReadOnlyList&lt;EnsembleStrategy&gt; active)` | — |

## `ProcioneMGR/Services/Trading/Internal/BracketOrderManager.cs`

### 📦 `BracketOrderManager` `(`

> Piazzamento/cancellazione degli ordini trigger resting (stop-loss/take-profit lato exchange) sui Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento: stesse chiamate, stesso ordine, stessa gestione degli errori (mai bloccante — ogni fallimento resta solo loggato, gli stop software restano la fonte di verità). Riceve / come delegati verso i metodi privati di persistenza di invece di duplicarli: stessa identica scrittura, testabile in isolamento passando dei fake.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task TryPlaceRestingBracketAsync(OpenPosition pos, TradingCredentials creds, string exchangeName, DateTime ts, CancellationToken ct)` | Piazza gli ordini trigger STOP_MARKET/TAKE_PROFIT_MARKET sul lato opposto della posizione. Invocato solo se è attivo (default OFF). |
| `m` | `Task TryCancelRestingBracketAsync(OpenPosition pos, TradingCredentials creds, string exchangeName, CancellationToken ct)` | [P0-5] Cancella gli ordini TRIGGER resting prima di chiudere a mercato, così non restano ordini orfani sull'exchange. INERTE se non ci sono id (feature off, default). |

## `ProcioneMGR/Services/Trading/Internal/ExecutionQuality.cs`

### 📦 `ExecutionQuality`

> [Fase 1 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Qualità di esecuzione degli ordini di corsia. Fino a questa fase la piattaforma misurava l'implementation shortfall solo sugli ordini eseguiti a fette (TWAP/VWAP/Iceberg), dove ExecutionJob.ArrivalPrice era già fissato a t0. Gli ordini di corsia normali — cioè la stragrande maggioranza — catturavano il prezzo di fill ma lo usavano soltanto come guardia ( ): mai come misura di costo. Il risultato era che il costo assunto in selezione ( PipelineCosts.DefaultSlippagePercent ) non aveva alcun riscontro con quello pagato davvero. Qui vivono le due primitive che chiudono quel cerchio, tenute deliberatamente stupide e senza dipendenze così da essere testabili in isolamento e riusabili da entrambi i percorsi (apertura e chiusura, Spot e Futures).

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal? ShortfallBps(OrderSide side, decimal? arrivalPrice, decimal? fillPrice)` | Implementation shortfall in punti base fra il prezzo di arrivo (riferimento alla decisione) e il prezzo eseguito, segnato come costo : positivo = abbiamo eseguito peggio del riferimento (comprato più caro o venduto più … |
| `m` | `Task&lt;(T Result, int ElapsedMs)&gt; MeasureAsync&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt; call)` | Esegue la chiamata all'exchange misurandone la durata in millisecondi. La misura parte prima della chiamata e include quindi anche l'attesa imposta dal rate-limiter client-side ( ). È voluto: il ritardo che conta per un… |

## `ProcioneMGR/Services/Trading/Internal/ExecutionSlicePlanner.cs`

### 📦 `ExecutionSlicePlanner` `(`

> Decide fra apertura IMMEDIATA ed esecuzione a fette (TWAP/VWAP/Iceberg) — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5, rif. ROADMAP-QLIB §1.2). Estratto da senza alcun cambio di comportamento: stesso pre-check aggregato sulla quantità PIENA (altrimenti MaxPositionSizePercent sarebbe bypassabile fetta per fetta), stesso calcolo di finestra/numero di fette, stessa prima fetta immediata seguita dal piano per le successive. Riceve / / come delegati verso i metodi di che restano lì (orchestrazione a livello di engine, fuori da questa cascata di apertura/chiusura).

## `ProcioneMGR/Services/Trading/Internal/FillSanityCheck.cs`

### 📦 `FillSanityCheck`

> Sanity check sul fill riportato dall'exchange — bug B1 (docs/TEST-UI-2026-07-18.md): il testnet ha risposto "Filled" con quantità cumulative (100x+) e prezzo 0, e il motore le ha adottate così com'erano corrompendo capitale e PnL (-1,8M su 10k). Il SafetyChecker guarda l'ordine PRIMA dell'invio: questo è il gemello sul RITORNO — il fill è verificato contro la quantità RICHIESTA (tolleranza ) e contro il prezzo corrente di mercato (banda ). Fuori banda ⇒ il fill è sospetto e NON va MAI adottato: l'apertura viene rifiutata come esito incerto (verifica manuale), la chiusura si finalizza al prezzo di riferimento locale (rifiutarla riaprirebbe il loop di oversell del bug H2). Solo i valori RIPORTATI vengono verificati: null (exchange che non riporta il dettaglio del fill) mantiene il fallback locale di sempre. Paper non passa mai di qui (nessuna chiamata exchange): comportamento invariato.

## `ProcioneMGR/Services/Trading/Internal/FuturesPositionReconciler.cs`

### 📦 `FuturesPositionReconciler` `(`

> Ogni candela (solo Futures, Testnet/Live), verifica sull'exchange che le posizioni locali siano ancora aperte — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento. L'exchange può liquidare/ chiudere una posizione indipendentemente dal ciclo del motore: se risulta flat lato exchange ma aperta localmente, la chiudiamo qui con il miglior prezzo noto. Difesa inversa: una posizione aperta sull'exchange ma sconosciuta al motore NON viene mai chiusa d'ufficio, solo allertata una volta finché la condizione persiste. untrackedRemoteAlerted è passato/restituito per valore (non ref : non consentito in un metodo async ) — il chiamante riassegna il campo dell'engine con il valore restituito.

## `ProcioneMGR/Services/Trading/Internal/OrderReconciler.cs`

### 🔢 `ReconcileStatus` `{ Filled, NotFound, TerminalUnfilled, Uncertain }`

### 🧾 `ReconcileOutcome` `(ReconcileStatus Status, decimal? FillPrice, decimal? FillQty, string? ExchangeOrderId);`

### 📦 `OrderReconciler` `(IExchangeClientFactory exchangeFactory)`

> Riconcilia un ordine MARKET dall'esito di rete incerto — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO- ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento. Interroga lo STATO per clientOrderId (fino a 3 tentativi, pausa 2s): GetOpenOrders non basta, un MARKET riempito durante il blip non è tra gli ordini "aperti" e verrebbe scambiato per "mai piazzato" — posizione reale non tracciata E ordine duplicato alla candela successiva. Se l'ordine risulta ancora vivo viene CANCELLATO e ricontrollato, così non può riempirsi "alle nostre spalle" dopo che lo abbiamo dichiarato assente. [B1] I valori di fill nell' sono riportati COSÌ COME arrivano dall'exchange e quindi NON fidati: chi li adotta (PositionOpener/PositionCloser) DEVE prima passarli da — un testnet può rispondere "Filled" con quantità cumulative o prezzo 0 (docs/TEST-UI-2026-07-18.md).

## `ProcioneMGR/Services/Trading/Internal/PositionCloser.cs`

### 📦 `PositionCloser` `(`

> Chiusura di posizioni Spot e Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento: stesse chiamate exchange, stessa gestione della riconciliazione di rete incerta, stesso calcolo di PnL/ capitale disponibile. Riceve e come riferimenti diretti (non copie): le mutazioni (AvailableCapital, RealizedPnl, DailyPnl, rimozione da positions) sono visibili a esattamente come quando il codice viveva inline. NON tocca , di proposito: quel timestamp alimenta SOLO l'anti-spam n.6 del , che gira esclusivamente sul percorso di APERTURA. Segnarlo anche in chiusura faceva pagare alla successiva apertura il throttle di una chiusura appena avvenuta, e su un'inversione di segnale (chiudi long → apri short sulla stessa candela) l'apertura opposta veniva rifiutata con elapsed = 0. Vedi docs/REPORT-RICERCA-2026-07.md.

## `ProcioneMGR/Services/Trading/Internal/PositionOpener.cs`

### 📦 `PositionOpener` `(`

> Apertura di posizioni Spot e Futures — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento: stessa gestione della riconciliazione di rete incerta, stesso calcolo margine/liquidazione sui Futures, stessa fusione via media ponderata quando mergeInto non è null (fetta 2..K di un ExecutionJob). Riceve / / come riferimenti diretti: le mutazioni sono visibili a esattamente come quando il codice viveva inline.

## `ProcioneMGR/Services/Trading/Internal/ProtectiveExitEvaluator.cs`

### 🔢 `ProtectiveExitKind`

> Tipo di uscita protettiva scattata su una posizione.

### ▫️ `ProtectiveExit` `(ProtectiveExitKind Kind, decimal FillPrice)`

> Esito della valutazione. alimenta ClosePositionAsync e finisce in : le stringhe devono restare "Liquidation"/"StopLoss"/ "TakeProfit" perché PositionCloser deduce dal prefisso "Liquidation".

| | Firma | Descrizione |
|---|---|---|
| `p` | `ProtectiveExit None` | — |
| `p` | `bool ShouldClose` | — |
| `p` | `string Reason` | — |

### 📦 `ProtectiveExitEvaluator`

> Valutazione PURA (nessun I/O, nessuna mutazione di stato) delle uscite protettive di una posizione: liquidazione, stop loss, take profit e stop dinamico da trailing. Estratta da TradingEngine.ProcessCandleAsync senza cambio di comportamento perché ora ha DUE chiamanti che devono decidere in modo identico: - il percorso a candela chiusa, che passa l'OHLC reale della barra; - il percorso a tick real-time ( TradingEngine.ProcessPriceTickAsync ), che passa una barra degenere open = high = low = close = prezzo corrente . Sul tick la degenerazione produce spontaneamente la semantica giusta: il fill calcolato come "esito peggiore fra livello e apertura" collassa sul prezzo corrente di mercato, che è appunto il prezzo realistico di esecuzione in quell'istante. Nessun ramo speciale per il real-time.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal? EffectiveStop(OpenPosition pos)` | Livello di stop EFFETTIVO: lo stop fisso, sostituito dal livello di trailing quando questo è più favorevole. Il trailing è CAUSALE — si calcola sul best-since-entry accumulato dalle barre PRECEDENTI, mai includendo quel… |
| `m` | `ProtectiveExit EvaluateLiquidation(OpenPosition pos, decimal high, decimal low, bool isFutures)` | Liquidazione (solo Futures, e solo con un prezzo di liquidazione noto e positivo). Valutata PRIMA di tutto il resto: se il mercato ha toccato il livello, la posizione non esiste più sull'exchange e ogni altra valutazion… |
| `m` | `ProtectiveExit EvaluateStopAndTarget(OpenPosition pos, decimal open, decimal high, decimal low)` | Stop e target sulla barra. Lo STOP ha la precedenza: se entrambi i livelli cadono nella stessa barra non si può sapere quale sia stato toccato per primo, e si assume l'esito peggiore. Il fill è al LIVELLO, oppure all'ap… |
| `m` | `void UpdateBestSinceEntry(OpenPosition pos, decimal high, decimal low)` | Aggiorna il best-since-entry usato dal trailing. Va chiamato SOLO se nessuna uscita è scattata su questa barra: includere la barra che ha già chiuso la posizione sposterebbe il livello a posteriori, rompendo la causalit… |

## `ProcioneMGR/Services/Trading/Internal/SignalOrderBuilder.cs`

### 📦 `SignalOrderBuilder` `(`

> Trasforma un segnale di strategia in un ordine dimensionato — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratto da senza alcun cambio di comportamento: stesso guard anti-short-su-spot-reale, stesso dimensionamento (margine isolato sui Futures, nozionale pieno sullo Spot), stesso arrotondamento al LOT_SIZE reale (Testnet/Live) o a precisione fissa (Paper), stessa coda di conferma manuale in Live. resta un delegato verso : decide immediata vs. a fette, fuori da questo collaboratore.

## `ProcioneMGR/Services/Trading/Internal/TradingPersistence.cs`

### 📦 `TradingPersistence` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory, int laneId)`

> Le operazioni di persistenza DB usate lungo tutta la cascata privata di — Intervento B, Fase 1 (PRD-CONSOLIDAMENTO-ARCHITETTURA.md §4.5). Estratte senza alcun cambio di comportamento: stesse query, stesso ordine, stesse colonne aggiornate. Ogni collaboratore estratto dalla stessa cascata ( , gli esecutori Spot/Futures, il chiusore posizioni) riceve un'istanza di questa classe invece di ripetere dbFactory/laneId nel proprio costruttore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;List&lt;OhlcvData&gt;&gt; GetRecentCandlesAsync(string symbol, string timeframe, int take, CancellationToken ct)` | Ultime candele (ordine cronologico crescente) per costruire il profilo di esecuzione a fette. |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetPendingOrdersAsync(CancellationToken ct)` | — |
| `m` | `Task SaveOrderAsync(Order order, bool isExisting, CancellationToken ct)` | — |
| `m` | `Task PersistOrderAsync(Order order, CancellationToken ct)` | — |
| `m` | `Task PersistNewPositionAsync(OpenPosition pos, CancellationToken ct)` | — |
| `m` | `Task UpdatePositionRowAsync(OpenPosition pos, CancellationToken ct)` | Aggiorna la riga di una posizione ESISTENTE dopo un fill fuso (media ponderata di una fetta). |
| `m` | `Task RemovePositionAsync(OpenPosition pos, CancellationToken ct)` | — |
| `m` | `Task PersistTradeAsync(TradeRecord trade, CancellationToken ct)` | — |
| `m` | `Task PersistExecutionJobAsync(ExecutionJob job, CancellationToken ct)` | — |
| `m` | `Task AuditAsync(string action, object details, TradingMode mode, DateTime ts, CancellationToken ct)` | — |

## `ProcioneMGR/Services/Trading/Internal/VolatilityScaler.cs`

### 📦 `VolatilityScaler`

> Dosaggio della posizione sulla volatilità realizzata: quando il mercato si agita si espone meno capitale, quando si calma se ne espone di più. Onestà sull'evidenza. Nasce da un risultato che poi NON ha replicato: su un paniere di 24 monete portava lo Sharpe da 0,12 a 0,43 (a parità di esposizione media), ma su un insieme diverso di 12 simboli lo peggiora (0,57 contro 0,79) e su singolo simbolo batte l'esposizione costante equivalente solo in 2 casi su 12. Vedi docs/REPORT-DOSAGGIO-VOLATILITA.md . Non è quindi una fonte di rendimento corretto per il rischio. Ciò che fa in modo affidabile, misurato in ogni prova, è ridurre l'esposizione media e con essa l'ampiezza delle oscillazioni: è una manopola di controllo del drawdown, e va tenuta per quello. Proprietà di sicurezza. vale 1,0 di default, quindi il moltiplicatore può solo RIDURRE la dimensione decisa da , mai aumentarla. Ne segue che …

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal Compute(IReadOnlyList&lt;decimal&gt; closes, string timeframe, SafetyConfiguration cfg)` | Moltiplicatore da applicare alla dimensione della posizione. Ritorna sempre 1 (comportamento invariato) se la funzione è spenta, se i dati non bastano o se la volatilità stimata è nulla. Chiusure in ordine cronologico: … |
| `m` | `double RealizedAnnualVolatility(IReadOnlyList&lt;decimal&gt; closes, int lookback, string timeframe)` | Volatilità annualizzata dei rendimenti semplici sulle ultime barre. Rendimenti semplici e non logaritmici per restare identici alla misura della ricerca. |

## `ProcioneMGR/Services/Trading/LaneCountCoherenceProbe.cs`

### 🧾 `LaneCountCoherenceResult` `(`

> Esito del confronto fra il numero di corsie del guscio e quello del motore remoto. Le corsie di QUESTO processo ( ). Le corsie effettive del motore, come dichiarate da lui. null = non determinabile (motore irraggiungibile o valore illeggibile): non è un disallineamento, è ignoranza — e le due cose vanno dette in modo diverso. Come si è arrivati al verdetto, per il log e per un eventuale banner.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Mismatch` | Vero solo quando ENTRAMBI i numeri sono noti e diversi. |

### 📦 `LaneCountCoherenceProbe` `(`

> [AF0] Il numero di corsie è TOPOLOGIA duplicata per necessità in due processi: il guscio lo legge dal proprio Trading:LaneCount , il motore in-cluster dal suo (ConfigMap trading-config.env ), e ognuno lo congela alla prima lettura. Un disallineamento produce corsie che il guscio vede e il motore non ha (comandi rifiutati dal validatore gRPC) o, peggio, corsie che il motore fa girare e il guscio non mostra — motori senza occhi. Fino a oggi il sintomo esisteva ma era TARDIVO: lo si scopriva dal primo comando fallito su una corsia alta, molto lontano dalla causa. Questa sonda lo dice all'avvio, con la stessa voce del : LogCritical + notifica, una volta. Vive SOLO nel monolite in modalità remota (registrata nel ramo useRemote di AddTradingLanes ): col motore in-process i due numeri escono per costruzione dallo stesso file, e non c'è nulla da sorvegliare.

| | Firma | Descrizione |
|---|---|---|
| `p` | `LaneCountCoherenceResult? Result` | Ultimo esito, per un eventuale banner diagnostico (stesso patto di MasterKeyProbe.Result). |
| `m` | `Task&lt;LaneCountCoherenceResult&gt; ProbeAsync(CancellationToken ct = default)` | Legge il valore effettivo dal motore e lo confronta col proprio. Non lancia per irraggiungibilità: quella ha già la sua superficie (ogni pannello che parla col motore la dichiara), e la sonda non deve trasformarla in un… |

### 📦 `LaneCountCoherenceProbeWorker` `(`

> Esegue la sonda all'avvio, con gli stessi tempi del : attesa iniziale (il port-forward o il pod possono non essere ancora su), poi qualche tentativo distanziato. Ritenta anche sull'irraggiungibilità — che a freddo è lo stato normale — e si arrende senza allarme se il motore resta muto: il silenzio del motore ha già i suoi allarmi.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Trading/LaneDirectory.cs`

### 🧾 `LaneSummary` `(int Id, string Symbol, string Timeframe, string Mode, bool IsRunning)`

> Riassunto di una corsia: quel tanto che basta per sceglierla senza doverla aprire. Vuoto se la corsia non è mai stata configurata.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsConfigured` | — |

### 🔌 `ILaneDirectory`

> Elenca le corsie con il loro stato corrente.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;LaneSummary&gt;&gt; ListAsync(CancellationToken ct = default)` | — |

### 📦 `LaneDirectory` `(IDbContextFactory&lt;ApplicationDbContext&gt; dbFactory) : ILaneDirectory`

> Legge in un colpo solo ciò che serve al selettore di corsia: simbolo e timeframe dalla configurazione dell'ensemble, modalità e stato di esecuzione dal motore. Vive come servizio e non dentro le pagine perché lo usano Trading ed Ensemble allo stesso modo, e perché con corsie configurabili la domanda "quali corsie ci sono e cosa ci gira" smette di avere una risposta ovvia: prima erano tre e si conoscevano a memoria. Due letture per tutte le corsie, non due per corsia: con dodici corsie la differenza fra una query e ventiquattro si vede, e questo elenco si ridisegna a ogni refresh della pagina.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;LaneSummary&gt;&gt; ListAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Trading/LaneEpisodes.cs`

### 🔢 `LaneEpisodeSource`

> Da dove viene ciò che si sa di un episodio. La distinzione non è pedanteria: gli episodi PRIMA del 2026-08-06 hanno una voce di `StartEngine` che non registrava simbolo né strategie, quindi il simbolo si ricava dagli ordini stessi e le strategie non si sanno. Dirlo è la differenza fra un'informazione e una ricostruzione.

### 🧾 `LaneEpisode` `(`

> Un tratto di vita di una corsia: da un avvio del motore al successivo. È l'unità che mancava — senza, lo storico di una corsia è un mucchio indistinto di esperimenti diversi.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsCurrent` | L'episodio in corso: nessun avvio successivo lo ha chiuso. |
| `p` | `string Title` | Etichetta breve per la UI: «Composite DOT/USDT 15m» oppure «DOT/USDT (strategia non registrata)». |

### 📦 `LaneEpisodeBuilder`

> [2026-08-06] Ricostruisce gli episodi di una corsia dagli avvii del motore. Perché non serviva una tabella nuova : i confini erano già a database. Ogni `StartEngine` nel registro di audit apre un tratto, il successivo lo chiude — la corsia 0 ne aveva 11, mai usati. Mancava solo COSA girasse in ciascuno, ed è un buco di tre campi nel payload, colmato dal 2026-08-06 in poi. Il compromesso onesto sul passato : per gli episodi vecchi il payload non ha simbolo né strategie. Il simbolo si ricava dagli ordini caduti dentro l'intervallo — è una deduzione solida (un episodio opera su un simbolo solo) ma resta una deduzione, e la dichiara. Le strategie di allora non sono recuperabili: si dice «non registrata», non si inventa. Puro e senza I/O: riceve le voci di avvio e gli ordini già letti.

### 📦 `StartPayload`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string? Mode` | — |
| `p` | `string? Symbol` | — |
| `p` | `string? Timeframe` | — |
| `p` | `List&lt;string&gt;? StrategyNames` | — |
| `p` | `List&lt;int&gt;? SavedStrategyIds` | Gli id delle strategie SALVATE, il ponte verso /strategies. Vuoto sugli episodi vecchi. |

## `ProcioneMGR/Services/Trading/LaneExecutionLease.cs`

### 🔌 `ILaneLease` `: IAsyncDisposable`

> [B0 PRD core-caldo] Lease di ESECUZIONE per corsia: advisory lock Postgres a livello di sessione. "Mai due esecutori sulla stessa corsia" era retto dalla registrazione condizionale (vedi ) più la disciplina di deploy — il Deployment remoto non deve mai essere vivo col toggle a false. Il lease trasforma quel patto in invariante applicata dal DATABASE, che è l'unica cosa che i due processi condividono per certo: chi non ottiene il lock non alimenta il motore e lo dice con LogCritical, quindi un deploy incoerente fallisce a voce alta invece di aprire ordini due volte.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | — |
| `m` | `ValueTask&lt;bool&gt; IsAliveAsync(CancellationToken ct = default)` | Verifica che la sessione che detiene il lock sia ancora viva (SELECT 1). Se la connessione è caduta, Postgres ha già liberato il lock lato server: il chiamante DEVE smettere di alimentare il motore e riacquisire. |

### 🔌 `ILaneLeaseFactory`

> Factory del lease. Null da = lock detenuto da un altro processo.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;ILaneLease?&gt; TryAcquireAsync(int laneId, CancellationToken ct = default)` | — |

### 📦 `NpgsqlLaneLeaseFactory` `(`

| | Firma | Descrizione |
|---|---|---|
| `k` | `int LeaseClassId` | Spazio dei lock di questa applicazione ('PROC' in ASCII): separa i nostri advisory lock da chiunque altro usi lo stesso database. |
| `m` | `Task&lt;ILaneLease?&gt; TryAcquireAsync(int laneId, CancellationToken ct = default)` | — |

### 📦 `NpgsqlLaneLease` `(int laneId, NpgsqlConnection connection, ILogger logger) : ILaneLease`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | — |
| `m` | `ValueTask&lt;bool&gt; IsAliveAsync(CancellationToken ct = default)` | — |
| `m` | `ValueTask DisposeAsync()` | — |

## `ProcioneMGR/Services/Trading/LaneInvariantChecker.cs`

### 📦 `LaneInvariantChecker`

> Valutazione PURA degli invarianti contabili di una corsia (Fase 0-A3): stato + posizioni → elenco di violazioni leggibili. Separata dal per essere testabile senza database né motore (stesso criterio di FillSanityCheck per il bug B1).

## `ProcioneMGR/Services/Trading/LaneInvariantOptions.cs`

### 📦 `LaneInvariantOptions`

> Soglie del watchdog di invarianti contabili per corsia (Fase 0-A3, PRD Autonomia Operativa), sezione Trading:LaneInvariants . Le soglie sono LASCHE apposta: il watchdog non duplica il pre-ordine (che resta il freno fine), è un tripwire per stati contabili ASSURDI che nessun percorso legittimo può produrre — come il caso reale della corsia 2 (PnL -1,8M su capitale 10k con leva 2). Hot-reload via IOptionsMonitor.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Default ON: è un freno di sicurezza, spegnerlo è la scelta che va motivata. |
| `p` | `int CheckIntervalSeconds` | Cadenza del check (letta all'avvio del worker; cambiarla richiede riavvio, come PromotionWorker). |
| `p` | `decimal AvailableCapitalTolerance` | ε in valuta: AvailableCapital sotto -ε è una violazione (mai negativo oltre l'arrotondamento). |
| `p` | `decimal MaxAbsPnlCapitalMultiple` | k: \|PnL totale (realizzato + non realizzato)\| oltre k × TotalCapital × Leverage è una violazione. |
| `p` | `decimal MaxExposureCapitalMultiple` | Nozionale aperto complessivo oltre questo multiplo di TotalCapital × Leverage è una violazione. |

## `ProcioneMGR/Services/Trading/LaneInvariantWatchdog.cs`

### 📦 `LaneInvariantWatchdog` `(`

> Watchdog degli invarianti contabili per corsia (Fase 0-A3, PRD Autonomia Operativa §3). Motivazione empirica: nella sessione di esercizio 2026-07-18 la corsia 2 Testnet è rimasta a PnL -1.817.925 su capitale 10.000 per ORE senza che nessun automatismo se ne accorgesse — il fill sanity check (A1) impedisce che si ripeta per QUELLA via, questo watchdog è la rete di sicurezza per qualunque via futura verso uno stato contabile assurdo. Politica su violazione: QUARANTENA — stop del trading, riga persistita che blocca il riavvio (vedi ), audit + LogCritical. NESSUNA chiusura forzata delle posizioni: stessa filosofia della "difesa inversa" del FuturesPositionReconciler — su uno stato che non capiamo, l'azione automatica peggiore è proprio quella irreversibile. Registrato SOLO accanto al motore locale (vedi TradingServiceCollectionExtensions): in modalità remota il watchdog vive nel servizio di…

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task TickAsync(CancellationToken ct)` | Un tick: controlla tutte le corsie in esecuzione. Pubblico per test. |

## `ProcioneMGR/Services/Trading/LanePromoter.cs`

### 🔌 `ILanePromoter`

> Applies a lane mode change (Paper↔Testnet) as a stop→restart of the lane's keyed trading engine, and records a user-visible audit entry. This is the "action" half of the promotion feature (decisions live in ). SAFETY (defense in depth): this method THROWS if asked to switch a lane to — no automated path may ever put a lane into Live. Switching to Testnet uses the already-configured Testnet credentials; if they are missing the engine's StartAsync throws a clear error (not silent), the lane is left stopped, and the failure is logged.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)` | — |

### 📦 `LanePromoter` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task PromoteLaneAsync(int laneId, TradingMode newMode, string reason, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Trading/LaneQuarantineStore.cs`

### 🔌 `ILaneQuarantineStore`

> Accesso alla quarantena corsie (Fase 0-A3): usato dal (scrittura), dalla pagina /trading (lettura + rimozione Admin) e indirettamente da TradingEngine.StartAsync (che però legge la tabella direttamente via dbFactory, senza dipendenza in più). L'audit (LaneQuarantined / LaneQuarantineCleared) vive QUI, così nessun chiamante può quarantenare/liberare una corsia senza lasciare traccia.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;LaneQuarantine?&gt; GetAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;LaneQuarantine&gt;&gt; GetAllAsync(CancellationToken ct = default)` | Tutte le quarantene attive (per il riallineamento di flotta e la UI multi-corsia). |
| `m` | `Task&lt;bool&gt; TryQuarantineAsync(int laneId, string reason, string detailsJson, CancellationToken ct = default)` | Mette la corsia in quarantena se non lo è già. false se una quarantena era già attiva (la prima vince: la riga esistente conserva l'evidenza originale). |
| `m` | `Task&lt;bool&gt; ClearAsync(int laneId, string? userId, CancellationToken ct = default)` | Rimuove la quarantena (azione umana, /trading solo Admin). false se non c'era. |

### 📦 `LaneQuarantineStore` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;LaneQuarantine?&gt; GetAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;LaneQuarantine&gt;&gt; GetAllAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;bool&gt; TryQuarantineAsync(int laneId, string reason, string detailsJson, CancellationToken ct = default)` | — |
| `m` | `Task&lt;bool&gt; ClearAsync(int laneId, string? userId, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Trading/LaneSafetyMonitor.cs`

### 🔌 `ILaneRiskProfileSink`

> Riceve il profilo di rischio della corsia. Separato dal monitor per non costringere chi lo imposta (il all'avvio) a conoscere l'implementazione concreta.

| | Firma | Descrizione |
|---|---|---|
| `m` | `void SetProfile(RiskProfile? profile)` | Imposta (o azzera, con null ) il profilo attivo per la corsia. Idempotente. |
| `p` | `RiskProfile? Profile` | Profilo attualmente attivo, o null se la corsia usa le soglie globali. |

### 📦 `LaneSafetyMonitor` `(IOptionsMonitor&lt;SafetyConfiguration&gt; global)`

> [R3] Soglie di sicurezza EFFETTIVE di una corsia: profilo della corsia sovrapposto alla configurazione globale. Prima di R3 era globale — un'unica sezione di appsettings.json condivisa da tutte le corsie — quindi un "profilo di rischio" non poteva essere per-corsia, e due corsie non potevano avere appetiti al rischio diversi. PERCHÉ IMPLEMENTA invece di introdurre un'astrazione nuova: le soglie sono lette in ~19 punti fra TradingEngine , SafetyChecker , PositionOpener , PositionCloser , SignalOrderBuilder ed ExecutionSlicePlanner , tutti già scritti contro questa interfaccia. Rispettandola, il profilo per-corsia entra in vigore ovunque senza toccare un solo punto di lettura — e senza il rischio, in un cambiamento a tappeto, di dimenticarne uno proprio sul percorso dei soldi. L'hot-reload resta intatto: ricompone a ogni accesso partendo dal valore corrente del monitor globale, quindi una…

| | Firma | Descrizione |
|---|---|---|
| `p` | `RiskProfile? Profile` | — |
| `m` | `void SetProfile(RiskProfile? profile)` | — |
| `p` | `SafetyConfiguration CurrentValue` | — |
| `m` | `SafetyConfiguration Get(string? name)` | — |
| `m` | `IDisposable? OnChange(Action&lt;SafetyConfiguration, string?&gt; listener)` | Inoltra le notifiche del monitor globale. Il cambio di PROFILO non notifica di proposito: avviene solo all'avvio della corsia, quando il motore sta già rileggendo tutto. |

## `ProcioneMGR/Services/Trading/LiveExecutionOptions.cs`

### 📦 `LiveExecutionOptions`

> Opzioni dell'esecuzione live "a fette" (TWAP/VWAP/Iceberg su Testnet/Live). Sezione config Trading:LiveExecution . Letta via (hot-reload): è un interruttore di sicurezza, deve poter essere spento senza riavviare l'app. Default safe-off, come ogni automazione della piattaforma.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Master switch. Default false: nessun piano di esecuzione viene mai creato o avanzato. |
| `p` | `int DefaultWindowMinutes` | Finestra di esecuzione di default (minuti) se la strategia non ne specifica una propria. |
| `p` | `int WorkerTickSeconds` | Cadenza del worker che avanza le fette dovute. |
| `p` | `int AbandonGraceMinutes` | Grazia oltre la finestra prima di dichiarare abbandonate le fette non piazzabili. |

## `ProcioneMGR/Services/Trading/PromotionEvaluator.cs`

### 📦 `PromotionEvaluatorOptions`

> Soglie della promozione/retrocessione automatica delle corsie (sezione di config PromotionEvaluator ).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal MinSharpeRealized` | — |
| `p` | `int MinTradeCount` | — |
| `p` | `decimal MaxDrawdownPercent` | — |
| `p` | `int MinObservationWeeks` | — |
| `p` | `decimal MinWinRate` | — |
| `p` | `bool AutoPromoteToTestnet` | Se true il PromotionWorker promuove davvero (Paper→Testnet); se false valuta soltanto (la UI mostra "pronto"). |
| `p` | `bool NotifyOnPromotion` | Scrive una voce di audit visibile all'utente a ogni promozione/retrocessione. |
| `p` | `decimal HardMaxDrawdownPercent` | Blocco assoluto: una corsia con drawdown oltre questa soglia non viene MAI promossa, anche se il resto è ottimo. |
| `p` | `bool AutoDemoteToPaper` | — |
| `p` | `decimal DemoteSharpeThreshold` | — |
| `p` | `int DemoteMinWeeks` | — |
| `p` | `bool AutoDemoteLiveToTestnet` | Se true, una corsia LIVE degradata viene retrocessa a Testnet (mai a Paper diretto). Default false. |
| `p` | `bool DemoteLiveDryRun` | Finché è true (default), la retrocessione Live si ANNUNCIA soltanto (WouldDemoteLive + reason DRY-RUN), senza agire. |
| `p` | `decimal DemoteLiveSharpeThreshold` | Sharpe realizzato sotto cui la corsia Live è considerata degradata. |
| `p` | `decimal DemoteLiveMaxDrawdownPercent` | Drawdown oltre cui la corsia Live è considerata degradata, a prescindere dallo Sharpe. |
| `p` | `int DemoteLiveMinWeeks` | Storia minima (settimane) prima che il degrado di una Live sia un giudizio e non rumore. |
| `p` | `int DemoteLiveMinTrades` | Trade minimi prima che il degrado di una Live sia un giudizio e non rumore. |
| `p` | `int EvaluationIntervalHours` | Ogni quante ore il PromotionWorker rivaluta le corsie. |

### 📦 `LaneMetrics`

> Metriche realizzate di una corsia, con i flag "criterio soddisfatto?" per la trasparenza in UI.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal RealizedSharpe` | — |
| `p` | `decimal RealizedProfitFactor` | — |
| `p` | `decimal MaxDrawdown` | — |
| `p` | `int TradeCount` | — |
| `p` | `decimal WinRate` | — |
| `p` | `TimeSpan ObservationPeriod` | — |
| `p` | `bool MeetsMinSharpe` | — |
| `p` | `bool MeetsMinTrades` | — |
| `p` | `bool MeetsMaxDrawdown` | — |
| `p` | `bool MeetsMinWeeks` | — |
| `p` | `bool MeetsMinWinRate` | — |

### 📦 `PromotionDecision`

> Decisione di promozione/retrocessione per una corsia. La modalità suggerita non è MAI Live (safety).

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | — |
| `p` | `string Symbol` | — |
| `p` | `TradingMode CurrentMode` | — |
| `p` | `TradingMode SuggestedMode` | — |
| `p` | `bool ShouldPromote` | True se va promossa (Paper→Testnet). |
| `p` | `bool ShouldDemote` | True se va retrocessa (Testnet→Paper) perché l'edge è svanito. |
| `p` | `bool ReadyForTestnet` | True se la corsia è pronta per Testnet ma l'auto-promozione è disattivata (mostra "pronto" in UI). |
| `p` | `bool WouldDemoteLive` | [AF4a] True se una corsia LIVE degradata VERREBBE retrocessa, ma il dry-run è acceso: solo visibilità, mai azione. |
| `p` | `string Reason` | — |
| `p` | `LaneMetrics Metrics` | — |
| `p` | `bool IsRunning` | — |

### 🔌 `IPromotionEvaluator`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;PromotionDecision&gt; EvaluateLaneAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;IReadOnlyList&lt;PromotionDecision&gt;&gt; EvaluateAllLanesAsync(CancellationToken ct = default)` | — |

### 📦 `PromotionEvaluator` `(`

> Decide se una corsia di Paper trading ha performato abbastanza bene, abbastanza a lungo, da essere promossa a Testnet (stesso protocollo di Live ma senza soldi veri), o se una corsia Testnet va retrocessa a Paper perché l'edge è svanito. CONFINE DI SICUREZZA NON NEGOZIABILE: la modalità suggerita non è MAI . Nessuna metrica, per quanto eccellente, promuove automaticamente a Live: Testnet→Live resta sempre una decisione manuale dietro + conferma umana. Le corsie già in Live non vengono nemmeno valutate. La logica di decisione ( ) è pura e deterministica: testabile in isolamento con sintetiche, senza DB né rete.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneCount` | Numero di corsie isolate (allineato a Program.cs LaneCount). |
| `m` | `Task&lt;IReadOnlyList&lt;PromotionDecision&gt;&gt; EvaluateAllLanesAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;PromotionDecision&gt; EvaluateLaneAsync(int laneId, CancellationToken ct = default)` | — |
| `m` | `PromotionDecision Decide(LaneMetrics metrics, TradingMode currentMode, bool isRunning, PromotionEvaluatorOptions opt)` | Cuore deterministico della valutazione. Puro (nessun DB/orologio/rete): a parità di metriche la decisione è sempre identica. SICUREZZA: non è mai ; le corsie in Live non vengono toccate. |

## `ProcioneMGR/Services/Trading/PromotionWorker.cs`

### 📦 `PromotionWorker` `(`

> Rivaluta periodicamente (default ogni 6 ore) le corsie di trading e, se abilitato, promuove automaticamente a Testnet quelle che hanno performato bene abbastanza a lungo — e retrocede a Paper quelle Testnet il cui edge è svanito. La promozione è una decisione importante: cadenza oraria bassa apposta (reagisce in meno di un giorno, non ogni minuto). SAFETY: promuove/retrocede solo tra Paper e Testnet. NON promuove MAI a Live (neanche con metriche eccellenti): Testnet→Live resta manuale dietro SafetyChecker + conferma umana. Le corsie in Live non vengono toccate.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task TickAsync(CancellationToken ct)` | Un tick: valuta tutte le corsie e agisce sulle decisioni. Pubblico per test. |

## `ProcioneMGR/Services/Trading/ProtectiveExitAudit.cs`

### 🧾 `ProtectiveExitAnomaly` `(`

> Una protezione che risulta toccata da barre GIÀ CHIUSE mentre la posizione è ancora aperta. Non è una previsione né una stima: è un confronto fra due fatti registrati. La posizione rimasta aperta. Il simbolo su cui è aperta. "take profit" o "stop loss". Il livello impostato. Il prezzo estremo raggiunto: il minimo per uno short al target, ecc. Quante barre chiuse hanno superato il livello. L'apertura della PRIMA barra che l'ha superato: da lì la posizione doveva essere chiusa.

### 📦 `ProtectiveExitAudit`

> [2026-08-06] Il controllo che mancava. Perché esiste : il proprietario si è accorto a occhio che uno short ETC/USDT sulla corsia 3 aveva raggiunto il take profit senza chiudersi. Il minimo VERO della barra 4h delle 08:00 era 6,31 contro un target di 6,3786 — un fatto scritto a database da ore. Nessun pannello lo diceva: il battito mostrava «ultima candela 16:00 · 0 barre indietro» in verde, e la riga della posizione mostrava un PnL non realizzato positivo, cioè due indicatori che rassicuravano mentre l'uscita non scattava. La causa è stata corretta ( : si alimentano solo barre chiuse). Questo controllo è l'altra metà: una causa corretta non è un controllo , e la prossima ragione per cui un'uscita non scatta sarà diversa da questa. Qui non si indaga il perché — si confronta ciò che è impostato con ciò che è successo, e si dice che non torna. Puro e senza I/O, così la regola è verificabil…

## `ProcioneMGR/Services/Trading/ProtectiveExitDiagnosticsService.cs`

### 📦 `ProtectiveExitDiagnosticsService` `(`

> [B3] Le tre cose che il 2026-07-28 sono state costruite e lasciate senza nessuno che le guardasse: i confronti d'ombra fra tick e candela, le posizioni rimaste su corsie che non esistono più, e la misura del ritardo delle uscite protettive. Il difetto era lo stesso di C4 prima del suo consumo: codice corretto, testato, e **mai chiamato da niente** — verde a livello di classe, inesistente a livello di prodotto. La sentinella scriveva su una tabella che nessuna query leggeva; l'allarme sulle posizioni orfane viveva solo nei log del pod, dove lo vede chi va a cercarlo sapendo già che esiste; l'analizzatore del ritardo era raggiungibile solo da riga di comando. Qui vivono le letture; il pannello in Trading.razor le mostra. Sola lettura su Postgres, nessuna chiamata al motore: si può interrogare anche a core remoto irraggiungibile — che è proprio il momento in cui uno vuole sapere cosa è suc…

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;OpenPosition&gt;&gt; OrphanPositionsAsync(CancellationToken ct = default)` | Posizioni su corsie oltre : nessun motore ne valuta stop, target o trailing, e nessuno le chiuderà mai da solo. Il watchdog le urla nei log una volta per corsia; qui si vedono senza doverli leggere. La query NON filtra … |

## `ProcioneMGR/Services/Trading/ProtectiveExitLagAnalyzer.cs`

### 📦 `ProtectiveExitLagAnalyzer`

> [B3] Misura del RITARDO delle uscite protettive: quanto tempo, e quanto prezzo, separa il momento in cui il mercato tocca il livello di stop dal momento in cui il percorso a candele se ne accorge — cioè la chiusura della barra di corsia. Nasce da un difetto del gate B3. Il gate chiede «confronto tick-vs-candela nelle metriche», ma in assetto osservativo ( DriveProtectiveExits=false ) i tick vengono scartati e procione.trading.protective_exits si incrementa solo quando un'uscita SCATTA: la serie source=tick non può esistere finché non si accende il drive. Il confronto che deve autorizzare l'accensione richiedeva l'accensione. Qui la domanda si chiude OFFLINE, senza toccare l'assetto: le candele a risoluzione fine (5m, o 1m dove esistono) fanno da surrogato dei tick contro le barre di corsia. È un surrogato CONSERVATIVO in tre modi dichiarati: - il momento di scoperta sul percorso fine è …

| | Firma | Descrizione |
|---|---|---|
| `m` | `TimeSpan Step(string timeframe)` | Passo di un timeframe, dalla tabella canonica — non da una seconda copia locale, che potrebbe divergere. Deliberatamente SENZA ripiego su un valore di comodo: un passo sbagliato falserebbe in silenzio proprio la grandez… |
| `k` | `double VerdictFlipThresholdBps` | Soglia oltre la quale il verdetto di B3 si considera ROVESCIATO. Piccola ma non nulla: una mediana appena sopra lo zero è rumore, non un rovesciamento. |
| `m` | `bool IsVerdictFlipped(double medianDelayCostBps, double thresholdBps = VerdictFlipThresholdBps)` | Il verdetto di B3 si è rovesciato su questa serie? Vive qui, e non nella fase CLI che la consuma, per un motivo solo: quella che può essere sbagliata è la CONVENZIONE DI SEGNO, non il confronto. Positivo = il feed avreb… |

### ▫️ `Walk` `(`

> Esito di un cammino: quando l'uscita è stata SCOPERTA, a che prezzo si può uscire davvero.

### 📦 `ProtectiveExitLagRequest`

> Parametri della misura. Il bracket è quello REALE della corsia che si vuole valutare.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string LaneTimeframe` | Timeframe su cui opera la corsia (quello che oggi decide le uscite). |
| `p` | `string FineTimeframe` | Timeframe fine che fa da surrogato dei tick (5m, o 1m dove esiste). |
| `p` | `decimal StopLossPercent` | — |
| `p` | `decimal? TakeProfitPercent` | — |
| `p` | `decimal? TrailingStopPercent` | — |
| `p` | `OrderSide Side` | — |
| `p` | `int MaxHoldBars` | Orizzonte massimo in barre di corsia: oltre, la posizione è dichiarata non uscita. |
| `p` | `int SampleEveryNBars` | Passo di campionamento degli ingressi, per non simulare ogni singola barra su serie lunghe. |

### 📦 `ProtectiveExitLagObservation`

> Una posizione simulata, vista dai due percorsi.

| | Firma | Descrizione |
|---|---|---|
| `p` | `DateTime EntryTimeUtc` | — |
| `p` | `decimal EntryPrice` | — |
| `p` | `bool CandleExited` | — |
| `p` | `bool FineExited` | — |
| `p` | `string CandleReason` | — |
| `p` | `string FineReason` | — |
| `p` | `DateTime? CandleDiscoveredAtUtc` | — |
| `p` | `DateTime? FineDiscoveredAtUtc` | — |
| `p` | `double LeadSeconds` | Secondi di anticipo del percorso fine sulla scoperta dell'uscita. |
| `p` | `double DelayCostBps` | Costo del ritardo in punti base dell'ingresso: quanto si perde uscendo al prezzo ottenibile a barra chiusa invece che al momento del tocco. Positivo = il percorso fine esce meglio. |
| `p` | `double CandleFillOptimismBps` | Scarto fra il fill che il motore REGISTRA sul percorso a candela (il livello) e il prezzo davvero ottenibile alla chiusura di quella barra. Positivo = la contabilità è ottimista. |
| `p` | `bool ReasonsAgree` | — |

### 📦 `ProtectiveExitLagReport`

> Sintesi della misura su una serie.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string LaneTimeframe` | — |
| `p` | `string FineTimeframe` | — |
| `p` | `decimal StopLossPercent` | — |
| `p` | `decimal? TakeProfitPercent` | — |
| `p` | `decimal? TrailingStopPercent` | — |
| `p` | `int PositionsSimulated` | — |
| `p` | `int BothExited` | — |
| `p` | `int OnlyCandleExited` | — |
| `p` | `int OnlyFineExited` | — |
| `p` | `int NeitherExited` | — |
| `p` | `int ReasonDisagreements` | — |
| `p` | `double MedianLeadSeconds` | — |
| `p` | `double P90LeadSeconds` | — |
| `p` | `double MeanLeadSeconds` | — |
| `p` | `double MedianDelayCostBps` | — |
| `p` | `double MeanDelayCostBps` | — |
| `p` | `double P10DelayCostBps` | — |
| `p` | `double P90DelayCostBps` | — |
| `p` | `double AdverseShare` | Quota di posizioni in cui il percorso fine esce PEGGIO: il feed non è gratis per definizione. |
| `p` | `double MedianCandleFillOptimismBps` | — |
| `p` | `double MeanCandleFillOptimismBps` | — |
| `p` | `IReadOnlyList&lt;ProtectiveExitLagByKind&gt; ByKind` | [2026-08-06] Lo STESSO calcolo, separato per tipo di uscita. Nasce da un'obiezione del proprietario, e l'obiezione è fondata: il verdetto del 2026-07-28 («uscire al tocco è peggio, 24 configurazioni su 24») somma stop l… |
| `p` | `IReadOnlyList&lt;ProtectiveExitLagObservation&gt; Observations` | — |

### 📦 `ProtectiveExitLagByKind`

> Il costo del ritardo per UN tipo di uscita. Calcolato solo sulle posizioni in cui i due percorsi escono per la stessa ragione: se il percorso fine esce in take profit e quello a candele in stop loss, la differenza di prezzo non misura il ritardo — misura due eventi diversi, ed è la coppia che conta.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Kind` | "StopLoss", "TakeProfit" o "Liquidation". |
| `p` | `int Count` | Uscite concordi di questo tipo: la base su cui sono calcolati i numeri qui sotto. |
| `p` | `double MedianDelayCostBps` | Negativo = aspettare la chiusura CONVIENE. Positivo = il tocco conviene. |
| `p` | `double MeanDelayCostBps` | — |
| `p` | `double P10DelayCostBps` | — |
| `p` | `double P90DelayCostBps` | — |
| `p` | `double MedianLeadSeconds` | — |
| `p` | `double AdverseShare` | Quota di casi in cui il percorso fine esce PEGGIO: il tocco non è gratis nemmeno qui. |

## `ProcioneMGR/Services/Trading/ProtectiveExitShadow.cs`

### 📦 `ProtectiveExitShadow`

> [B3, sentinella] Un confronto COMPLETATO fra il momento in cui il feed real-time avrebbe fatto scattare un'uscita protettiva e il momento in cui il percorso a candele l'ha fatta scattare davvero. Una riga per confronto, scritta solo quando entrambi i lati esistono. Non serve a produrre una media: su tre corsie che fanno una dozzina di trade al mese le osservazioni sono troppo poche perché una mediana significhi qualcosa, e quella domanda è già stata chiusa offline dal replay su migliaia di posizioni (REPORT-B3-EXITLAG-2026-07-28). Serve a vedere il caso SINGOLO che il replay non poteva vedere: un crollo con gap, dove aspettare la chiusura della barra non costa qualche punto base ma una categoria diversa di danno.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | — |
| `p` | `string Symbol` | — |
| `p` | `TradingMode Mode` | Modalità della corsia al momento del confronto (mai mescolare Paper e Testnet). |
| `p` | `string PositionId` | — |
| `p` | `OrderSide Side` | — |
| `p` | `decimal EntryPrice` | — |
| `p` | `DateTime DetectedAtUtc` | Quando il primo tick ha soddisfatto la condizione di uscita, e a che prezzo. |
| `p` | `decimal DetectedPrice` | — |
| `p` | `string DetectedReason` | Motivo che sarebbe scattato sul tick ("StopLoss", "TakeProfit", "Liquidation"). |
| `p` | `decimal ShadowFillPrice` | Prezzo di riempimento che il tick avrebbe ottenuto, dallo stesso evaluator del motore. |
| `p` | `DateTime ActualExitAtUtc` | Quando il percorso a candele ha chiuso davvero, a che prezzo e per quale motivo. |
| `p` | `decimal ActualFillPrice` | — |
| `p` | `string ActualReason` | — |
| `p` | `double LeadSeconds` | Secondi di anticipo del feed sulla scoperta. |
| `p` | `double DelayCostBps` | Costo del ritardo in punti base dell'ingresso, orientato sulla posizione: POSITIVO = il feed avrebbe fatto uscire meglio, negativo = aspettare la chiusura è convenuto. Stessa convenzione di , così i due numeri sono conf… |
| `p` | `DateTime CreatedAtUtc` | — |

### 🔌 `IProtectiveExitShadowRecorder`

> Persiste i confronti d'ombra e ALLERTA sul caso singolo. La soglia è il punto di tutto il meccanismo: senza, questa tabella sarebbe un raccoglitore che nessuno legge.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task RecordAsync(ProtectiveExitShadow comparison, CancellationToken ct = default)` | — |

### 📦 `ProtectiveExitShadowOptions`

> Opzioni della sentinella.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Spegnibile: la sentinella osserva e basta, ma osservare a ogni tick ha un costo e chi non la vuole deve poterla togliere senza toccare il codice. |
| `p` | `double AlertAboveBps` | Sopra questo costo in punti base si allerta, sul SINGOLO evento. 200 bps (2%) non è una stima: è la soglia oltre la quale un caso solo vale più di una media, perché a quel punto non si sta più misurando l'effetto dell'o… |

### 📦 `ProtectiveExitShadowRecorder` `(`

> Implementazione: una INSERT per confronto, più l'allarme sopra soglia. Nessun aggiornamento e nessuno stato: le rilevazioni ancora in sospeso vivono in memoria nel motore e si perdono a un riavvio del core. È una rinuncia dichiarata — raddoppiare lo schema per non perdere una manciata di rilevazioni in volo su una sentinella non vale il suo costo.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task RecordAsync(ProtectiveExitShadow c, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Trading/Queries/GetLaneStatusQuery.cs`

### 🧾 `GetLaneStatusQuery` `(int LaneId) : IRequest&lt;TradingEngineStatus&gt;;`

### 📦 `GetLaneStatusQueryHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;GetLaneStatusQuery, TradingEngine…`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;TradingEngineStatus&gt; Handle(GetLaneStatusQuery request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Queries/GetOpenPositionsQuery.cs`

### 🧾 `GetOpenPositionsQuery` `(int LaneId) : IRequest&lt;List&lt;OpenPosition&gt;&gt;;`

### 📦 `GetOpenPositionsQueryHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;GetOpenPositionsQuery, List&lt;Op…`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;List&lt;OpenPosition&gt;&gt; Handle(GetOpenPositionsQuery request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Queries/GetOrderHistoryQuery.cs`

### 🧾 `GetOrderHistoryQuery` `(int LaneId, DateTime? From = null) : IRequest&lt;List&lt;Order&gt;&gt;;`

### 📦 `GetOrderHistoryQueryHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;GetOrderHistoryQuery, List&lt;Ord…`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;List&lt;Order&gt;&gt; Handle(GetOrderHistoryQuery request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Queries/GetPendingOrdersQuery.cs`

### 🧾 `GetPendingOrdersQuery` `(int LaneId) : IRequest&lt;List&lt;Order&gt;&gt;;`

### 📦 `GetPendingOrdersQueryHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;GetPendingOrdersQuery, List&lt;Or…`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;List&lt;Order&gt;&gt; Handle(GetPendingOrdersQuery request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/Queries/GetPerformanceQuery.cs`

### 🧾 `GetPerformanceQuery` `(int LaneId, DateTime? From = null) : IRequest&lt;TradingPerformance&gt;;`

### 📦 `GetPerformanceQueryHandler` `(IServiceProvider serviceProvider) : IRequestHandler&lt;GetPerformanceQuery, TradingPerfo…`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;TradingPerformance&gt; Handle(GetPerformanceQuery request, CancellationToken cancellationToken)` | — |

## `ProcioneMGR/Services/Trading/RemoteTradingEngineClient.cs`

### 📦 `RemoteTradingEngineClient` `(`

> Implementazione di che delega l'esecuzione al microservizio procionemgr-trading via gRPC (Fase 2b microservizi). Attiva nel monolite solo con Trading:UseRemoteTrading=true , dove SOSTITUISCE il locale (mai affiancarlo: due motori sulla stessa corsia aprirebbero ordini in doppio). Implementa l'interfaccia INTERA di proposito: i consumer — Trading.razor ma soprattutto gli automatismi / / , che promuovono e retrocedono corsie da soli — risolvono keyed e non sanno (né devono sapere) cosa c'è dietro. Un'implementazione parziale romperebbe in silenzio quell'automazione di sicurezza.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | — |
| `m` | `Task&lt;TradingEngineStatus&gt; GetStatusAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;OpenPosition&gt;&gt; GetOpenPositionsAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;TradingPerformance&gt; GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task StartAsync(TradingMode mode, CancellationToken ct = default)` | — |
| `m` | `Task StopAsync(CancellationToken ct = default)` | — |
| `m` | `Task EmergencyStopAsync(string reason, CancellationToken ct = default)` | — |
| `m` | `Task ClosePositionAsync(string positionId, CancellationToken ct = default)` | — |
| `m` | `Task CloseAllPositionsAsync(string reason, CancellationToken ct = default)` | — |
| `m` | `Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetPendingOrdersAsync(CancellationToken ct = default)` | — |
| `m` | `Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default)` | — |
| `m` | `Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default)` | [R1] I tick di prezzo NON attraversano mai il confine gRPC: sarebbe una chiamata di rete per ogni tick, e la latenza che si vorrebbe eliminare tornerebbe dentro dal lato sbagliato. Il feed real-time è registrato nello S… |
| `m` | `Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Trading/SafetyChecker.cs`

### 📦 `SafetyCheckResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsAllowed` | — |
| `p` | `List&lt;string&gt; Violations` | — |
| `p` | `bool RequiresEmergencyStop` | True se una violazione è CRITICA (max daily loss / max drawdown) e va attivato l'emergency stop. |

### 📦 `SafetyChecker`

> Valida ogni ordine contro i limiti di sicurezza PRIMA di piazzarlo. Principio: meglio rifiutare un ordine valido che accettarne uno pericoloso. Solo il metodo statico puro: l'interfaccia istanza (ISafetyChecker/ValidateOrderAsync) era registrata in DI ma mai risolta da nessuno — rimossa come codice morto.

| | Firma | Descrizione |
|---|---|---|
| `m` | `SafetyCheckResult Evaluate(Order order, TradingEngineStatus status, SafetyConfiguration cfg, DateTime nowUtc)` | Valutazione PURA (senza I/O) di tutti i safety check. Raccoglie TUTTE le violazioni (non si ferma alla prima) così l'operatore vede l'intero quadro. Testabile direttamente. |

## `ProcioneMGR/Services/Trading/SafetyConfiguration.cs`

### 📦 `SafetyConfiguration`

> Limiti di sicurezza del trading. Bindato da appsettings.json sezione "Trading:Safety". I default sono CONSERVATIVI: in caso di config mancante il sistema resta prudente.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal MaxPositionSizePercent` | Max % del capitale totale per una singola posizione. |
| `p` | `decimal PositionSizePercent` | % del capitale impiegata per ogni apertura: NOZIONALE investito per lo Spot (leva implicita 1x), MARGINE isolato per i Futures — il nozionale reale è margine × leva, stessa semantica del motore di backtest. Coerenza ric… |
| `p` | `decimal MaxTotalExposurePercent` | Max % del capitale totale impegnata complessivamente in posizioni aperte. |
| `p` | `decimal MaxDailyLossPercent` | Stop trading se la perdita giornaliera supera questa % del capitale. |
| `p` | `decimal MaxDrawdownPercent` | Stop trading se il drawdown supera questa %. |
| `p` | `int MaxOpenPositions` | Numero massimo di posizioni aperte contemporaneamente. |
| `p` | `int MinOrderIntervalSeconds` | Intervallo minimo (secondi) tra un ordine e il successivo (anti-spam). |
| `p` | `bool RequireManualConfirmationForLive` | Se true, ogni ordine in modalità Live richiede conferma manuale dell'operatore. |
| `p` | `bool VolatilityTargetingEnabled` | Se true, viene moltiplicato per un fattore che punta a una volatilità costante: meno capitale esposto quando il mercato si agita, di più quando si calma. È l'unico risultato di ricerca sopravvissuto al controllo a espos… |
| `p` | `decimal TargetAnnualVolatilityPercent` | Volatilità annualizzata a cui puntare (%). Sotto questo valore si espone di più, sopra di meno. |
| `p` | `int VolatilityLookbackBars` | Barre usate per stimare la volatilità realizzata. 30 è il valore validato dalla ricerca. |
| `p` | `decimal MinExposureMultiplier` | Pavimento del moltiplicatore: sotto questo non si scende, per non annullare del tutto l'operatività. |
| `p` | `decimal MaxExposureMultiplier` | Tetto del moltiplicatore. Default 1,0 di proposito: così il dosaggio può solo RIDURRE la dimensione rispetto a , mai aumentarla, e accendere la funzione non può violare né . Alzarlo sopra 1,0 toglie questa garanzia. |
| `p` | `int MaxLeverageAllowed` | Leva massima consentita per il trading Futures (default CONSERVATIVO: con un capitale piccolo la leva alta è attraente ma la crescita del rischio non è lineare — vedi , che tipicamente sconsiglia oltre 3-5x anche per si… |
| `p` | `decimal MaintenanceMarginPercent` | Margine di mantenimento in % del nozionale, usato per la STIMA locale del prezzo di liquidazione ( ) quando l'exchange non la riporta ancora (es. subito dopo il fill, o in modalità Paper). Stessa convenzione e stesso de… |
| `p` | `bool UseExchangeRestingStops` | [P0-5] Se true, all'apertura di una posizione FUTURES in Testnet/Live il motore piazza sull'exchange ordini TRIGGER reduce-only (stop-market e take-profit-market) come protezione "resting": restano validi sull'exchange … |
| `p` | `decimal MaxFillPriceDeviationPercent` | [B1] Banda massima (± % dal prezzo corrente di mercato) entro cui il prezzo di fill riportato dall'exchange è considerato plausibile. Fuori banda (o ≤ 0) il fill è SOSPETTO e non viene mai adottato: vedi e il bug B1 in … |
| `p` | `decimal MaxFillQuantityDeviationPercent` | [B1] Tolleranza massima (± % dalla quantità RICHIESTA) entro cui la quantità di fill riportata dall'exchange è considerata plausibile. Fuori tolleranza (es. quantità cumulative 100x dal testnet, bug B1) il fill è SOSPET… |
| `p` | `decimal FeePercent` | [P2-8] Fee dell'exchange in % del nozionale, applicata sia in apertura sia in chiusura. Prima era una costante fissa in TradingEngine (stesso valore di default, 0.1%), scollegata dal fee reale e dal parametro equivalent… |

## `ProcioneMGR/Services/Trading/SafetyExposure.cs`

### 📦 `SafetyExposure`

> [D-02, Fase 1 PRD-RISANAMENTO 2026-08-08] L'esposizione che alimenta il check n.2 del ( MaxTotalExposurePercent ): il NOZIONALE delle posizioni aperte, Σ Quantity × EntryPrice , per ogni tipo di mercato. PERCHE' esiste come funzione a parte: prima il valore era calcolato inline in TradingEngine.BuildSafetyStatus e sui Futures usava il MARGINE (Σ MarginBalance), con un commento che dichiarava l'asimmetria «volutamente conservativa». Era vero sul singolo ordine (order.Notional e' leveraged) ma FALSO sull'accumulo: ogni posizione gia' aperta pesava 1/leva della propria esposizione reale, e con MaxOpenPositions alzato il capitale esposto superava il DOPPIO di MaxTotalExposurePercent senza far scattare il check (esempio numerico in docs/audit/20_DEEP_DIVE_CODE_ANALYSIS.md §3). Coi default la coincidenza 10% × 5 = 50% mascherava il buco. Le unita' ora sono omogenee: il limite vincola cio' che…

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal ExposedNotional(IEnumerable&lt;OpenPosition&gt; positions)` | Nozionale complessivamente esposto dalle posizioni aperte (unita' di order.Notional). |

## `ProcioneMGR/Services/Trading/TradingContractMapper.cs`

### 📦 `TradingContractMapper`

> Mappatura fra i modelli di dominio del trading e i messaggi di trading.proto (Fase 2b). Usata da ENTRAMBI i lati del filo — TradingCommandServiceImpl (servizio) e (monolite) — così la proiezione è definita una volta sola e non può divergere fra chi scrive e chi legge. Gli enum sono mappati a switch esaustivo, mai per cast ordinale (stesso patto di MlStageMapper in Fase 2a): TradingMode.Paper vale 0 in C# ma TRADING_MODE_PAPER vale 1 in proto3 (lo zero è riservato a UNSPECIFIED), quindi un cast diretto trasformerebbe Paper in Testnet — cioè una simulazione in una sessione con soldi veri sull'exchange. Uno switch esplicito che lancia sull'ignoto rende impossibile quella classe di errore.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Proto.TradingMode ToProto(TradingMode mode)` | — |
| `m` | `TradingMode FromProto(Proto.TradingMode mode)` | — |
| `m` | `Proto.MarketType ToProto(MarketType type)` | — |
| `m` | `MarketType FromProto(Proto.MarketType type)` | — |
| `m` | `Proto.OrderSide ToProto(OrderSide side)` | — |
| `m` | `OrderSide FromProto(Proto.OrderSide side)` | — |
| `m` | `Timestamp ToProto(DateTime utc)` | Timestamp.FromDateTime PRETENDE Kind=Utc, ma i DateTime che arrivano da Postgres hanno Kind=Unspecified (switch "legacy timestamp behavior": semantica naive-UTC, i tick sono già UTC). Senza questo SpecifyKind la convers… |
| `m` | `Timestamp? ToProtoNullable(DateTime? utc)` | — |
| `m` | `DateTime FromProto(Timestamp ts)` | Ritorna Kind=Utc (non Unspecified come il DB): il valore è esplicitamente UTC. |
| `m` | `DateTime? FromProtoNullable(Timestamp? ts)` | — |
| `m` | `Proto.GetLaneStatusResponse ToProto(TradingEngineStatus s, int laneId)` | — |
| `m` | `TradingEngineStatus FromProto(Proto.GetLaneStatusResponse r)` | — |
| `m` | `Proto.OpenPosition ToProto(OpenPosition p)` | — |
| `m` | `OpenPosition FromProto(Proto.OpenPosition p)` | — |
| `m` | `Proto.GetPerformanceResponse ToProto(TradingPerformance p)` | — |
| `m` | `TradingPerformance FromProto(Proto.GetPerformanceResponse r)` | — |
| `m` | `Proto.TradeRecord ToProto(TradeRecord t)` | — |
| `m` | `TradeRecord FromProto(Proto.TradeRecord t)` | — |

## `ProcioneMGR/Services/Trading/TradingEngine.cs`

### 📦 `TradingEngine` `(`

> Trading engine (Fase 8) per UNA corsia di trading isolata ( ). Implementa la modalità PAPER (simulazione con dati reali, nessun soldo vero). Registrato come Keyed Singleton (una istanza per corsia — vedi Program.cs) invece di un singolo Singleton globale come prima del supporto multi-coppia: thread-safe via come prima, ma ora ogni istanza filtra/imposta (e l'equivalente su Order/TradeRecord/TradingEngineState/TradingAuditLog) con il PROPRIO , così due corsie non vedono/toccano mai le posizioni o gli ordini l'una dell'altra — anche condividendo lo stesso database. Le righe esistenti PRIMA di questo supporto hanno LaneId=0 (default di migrazione): sono automaticamente la corsia 0, la sessione di trading già in corso non viene toccata da questo refactor. SAFETY: ogni apertura passa da ; le violazioni critiche (daily loss, drawdown) attivano l'emergency stop che CHIUDE TUTTE le posizioni DI…

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | — |
| `k` | `string ChampionStrategyName` | Nome sentinella di strategia (non nello switch di ): risolve il Champion del registry via e lo esegue come su questa lane. CONSENTITO SOLO Paper/Testnet — mai Live (vedi ). |

### 🧾 `ChampionCacheEntry` `(int ModelId, int Version, MlStrategy Strategy, IReturnPredictor Predictor);`

> Cache per-lane del Champion materializzato. Il payload pesante (deserializzazione del modello dal blob) si ricarica SOLO quando cambia il modello ( o ): un controllo leggero a ogni candela, ricostruzione solo al cambio.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task StartAsync(TradingMode mode, CancellationToken ct = default)` | — |
| `m` | `Task StopAsync(CancellationToken ct = default)` | — |
| `m` | `Task EmergencyStopAsync(string reason, CancellationToken ct = default)` | — |
| `m` | `Task CloseAllPositionsAsync(string reason, CancellationToken ct = default)` | [M2] Chiude tutte le posizioni della corsia al miglior prezzo noto SENZA toccare i flag di emergenza: è il "flatten" usato dalla promozione/retrocessione di corsia (LanePromoter), dove fermare la corsia non è un'emergen… |
| `m` | `Task ProcessCandleAsync(OhlcvData candle, CancellationToken ct = default)` | — |
| `m` | `Task ProcessPriceTickAsync(decimal price, DateTime tsUtc, CancellationToken ct = default)` | [R1] Elabora un TICK di prezzo real-time. Valuta ESCLUSIVAMENTE le uscite protettive (liquidazione, stop loss, take profit, trailing) sulle posizioni già aperte. CONFINE NON NEGOZIABILE: da qui non si apre MAI una posiz… |

### ▫️ `ShadowDetection` `(DateTime AtUtc, decimal Price, string Reason, decimal FillPrice);`

> Rilevazione d'ombra in attesa che il percorso a candele chiuda la stessa posizione.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ProcessDueExecutionSlicesAsync(CancellationToken ct = default)` | Avanza le fette dovute di ogni piano Running di questa corsia. Chiamato dall' ExecutionWorker . Stesso pattern-guard di (gate + IsRunning/IsEmergencyStopped), quindi serializzato con tutto il resto del motore. |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetPendingOrdersAsync(CancellationToken ct = default)` | — |
| `m` | `Task ConfirmOrderAsync(string orderId, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task RejectOrderAsync(string orderId, string? userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;TradingEngineStatus&gt; GetStatusAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;OpenPosition&gt;&gt; GetOpenPositionsAsync(CancellationToken ct = default)` | — |
| `m` | `Task SetStopLossTakeProfitAsync(string positionId, decimal? stopLoss, decimal? takeProfit, decimal? trailingStopPercent = null, CancellationToken ct …` | — |
| `m` | `Task ClosePositionAsync(string positionId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;Order&gt;&gt; GetOrderHistoryAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `Task&lt;TradingPerformance&gt; GetPerformanceAsync(DateTime? from = null, CancellationToken ct = default)` | — |
| `m` | `void TrimEquity(List&lt;EquityPoint&gt; curve, int maxPoints = 10_000, int trimBlock = 2_000)` | [M1] Ritenzione bounded della curva equity in-memory: oltre punti si scarta il BLOCCO più vecchio (una RemoveRange ogni candele, non una RemoveAt per candela). A 5m sono ~34 giorni di storia: abbastanza per Sharpe/drawd… |

## `ProcioneMGR/Services/Trading/TradingEntities.cs`

### 📦 `TradingEngineState`

> Stato persistito del trading engine (riga singola). Garantisce idempotenza: al restart il sistema ricostruisce lo stato (running/mode/capitale/emergency) dal DB.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default, esistente prima del supporto multi-coppia). Ogni corsia ha la propria istanza di TradingEngine/EnsembleManager, mai condivise. |
| `p` | `TradingMode Mode` | — |
| `p` | `MarketType MarketType` | — |
| `p` | `int Leverage` | Leva della sessione (1 per Spot; impostata via SetLeverageAsync all'avvio per Futures). |
| `p` | `bool IsRunning` | — |
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `string Timeframe` | — |
| `p` | `decimal TotalCapital` | — |
| `p` | `decimal AvailableCapital` | — |
| `p` | `decimal RealizedPnl` | — |
| `p` | `decimal PeakEquity` | Equity massima raggiunta (per il calcolo del drawdown). |
| `p` | `decimal MaxDrawdownPercent` | Massimo drawdown % osservato dalla StartAsync della sessione (persistito): prima viveva solo nella curva equity in-memory, quindi un riavvio lo azzerava — e il gate assoluto HardMaxDrawdownPercent di PromotionEvaluator … |
| `p` | `decimal DailyPnl` | PnL realizzato nelle ultime 24h (rolling), per il safety check daily-loss. |
| `p` | `DateTime DailyAnchorUtc` | — |
| `p` | `DateTime? StartedAtUtc` | — |
| `p` | `DateTime? LastOrderUtc` | — |
| `p` | `bool IsEmergencyStopped` | — |
| `p` | `string? EmergencyStopReason` | — |
| `p` | `DateTime UpdatedAtUtc` | — |

### 📦 `TradingAuditLog`

> Audit trail: ogni azione di trading (ordine, chiusura, emergency, start/stop) è loggata.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading che ha generato questa voce di audit (0 = corsia di default). |
| `p` | `DateTime TimestampUtc` | — |
| `p` | `string Action` | "PlaceOrder", "OrderRejected", "ClosePosition", "EmergencyStop", "StartEngine", "StopEngine". |
| `p` | `string Details` | JSON con i dettagli dell'azione. |
| `p` | `string? UserId` | Utente che ha eseguito l'azione (null per il background worker). |
| `p` | `TradingMode Mode` | — |

### 📦 `LaneQuarantine`

> Quarantena di una corsia (Fase 0-A3, PRD Autonomia Operativa): riga inserita dal quando un invariante contabile risulta violato (es. il caso reale della corsia 2: PnL -1,8M su capitale 10k da fill patologici). Finché la riga esiste, TradingEngine.StartAsync RIFIUTA di riavviare la corsia: un nuovo StartAsync azzererebbe capitale/PnL cancellando l'evidenza da esaminare. La rimozione è un'azione umana esplicita (/trading, solo Admin) dopo verifica. Tabella separata da proprio perché StartAsync RIGENERA quella riga da zero: un flag lì sopra non sopravvivrebbe.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int LaneId` | Chiave naturale: al più una quarantena attiva per corsia. |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `string Reason` | Invarianti violati, leggibile per l'operatore (banner in /trading). |
| `p` | `string DetailsJson` | JSON con i valori osservati al momento della violazione (stato contabile + soglie). |

## `ProcioneMGR/Services/Trading/TradingLanes.cs`

### 📦 `TradingLanes`

> Numero di corsie di trading isolate (LaneId 0..Count-1). UNICA fonte di verità: prima il "3" era ripetuto a mano in Program.cs (registrazioni keyed), Trading.razor, Ensemble.razor e PromotionEvaluator — aumentare le corsie toccandone solo alcuni avrebbe prodotto corsie invisibili in UI o mai valutate dalla promozione. Configurabile da Trading:LaneCount (default ), letto una volta sola all'avvio da AddTradingLanes . Resta uno static e non un servizio iniettato perché è consultato da posti in cui un servizio non arriverebbe senza una cascata di modifiche: pagine Razor, watchdog degli invarianti, valutatore di promozione, validatore di lane del gRPC. Un valore letto una volta e mai più cambiato è la forma più semplice che risolve il problema; renderlo mutabile a caldo significherebbe invece che il numero di corsie può cambiare mentre dei motori stanno operando — cioè avere corsie orfane o …

| | Firma | Descrizione |
|---|---|---|
| `k` | `int DefaultCount` | Valore storico, e default se nessuno configura nulla. |
| `k` | `int MaxCount` | Tetto invalicabile. Non è una stima di capacità: è una protezione dal refuso. Ogni corsia avvia tre worker (il più frequente batte ogni 2 secondi con una lettura di stato), quindi un "LaneCount": 300 scritto per sbaglio… |
| `p` | `int Count` | Numero di corsie attive in questo processo. |
| `m` | `void Configure(int count)` | Imposta il numero di corsie. Va chiamata una volta sola all'avvio, prima che qualunque cosa legga . Ri-chiamarla con lo STESSO valore è innocuo (i test costruiscono più contenitori DI nello stesso processo); con un valo… |
| `m` | `void ResetForTests()` | Solo per i test: riporta il conteggio al default e scongela. |

## `ProcioneMGR/Services/Trading/TradingModels.cs`

### 🔢 `TradingMode`

### 🔢 `MarketType`

> Spot (proprietà dell'asset, leva implicita 1x) vs Futures perpetui a margine ISOLATO (leva configurabile, rischio di liquidazione). Impostato per l'intera sessione di trading (come Symbol/Timeframe), non cambia a runtime.

### 🔢 `OrderSide`

### 🔢 `OrderType`

### 🔢 `OrderStatus`

### 📦 `TradingEngineStatus`

| | Firma | Descrizione |
|---|---|---|
| `p` | `TradingMode Mode` | — |
| `p` | `MarketType MarketType` | — |
| `p` | `int Leverage` | Leva della sessione (1 per Spot; configurabile per Futures). |
| `p` | `bool IsRunning` | — |
| `p` | `string ExchangeName` | — |
| `p` | `string Symbol` | — |
| `p` | `decimal TotalCapital` | — |
| `p` | `decimal AvailableCapital` | — |
| `p` | `decimal UsedCapital` | Capitale impegnato in posizioni aperte: nozionale pieno per lo Spot, solo il MARGINE isolato per i Futures (è quanto viene realmente sottratto ad AvailableCapital — vedi TradingEngine.ExecuteFuturesOpenAsync ). |
| `p` | `decimal TotalPnl` | — |
| `p` | `decimal TotalPnlPercent` | — |
| `p` | `decimal DailyPnl` | PnL realizzato nelle ultime 24h (negativo = perdita). Usato dal safety check daily-loss. |
| `p` | `decimal MaxDrawdown` | Max drawdown corrente, in PERCENTUALE (0-100). |
| `p` | `int TotalTrades` | — |
| `p` | `int OpenPositionCount` | Numero di posizioni attualmente aperte (per il safety check MaxOpenPositions). |
| `p` | `decimal WinRate` | — |
| `p` | `DateTime? StartedAtUtc` | — |
| `p` | `DateTime? LastOrderUtc` | — |
| `p` | `bool IsEmergencyStopped` | — |
| `p` | `string? EmergencyStopReason` | — |
| `p` | `string Timeframe` | [E6] Timeframe della corsia: serve a chi legge il battito per giudicarlo con la regola di freschezza. |
| `p` | `DateTime? LastProcessedCandleUtc` | [E6] Apertura dell'ultima candela VALUTATA dal motore in questo avvio del processo (null = nessuna ancora). È il battito che IsRunning non è: quel flag dichiara l'intenzione di girare, questo timestamp prova l'attività … |

### 📦 `OpenPosition`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default). |
| `p` | `string PositionId` | — |
| `p` | `string StrategyId` | — |
| `p` | `string Symbol` | — |
| `p` | `OrderSide Side` | — |
| `p` | `decimal EntryPrice` | — |
| `p` | `decimal Quantity` | — |
| `p` | `decimal? StopLoss` | — |
| `p` | `decimal? TakeProfit` | — |
| `p` | `decimal? TrailingStopPercent` | Trailing stop in %, applicato automaticamente dall'EnsembleStrategy o impostato a mano. Il livello effettivo si ricalcola ogni candela da (vedi TradingEngine.ProcessCandleAsync ), sullo stesso schema causale del motore … |
| `p` | `decimal? BestPriceSinceEntry` | Massimo (long) / minimo (short) toccato dal prezzo dall'apertura, per il trailing stop. Null finché il trailing non è attivo. |
| `p` | `DateTime OpenedAtUtc` | — |
| `p` | `decimal CurrentPrice` | — |
| `p` | `decimal UnrealizedPnl` | — |
| `p` | `decimal UnrealizedPnlPercent` | — |
| `p` | `string? ExchangeOrderId` | — |
| `p` | `TradingMode OpenedInMode` | Modalità in cui la posizione è stata APERTA. Discriminatore anti-mescolamento (M2): al cambio di modalità della corsia (promozione/retrocessione), EnsureLoadedAsync carica solo le righe della modalità corrente e PURGA l… |
| `p` | `string? StopOrderId` | [P0-5] Id (clientOrderId) degli ordini TRIGGER reduce-only piazzati sull'exchange come protezione "resting" (stop-market / take-profit-market), quando è attivo (default OFF). PERSISTITI (M3): dopo un riavvio la chiusura… |
| `p` | `string? TakeProfitOrderId` | [P0-5] Vedi . |
| `p` | `int Leverage` | Leva della posizione (1 per Spot). |
| `p` | `decimal? LiquidationPrice` | Prezzo di liquidazione stimato/riportato dall'exchange (solo Futures, null per Spot). In Testnet/Live è la fonte di verità dell'exchange quando disponibile, altrimenti la stima locale via ; in Paper è sempre la stima lo… |
| `p` | `decimal MarginBalance` | Margine isolato allocato alla posizione (= Quantity*EntryPrice per lo Spot). |

### 📦 `Order`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default). |
| `p` | `string OrderId` | — |
| `p` | `string ClientOrderId` | Client order id idempotente inviato all'exchange (newClientOrderId/clientOid). |
| `p` | `string PositionId` | — |
| `p` | `string StrategyId` | — |
| `p` | `string Symbol` | — |
| `p` | `OrderSide Side` | — |
| `p` | `OrderType Type` | — |
| `p` | `decimal Quantity` | — |
| `p` | `decimal? Price` | Prezzo limite, oppure prezzo di riferimento stimato per i market order (per i safety check). |
| `p` | `OrderStatus Status` | — |
| `p` | `decimal? FilledPrice` | — |
| `p` | `decimal? FilledQuantity` | — |
| `p` | `DateTime CreatedAtUtc` | — |
| `p` | `DateTime? FilledAtUtc` | — |
| `p` | `string? ExchangeOrderId` | — |
| `p` | `string? ErrorMessage` | — |
| `p` | `TradingMode Mode` | — |
| `p` | `MarketType MarketType` | — |
| `p` | `int Leverage` | Leva usata per questo ordine (1 per Spot). |
| `p` | `bool ManuallyConfirmed` | Conferma manuale dell'operatore (richiesta in Live se abilitata in SafetyConfiguration). |
| `p` | `decimal? ArrivalPrice` | [Fase 1] Prezzo di riferimento fissato al momento della DECISIONE, prima di inviare l'ordine all'exchange: il denominatore dell'implementation shortfall. Serve un campo suo perché ha due significati (limite oppure rifer… |
| `p` | `int? SubmitLatencyMs` | [Fase 1] Millisecondi fra l'invio della richiesta all'exchange e la sua risposta. Include di proposito l'attesa del rate-limiter client-side: è il ritardo che la strategia subisce davvero, non solo quello di rete. |
| `p` | `decimal Notional` | Notional stimato dell'ordine (Quantity × Price). |
| `p` | `decimal? ShortfallBps` | [Fase 1] Implementation shortfall in punti base, segnato come COSTO (positivo = eseguito peggio del riferimento), stessa convenzione degli ExecutionJob e di ExecutionSimulator. Null quando l'esecuzione non è misurabile. |

### 📦 `TradingPerformance`

| | Firma | Descrizione |
|---|---|---|
| `p` | `List&lt;EquityPoint&gt; EquityCurve` | — |
| `p` | `decimal TotalReturn` | — |
| `p` | `decimal SharpeRatio` | — |
| `p` | `decimal MaxDrawdown` | — |
| `p` | `int TotalTrades` | — |
| `p` | `decimal WinRate` | — |
| `p` | `decimal AverageWin` | — |
| `p` | `decimal AverageLoss` | — |
| `p` | `decimal ProfitFactor` | — |
| `p` | `List&lt;TradeRecord&gt; Trades` | — |

### 📦 `TradeRecord`

| | Firma | Descrizione |
|---|---|---|
| `p` | `int Id` | — |
| `p` | `int LaneId` | Corsia di trading isolata (0 = corsia di default). |
| `p` | `string PositionId` | — |
| `p` | `string StrategyId` | — |
| `p` | `string Symbol` | — |
| `p` | `OrderSide Side` | — |
| `p` | `decimal EntryPrice` | — |
| `p` | `decimal ExitPrice` | — |
| `p` | `decimal Quantity` | — |
| `p` | `decimal Pnl` | — |
| `p` | `decimal PnlPercent` | — |
| `p` | `DateTime OpenedAtUtc` | — |
| `p` | `DateTime ClosedAtUtc` | — |
| `p` | `TimeSpan Duration` | — |
| `p` | `string? ExitReason` | — |
| `p` | `TradingMode Mode` | — |
| `p` | `MarketType MarketType` | — |
| `p` | `int Leverage` | Leva usata per il trade (1 per Spot). |
| `p` | `bool WasLiquidated` | True se la chiusura è stata una liquidazione (forzata o rilevata per riconciliazione). |

## `ProcioneMGR/Services/Trading/TradingOrderQueries.cs`

### 📦 `TradingOrderQueries`

> Composizione delle query di LETTURA sugli ordini, condivisa fra e (che le esegue in bypass di gRPC: sono query pure su Postgres, senza stato in-memory del motore). Esiste per eliminare una deriva alla radice: in Fase 2b il client remoto portava una COPIA riga-per-riga di queste query, tenuta allineata da un commento ("se cambia là, aggiorna qua") e da test head-to-head. Ma quei test confrontano i risultati sui dati che il test semina: un filtro aggiunto solo lato engine su una dimensione non seminata avrebbe prodotto risultati identici comunque, e la divergenza sarebbe passata inosservata — le due modalità avrebbero mostrato ordini diversi in produzione. Con la composizione unica la deriva è impossibile per costruzione; i test head-to-head restano come cintura contro una futura re-duplicazione. Prende IQueryable e non il DbContext: il chiamante decide tracking (AsNoTracking o no) e mate…

| | Firma | Descrizione |
|---|---|---|
| `m` | `IQueryable&lt;Order&gt; History(IQueryable&lt;Order&gt; orders, int laneId, DateTime? from)` | Storico ordini della corsia, più recenti prima, cap a 500 righe (è ciò che la UI mostra; nessun consumer automatico legge lo storico). opzionale su CreatedAtUtc. |
| `m` | `IQueryable&lt;Order&gt; PendingLive(IQueryable&lt;Order&gt; orders, int laneId)` | Ordini Live in attesa di conferma manuale dell'operatore, più recenti prima. Il filtro Mode==Live è sostanza, non ottimizzazione: Paper/Testnet non passano dalla coda di conferma. |

## `ProcioneMGR/Services/Trading/TradingPageService.cs`

### 📦 `TradingPageService` `(`

> Orchestrazione di Components/Pages/Trading.razor (P1-5, audit consolidamento 2026-07-17): tutte le chiamate a / / / e lo stato che ne deriva, così la logica di orchestrazione ha test unitari indipendenti da Blazor (vedi TradingPageServiceTests ). Il componente resta responsabile solo di ciò che è intrinsecamente Blazor: rendering, ciclo di vita ( OnInitializedAsync / Dispose , PollingTimer ), StateHasChanged , e la manciata di stato puramente di UI (modalità radio-selezionata, checkbox di conferma Live, corsia attualmente visualizzata) che non richiede alcuna chiamata a servizio. Registrato Scoped: in Blazor Server uno scope = un circuito, quindi un'istanza per sessione utente — stessa granularità del componente che la consuma, senza stato condiviso fra utenti. La corsia ( laneId ) NON è stato interno di questo servizio ma un parametro esplicito di ogni metodo: è una selezione di naviga…

| | Firma | Descrizione |
|---|---|---|
| `p` | `TradingEngineStatus? Status` | — |
| `p` | `LaneQuarantine? Quarantine` | Quarantena attiva della corsia visualizzata (Fase 0-A3), null se la corsia è pulita. |
| `p` | `List&lt;OpenPosition&gt; Positions` | — |
| `p` | `List&lt;Order&gt; Orders` | — |
| `p` | `bool ShowAllOrders` | [2026-08-05] Falso (default): la tabella ordini mostra solo il TEST CORRENTE, come i KPI. Vero: tutta la vita della corsia, comprese le configurazioni precedenti su altri simboli — utile per un'indagine, fuorviante come… |
| `m` | `void ToggleOrderHistory()` | Alterna fra la finestra del test corrente e lo storico completo. Il chiamante ricarica. |
| `p` | `IReadOnlyList&lt;LaneEpisode&gt; Episodes` | [2026-08-06] Gli episodi della corsia: un tratto di vita per ogni avvio del motore, dal più recente. Popolato solo in modalità storico completo — sul test corrente c'è un episodio solo e raggrupparlo sarebbe cerimonia i… |
| `m` | `IReadOnlyList&lt;Order&gt; OrdersOf(LaneEpisode ep)` | Gli ordini di un episodio, per la tabella raggruppata. |
| `p` | `IReadOnlyList&lt;ProtectiveExitAnomaly&gt; ExitAnomalies` | [2026-08-06] Protezioni risultate toccate da barre CHIUSE con la posizione ancora aperta. Vuoto è il caso normale. Vedi per il perché esiste. |
| `p` | `List&lt;Order&gt; Pending` | — |
| `p` | `List&lt;Indicators.IndicatorSeries&gt; Equity` | — |
| `p` | `string? Message` | — |
| `p` | `bool IsError` | — |
| `p` | `DateTime? StaleSince` | Da quando il servizio di trading remoto (Trading:UseRemoteTrading) non risponde; null se l'ultimo refresh è andato a buon fine. |
| `p` | `string? LastStaleReason` | Codice di stato gRPC dell'ultimo fallimento: dice all'operatore se il servizio è giù o solo lento/rotto. |
| `p` | `List&lt;PromotionDecision&gt; Promotions` | — |
| `p` | `bool PromoBusy` | — |
| `p` | `string? PromoMessage` | — |
| `p` | `bool PromoIsError` | — |
| `p` | `SafetyConfiguration Safety` | Copia di lavoro delle soglie di sicurezza (form Admin) — vedi / . |
| `p` | `bool SafetyReachable` | Falso quando il motore remoto non ha risposto all'ultima lettura: i valori nel form sono i DEFAULT, non le soglie applicate — e il pannello lo deve dire invece di spacciarli per vere. |
| `p` | `string? SafetyError` | Motivo dell'irraggiungibilità quando è falso. |
| `p` | `string? SafetySource` | Sorgente prevalente della sezione ("appsettings.json", "variabili d'ambiente"…), per spiegare perché salvare può non bastare. |
| `p` | `bool SafetyIsRemote` | Vero se le soglie vivono in un altro processo (cambia solo cosa dire all'operatore). |

### 🧾 `LaneStoryStrategy` `(`

> La "carta d'identità" della corsia: cosa gira, con che aspettative, da dove viene.

### 🧾 `LaneStoryInfo` `(`

| | Firma | Descrizione |
|---|---|---|
| `p` | `LaneStoryInfo? Story` | — |
| `p` | `List&lt;Data.OhlcvData&gt; ChartCandles` | Candele per il grafico prezzi+operazioni (ultime ~300 del simbolo/timeframe della corsia). |
| `m` | `Task LoadLaneStoryAsync(int laneId, CancellationToken ct = default)` | [2026-08-03, richiesta proprietario] Carica la storia della corsia (configurazione con le aspettative + provenienza dal journal della flotta — lo stesso testo delle proposte Telegram) e le candele per il grafico delle o… |
| `m` | `Task ClearLaneAsync(int laneId, CancellationToken ct = default)` | [2026-08-03] Svuota la configurazione di una corsia FERMA (mai la rimuove: l'id è identità — posizioni e storico vi restano agganciati, ed è la lezione delle posizioni orfane di luglio). La corsia torna "non configurata… |
| `p` | `TradingPerformance? Perf` | [2026-08-03] Perf del TEST CORRENTE (da StartedAtUtc): la stessa base della decisione di promozione. I KPI della pagina la usano al posto dei totali di Status, che sommano tutte le vite precedenti della corsia (corsia 0… |
| `m` | `Task RefreshAsync(int laneId)` | — |
| `m` | `Task ClearQuarantineAsync(int laneId, string? userId)` | Rimozione della quarantena (solo Admin, dopo verifica): audit con lo userId di chi decide. |
| `m` | `Task RefreshPromotionsAsync()` | — |
| `m` | `Task PromoteAsync(int laneId, TradingMode newMode, int currentlyViewedLaneId)` | — |
| `m` | `Task StartAsync(int laneId, TradingMode mode)` | — |
| `m` | `Task StopAsync(int laneId)` | — |
| `m` | `Task EmergencyAsync(int laneId)` | — |
| `m` | `Task CloseAsync(int laneId, string positionId)` | — |
| `m` | `string? SlValue(OpenPosition p)` | — |
| `m` | `string? TpValue(OpenPosition p)` | — |
| `m` | `string? TslValue(OpenPosition p)` | — |
| `m` | `void SetSlEdit(string id, string? raw)` | — |
| `m` | `void SetTpEdit(string id, string? raw)` | — |
| `m` | `void SetTslEdit(string id, string? raw)` | — |
| `m` | `decimal? ParseLevel(string? raw)` | — |
| `m` | `Task SaveSlTpAsync(int laneId, string positionId)` | — |
| `m` | `Task ConfirmAsync(int laneId, string orderId, string? userId)` | — |
| `m` | `Task RejectAsync(int laneId, string orderId, string? userId)` | — |
| `m` | `Task ReloadSafetyAsync(CancellationToken ct = default)` | — |
| `m` | `Task SaveSafetyAsync()` | — |

## `ProcioneMGR/Services/Trading/TradingServiceCollectionExtensions.cs`

### 📦 `TradingServiceCollectionExtensions`

> Composizione DI delle corsie di trading (LaneId 0.. -1). Estratta da Program.cs per essere riusata verbatim dal servizio standalone ProcioneMGR.Trading (Fase 2b microservizi). È QUI che vive la garanzia di sicurezza centrale della Fase 2b: il vincolo "mai due esecuzioni simultanee sulla stessa corsia" non è retto da un lock distribuito ma dalla REGISTRAZIONE CONDIZIONALE — con Trading:UseRemoteTrading=true il monolite non registra alcun / / locale, e l'unico processo che esegue ordini è procionemgr-trading (replicas:1 + Recreate, tutte e 3 le lane in-process: il per-istanza del motore resta quindi sufficiente). I due insiemi sono mutuamente esclusivi per costruzione, non per convenzione — vedi TradingServiceCollectionExtensionsTests. Lo stesso ragionamento vale per ogni componente che SCRIVE: l' resta del solo monolite (vedi isTradingServiceHost ). La regola generale di questa fase è ch…

## `ProcioneMGR/Services/Trading/TradingWorker.cs`

### 📦 `TradingWorker` `(`

> Guida il trading engine alimentandolo con le candele. Quando l'engine viene avviato (nuova sessione), riproduce progressivamente le ultime ReplayDays giornate di dati storici (a piccoli batch per tick) così l'attività è osservabile in tempo reale nella UI; una volta raggiunto il presente, elabora le nuove candele man mano che arrivano dal MarketDataSyncWorker.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `DateTime? LastClosedBarOpenUtc(string timeframe, DateTime nowUtc)` | [2026-08-06] L'istante di APERTURA dell'ultima barra che ha già chiuso. Null se il timeframe non è riconosciuto. Il guasto che questa funzione chiude , trovato dal proprietario il 2026-08-06 sulla corsia 3: uno short ET… |

# `Services/Execution/`

## `ProcioneMGR/Services/Execution/ExecutionAlgorithmFactory.cs`

### 🔌 `IExecutionAlgorithmFactory`

> Crea gli algoritmi di esecuzione per nome ed espone l'elenco per la UI. Stesso pattern di StrategyFactory / AlphaFactorFactory : aggiungere un algoritmo = nuova classe + un case.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IExecutionAlgorithm&gt; All` | — |
| `m` | `IExecutionAlgorithm Create(string name)` | Ritorna l'algoritmo richiesto; "Immediate" come fallback retrocompatibile per nomi ignoti. |

### 📦 `ExecutionAlgorithmFactory` `: IExecutionAlgorithmFactory`

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;IExecutionAlgorithm&gt; All` | — |
| `m` | `IExecutionAlgorithm Create(string name)` | — |

## `ProcioneMGR/Services/Execution/ExecutionAlgorithms.cs`

### 📦 `ExecutionPlanning`

> Helper condivisi per costruire piani con somma quantità esatta.

### 📦 `ImmediateExecutionAlgorithm` `: IExecutionAlgorithm`

> ESECUZIONE IMMEDIATA: un solo ordine per l'intera quantità, alla prima candela. Riproduce il comportamento ODIERNO della piattaforma (nessuno slicing) ed è il default retrocompatibile.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList&lt;OhlcvData&gt; fineGrainedCandles, ExecutionParameters parameters)` | — |

### 📦 `TwapExecutionAlgorithm` `: IExecutionAlgorithm`

> TWAP (Time-Weighted Average Price): fette uguali distribuite uniformemente nel tempo lungo la finestra di esecuzione. Riduce l'impatto di mercato di size grandi spargendo l'ordine.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList&lt;OhlcvData&gt; fineGrainedCandles, ExecutionParameters parameters)` | — |

### 📦 `VwapExecutionAlgorithm` `: IExecutionAlgorithm`

> VWAP (Volume-Weighted Average Price): quantità proporzionale al profilo di volume delle candele fini — concentra dove c'è più liquidità, minimizzando la partecipazione (e quindi l'impatto). In backtest usa il volume realizzato della finestra; nel live si userebbe un profilo storico.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList&lt;OhlcvData&gt; fineGrainedCandles, ExecutionParameters parameters)` | — |

### 📦 `IcebergExecutionAlgorithm` `: IExecutionAlgorithm`

> ICEBERG: mostra solo un "clip" fisso per volta e lo rimpiazza finché la quantità totale è esaurita. Nasconde la size reale distribuendola in ordini figli piccoli e sequenziali nel tempo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList&lt;OhlcvData&gt; fineGrainedCandles, ExecutionParameters parameters)` | — |

### 📦 `AdaptiveExecutionAlgorithm` `: IExecutionAlgorithm`

> ADATTIVO (Almgren-Chriss semplificato, NON appreso — deliberatamente scartato un agente RL: si sarebbe allenato contro il nostro stesso simulatore d'impatto illustrativo (√-partecipazione, legge di Almgren, vedi ExecutionSimulator — era lineare prima di E1), imparando i suoi artefatti invece della dinamica reale del mercato — rischio di "sim-to-real gap" e overfitting documentato in letteratura per questo esatto ambito). Come VWAP ma pesa MOLTIPLICATIVAMENTE il profilo di volume con un decadimento esponenziale nel tempo la cui intensità dipende dalla volatilità realizzata del profilo: più alta la volatilità, più front-loaded l'esecuzione — si riduce l'esposizione al RISCHIO di prezzo nel tempo (non il costo di impatto simulato, che nel modello di fill dipende solo dalla √ della partecipazione al volume, mai da volatilità). Degrada a VWAP quando la volatilità è nulla (mercato piatto: dec…

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | — |
| `m` | `ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList&lt;OhlcvData&gt; fineGrainedCandles, ExecutionParameters parameters)` | — |

## `ProcioneMGR/Services/Execution/ExecutionModels.cs`

### 🔢 `ExecutionSide`

> Direzione di un ordine di esecuzione (disaccoppiata da OrderSide del layer Trading).

### 🧾 `ExecutionIntent` `(`

> "Intenzione" di ordine decisa al timeframe di DECISIONE (es. una barra 4h): symbol, lato, quantità totale e prezzo di arrivo (il prezzo di riferimento all'istante della decisione, contro cui si misura l'implementation shortfall). Non contiene ancora COME eseguire: è l'input degli che producono un piano sul timeframe di ESECUZIONE (es. 5m).

### 🧾 `ExecutionSlice` `(int CandleIndex, decimal Quantity);`

> Un ordine figlio: quantità da eseguire nella candela fine di indice .

### 🔢 `MarketImpactModel`

> Forma del modello di impatto di mercato in funzione della partecipazione al volume.

### 📦 `ExecutionPlan`

> Piano di esecuzione: la sequenza di ordini figli prodotta da un algoritmo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Algorithm` | — |
| `p` | `IReadOnlyList&lt;ExecutionSlice&gt; Slices` | — |
| `p` | `decimal PlannedQuantity` | — |
| `p` | `int SliceCount` | — |

### 📦 `ExecutionParameters`

> Parametri di esecuzione (algoritmi + modello di fill del simulatore). I valori di default sono illustrativi: l'impatto di mercato reale va calibrato/validato in Paper (rif. ROADMAP-QLIB §1.2), non assunto. Il modello di impatto (default √partecipazione, cfr. ) dipende dalla "partecipazione" (quota del volume di candela assorbita dall'ordine figlio), premiando la distribuzione dell'ordine nel tempo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `int MaxSlices` | Numero massimo di fette per TWAP/VWAP. |
| `p` | `decimal IcebergClipFraction` | Dimensione del clip Iceberg come frazione della quantità totale. |
| `p` | `decimal ImpactCoefficient` | Impatto di mercato per unità di partecipazione (quota del volume di candela). |
| `p` | `MarketImpactModel ImpactModel` | Forma del modello di impatto (default √partecipazione, la legge empirica di Almgren). |
| `p` | `decimal MaxImpactPct` | Tetto all'impatto di una singola fetta (evita valori assurdi su candele a volume nullo). |
| `p` | `decimal HalfSpreadPct` | Costo fisso di attraversamento dello spread per fill (metà spread). |
| `p` | `decimal ReferenceVolatility` | Volatilità di riferimento (deviazione standard dei log-return) usata da Adaptive per calibrare l'urgenza: sigma_realizzata/ReferenceVolatility &gt; 1 ⇒ mercato più volatile del normale ⇒ esecuzione più front-loaded. Val… |
| `p` | `decimal DecayBaseRate` | Tasso di decadimento base per Adaptive, moltiplicato per l'urgency ratio clampato in [0.25, 4.0] per ottenere il lambda effettivo del peso esponenziale. A urgency=1 (volatilità pari al riferimento) e lambda=0.15, il rap… |

### 🧾 `ExecutionFill` `(int CandleIndex, decimal Quantity, decimal Price, decimal ParticipationPct);`

> Un fill simulato di una fetta.

### 📦 `ExecutionResult`

> Esito della simulazione di un piano: prezzo medio di riempimento e implementation shortfall (scostamento dal prezzo di arrivo, segnato come COSTO: positivo = peggio dell'arrivo).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Algorithm` | — |
| `p` | `decimal FilledQuantity` | — |
| `p` | `decimal AverageFillPrice` | — |
| `p` | `decimal ArrivalPrice` | — |
| `p` | `decimal SlippageBps` | Implementation shortfall in punti base (bps), segnato come costo per il lato dell'ordine. |
| `p` | `IReadOnlyList&lt;ExecutionFill&gt; Fills` | — |

## `ProcioneMGR/Services/Execution/ExecutionSimulator.cs`

### 🔌 `IExecutionSimulator`

> Simula il riempimento di un sulle candele fini, con impatto di mercato e spread, invece di assumere un fill istantaneo a chiusura candela (assunzione odierna del BacktestEngine ). Serve a MISURARE la differenza fra algoritmi (Immediate vs TWAP/VWAP/ Iceberg) sugli stessi dati — la premessa del 10-20% di miglioramento va misurata qui, non assunta (rif. docs/archive/ROADMAP-QLIB.md §1.2 ). Puro/deterministico.

### 📦 `ExecutionSimulator` `: IExecutionSimulator`

## `ProcioneMGR/Services/Execution/IExecutionAlgorithm.cs`

### 🔌 `IExecutionAlgorithm`

> Algoritmo di esecuzione: dato un ordine "intenzione" ( ) e le candele del timeframe di esecuzione (es. 5m dentro una barra di decisione 4h), produce un di ordini figli. È il layer che oggi manca fra "la strategia decide" e "l'ordine parte" (rif. docs/archive/ROADMAP-QLIB.md §1.2 ). riproduce il comportamento ODIERNO (un solo ordine) ed è il default retrocompatibile; TWAP/VWAP/Iceberg distribuiscono l'ordine per ridurre l'impatto di mercato. Puri/stateless → registrabili come Singleton.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Name` | Nome tecnico: "Immediate" \| "Twap" \| "Vwap" \| "Iceberg". |
| `m` | `ExecutionPlan BuildPlan(ExecutionIntent intent, IReadOnlyList&lt;OhlcvData&gt; fineGrainedCandles, ExecutionParameters parameters)` | Costruisce il piano di ordini figli. Ogni implementazione garantisce che la somma delle quantità delle fette sia ESATTAMENTE (nessuna quantità persa o creata per arrotondamento) e che ogni indice di candela sia valido. |

# `Services/Risk/`

## `ProcioneMGR/Services/Risk/BotPageService.cs`

### 📦 `BotPageService` `(`

> [R3] Orchestrazione della Modalità Semplice ( Components/Pages/Bot.razor ). Stessa divisione di responsabilità di : qui vivono le chiamate ai servizi e lo stato che ne deriva, così la logica è testabile senza Blazor; il componente resta responsabile solo di rendering e ciclo di vita. LA MODALITÀ SEMPLICE È UNA VISTA, NON UN MOTORE PARALLELO. Opera su una corsia esistente attraverso gli stessi e della pagina /trading: nessun percorso alternativo verso l'exchange, nessun controllo di sicurezza scavalcato. Ciò che semplifica è la SCELTA (capitale + profilo invece di dodici soglie), non l'esecuzione. Registrato Scoped: in Blazor Server uno scope = un circuito.

| | Firma | Descrizione |
|---|---|---|
| `k` | `int BotLaneId` | La Modalità Semplice governa la corsia 0. Le altre restano alla pagina /trading per l'uso esperto: una vista "un pulsante" che gestisse tre corsie non sarebbe più semplice. |
| `k` | `decimal RoundTurnPercent` | Round-turn usato per la stima dei costi mostrata all'utente: fee 0,1%/lato + slippage 0,05%/fill, gli stessi di R2. |
| `p` | `decimal Capital` | — |
| `p` | `string ProfileName` | — |
| `p` | `RiskProfile Profile` | — |
| `p` | `TradingEngineStatus? Status` | — |
| `p` | `List&lt;OpenPosition&gt; Positions` | — |
| `p` | `List&lt;TradeRecord&gt; RecentTrades` | — |
| `p` | `string? StrategySummary` | Cosa la corsia è configurata per operare, in una riga leggibile. Null se non c'è nulla. |
| `p` | `string? ConfiguredTimeframe` | Timeframe realmente configurato in corsia: può divergere da quelli preferiti dal profilo. |
| `p` | `Guid? LatestApplicableRunId` | Run di ricerca più recente con un ensemble applicabile, se la corsia è vuota. |
| `p` | `bool HasStrategies` | — |
| `p` | `bool IsRunning` | — |
| `p` | `bool Busy` | — |
| `p` | `string? Message` | — |
| `p` | `bool IsError` | — |
| `p` | `bool TimeframeMismatch` | Il profilo scelto preferisce timeframe diversi da quello effettivamente in corsia. Non è un errore — il tetto di operazioni del profilo protegge comunque — ma va detto: una strategia a 15m sotto un profilo Prudente verr… |
| `m` | `Task LoadAsync(CancellationToken ct = default)` | — |
| `m` | `Task RefreshAsync(CancellationToken ct = default)` | Aggiorna solo lo stato osservato: chiamato dal polling, non deve toccare il form. |
| `m` | `Task SaveAsync(CancellationToken ct = default)` | Salva capitale e profilo sulla corsia. Non avvia nulla. |
| `m` | `Task StartAsync(CancellationToken ct = default)` | Salva e avvia in PAPER. Mai in Testnet o Live: il passaggio a denaro reale resta un'azione esplicita dalla pagina /trading, dietro i controlli che già esistono. Una vista "un pulsante" non deve poter avviare operatività… |
| `m` | `Task StopAsync(CancellationToken ct = default)` | — |
| `m` | `Task ApplyLatestResearchAsync(CancellationToken ct = default)` | Schiera sulla corsia l'ensemble dell'ultima ricerca completata. Scrive SOLO configurazione: non avvia trading (stessa garanzia di ). |

## `ProcioneMGR/Services/Risk/CorrelatedExposureGuard.cs`

### 🧾 `CorrelatedExposureAssessment` `(`

> Esito della valutazione di esposizione correlata per una candidata apertura. False quando manca il necessario per una misura onesta (storico insufficiente, capitale non determinabile). In quel caso il guard NON blocca: vedi la nota sul fail-safe in . Esposizione correlata NETTA e con segno (positiva = sbilanciata al rialzo). È la somma del nozionale candidato più quello delle posizioni già aperte, ciascuna pesata per la sua correlazione col simbolo candidato.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Exceeds` | True se questa apertura porterebbe l'esposizione correlata oltre il limite. |
| `m` | `CorrelatedExposureAssessment NotMeasurable(string reason)` | — |

### 🧾 `CorrelatedExposureContribution` `(int LaneId, string Symbol, double Correlation, decimal SignedNotional)`

> Contributo di una singola posizione aperta all'esposizione correlata.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal WeightedNotional` | Quota di questa posizione che "vale come" esposizione sul simbolo candidato. |

### 📦 `CorrelatedExposureOptions`

> Opzioni del limite di esposizione correlata. Default SPENTO.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Interruttore generale. Default FALSE: la funzione va prima calibrata sui dati delle corsie realmente attive, perché una soglia scelta male non protegge — paralizza. |
| `p` | `decimal MaxCorrelatedExposurePercent` | Tetto dell'esposizione correlata netta, in % del capitale aggregato delle corsie nella stessa modalità. Il default coincide col tetto di esposizione totale per singola corsia ( ): un insieme di posizioni che si muovono … |
| `p` | `double MinCorrelationToCount` | Sotto questa correlazione (in valore assoluto) una posizione è trattata come indipendente e non contribuisce. Serve a non accumulare rumore da decine di correlazioni spurie piccole. |
| `p` | `string Timeframe` | Timeframe delle barre su cui si stima la correlazione. |
| `p` | `int LookbackBars` | Numero di barre della finestra di stima (720 barre 1h ≈ 30 giorni). |
| `p` | `int MinOverlappingBars` | Barre in comune minime perché una correlazione sia considerata stimabile. |
| `p` | `TimeSpan CacheTtl` | Validità della correlazione in cache: oltre, si ricalcola. |

### 🔌 `ICorrelatedExposureGuard`

> Valuta l'esposizione correlata di una candidata apertura. Vedi .

### 📦 `CorrelatedExposureGuard` `(`

> [Fase 2 — docs/archive/ROADMAP-ARCHITETTURE-ESECUZIONE.md] Limite di esposizione su posizioni CORRELATE. Tutti i limiti di rischio a runtime erano scalari e ciechi alla correlazione: tetto sulla singola posizione, tetto sull'esposizione totale di una corsia, numero massimo di posizioni aperte. Tre corsie che aprono long su tre altcoin ad alta correlazione con BTC contavano quindi come tre scommesse indipendenti mentre erano, in sostanza, una sola scommessa di taglia tripla — ed è esattamente nei crash, quando le correlazioni crypto tendono a 1, che quella distinzione conta. La matematica per accorgersene esisteva già in piattaforma ( ), ma viveva solo nella ricerca: mai nel percorso decisionale. Somma con segno, non in valore assoluto. Due long correlati sommano rischio, un long e uno short correlati lo compensano: pesare per ρ mantenendo il segno del nozionale è ciò che rende la misura…

## `ProcioneMGR/Services/Risk/KellyCalculator.cs`

### 📦 `KellyCalculator`

> Criterio di Kelly per il position sizing (Jansen ML4T, cap. 5): la frazione di capitale da impegnare che massimizza la crescita logaritmica della ricchezza a lungo termine. - Caso binario (dai trade): f* = p - (1-p)/b, con p = probabilita' di vincita e b = payoff ratio (guadagno medio / perdita media). - Caso continuo (dai rendimenti, approssimazione normale): f* = mu / sigma^2; in alternativa la massimizzazione numerica di E[log(1+f*r)] sotto Normal(mu, sigma). - Multi-asset (Chan 2008): w = Sigma^-1 * mu, equivalente al portafoglio max-Sharpe (potenzialmente a leva), poi normalizzato. In pratica si usa una FRAZIONE del Kelly pieno (half-Kelly): il Kelly pieno e' ottimo solo se le stime di p/b o mu/sigma sono esatte — non lo sono mai, e sbagliare per eccesso costa piu' che sbagliare per difetto.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal BinaryKelly(decimal winProbability, decimal payoffRatio)` | Kelly binario: f* = p - (1-p)/b. Ritorna 0 se l'edge e' negativo o b non valido. |
| `m` | `KellySuggestion FromTradeHistory(IReadOnlyList&lt;BacktestTrade&gt; trades)` | Kelly binario dalla lista dei trade di un backtest: p = percent win, b = \|guadagno medio\| / \|perdita media\|. |
| `m` | `decimal ContinuousKelly(decimal meanReturn, decimal returnStdDev)` | Kelly continuo in forma chiusa (approssimazione normale): f* = mu / sigma^2. mu e sigma sono per periodo (stessa periodicita' dei rendimenti passati). |
| `m` | `double ContinuousKellyNumeric(double mean, double std, double maxFraction = 2.0)` | Kelly continuo numerico (l'approccio esatto del libro): massimizza E[log(1+f*r)] con r ~ Normal(mean, std), integrale su [mean-3*std, mean+3*std] (Simpson) e ricerca golden-section di f in [0, maxFraction]. |
| `m` | `double EmpiricalKelly(IReadOnlyList&lt;double&gt; returns, double maxFraction = 2.0)` | Kelly EMPIRICO (robusto alle code grasse): massimizza la crescita logaritmica attesa G(f) = media su i di log(1 + f·rᵢ) usando la distribuzione EMPIRICA dei rendimenti osservati, senza assumere normalità. Quando i dati … |
| `m` | `IReadOnlyList&lt;decimal&gt; MultiAssetKelly(IReadOnlyList&lt;IReadOnlyList&lt;double&gt;&gt; returnsByAsset)` | Kelly multi-asset (Chan): w = Sigma^-1 * mu sui rendimenti storici allineati per colonna (un asset per colonna), normalizzato perche' la somma dei \|pesi\| faccia 1. Equivale al portafoglio max-Sharpe non vincolato; pes… |

### 🧾 `KellySuggestion` `(`

> Frazione di Kelly suggerita dai trade storici (con la meta' prudenziale).

## `ProcioneMGR/Services/Risk/LeverageAdvisor.cs`

### 📦 `LeverageAdvisor`

> Consulente per la leva: dati i trade di un backtest a leva 1, simula per bootstrap (ricampionamento con reimmissione, stile Montecarlo evoluta) migliaia di sequenze di trade a diversi livelli di leva e misura cio' che conta davvero per un capitale piccolo: - crescita mediana del capitale (non la media, gonfiata dalle code fortunate); - probabilita' di DIMEZZARE il capitale almeno una volta lungo il percorso; - probabilita' di ROVINA (equity sotto una soglia di sopravvivenza); - frequenza di liquidazioni (perdita del margine su un singolo trade). La leva consigliata e' la piu' alta con P(dimezzamento) sotto la tolleranza richiesta: la leva ottima per la crescita (Kelly) e' quasi sempre PIU' BASSA di quella che sembra attraente — oltre, la crescita mediana CROLLA anche se la media sale.

### 🧾 `LeverageScenario` `(`

> Esito della simulazione per un singolo livello di leva.

### 🧾 `LeverageAdvice` `(`

> Tabella degli scenari + leva consigliata.

## `ProcioneMGR/Services/Risk/MarginMath.cs`

### 📦 `MarginMath`

> Matematica del margine isolato (leva/liquidazione), condivisa tra il motore di backtest ( BacktestEngine.Portfolio ) e il trading live a futures ( TradingEngine ), così il prezzo di liquidazione STIMATO in un contesto e quello nell'altro sono calcolati con la STESSA formula — nessun rischio che backtest e live disegnino un rischio diverso per lo stesso trade. Funzioni pure, nessuna dipendenza da I/O o da altri servizi. Nota: per una posizione REALE su un exchange, il prezzo di liquidazione autoritativo è quello riportato dall'exchange stesso (include fondo assicurativo, mark price vs last price, eventuali fee di liquidazione) — queste formule sono una stima prudente usata per il monitoraggio locale e per i pre-check di sicurezza PRIMA di interrogare l'exchange.

| | Firma | Descrizione |
|---|---|---|
| `m` | `decimal LiquidationDistanceFraction(decimal leverage, decimal maintenanceMarginFraction)` | Distanza dalla liquidazione come frazione del prezzo di ingresso (sempre positiva se la leva è sostenibile): quanto può muoversi il prezzo, in %, prima della liquidazione. Dipende solo da leva e margine di mantenimento,… |

## `ProcioneMGR/Services/Risk/MonteCarloAnalyzer.cs`

### 📦 `MonteCarloAnalyzer`

> Montecarlo Analysis "evoluta" (Trombetta, cap. 8): ricombina casualmente la lista dei trade per stimare la distribuzione dei draw down possibili, oltre a quello storico. Rispetto alla Montecarlo classica aggiunge tre leve: 1. costi extra per appesantire la curva (stress dei costi fissi/slippage); 2. rumore casuale proporzionale al singolo trade (generalizza i risultati); 3. ricombinazione di un SOTTOINSIEME dei trade (distribuzione del valore atteso). L'output chiave e' il draw down al 95esimo percentile della distribuzione: e' il livello di guardia consigliato per lo spegnimento del sistema (tipicamente 1.5-2.5 volte il max draw down storico). Deterministico a parita' di .

| | Firma | Descrizione |
|---|---|---|
| `m` | `MonteCarloResult Run(IReadOnlyList&lt;decimal&gt; tradePnls, MonteCarloConfig config)` | — |

### 🔢 `MonteCarloSamplingMode`

> Come vengono ricombinati i trade a ogni shuffle.

### 🧾 `MonteCarloConfig`

> Parametri della Montecarlo Analysis evoluta.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal ExtraCostPerTrade` | Costo extra per lato imputato a ogni trade (applicato x2, round turn). |
| `p` | `decimal NoisePercent` | Rumore casuale max, in % del valore nominale del singolo trade (0 = disattivo). |
| `p` | `decimal OperationsPercent` | Percentuale dei trade da ricombinare a ogni shuffle (100 = tutti). |
| `p` | `int NumberOfShuffles` | — |
| `p` | `int? Seed` | Seed per risultati riproducibili (null = casuale). |
| `p` | `MonteCarloSamplingMode SamplingMode` | Default = comportamento storico invariato. |
| `p` | `int MeanBlockLength` | Lunghezza media dei blocchi nel modo . |

### 🧾 `MonteCarloResult`

> Esito della Montecarlo Analysis evoluta.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;decimal&gt; OriginalEquity` | — |
| `p` | `decimal OriginalMaxDrawdown` | — |
| `p` | `IReadOnlyList&lt;decimal&gt; WorstEquity` | — |
| `p` | `IReadOnlyList&lt;decimal&gt; BestEquity` | — |
| `p` | `decimal WorstMaxDrawdown` | — |
| `p` | `decimal BestMaxDrawdown` | — |
| `p` | `decimal MaxDrawdown95` | 95esimo percentile dei max draw down: livello di guardia consigliato. |
| `p` | `decimal RiskFactor95` | MaxDrawdown95 / draw down storico (atteso tipicamente tra 1.5 e 2.5). |
| `p` | `decimal RiskFactorWorst` | — |
| `p` | `IReadOnlyList&lt;decimal&gt; SortedMaxDrawdowns` | Distribuzione ordinata (crescente) dei max draw down delle ricombinazioni. |

## `ProcioneMGR/Services/Risk/PerformanceControlService.cs`

### 📦 `PerformanceControlService`

> Controllo dinamico del rischio (Trombetta, cap. 8): inibisce e riattiva una strategia in base allo stato di salute della sua equity line, pagando un "premio di assicurazione" in profitto in cambio di draw down piu' contenuti. Due modalita' implementate: - Performance Control (metrico): profitto a finestra scorrevole degli ultimi N trade; se scende sotto la soglia, i trade successivi vengono saltati finche' la metrica (sempre calcolata sui trade ORIGINALI) non risale sopra la soglia. - Equity Control (grafico): media mobile semplice sull'equity dei trade originali; si opera solo quando l'equity e' sopra la propria media. In entrambi i casi il segnale e' valutato sul trade PRECEDENTE (nessun look-ahead: la decisione di eseguire il trade i usa solo informazioni fino a i-1).

### 🧾 `EquityControlResult`

> Confronto tra la curva originale e quella controllata (cap. 8 del libro).

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;bool&gt; ExecutedFlags` | Per ogni trade originale: true se la curva controllata lo avrebbe eseguito. |
| `p` | `IReadOnlyList&lt;decimal&gt; OriginalEquity` | — |
| `p` | `IReadOnlyList&lt;decimal&gt; ControlledEquity` | — |
| `p` | `int OriginalTradeCount` | — |
| `p` | `int ControlledTradeCount` | — |
| `p` | `decimal OriginalProfit` | — |
| `p` | `decimal ControlledProfit` | — |
| `p` | `decimal OriginalMaxDrawdown` | — |
| `p` | `decimal ControlledMaxDrawdown` | — |
| `p` | `decimal OriginalAvgDrawdown` | — |
| `p` | `decimal ControlledAvgDrawdown` | — |
| `p` | `TradeReport OriginalReport` | Report completi per il confronto metrico puntuale (stile Figura 8.24). |
| `p` | `TradeReport ControlledReport` | — |
| `p` | `decimal ProfitRetention` | Quota di profitto conservata dalla curva controllata (1 = nessuna perdita). |
| `p` | `decimal MaxDrawdownRatio` | Quota di max draw down rispetto all'originale (&lt; 1 = rischio ridotto). |

## `ProcioneMGR/Services/Risk/RiskProfile.cs`

### 🧾 `RiskProfile` `(`

> [R3] Profilo di rischio: l'UNICA scelta tecnica che la Modalità Semplice chiede all'utente, insieme al capitale. COSA UN PROFILO È: un insieme di VINCOLI — quanto capitale per posizione, quanta esposizione totale, quanta perdita si tollera, e soprattutto QUANTO SPESSO si opera. COSA UN PROFILO NON È: una scelta di strategia. Il PDF di partenza proponeva di mappare "aggressivo → scalping", "prudente → DCA". In questa piattaforma le strategie sono un OUTPUT verificato di discovery → walk-forward → gate anti-overfitting (Deflated Sharpe, PBO): lasciare che un profilo scelga la strategia scavalcherebbe proprio la macchina che protegge dall'overfitting. Il profilo decide quanto si rischia, non cosa si compra. PERCHÉ IL TURNOVER È IL PARAMETRO PRINCIPALE. La misura di R2 ( docs/REPORT-1M-COSTI-R2.md ) ha stabilito che il costo dell'operatività è funzione del turnover, non della risoluzione de…

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal MaxTradesPerDay` | Tetto di operazioni complete al giorno implicato da . Un giro completo costa DUE ordini (apertura + chiusura), quindi il divisore è 2×intervallo. NB sul perché intervalli di ore non sono pericolosi: il è invocato SOLO s… |
| `m` | `decimal EstimatedAnnualCostPercent(decimal roundTurnPercent)` | Costo annuo stimato in % del capitale se il profilo operasse SEMPRE al suo tetto di turnover. È un tetto, non una previsione: quasi nessuna strategia satura il proprio limite. Serve a rendere visibile all'utente la lezi… |
| `m` | `SafetyConfiguration Apply(SafetyConfiguration global)` | Sovrappone il profilo alla configurazione globale. La divisione delle responsabilità è deliberata: il PROFILO possiede l'appetito al rischio (dimensioni, esposizione, perdite tollerate, frequenza, leva); la configurazio… |

### 📦 `RiskProfiles`

> I profili offerti dalla Modalità Semplice. Tre, non dieci: la scelta deve essere fattibile da chi non conosce il dominio. I tetti di turnover sono calibrati sui numeri misurati in R2, non sull'intuizione. Là le uniche configurazioni con un cost drag tollerabile (3,4% su sei mesi) giravano a ~0,6 operazioni al giorno; a ~5/giorno il drag era già 24%, a ~28/giorno il 77%. Da qui la scala **0,5 / 0,75 / 1,5 operazioni al giorno**, che circonda il valore misurato invece di superarlo di un ordine di grandezza. Una prima stesura proponeva 3 / 6 / 12 al giorno: la formula del costo annuo, girata su quei valori, dava 16% / 66% / 131% l'anno di sole commissioni — cioè profili che perdono per costruzione. La formula è validata contro R2: 0,57 trade/giorno al 10% di size dà 6,2%/anno, contro il 3,43% su sei mesi effettivamente misurato. INVARIANTE che ogni profilo DEVE rispettare (validata all'avv…

| | Firma | Descrizione |
|---|---|---|
| `k` | `string Prudente` | — |
| `k` | `string Equilibrato` | — |
| `k` | `string Dinamico` | — |
| `p` | `RiskProfile Conservative` | — |
| `p` | `RiskProfile Balanced` | — |
| `p` | `RiskProfile Dynamic` | — |
| `p` | `IReadOnlyList&lt;RiskProfile&gt; All` | — |
| `p` | `RiskProfile Default` | Il profilo predefinito quando l'utente non ha ancora scelto. |
| `m` | `RiskProfile? Find(string? name)` | Profilo per nome, oppure null se il nome è vuoto o sconosciuto. Il null NON è un errore: significa "questa corsia non usa la Modalità Semplice" e le soglie restano quelle globali — cioè il comportamento di ogni corsia e… |

# `Services/Carry/`

## `ProcioneMGR/Services/Carry/CarryBacktestEngine.cs`

### 📦 `CarryBacktestEngine`

> [E3 roadmap profitto-intraday] Backtest DETERMINISTICO del carry delta-neutro (long spot + short perp). Itera sugli EVENTI DI FUNDING (il passo naturale del carry, ogni 8h): a ogni evento, se in posizione, lo short INCASSA il funding firmato (positivo → income; negativo → costo); la decisione di aprire/chiudere usa la media annualizzata degli ultimi TrailingFundingEvents eventi, con isteresi enter&gt;exit. I costi delle DUE gambe (fee+slippage spot e perp) si pagano all'apertura e alla chiusura. Delta-neutralità e semplificazione dichiarata. Con long spot e short perp allo stesso nozionale sullo stesso sottostante, la componente DIREZIONALE del prezzo si elide: quel che resta è funding − costi. La BASE (differenza spot/perp) e il suo drift sono un rischio del second'ordine REALE che questo backtest — che vede solo la serie funding, non le due serie prezzo separate — NON modella. Va dich…

| | Firma | Descrizione |
|---|---|---|
| `m` | `CarryBacktestResult Run(IReadOnlyList&lt;FundingRatePoint&gt; funding, CarryConfiguration config)` | — |

## `ProcioneMGR/Services/Carry/CarryDecider.cs`

### 🔢 `CarryAction`

> Cosa fare al prossimo punto di decisione del carry.

### 📦 `CarryDecider`

> [E3] La REGOLA DI DECISIONE del carry, pura e UNICA: la usano sia il sia il motore live, così backtest e operatività non possono divergere. Isteresi: si entra sopra la soglia di ingresso, si esce sotto quella di uscita (che è più bassa), e fra le due non si fa nulla — niente ping-pong attorno a una singola soglia.

| | Firma | Descrizione |
|---|---|---|
| `m` | `CarryAction Decide(decimal annualizedFundingPercent, bool inPosition, CarryConfiguration config)` | — |
| `m` | `decimal? TrailingAnnualized(IReadOnlyList&lt;decimal&gt; ratesPercentPer8h, int trailing, int eventsPerDay)` | Funding annualizzato dalla media degli ultimi rate (% per 8h). null se i punti non bastano (finestra non piena) → il chiamante non decide (Hold). |

## `ProcioneMGR/Services/Carry/CarryEngine.cs`

### 🔢 `CarryMode`

> Modalità operativa del carry. Contiene DELIBERATAMENTE solo Paper e Testnet: Live è IRRAPPRESENTABILE — non esiste il valore, quindi nessun percorso di codice, nessuna config, nessun bug può portare il carry a operare con denaro reale. È il failsafe più forte possibile (più forte di un controllo a runtime): ciò che non si può esprimere non può accadere.

### 🧾 `CarryLegOrder` `(string Symbol, bool IsPerp, bool IsBuy, decimal NotionalQuote);`

> Una gamba desiderata del carry (spot o perp), con lato e nozionale.

### 🧾 `CarryExecutionResult` `(bool Success, string Message);`

> Esito dell'esecuzione di un'apertura/chiusura a due gambe.

### 🔌 `ICarryExecutor`

> Astrazione dell'esecuzione a due gambe: la implementa il Paper (registra e basta) e il Testnet (ordini Bitget demo). Il decide COSA fare; l'executor decide COME, e solo l'executor tocca l'exchange — così la logica di decisione è testabile senza rete.

| | Firma | Descrizione |
|---|---|---|
| `p` | `CarryMode Mode` | — |
| `m` | `Task&lt;CarryExecutionResult&gt; OpenAsync(string symbol, decimal notionalQuote, CancellationToken ct)` | Apre il carry: long spot + short perp allo stesso nozionale. Le due gambe insieme. |
| `m` | `Task&lt;CarryExecutionResult&gt; CloseAsync(string symbol, decimal notionalQuote, CancellationToken ct)` | Chiude entrambe le gambe (spot sell + perp buy reduce-only). |

### 📦 `CarrySymbolState`

> Stato per-simbolo del carry live.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool InPosition` | — |
| `p` | `DateTime? OpenedUtc` | — |
| `p` | `decimal NotionalQuote` | — |
| `p` | `decimal FundingCollectedPercent` | — |

### 📦 `CarryEngine` `(ICarryExecutor executor, CarryConfiguration config, ILogger&lt;CarryEngine&gt; logger)`

> [E3] Orchestrazione LIVE del carry delta-neutro (long spot + short perp) su Bitget, in Paper o Testnet — MAI Live (vedi ). Usa la STESSA regola di decisione del backtest ( ): a ogni valutazione calcola il funding annualizzato recente e apre/ chiude tramite l' . Isolato dal motore a corsia single-leg: non lo tocca, per non destabilizzare il percorso di trading esistente. Stato in memoria per-simbolo (persistenza fra riavvii = follow-up dichiarato). Il funding per la decisione arriva dal chiamante (serie recente da DB/exchange), così il motore resta puro e testabile.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyDictionary&lt;string, CarrySymbolState&gt; States` | — |
| `m` | `Task&lt;CarryAction&gt; EvaluateAsync(string symbol, IReadOnlyList&lt;decimal&gt; recentFundingPercent, CancellationToken ct)` | Valuta un simbolo dato il suo funding recente (% per 8h, ordinato, ultimo = più recente) e agisce. Ritorna l'azione intrapresa. La size del nozionale per gamba è del capitale. |

### 📦 `PaperCarryExecutor` `(ILogger&lt;PaperCarryExecutor&gt; logger) : ICarryExecutor`

> Executor Paper: NON tocca l'exchange, registra soltanto le due gambe che verrebbero aperte/chiuse. È la simulazione sicura per il forward test locale del carry, senza alcun rischio.

| | Firma | Descrizione |
|---|---|---|
| `p` | `CarryMode Mode` | — |
| `m` | `Task&lt;CarryExecutionResult&gt; OpenAsync(string symbol, decimal notionalQuote, CancellationToken ct)` | — |
| `m` | `Task&lt;CarryExecutionResult&gt; CloseAsync(string symbol, decimal notionalQuote, CancellationToken ct)` | — |

## `ProcioneMGR/Services/Carry/CarryModels.cs`

### 📦 `CarryConfiguration`

> [E3 roadmap profitto-intraday] Configurazione del carry delta-neutro (long spot + short perp sullo stesso simbolo). L'edge è il FUNDING incassato dallo short quando è positivo — un flusso, non una previsione. Delta-neutro: la componente direzionale del prezzo si elide fra le due gambe.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal InitialCapital` | Capitale iniziale (unità di conto, es. USDT). |
| `p` | `decimal PositionSizePercent` | % del capitale impegnata come nozionale PER GAMBA (le due gambe hanno lo stesso nozionale). |
| `p` | `decimal EnterAnnualFundingPercent` | Si ENTRA quando il funding annualizzato medio (finestra ) supera questa soglia (%). |
| `p` | `decimal ExitAnnualFundingPercent` | Si ESCE quando il funding annualizzato medio scende sotto questa soglia (%). Deve essere &lt; enter (isteresi). |
| `p` | `int TrailingFundingEvents` | Eventi di funding su cui mediare per la decisione (8h l'uno: 9 ≈ 3 giorni). Smussa gli spike singoli. |
| `p` | `int FundingEventsPerDay` | Eventi di funding al giorno dell'exchange (Binance/Bitget: 3, ogni 8h). |
| `p` | `decimal SpotFeePercent` | Commissione per lato della gamba SPOT (% del nozionale). |
| `p` | `decimal PerpFeePercent` | Commissione per lato della gamba PERP (% del nozionale, tipicamente &lt; spot). |
| `p` | `decimal SlippagePercent` | Slippage sfavorevole per gamba (%), in entrata e in uscita. |

### 🧾 `CarryEpisode` `(`

> Un episodio di carry: quando aperto/chiuso, funding incassato, costi, netto.

### 📦 `CarryBacktestResult`

> Esito del backtest carry. I "percent" sono sul nozionale di UNA gamba (il capitale impegnato per episodio); il netto totale è sul capitale iniziale.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal FinalCapital` | — |
| `p` | `decimal TotalReturnPercent` | — |
| `p` | `decimal GrossFundingPercent` | — |
| `p` | `decimal TotalCostPercent` | — |
| `p` | `int Episodes` | — |
| `p` | `int FundingEventsInPosition` | — |
| `p` | `int FundingEventsTotal` | — |
| `p` | `decimal TimeInPositionFraction` | — |
| `p` | `decimal NetAnnualizedPercent` | Rendimento netto annualizzato sul periodo INTERO (capitale sempre allocato). |
| `p` | `List&lt;CarryEpisode&gt; EpisodeList` | — |
| `p` | `List&lt;EquityPoint&gt; EquityCurve` | — |

## `ProcioneMGR/Services/Carry/CarryWorker.cs`

### 📦 `CarryOptions`

> Configurazione del forward-test del carry (sezione "Carry").

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Default OFF: il carry è un edge nuovo in forward test, si accende deliberatamente. Anche acceso, di default gira in PAPER (nessun ordine reale) — vedi . |
| `p` | `string Mode` | "Paper" (default, simulazione) o "Testnet". Live NON è un valore accettato: il parsing lo rifiuta e resta Paper. Il carry non può mai operare con denaro reale. |
| `p` | `List&lt;string&gt; Symbols` | Simboli (ticker base) da sorvegliare per il carry. |
| `p` | `int EvaluationMinutes` | Minuti fra due valutazioni (il funding cambia ogni 8h: un'ora è ampiamente sufficiente). |
| `p` | `decimal EnterAnnualFundingPercent` | — |
| `p` | `decimal ExitAnnualFundingPercent` | — |
| `p` | `int TrailingFundingEvents` | — |
| `p` | `decimal PositionSizePercent` | — |

### 📦 `CarryWorker` `(`

> [E3] Forward test del carry delta-neutro. Legge il funding recente dei simboli configurati dal DB (serie , la stessa che alimenta backtest e sentiment) e fa girare il con la stessa regola del backtest. In PAPER registra le decisioni senza toccare alcun exchange: è il modo sicuro di vedere, dal vivo, quando il carry aprirebbe/chiuderebbe, PRIMA di dargli ordini reali su Bitget demo. SICUREZZA: non ha il valore Live; il parsing di accetta solo Paper/Testnet e ripiega su Paper per qualsiasi altro valore. Il carry non può operare con denaro reale, per costruzione.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyDictionary&lt;string, CarrySymbolState&gt; States` | Stato per-simbolo del forward test (vuoto finché non c'è stata una valutazione). |
| `p` | `DateTime? LastEvaluationUtc` | Quando è stata completata l'ultima valutazione (null = mai). |
| `p` | `IReadOnlyList&lt;string&gt; SymbolsWithoutData` | Simboli che nell'ultimo giro non avevano abbastanza storia di funding per decidere. |
| `p` | `CarryMode EffectiveMode` | Modalità EFFETTIVA (mai Live: vedi ). |
| `m` | `Task ExecuteAsync(CancellationToken ct)` | — |
| `m` | `Task&lt;int&gt; TickAsync(CancellationToken ct)` | Un giro di valutazione su tutti i simboli configurati. Pubblico perché la UI possa forzarlo ("Esegui ora") sulla STESSA istanza del hosted service — stesso pattern di SentimentSyncWorker e FeatureDriftWorker . Restituis… |
| `m` | `(CarryMode Mode, string? Warning) ResolveMode(string configured)` | Parsing SICURO della modalità: solo Paper/Testnet, mai Live — e Testnet degrada a Paper finché l'executor Bitget demo non è attivo (follow-up gated dal wallet demo Futures finanziato). Restituisce anche il motivo, così … |

# `Services/Fleet/`

## `ProcioneMGR/Services/Fleet/FleetModels.cs`

### 📦 `FleetOptions`

> [AF2] Opzioni dell'orchestratore di flotta (sezione Fleet ). Default: SPENTO, e anche da acceso parte in DryRun (solo journal, zero azioni) — l'ordine degli incrementi è parte del contratto: prima si osserva il journal per giorni, poi si toglie il dry-run apposta.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | — |
| `p` | `bool DryRun` | Finché è true (default), l'orchestratore DECIDE e SCRIVE il journal ma non esegue nulla. |
| `p` | `int TickMinutes` | Cadenza del tick in minuti. |
| `p` | `decimal RetireSharpeThreshold` | Sharpe realizzato sotto cui un forward test è un perdente da ritirare. |
| `p` | `int RetireMinWeeks` | Settimane minime di osservazione prima che un ritiro sia un giudizio e non rumore. |
| `p` | `int RetireMinTrades` | Trade minimi prima che un ritiro sia un giudizio e non rumore. |
| `p` | `int RetireConfirmTicks` | Tick CONSECUTIVI in cui il verdetto di ritiro deve ripetersi prima di agire (isteresi: uno Sharpe che oscilla attorno alla soglia non deve produrre stop a raffica). |
| `p` | `int MaxAssignmentsPerTick` | Assegnazioni massime per tick (prudenza: una alla volta, il tick dopo si rivaluta). |
| `p` | `decimal MinTradesPerMonth` | Trade/mese minimi dichiarati (derivati dall'holdout) perché un candidato entri in coda. Preferenza del proprietario: intraday/swing breve — un candidato che non dichiara la sua frequenza non entra affatto. |
| `p` | `int CandidateMaxAgeDays` | Età massima (giorni) di un run perché sia ancora un candidato fresco. |
| `p` | `int MaxLanesWithoutExposureGuard` | Oltre questo numero di corsie ATTIVE, l'orchestratore rifiuta nuove assegnazioni se il limite di esposizione correlata ( Trading:CorrelatedExposure ) è spento: una flotta larga senza guardia trasversale è concentrazione… |
| `p` | `int CarrySilenceAlertHours` | Notifica se il worker del carry è abilitato ma non decide da più di queste ore. |
| `p` | `bool UseCommittee` | [AF3] Consulta il comitato AI sui PAREGGI (più candidati idonei della stessa assegnazione). Default false; richiede anche Committee:Enabled . Il comitato sceglie SOLO dentro il menù che il core ha già validato: una risp… |

### 🧾 `FleetLaneState` `(`

> Fotografia di una corsia come la vede l'orchestratore (sola lettura).

### 🧾 `FleetCandidate` `(`

> Un run candidato al forward test. : "pass" = sopravvissuti alla validazione piena (assegnabile in automatico); "grey" = bocciato SOLO per finestra corta (ContoTrade/sotto-potenza) — proposto al click umano, mai assegnato da solo (F5). La durata mediana delle posizioni NON è derivabile a livello di run (la trade list dei candidati non è persistita): si dichiara la frequenza (trade/mese) e il timeframe, la durata vera la misurerà il forward test stesso.

### 📦 `FleetState`

> Lo stato complessivo su cui ragiona. Solo dati, nessun servizio.

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyList&lt;FleetLaneState&gt; Lanes` | — |
| `p` | `IReadOnlyList&lt;FleetCandidate&gt; Candidates` | — |
| `p` | `int FootprintLanes` | Le prime N corsie (l'impronta storica dell'auto-apply, oggi 3): territorio di auto-reapply e campagne, MAI dell'orchestratore. La flotta lavora da qui in su. |
| `p` | `bool ExposureGuardEnabled` | Il limite di esposizione correlata fra corsie è acceso? (precondizione AF4b) |
| `p` | `DateTime NowUtc` | — |

### 🧾 `FleetAction` `(string Reason);`

> Le azioni che l'orchestratore può decidere. Chiuse: non esiste un'azione "avvia Live" per costruzione.

### 🧾 `AssignCandidateToLane` `(Guid RunId, int LaneId, string Reason) : FleetAction(Reason);`

> Schiera il candidato sulla corsia libera indicata e la avvia in Paper (AF2b; in DryRun solo journal).

### 🧾 `StopAndFreeLane` `(int LaneId, string Reason) : FleetAction(Reason);`

> Ferma un forward test perdente e libera la corsia.

### 🧾 `ProposeGreyCandidate` `(Guid RunId, string Reason) : FleetAction(Reason);`

> Fascia grigia (F5): si propone al click umano, MAI si assegna da soli.

### 🧾 `FleetNoOp` `(string Reason) : FleetAction(Reason);`

> Nessuna azione, ma con un motivo che PORTA informazione (conflitto, guardia, coda bloccata).

### 🧾 `FleetAssignmentMenu` `(int LaneId, IReadOnlyList&lt;FleetCandidate&gt; Eligible, Guid DefaultRunId);`

> [AF3] Il PAREGGIO che il comitato può arbitrare: più candidati idonei per la stessa corsia. è la scelta deterministica (il più vecchio) — quella che vale se il comitato non produce una maggioranza valida, ed è già dentro il piano.

### 🧾 `FleetPlan` `(IReadOnlyList&lt;FleetAction&gt; Actions, FleetAssignmentMenu? Menu = null)`

> Il piano di un tick. è presente solo quando esiste un pareggio arbitrabile.

| | Firma | Descrizione |
|---|---|---|
| `p` | `FleetPlan Empty` | — |

## `ProcioneMGR/Services/Fleet/FleetOrchestrator.cs`

### 📦 `FleetOrchestrator`

> [AF2] Il cuore deterministico della "Queen Bee": è una funzione PURA — stesso stato, stesso piano, sempre — fuzzabile come la promozione. Le AI non abitano qui: al più, in un pareggio fra opzioni equivalenti, il core produce il pareggio e il worker può chiedere al comitato (AF3) quale scegliere; una risposta invalida ricade sul default deterministico che questo stesso metodo definisce. I confini, in ordine di importanza: 1. l'orchestratore NON tocca MAI: corsie dell'impronta storica (0..FootprintLanes-1, territorio di auto-reapply e campagne), corsie Live o Testnet (le gestisce PromotionWorker), corsie in quarantena, corsie in emergency stop, corsie possedute da una campagna; 2. la fascia grigia (F5) si PROPONE al click umano, mai si assegna da soli; 3. [AF4b] con più di MaxLanesWithoutExposureGuard corsie attive e la guardia di esposizione correlata spenta, niente assegnazioni nuove: p…

| | Firma | Descrizione |
|---|---|---|
| `m` | `FleetPlan Decide(FleetState state, FleetOptions opt)` | — |

## `ProcioneMGR/Services/Fleet/FleetOrchestratorWorker.cs`

### 📦 `FleetOrchestratorWorker` `(`

> [AF2] Il braccio della Queen Bee: ogni tick legge lo stato (reader), decide (core puro), applica l'ISTERESI sui ritiri e scrive il journal. In questo incremento (AF2a) NON esegue nulla — nemmeno con Fleet:DryRun=false : l'esecuzione arriva con AF2b, e un flag girato in anticipo deve produrre un avviso, non un'azione non collaudata. Vive nel SOLO monolite (è il cervello: scheduler, planner e promozioni stanno già qui).

| | Firma | Descrizione |
|---|---|---|
| `p` | `FleetPlan? LastPlan` | Ultimo piano deciso, per il pannello (/admin/autonomy). |
| `p` | `DateTime? LastTickUtc` | — |
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |
| `m` | `Task TickAsync(CancellationToken ct)` | Un tick completo. Pubblico per i test di integrazione e per un futuro "Esegui ora". |

## `ProcioneMGR/Services/Fleet/FleetStateReader.cs`

### 🔌 `IFleetStateReader`

> [AF2] Costruisce il in SOLA lettura: corsie (directory + quarantene + possesso campagne + stato vivo dei motori) e coda candidati (run completati + verdetti di validazione). Difensivo per corsia e per run: un guasto su una corsia la rende INTOCCABILE (mai "libera per errore"), un run illeggibile esce dalla coda con un log — l'orchestratore deve poter ragionare su ciò che sa, non inciampare su ciò che non sa.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;FleetState&gt; ReadAsync(CancellationToken ct = default)` | — |

### 📦 `FleetStateReader` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;FleetState&gt; ReadAsync(CancellationToken ct = default)` | — |

### ▫️ `CandidateVerdict` `(string Band, decimal TradesPerMonth, string Timeframe, string Summary);`

| | Firma | Descrizione |
|---|---|---|
| `m` | `bool IsGrey(ValidatedCandidate candidate)` | IL filtro della fascia grigia — l'unica definizione, condivisa fra il lettore (le proposte) e il GreyDeployer (il click umano): bocciato per SOLA finestra corta (ContoTrade "Solo N trade…" o DSR in [0.80, 0.95)) CON Sha… |

## `ProcioneMGR/Services/Fleet/GreyDeployer.cs`

### 🧾 `GreyChoice` `(`

> Un candidato grigio schierabile, come lo mostra il form.

### 🧾 `GreyDeployResult` `(bool Success, string Message);`

> Esito dello schieramento, scritto per un umano.

### 🔌 `IGreyDeployer`

> [F5] IL CLICK UMANO della fascia grigia: prende un candidato grigio da un run (identità + parametri ESATTI validati), gli monta il bracket SL/TP data-driven (stesso dell'applica) e lo scrive su una corsia di FLOTTA libera, avviandola in Paper se richiesto. Confini (gli stessi della Queen Bee, qui applicati a una azione UMANA): - solo corsie oltre l'impronta auto-apply, mai quarantene, mai corsie che girano; - solo Paper: la modalità non è nemmeno un parametro; - solo candidati che passano il filtro grigio del lettore (Sharpe holdout positivo, bocciati per sola finestra corta) — questo servizio non è una porta di servizio per schierare qualunque cosa, è il braccio della proposta F5. Ogni schieramento finisce nel journal della flotta con Source="human".

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;GreyChoice&gt;&gt; ListGreyAsync(Guid runId, CancellationToken ct = default)` | — |

### 📦 `GreyDeployer` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;GreyChoice&gt;&gt; ListGreyAsync(Guid runId, CancellationToken ct = default)` | — |

# `Services/Security/`

## `ProcioneMGR/Services/Security/AesGcmEncryptionService.cs`

### 📦 `AesGcmEncryptionService` `: IEncryptionService, IMasterKeyStatus, IMasterKeyRing`

> Implementazione AES-256-GCM di . Formato di output (poi codificato base64): [1 byte versione][12 byte nonce][16 byte tag GCM][N byte ciphertext] Il nonce e' casuale per ogni cifratura (mai riusato con la stessa chiave), requisito di sicurezza fondamentale per GCM. La chiave master a 256 bit e' derivata dal valore di configurazione "Security:MasterKey": - se il valore e' base64 di esattamente 32 byte, viene usato direttamente; - altrimenti viene derivata via SHA-256 della stringa UTF-8. STATO (verificato 2026-07-17): in produzione la master key NON vive in appsettings.json. I deployment K8s (infra/k8s/trading/deployment.yaml, infra/k8s/ui/deployment.yaml) la iniettano gia' via Secret dedicato (rispettivamente trading-secrets/ui-secrets, chiave Security__MasterKey, mai nell'immagine) — le due copie devono restare identiche perche' entrambi i processi decifrano le stesse credenziali exchan…

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsDefaultDevKey` | — |
| `p` | `bool HasPreviousKeys` | — |
| `c` | `AesGcmEncryptionService(IConfiguration configuration)` | — |
| `m` | `string Encrypt(string plaintext)` | — |
| `m` | `string Decrypt(string ciphertext)` | — |
| `m` | `bool IsEncryptedWithCurrentKey(string ciphertext)` | — |

## `ProcioneMGR/Services/Security/DataProtectionSetup.cs`

### 📦 `DataProtectionSetup`

> Composizione di Data Protection, estratta da Program.cs per essere verificabile da test. Data Protection è ciò che firma e cifra i cookie di autenticazione. La sua discriminante applicativa decide quali chiavi vengono derivate: due processi con discriminanti diverse non possono leggere i cookie l'uno dell'altro.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string ApplicationName` | Nome applicativo FISSO. Il default di ASP.NET Core lo deriva dal ContentRootPath , e quello è il difetto: due copie dello STESSO repository in cartelle diverse — un worktree git accanto al checkout principale — otterreb… |

## `ProcioneMGR/Services/Security/EncryptedStringConverter.cs`

### 📦 `EncryptedStringConverter` `: ValueConverter&lt;string, string&gt;`

> ValueConverter EF Core che cifra una stringa quando viene scritta sul DB e la decifra quando viene letta. EF NON invoca il converter per i valori null, quindi le proprieta' nullable (es. Passphrase) sono gestite automaticamente.

| | Firma | Descrizione |
|---|---|---|
| `c` | `EncryptedStringConverter(IEncryptionService encryption)` | — |

## `ProcioneMGR/Services/Security/ExchangeCredentialReader.cs`

### 🧾 `DecryptedExchangeCredential` `(`

> Credenziale exchange decifrata riga per riga. Se è false la riga esiste sul DB ma è cifrata con una master key DIVERSA da quella del processo corrente: i campi segreti sono null (mai plaintext parziale) e la UI deve mostrare il badge "reinserire le credenziali" invece di usarla.

| | Firma | Descrizione |
|---|---|---|
| `p` | `string MaskedApiKey` | ApiKey mascherata per la UI (mai esporre il secret). Vuota se non decifrabile. |

### 🔌 `IExchangeCredentialReader`

> Lettura RESILIENTE delle credenziali exchange (bug B2, docs/TEST-UI-2026-07-18.md): il converter EF decifra dentro la materializzazione, quindi una sola riga cifrata con una master key diversa faceva esplodere l'intera query (AuthenticationTagMismatchException) — Internal Server Error su /settings/exchanges e avvio Testnet/Live abbattuto da un'eccezione grezza. Qui si legge il ciphertext (proiezione keyless ) e si decifra in memoria riga per riga: il fallimento di UNA riga la flagga soltanto.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;DecryptedExchangeCredential&gt;&gt; LoadForUserAsync(string userId, CancellationToken ct = default)` | Tutte le credenziali di un utente (per /settings/exchanges), le più recenti prima. |
| `m` | `Task&lt;DecryptedExchangeCredential?&gt; FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)` | La credenziale da usare per il trading su (exchange, testnet) — stessa semantica storica di TradingEngine.LoadCredentialsAsync: qualunque utente (piattaforma a operatore singolo). PREFERISCE una riga decifrabile se ne e… |
| `m` | `Task&lt;(int Total, int Unreadable)&gt; CountUnreadableAsync(CancellationToken ct = default)` | Censimento per il probe di avvio (Fase 3-C2, PRD Autonomia): quante credenziali esistono e quante NON si decifrano con la master key corrente — di qualunque utente. Non espone dati. |

### 📦 `ExchangeCredentialReader` `(`

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;IReadOnlyList&lt;DecryptedExchangeCredential&gt;&gt; LoadForUserAsync(string userId, CancellationToken ct = default)` | — |
| `m` | `Task&lt;DecryptedExchangeCredential?&gt; FindForTradingAsync(ExchangeName exchange, bool testnet, CancellationToken ct = default)` | — |
| `m` | `Task&lt;(int Total, int Unreadable)&gt; CountUnreadableAsync(CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Security/IEncryptionService.cs`

### 🔌 `IEncryptionService`

> Cifratura simmetrica autenticata per i segreti a riposo (API key / secret / passphrase degli exchange). L'implementazione usa AES-256-GCM.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string Encrypt(string plaintext)` | Cifra un testo in chiaro e restituisce una stringa portabile (base64, con nonce e tag inclusi). |
| `m` | `string Decrypt(string ciphertext)` | Decifra una stringa prodotta da . Lancia se il testo e' manomesso. |

### 🔌 `IMasterKeyStatus`

> Stato della master key, separato da perché i consumer del guard (startup di produzione, gate Live del TradingEngine) non devono poter cifrare nulla — solo sapere se la chiave in uso è ancora il PLACEHOLDER committato nel template. Con quella chiave (pubblica su git) i segreti "cifrati" sono di fatto in chiaro per chiunque legga il repo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsDefaultDevKey` | True se la master key configurata è il placeholder di sviluppo committato nel template. |

### 🔌 `IMasterKeyRing`

> Vista sul KEYRING della rotazione (Fase 0 PRD-RISANAMENTO, 2026-08-08). Separata da per lo stesso principio di : chi orchestra la rotazione (la pagina /settings/exchanges, il MasterKeyRotationService) deve poter CLASSIFICARE i payload — non gli serve cifrare in proprio.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool HasPreviousKeys` | True se sono configurate chiavi PRECEDENTI (una rotazione è in corso). |
| `m` | `bool IsEncryptedWithCurrentKey(string ciphertext)` | True se il payload si apre con la chiave CORRENTE (nessun bisogno di ri-cifratura). False sia per i payload sulla chiave precedente sia per quelli indecifrabili o corrotti: la distinzione fra i due casi la fa il chiaman… |

## `ProcioneMGR/Services/Security/MasterKeyProbe.cs`

### 🧾 `MasterKeyProbeResult` `(int Total, int Unreadable, DateTime CheckedAtUtc)`

> Esito dell'ultimo probe della master key (Fase 3-C2). Null in = probe non ancora eseguito.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool HasUnreadable` | Vero quando esistono credenziali cifrate con una master key DIVERSA da quella del processo. |

### 🔌 `IMasterKeyProbe`

> Probe di avvio della master key (Fase 3-C2, PRD Autonomia §6): l'app avviata con la chiave sbagliata oggi "muore in silenzio" sul percorso credenziali — lo scopri quando una pagina va in 500 o un avvio Testnet fallisce. Qui il fallimento diventa RUMOROSO alla partenza: LogCritical + notifica (Fase 4) + stato esposto alla UI (banner persistente in /trading e /settings/exchanges). Il probe LEGGE soltanto (nessuna scrittura, può vivere in ogni host).

| | Firma | Descrizione |
|---|---|---|
| `p` | `MasterKeyProbeResult? Result` | — |
| `m` | `Task&lt;MasterKeyProbeResult&gt; ProbeAsync(CancellationToken ct = default)` | Esegue il probe e aggiorna . Usato dal worker di avvio (e dai test). |
| `m` | `Task RefreshAfterCredentialChangeAsync(CancellationToken ct = default)` | Ri-esegue il probe dopo una MODIFICA alle credenziali, senza propagare errori al chiamante. Serve perché era un'istantanea presa una volta sola all'avvio: chi reinseriva le credenziali sistemandole si vedeva restare add… |

### 📦 `MasterKeyProbe` `(`

| | Firma | Descrizione |
|---|---|---|
| `p` | `MasterKeyProbeResult? Result` | — |
| `m` | `Task RefreshAfterCredentialChangeAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;MasterKeyProbeResult&gt; ProbeAsync(CancellationToken ct = default)` | — |

### 📦 `MasterKeyProbeWorker` `(`

> Esegue il probe all'AVVIO, con retry se il DB non è ancora raggiungibile (l'ordine di avvio dei pod in K8s non è garantito — stessa cura di PipelineSchedulerWorker per la bonifica orfani). Da qui in poi l'esito NON resta congelato: la pagina delle credenziali chiama dopo ogni aggiunta o eliminazione, così chi sistema le credenziali vede sparire il banner subito invece di doverci convivere fino al riavvio.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Security/MasterKeyRotationService.cs`

### 🧾 `MasterKeyReEncryptReport` `(int Total, int ReEncrypted, int AlreadyCurrent, int Unreadable)`

> Esito della ri-cifratura di massa: quante righe viste, riportate sulla chiave corrente, saltate. Righe cifrate censite (credenziali exchange + chiavi AI). Righe riscritte con la chiave corrente (erano su una chiave precedente). Righe già sulla chiave corrente: non toccate. Righe che NESSUNA chiave del ring apre: restano com'erano, vanno reinserite a mano (badge in /settings/exchanges).

| | Firma | Descrizione |
|---|---|---|
| `m` | `string ToString()` | — |

### 🔌 `IMasterKeyRotationService`

> Ri-cifratura di massa dei segreti a riposo con la chiave CORRENTE (Fase 0 PRD-RISANAMENTO: lo "strumento di re-cifratura" che il TODO storico di dichiarava mancante). Si usa DURANTE una rotazione, quando il keyring ha la vecchia chiave in PreviousMasterKeys: le righe ancora sulla vecchia vengono decifrate col ring e riscritte con la corrente. Al termine si può svuotare PreviousMasterKeys.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;MasterKeyReEncryptReport&gt; ReEncryptAllAsync(CancellationToken ct = default)` | Ri-cifra tutte le righe apribili col keyring che NON sono già sulla chiave corrente. Idempotente: una seconda esecuzione trova tutto AlreadyCurrent. Le righe indecifrabili non vengono mai toccate (nessuna perdita di dat… |

### 📦 `MasterKeyRotationService` `(`

> COME funziona la riscrittura: si carica l'entità EF (il converter decifra col RING, quindi le righe su chiave precedente ora materializzano), si marca la proprietà cifrata come modificata a parità di valore, e al SaveChanges il converter ri-cifra con la chiave CORRENTE. Il forcing di IsModified è necessario perché lo snapshot di EF confronta il valore in chiaro — identico — e senza il flag non scriverebbe nulla. RESILIENZA (lezione del bug B2): la materializzazione EF decifra DENTRO la query, quindi una sola riga indecifrabile abbatterebbe una query cumulativa. Qui si classifica prima dal ciphertext grezzo (vista keyless per le credenziali exchange, SqlQueryRaw per le chiavi AI) e si caricano SOLO le righe di cui il ring risponde, una per una.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;MasterKeyReEncryptReport&gt; ReEncryptAllAsync(CancellationToken ct = default)` | — |

### 🧾 `AiCredentialCiphertextRow` `(int Id, string Provider, string ApiKey);`

> Proiezione grezza di una riga AiCredentials: il ciphertext così com'è sul DB.

# `Services/Exchanges/`

## `ProcioneMGR/Services/Exchanges/BinanceClient.cs`

### 📦 `BinanceClient` `(HttpClient http, ILogger&lt;BinanceClient&gt; logger, IExchangeClock? clock = null) : IE…`

> Client Binance Spot via REST pubblica (nessuna firma necessaria per i dati di mercato). Endpoint klines: GET /api/v3/klines, max 1000 candele per richiesta.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `p` | `int MaxCandlesPerRequest` | — |
| `m` | `Task&lt;List&lt;Ohlcv&gt;&gt; FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;string&gt;&gt; GetSymbolsAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;bool&gt; TestConnectionAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)` | — |
| `m` | `Task&lt;CancelOrderResult&gt; CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;OpenOrder&gt;&gt; GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `Task&lt;OrderStatusResult&gt; GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `string NormalizeBinanceOrderStatus(string status)` | Normalizza lo stato ordine Binance nello schema comune di . |
| `m` | `Task&lt;AccountBalance&gt; GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `Task&lt;SymbolFilters&gt; GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)` | — |
| `m` | `Task&lt;SetLeverageResult&gt; SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)` | [P0-5] Ordine TRIGGER reduce-only "resting" via /fapi/v1/order: STOP_MARKET (stop) o TAKE_PROFIT_MARKET (target), attivato sul MARK price (workingType=MARK_PRICE), che chiude la posizione anche se il processo va giù. Ve… |
| `m` | `string BuildTriggerQuery(string market, string side, bool isStopLoss, decimal quantity, decimal stopPrice, string clientOrderId, long timestampMs)` | Query firmabile per un ordine trigger reduce-only market (funzione pura, testabile). STOP_MARKET per lo stop-loss, TAKE_PROFIT_MARKET per il take-profit; stopPrice = prezzo di attivazione. |
| `m` | `Task&lt;FuturesPosition?&gt; GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;CancelOrderResult&gt; CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = def…` | — |
| `m` | `Task&lt;List&lt;OpenOrder&gt;&gt; GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;OrderStatusResult&gt; GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = …` | — |
| `m` | `Task&lt;FuturesBalance&gt; GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;SymbolFilters&gt; GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)` | — |
| `m` | `Task&lt;decimal&gt; GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Exchanges/BitgetAttestationOptions.cs`

### 📦 `BitgetAttestationOptions`

> [Fase 3 PRD-RISANAMENTO] Sezione Trading:Bitget : l'attestazione che sblocca i MARKET-BUY spot su Bitget. Il POCO esiste per il pannello di /admin/protections — il consumo vero resta la lettura puntuale in BitgetClient.PlaceOrderAsync (hot, a ogni ordine). Non è una preferenza: è la registrazione di un FATTO («ho verificato dal vivo con tools/SpotVerify che la semantica del campo size è quella che il client manda»). Il default false blocca il percorso d'ordine perché la v2 di Bitget documenta size come controvalore QUOTE sui market-buy spot, e un ordine di taglia sbagliata è il danno che il blocco previene.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool SpotMarketBuyVerified` | — |

## `ProcioneMGR/Services/Exchanges/BitgetClient.cs`

### 📦 `BitgetClient` `(`

> Client Bitget Spot via REST pubblica v2 (dati di mercato non firmati). Endpoint candele: GET /api/v2/spot/market/candles.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `p` | `int MaxCandlesPerRequest` | — |
| `m` | `Task&lt;List&lt;Ohlcv&gt;&gt; FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;string&gt;&gt; GetSymbolsAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;bool&gt; TestConnectionAsync(CancellationToken ct = default)` | — |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)` | — |
| `m` | `Task&lt;CancelOrderResult&gt; CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `Task&lt;List&lt;OpenOrder&gt;&gt; GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `Task&lt;OrderStatusResult&gt; GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `string NormalizeBitgetOrderStatus(string status)` | Normalizza lo stato ordine Bitget (spot "status" / mix "state") nello schema comune. |
| `m` | `Task&lt;AccountBalance&gt; GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default)` | — |
| `m` | `Task&lt;SymbolFilters&gt; GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)` | — |
| `m` | `Task&lt;SetLeverageResult&gt; SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `string? DemoSymbolHint(string? error, string symbol, bool testnet)` | Traduce l'errore più insidioso della **demo futures** Bitget ("paptrading"): l'ambiente simulato espone solo un sottoinsieme di contratti (pochi major), e chiedere leva o ordini su un simbolo non simulato risponde 40034… |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)` | — |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)` | [P0-5] Ordine TRIGGER reduce-only "resting" via place-plan-order (Mix v2): stop-market o take-profit-market attivato sul MARK PRICE, che chiude la posizione anche se il processo va giù. ⚠️ Costruzione payload conforme a… |
| `m` | `string BuildTriggerPlanBody(string market, string productType, string side, decimal triggerPrice, decimal size, string clientOid)` | Corpo JSON di un ordine plan reduce-only market su mark price (funzione pura, testabile). Il verso (stop vs take-profit) è implicito nel rispetto al mark corrente. |
| `m` | `Task&lt;FuturesPosition?&gt; GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;CancelOrderResult&gt; CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = def…` | — |
| `m` | `Task&lt;List&lt;OpenOrder&gt;&gt; GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;OrderStatusResult&gt; GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = …` | — |
| `m` | `Task&lt;FuturesBalance&gt; GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;SymbolFilters&gt; GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)` | — |
| `m` | `Task&lt;decimal&gt; GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default)` | — |

## `ProcioneMGR/Services/Exchanges/ExchangeClientException.cs`

### 📦 `ExchangeClientException` `: Exception`

> Errore restituito da un exchange (HTTP non-2xx o payload d'errore).

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `p` | `int StatusCode` | — |
| `c` | `ExchangeClientException(ExchangeName exchange, int statusCode, string body)` | — |

## `ProcioneMGR/Services/Exchanges/ExchangeClientFactory.cs`

### 📦 `ExchangeClientFactory` `(IServiceProvider services) : IExchangeClientFactory`

> Factory che risolve il client corretto dal DI. I client sono registrati come typed HttpClient (vedi Program.cs), quindi otteniamo istanze gia' configurate con base address e resilienza. Per aggiungere un exchange: implementa e , registralo in DI e aggiungi un case qui e in . Nessun'altra parte del sistema cambia.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IExchangeClient Create(ExchangeName exchange)` | — |
| `m` | `IExchangeClient Create(string exchangeName)` | — |
| `m` | `IFuturesExchangeClient CreateFutures(ExchangeName exchange)` | — |
| `m` | `IFuturesExchangeClient CreateFutures(string exchangeName)` | — |

## `ProcioneMGR/Services/Exchanges/ExchangeClock.cs`

### 🔌 `IExchangeClock`

> Orologio usato per il campo timestamp delle richieste FIRMATE. Perché non basta : gli exchange rifiutano una richiesta firmata se il suo timestamp si discosta dall'ora del LORO server oltre recvWindow (5s qui). Un orologio locale che deriva di pochi secondi — cosa normale su una macchina desktop che non sincronizza NTP con regolarità, o dopo una sospensione — fa fallire ordini validi con l'errore Binance -1021 Timestamp for this request is outside of the recvWindow . È un guasto particolarmente sgradevole perché intermittente e perché colpisce anche le CHIUSURE: uno stop che non riesce a partire per un problema d'orologio è una perdita reale. Rimedio: si misura una volta l'offset rispetto al server dell'exchange e lo si applica a ogni timestamp firmato, riallineandolo periodicamente.

| | Firma | Descrizione |
|---|---|---|
| `m` | `long TimestampMillis(ExchangeName exchange)` | Millisecondi Unix da usare nel campo timestamp , corretti per l'offset noto. |
| `m` | `TimeSpan Offset(ExchangeName exchange)` | Offset corrente (ora server − ora locale). Zero finché non è stato misurato. |
| `m` | `void SetOffset(ExchangeName exchange, TimeSpan offset)` | Registra un offset appena misurato. |

### 📦 `ExchangeClock` `(ILogger&lt;ExchangeClock&gt; logger, TimeProvider? timeProvider = null) : IExchangeClock`

> Implementazione condivisa (singleton). Parte con offset ZERO per ogni exchange, quindi finché non ha misurato nulla il comportamento è identico a quello storico: nessuna regressione se la sonda fallisce o non gira.

| | Firma | Descrizione |
|---|---|---|
| `m` | `TimeSpan Offset(ExchangeName exchange)` | — |
| `m` | `long TimestampMillis(ExchangeName exchange)` | — |
| `m` | `void SetOffset(ExchangeName exchange, TimeSpan offset)` | — |

### 📦 `ExchangeClockSyncWorker` `(`

> Misura periodicamente l'offset d'orologio verso ogni exchange interrogandone l'endpoint di ora del server (pubblico, nessuna credenziale). La misura sottrae metà del round-trip: il timestamp che il server riporta è di quando LUI ha risposto, quindi confrontarlo con l'ora locale di ricezione conterebbe come deriva anche la latenza di rete, che deriva non è.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | — |

## `ProcioneMGR/Services/Exchanges/ExchangeRateLimitHandler.cs`

### 📦 `ExchangeRateLimitHandler` `(`

> Disciplina di rate-limit verso le API REST degli exchange, applicata come sul typed HttpClient: vale per OGNI chiamata (pubblica o firmata, spot o futures) senza toccare le decine di punti che le compongono. Due meccanismi distinti, entrambi necessari: 1. LIMITE PROATTIVO (token bucket): non si supera un tetto di richieste al secondo. È ciò che evita di finire in ban, invece di reagirvi. Serve perché il feed real-time e le corsie multiple possono generare raffiche che il ciclo REST da solo non produceva. 2. RITIRO REATTIVO su 429 (rate limit) e 418 (IP bannato da Binance dopo 429 ripetuti): si rispetta Retry-After quando c'è, altrimenti backoff esponenziale con jitter. Continuare a martellare dopo un 429 è esattamente il comportamento che trasforma un limite temporaneo in un ban dell'IP. NB: non si ritenta MAI più di volte, e i 5xx NON vengono ritentati qui. La ragione è di sicurezza, n…

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;HttpResponseMessage&gt; SendAsync(HttpRequestMessage request, CancellationToken ct)` | — |
| `m` | `void Dispose(bool disposing)` | — |

## `ProcioneMGR/Services/Exchanges/ExchangeTrading.cs`

### ▫️ `TradingCredentials` `(string ApiKey, string ApiSecret, string? Passphrase, bool IsTestnet);`

> Credenziali per le chiamate firmate (private) all'exchange.

### 📦 `PlaceOrderRequest`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `string Side` | — |
| `p` | `string Type` | — |
| `p` | `decimal Quantity` | — |
| `p` | `decimal? Price` | — |
| `p` | `decimal? TriggerPrice` | [P0-5] Prezzo di attivazione per gli ordini TRIGGER (stop-market / take-profit-market) piazzati via . Null per MARKET/LIMIT. |
| `p` | `string ClientOrderId` | Id idempotente lato client (newClientOrderId / clientOid). |
| `p` | `TradingCredentials Credentials` | — |

### 📦 `PlaceOrderResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Success` | — |
| `p` | `string? ExchangeOrderId` | — |
| `p` | `string Status` | — |
| `p` | `decimal? FilledPrice` | — |
| `p` | `decimal? FilledQuantity` | — |
| `p` | `string? Error` | — |
| `p` | `bool NetworkUncertain` | True se la chiamata HTTP è fallita (timeout/5xx) e NON sappiamo se l'ordine sia stato piazzato: il chiamante DEVE riconciliare con GetOpenOrdersAsync prima di ritentare. |

### 📦 `CancelOrderResult`

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Success` | — |
| `p` | `string? Error` | — |

### 📦 `OrderStatusResult`

> Stato di un ordine interrogato per clientOrderId, per la riconciliazione post-errore-di-rete. A differenza di GetOpenOrdersAsync (solo ordini "resting"), questo lookup vede anche gli ordini GIÀ ESEGUITI: un MARKET riempito durante un blip di rete non è negli open orders ma esiste qui — senza questa distinzione un fill reale verrebbe scambiato per "mai piazzato" (ordine duplicato).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Found` | False se l'exchange dichiara esplicitamente che l'ordine non esiste. |
| `p` | `bool NetworkUncertain` | True se il lookup stesso è fallito (timeout/5xx): stato ancora IGNOTO, ritentare. |
| `p` | `string Status` | Normalizzato: Filled \| PartiallyFilled \| Open \| Cancelled \| Rejected \| Expired. |
| `p` | `decimal? FilledPrice` | Prezzo medio di esecuzione (null se non ancora eseguito o non disponibile). |
| `p` | `decimal? FilledQuantity` | — |
| `p` | `string? ExchangeOrderId` | — |
| `p` | `string? Error` | — |
| `p` | `bool IsTerminalUnfilled` | True se l'ordine esiste ma è terminato SENZA esecuzione (safe da ritentare). |

### 📦 `OpenOrder`

| | Firma | Descrizione |
|---|---|---|
| `p` | `string ExchangeOrderId` | — |
| `p` | `string ClientOrderId` | — |
| `p` | `string Symbol` | — |
| `p` | `string Side` | — |
| `p` | `decimal Quantity` | — |
| `p` | `decimal? Price` | — |
| `p` | `string Status` | — |

### 📦 `AccountBalance`

| | Firma | Descrizione |
|---|---|---|
| `p` | `Dictionary&lt;string, decimal&gt; Free` | — |
| `p` | `Dictionary&lt;string, decimal&gt; Locked` | — |

### 📦 `SymbolFilters`

> Filtri di trading di un simbolo (da exchangeInfo): passo lotto/prezzo e minimi. Servono a formattare la quantità in modo che l'exchange non rifiuti l'ordine.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal StepSize` | — |
| `p` | `decimal MinQty` | — |
| `p` | `decimal TickSize` | — |
| `p` | `decimal MinNotional` | — |
| `m` | `decimal RoundQuantity(decimal qty)` | Arrotonda la quantità per DIFETTO al multiplo di StepSize valido. |
| `m` | `decimal RoundPrice(decimal price)` | Arrotonda il prezzo al multiplo di TickSize valido. |
| `m` | `bool IsTradable(decimal qty, decimal price)` | True se l'ordine rispetta minQty e minNotional. |

### 📦 `ExchangeSigning`

> Firme HMAC per le richieste autenticate. Funzioni pure, testabili.

| | Firma | Descrizione |
|---|---|---|
| `m` | `string HmacSha256Hex(string message, string secret)` | HMAC-SHA256 esadecimale minuscolo (Binance). |
| `m` | `string HmacSha256Base64(string message, string secret)` | HMAC-SHA256 in base64 (Bitget). |
| `m` | `long UnixMillis(DateTime utc)` | — |

## `ProcioneMGR/Services/Exchanges/FuturesTrading.cs`

### 📦 `SetLeverageResult`

> Esito dell'impostazione della leva su un simbolo (richiesta PRIMA di ogni apertura).

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Success` | — |
| `p` | `int Leverage` | — |
| `p` | `string? Error` | — |

### 📦 `FuturesPosition`

> Posizione futures come riportata dall'EXCHANGE (fonte di verità): a differenza della stima locale ( ), il prezzo di liquidazione qui è quello REALE calcolato dall'exchange (include fondo assicurativo, mark price, fee).

| | Firma | Descrizione |
|---|---|---|
| `p` | `string Symbol` | — |
| `p` | `decimal Quantity` | Sempre positiva; il lato è in . |
| `p` | `string Side` | "LONG" \| "SHORT". |
| `p` | `decimal EntryPrice` | — |
| `p` | `decimal MarkPrice` | — |
| `p` | `int Leverage` | — |
| `p` | `decimal LiquidationPrice` | — |
| `p` | `decimal UnrealizedPnl` | — |
| `p` | `decimal MarginBalance` | Margine isolato allocato alla posizione. |

### 📦 `FuturesBalance`

> Saldo del conto futures (margine, non asset spot).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal AvailableMargin` | Margine disponibile per aprire nuove posizioni. |
| `p` | `decimal TotalEquity` | Equity totale del conto futures (margine + PnL non realizzato). |

### 🔌 `IFuturesExchangeClient`

> Estensione futures (perpetual USDT-margined, margine ISOLATO) di . Interfaccia SEPARATA e non metodi opzionali sull'esistente: spot e futures hanno semantiche diverse (saldo vs posizione, leva, margine, liquidazione) e mescolarle avrebbe reso ambiguo. / implementano ENTRAMBE le interfacce sulla stessa classe (stesso HttpClient/firma HMAC). Margine ISOLATO per scelta di sicurezza: ogni posizione rischia solo il proprio margine, mai l'intero saldo del conto (a differenza del margine "cross") — coerente con l'uso di leva alta su un capitale piccolo, dove l'isolamento del rischio per trade è essenziale.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `m` | `Task&lt;SetLeverageResult&gt; SetLeverageAsync(string symbol, int leverage, TradingCredentials credentials, CancellationToken ct = default)` | Imposta la leva (margine isolato) per il simbolo. Va chiamata PRIMA di ogni apertura. |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceFuturesOrderAsync(PlaceOrderRequest request, bool reduceOnly, CancellationToken ct = default)` | Piazza un ordine futures. Riusa / dello spot (stessa forma); la leva è già impostata separatamente via . True per gli ordini di CHIUSURA: impedisce all'exchange di aprire/aumentare una posizione se la quantità fosse per… |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceFuturesTriggerOrderAsync(PlaceOrderRequest request, bool isStopLoss, CancellationToken ct = default)` | [P0-5] Piazza un ordine TRIGGER reduce-only che vive sull'exchange come protezione "resting": uno stop-market ( = true) o un take-profit-market (false), attivato quando il mark price tocca . A differenza degli stop soft… |
| `m` | `Task&lt;FuturesPosition?&gt; GetPositionAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)` | Posizione aperta corrente per il simbolo (null se flat). Fonte di verità per la liquidazione. |
| `m` | `Task&lt;CancelOrderResult&gt; CancelFuturesOrderAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = def…` | — |
| `m` | `Task&lt;List&lt;OpenOrder&gt;&gt; GetOpenFuturesOrdersAsync(string symbol, TradingCredentials credentials, CancellationToken ct = default)` | — |
| `m` | `Task&lt;OrderStatusResult&gt; GetFuturesOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials credentials, CancellationToken ct = …` | Stato di un ordine futures per client order id, INCLUSI gli ordini già eseguiti/terminati (a differenza di ). Lookup autorevole per la riconciliazione dopo un . |
| `m` | `Task&lt;FuturesBalance&gt; GetFuturesBalanceAsync(TradingCredentials credentials, CancellationToken ct = default)` | Saldo del conto futures (margine disponibile, equity totale). |
| `m` | `Task&lt;SymbolFilters&gt; GetFuturesSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)` | Filtri di trading del simbolo futures (LOT_SIZE/PRICE_FILTER/minNotional). Endpoint pubblico. |
| `m` | `Task&lt;decimal&gt; GetFundingRateAsync(string symbol, bool testnet, CancellationToken ct = default)` | Funding rate corrente, in % per periodo di 8 ore (stessa convenzione di FundingRatePercentPer8h nel backtest). Endpoint pubblico. |

## `ProcioneMGR/Services/Exchanges/IExchangeClient.cs`

### 🔌 `IExchangeClient`

> Astrazione di un exchange (Strategy Pattern). Aggiungere un nuovo exchange significa implementare questa interfaccia e registrarla nella , senza toccare il codice esistente. I simboli usano la forma canonica "BASE/QUOTE" (es. "BTC/USDT"); ogni client converte internamente nel formato dell'exchange.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | Exchange gestito da questa implementazione. |
| `p` | `int MaxCandlesPerRequest` | Numero massimo di candele restituibili in una singola richiesta (rate-limit). |
| `m` | `Task&lt;List&lt;Ohlcv&gt;&gt; FetchOhlcvAsync(string symbol, string timeframe, long since, int limit, CancellationToken ct = default)` | Scarica candele OHLCV pubbliche. Simbolo canonico, es. "BTC/USDT". Timeframe canonico, es. "1h". Timestamp di partenza in millisecondi Unix (UTC). Numero massimo di candele desiderate (verra' limitato a ). |
| `m` | `Task&lt;List&lt;string&gt;&gt; GetSymbolsAsync(CancellationToken ct = default)` | Elenco dei simboli negoziabili in forma canonica "BASE/QUOTE". |
| `m` | `Task&lt;bool&gt; TestConnectionAsync(CancellationToken ct = default)` | Verifica la raggiungibilita' dell'exchange (endpoint pubblico). |
| `m` | `Task&lt;PlaceOrderResult&gt; PlaceOrderAsync(PlaceOrderRequest request, CancellationToken ct = default)` | Piazza un ordine. Su errore di rete imposta NetworkUncertain . |
| `m` | `Task&lt;CancelOrderResult&gt; CancelOrderAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)` | Cancella un ordine per client order id. |
| `m` | `Task&lt;List&lt;OpenOrder&gt;&gt; GetOpenOrdersAsync(string symbol, TradingCredentials creds, CancellationToken ct = default)` | Ordini aperti (per riconciliazione dopo un errore di rete). |
| `m` | `Task&lt;OrderStatusResult&gt; GetOrderStatusAsync(string symbol, string clientOrderId, TradingCredentials creds, CancellationToken ct = default)` | Stato di un ordine per client order id, INCLUSI gli ordini già eseguiti/terminati (a differenza di ). È il lookup autorevole per la riconciliazione dopo un . |
| `m` | `Task&lt;AccountBalance&gt; GetBalanceAsync(TradingCredentials creds, CancellationToken ct = default)` | Saldi del conto. |
| `m` | `Task&lt;SymbolFilters&gt; GetSymbolFiltersAsync(string symbol, bool testnet, CancellationToken ct = default)` | Filtri di trading del simbolo (LOT_SIZE / PRICE_FILTER / minNotional). Endpoint pubblico. |

## `ProcioneMGR/Services/Exchanges/IExchangeClientFactory.cs`

### 🔌 `IExchangeClientFactory`

> Restituisce l'implementazione per un dato exchange.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IExchangeClient Create(ExchangeName exchange)` | — |
| `m` | `IExchangeClient Create(string exchangeName)` | Variante che accetta il nome testuale (case-insensitive), utile dalla UI/servizi. |
| `m` | `IFuturesExchangeClient CreateFutures(ExchangeName exchange)` | Variante futures: stessa istanza concreta di , vista come . |
| `m` | `IFuturesExchangeClient CreateFutures(string exchangeName)` | — |

## `ProcioneMGR/Services/Exchanges/Ohlcv.cs`

### ▫️ `Ohlcv` `(`

> Candela OHLCV "di trasporto", restituita dai client exchange. Disaccoppiata dall'entita' di persistenza : il layer di ingestione mappa da questo DTO all'entita'.

| | Firma | Descrizione |
|---|---|---|
| `p` | `long TimestampMs` | Timestamp di apertura in millisecondi Unix (UTC). |

## `ProcioneMGR/Services/Exchanges/Timeframes.cs`

### 📦 `Timeframes`

> Timeframe canonici dell'applicazione e relativa durata. Ogni client exchange traduce questi valori nel proprio dialetto (es. Bitget "1day" per "1d").

| | Firma | Descrizione |
|---|---|---|
| `p` | `IReadOnlyDictionary&lt;string, TimeSpan&gt; Supported` | Mappa timeframe canonico -&gt; durata. |
| `m` | `bool IsSupported(string timeframe)` | — |
| `m` | `long ToMilliseconds(string timeframe)` | Durata del timeframe in millisecondi. Lancia se non supportato. |

# `Services/MarketData/`

## `ProcioneMGR/Services/MarketData/BinanceStreamMapper.cs`

### 📦 `BinanceStreamMapper` `: IExchangeStreamMapper`

> Dialetto degli stream pubblici Binance (combined streams). Due canali per sottoscrizione: - {sym}@bookTicker : best bid/ask ad ogni variazione — la fonte dei tick per le uscite protettive; - {sym}@kline_{tf} : candele, di cui si usa SOLO quella con k.x == true (chiusa). Le candele NON chiuse vengono scartate di proposito: una candela in formazione ha High/Low provvisori, e darla in pasto alle strategie significherebbe valutare segnali su una barra che può ancora cambiare — l'esatto contrario di ciò che il backtest valida.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `p` | `string? HeartbeatFrame` | Binance manda frame di ping di protocollo: ClientWebSocket risponde da solo. |
| `p` | `TimeSpan HeartbeatInterval` | — |
| `m` | `string ToStreamSymbol(string canonical)` | "BTC/USDT" -&gt; "btcusdt" (gli stream vogliono il simbolo minuscolo). |
| `m` | `Uri BuildEndpoint(IReadOnlyCollection&lt;StreamSubscription&gt; subscriptions)` | — |
| `m` | `IReadOnlyList&lt;string&gt; BuildSubscribeFrames(IReadOnlyCollection&lt;StreamSubscription&gt; subscriptions)` | Binance codifica tutto nell'URL: nessun frame di sottoscrizione da mandare. |
| `m` | `StreamEvent Parse(string raw, IReadOnlyDictionary&lt;string, StreamSubscription&gt; byExchangeSymbol)` | — |

## `ProcioneMGR/Services/MarketData/BitgetStreamMapper.cs`

### 📦 `BitgetStreamMapper` `: IExchangeStreamMapper`

> Dialetto degli stream pubblici Bitget v2. SOLO IL CANALE ticker , deliberatamente. Il canale delle candele di Bitget non espone un flag "barra chiusa" come il k.x di Binance: pubblica ripetutamente la candela IN CORSO, e per dedurne la chiusura bisognerebbe aspettare la comparsa di quella successiva e considerare chiusa la precedente. È un'inferenza fragile — un buco di connessione o un riordino la fanno sbagliare — e il premio è piccolo: anticipare di qualche minuto un INGRESSO. Il valore vero di questo feed sono le USCITE protettive, che vivono sui tick e qui ci sono tutte. Le candele Bitget continuano quindi ad arrivare dal ciclo REST già esistente, invariato. NB sul Demo Trading: i dati di mercato Bitget sono PUBBLICI e condivisi fra ambiente reale e demo (stessa lezione già appresa in BitgetClient.PublicMarketProductType ), quindi il productType demo "S..." non va mai usato qui — s…

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `p` | `string? HeartbeatFrame` | Bitget richiede un ping APPLICATIVO: senza, chiude la connessione dopo ~30s di silenzio. |
| `p` | `TimeSpan HeartbeatInterval` | — |
| `m` | `string ToStreamSymbol(string canonical)` | "BTC/USDT" -&gt; "BTCUSDT". |
| `m` | `Uri BuildEndpoint(IReadOnlyCollection&lt;StreamSubscription&gt; subscriptions)` | — |
| `m` | `IReadOnlyList&lt;string&gt; BuildSubscribeFrames(IReadOnlyCollection&lt;StreamSubscription&gt; subscriptions)` | — |
| `m` | `StreamEvent Parse(string raw, IReadOnlyDictionary&lt;string, StreamSubscription&gt; byExchangeSymbol)` | — |

## `ProcioneMGR/Services/MarketData/IExchangeStreamMapper.cs`

### 🔌 `IExchangeStreamMapper`

> Traduce fra il modello interno del feed e il dialetto WebSocket di un exchange: come si compone l'URL, quali frame di sottoscrizione mandare dopo la connessione, come si legge un messaggio e come si tiene viva la connessione a livello applicativo. Solo MARKET DATA PUBBLICO: nessun mapper firma richieste né tocca credenziali. Da qui discende che il feed funziona anche dove il trading è precluso (es. Binance Futures per un utente UE soggetto a MiCA), perché nessuno dei due exchange richiede una API key per gli stream pubblici.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `m` | `Uri BuildEndpoint(IReadOnlyCollection&lt;StreamSubscription&gt; subscriptions)` | Endpoint a cui connettersi per queste sottoscrizioni. |
| `m` | `IReadOnlyList&lt;string&gt; BuildSubscribeFrames(IReadOnlyCollection&lt;StreamSubscription&gt; subscriptions)` | Frame da inviare subito dopo la connessione. Vuoto per gli exchange che codificano le sottoscrizioni nell'URL (Binance); popolato per quelli che le negoziano (Bitget). |
| `p` | `string? HeartbeatFrame` | Frame di keep-alive applicativo, oppure null se l'exchange si accontenta dei ping di protocollo (a cui risponde da solo). |
| `p` | `TimeSpan HeartbeatInterval` | Intervallo del keep-alive applicativo. Ignorato se è null. |
| `m` | `StreamEvent Parse(string raw, IReadOnlyDictionary&lt;string, StreamSubscription&gt; byExchangeSymbol)` | Interpreta un messaggio grezzo. Non lancia MAI: un frame inatteso, malformato o semplicemente non interessante (ack di sottoscrizione, pong, evento di un canale che non usiamo) ritorna . Un parser che lancia farebbe cad… |

## `ProcioneMGR/Services/MarketData/IWebSocketTransport.cs`

### 🔌 `IWebSocketTransport` `: IAsyncDisposable`

> Canale WebSocket ridotto all'osso. Esiste come interfaccia per una ragione sola: rendere TESTABILE senza rete né server finto — i test iniettano un transport che consegna messaggi da una coda e simula disconnessioni. La logica difficile (riconnessione, backoff, staleness, resubscribe) sta nel feed, non qui.

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task ConnectAsync(Uri uri, CancellationToken ct)` | — |
| `m` | `Task SendAsync(string message, CancellationToken ct)` | — |
| `m` | `Task&lt;string?&gt; ReceiveAsync(CancellationToken ct)` | Prossimo messaggio testuale completo, oppure null se il canale si è chiuso. |

### 🔌 `IWebSocketTransportFactory`

> Crea un transport nuovo per ogni tentativo di connessione (un ClientWebSocket non si riusa dopo la chiusura).

| | Firma | Descrizione |
|---|---|---|
| `m` | `IWebSocketTransport Create()` | — |

### 📦 `ClientWebSocketTransport` `: IWebSocketTransport`

> Implementazione su della BCL. Nessuna dipendenza esterna: la libreria SuperSocket non serve, .NET ha già tutto — e i frame di ping del server ricevono risposta pong automaticamente dallo stack, quindi il keep-alive di protocollo è gratis. (Il ping APPLICATIVO richiesto da alcuni exchange, es. Bitget, resta compito del mapper.)

| | Firma | Descrizione |
|---|---|---|
| `c` | `ClientWebSocketTransport()` | — |
| `m` | `Task ConnectAsync(Uri uri, CancellationToken ct)` | — |
| `m` | `Task SendAsync(string message, CancellationToken ct)` | — |
| `m` | `Task&lt;string?&gt; ReceiveAsync(CancellationToken ct)` | — |
| `m` | `ValueTask DisposeAsync()` | — |

### 📦 `ClientWebSocketTransportFactory` `: IWebSocketTransportFactory`

> Factory di produzione.

| | Firma | Descrizione |
|---|---|---|
| `m` | `IWebSocketTransport Create()` | — |

## `ProcioneMGR/Services/MarketData/LiquidationAccumulation.cs`

### 🧾 `LiquidationEvent` `(DateTime TimestampUtc, string BaseTicker, bool LongLiquidated, decimal Notional);`

> Una liquidazione forzata: quando, che ticker, quale LATO è stato liquidato, quanto nozionale. true = liquidato un LONG (l'exchange VENDE, side "SELL"); false = liquidato uno short.

### 📦 `BinanceLiquidationMapper`

> [F4 roadmap frontiere-profitto] Parsing dello stream pubblico Binance futures !forceOrder@arr : OGNI ordine di liquidazione del mercato, keyless. Il dato non è storico — su questo stream esiste solo il presente — quindi il suo intero valore è l'ACCUMULO: fra sei mesi le serie per-simbolo datano le cascate e alimentano feature di fragilità.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string StreamUri` | Stream market-wide (un solo socket per tutto il listino futures USDT-M). |
| `m` | `LiquidationEvent? Parse(string json)` | Payload → evento. Null per messaggi non-forceOrder, simboli non /USDT o payload malformati (uno stream pubblico non merita mai un'eccezione che ammazza il worker). Nozionale = quantità × prezzo medio di esecuzione (fall… |

### 🧾 `LiquidationBucket` `(string BaseTicker, DateTime HourUtc)`

> Totali di un'ora per un ticker (il "secchio" che il worker scrive a ogni flush).

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal LongNotional` | — |
| `p` | `decimal ShortNotional` | — |
| `p` | `int LongCount` | — |
| `p` | `int ShortCount` | — |

### 📦 `LiquidationAggregator`

> Aggregazione per (ticker, ora UTC). Thread-safe per il pattern del worker (loop di lettura e flush sullo stesso task, ma il lock costa nulla e toglie ogni dubbio). I secchi restano in memoria finché non li ritira: il flush è idempotente perché scrive il TOTALE corrente del secchio (upsert), non i delta.

| | Firma | Descrizione |
|---|---|---|
| `m` | `void Add(LiquidationEvent e)` | — |
| `m` | `IReadOnlyList&lt;LiquidationBucket&gt; Snapshot()` | Fotografia dei secchi correnti (copie: il chiamante può scriverle senza lock). |
| `m` | `void PruneBefore(DateTime cutoffUtc)` | Ritira i secchi delle ore ormai chiuse e già scritte (il flush li ha resi definitivi). |

## `ProcioneMGR/Services/MarketData/LiquidationSyncWorker.cs`

### 📦 `LiquidationsOptions`

> Configurazione dell'accumulo liquidazioni (sezione "Liquidations").

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool Enabled` | Default ON: stream pubblico keyless in sola lettura, e il dato NON è ricostruibile a posteriori — ogni giorno spento è un giorno di storia perso (stessa logica dell'accumulo OI/long-short del Sentiment 2.0). Spegnibile … |
| `p` | `int FlushMinutes` | Minuti fra due flush su DB. |
| `p` | `int StaleSeconds` | Secondi senza messaggi oltre i quali il canale si considera guasto. 900s (15 min), NON 120: trovato girando dal vivo la prima notte — !forceOrder@arr è un feed di EVENTI sparsi (le liquidazioni di TUTTO il listino), e i… |
| `p` | `int BlockedRetryMinutes` | [2026-07-24] Minuti di pausa quando l'endpoint futures risulta bloccato (connesso ma muto — vedi il ramo endpointLikelyBlocked). Lungo di proposito: evita il churn di riconnessione a vuoto quando i dati non arriveranno … |

### 📦 `LiquidationSyncWorker` `(`

> [F4 roadmap frontiere-profitto] Accumula le liquidazioni forzate del mercato futures Binance (stream pubblico !forceOrder@arr , un socket per tutto il listino) in per (simbolo, ora): nozionale e conteggio per lato. È un INVESTIMENTO, non una feature: oggi il dato non decide nulla (nessun consumo decisionale); fra mesi le serie datano le cascate per l'event-study e alimentano feature di fragilità — che passeranno dal gate come ogni altra ipotesi. La retention del sentiment ESENTA questa fonte (vedi SentimentSyncWorker.PurgeAsync ): l'accumulo è l'intero valore. Robustezza: riconnessione con backoff esponenziale (5s→60s), canale silente oltre trattato come guasto, flush IDEMPOTENTE (upsert del totale del secchio, mai delta) così un crash fra due flush non duplica nulla.

| | Firma | Descrizione |
|---|---|---|
| `p` | `LiquidationAggregator Aggregator` | — |
| `p` | `bool IsConnected` | Vero quando il socket è aperto e sta accumulando (lo legge il pannello /admin/autonomy). |
| `p` | `long TotalMessages` | Messaggi ricevuti da quando l'applicazione è partita. |
| `m` | `Task ExecuteAsync(CancellationToken ct)` | — |
| `m` | `void ProcessMessage(string json)` | — |
| `m` | `bool IsEndpointLikelyBlocked(long totalMessagesEver, int consecutiveSilentConnects)` | [2026-07-24] L'endpoint è "probabilmente bloccato" (connesso ma muto) quando NON è mai arrivato un solo messaggio E si sono incatenate ≥3 connessioni chiuse a zero messaggi. Le DUE condizioni insieme: un feed genuinamen… |
| `m` | `Task FlushAsync(CancellationToken ct)` | Upsert dei secchi correnti: la riga (Source, Metric, Symbol, ora) riceve il TOTALE del secchio — idempotente per costruzione. Dopo il flush, i secchi delle ore chiuse da più di 2 ore vengono ritirati dalla memoria (il l… |

## `ProcioneMGR/Services/MarketData/RealtimeMarketDataModels.cs`

### ▫️ `StreamSubscription` `(`

> Una sottoscrizione al feed real-time: exchange + simbolo canonico ("BTC/USDT") + timeframe canonico ("5m") + tipo di mercato. Il timeframe serve solo allo stream delle candele: i tick di prezzo non ne hanno uno.

### ▫️ `PriceTick` `(`

> Aggiornamento del book in cima (best bid / best ask) per un simbolo. è il prezzo usato per valutare le uscite protettive. Scelta deliberata rispetto al lato "onesto" (bid per chiudere un long, ask per chiudere uno short): su coppie liquide il mezzo spread è trascurabile, mentre usare il lato farebbe scattare gli stop di long e short in momenti diversi sullo stesso mercato, e renderebbe il livello sensibile a un allargamento momentaneo del book. Il prezzo di ESECUZIONE resta comunque quello riportato dall'exchange sul market order di chiusura, non questo.

| | Firma | Descrizione |
|---|---|---|
| `p` | `decimal Mid` | — |
| `p` | `decimal SpreadPercent` | Spread relativo al mezzo, in percentuale. Usato per scartare quotazioni implausibili. |
| `m` | `bool IsPlausible(decimal maxSpreadPercent)` | Quotazione utilizzabile per una decisione. Un book incrociato (ask &lt; bid), un prezzo non positivo o uno spread abnorme indicano una quotazione stantia o corrotta — la stessa classe di spazzatura che il bug B1 ha most… |

### ▫️ `BarClosed` `(`

> Candela CHIUSA notificata dallo stream (Binance: k.x == true ). Trasporta l'OHLCV completo, quindi può essere consegnata al motore senza attendere il ciclo REST.

| | Firma | Descrizione |
|---|---|---|
| `m` | `OhlcvData ToOhlcv()` | — |

### ▫️ `StreamEvent` `(PriceTick? Tick, BarClosed? Bar)`

> Evento emesso dal parser di uno stream: un tick, una candela chiusa, o niente di utile.

| | Firma | Descrizione |
|---|---|---|
| `p` | `StreamEvent None` | — |
| `m` | `StreamEvent FromTick(PriceTick tick)` | — |
| `m` | `StreamEvent FromBar(BarClosed bar)` | — |
| `p` | `bool IsEmpty` | — |

### 📦 `RealtimeFeedOptions`

> Configurazione del feed real-time, sezione MarketData:Realtime di appsettings.json. I default sono pensati per essere INERTI: a feature spenta il comportamento della piattaforma è identico a prima del feed.

| | Firma | Descrizione |
|---|---|---|
| `k` | `string SectionName` | — |
| `p` | `bool Enabled` | Interruttore generale. DEFAULT FALSE: il feed è additivo rispetto alla sincronizzazione REST già esistente, quindi spegnerlo riporta esattamente al comportamento a sole candele. |
| `p` | `bool DriveProtectiveExits` | Se true i tick alimentano le uscite protettive del motore. Separato da apposta: permette di tenere il feed acceso in sola OSSERVAZIONE (log e metriche, nessuna decisione) per convincersi che i prezzi siano sani prima di… |
| `p` | `int SubscriptionRefreshSeconds` | Ogni quanto rileggere le corsie per aggiornare l'insieme delle sottoscrizioni. |
| `p` | `int StaleAfterSeconds` | Silenzio oltre il quale il feed è considerato STALE. Non blocca nulla (la sincronizzazione REST resta comunque attiva e indipendente), ma smette di essere considerato una fonte viva e genera un allarme: non si opera mai… |
| `p` | `int ReconnectInitialDelayMs` | Attesa iniziale prima di un tentativo di riconnessione. |
| `p` | `int ReconnectMaxDelayMs` | Tetto dell'attesa di riconnessione (backoff esponenziale con jitter). |
| `p` | `decimal MaxSpreadPercent` | Vedi : oltre questo spread il tick è scartato. |

## `ProcioneMGR/Services/MarketData/RealtimePriceWorker.cs`

### 📦 `RealtimePriceWorker` `(`

> Orchestratore del feed real-time: UNO per flotta, non uno per corsia. Tiene una connessione per exchange, ricava le sottoscrizioni dalle corsie effettivamente in esecuzione, e instrada: - i TICK verso delle corsie che operano quel simbolo (solo uscite protettive: il motore non apre mai da un tick); - le CANDELE CHIUSE verso la tabella OHLCV e poi verso il motore, senza attendere il ciclo REST. Il feed è ADDITIVO: MarketDataSyncWorker e TradingWorker restano attivi e indipendenti. Non c'è quindi nessun "fallback" da attivare quando il WebSocket cade — il percorso a candele REST non ha mai smesso di funzionare. Quello che serve, e che c'è, è non CREDERSI aggiornati quando non lo si è: da qui la watchdog di staleness che allerta.

### 🧾 `LaneRoute` `(int LaneId, ExchangeName Exchange, string Symbol, string Timeframe, MarketType MarketTyp…`

> Istantanea di ciò che ogni corsia sta operando, aggiornata a ogni refresh.

| | Firma | Descrizione |
|---|---|---|
| `p` | `bool IsRunning` | Vero mentre una sessione di feed è attiva. È l'osservabile con cui RealtimeFeedSwitchTests verifica che l'interruttore apra e chiuda DAVVERO le connessioni: senza, quella prova si ridurrebbe a «il metodo non ha lanciato… |
| `m` | `Task ExecuteAsync(CancellationToken stoppingToken)` | Ciclo esterno: sorveglia l'INTERRUTTORE e apre/chiude una sessione di feed di conseguenza. Prima del 2026-07-29 questo metodo usciva subito con Enabled=false e non tornava più: accendere il feed richiedeva un riavvio de… |
| `m` | `bool ShouldAlertStale(FeedHealth health, TimeSpan threshold, DateTime nowUtc, DateTime sessionStartedUtc)` | Quando un canale silenzioso merita un allarme. «Non ha ancora cominciato» NON è «ha smesso». Appena connesso, il primo messaggio può tardare più della soglia in tutta legittimità — mercato calmo, handshake, sottoscrizio… |
| `m` | `bool ShouldAlertStale(DateTime? lastUtc, TimeSpan threshold, DateTime nowUtc, DateTime graceStartUtc)` | [G2] Il cuore della regola, riusato per-feed (overload sopra, coi suoi test) e per-serie: silenzio da sempre dentro la grazia ⇒ non ancora un allarme; tutto il resto del silenzio oltre soglia ⇒ allarme, incluso chi non … |

## `ProcioneMGR/Services/MarketData/SymbolCatalog.cs`

### ▫️ `SeriesKey` `(string Symbol, string Timeframe);`

> Una serie nota al catalogo: la coppia (simbolo, timeframe).

### 🔌 `ISymbolCatalog`

> [E-04, Fase 2 PRD-RISANAMENTO] L'elenco dei simboli noti, in UN posto solo. Prima SETTE pagine eseguivano ciascuna per conto proprio db.OhlcvData.Select(c =&gt; c.Symbol).Distinct() — una scansione (pur solo-indice) su ~12M righe per ottenere ~30 stringhe, ripetuta a ogni apertura di pagina, con la POLITICA dei simboli decisa implicitamente da ogni copia. POLITICA DICHIARATA: unione di TrackedSeries (le serie tracciate ora — copre quelle appena aggiunte e ancora senza candele) e delle serie storiche presenti in OhlcvData (una serie rimossa dalla watchlist resta selezionabile per l'analisi: i suoi dati esistono). È la stessa semantica che le sette copie producevano di fatto, ora scritta e testabile. La stessa politica vale per le COPPIE di : coppie realmente presenti a DB più quelle tracciate — MAI il prodotto cartesiano simboli × timeframe, che mentirebbe sulle serie senza dati.

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;IReadOnlyList&lt;string&gt;&gt; GetKnownSymbolsAsync(CancellationToken ct = default)` | Simboli noti, ordinati. Cache condivisa: la scansione grossa gira al più una volta per finestra. |
| `m` | `ValueTask&lt;IReadOnlyList&lt;SeriesKey&gt;&gt; GetKnownSeriesAsync(CancellationToken ct = default)` | Le coppie (simbolo, timeframe) note, ordinate per simbolo e poi timeframe. Stessa cache e stessa politica dei simboli: serie con dati a DB più quelle tracciate, non il cartesiano. |
| `m` | `void Invalidate()` | Invalida la cache (es. dopo l'aggiunta di una serie in watchlist). |

### 📦 `SymbolCatalog` `(`

> Singleton con cache a scadenza (default 5 minuti): i simboli cambiano solo quando si aggiunge o ingerisce una serie nuova, non a ogni apertura di pagina. Il costo passa da "una scansione per pagina per utente" a "una scansione per finestra per processo". La UI può forzare il refresh via (lo fa la watchlist al salvataggio). Simboli e coppie vivono in UNO snapshot solo, caricato in un colpo: i simboli sono la proiezione delle coppie, quindi chiedere entrambi non raddoppia la scansione.

### 🧾 `Snapshot` `(IReadOnlyList&lt;string&gt; Symbols, IReadOnlyList&lt;SeriesKey&gt; Series);`

| | Firma | Descrizione |
|---|---|---|
| `m` | `ValueTask&lt;IReadOnlyList&lt;string&gt;&gt; GetKnownSymbolsAsync(CancellationToken ct = default)` | — |
| `m` | `ValueTask&lt;IReadOnlyList&lt;SeriesKey&gt;&gt; GetKnownSeriesAsync(CancellationToken ct = default)` | — |
| `m` | `void Invalidate()` | — |

### 📦 `SeriesKeyIgnoreCase` `: IEqualityComparer&lt;SeriesKey&gt;`

> Uguaglianza case-insensitive sulla coppia, coerente con l'unione dei simboli.

| | Firma | Descrizione |
|---|---|---|
| `p` | `SeriesKeyIgnoreCase Instance` | — |
| `m` | `bool Equals(SeriesKey x, SeriesKey y)` | — |
| `m` | `int GetHashCode(SeriesKey k)` | — |

## `ProcioneMGR/Services/MarketData/WebSocketPriceFeed.cs`

### 🧾 `FeedHealth` `(`

> Stato osservabile di una connessione del feed, per UI, metriche e watchdog.

| | Firma | Descrizione |
|---|---|---|
| `m` | `bool IsStale(TimeSpan threshold, DateTime nowUtc)` | True se il canale tace da troppo: la fonte non è più considerabile viva. |

### 🧾 `SeriesHealth` `(`

> [G2] Salute di UNA serie sottoscritta: ultimo evento utile (tick/candela) e istante di sottoscrizione. Complementa : quello dice se il CANALE è vivo, questo se il SIMBOLO consegna — e basta un simbolo vivo a mascherare il silenzio degli altri.

### 📦 `WebSocketPriceFeed` `(`

> Una connessione WebSocket verso un exchange, mantenuta viva a oltranza. Responsabilità: connettere, sottoscrivere, leggere, riconnettere con backoff esponenziale e jitter, ripresentare le sottoscrizioni dopo ogni riconnessione, e RICICLARE la connessione quando il set di sottoscrizioni cambia (gli exchange le negoziano solo al connect: senza riciclo un cambio resterebbe lettera morta). Il PARSING è del mapper, il ROUTING è del worker: qui si emettono solo eventi già tipizzati. Il jitter sul backoff non è ornamentale: senza, tre corsie che perdono la connessione insieme (tipico — la rete cade per tutte) ritenterebbero nello stesso istante a ogni giro, martellando l'exchange in sincrono proprio mentre è in difficoltà.

| | Firma | Descrizione |
|---|---|---|
| `p` | `ExchangeName Exchange` | — |
| `p` | `Action&lt;PriceTick&gt;? TickReceived` | Emesso per ogni tick valido. I gestori NON devono lanciare: un'eccezione qui è loggata e ignorata. |
| `p` | `Action&lt;BarClosed&gt;? BarClosed` | Emesso per ogni candela CHIUSA (solo sugli exchange che la segnalano esplicitamente). |
| `p` | `FeedHealth Health` | — |
| `p` | `IReadOnlyList&lt;SeriesHealth&gt; SeriesHealthSnapshot` | [G2] Salute PER SERIE: per ogni simbolo sottoscritto, l'ultimo evento utile ricevuto (null = mai, da quando è sottoscritto) e l'istante di sottoscrizione — il riferimento della grazia: un simbolo appena aggiunto non ha … |
| `m` | `bool UpdateSubscriptions(IReadOnlyList&lt;StreamSubscription&gt; subscriptions)` | Aggiorna l'insieme delle sottoscrizioni. Se è CAMBIATO rispetto a quello attivo, la connessione corrente viene riciclata QUI, cancellandone il CTS: Binance codifica le sottoscrizioni nell'URL e Bitget invia i frame solo… |
| `m` | `Task RunAsync(CancellationToken ct)` | Ciclo di vita della connessione: gira finché non viene cancellato. Ogni caduta è un evento ATTESO, non un errore fatale — si riprova, per sempre, con attesa crescente. |
| `m` | `TimeSpan BackoffDelay(int attempt)` | Backoff esponenziale con jitter pieno, limitato dal tetto configurato. |

# `ProcioneMGR.Trading/`

## `ProcioneMGR.Trading/Program.cs`

### 📦 `Program` `;`

## `ProcioneMGR.Trading/SharedSecretAuthInterceptor.cs`

### 📦 `SharedSecretAuthInterceptor` `(`

> Autorizzazione applicativa sul gRPC di trading: fino a qui l'unico confine era la NetworkPolicy K8s (topologia di rete, non applicativa) — un confine noto per avere un limite documentato ( kubectl port-forward lo scavalca, vedi infra/k8s/README.md). ConfirmOrder / StartLane possono muovere denaro vero: un secondo fattore applicativo, anche solo un segreto condiviso, alza il costo di uno sbaglio di configurazione della rete da "ordini reali" a "richiesta rifiutata". FAIL-CLOSED per scelta, non fail-open: se il segreto non è configurato lato server, OGNI chiamata viene rifiutata — mai un servizio "protetto solo se qualcuno si ricorda di attivarlo". Stesso principio già in uso per la master key ( Program.cs , fail-fast in Production).

## `ProcioneMGR.Trading/TradingCommandServiceImpl.cs`

### 📦 `TradingCommandServiceImpl` `(`

> Implementazione gRPC dei comandi di trading (Fase 2b). Ogni RPC risolve l'istanza keyed di della lane indicata nella request e le delega la chiamata: nessuna logica di dominio vive qui, è un adattatore fra il filo e il motore riusato verbatim. SICUREZZA: sblocca il piazzamento REALE di un ordine Live e può avviare una sessione con soldi veri. A questo livello non c'è l'equivalente esatto dell'[Authorize] di Trading.razor (qui il chiamante è un processo, non un utente autenticato), ma DUE livelli indipendenti proteggono comunque ogni rpc (P1-6, 2026-07-17): la NetworkPolicy che accetta ingress solo dal pod procionemgr-ui (infra/k8s/trading/networkpolicy.yaml) e, registrato globalmente su AddGrpc in Program.cs, — un segreto condiviso verificato a tempo costante, fail-closed se non configurato. Non esporre comunque questo servizio altrove: nessuno dei due è un sostituto di autenticazione/a…

| | Firma | Descrizione |
|---|---|---|
| `m` | `Task&lt;GetLaneStatusResponse&gt; GetLaneStatus(GetLaneStatusRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;GetOpenPositionsResponse&gt; GetOpenPositions(GetOpenPositionsRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;GetPerformanceResponse&gt; GetPerformance(GetPerformanceRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;StartLaneResponse&gt; StartLane(StartLaneRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;StopLaneResponse&gt; StopLane(StopLaneRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;EmergencyStopResponse&gt; EmergencyStop(EmergencyStopRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;ClosePositionResponse&gt; ClosePosition(ClosePositionRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;CloseAllPositionsResponse&gt; CloseAllPositions(CloseAllPositionsRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;ConfirmOrderResponse&gt; ConfirmOrder(ConfirmOrderRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;RejectOrderResponse&gt; RejectOrder(RejectOrderRequest request, ServerCallContext context)` | — |
| `m` | `Task&lt;GetEngineConfigResponse&gt; GetEngineConfig(GetEngineConfigRequest request, ServerCallContext context)` | Il guscio chiede al motore la SUA configurazione. Fino al 2026-07-29 la mostrava indovinando dal proprio file, che con guscio e motore in processi diversi non è lo stesso. |
| `m` | `Task&lt;SetEngineConfigResponse&gt; SetEngineConfig(SetEngineConfigRequest request, ServerCallContext context)` | Scrittura di UNA sezione, filtrata dall'allow-list chiusa di e validata con le stesse regole dei pannelli. Un rifiuto è una precondizione violata, non un guasto: va sul filo come PermissionDenied (sezione non consentita… |
