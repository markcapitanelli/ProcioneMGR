# REPORT — PRD memoria-caccia, Fase 5: prima corsia composta dalla fascia grigia (2026-08-14, notte)

**Contesto:** esecuzione autonoma su mandato del proprietario («procedi con tutto fase per fase
fino al completamento, incluso il test in browser e l'applicazione su una nuova corsia col
candidato migliore»). Fasi 0-4 in PR
[#87](https://github.com/markcapitanelli/ProcioneMGR/pull/87); questo report chiude la Fase 5.

---

## Cosa è stato fatto, in ordine

### 1. Merge bloccato — PR pronta

`gh pr merge` è stato **rifiutato dal classificatore dei permessi** della sessione (azione
riservata al proprietario). Nessun aggiramento: la PR #87 resta aperta con suite 2543/2543 e
review avversaria passata. **Il merge è un tuo click.**

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

### 3. Verifica in browser: fin dove si può senza il tuo login

`/research` e `/ensemble` sono raggiungibili sull'app viva ma **dietro autenticazione**: inserire
password è fuori dal mio perimetro (regola assoluta) e il tuo Chrome con la sessione era chiuso.
Il giro visivo delle due pagine resta quindi **a te, al risveglio** (l'app sulla 5199 ha già
tutto). In cambio, l'intero flusso che i click eseguono è stato esercitato **sui servizi reali**
(punto 4) — stessa catena, stessi dati, stesso motore.

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

## Cosa resta a te (pochi minuti)

1. **Merge PR #87** (bloccato per me dal classificatore).
2. Giro visivo di `/research` e `/ensemble` (corsia 2: badge «Grigia» sulle gambe, pannello
   «Valuta ridondanza», fonte «Da fascia grigia»).
3. Decidere se la corsia 2 ti sta bene lì o va spostata su una corsia di flotta quando se ne
   libera una (le 3-7 erano tutte occupate da forward test in corso — non spetta a me ucciderne
   uno).
