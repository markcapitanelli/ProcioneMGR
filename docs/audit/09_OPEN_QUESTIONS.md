# 09 — DOMANDE APERTE

Ciò che **non** ho potuto determinare con certezza dal codice, e le decisioni che spettano a un
essere umano. Ordinate per urgenza.

Ogni voce dichiara: **cosa non so · perché non posso saperlo dal codice · cosa cambia a seconda
della risposta**.

---

## 🔴 Bloccanti per la sicurezza

### Q2 — La rotazione dei segreti è stata fatta?

**Cosa non so.** Se master key, segreto gRPC e password Postgres esposti in
`ProcioneMGR/appsettings.json.pre-audit-test-20260729-141448` (tracciato da git dal 2026-07-29) siano
stati **ruotati**.

**Perché non posso saperlo.** La rotazione è un'operazione esterna al repository. Il file è ancora
tracciato oggi, il che suggerisce che nulla sia stato fatto, ma non lo prova: si può ruotare senza
rimuovere il file.

**Cosa cambia.**
- **Se non ruotati:** le credenziali exchange cifrate sono di fatto in chiaro per chiunque abbia
  accesso alla storia. La Fase 0 del blueprint è **urgente**, non "prima priorità".
- **Se ruotati:** resta comunque da rimuovere il file e sistemare `.gitignore`, ma il rischio è
  storico e non attuale.

> Segnalazione già in memoria dal 2026-07-29 come «SEGNALATO NON RISOLTO». Questo audit la conferma
> aperta al 2026-08-08. **È la domanda a cui rispondere per prima.**

### Q3 — Qual è il valore *effettivo* di `MarketData:Realtime:DriveProtectiveExits`?

**Cosa non so.** Il valore nel `appsettings.json` reale.

**Perché non posso saperlo.** Il file è gitignored (correttamente) e **assente da questo worktree**.
Tutto ciò che ho è il default di classe (`RealtimeMarketDataModels.cs:117` → `true`) e
`appsettings.json.example` (→ `true`), entrambi in contraddizione con la regola 7 di `CLAUDE.md` e
con `docs/REPORT-B3-EXITLAG-2026-07-28.md`.

**Cosa cambia.**
- **Se nel file reale è `false`:** il rischio operativo attuale è nullo, ma il default del codice
  resta sbagliato per chiunque parta da zero, usi l'example o faccia un deploy nuovo. Va corretto lo
  stesso, con priorità media.
- **Se è `true` (o assente, ⇒ eredita `true`):** basta accendere `MarketData:Realtime:Enabled` per
  attivare in automatico l'assetto misurato come peggiore. Priorità alta.

**Come rispondere in 5 secondi:**
```bash
grep -A3 '"Realtime"' ProcioneMGR/appsettings.json
```

---

## 🟠 Decisioni architetturali da prendere

### Q4 — Microstructure: integrare o eliminare?

**Il fatto.** 6 file, 1.166 righe, 5 file di test, zero DI, zero UI, unico consumatore
`tools/PlatformExpand`.

**Perché serve una decisione umana.** Non è una questione tecnica ma di **strategia di ricerca**.
`IncrementalIcGate` (467 righe) è il gate anti-ridondanza che servirebbe subito ad
`AlphaFactorFactory` con 158+ candidati; OFI e tape sono le uniche feature di microstruttura
disponibili. Ma la memoria del progetto dice che la ricerca D3 sull'OFI è stata **misurata senza il
pilota C5**, quindi il verdetto sul valore predittivo non è chiuso.

**Le due strade.**
- **(A) Integrare** — costo medio, valore alto e immediato sul gate IC.
- **(B) Spostare sotto `tools/`** — costo minimo, toglie l'illusione che faccia parte della
  piattaforma.

**Domanda diretta:** l'OFI resta una linea di ricerca aperta o è stata chiusa?

### Q5 — `JumpModel`: si integra o si cancella?

