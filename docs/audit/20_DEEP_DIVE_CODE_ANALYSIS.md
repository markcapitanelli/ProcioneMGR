# 20 — DEEP DIVE: lettura del codice riga per riga

> **Secondo passaggio, 2026-08-08.** Il primo audit (documenti 00-10) era **strutturale**: mappa,
> cablaggi, integrazione. Questo è **di merito**: apre i file, verifica che le formule siano quelle
> giuste e che i controlli facciano quello che dichiarano.
>
> **Metodo:** lettura diretta del sorgente + verifica numerica delle formule con esempi eseguiti.
> Nessun file sorgente modificato.

---

## Sommario dei nuovi reperti

| ID | Reperto | Tipo | Severità |
|---|---|---|---|
| **D-01** | Il gate DSR usa N ≤ 15 mentre il run prova migliaia di combinazioni — e il numero vero è già misurato | metodologico | 🔴 **High** |
| **D-02** | `MaxTotalExposurePercent` non vincola l'esposizione aggregata sui Futures; il commento dice che l'asimmetria è conservativa, non lo è nella direzione che conta | safety | 🟠 **Medium-High** |
| **D-03** | La validazione anti-sovrapposizione Selection/Holdout esiste in un solo punto (salvataggio UI) | metodologico | 🟡 Medium |
| **D-04** | Il gemello nullo ha geometria intra-barra idealizzata (stoppini simmetrici) | metodologico | 🔵 Low |
| **D-05** | ~~Ora del digest~~ — **riqualificato: è deliberato e documentato** | — | ✅ non è un difetto |
| **D-06** | **La rotazione della master key non è implementata** — cambia il piano di rimedio di C-01 | safety / operativo | 🔴 **High** |
| **Q6** | **CHIUSA** — la selezione IC non è dove pensavo, e il disegno è corretto | — | ✅ |

> **D-05 e D-06 vengono dal terzo passaggio** (catalogo file-per-file, documento 21): leggere il
> `<summary>` di **ogni** file ha smentito un mio reperto e ne ha prodotto uno più grave.

E, altrettanto importante, **ciò che ho verificato essere corretto** — vedi §7.

---

## 1. Q6 CHIUSA — dove avviene davvero la selezione delle feature

Nel primo audit avevo segnalato come rischio più serio (G-15) un possibile leakage di selezione.
**La risposta è che il rischio non esiste come lo avevo formulato, e il disegno è migliore di quanto
supponessi.**

### I fatti

`IIcFeatureSelector` ha **un solo consumatore in tutto il repository**:

```
ProcioneMGR/Components/Pages/FeatureSelection.razor:564
    var sel = Selector.Select(candidates, candles, config);
```

Non è usato da `MlLabService`, né da `MlModelTrainingStage`, né da `DatasetBuilder`. **Non fa parte
della pipeline automatica**: è uno strumento esplorativo manuale della pagina `/feature-selection`.

La pipeline ha **la propria** selezione, in `Stages/AnalysisStages.cs` (`FeatureEngineeringStage`),
e la confina con un'istruzione esplicita:

```csharp
// ANTI-LOOK-AHEAD: only the selection range feeds any choice.
var candles = await ctx.Candles.GetAsync(primary.Symbol, primary.Timeframe,
                                          ctx.Ranges.SelectionFrom, ctx.Ranges.SelectionTo, ct);
```

La separazione dei periodi è strutturale (`PipelineModels.cs:24-30`):

```csharp
public sealed class PipelineDateRanges
{
    public DateTime SelectionFrom { get; set; }
    public DateTime SelectionTo   { get; set; }
    public DateTime HoldoutFrom   { get; set; }
    public DateTime HoldoutTo     { get; set; }
}
```

E ogni stage rispetta il confine:

| Stage | Range usato |
|---|---|
| `FeatureEngineeringStage` | Selection |
| `RegimeAnalysisStage`, `VolatilityRegimeStage`, `PairsScreeningStage` | Selection |
| `MlModelTrainingStage` | Selection (`ModelStages.cs:54`, `TrainingDataTo = SelectionTo`) |
| `StrategyDiscoveryStage` | Selection |
| `HoldoutValidationStage` | **Selection e Holdout separati** (`ModelStages.cs:359` e `:365`) |
| `NullTwinValidationStage` | **Holdout** (`NullTwinValidationStage.cs:66`) |

