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

## §3 — Fasi successive

*Riempito il 2026-08-01, quando il proprietario ha portato il PDF di ricerca esterna "Architetture
AI per Trading in C#" e chiesto la roadmap d'integrazione. Il confronto punto-per-punto col PDF sta
in `docs/ROADMAP.md` (sezione «Layer AI multi-provider»); qui le fasi decise. Il terzo candidato
storico ("spiegazioni leggibili dei run") resta aperto e non impegnato.*

### Fase B — Sentiment via LLM (ESEGUITA 2026-08-01)

Lo scorer lessicale a 25 parole era esplicitamente segnalato debole ("DEMOTE" nell'audit
algoritmico di luglio); `ISentimentScorer` era già progettata per la sostituzione 1:1.

| Pezzo | Cosa | Dove |
|---|---|---|
| Contratto | `ISentimentScorer.ScoreAsync(title, summary, ct)` — asincrono (un'implementazione può fare rete); chi implementa NON lascia mai propagare un fallimento del canale | `Services/Sentiment/ISentimentScorer.cs` |
| Scorer LLM | `LlmSentimentScorer`: provider ATTIVO via `ILlmClient`+`LlmCallGuard` (path metrica **"sentiment"**, breaker del layer condiviso per scelta), parsing difensivo con clamp [-1,1], fallback interno al lessico su OGNI esito non-Ok; `ScoreBatchAsync` (20 titoli per chiamata) per il replay storico | `Services/Sentiment/LlmSentimentScorer.cs` |
| Routing | `DelegatingSentimentScorer` su `Sentiment:ScorerProvider` hot-reload ("Keyword" default = comportamento storico; sceglierne un altro è il consenso esplicito al costo) | `Services/Sentiment/DelegatingSentimentScorer.cs` |
| Call-site | UNICO in tutto il repo: `AltDataSyncService.SyncAllAsync` (~orario, ~decine di titoli). Fallimento di scoring = elemento SALTATO e ritentato al giro dopo, mai uno zero inventato (la dedupe non rivisita mai un elemento salvato) | `Services/AltData/AltDataSyncService.cs` |
| Confronto | `SentimentScorerComparisonService`: rigioca le notizie storiche con ciascuno scorer e le giudica con lo STESSO `FactorEvaluator` (IC Spearman, t-stat Newey-West, IR, quantili) sulle stesse candele — nessun gate nuovo; pannello «3. Scorer del sentiment» in /sentiment con tetto costi e lista dei disaccordi | `Services/Sentiment/SentimentScorerComparisonService.cs` |

**Costo dichiarato** (§1.4): percorso vivo = decine di piccole chiamate l'ora al massimo (solo
notizie nuove); replay storico = N/20 chiamate col tetto scelto dal pannello. Il free tier NVIDIA
(16 richieste concorrenti) non è mai un vincolo: le chiamate sono sequenziali.

**Confine** (§1.5): consumatore puro di `ILlmClient`/`LlmCallGuard`; il punteggio influenza il
fattore sentiment esattamente come il lessico di prima — il gate per l'uso live resta l'IC OOS
oltre i costi, come per ogni fattore.

### Fase C — Secondo parere multi-provider (ESEGUITA 2026-08-01)

| Pezzo | Cosa | Dove |
|---|---|---|
| Config | `Llm:ComparisonEnabled` (default off — raddoppia il costo per run) + `Llm:ComparisonProvider` | `LlmOptions` |
| Resolver | `ILlmClientResolver`: il secondo parere parla con un provider SPECIFICO, non con l'attivo del delegante | `Services/Llm/LlmClientResolver.cs` |
| Flusso | DOPO l'advisory primaria riuscita (mai al posto; mai su advisory in errore), stessa `PipelineRecommendation`, stesso prompt. **Best-effort dichiarato**: NON passa dal breaker condiviso (un guasto del provider di confronto non deve sospendere advisory/veto), timeout proprio, fallimento = log + run con un parere solo, senza retry | `PipelineSupervisor.TryWriteComparisonAdvisoryAsync` |
| Persistenza | artifact con **Kind distinto** `LlmAdvisoryCompare` (worker/pannello/test filtrano sul Kind primario e non devono vederlo), provider nello StageName; NESSUNA migrazione | — |
| UI | toggle+provider nel pannello «Provider e chiavi»; il secondo parere compare DENTRO la card del run, affiancato | `/admin/ai-supervisor` |

Se il provider di confronto coincide con l'attivo il confronto si salta da solo. Skip anche senza
chiave o con provider ignoto — sempre a voce nel log, mai in silenzio.

### Fase D — Tre provider in un colpo: Gemini, Groq, HuggingFace (ESEGUITA 2026-08-02)

Il proprietario si è procurato le chiavi API di Google Gemini, Groq e HuggingFace. Tutti e tre
parlano il dialetto OpenAI-compatible → il principio §1.2 passa dalla promessa alla prova:

