# PRD — Memoria della caccia e composizione multi-strategia

**Data:** 2026-08-14 · **Mandato:** proprietario, in chat · **Stato:** FASI 0–4 ESEGUITE (stesso giorno), Fase 5 in corso

> **[2026-08-14, sera] Esecuzione.** Fasi 0–4 implementate, testate e passate dalla review
> avversaria multi-lente (15 finding confermati e TUTTI corretti — fra cui uno HIGH: le stringhe
> non troncate ai limiti di colonna potevano far fallire l'intera indicizzazione; 3 refutati).
> Suite completa 2536/2536 prima delle correzioni, ri-verificata dopo. Consegnato:
> `/research` (archivio candidati: indice a righe derivato `ResearchCandidates`, aggregati per
> famiglia, motivi di scarto classificati, fascia grigia in evidenza, Rianalizza→Optimization),
> `includeGreyZone`+`redundancyWarnRho` sull'assemblaggio (giudice unico `GreyZone.IsGrey`,
> grigi mai al posto dei sopravvissuti, correlazione dell'HRP dichiarata in proposta e negli
> alert), quinta fonte gamba «Da fascia grigia» in `/ensemble` col badge e «Valuta ridondanza
> gambe», dichiarazioni T4 in Note/azioni, semantica lorda dell'esposizione fissata dai test
> (T5). Risposte alle domande aperte del §9, prese in autonomia per procedere: una coppia per
> corsia (vincolo architetturale attuale confermato); stessa soglia grigia già in produzione
> (mai due giudici); `maxLegs` invariato a 3 anche coi grigi (i grigi riempiono solo i posti
> liberi). Fase 5 (verifica in browser + applicazione su corsia Paper) documentata nel report
> di chiusura.

## 0. Le due domande, testuali

1. La caccia salva i candidati e le proposte trovate, anche in fascia grigia, così da non
   ripetersi e poterle rianalizzare?
2. Ha senso unire due o più strategie diverse (fascia grigia o migliori) che si applicano alla
   STESSA coppia, per farle operare insieme su una sola corsia? È fattibile? È intelligente? Ci
   sono rischi?

Questo documento risponde a entrambe con i numeri veri del database e del codice, non a memoria.

---

## 1. Verdetto in breve

**Domanda 1 — la premessa è già stata posta il 2026-08-06, e la risposta di allora resta vera:
i candidati SONO GIÀ salvati.** `ValidatedCandidates` ha 6.554 righe (24 metriche + parametri +
motivo di scarto ciascuna), 84 run, dal 2026-07-02 — vedi **"Filone R — Archivio della ricerca"**
in [ROADMAP.md](ROADMAP.md) (2026-08-06). Il buco non è la scrittura, è la **lettura**: ogni run
è un blob JSON isolato, non esiste una pagina che li attraversi. Filone R lo aveva già scoperto e
scritto (R1-R4) ma **nessuno dei quattro punti è stato costruito** — la Ondata Risanamento ha
avuto la precedenza. Qui riprendo R1/R2 e li estendo con la lente che serve alla domanda 2
(raggruppamento per coppia, fascia grigia in evidenza, azione diretta).

**Domanda 2 — fattibile, e più intelligente di quanto sembri: l'80% esiste già.** La piattaforma
ha da tempo un motore di composizione multi-strategia per corsia (`Ensemble`, pagina `/ensemble`)
e un assemblatore automatico che pesa più sopravvissuti con **Hierarchical Risk Parity** — lo
stesso algoritmo di López de Prado usato altrove nel progetto, scelto apposta perché non
impazzisce con asset/segnali correlati (`EnsembleAssemblyStage`, Pipeline Stage 11). Il problema
non è costruire la composizione: **è che quel motore accetta SOLO candidati "sopravvissuti
pieni", e dal 13 luglio i sopravvissuti pieni sono zero** — quindi è fermo per fame, non per
difetto. La tua idea — ammettere anche la fascia grigia — è esattamente la chiave che lo
rimette in moto, sulla stessa coppia (un ensemble è già "una corsia, un simbolo, N gambe").