### Verdetto

**G-15 va derubricato.** Il disegno è: si sceglie tutto sul periodo di selezione, si giudica sul
periodo di holdout, e i due non si toccano. La selezione delle feature dentro il periodo di
selezione **rende ottimistico solo il punteggio interno a quel periodo** — che non è il punteggio su
cui si decide.

Resta però un rischio residuo **umano**, non di codice: `/feature-selection` permette di esplorare
l'IC su **qualunque** intervallo, compreso quello che poi verrà usato come holdout. Se l'operatore
sceglie i fattori guardando anche l'holdout e poi lancia un run, il leakage entra dalla persona.
Il codice non può impedirlo — ma la pagina potrebbe **dichiararlo**. Vedi la proposta in §6.

---

## 2. 🔴 D-01 — Il gate anti-overfitting è sistematicamente più permissivo di quanto la piattaforma stessa assume

**È il reperto più importante di questo passaggio.**

### Tre conteggi di "tentativi" nello stesso run, che non si parlano

| # | Dove | Quanto vale | A cosa serve |
|---|---|---|---|
| 1 | `PowerCheckStage`, parametro `expectedTrials` | **300** (default) | dichiarare a priori lo Sharpe minimo rilevabile (MinTRL) |
| 2 | `StrategyDiscoveryResult.CombinationsTested` | **il numero reale misurato** | **solo visualizzazione** |
| 3 | `OverfittingGate.Apply` → `nominalTrials = validated.Count` | **≤ `topN` = 15** (default) | **il gate DSR che decide davvero** |

Il conteggio n.2 è vero e disponibile:

```csharp
// StrategyDiscoveryEngine.cs
:60      tested += r.TotalCombinationsTested;
:99      Candidates = candidates.OrderByDescending(c => c.OutOfSampleSharpe).Take(config.TopN).ToList(),
:101     CombinationsTested = tested,
```

Ma finisce **solo** in `Discovery.razor:173` («N job · N combinazioni · Ns») e nell'output console
dei tool CLI. **Non raggiunge mai il DSR.**

Il conteggio n.1 finisce in `PowerCheckOutput.TrialsAssumed` ed è usato **solo** per la frase del
riepilogo. **Non raggiunge mai il DSR.**

Il gate usa il n.3, che è il più piccolo dei tre — e `EffectiveTrials` lo **riduce ulteriormente**
collassando i candidati correlati (`ModelStages.cs:721`).

### Perché conta: quanto si abbassa la soglia

La soglia SR* che il candidato deve battere è, dal codice
(`DeflatedSharpeRatio.ExpectedMaxSharpe`):

> SR\* = σ · [ (1−γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)) ],  γ = 0,5772

Calcolata con la formula del codice:

| N | SR* (in unità di σ) | rapporto vs N=15 |
|---:|---:|---:|
| **15** | **1,771 σ** | 1,00× |
| 50 | 2,276 σ | 1,29× |
| 100 | 2,531 σ | 1,43× |
| **300** | **2,896 σ** | **1,64×** |
| 1.000 | 3,255 σ | 1,84× |
| **3.000** | **3,555 σ** | **2,01×** |
| 10.000 | 3,861 σ | 2,18× |

**Lettura:** se un run prova 3.000 combinazioni — del tutto plausibile per una discovery su
strategia × coppia × timeframe × griglia parametri — la barra applicata è **la metà** di quella che
dovrebbe essere.

### L'ironia: il codice lo sa già

`PowerCheckStage` avvisa esplicitamente, nella descrizione del suo stesso parametro:

> *«quante combinazioni verranno provate nel run (discovery × parametri): determina fin dove arriva
> il puro caso (E[max] del DSR). **Sottostimarlo gonfia la potenza dichiarata**»*

È esattamente ciò che accade — non nel PowerCheck, ma nel gate a valle.

### Copertura di test

Nessun test asserisce che N rifletta la ricerca reale.
`OverfittingGateTests.Apply_StrongSteadyHoldoutFewTrials_Survives` — il nome stesso dice
«FewTrials». `EffectiveTrialsTests` è ottimo ma verifica il collasso dei correlati, non l'origine
di N.

