# ProcioneMGR — Roadmap Kubernetes, Fase 2: Orchestrazione su Kubernetes e Gestione Infrastrutturale

**Continuazione di `docs/ROADMAP-K8S-FASE1.md`** — la Fase 1 ha progettato il Dockerfile, il Job di
migrazione e gli endpoint di health check senza ancora scrivere un solo manifesto Kubernetes; questa
Fase 2 formalizza quei pezzi in `Deployment`/`Service`/`ConfigMap`/`Secret` reali, con la stessa
disciplina delle fasi precedenti: ogni raccomandazione del PDF generico viene verificata contro il
codice prima di essere accettata.

---

## 0. Premessa: due assunzioni del PDF che il codice smentisce

Il PDF descrive `ConfigMap`/`Secret`/`Deployment` con la semantica K8s standard, corretta in
generale ma scritta per un'app stateless generica. Leggendo il codice reale emergono due conflitti
specifici, non ipotetici:

**1. `ConfigMap` presuppone configurazione sola-lettura; ProcioneMGR ha configurazione scritta a
runtime.** `Services/Config/AppConfigWriter.cs` (usato da `Services/Trading/SafetyConfigWriter.cs`
e dal pannello `/admin/autonomy`) fa un **read-modify-write reale sul file `appsettings.json` su
disco** (`Path.Combine(env.ContentRootPath, "appsettings.json")`, riga 33), protetto da un
`SemaphoreSlim` contro scritture concorrenti (righe 24-26). Il provider di configurazione JSON
dell'host ha `reloadOnChange=true` (commento esplicito in `AppConfigWriter.cs:7-9` e
`SafetyConfigWriter.cs:5-6`): entro ~1 secondo dal salvataggio, chi legge via `IOptionsMonitor<T>`
vede i nuovi valori **senza riavvio**. Un `ConfigMap` montato come volume in Kubernetes è
**sola-lettura dal pod**: se il file finisse lì, il salvataggio da `/trading` o `/admin/autonomy`
fallirebbe (o, nel caso di un workaround per renderlo scrivibile, le modifiche andrebbero perse a
ogni riavvio del pod, perché un volume ConfigMap viene rimontato da zero dall'oggetto API a ogni
avvio — non è un volume persistente). Questo non è un dettaglio implementativo minore: è la
funzionalità che oggi permette a un operatore di alzare/abbassare i limiti di sicurezza (`/trading`,
pannello sicurezza) senza restart, e va preservata.

**2. `Deployment` presuppone `RollingUpdate` e potenzialmente più repliche; ProcioneMGR ha un vincolo
di singleton già stabilito in Fase 0.** §1.1/§2 di `ROADMAP-K8S-FASE0.md` hanno già stabilito
`replicas: 1` come non negoziabile (i worker di trading vivono nello stesso processo della UI) e
hanno segnalato il rischio di una `RollingUpdate` con `maxSurge` che crea temporaneamente 2 pod.
Questa Fase 2 rende quella regola concreta nel manifesto (Sezione 1).

Le due cose sono collegate da un'osservazione che il PDF non poteva fare: **dato che `replicas: 1`
è già un vincolo di sicurezza indipendente**, un `PersistentVolumeClaim` `ReadWriteOnce` (che con più
repliche sarebbe un problema — contesa di accesso — ma con una sola replica è innocuo) è la soluzione
naturale al problema (1): risolve la scrivibilità senza toccare una riga di codice applicativo,
sfruttando un vincolo già deciso invece di introdurne uno nuovo. Sezione 3 sviluppa questo punto.

---

## 1. Deployment