| Pezzo | Cosa | Dove |
|---|---|---|
| Base comune | `NvidiaLlmClient` elevato a `OpenAiCompatibleLlmClient` (astratta): l'intera logica HTTP/parse/errori è UNA; ogni provider è una sottoclasse da cinque righe (nome + coppia BaseUrl/Model dalle opzioni) | `Services/Llm/NvidiaLlmClient.cs` |
| Provider | `GeminiLlmClient` (layer compat `generativelanguage.googleapis.com/v1beta/openai`, default `gemini-2.5-flash`), `GroqLlmClient` (`api.groq.com/openai/v1`, default `llama-3.3-70b-versatile`), `HuggingFaceLlmClient` (router `router.huggingface.co/v1`, default `meta-llama/Llama-3.3-70B-Instruct`) | idem |
| Config | `Llm:{Gemini,Groq,HuggingFace}{Model,BaseUrl}` — campi piatti hot-reload, stesso stile di NvidiaModel | `LlmOptions` |
| Chiavi | zero modifiche: `AiCredentials` è keyed per provider, il pannello mostra le righe nuove da solo; env fallback `GEMINI_API_KEY`/`GROQ_API_KEY`/`HUGGINGFACE_API_KEY` | — |
| Errori | il classificatore del guard legge il contratto generico `<PROVIDER> HTTP <code>:` — la tassonomia è del codice HTTP, non del nome del provider | `LlmCallGuard` |
| Routing | `LlmClientResolver` a 5; `DelegatingLlmClient` instrada via resolver (fallback storico a due senza) | `LlmClientResolver.cs` |
| UI | il pannello genera un campo modello PER provider dal ciclo su `AiProviders.Known`; la tabella chiavi e il select del secondo parere erano già cicli | `/admin/ai-supervisor` |
| **Elenco modelli per chiave** | `IModelCatalogProvider.ListModelsAsync` — `GET {base}/models` per i compat (stesso Bearer, stesso contratto d'errore), `GET /v1/models` col dialetto proprio (x-api-key) per Anthropic; nel pannello un pulsante per provider scarica l'elenco DELLA PROPRIA chiave e lo aggancia al campo come suggerimenti (datalist), con avviso se il modello attuale non è in elenco | idem |

**Il caso che ha dettato la funzione** (2026-08-02, dal vivo): Google ha ritirato
`gemini-2.5-flash` per le chiavi nuove e perfino l'alias `gemini-flash-latest` puntava al modello
morto — errore parlante `GEMINI HTTP 404` col rimedio nel testo. L'elenco della chiave reale
(59 modelli) ha dato l'id giusto: `models/gemini-3.6-flash` (prefisso `models/` incluso, com'è
nell'elenco). **I nomi dei modelli invecchiano; l'elenco della propria chiave no.**

**Collaudo dal vivo (2026-08-02)**: Gemini · `models/gemini-3.6-flash` ✔, Groq ·
`llama-3.3-70b-versatile` ✔ (latenza da primato), HuggingFace · `meta-llama/Llama-3.3-70B-Instruct`
✔ — tre «Prova collegamento» confermati sull'app reale con le chiavi del proprietario. Con cinque
provider il **secondo parere (Fase C) ha finalmente coppie utilizzabili** anche a credito Anthropic
esaurito (es. attivo Nvidia + confronto Groq). Il confine advisory e il breaker unico del layer
restano invariati.

### Fase D.1 — Failover automatico, Anthropic retrocessa, pannello lineare (2026-08-02)

Tre richieste del proprietario a valle del collaudo:

1. **Anthropic fuori dalle AI principali** (credito esaurito; «finché ho le altre sfrutterò
   quelle»): default `Llm:Provider=Nvidia`, `ComparisonProvider=Groq`, Anthropic in coda a
   `AiProviders.Known` e FUORI dalla catena di failover di default. Resta un provider completo:
   se il credito torna, si riseleziona dal pannello.
2. **Failover automatico** («se si verifica un errore di qualsiasi genere, la piattaforma testa
   in automatico un'altra AI»): `DelegatingLlmClient` prova la catena
   `Llm:FailoverProviders` (default Nvidia→Groq→Gemini→HuggingFace, `FailoverEnabled=true`),
   saltando chi non ha chiave; le cancellazioni NON innescano failover (uno shutdown non è un
   guasto del provider); ogni salto è nel log e `ILlmCompletionInfo.LastCompletionModel` dice chi
   ha DAVVERO risposto (l'advisory registra la verità, non l'intenzione). Il breaker del guard
   scatta solo se falliscono TUTTI — coerente: è il breaker del layer, e il layer ora è la
   federazione. Il «Prova collegamento» risolve il provider DIRETTAMENTE (senza failover): un
   test che "passa" grazie a un'altra AI sarebbe un controllo che rassicura.
3. **Modelli automatici + pannello lineare**: all'apertura il pannello scarica DA SOLO gli
   elenchi per ogni AI con chiave e, dove il modello configurato manca o non è più valido,
   `ModelAutoSelector` (puro, testato) ne sceglie uno — filtro anti non-chat
   (tts/image/embedding/…), preferenze per provider, a parità vince la versione più recente.
   UI ridisegnata: UNA riga principale (AI attiva · modello con suggerimenti · Prova · Salva)
   con la catena di failover dichiarata sotto; i campi per-provider stanno in una sezione
   «avanzate» richiudibile.

### Candidato aperto (non impegnato)

- **Spiegazioni leggibili dei run per il pannello** — nessuna nuova informazione lo giustifica
  rispetto a B/C; si riapre quando il proprietario lo chiede.

## §4 — Non-obiettivi

- Nessun SDK aggiuntivo per provider OpenAI-compatible (il contratto è tre campi JSON).
- Nessuna chiave in appsettings o nel repo; mai rimostrare una chiave salvata.
- Nessun uso delle AI oltre il confine advisory, in nessuna fase futura.
- Nessuna rimozione del percorso Anthropic: resta il default finché il proprietario non decide.
- Il pilota di inferenza LOCALE (ONNX) NON sta in questo PRD: non è un consumatore di
  `ILlmClient` (§1.5) — ha il suo documento, [PRD-ONNX-SENTIMENT-PILOT-2026-08](PRD-ONNX-SENTIMENT-PILOT-2026-08.md).
- Retry del secondo parere (Fase C): un parere accessorio perso non vale il macchinario di
  ripresa; il run resta con un parere solo, dichiarato nel log.