### Proposta

1. Portare `CombinationsTested` nel `PipelineContext` (`ctx.TrialsExplored`), sommandolo su tutti
   gli stage che cercano (`StrategyDiscovery`, `CreativeDiscovery`, `MlModelTraining` se fa griglia,
   `AlphaMining`).
2. `OverfittingGate.Apply` usa `max(validated.Count, ctx.TrialsExplored)` come N nominale, prima di
   applicare `EffectiveTrials`.
3. Se i due divergono, **scriverlo nel log del run**, come già si fa per il collasso degli effettivi:
   `"N tentativi DSR: 15 candidati → 3.180 combinazioni realmente provate."`
4. Test di regressione: un run che prova 1.000 combinazioni e ne tiene 15 deve produrre SR\* più alto
   di un run che ne prova 15.

> ⚠️ **Va detto chiaramente:** applicare questa correzione **abbasserà i DSR storici** e farà
> probabilmente cadere candidati oggi "sopravvissuti". È il risultato atteso, ed è coerente con la
> storia di questo progetto — dieci ondate di ricerca già chiuse in negativo con onestà. Un gate che
> assolve troppo non protegge nessuno.

---

## 3. 🟠 D-02 — `MaxTotalExposurePercent` non vincola l'aggregato sui Futures

### Il meccanismo

`SafetyChecker.Evaluate` check 2 (`SafetyChecker.cs:47`):

```csharp
if (capital > 0m && status.UsedCapital + notional > capital * cfg.MaxTotalExposurePercent / 100m)
```

Ma le due grandezze sommate **non sono nella stessa unità** sui Futures
(`TradingEngine.BuildSafetyStatus`, righe 1528-1533):

```csharp
// Futures: margine bloccato (non nozionale). NB: SafetyChecker.MaxTotalExposurePercent
// somma questo a order.Notional (leveraged) per il nuovo ordine — asimmetria
// volutamente conservativa (fa scattare il limite prima, mai dopo), non un bug.
UsedCapital = _state.MarketType == MarketType.Futures
    ? _positions.Sum(p => p.MarginBalance)          // MARGINE
    : _positions.Sum(p => p.Quantity * p.EntryPrice); // NOZIONALE
```

- `Order.Notional => Quantity * (Price ?? 0m)` — **nozionale pieno**, leveraged
  (`TradingModels.cs:227`)
- `OpenPosition.MarginBalance` per Futures = **margine** (`PositionOpener.cs:371`), per Spot =
  nozionale (`PositionOpener.cs:183`)

### Il commento è corretto sul singolo ordine, sbagliato sull'accumulo

Sul **nuovo ordine** l'asimmetria è effettivamente conservativa: si usa il numero grande.
Sulle **posizioni già aperte** no: ognuna conta 1/leva della propria esposizione reale. Più la
posizione invecchia nel libro, meno pesa nel controllo.

### Esempio numerico eseguito

Capitale 10.000 · leva 5× · `PositionSizePercent` 2% ⇒ margine 200, nozionale **1.000** per posizione.
Limiti: `MaxPositionSizePercent` 10% (1.000) · `MaxTotalExposurePercent` 50% (5.000).

| `MaxOpenPositions` | Posizioni aperte | `UsedCapital` finale (margini) | **Esposizione vera** | Limite dichiarato |
|---:|---:|---:|---:|---:|
| 5 (default) | 5 | 1.000 | 5.000 = **50%** | 50% ✅ |
| 10 | 10 | 2.000 | 10.000 = **100%** | 50% ❌ |

Con `MaxOpenPositions=10`, l'ultimo controllo calcola `1.800 + 1.000 = 2.800 ≤ 5.000` → **passa**,
mentre l'esposizione reale è il **doppio** del limite dichiarato.

### Il vincolo effettivo

> Sui Futures il tetto reale è **`MaxPositionSizePercent × MaxOpenPositions`**, non
> `MaxTotalExposurePercent`.
> Coi default coincidono **per coincidenza**: 10% × 5 = 50%. Cambiare uno dei due li fa divergere
> in silenzio, e la UI continua a mostrare un limite che non sta vincolando.

### Attenuanti reali (che riducono la severità, non la annullano)