**Cosa manca davvero** (il lavoro genuinamente nuovo, piccolo e preciso):
1. far arrivare la fascia grigia dentro `EnsembleAssemblyStage`, dietro un interruttore esplicito;
2. un avviso di ridondanza/correlazione fra le gambe proposte — il calcolo esiste già dentro
   l'HRP e oggi viene **scartato** (`HierarchicalRiskParityOptimizer.cs:28`, la matrice di
   correlazione si calcola e non esce mai dalla funzione);
3. dichiarare, ogni volta, che una proposta "da fascia grigia" è un secondo giro di selezione
   sopra un giro già passato dai gate — mai spacciarla per un sopravvissuto vero.

**Rischi reali, non teorici** — falsa diversificazione (due gambe correlate sembrano
diversificazione e non lo sono), overfitting spostato alla scelta della combinazione, gambe che
aprono posizioni opposte sullo stesso simbolo senza nettarle, e un bug di conteggio
dell'esposizione aggregata già noto e documentato che più gambe concorrenti rendono più facile
da colpire. Dettaglio in [§6](#6-rischi).

**Raccomandazione:** procedere, in quest'ordine — Fase 0 (Filone R, quasi gratis, sblocca la
lettura) prima di tutto; poi l'estensione mirata di `EnsembleAssemblyStage`; mai un passo verso
Live diverso da quello che esiste oggi per qualunque altra corsia.

---

## 2. Cosa esiste già (con riferimenti precisi)

