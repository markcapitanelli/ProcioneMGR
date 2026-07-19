# Supervisione AI — `/admin/ai-supervisor`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Admin/AiSupervisor.razor`](../../ProcioneMGR/Components/Pages/Admin/AiSupervisor.razor) (~250 righe) |
| **Route** | `/admin/ai-supervisor` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Mostra i pareri dell'**advisory layer Claude** sul ciclo di ricerca: a ogni run di pipeline
concluso, l'agente legge la `PipelineRecommendation` e scrive un **parere leggibile** —
riepilogo, aggiustamenti proposti ai parametri di caccia, decisioni che richiedono conferma
esplicita dell'utente.

**Confine di sicurezza non negoziabile** (dal `GuidaPanel`): l'AI è *solo advisory*. Non può
avviare il trading live, non bypassa il `SafetyChecker`, non apre posizioni; i suggerimenti
sono spunti, mai comandi auto-applicati. Il layer è attivo solo con `Llm:Enabled=true` e la
env `ANTHROPIC_API_KEY`.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 21–35 | Cosa fa l'agente e il confine advisory-only |
| **Stato del layer AI** | 38–87 | Badge: chiave presente/assente, **operativo / SOSPESO** (breaker con causa e orario del prossimo probe), conteggi advisory ok / in errore / run in attesa (7gg); azioni **Aggiorna**, **Riprova adesso**, **Rianalizza advisory in errore** |
| Elenco advisory | 89–150 | Card per run: id run, badge confidenza (alta/media/bassa), badge errore, modello usato, riepilogo, tabella "Aggiustamenti proposti (da valutare, non applicati)", elenco "Decisioni che richiedono la tua conferma" |

## Come funziona (flusso del codice)

### Stato e hardening (righe 166–203)
`ILlmCallGuard.GetStatus()` espone lo stato del **circuit breaker** introdotto con
l'hardening 2026-07 (la causa storica del "mai funzionato" era il **credito API esaurito**:
28 advisory su 28 in errore senza segnalazione). Il guard classifica gli errori SDK
(credito, chiave invalida, rate limit…), apre il breaker con cooldown e fa **probe
periodici**: il ripristino alla ricarica del credito è **automatico**. La UI mostra causa,
prossimo probe e ultimo errore.

I conteggi vengono dagli artifact: advisory ok/errore sugli ultimi 50, più la **stessa
anti-join del worker** per i run completati recenti ancora senza advisory (righe 192–197)
— così la pagina e il worker non possono divergere sulla nozione di "in attesa".

### Azioni di recupero
- **Riprova adesso** (righe 205–220): `LlmWorker.TickAsync(forceProbe: true)` — ignora il
  cooldown del breaker; utile appena risolta la causa (es. credito ricaricato).
- **Rianalizza advisory in errore** (righe 222–239):
  `IPipelineSupervisor.DeleteErrorAdvisoriesAsync(7gg)` — elimina gli artifact-errore così
  il ciclo automatico rianalizza quei run (≤5 per tick). Niente artifact-spazzatura
  ripetuti: il retry è governato.

### Rendering advisory (righe 174–187)
Gli advisory sono `PipelineArtifacts` con `Kind = Advisory`; il payload JSON è
deserializzato in `SupervisorAdvisory` con fallback difensivo ("Advisory non leggibile")
per i payload corrotti.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `ILlmClient` / `AnthropicLlmClient` | La chiamata a Claude (Anthropic C# SDK); `IsConfigured` per il badge chiave | [`Services/Llm/AnthropicLlmClient.cs`](../../ProcioneMGR/Services/Llm/AnthropicLlmClient.cs) |
| `ILlmCallGuard` / `LlmCallGuard` | Breaker + classificazione errori + probe di ripristino | [`Services/Llm/LlmCallGuard.cs`](../../ProcioneMGR/Services/Llm/LlmCallGuard.cs) |
| `IPipelineSupervisor` / `PipelineSupervisor` | Produzione advisory dal run + pulizia errori | [`Services/Llm/PipelineSupervisor.cs`](../../ProcioneMGR/Services/Llm/PipelineSupervisor.cs) |
| `LlmSupervisorWorker` | Il ciclo periodico che analizza i run completati | [`Services/Llm/LlmSupervisorWorker.cs`](../../ProcioneMGR/Services/Llm/LlmSupervisorWorker.cs) |
| `SupervisorAdvisory` | Il payload: summary, suggerimenti parametro, decisioni per l'utente, confidenza | [`Services/Llm/SupervisorAdvisory.cs`](../../ProcioneMGR/Services/Llm/SupervisorAdvisory.cs) |
| `TelegramNotifier` (indiretto) | Notifiche one-shot su sospensione/ripristino del layer | [`Services/Notifications/TelegramNotifier.cs`](../../ProcioneMGR/Services/Notifications/TelegramNotifier.cs) |

## Dati letti / scritti

- **Legge**: `PipelineArtifacts` (Kind=Advisory), `PipelineRuns` (anti-join per i pendenti).
- **Scrive**: eliminazione advisory in errore (per la rianalisi); il tick forzato produce
  nuovi artifact.

## Collegamenti con le altre pagine

- [Pipeline](pipeline.md) — i run che generano gli advisory; distinto dal **supervisore
  veto-only** della ri-applica (quello è in Autonomia), anche se condividono l'`ILlmClient`.
- [Autonomia](admin-autonomy.md) — gli interruttori del layer e dell'agente veto.

## Note di design

- Advisory ≠ veto: questa pagina mostra il **consulente** (parere discorsivo post-run);
  il potere di veto sulla ri-applica è un agente separato con lo stesso confine di sicurezza.
- Il pattern breaker+probe+notifiche nasce da una lezione concreta: un layer esterno che
  fallisce in silenzio è peggio di uno spento — lo stato dev'essere visibile e il recupero
  automatico.
