# 00 — Indice dell'audit

**Progetto:** ProcioneMGR
**Data:** 4 agosto 2026
**Modalità:** read-only. Nessun file di progetto è stato modificato; gli unici file creati stanno in
`docs/audit/`.
**Base:** worktree su `master` @ `709f1f7`; applicazione ispezionata dal vivo su
`http://localhost:5199`.

---

## Documenti

| # | File | Contenuto |
|---|---|---|
| 00 | **00_INDEX.md** | questo documento |
| 01 | [01_PROJECT_OVERVIEW.md](01_PROJECT_OVERVIEW.md) | cos'è, tecnologie, entry point, comandi, struttura |
| 02 | [02_ARCHITECTURE.md](02_ARCHITECTURE.md) | architettura, pattern, database, auth, middleware |
| 03 | [03_CODE_MAP.md](03_CODE_MAP.md) | mappa del codice cartella per cartella, relazioni fra moduli |
| 04 | [04_RUNTIME_AND_DATA_FLOW.md](04_RUNTIME_AND_DATA_FLOW.md) | cosa succede all'avvio, flussi di dati, worker, side effect |
| 05 | [05_UI_PAGES_AND_ROUTES.md](05_UI_PAGES_AND_ROUTES.md) | 66 route con protezioni, componenti, osservazioni UX |
| 06 | [06_API_AND_INTEGRATIONS.md](06_API_AND_INTEGRATIONS.md) | endpoint, gRPC, servizi esterni, variabili d'ambiente |
| 07 | [07_BROWSER_CHECK_REPORT.md](07_BROWSER_CHECK_REPORT.md) | controllo nel browser sull'app reale |
| 08 | [08_TEST_PLAN.md](08_TEST_PLAN.md) | suite esistente, smoke test, test consigliati |
| 09 | [09_RISKS_AND_TECH_DEBT.md](09_RISKS_AND_TECH_DEBT.md) | **14 rischi con priorità** |
| 10 | [10_CLAUDE_CODE_MEMORY.md](10_CLAUDE_CODE_MEMORY.md) | memoria operativa per Claude Code |
| 11 | [11_NOTEBOOKLM_EXPORT.md](11_NOTEBOOKLM_EXPORT.md) | **documento unico autosufficiente** per NotebookLM |
| 12 | [12_UI_WALKTHROUGH.md](12_UI_WALKTHROUGH.md) | **giro completo dell'app dal vivo**, con il cluster acceso |
| 13 | [13_DEEP_DIVE_CODE.md](13_DEEP_DIVE_CODE.md) | **approfondimento del codice**: safety, promozione, il difetto della quantità |
| — | [playwright-smoke.mjs](playwright-smoke.mjs) | script di smoke test eseguibile |
| — | [`/CLAUDE.md`](../../CLAUDE.md) | istruzioni per Claude Code: **il notebook come memoria primaria** |

## Filone K — il governo autonomo (2026-08-31 → 09-03)

| # | File | Contenuto |
|---|---|---|
| 36 | [36_RITIRO_CORSIA_NUMERI_2026-09-01.md](36_RITIRO_CORSIA_NUMERI_2026-09-01.md) | i numeri per decidere quando ritirare una corsia |
| 37 | [37_DECISIONI_RITIRO_2026-09-01.md](37_DECISIONI_RITIRO_2026-09-01.md) | le decisioni sul ritiro, e cinque difetti sotto la taratura |
| 38 | [38_FASE3_MISURE_2026-09-01.md](38_FASE3_MISURE_2026-09-01.md) | la Fase 3 misurata prima di essere fatta: quattro item cambiano |
| 39 | [39_FILONE_K_STATO_2026-09-02.md](39_FILONE_K_STATO_2026-09-02.md) | lo stato del filone: K48-K53, la rettifica di K50, il comitato senza quorum |
| 40 | [40_CACCIA_19_E_STABILITA_2026-09-03.md](40_CACCIA_19_E_STABILITA_2026-09-03.md) | K56 cadenza per configurazione, K57 gate di stabilità |
| 41 | [41_GOVERNO_DELLA_CACCIA_2026-09-03.md](41_GOVERNO_DELLA_CACCIA_2026-09-03.md) | K58 copertura, K59 tetto in ore, K60 proponitore |
| 42 | [42_REVISIONE_FILONE_K_2026-09-03.md](42_REVISIONE_FILONE_K_2026-09-03.md) | **revisione completa del filone**: 14 segnalazioni alte, tre difetti dal vivo, correzioni applicate |

## Secondo passaggio — 2026-08-04, sera

L'audit è stato ripreso e approfondito. Cosa è cambiato:

- **Cluster kind ripristinato** (il proxy socat puntava a un IP obsoleto): l'app è stata vista nel
  suo **stato nominale**, non più degradato.
- **Giro completo dell'interfaccia** — [12](12_UI_WALKTHROUGH.md): 20 pagine ispezionate a schermo
  con dati reali.
- **Lettura in profondità** di `SafetyChecker`, `SafetyConfiguration`, `PromotionEvaluator`,
  `SignalOrderBuilder`, `SymbolFilters` — [13](13_DEEP_DIVE_CODE.md).
- **Tre rischi nuovi**, di cui due ALTA: [R15](09_RISKS_AND_TECH_DEBT.md#r15) quantità dell'ordine
  non arrotondata, [R16](09_RISKS_AND_TECH_DEBT.md#r16) nessun backup del database,
  [R17](09_RISKS_AND_TECH_DEBT.md#r17) 38% di errori nel supervisore AI.
- **Due mie conclusioni corrette**: R5 (le fonti rotte *sono* segnalate in UI) e il dubbio su
  `/admin/protections` nel piano di test (legge davvero dal motore remoto).

## Guida alla lettura

**Hai cinque minuti.** Leggi la sezione 🔴 di [09](09_RISKS_AND_TECH_DEBT.md). Il primo rischio
richiede un'azione oggi.

**Devi lavorare sul codice.** [10_CLAUDE_CODE_MEMORY.md](10_CLAUDE_CODE_MEMORY.md) — comandi,
convenzioni, cose da non rompere, errori comuni.

**Devi caricare tutto su NotebookLM.** [11_NOTEBOOKLM_EXPORT.md](11_NOTEBOOKLM_EXPORT.md) è
autosufficiente: overview, architettura, rotte, API, glossario, FAQ, decisioni architetturali.

**Vuoi capire il progetto da zero.** 01 → 02 → 03 → 04, in quest'ordine.

**Vuoi sapere se funziona davvero.** [07](07_BROWSER_CHECK_REPORT.md), che riporta anche cosa non
ha funzionato durante l'audit e perché.

---

## Stato dell'analisi

### Analizzato bene

| Area | Come |
|---|---|
| **Struttura e composizione** | `Program.cs` letto per intero (674 righe); conteggio file per progetto e cartella |
| **Route e protezioni** | 66 `@page` estratte dal codice **e** 37 route sondate via HTTP sull'app viva: 28/28 redirect corretti |
| **Modello dati** | 30 `DbSet` enumerati dal `DbContext` |
| **Sicurezza** | barriere anti-Live tracciate file per file; modello di cifratura verificato; **un problema critico trovato e confermato con quattro comandi git indipendenti** |
| **Integrazioni esterne** | tutti gli URL estratti dal codice; stato reale delle fonti verificato nel log di runtime |
| **Comportamento a runtime** | app avviata davvero: 1667 righe di log analizzate, worker e cadenze osservati |
| **UI** | homepage, `/trading`, `/dashboard`, `/metrics` ispezionate a schermo; console e rete controllate; responsive a 375 px |

### Da approfondire

| Argomento | Perché non concluso |
|---|---|
| **Numero reale dei test** | il README dice 988, il conteggio statico ~1999; serve `dotnet test --list-tests` con Docker |
| **`/trading` in stato nominale** | durante l'audit il core caldo era irraggiungibile: ho verificato solo il percorso degradato |
| **Area autenticata oltre 4 pagine** | ispezionate `/`, `/trading`, `/dashboard`, `/metrics`; le altre 30 solo via codice e sonda HTTP |
| **Stati di loading** | in locale le pagine rispondono troppo in fretta; servirebbe throttling |
| **Dipendenze vulnerabili** | `dotnet list package --vulnerable` non eseguito |
| **Query dietro il warning EF 10103** | il log non la nomina |
| **Dipendenze circolari fra namespace** | a livello di progetto non ce ne sono; dentro `Services/` servirebbe uno strumento dedicato |
| **Cluster kind** | API server irraggiungibile dopo il black-out; diagnosi fuori perimetro |

### Convenzioni usate

- Ogni affermazione non ovvia è ancorata a un percorso di file, spesso con numero di riga.
- Ciò che non ho potuto verificare è marcato **DA VERIFICARE**, mai dedotto.
- **Nessun segreto reale compare in questi documenti**: ovunque `[REDACTED]`.
- Dove il codice e la realtà divergono (per esempio il numero di corsie), è segnalato.

---

## Il risultato in una riga

Codebase solida, insolitamente disciplinata sulla sicurezza e onesta nel dichiarare i propri limiti
— con **un segreto critico pubblicato per errore su un repository pubblico** che va ruotato subito.