**Il fatto.** 288 righe, testate, con test che dimostrano che produce regimi **più persistenti** del
K-means — cioè che risolve un difetto noto del rilevatore in esercizio. Zero riferimenti in produzione.

**Perché serve una decisione umana.** Non riesco a determinare **perché** non sia mai stato cablato.
Le ipotesi sono incompatibili fra loro:
1. è stato scritto, misurato e **scartato** (ma allora perché i test lo promuovono?);
2. è stato scritto e il cablaggio è stato dimenticato;
3. è pronto e in attesa di un ciclo di misura.

Non ho trovato un report in `docs/` che chiuda la questione.

**Cosa cambia.** Nel caso 1 va cancellato con una riga di commento che dica perché. Nei casi 2-3 va
cablato dietro `MarketRegime:Model` e misurato.

### ~~Q6 — La selezione feature per IC avviene dentro o fuori dai fold?~~ ✅ **CHIUSA il 2026-08-08**

**Risposta.** La domanda era mal posta, e il disegno reale è migliore di quanto supponevo.

`IIcFeatureSelector` ha **un solo consumatore in tutto il repository**:
`Components/Pages/FeatureSelection.razor:564`. **Non fa parte della pipeline automatica** — è uno
strumento esplorativo manuale.

La pipeline ha **la propria** selezione, in `FeatureEngineeringStage` (`AnalysisStages.cs`),
confinata al periodo di selezione con un'istruzione esplicita nel codice:
`// ANTI-LOOK-AHEAD: only the selection range feeds any choice.`
La separazione è strutturale (`PipelineDateRanges`: Selection vs Holdout) e ogni stage la rispetta;
`NullTwinValidationStage` gira sull'holdout, `MlModelTrainingStage` sulla selezione.

**G-15 è derubricato.** Resta un rischio **umano**: `/feature-selection` permette di esplorare l'IC
su qualunque intervallo, holdout incluso. Se l'operatore sceglie i fattori guardandolo, il leakage
entra dalla persona e nessun gate lo vede. → proposta di rimedio in
[20_DEEP_DIVE_CODE_ANALYSIS.md](20_DEEP_DIVE_CODE_ANALYSIS.md) §6.

**Nel cercare la risposta è emerso un problema più grave**: il gate DSR usa N ≤ 15 mentre il run
prova migliaia di combinazioni — vedi **D-01** nel documento 20. La domanda giusta non era «la
selezione è dentro i fold?» ma «**il gate sa quante volte abbiamo davvero provato?**». La risposta
è no.

### Q7 — `MeanVarianceOptimizer` e `RiskParityOptimizer` servono ancora?

**Cosa non so.** Se siano rimasti solo come strumento di confronto per l'operatore (scelta legittima)
o se l'intenzione fosse renderli applicabili.