**`replicas: 1`, `strategy: Recreate`** (non `RollingUpdate`). Motivazione diretta da Fase 0 §1.1: con
`RollingUpdate` e `maxSurge: 1` (il default se non specificato altrimenti), Kubernetes crea il pod
nuovo **prima** di terminare quello vecchio — per una finestra reale, seppur breve, esisterebbero 2
processi con `TradingWorker`/`ExecutionWorker`/`PromotionWorker` attivi sulle stesse 3 corsie
(`Services/Trading/TradingLanes.cs`). `Recreate` termina sempre il pod esistente prima di crearne uno
nuovo: un breve downtime pianificato (accettabile, coerente con "conferma manuale obbligatoria per
Live" — il sistema non deve mai agire senza supervisione continua) invece di un rischio di doppia
esecuzione.

**`PodDisruptionBudget` con `minAvailable: 1`**: con una sola replica questo non "protegge la
disponibilità" nel senso classico (non c'è nulla da distribuire), ma forza `kubectl drain`/manutenzioni
del nodo a **fallire esplicitamente** invece di terminare silenziosamente l'unico pod di trading — dà
all'operatore un segnale esplicito invece di un'interruzione a sorpresa, coerente con il principio
"mai un'azione irreversibile senza intervento umano" già presente nel codice (`RequireManualConfirmationForLive`).

**`resources.requests`/`resources.limits`**: **non ancora determinabili con numeri precisi** — Fase 0
§5 ha pianificato il profiling con `dotnet-counters` sul processo reale (uso UI, run pipeline, sweep
di optimization, training LightGBM) proprio per questo scopo. Se quel profiling non è ancora stato
eseguito, va fatto **prima** di fissare i valori definitivi nel manifesto — inserire numeri
indovinati qui contraddirebbe il metodo di tutte le fasi precedenti (verificare, non assumere).
Placeholder conservativo da rivedere: `requests` bassi (il processo idle è leggero — nessun
caricamento eager di modelli ML, confermato in Fase 1 §3), `limits` con margine ampio per assorbire i
picchi noti (sweep di optimization, training `GradientBoostingReturnPredictor`/LightGBM) senza
OOMKill, che sarebbe il caso peggiore possibile per un processo con potenzialmente una posizione
aperta.

**Probes** (wiring concreto degli endpoint progettati in Fase 1 §3):
- `startupProbe`: `httpGet /healthz/live`, `failureThreshold` generoso (es. 30 tentativi ×
  `periodSeconds: 2` = 60s di margine) — copre sia il fail-fast su master key placeholder
  (`Program.cs:415-422`, che comunque termina il processo subito, non è un caso di probe lento ma di
  crash immediato) sia il round-trip Postgres bloccante di `DbInitializer.InitializeAsync`
  (`Program.cs:450-453`) prima che l'HTTP listener accetti connessioni.
- `readinessProbe`: `httpGet /healthz/ready` (include `AddNpgSql`, Fase 1 §3) — tolto dagli endpoint
  del Service se Postgres è irraggiungibile, senza riavviare il pod.
- `livenessProbe`: `httpGet /healthz/live` — probe "dumb", nessuna dipendenza esterna, per non
  innescare un riavvio a cascata se solo Postgres ha un problema transitorio (stesso principio del
  PDF, qui applicato con endpoint reali).

---

## 2. Service

`ClusterIP`, selector sui label standard (Fase 0 §7: `app.kubernetes.io/name=procionemgr`,
`app.kubernetes.io/component=web`), porta allineata a quanto impostato nel Dockerfile (Fase 1 §1,
`ASPNETCORE_URLS=http://+:8080`).

**Session affinity (Fase 0 §1.2) — oggi non necessaria, non per assenza del problema ma perché il
vincolo di Sezione 1 lo rende moot**: con `replicas: 1` non esiste un secondo pod a cui un client
potrebbe essere instradato per errore, quindi la sticky session che Fase 0 §1.2 identificava come
necessaria per il circuito SignalR di Blazor Server non ha nulla da fare oggi. Torna rilevante solo
se in una fase futura (dopo un'eventuale estrazione Strangler Fig, Fase 0 §1.3) la UI web venisse
scalata indipendentemente dai worker di trading — da riprendere in quel momento, non ora.

---

## 3. Configurazione: ConfigMap (baseline immutabile) + PersistentVolumeClaim (stato operativo)

Verificando `Program.cs` sezione per sezione, la configurazione si divide in **tre livelli distinti
per come viene letta**, non due come assumerebbe un PDF generico — e questa Fase 2 ricalca
esattamente il confine che il codice stesso disegna, invece di inventarne uno nuovo:

### 3.1 Sezioni lette una sola volta all'avvio (`.Get<T>()`, mai `IOptionsMonitor`)

`Execution` (`Program.cs:135`), `FactorCache` (`159`), `Registry` (`271`), `EnsembleComparator`
(`368`), `PipelineSupervisor` (`375`), più `Logging`/`AllowedHosts`. Nessuna di queste ha un pannello
UI che le modifica a runtime — un `ConfigMap` montato come **variabili d'ambiente** (convenzione
ASP.NET Core a doppio underscore, es. `Registry__MinChampionDeflatedSharpe`) è perfettamente
adeguato: un cambiamento richiede comunque un riavvio del pod, che è esattamente il comportamento
odierno (il processo le legge una volta e basta).

### 3.2 Sezioni con `Configure<T>()` + `IOptionsMonitor<T>` (hot-reload, editabili da UI)

`Trading:Safety` → `SafetyConfiguration` (`Program.cs:106`, pannello `/trading`, iniettato in
`Trading.razor:13`), `Trading:LiveExecution` → `LiveExecutionOptions` (`114`,
`Autonomy.razor:18`), `Drift` → `DriftMonitorOptions` (`243`, `Autonomy.razor:22`), `Llm` →
`LlmOptions` (`355`, `Autonomy.razor:21` — solo `Enabled`/`Model`/`MaxTokens`/`PollIntervalMinutes`,
**non** la API key, che resta solo `ANTHROPIC_API_KEY` env var), `AutoReapply` → `AutoReapplyOptions`
(`373`, `Autonomy.razor:19`), `PromotionEvaluator` → `PromotionEvaluatorOptions` (`390`,
`Autonomy.razor:20`). **Non è una coincidenza che questo elenco coincida esattamente con i pannelli
di `/admin/autonomy` e `/trading`**: è il confine che il codice ha già tracciato tra "config di
sistema" e "config operativa modificabile da un umano senza restart". Queste sei sezioni **devono**
restare nel file scritto da `AppConfigWriter`, quindi vanno su un volume scrivibile, non un
`ConfigMap`.

**Design proposto**: un `PersistentVolumeClaim` `ReadWriteOnce` (nessun problema di contesa —
`replicas: 1`, Sezione 1) montato al posto di `appsettings.json` nel content root del pod. Al primo
avvio (o dopo un ripristino da zero del volume) il file non esiste ancora: un piccolo
`initContainer` lo seed-a copiando una versione baseline **da un `ConfigMap` immutabile** (generato
dal repository, versione git-taggata) **solo se il file non esiste già** sul PVC (`test -f
/data/appsettings.json || cp /seed/appsettings.json /data/appsettings.json`) — idempotente: al primo
deploy popola i valori di default sicuri, ai deploy successivi **preserva le modifiche fatte
dall'operatore**, esattamente come oggi il file sopravvive ai riavvii del processo su una singola
macchina.

**Proprietà fail-safe, non fail-open**: se il PVC venisse perso/ricreato da zero, il reseed
ripartirebbe dai default del template (`appsettings.json.example`): `RequireManualConfirmationForLive
= true`, `Trading:LiveExecution:Enabled = false`. Un guasto sul volume riporta il sistema allo stato
più conservativo, non a uno permissivo — proprietà da verificare esplicitamente in Fase 6 (verifica
su `kind`), non solo da assumere.

### 3.3 Dati sensibili (Secret, Sezione 4)

`ConnectionStrings:PostgresConnection`, `Security:MasterKey`, `ANTHROPIC_API_KEY` — mai nel
`ConfigMap` né sul PVC scrivibile, coerente con come sono già trattati oggi (`.gitignore`, comment in
`appsettings.json.example`).

---

## 4. Secret

Verificato in Fase 1: il binding a variabili d'ambiente non richiede alcuna modifica di codice
(`ConnectionStrings__PostgresConnection`, convenzione standard, già usata in `.claude/launch.json:16`;
`PROCIONE_MGR_MASTER_KEY`, letto esplicitamente in `AesGcmEncryptionService.cs:50`;
`ANTHROPIC_API_KEY`, letto in `AnthropicLlmClient.cs:32`). Per il cluster locale `kind` di Fase 0 §3,
un `Secret` Kubernetes nativo (`kubectl create secret generic`) è sufficiente: nessun secret store
esterno da installare ora, coerente con la decisione già presa in Fase 0 §3 ("cosa NON serve ancora")
e con la scelta piattaforma differita di Fase 0 §6.

**Limite da tenere a mente per una futura produzione (non un'azione da fare ora)**: un `Secret`
nativo K8s è solo base64 (non cifrato) a meno che il cluster non abbia l'**encryption at rest di
etcd** abilitata — irrilevante su `kind` locale (macchina di sviluppo singola, non multi-tenant), ma
da rivalutare insieme alla scelta della piattaforma di produzione (Fase 0 §6: Vault/Azure Key Vault
via Secrets Store CSI Driver, stesso criterio "da decidere dopo Fase 1-2" già fissato).

---

## 5. Verifica sul cluster `kind` locale

Estende la verifica già pianificata in Fase 1 §6 (build immagine, `kind load docker-image`, manifest
minimi, suite Playwright di Fase 0 §4) con un controllo **nuovo e specifico di questa fase**, non
presente nel PDF perché dipende dalla scoperta di Sezione 3:

1. Applica i manifest completi (`Deployment` con PVC+initContainer seed, `Service`, `ConfigMap`
   baseline, `Secret`) nel namespace `procionemgr-staging`.
2. Dal pod in esecuzione, modifica un valore da `/trading` (pannello sicurezza) — es. abbassa
   `MaxPositionSizePercent`.
3. Elimina il pod (`kubectl delete pod`) e lascia che il `Deployment` (`replicas: 1`) lo ricrei.
4. Verifica che il valore modificato al passo 2 **sia ancora presente** dopo il riavvio (prova che il
   PVC, non un `ConfigMap`, sta effettivamente persistendo lo stato operativo) — questo è il test che
   il PDF non avrebbe mai scritto, perché non sapeva che questa scrittura a runtime esiste.
5. Riesegui la suite Playwright di Fase 0 §4 come verifica di non-regressione generale.

---

## Tabella riassuntiva: attività Fase 2

| # | Attività | Dipende da | Priorità |
|---|---|---|---|
| 1 | Manifesto Deployment (`replicas:1`, `strategy: Recreate`, probes, PDB) | Fase 0 §1.1/§2, Fase 1 §3 | P0 |
| 2 | Manifesto Service (`ClusterIP`) | Fase 0 §7 (naming/label) | P0 |
| 3 | ConfigMap baseline (sezioni "lette una volta", §3.1) | — | P0 |
| 4 | PVC + `initContainer` di seed (sezioni hot-reload, §3.2) | Fase 0 §1.1 (`replicas:1` rende innocuo `ReadWriteOnce`) | P0 |
| 5 | Secret nativo K8s (§4) | Fase 0 §3/§6 (nessun secret store esterno ancora) | P0 |
| 6 | `resources.requests/limits` definitivi | Fase 0 §5 (profiling `dotnet-counters`, se non ancora fatto) | P1 — bloccato da dati mancanti |
| 7 | Verifica persistenza PVC su `kind` (§5) | 1-4 completate | P0 |

---

## Prossimo passo operativo

Le attività 1-5 possono procedere in parallelo (sono manifest indipendenti); l'unica dipendenza reale
è che il Dockerfile di Fase 1 esista già (lo progetta, non lo esegue — se non ancora costruito
davvero, va fatto prima di poter testare 1-7 concretamente). L'attività 6 resta esplicitamente
bloccata dal profiling di Fase 0 §5: se quel profiling non è stato eseguito, va segnalato come
prerequisito mancante piuttosto che stimare risorse a occhio. La verifica di persistenza del PVC
(attività 7) è il criterio di accettazione più importante di questa fase — è la prova diretta che la
scoperta di Sezione 3 (read-modify-write su `appsettings.json`) è stata gestita correttamente e non
silenziosamente rotta dalla containerizzazione.
