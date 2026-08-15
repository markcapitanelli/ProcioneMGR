# REPORT — PRD memoria-caccia, Fase 5: prima corsia composta dalla fascia grigia (2026-08-14, notte)

**Contesto:** esecuzione autonoma su mandato del proprietario («procedi con tutto fase per fase
fino al completamento, incluso il test in browser e l'applicazione su una nuova corsia col
candidato migliore»). Fasi 0-4 in PR
[#87](https://github.com/markcapitanelli/ProcioneMGR/pull/87); questo report chiude la Fase 5.

---

## Cosa è stato fatto, in ordine

### 1. Merge — FATTO dal proprietario

`gh pr merge` era stato **rifiutato dal classificatore dei permessi** della sessione (azione
riservata al proprietario); il proprietario ha mergiato la PR #87 il 2026-08-15
(`18efd19` su master), con suite 2543/2543 e review avversaria passata.

### 2. Il guscio sulla 5199 gira col codice del branch

Alle ~19:11 un guscio (master, senza le pagine nuove) era stato avviato sulla 5199. Per la
verifica dal vivo è stato fermato e sostituito con **l'unica** istanza col codice del branch
(worktree `nice-merkle-9c3c0d`, stessa riga di comando, stessa config reale,
`ASPNETCORE_URLS=http://localhost:5199` come da `run-postgres.ps1`): il motore in-cluster non è
mai stato toccato, la regola del singolo scrittore è intatta (un guscio solo; un secondo
avvio sulla stessa porta fallirebbe a voce alta sul bind).

- La migrazione **`AddResearchCandidates` è stata applicata al DB reale** dal migrate-on-startup.
- All'avvio l'app ha **ripreso dal checkpoint la pipeline `c465af9d`** interrotta dal riavvio, e
  il run ha attraversato il codice nuovo dal vivo:
  `[EnsembleAssembly] 0 sopravvissuti; 21 candidati in fascia grigia ESCLUSI (includeGreyZone=false…)`
  — il default-off dichiarato, su un run vero.

### 3. Verifica in browser — ESEGUITA (2026-08-15, dopo il login del proprietario)

Livello 4 completato sull'app viva, dati reali:

- **`/research`**: KPI **11.300 candidati · 134 run · 37 promossi · 1.024 in fascia grigia ·
  10.239 scartati nel merito**, periodo 02/07→15/08. Filtri popolati dai dati veri (36 coppie,
  6 timeframe, 15 famiglie), resa per famiglia, motivi di scarto classificati, tabella coi badge
  e le azioni Rianalizza/Componi. Tetto dichiarato: «200 visualizzati su 11.300 filtrati».
- **`/ensemble` corsia 2**: entrambe le gambe col badge **Grigia**, SL 4,21 / TP 11,64 e le attese
  holdout compilate; pannello **«Da fascia grigia (ADA/USDT 4h) — 2 candidati»** con la
  dichiarazione del secondo giro di selezione; **«Valuta ridondanza gambe» cliccato dal vivo** →
  `(ultimi 90 giorni, 89 giorni comuni · soglia |ρ| 0,70) Supertrend ↔ Composite · ρ=0,59`,
  sotto soglia, badge neutro.
- Voce **«Archivio candidati»** presente in NavMenu sotto Ricerca & Sviluppo.

#### Due fatti misurati che vale la pena sapere

1. **La banda DSR della fascia grigia è, oggi, vuota.** Tutti e **1.024** i grigi sono tali per
   *sola finestra corta*; **zero** entrano dalla banda DSR [0,80–0,95). Nello stesso archivio ci
   sono **343 candidati bocciati dal DSR con Sharpe holdout medio +1,05** che NON sono grigi,
   perché il loro DSR sta *sotto* 0,80 — effetto atteso del conteggio corretto dei tentativi
   (fix D-01). Risponde di fatto alla domanda aperta §9.2 del PRD: la soglia grigia non va
   irrigidita, semmai è il ramo DSR a non mordere mai. Se un giorno volessi pescare anche lì,
   la leva è il *pavimento* `GreyZone.DsrFloor`, non il tetto.
2. **Il rolling Sharpe non conferma l'holdout, come da copione.** Nella ri-simulazione a 90
   giorni: **Supertrend −0,24** (holdout 3,19), **Composite +2,03** (holdout 1,06). Finestre
   diverse, quindi non è una smentita — ma è esattamente il motivo per cui la corsia sta in
   Paper e non altrove.

### 4. La corsia — eseguito via i servizi reali (harness fuori repo, nello scratchpad)

| Passo | Esito |
|---|---|
| Indicizzazione (stessa di /research) | **132 run, 11.242 candidati** a indice, di cui **1.023 in fascia grigia** — 0 payload illeggibili |
| Coppia del candidato migliore | **ADA/USDT 4h** (miglior Sharpe holdout fra tutti i grigi: 3,19), 2 grigi componibili |
| Corsia | Le corsie di flotta 3-7 sono TUTTE occupate e RUNNING (rifiutate); scelta la **corsia 2**, libera e ferma — è impronta auto-apply: un futuro «Applica al Trading» con 3 gruppi di coppie potrebbe sovrascriverla (oggi improbabile: l'auto-apply scrive solo con gambe proposte, e a `includeGreyZone=false` non ne produce da un mese) |
| Gambe aggiunte (flusso T3) | **Supertrend** (Sharpe holdout **3,19** su 17 trade — «Solo 17 trade in holdout (< 20)», run `fcd40a62` fresco di stanotte) + **Composite** (Sharpe holdout **1,06** su 6 trade — «Solo 6 trade in holdout (< 25)», run `500fd9c7` di luglio: l'archivio che ripaga) |
| Bracket | automatico dalle escursioni, per entrambe: **SL 4,21% / TP 11,64%** |
| Ridondanza (T2) | **ρ = 0,59** fra le due gambe su 89 giorni comuni — sotto la soglia 0,7, dichiarato: diversificano davvero |
| Avvio | Save + Enable + `StartAsync(Paper)` via gRPC → **motore in-cluster: `running=True, mode=Paper, ADA/USDT 4h, capitale 10.000`** |

**Orizzonte dichiarato** (regola di piattaforma): dai conteggi holdout, ~4 trade/mese
(Supertrend) + ~1,5 (Composite) ≈ **5-6 trade/mese** attesi sulla corsia, swing su 4h. La durata
mediana delle posizioni la misurerà il forward test — che è il giudice, come sempre.

---

## Stato in cui trovi la piattaforma

- **Corsia 2**: ADA/USDT 4h, 2 gambe grigie etichettate (`SourceVerdict=Grey`), IN PAPER, motore
  in-cluster che la esegue. Fermarla = un click su /trading, come ogni corsia.
- **Guscio 5199**: istanza col codice del branch (worktree). Al merge di PR #87, riavvialo pure
  dal repo principale col tuo flusso abituale: la config è a DB, non perde nulla. Le config reali
  (`appsettings.json`) sono copiate nel worktree, ignorate da git.
- **PR #87**: da mergiare (suite completa verde, review passata).
- **Notifiche**: il watchdog può aver mandato 1-2 transizioni Telegram durante gli scambi di
  guscio (19:11→sostituzione, poi il riavvio per la porta).
- Notebook NotebookLM non aggiornato: l'autenticazione era scaduta e il login è interattivo —
  al prossimo `nlm login` va ricaricato `docs/ROADMAP.md` + questo report.

## Cosa resta a te

1. ~~Merge PR #87~~ **fatto** (`18efd19`). ~~Giro visivo~~ **fatto** (§3).
2. **Riavviare il guscio dal repo principale** quando ti fa comodo: ora la 5199 gira dal
   worktree, e master ha tutto. La configurazione vive a DB, non si perde nulla.
3. **Decidere la casa della corsia 2**: sta nell'impronta auto-apply (0-2) perché le corsie di
   flotta 3-7 erano tutte occupate da forward test in corso. Se se ne libera una, spostarla è
   una riconfigurazione di due minuti.
4. **NotebookLM**: `nlm login` (interattivo) e poi ricaricare `docs/ROADMAP.md` + questo report.

## Nota di metodo, per il prossimo giro

Il livello 4 ha confermato tutto il costruito e in più ha prodotto **due misure che nessun test
poteva dare** (§3): la banda DSR della fascia grigia è vuota nell'archivio reale, e il rolling
Sharpe delle due gambe non conferma l'holdout. Nessuna delle due è un difetto del codice: sono
esattamente il genere di cosa per cui la pagina `/research` è stata costruita — leggere la
caccia invece di ripeterla.