**Cosa cambia.** Nel primo caso C-05 non è un gap ma una funzionalità, e va **documentato** in
`/portfolio` («questa pagina confronta; l'allocatore operativo è HRP»). Nel secondo, va fatta la
Fase 4.

---

## 🟡 Ambiguità da chiarire

### Q1 — Le due serie di audit convivono o una sostituisce l'altra?

`docs/audit/` contiene ora:
- la serie del **2026-08-04** (`00_INDEX` … `13_DEEP_DIVE_CODE`), caricata nel notebook NotebookLM;
- questa serie del **2026-08-08** (`00_MASTER_INDEX`, `01_PROJECT_MAP`, …).

Numerazioni sovrapposte (`01_PROJECT_OVERVIEW` vs `01_PROJECT_MAP`), contenuti complementari ma
parzialmente ridondanti. **Nulla è stato sovrascritto.**

**Opzioni:** (a) tenere entrambe e rinominare questa serie con prefisso (es. `R00_`…);
(b) archiviare la serie A in `docs/audit/archive-2026-08-04/`;
(c) fondere le due in una sola serie.

Va decisa anche perché la serie A è **la sorgente del notebook NotebookLM**: se questa la sostituisce,
il notebook va ricaricato (protocollo di `CLAUDE.md` §5).

### Q8 — `Sentiment:EnableMlFeature`: da quando esiste la copertura?

Per accendere la feature sentiment nel ML serve sapere **da quale data** `SentimentMetricPoint` e
`AltDataPoint` hanno copertura continua. È un dato di runtime (query sul DB), non deducibile dal codice.

Senza questo, accendere la feature falsa ogni backtest che parta prima di quella data.

### Q9 — `Drift:Enabled=false`: scelta o dimenticanza?

Il ciclo drift → registry → richiesta di riaddestramento è **completo e testato**, ma spento.
Non ho trovato un report che spieghi lo spegnimento (a differenza di `DriveProtectiveExits` e
`DriveDecisions`, che hanno report dedicati).

**Cosa cambia.** Se è una scelta, va documentata come le altre. Se è residuo di uno sviluppo mai
attivato, la Fase 7 lo accende gradualmente.

### Q10 — `tools/PlatformExpand`: strumento vivo o archeologia?

5.848 righe in un solo file, il più grande del repository. I risultati che cita (`huntdense`,
`voloverlay`, `volsingle`) sono la fonte dei commenti più importanti nel codice di produzione — per
esempio l'onestà di `VolatilityScaler` sui 2 casi su 12.

**Cosa non so.** Se venga ancora eseguito o sia il sedimento di campagne concluse.

**Cosa cambia.** Se vivo, va rifattorizzato (G-09) e i suoi gate vanno condivisi con la pipeline. Se
concluso, va congelato con un README che dica quali risultati ha prodotto e quando — perché i
commenti del codice di produzione ci si appoggiano.

### Q11 — `events.proto`: previsione o residuo?

Definito in `ProcioneMGR.Contracts/Protos/`, nessun publisher né subscriber. Riservato per un bus
futuro o rimasto da un progetto abbandonato?

### Q12 — Quante corsie girano davvero, e in che modalità?

`Trading:LaneCount` è configurabile e **congelato alla prima lettura**. La memoria del progetto parla
di 8 corsie Paper (2026-08-04), l'example non lo fissa. Serve lo stato runtime
(`/trading` o query su `TradingEngineState`).

Rilevante perché `CorrelatedExposureGuard` e `LaneInvariantWatchdog` hanno costo proporzionale al
numero di corsie, e la Fase 4 (cambio di allocatore) va applicata **solo a corsie ferme**.

---

## 🔵 Verifiche che richiedono l'app in esecuzione

Questo audit è stato fatto **solo sul codice**, come richiesto. Le seguenti non sono determinabili
staticamente e richiederebbero l'app su `http://localhost:5199` (livello 4 di
`docs/STANDARD-VERIFICA.md`):

| # | Verifica | Perché conta |
|---|---|---|
| V1 | Le 32 pagine si aprono senza errori di runtime | l'analisi statica non trova un `NullReferenceException` in un `@if` |
| V2 | I pannelli di `/admin/autonomy` scrivono davvero (hot-reload ~1 s) | il pattern è corretto nel codice; l'effetto va visto |
| V3 | "Esegui ora" agisce sull'istanza del hosted service | la registrazione doppia è corretta; l'effetto va visto |
| V4 | `DataAvailability` dichiara numeri veri | è il presidio contro i "controlli che rassicurano" |
| V5 | Stato reale delle corsie e loro modalità | Q12 |
| V6 | Copertura temporale del sentiment | Q8 |

---

## Riepilogo: le tre domande da porre per prime

1. **Q2 — I segreti sono stati ruotati?** Se no, tutto il resto aspetta.
2. **Q6 — La selezione IC è dentro i fold?** Da qui dipende la validità di ogni risultato storico.
3. **Q4/Q5 — Microstructure e JumpModel: dentro o fuori?** Sono le due voci che pesano di più sulla
   percezione di «macchina sconnessa», e la risposta è una decisione, non un'analisi.