1. `StartAsync` rifiuta l'avvio se `PositionSizePercent × leva > MaxPositionSizePercent`
   (`TradingEngine.cs:~248`): il singolo ordine è sempre capped.
2. `RiskProfileTests.cs:31,39` asserisce l'invariante `PositionSizePercent × leva ≤
   MaxPositionSizePercent ≤ MaxTotalExposurePercent` **per i profili predefiniti**.
3. `CorrelatedExposureGuard` è attivo di default e limita l'esposizione **correlata** fra corsie.
4. `MaxOpenPositions = 5` di default: bisogna cambiarlo deliberatamente per rompere la coincidenza.

### Copertura di test

`SafetyCheckerTests.cs:61` testa l'accumulo **solo con semantica Spot**
(`UsedCapital = 4.800` + 500 > 5.000). **Nessun test copre il mix margine/nozionale sui Futures.**

### Proposta

Rendere le due grandezze omogenee — la scelta più difendibile è confrontare **nozionale con
nozionale**:

```csharp
UsedCapital = _state.MarketType == MarketType.Futures
    ? _positions.Sum(p => p.Quantity * p.EntryPrice)   // nozionale, come lo Spot
    : _positions.Sum(p => p.Quantity * p.EntryPrice);
```

…e correggere il commento. Se invece si preferisce che `MaxTotalExposurePercent` significhi
*margine impegnato*, allora il nuovo ordine deve contribuire con il **suo margine**
(`notional / leverage`), non col nozionale. **Le due letture sono entrambe legittime; la mescolanza
non lo è.**

Aggiungere un test: N posizioni Futures a leva L devono far scattare `MaxTotalExposurePercent` alla
stessa esposizione nozionale di N posizioni Spot equivalenti.

---

## 4. 🟡 D-03 — L'invariante Selection/Holdout è applicata in un punto solo

L'unica barriera contro la sovrapposizione dei periodi è in `PipelinePageService.SaveConfigAsync`:

```csharp
:191  if (draft.Ranges.SelectionTo <= draft.Ranges.SelectionFrom || draft.Ranges.HoldoutTo <= draft.Ranges.HoldoutFrom)
          return PipelineSaveResult.Error("Range di date non validi.");
:193  if (draft.Ranges.HoldoutFrom < draft.Ranges.SelectionTo)
          return PipelineSaveResult.Error("L'holdout deve iniziare DOPO la fine della selezione (mai sovrapposti).");