| Pezzo | Cosa fa oggi | File |
|---|---|---|
| **Discovery** (`/discovery`, "la caccia") | Sweep strategia×coppia×timeframe, walk-forward, badge DSR; **classifica GLOBALE Top 20** (non per coppia), salvataggio manuale riga per riga | `Components/Pages/Discovery.razor`, `Services/Discovery/StrategyDiscoveryEngine.cs` |
| **Pipeline** (`/pipeline`, 15 stadi) | Validazione disciplinata: ogni candidato diventa un `ValidatedCandidate` con **24 campi** (Sharpe selezione/holdout, DSR, PBO, p-value permutazione, percentile gemello nullo, motivo di scarto…) | `Services/Pipeline/PipelineModels.cs:418` |
| **`ValidatedCandidates` (dati)** | **6.554 candidati, 84 run, dal 2026-07-02**, già a database | misurato 2026-08-06, Filone R |
| **Fascia grigia — la definizione** | `IsGrey`: bocciato ma con Sharpe holdout positivo E trade>0 E (bocciato SOLO per finestra corta OPPURE DSR in [0.80, 0.95)) | `Services/Fleet/FleetStateReader.cs:236` |
| **FleetOrchestrator / GreyDeployer** | Usa `IsGrey` per **proporre** (mai assegnare da solo) una corsia libera a un candidato in fascia grigia — ma solo sulle corsie 3+, opt-in, e SOLO sui candidati che arrivano dal Pipeline | `Services/Fleet/`, journal in `OrchestratorDecision` |
| **`EnsembleAssemblyStage`** (Pipeline stage 11) | Prende i sopravvissuti dello STESSO run, li pesa con un optimizer di portafoglio selezionabile (**HRP** di default, MeanVariance, RiskParity), produce una `EnsembleProposal` (gambe + pesi + nota) | `Services/Pipeline/Stages/DecisionStages.cs:25` |
| **Il cancello che oggi blocca tutto** | `ValidateInput` rifiuta il run **se zero sopravvissuti pieni**; i leg sono filtrati a `.Where(v => v.Survived)` — la fascia grigia non entra mai qui | `DecisionStages.cs:59` e `:72-76` |
| **Ensemble** (`/ensemble`) | Combina **più strategie in un unico portafoglio su una corsia**, capitale riallocato per rolling Sharpe (shrinkage verso l'equipeso), Min/Max % per gamba, SL/TP/trailing e algoritmo di esecuzione per gamba. **Un solo `TradingEngine` per corsia esegue tutte le gambe** — la regola del singolo scrittore non è in discussione, è già rispettata per costruzione | `Components/Pages/Ensemble.razor`, `Services/Ensemble/EnsembleManager.cs` |
| **Fonti gamba oggi** | Predefinite, Salvate (`SavedStrategies`, richiede salvataggio manuale da Discovery/Backtest/Optimization), Modelli ML compatibili (stesso symbol/timeframe), Champion del registry | `Ensemble.razor:255-335` |
| **"Applica al Trading"** | Bottone (+ un loop di re-apply automatico, opt-in) che traduce una `EnsembleProposal` del Pipeline in gambe Ensemble precompilate con SL/TP e Sharpe atteso | `Services/Pipeline/PipelineApplier.cs` |
| **Correlazione fra gambe — calcolata e persa** | `HierarchicalRiskParityOptimizer.Optimize` calcola la matrice di correlazione per il clustering gerarchico, ma il risultato restituito porta SOLO i pesi finali — la matrice non esce mai dalla funzione | `Services/Portfolio/HierarchicalRiskParityOptimizer.cs:28,42` |
| **Esposizione aggregata — limite noto** | `SafetyChecker` somma `UsedCapital` della corsia contro `MaxTotalExposurePercent` ad ogni ordine, ma un difetto di conteggio già documentato nel codice permette al capitale esposto reale di superare il doppio del limite senza far scattare il controllo | `Services/Trading/SafetyExposure.cs:13`, `Services/Trading/SafetyChecker.cs:47` |

**La lettura d'insieme**: Discovery e Pipeline sono due "cacce" parallele con forme dati diverse
(`DiscoveryCandidate` vs `ValidatedCandidate`); solo la seconda alimenta oggi fascia grigia e
composizione. Non è un difetto da correggere in questo PRD — è il motivo per cui il lavoro nuovo
si aggancia al binario Pipeline→Ensemble, dove la disciplina (gate, un solo giudice) è già in
piedi, invece di reinventarla sul binario Discovery.

---

## 3. Il problema preciso

Due guasti diversi, non uno:

**A. Non si legge quello che c'è già (Filone R, mai eseguito).** Misurato il 2026-08-06: dal 13
luglio, 66 run e 5.131 candidati, **zero sopravvissuti** — ma fra i bocciati, **702 hanno Sharpe
holdout medio positivo** (532 fermati solo da "pochi trade", Sharpe medio +1,10; 170 dal DSR,
+1,07). Sono candidati potenzialmente in fascia grigia, oggi visibili solo con una query scritta
a mano. Nessuna pagina raggruppa per coppia, nessuna evidenzia la fascia grigia, nessuna
permette di "riprendere da dove si era arrivati" senza ri-lanciare la caccia da capo.

**B. Il motore di composizione esiste ma è a digiuno.** `EnsembleAssemblyStage` è sofisticato
(HRP, bias di regime, rinormalizzazione) e produce **9 proposte in tutto, l'ultima il
2026-07-09** — non perché sia rotto, ma perché il suo cancello d'ingresso (`Survived`) non si
apre da un mese. La domanda 2 dell'utente, in termini di codice, è: *«quel cancello può aprirsi
anche per la fascia grigia, con le dovute cautele?»* — e la risposta è sì, con tre aggiunte
precise (§5).

**Un terzo filo, minore ma reale**: la scelta di QUALI candidati combinare è essa stessa una
selezione, e il progetto ha già pagato il conto di trattare la selezione come gratuita
(445k combinazioni testate su asset correlati → 0 significative, [Ricerca e dosaggio
2026-07-20]). Ammettere la fascia grigia nella composizione riapre la stessa domanda un piano
più in alto: combinare aggiunge un nuovo "numero di tentativi" che va dichiarato, non nascosto.

---

## 4. Parte 1 — Rendere leggibile ciò che già c'è

Riprende **Filone R** (`docs/ROADMAP.md`, 2026-08-06) così com'era scritto, con un'estensione
per servire anche la Parte 2.

| # | Cosa | Perché | Stato |
|---|---|---|---|
| R1 | Pagina `/research`: vista trasversale sui candidati archiviati — motivi di scarto aggregati, tasso di passaggio per famiglia/simbolo/timeframe, andamento nel tempo | Rende interrogabile ciò che già c'è; le query esistono, provate a mano il 2026-08-06 | **aperto**, mai iniziato (nessun `Research.razor` nel codice) |
| R2 | Indicizzare i candidati in righe invece che in blob JSON — tabella derivata, **ricostruibile dagli artefatti esistenti**, nessun dato nuovo da raccogliere | Oggi ogni domanda costa una scansione `jsonb_array_elements` su ~6 MB; con l'archivio che cresce non regge | **aperto** |
| R3 | Tasso di passaggio per famiglia dentro `CampaignPlanner` | La rotazione delle cacce è oggi cieca alla resa storica delle famiglie | **aperto**, non bloccante per questo PRD |
| R4 | Riesaminare i 702 "bocciati per potenza statistica" con la finestra giusta, come esperimento dichiarato | Unico bacino di candidati con Sharpe medio positivo mai prodotto | **aperto**, non bloccante |
| **R5 (nuovo)** | **Filtro/raggruppamento esplicito per coppia**, con la fascia grigia evidenziata (badge, riuso di `IsGrey`) e due azioni dirette per riga: **"Rianalizza"** (precompila Backtest/Optimization) e **"Proponi per composizione"** (ponte verso la Parte 2, §5) | È la lente specifica che la domanda 1 dell'utente chiede e che R1 da solo non copre (R1 è statistica aggregata, qui serve il candidato singolo, navigabile per coppia) | nuovo |

**Vincoli di design (non negoziabili):**
- **Nessun secondo giudice.** R5 riusa `FleetStateReader.IsGrey` (o lo promuove a un posto
  condiviso se serve anche fuori da `Services/Fleet/`) — non inventa una seconda soglia di
  "grigio". Il progetto ha già pagato il conto di due regole per la stessa cosa (D2.b, doppia
  regola sull'ampiezza finestra → verdetti contraddittori sulla stessa serie).
- **Sola lettura.** Come `/experiments` oggi: R1/R2/R5 non scrivono nulla di nuovo sul percorso
  di trading, non toccano `ValidatedCandidates`, aggiungono solo un indice/vista per leggerlo.
- **Retention.** Nessuna nuova policy necessaria in Fase 0: i dati ci sono già e crescono al
  ritmo delle cacce esistenti (misurato: ~80 candidati/run in media). R2 valuta se serve un
  tetto quando l'archivio supererà la decina di milioni di righe — non è un problema oggi.

---

## 5. Parte 2 — Comporre dalla fascia grigia, sulla stessa coppia

**Cosa NON cambia:** un ensemble resta "una corsia, un simbolo, un timeframe, N gambe", un solo
`TradingEngine` esegue, mai un passo automatico oltre Paper, mai Live senza il gate umano
esistente. La proposta della domanda 2 sta **dentro** l'architettura attuale, non ne richiede
una nuova — è la cosa che rende la proposta solida invece che rischiosa.

| # | Cosa | Dove | Gate/verifica |
|---|---|---|---|
| T1 | `EnsembleAssemblyStage`: ammettere `IsGrey(candidate)` oltre a `Survived`, dietro un parametro di stage esplicito (`includeGreyZone`, default **false** — mai un cambio di comportamento silenzioso su run esistenti). `ProposedLeg` guadagna un campo di provenienza (`SourceVerdict`: Survived \| Grey) | `DecisionStages.cs:59,72-76`, `PipelineModels.cs:512` | Test di regressione: `includeGreyZone=false` produce byte-identici i pesi di oggi (stesso principio già usato per il default HRP in 2.8) |
| T2 | Esporre l'avviso di ridondanza: la matrice di correlazione già calcolata in `HierarchicalRiskParityOptimizer` esce dalla funzione (nuovo campo su `PortfolioAllocation`), tradotta in un avviso leggibile ("gamba A e gamba B correlate a ρ=0,87 — combinarle diversifica poco") mostrato PRIMA del click "Applica al Trading" | `HierarchicalRiskParityOptimizer.cs:28,42`, `PortfolioMath.cs` | Livello 1: correlazione ricalcolata a mano su una coppia di serie sintetiche note (ρ atteso vs ρ calcolato); livello 2: due gambe indipendenti (rumore) → avviso assente |
| T3 | Quinta fonte gamba in Ensemble: **"Da fascia grigia (caccia)"**, filtrata al symbol/timeframe della corsia — stessa logica già usata per il filtro dei modelli ML compatibili | `Ensemble.razor:255-335` (nuovo blocco), riuso di R5 | Livello 4: nel browser, aggiungere una gamba grigia a una corsia Paper e verificare che il monitor di decadimento (§ esistente) la tracci separatamente |
| T4 | Dichiarazione esplicita nel report: una proposta "da fascia grigia" mostra sempre quante combinazioni ha visto la selezione (numero di gambe candidate × combinazioni possibili), coerente col principio "verdetto statistico e verdetto operativo vanno detti insieme" | `EnsemblePageService`, GuidaPanel di `/ensemble` | Nessun nuovo gate automatico in MVP; verifica operativa (livello 4) che il messaggio compaia sempre, non solo quando è "buono" |
| T5 | Verificare `SafetyExposure` sotto più gambe concorrenti sullo stesso simbolo (posizioni non nettate fra gambe che dissentono) prima di abilitare T3 su corsie Futures a leva | `SafetyExposure.cs`, `SafetyChecker.cs:47` | Test dedicato: N gambe con segnali opposti aperte insieme → l'esposizione aggregata rilevata combacia con la somma dei nozionali reali, entro la tolleranza già nota |

**Non-obiettivi, espliciti:**
- Nessuna assegnazione automatica di corsia dalla fascia grigia — resta click umano, come già
  per il GreyDeployer esistente.
- Nessun nuovo percorso verso Live: le gambe da fascia grigia sono soggette esattamente agli
  stessi cancelli Paper→Testnet→Live di qualunque altra gamba oggi.
- Nessuna ricerca automatica di "quali coppie di strategie combinare fra tutte le coppie
  possibili" — l'MVP è assistito (l'operatore sceglie da una lista filtrata), non un secondo
  motore di ottimizzazione. Un motore che cerca la combinazione migliore sarebbe di nuovo il
  problema del §3 (selezione come se fosse gratuita) e non è richiesto da questo PRD.
- Non tocca `GreyDeployer`/Fleet Orchestrator (resta per l'assegnazione di UNA strategia a UNA
  corsia libera, lanes 3+, autonomo opt-in) — questo PRD è un binario diverso, manuale, dentro
  Ensemble.

---

## 6. Rischi

Risposta diretta alla tua domanda "ci sono dei rischi?" — in ordine di gravità.

1. **Falsa diversificazione.** Due gambe entrambe trend-following sulla stessa coppia sembrano
   "due strategie" ma sono la stessa scommessa raddoppiata. È l'unico rischio per cui l'HRP aiuta
   davvero (pesa meno i cluster correlati) — ma solo se T2 lo rende visibile invece di lasciarlo
   dentro l'allocazione senza spiegazione.
2. **Overfitting spostato alla scelta della combinazione.** Scegliere QUALE coppia di candidati
   combinare, su un universo di centinaia di candidati grigi, è un nuovo giro di selezione. La
   stessa lezione di [Ricerca e dosaggio 2026-07-20] (445k combinazioni → 0 significative,
   asset correlati fabbricano falsa significatività) si applica un piano più in alto. Mitigato da
   T4 (dichiarazione) e dal non-obiettivo "nessuna ricerca automatica della combinazione".
3. **Conflitto di segnale non nettato.** Le gambe di un ensemble hanno oggi capitale e posizioni
   proprie (non condivise): se gamba A è long e gamba B è short sulla stessa coppia nello stesso
   momento, restano DUE posizioni aperte che si pagano lo spread a vicenda invece di annullarsi.
   Non è necessariamente sbagliato (un overlay market-neutral deliberato è legittimo), ma con
   gambe scelte a mano oggi l'operatore se ne accorge; con più gambe suggerite dalla fascia
   grigia, va reso visibile invece di lasciarlo emergere solo a posteriori nel P&L.
4. **Bug di esposizione aggregata, amplificato.** `SafetyExposure` ha un difetto di conteggio già
   documentato in codice (l'esposizione reale può arrivare al doppio del limite dichiarato prima
   che il controllo scatti). Con più gambe concorrenti sullo stesso simbolo la probabilità di
   colpirlo sale. T5 lo mette come prerequisito, non come nota a margine.
5. **Costo di lettura, non di raccolta.** Il Filone R stesso lo segnala: con l'archivio che
   cresce, leggere `ValidatedCandidates` per query ad-hoc (`jsonb_array_elements` su blob) non
   regge. R2 lo risolve prima che serva per la Parte 2.
6. **Falsa sicurezza del badge.** Se una gamba "da fascia grigia" non è marcata come tale in ogni
   schermata (Ensemble, decay monitor, report), un lettore la confonde con un sopravvissuto pieno
   — esattamente la classe di difetto che il progetto chiama "controlli che rassicurano a
   prescindere dalla realtà" (Filone E, tre volte trovata e corretta nel 2026-07-31). `SourceVerdict`
   (T1) esiste apposta per non ripeterla.

**Rischio NON presente**, per chiarezza: la regola del singolo scrittore (regola 2) non è in
discussione — un ensemble multi-gamba resta UN `TradingEngine` per corsia; questo è già vero
oggi per qualunque ensemble esistente, con o senza fascia grigia.

---

## 7. Roadmap a fasi

Ogni fase chiude solo con i [4 livelli di verifica](STANDARD-VERIFICA.md) (unità contro
riferimento indipendente, controllo sul rumore, integrazione con pezzi veri, prova dal vivo nel
browser). Verdetto scritto anche se negativo.

| Fase | Contenuto | Dipende da | Gate di uscita |
|---|---|---|---|
| **0** | R1 + R2 + R5 (lettura, sola lettura, nessun rischio di esecuzione) | — | `/research` (o l'estensione scelta) mostra i 6.554+ candidati reali, filtrabili per coppia, fascia grigia evidenziata; verificato in browser sul DB vero |
| **1** | T1 — `includeGreyZone` su `EnsembleAssemblyStage`, default off | Fase 0 (serve la stessa definizione di grigio, già condivisa) | Test di regressione pesi identici a default off; con default on su un run storico rigiocato, le gambe grigie compaiono etichettate |
| **2** | T2 — avviso di ridondanza/correlazione esposto | Fase 1 | Correlazione ricalcolata a mano su serie sintetiche note; rumore puro (2 gambe indipendenti) → nessun falso avviso |
| **3** | T3 — quinta fonte gamba in Ensemble | Fase 1 (serve `SourceVerdict`) | Dal vivo: aggiunta di una gamba grigia a una corsia Paper reale, tracciata separatamente dal decay monitor esistente |
| **4** | T4 (dichiarazione multiple-testing) + T5 (verifica esposizione aggregata) | Fase 3 | T5 con test dedicato PRIMA di abilitare T3 su corsie Futures/leva |
| **5** | Osservazione Paper su almeno una corsia con gambe da fascia grigia | Fase 4 | Nessuna promozione automatica; verdetto scritto dopo un periodo di forward test comparabile a quello già usato per gli altri filoni (settimane, non giorni) |

**Non bloccante per nessun'altra roadmap aperta** (F, Risanamento-follow-up, guardiani): stesso
principio già dichiarato per il Filone G — questo lavoro entra in coda, non scavalca nulla di
già in corso.

---

## 8. Design UI

Mockup (wireframe, non codice Blazor) delle due schermate nuove/estese, coerenti con lo stile
attuale (tema scuro, badge Bootstrap): [vedi artifact allegato].

- **Schermo 1 — Archivio candidati** (Fase 0): estensione filtrabile per coppia, con badge
  Promosso/Fascia grigia/Scartato, e le due azioni per riga.
- **Schermo 2 — Ensemble, quinta fonte gamba** (Fase 3): elenco candidati grigi della stessa
  coppia/timeframe della corsia, con l'avviso di correlazione (T2) mostrato PRIMA di confermare.

---

## 9. Domande aperte per il proprietario

1. L'assemblaggio Pipeline (`EnsembleAssemblyStage`) sa già proporre gambe su coppie diverse
   nello stesso batch — l'ensemble/corsia però resta vincolato a un solo simbolo. Confermi che
   vuoi restare su **una coppia per corsia** (la tua formulazione originale, ed è anche il vincolo
   architetturale attuale di Ensemble), oppure ti interessa in futuro un secondo filone per
   proposte multi-coppia sulla stessa corsia (richiederebbe di sbloccare `EnsembleConfiguration`
   da un singolo `Symbol` — cambio più grosso, non incluso qui)?
2. La soglia di fascia grigia riusata è quella già in produzione per il Fleet Orchestrator (DSR
   in [0,80–0,95) o "solo pochi trade", con Sharpe holdout positivo). Per la composizione, che
   somma il rischio di più gambe sulla stessa corsia, vuoi la stessa soglia o una più severa
   (es. DSR ≥0,85)?
3. Quante gambe massime in modalità fascia-grigia? Oggi `maxLegs=3` di default per i soli
   sopravvissuti pieni — la stessa soglia o più bassa, dato che ogni gamba in più aumenta il
   rischio §6.3?
