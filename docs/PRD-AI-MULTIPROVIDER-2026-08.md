# PRD — Layer AI multi-provider: chiavi, instradamento, usi (2026-08)

*Nasce dalla richiesta del proprietario (2026-08-01): la pagina /admin/ai-supervisor non è mai
stata operativa (causa storica: credito API Anthropic esaurito, mai risposto), e l'idea è
sfruttare le AI in modalità e piattaforme diverse. Questo PRD copre la FONDAZIONE (chiavi +
provider + verifica dal vivo, Fase A, eseguita) e lascia dichiaratamente aperto il resto: gli usi
veri li descriverà il proprietario, e verranno progettati QUI come fasi successive — non
anticipati a scatola chiusa.*

## §1 — Principî (validi per ogni fase futura)

1. **Il provider è un dato, non un'architettura.** `Llm:Provider` è hot-reload e l'instradamento
   avviene a ogni chiamata (`DelegatingLlmClient`): cambiare AI non richiede riavvii né tocca i
   consumatori (`PipelineSupervisor`, `LlmCallGuard`, worker, pannelli). Un provider nuovo = un
   `ILlmClient` nuovo + una voce in `AiProviders.Known`.
2. **Endpoint OpenAI-compatible come dialetto di default.** Il client NVIDIA parla il contratto
   `chat/completions` con base URL parametrico (`Llm:NvidiaBaseUrl`): OpenRouter, endpoint
   self-hosted (vLLM/TGI) o altri aggregatori entrano cambiando URL e chiave.
3. **Chiavi cifrate a riposo, mai rimostrate.** Tabella `AiCredentials` (AES-256-GCM, stesso
   converter delle credenziali exchange), una riga per provider, gestita da /admin/ai-supervisor;
   fallback alle env (`ANTHROPIC_API_KEY`, `NVIDIA_API_KEY`) per compatibilità. Una master key
   cambiata viene DICHIARATA (LogCritical + fallback env), mai confusa con "chiave assente".
4. **Ogni canale ha il suo pulsante che chiede se funziona.** «Prova collegamento» fa una chiamata
   VERA al provider attivo e mostra risposta o errore testuale (la lezione del token Telegram e
   del credito Anthropic: mai un canale che fallisce in silenzio).
5. **Il confine advisory resta non negoziabile.** Qualunque uso futuro delle AI: mai avviare
   trading, mai bypassare SafetyChecker, mai Live. Gli usi nuovi si aggiungono come CONSUMATORI
   di `ILlmClient`/`LlmCallGuard`, dentro lo stesso confine.

## §2 — Fase A (ESEGUITA 2026-08-01): fondazione chiavi + NVIDIA

| Pezzo | Cosa | Dove |
|---|---|---|
| Schema | `AiCredentials` (Provider unico, ApiKey cifrata, UpdatedAtUtc), migrazione additiva | `Data/AiCredential.cs`, `ApplicationDbContext` |
| Store | `IAiKeyStore`: DB → env, cache in memoria, sorgente dichiarata per la UI, ricarica esplicita | `Services/Llm/AiKeyStore.cs` |
| Client | `NvidiaLlmClient` (OpenAI-compatible, errori parlanti col body del provider, guardia sul contenuto vuoto da reasoning) | `Services/Llm/NvidiaLlmClient.cs` |
| Routing | `DelegatingLlmClient` registrato come `ILlmClient`; `LlmOptions.Provider/NvidiaModel/NvidiaBaseUrl` | idem + `Program.cs` |
| Anthropic | invariato nel comportamento, ma ora legge la chiave dallo store (DB → env) | `AnthropicLlmClient` |
| UI | pannello «Provider e chiavi» in /admin/ai-supervisor: provider attivo, modelli, chiavi (inserite dall'operatore, mai rimostrate), «Prova collegamento» | `AiSupervisor.razor` |
| Guard/breaker | INVARIATI: il breaker è del layer, non del provider — un 402 NVIDIA lo apre esattamente come un 402 Anthropic | — |

**Verifica** (4 livelli): unit su client/routing/store (fake HTTP, Postgres fixture); il contratto
OpenAI-compatible provato contro l'endpoint vero col «Prova collegamento»; integrazione = pannello
sull'app reale con la chiave dell'operatore; browser = badge di stato e risposta del modello visibili.

## §3 — Fasi successive (INTENZIONALMENTE VUOTE)

Il proprietario illustrerà gli usi che ha in mente («sfruttare le AI in diverse modalità e
piattaforme»). Ogni idea entrerà qui come fase con: consumatore di `ILlmClient` (mai un canale
parallelo), costo stimato per chiamata, e il suo posto rispetto al confine advisory. Candidati già
emersi in passato e NON ancora decisi: supervisore su più provider in parallelo con confronto dei
pareri; analisi del sentiment con LLM al posto dello scorer lessicale (`ISentimentScorer` è già
sostituibile 1:1); spiegazioni leggibili dei run per il pannello. Nessuno di questi è impegnato.

## §4 — Non-obiettivi

- Nessun SDK aggiuntivo per provider OpenAI-compatible (il contratto è tre campi JSON).
- Nessuna chiave in appsettings o nel repo; mai rimostrare una chiave salvata.
- Nessun uso delle AI oltre il confine advisory, in nessuna fase futura.
- Nessuna rimozione del percorso Anthropic: resta il default finché il proprietario non decide.
