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

## Come funziona (flusso del codice)

### Copie locali e salvataggio (righe 470–539, 550–560)
La pagina lavora su **copie locali** delle opzioni (clone via JSON round-trip): si parte da
`IOptionsMonitor<T>.CurrentValue` e si scrive solo al Salva — così i campi non "saltano
sotto le dita" quando il monitor ricarica il file appena scritto.
`IAppConfigWriter.SaveSectionAsync(section, model)` **sostituisce l'intera sezione** in
`appsettings.json`: per questo i POCO locali (`MarketDataConfig`) includono anche i toggle
non esposti nel form (`UseRemoteIngestion`, `RemoteIngestionUrl`) — senza il round-trip, un
salvataggio da questa pagina li cancellerebbe dal file (commento alle righe 497–501).

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
