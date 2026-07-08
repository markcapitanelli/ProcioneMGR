# Gli ultimi 3 passi verso l'autonomia — Report di implementazione

Data: 2026-07-05 · Build: 0 errori, 0 warning nuovi · Test: **507/507** (28 nuovi) · Verificato dal vivo su PostgreSQL.

Tre funzionalità che chiudono il cerchio dell'autonomia, tutte dietro il confine di sicurezza non negoziabile
**research → esecuzione** e **Paper/Testnet → Live sempre manuale**.

---

## Punto 1 — Scheduler continuo con ri-applica automatica

- **`Services/Ensemble/EnsembleComparator.cs`** (nuovo): confronto oggettivo `corrente vs candidato` con **hysteresis**
  (default +10% Sharpe, o RF95 -15% a Sharpe non inferiore) + floor strutturale (min 2 gambe, 2 simboli). Se non c'è
  ensemble corrente applica il primo che supera il floor. `EnsembleSummary`/`LegSummary`/`EnsembleComparison` +
  `EnsembleComparatorOptions`.
- **`Services/Pipeline/PipelineApplier.cs`** (nuovo): estrae la logica di "Applica al Trading" da `Pipeline.razor` in un
  servizio riusabile (`IPipelineApplier`) — **una sola implementazione** usata sia dalla UI sia dallo scheduler. Distribuisce
  le gambe sulle corsie 0/1/2 con parametri validati (`BestStopVariant`) + bracket SL/TP automatico. Espone anche
  `GetCurrentEnsembleSummaryAsync` e `SummarizeRecommendation` per il confronto.
- **`Services/Pipeline/PipelineSchedulerWorker.cs`** (esteso): dopo l'evaluation delle config schedulate, ogni tick
  processa i run **Scheduled completati** non ancora valutati (left-anti-join su un artifact-marker), chiama supervisore +
  comparatore, e se entrambi approvano **ri-applica l'ensemble** (atomico via `SemaphoreSlim` globale). Ogni decisione è
  registrata come `PipelineArtifact` (Kind `AutoReapplyDecision`) — idempotente, e fonte per la UI.
- **`Components/Pages/Pipeline.razor`**: `ApplyRecommendationAsync` ora delega a `IPipelineApplier` (rimossi i vecchi helper
  inline); nuovo pannello "Giudizio del supervisore & ri-applica automatica" nel dettaglio run.
- Config: **`AutoReapply`** (`Enabled=false` di default — safety: finché non lo abiliti, lo scheduler lancia i run ma non
  schiera mai un ensemble da solo) + **`EnsembleComparator`** (soglie).

## Punto 2 — Layer AI di supervisione

- **`Services/Agents/IPipelineSupervisorAgent.cs`** (nuovo): `SupervisorJudgment` con **`ApproveReplacement`** (veto),
  `Summary`/`Suggestions`/`Concerns`/`Reasoning`. + `SupervisorAgentOptions`.
- **`Services/Agents/LoggingSupervisorAgent.cs`** (nuovo, default): nessuna AI, approva sempre → decide il comparatore.
- **`Services/Agents/ClaudeSupervisorAgent.cs`** (nuovo, opzionale): **riusa l'`ILlmClient` esistente** (SDK Anthropic, key da
  `ANTHROPIC_API_KEY`). Timeout 30s, degrada con grazia: key mancante / timeout / errore → `ApproveReplacement=true`
  (decidono le metriche). **Può solo porre un veto, mai forzare una sostituzione**, mai avviare trading.
- DI in `Program.cs`: registra Logging o Claude in base a `PipelineSupervisor:Provider`.

## Punto 3 — Auto-promozione Paper→Testnet (MAI a Live)

- **`Services/Trading/PromotionEvaluator.cs`** (nuovo): `IPromotionEvaluator` + logica **pura** `Decide(LaneMetrics, mode, ...)`
  (testabile in isolamento). Promuove Paper→Testnet se TUTTI i criteri sono soddisfatti (Sharpe≥0.8, ≥30 trade, DD≤15%,
  ≥3 settimane, win≥45%), con blocco assoluto DD>20%. Retrocede Testnet→Paper se l'edge svanisce. **`SuggestedMode` non è
  MAI Live**; le corsie in Live non vengono toccate.
- **`Services/Trading/LanePromoter.cs`** (nuovo): applica il cambio modalità (stop→restart della corsia keyed) + audit
  visibile all'utente. **Throw** se richiesto il passaggio a Live (defense in depth). Testnet usa le credenziali demo già
  configurate; se mancano, errore chiaro e corsia lasciata ferma.