```

`PipelineEngine` deserializza i range (`:222`) e **non li rivalida**;
`PipelineEngine.ValidateConfiguration` valida solo il DAG degli stage (`PipelineDagValidator`);
`PowerCheckStage.ValidateInput` controlla solo l'holdout vuoto o invertito, non la sovrapposizione.

**Attenuanti verificate.** Ho tracciato tutti gli scrittori di `DateRangesJson`:
`PipelinePageService:222` (validato), `:243` (duplicazione di una config già validata),
`tools/PlatformExpand:1411` (copia da una config base). Nessun percorso attivo crea range non
validati. Un JSON malformato produce tutte le date a `DateTime.MinValue` e il run **fallisce
rumorosamente** a `PowerCheckStage` — fail-closed per fortuna, non per progetto.

**Il rischio è futuro, non attuale:** una config creata prima che il controllo esistesse, una
scrittura diretta a DB, o un futuro creatore programmatico (il `CampaignPlanner` oggi non tocca i
range) girerebbe con un holdout contaminato **senza che nulla se ne accorga**.

**Proposta.** Spostare il controllo in `PipelineEngine` all'avvio del run — un `ValidateInput`
sull'intero contesto, non solo per stage. È la stessa lezione di `NullTwinJudge`: *una politica, una
sola implementazione*.

---

## 5. 🔵 D-04 e D-05 — due sfumature

### D-04 · Il gemello nullo ha stoppini simmetrici

`NullTwinGenerator.Generate` è, nel merito, **il pezzo di codice meglio ragionato che ho letto in
questa codebase**. Usa stationary block bootstrap (Politis–Romano) e — scelta non ovvia e corretta —
**segno i.i.d. per barra** invece che a blocchi, con questa motivazione nel doc-comment:

> *«un sign-flip a blocchi … lascerebbe vivo il momentum dentro il blocco, che una caccia rifarebbe
> sua»*

Dichiara anche il prezzo pagato (muore la correlazione segno-volatilità, cioè il leverage effect).

**La sfumatura:** la ricostruzione della barra distribuisce lo stoppino **simmetricamente**
(righe 92-102): `High = bodyHi + wick/2`, `Low = bodyLo − wick/2`. Le barre reali hanno stoppini
asimmetrici. Poiché i backtest usano high/low intra-barra per decidere se stop-loss e take-profit
vengono toccati, **la probabilità di tocco sui gemelli differisce da quella sui dati reali**. Il
nullo resta valido per un edge basato sui rendimenti; è leggermente distorto per strategie con stop
stretti — cioè proprio l'orizzonte intraday/swing breve di questa piattaforma.

**Proposta.** Campionare la ripartizione dello stoppino (quota sopra/sotto il corpo) dalla stessa
barra sorgente `j`, come già si fa per volume e ampiezza. Costo: due righe.

### D-05 · L'ora del digest giornaliero — ⚠️ **riqualificato: è deliberato**

`Services/Notifications/DailyDigest.cs:148,164,166` usa `DateTime.Now` (ora locale), non `UtcNow`.
È l'**unico** uso di `DateTime.Now` in tutta la codebase.

**Nel terzo passaggio (catalogo file-per-file) ho trovato che è dichiarato**, nel doc-comment di
`DigestOptions`:

> *«L'ora è quella LOCALE della macchina (il PC del proprietario): il digest serve a un umano che si
> sveglia, non a un cron UTC.»*

**Va quindi derubricato da difetto a scelta motivata.** Resta una sola conseguenza da conoscere: i
manifesti in `infra/k8s/` prevedono che il monolite possa girare in container, dove l'ora locale è
tipicamente UTC — lì `Hour = 8` invierebbe alle 08:00 UTC, cioè 09:00/10:00 italiane. Non è un bug,
è una dipendenza dall'ambiente che vale la pena rendere esplicita se il guscio finirà in cluster.

**Proposta (facoltativa):** fuso esplicito (`Notifications:Digest:TimeZone`, default `Europe/Rome`)
il giorno in cui il digest gira in container.

---

## 5-bis. 🔴 D-06 — La rotazione della master key **non è implementata**, e questo cambia il piano di rimedio di C-01

**Reperto del terzo passaggio, e il più consequenziale dopo D-01.**

Nel documento 06 avevo prescritto per C-01: *«ruotare la master key e ri-cifrare le credenziali
exchange»*. Il catalogo file-per-file ha portato alla luce che **quello strumento non esiste**.
`Services/Security/AesGcmEncryptionService.cs` lo dichiara — è **l'unico `TODO` reale di tutta la
codebase**:

> *«L'unico pezzo TODO reale, deliberatamente rimandato perché è una feature a sé (non un fix
> puntuale): **la rotazione della chiave**, per cui il formato riserva già il byte di versione ma
> **manca ancora il supporto multi-chiave (decifra-con-la-vecchia/cifra-con-la-nuova) e uno
> strumento di ri-cifratura di massa**.»*

### Cosa significa in pratica

Ruotare la master key **oggi** rende **illeggibili** tutte le credenziali exchange già a database.
Non esiste un percorso automatico per ri-cifrarle.

**La buona notizia:** la piattaforma è preparata a questo scenario, anche se non lo automatizza.
`ExchangeCredentialReader` decifra **riga per riga** e, quando una riga è cifrata con una chiave
diversa, non fallisce: marca `IsReadable = false`, azzera i campi segreti (mai plaintext parziale) e
la UI di `/settings/exchanges` mostra il badge **«reinserire le credenziali»**. `MasterKeyProbe`
lo dichiara inoltre all'avvio con `LogCritical` + notifica + banner.

### Il piano di rimedio corretto per C-01

1. Fare un **backup del database** (`/admin/backup` esiste già).
2. Annotare **fuori dalla piattaforma** le API key/secret degli exchange in uso — dopo la rotazione
   non saranno più recuperabili da qui.
3. Generare la nuova master key e sostituirla (env `PROCIONE_MGR_MASTER_KEY` o Secret K8s
   `Security__MasterKey` — **entrambi i pod devono avere la stessa copia**, guscio e motore
   decifrano le stesse righe).
4. **Fermare le corsie Testnet/Live prima**: una corsia che gira e non riesce più a decifrare le
   credenziali fallirebbe gli ordini.
5. Riavviare e **reinserire a mano** le credenziali in `/settings/exchanges`, guidati dal badge.
6. Ruotare separatamente `Trading:GrpcSharedSecret` e la password Postgres (indipendenti dalla
   master key, nessuna ri-cifratura necessaria).

> **Per un operatore singolo con due exchange la procedura è di pochi minuti.** Ma va detta:
> «ruota la chiave» suona come un'operazione di routine e **non lo è**, e questa è probabilmente
> la ragione per cui la rotazione non è ancora avvenuta (domanda aperta **Q2**).

**Proposta a valle:** implementare il supporto multi-chiave che il formato già prevede — il byte di
versione (`SchemeVersion = 1`) è riservato apposta. Con «decifra con la vecchia, cifra con la nuova»
la rotazione diventerebbe un'operazione ordinaria invece di un evento.

---

## 6. Proposta trasversale: rendere visibile il leakage umano

Il rischio residuo di Q6 (§1) e il reperto D-01 hanno la stessa radice: **la piattaforma misura la
cosa giusta ma non la mostra dove serve a decidere.**

Due interventi di poche righe:

1. **`/feature-selection`** — mostrare l'intervallo esplorato accanto ai risultati e, se si
   sovrappone all'holdout dell'ultima configurazione di pipeline salvata, un avviso:
   *«stai guardando l'IC su un periodo che sarà usato come holdout: sceglierne i fattori è leakage».*
2. **`/pipeline` e `/discovery`** — mostrare, accanto al DSR, il numero di combinazioni realmente
   provate e l'N usato dal gate. Se divergono, dirlo.

È esattamente la disciplina che questa piattaforma già applica altrove (`DataAvailability`, il
banner di freschezza di `/trading`, la sentinella d'ombra delle uscite): **degradare dicendolo**.

---

## 7. Ciò che ho verificato essere CORRETTO

Questa sezione conta quanto le precedenti: dice cosa **non** va toccato in una ricostruzione.

### 7.1 Formule statistiche — verificate contro la letteratura

| Componente | Verifica | Esito |
|---|---|---|
| `DeflatedSharpeRatio.ProbabilisticSharpe` | PSR = Φ((SR−SR\*)·√(T−1) / √(1 − γ₃·SR + (γ₄−1)/4·SR²)) | ✅ corretta (Bailey–López de Prado) |
| `DeflatedSharpeRatio.ExpectedMaxSharpe` | approssimazione del massimo di N gaussiane con Eulero–Mascheroni | ✅ corretta |
| `ReturnMoments` | momenti **di popolazione**, curtosi **non in eccesso** (normale = 3) | ✅ coerente con la convenzione richiesta dalle formule |
| `PurgedTimeSeriesCv.Split` | banda esclusa `[testStart − purge, testEnd + embargo)` | ✅ purge prima, embargo dopo — semantica giusta |
| `GarchModel.Fit` | riparametrizzazione ω=e^θ₀, α=σ(θ₁)·0,999, β=σ(θ₂)·(0,999−α) | ✅ i vincoli ω>0, α,β≥0, α+β<1 valgono **per costruzione** |
| `GarchModel.StudentTLogDensity` | scala s² = σ²(ν−2)/ν ⇒ Var(εₜ)=σ²ₜ indipendente da ν | ✅ standardizzazione corretta — dettaglio che molte implementazioni sbagliano |
| `HierarchicalRiskParityOptimizer` | distanza di Mantegna → dendrogramma → quasi-diagonale → bisezione con α = varRight/(varLeft+varRight) | ✅ López de Prado cap. 16, fedele |
| `NullTwinGenerator` | stationary block bootstrap + segno i.i.d. per barra | ✅ corretto (vedi D-04 per la sfumatura) |
| `KellyCalculator` | binario p−(1−p)/b · continuo μ/σ² · empirico dalla distribuzione | ✅ presenti tutte e tre, con l'empirico dichiarato ≤ del binario |

### 7.2 Sicurezza — verificata riga per riga

`SafetyChecker.Evaluate` — 11 controlli, tutti corretti nel merito:

| # | Controllo | Nota |
|---|---|---|
| 0 | capitale ≤ 0 ⇒ rifiuto | **fail-closed**: senza denominatore valido nessun ordine è dimensionabile |
| 1 | dimensione singola posizione | ✅ |
| 2 | esposizione totale | ⚠️ vedi **D-02** |
| 3 | perdita giornaliera, `>=` | fail-closed **alla soglia**; l'asimmetria col drawdown è stata corretta apposta |
| 4 | drawdown, `>=` | ✅ |
| 5 | numero posizioni aperte | ✅ |
| 6 | intervallo minimo fra ordini | ✅ |
| 7 | conferma manuale in Live | ✅ |
| 8 | emergency stop attivo | ✅ |
| 9 | quantità e prezzo > 0 | ✅ |
| 10 | leva massima (solo Futures) | ✅ |

Raccoglie **tutte** le violazioni invece di fermarsi alla prima — scelta giusta: l'operatore vede
l'intero quadro.

`TradingEngine.StartAsync` — quattro gate a strati, tutti con motivazione scritta:
quarantena (con l'osservazione acuta che riavviare cancellerebbe l'evidenza contabile che ha fatto
scattare l'allarme) · master key placeholder blocca Live · leva oltre il limite · **coerenza sizing
vs safety** (impedisce una corsia che girerebbe senza mai poter aprire un ordine).

### 7.3 Igiene del codice — scansione sistematica

| Pattern cercato | Risultato |
|---|---|
| `async void` | **zero** (l'unica occorrenza è un commento che spiega perché lo si evita) |
| `.Result` / `.Wait()` su Task | solo dopo `await Task.WhenAll(...)` (`TradingPageService.cs:333-336`) — **corretto** |
| `catch { }` vuoti | 8, **tutti giustificati**: `JSDisconnectedException` (circuito Blazor caduto), `OperationCanceledException` (spegnimento), `DisposeAsync` del lease (il dispose non deve lanciare, e il lock si rilascia lato server) |
| `DateTime.Now` | 1 sola occorrenza — vedi **D-05** |
| `double` su percorsi di denaro | **zero** (tutto `decimal`) |

### 7.4 Layer exchange

`ExchangeRateLimitHandler` — ritiro reattivo su **429** e **418** (IP bannato da Binance), rispetto
di `Retry-After`, backoff esponenziale con jitter, con la motivazione giusta nel commento:
*«continuare a martellare dopo un 429 è esattamente il comportamento che trasforma un limite in un
ban»*. Firma HMAC-SHA256 con `recvWindow=5000` e `timestamp` sincronizzato da `ExchangeClock`.

### 7.5 Pipeline — disciplina dei periodi

Ogni stage legge il range corretto (tabella in §1). `HoldoutValidationStage` esegue il candidato
**due volte**, su selezione e su holdout separatamente (`ModelStages.cs:359` e `:365`), così la
degradazione fra i due periodi è misurata e non supposta.

`OverfittingGate.Apply` combina **tre** gate indipendenti: DSR con N effettivi, PBO di pannello via
CSCV a 10 partizioni, e un **permutation test a blocchi di segno lungo il tempo** — descritto nel
codice come *«l'unica randomizzazione onesta su questi dati»*, che è la risposta diretta all'errore
già pagato (randomizzare su asset correlati fabbrica falsa significatività). Il p-value è riportato
ma **non blocca** di default: *«va prima osservato sul campo, poi promosso a gate — la stessa strada
fatta dal DSR»*. È la disciplina "osserva prima di decidere" applicata con coerenza.

---

### 7.6 `BacktestEngine` — la causalità, verificata riga per riga

È il componente da cui dipende ogni numero prodotto dalla piattaforma. Verificato:

| Aspetto | Implementazione | Esito |
|---|---|---|
| Ordine dentro la barra | stop/target **prima** del segnale (righe 258-336), poi limiti pendenti, poi nuovi ingressi | ✅ corretto |
| Barra d'ingresso | esclusa dai controlli di uscita (`i > entryIndex`): «il fill avviene alla sua close, l'escursione precedente non riguarda la posizione» | ✅ corretto |
| Stop **e** target nella stessa candela | si assume **l'esito peggiore (stop)** | ✅ conservativo |
| Gap oltre lo stop | fill a `Math.Min(stopLevel, candles[i].Open)` — se la barra apre sotto lo stop si riempie all'apertura, non al livello | ✅ **corretto e non ovvio**: molti backtest qui sono ottimisti |
| Gap oltre il target | `Math.Max(entryPrice*(1+tp), Open)` — simmetrico | ✅ |
| Liquidazione | stessa logica gap-aware | ✅ |
| Dosaggio volatilità | `closeArr.AsSpan(0, i + 1)` — solo fino alla barra corrente, e **la stessa funzione pura del live** (`Trading.Internal.VolatilityScaler`) | ✅ backtest e live **non possono divergere** |
| Modalità maker | il limite si appoggia solo quando il **segnale cambia**, non a ogni candela in cui persiste | ✅ con motivazione corretta: altrimenti «il tasso di riempimento misurerebbe la pazienza del ciclo invece della selezione avversa» |

**Convenzione da conoscere:** il fill avviene alla **close della barra che ha generato il segnale**
(`price = closeArr[i]`). È leggermente ottimistico in assoluto — non si può operare esattamente alla
chiusura che si è appena osservata — ma è la stessa convenzione del motore dal vivo
(`TradingWorker` processa barre **chiuse**). La proprietà che conta, cioè che backtest e live non
divergano, è rispettata.

### 7.7 Infrastruttura, migrazioni, CI

| Verifica | Esito |
|---|---|
| Segreti nei file tracciati di `infra/k8s/` | ✅ **nessuno**: `trading-config.env` e `ui-config.env` non hanno chiavi sensibili valorizzate. C-01 resta confinato al solo `appsettings.json.pre-audit-test-20260729-141448` |
| Migrazioni EF Core | 20 migrazioni + snapshot, l'ultima `20260805025522_AddTradePostMortems` — coerente con l'introduzione del migrate-on-startup |
| CI | 3 workflow: `ci.yml` (esegue `dotnet test`), `docker-build.yml`, `e2e-kind.yml` (end-to-end su cluster kind) |

---

## 8. Aggiornamento del quadro dei rischi

| Rischio (dal doc 04 §S) | Stato dopo il deep dive |
|---|---|
| Lookahead nei fattori | ✅ presidiato (`RollingOps` causali + `AuditCvLeakageTests`) |
| Leakage di CV | ✅ presidiato (purge + embargo + CPCV) |
| **Leakage di selezione** | ✅ **risolto in Q6**: la selezione è confinata al periodo di selezione; resta il rischio **umano** via `/feature-selection` |
| **Inflazione da prove multiple** | 🔴 **peggiore del previsto — D-01**: N usato dal gate ≤ 15 contro migliaia di combinazioni provate |
| Overfitting di composizione | ⚠️ invariato |
| Falsa significatività da correlazione | ✅ presidiato (permutation test a blocchi di segno + `EffectiveTrials`) |
| Deriva non sorvegliata (Alpha158) | ❌ invariato (G-04) |
| Determinismo incompleto | ⚠️ invariato (G-08) |
| **Esposizione aggregata Futures** | 🟠 **nuovo — D-02** |

---

## 9. Priorità aggiornate

Rispetto al blueprint del documento 07, il deep dive **sposta due voci in cima**:

| Ordine | Intervento | Perché ora |
|---|---|---|
| 1 | **C-01** rimuovere e ruotare i segreti | invariato, resta la prima cosa |
| 2 | **D-01** portare il conteggio reale dei tentativi nel gate DSR | il gate che protegge dall'illusione è oggi il più permissivo dei tre conteggi presenti nello stesso run |
| 3 | **D-02** omogeneizzare le unità di `MaxTotalExposurePercent` | un limite di sicurezza che non vincola ciò che dichiara |
| 4 | **C-02** `DriveProtectiveExits` default a `false` | invariato |
| 5 | **D-03** validare i range all'avvio del run, non solo al salvataggio | una politica, una sola implementazione |
| 6 | **D-04** stoppini asimmetrici nel gemello nullo | due righe, migliora il nullo proprio sull'orizzonte di riferimento |

Il resto del blueprint (documento 07) resta valido invariato.
