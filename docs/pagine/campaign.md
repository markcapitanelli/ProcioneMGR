# Campagne — `/campaign`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Campaign.razor`](../../ProcioneMGR/Components/Pages/Campaign.razor) (~230 righe) |
| **Route** | `/campaign` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Il **Campaign Planner** decide **cosa fare DOPO un run di pipeline**, automaticamente. Una
campagna è un elenco **ordinato** di configurazioni di caccia che il planner ruota da solo:

- **0 sopravvissuti** → passa alla prossima config della rotazione (con **backoff**: la
  stessa config non si ripete prima di N ore);
- **sopravvissuti** → l'ensemble candidato passa dalla **stessa catena** della ri-applica
  automatica (supervisore AI con veto + isteresi); se viene schierato, la rotazione si ferma
  e la campagna va in **osservazione**;
- **rotazione esaurita** → la campagna aspetta un **trigger contestuale** (cambio
  regime/volatilità) o l'intervento dell'operatore.

Guardrail (dal `GuidaPanel`, righe 23–28):
- **Doppio gate**: agisce solo se il gate globale `Campaign:Enabled` E la singola campagna
  sono entrambi abilitati.
- **Limiti duri**: config in Live sempre saltate; corsie avviate al massimo in **Paper** e
  solo se ferme; una corsia in quarantena rifiuta da sola.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| Banner gate globale | 31–37 | Se `Campaign:Enabled=false`: le campagne si preparano ma nessun run parte |
| Elenco campagne | 44–122 | Per campagna: stato (in rotazione / in osservazione / in attesa di trigger), badge "run in corso"/"spenta", backoff, auto-start Paper, ultima azione/esito, **tabella della rotazione** (config, ultimo esito, ultimo run, tentativi), azioni Abilita/Disabilita, "Rimetti in rotazione", elimina; bottone globale **▶ Tick adesso** |
| Nuova campagna | 125–169 | Nome, backoff ore, checkbox auto-start Paper, selezione **ordinata** delle config (i bottoni mostrano il numero d'ordine; badge rosso "Live" = verrà saltata); "Crea campagna (**nasce spenta**)" |

## Come funziona (flusso del codice)

La pagina è un guscio sottile su **`CampaignPageService`** (`Svc`): stato
(`Campaigns`, `Configs`, `GloballyEnabled`, `Message`) e azioni
(`CreateAsync`, `SetEnabledAsync`, `WakeAsync`, `DeleteAsync`, `TickNowAsync`).

- **Selezione ordinata** (righe 185–188): il click su una config la aggiunge in coda alla
  rotazione o la rimuove; l'indice mostrato sul bottone è l'ordine di rotazione.
- **Tick adesso** forza un giro del planner senza aspettare il worker.
- Gli esiti per config sono decodificati in `OutcomeLabel` (righe 222–230):
  `NoSurvivors` / `Applied` (✅ schierato) / `NotApplied` (sopravvissuti ma non schierato,
  es. veto del supervisore o isteresi) / `Failed`.

Il lavoro vero è del **`CampaignPlannerWorker`** in background: a ogni tick valuta le
campagne abilitate, decide la prossima config secondo le regole sopra e avvia il run di
pipeline con trigger 🎯 "Campaign".

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `CampaignPageService` | Stato e azioni della pagina | [`Services/Pipeline/CampaignPageService.cs`](../../ProcioneMGR/Services/Pipeline/CampaignPageService.cs) |
| `CampaignPlanner` | La logica di rotazione/backoff/esiti | [`Services/Pipeline/CampaignPlanner.cs`](../../ProcioneMGR/Services/Pipeline/CampaignPlanner.cs) |
| `CampaignPlannerWorker` | Il tick periodico in background | [`Services/Pipeline/CampaignPlannerWorker.cs`](../../ProcioneMGR/Services/Pipeline/CampaignPlannerWorker.cs) |
| `CampaignOptions` | Il gate globale `Campaign:Enabled` e i default | [`Services/Pipeline/CampaignOptions.cs`](../../ProcioneMGR/Services/Pipeline/CampaignOptions.cs) |
| `CampaignEntities` | Entità campagna + stati per config (`CampaignStatus`) | [`Services/Pipeline/CampaignEntities.cs`](../../ProcioneMGR/Services/Pipeline/CampaignEntities.cs) |

## Dati letti / scritti

- **Legge**: campagne e loro stati, `PipelineConfigurations` (per nome e modalità).
- **Scrive**: CRUD campagne, abilitazione, wake (rimetti in rotazione), tick manuale.

## Collegamenti con le altre pagine

- [Pipeline](pipeline.md) — le config della rotazione nascono lì; i run lanciati dal planner
  compaiono nello storico con trigger 🎯.
- [Autonomia](admin-autonomy.md) — la catena di ri-applica (comparator + veto AI) che decide
  se lo schieramento avviene.
- [Trading](trading.md) — le corsie eventualmente avviate in Paper.

## Note di design

- "Nasce spenta" è deliberato: creare una campagna non produce mai effetti immediati; serve
  l'abilitazione esplicita (e il gate globale).
- La sicurezza è a strati indipendenti: gate globale → gate campagna → skip config Live →
  solo Paper su corsie ferme → quarantena della corsia come ultima difesa.