- **`Services/Trading/PromotionWorker.cs`** (nuovo): `BackgroundService`, rivaluta ogni 6h; agisce solo su corsie attive.
- **`Components/Pages/Trading.razor`**: sezione "Promozioni" (tabella per-corsia + "Promuovi a Testnet" quando pronta +
  **"Promuovi a Live" sempre disabilitato** con tooltip).
- **`Components/Pages/Dashboard.razor`**: widget (solo Admin/Manager) con le corsie che richiedono attenzione.
- Config: **`PromotionEvaluator`** (soglie + intervalli).

---

## Decisioni architetturali

1. **Nessuna migrazione EF.** Il giudizio del supervisore e la decisione di ri-applica sono persistiti come `PipelineArtifact`
   (stesso pattern del `LlmAdvisory` già esistente), non come nuova colonna `PipelineRun.SupervisorJudgmentJson`. Evita una
   fragile doppia migrazione SQLite+Postgres ed è coerente col codebase. `EnsembleConfiguration.ExpectedRiskFactor95` è un
   campo JSON (colonna esistente), quindi anch'esso senza migrazione. **Deviazione documentata** dal prompt.
2. **Riuso del layer LLM esistente.** Il prompt proponeva un nuovo `ClaudeApiKey`/`ClaudeModel` in appsettings; invece
   `ClaudeSupervisorAgent` riusa `ILlmClient`/`AnthropicLlmClient` (key **solo** da env `ANTHROPIC_API_KEY`, modello dalla
   sezione `Llm`), rispettando il vincolo non negoziabile "la key non va nei file".
3. **`MultiSymbolOrchestrator` non esiste.** Le corsie sono keyed-DI (`ITradingEngine`/`IEnsembleManager` per laneId). Il
   metodo `PromoteLaneAsync` richiesto è implementato in un nuovo `ILanePromoter` che risolve la corsia keyed. **Deviazione documentata.**
4. **L'AI può solo vetare.** La sostituzione avviene solo se `comparator.ShouldReplace && judgment.ApproveReplacement`:
   un problema/veto dell'AI non forza mai un cambio, un'assenza di AI non blocca mai un cambio giustificato dai numeri.
5. **Ri-applica solo config `Scheduled`** e default OFF: l'automazione agisce sui cicli che l'utente ha già messo in
   automazione, non sui run manuali.

## File

**Nuovi (7 servizi + 3 test):** `EnsembleComparator.cs`, `PipelineApplier.cs`, `Agents/IPipelineSupervisorAgent.cs`,
`Agents/LoggingSupervisorAgent.cs`, `Agents/ClaudeSupervisorAgent.cs`, `Trading/PromotionEvaluator.cs`,
`Trading/LanePromoter.cs`, `Trading/PromotionWorker.cs` · Test: `EnsembleComparatorTests.cs`, `PromotionEvaluatorTests.cs`,
`SupervisorAgentTests.cs`.

**Modificati:** `Pipeline/PipelineSchedulerWorker.cs`, `Ensemble/EnsembleModels.cs`, `Program.cs`, `appsettings.json`,
`Components/Pages/Pipeline.razor`, `Components/Pages/Trading.razor`, `Components/Pages/Dashboard.razor`,
`ProcioneMGR.Tests/PipelineSchedulerWorkerTests.cs`.

## Verifiche di sicurezza (tutte superate)

- `PromotionEvaluatorTests.TestnetLane_WithGodTierMetrics_IsNeverPromotedToLive` — metriche estreme, mai Live.
- `PromotionEvaluatorTests.LiveLane_IsNeverTouched` — corsie Live intatte.
- `LanePromoter.PromoteLaneAsync` lancia se `newMode==Live`; UI "Promuovi a Live" disabilitata.
- `SupervisorAgentTests` — fallback su key sbagliata/errore/timeout → approva (decidono le metriche); veto onorato.
- `EnsembleComparatorTests` — hysteresis: miglioramento marginale (<10%) NON sostituisce.
- Dal vivo: `AutoReapply:Enabled=false` e `PipelineSupervisor:Provider=Logging` di default → nessun comportamento
  automatico inatteso; le 3 corsie ATOM/DOGE/SHIB restano in Paper.

## Prossimi passi consigliati

- Abilitare `AutoReapply:Enabled=true` dopo qualche ciclo schedulato osservato a mano.
- Impostare `PipelineSupervisor:Provider=Claude` + `ANTHROPIC_API_KEY` per il giudizio qualitativo.
- Lasciar maturare le corsie Paper fino a ≥30 trade / ≥3 settimane per innescare la prima valutazione di promozione.
