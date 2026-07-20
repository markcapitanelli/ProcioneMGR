# ProcioneMGR — Roadmap Kubernetes, Fase 4: Automazione, CI/CD e Autoscaling Intelligente

**Continuazione di `docs/ROADMAP-K8S-FASE0/1/2/3.md`** — a questo punto esistono (su carta: Dockerfile
e Job di migrazione in Fase 1, manifest Deployment/Service/ConfigMap/Secret/PVC in Fase 2, stack di
osservabilità in Fase 3). Questa Fase 4 chiude il ciclo con GitOps e automazione — ma è la fase in cui
il PDF generico diverge di più dalla realtà di ProcioneMGR, perché il suo pilastro centrale
("autoscaling intelligente") si scontra frontalmente con un vincolo di sicurezza già stabilito fin da
Fase 0.

---

## 0. Premessa: tre attriti tra il PDF e i vincoli già stabiliti

**1. L'autoscaling — il tema centrale della Fase 4 del PDF — è vietato per il Deployment
principale.** Fase 0 §1.1/§2 e Fase 2 §1 hanno già stabilito `replicas: 1`, `strategy: Recreate`,
nessun HPA, `PodDisruptionBudget` con `minAvailable: 1`: i worker di trading (`TradingWorker`,
`ExecutionWorker`, `PromotionWorker`, `PipelineSchedulerWorker`, ecc.) vivono nello stesso processo
della UI e non possono mai esistere in più di un'istanza contemporaneamente, pena ordini o
promozioni duplicate. L'intero toolkit che il PDF presenta (HPA nativo, HPA con metriche custom,
KEDA, VPA in modalità automatica) descrive tecniche per scalare orizzontalmente o verticalmente in
modo automatico — **tecniche che non si possono applicare al Deployment che le Fasi 1-3 hanno
progettato**, non per limite tecnico del cluster ma per lo stesso motivo di sicurezza già
documentato due volte. Questa Fase 4 non ignora il tema (Sezione 3 lo tratta per esteso), ma lo
riposiziona sui pochi carichi di lavoro di ProcioneMGR per cui l'autoscaling è davvero sicuro — i
candidati identificati in Fase 0 §1.3, non il monolite web+worker.

**2. GitOps "tutto in git" confligge con la disciplina sui segreti già in vigore.** Il PDF descrive
un flusso in cui ogni cambiamento allo stato desiderato del cluster passa da un commit Git,
sincronizzato automaticamente da ArgoCD/Flux. Preso alla lettera, questo includerebbe il manifesto
`Secret` (Fase 2 §4: `PROCIONE_MGR_MASTER_KEY`, `ConnectionStrings__PostgresConnection`,
`ANTHROPIC_API_KEY`) — esattamente il tipo di dato che `.gitignore` e la documentazione del progetto
trattano come **mai da committare**, principio già rispettato scrupolosamente per
`appsettings.json` (Fase 0). GitOps qui si applica solo ai manifest non sensibili
(`Deployment`/`Service`/`ConfigMap` baseline/probes/PDB); il `Secret` resta fuori dal repository,
provisionato fuori banda (Sezione 2).

**3. "Sync automatico a ogni push" contraddice la cultura di supervisione già codificata.** Il
codice applica ovunque lo stesso principio — auto-promozione consentita solo fino a Testnet, mai a
Live (`PromotionEvaluator`), conferma manuale obbligatoria per ordini Live (`SafetyChecker.cs:79`).
Applicare lo stesso criterio al deploy: sync automatico accettabile per l'ambiente di staging locale
`kind` (Fase 0 — nessun rischio reale, solo Bitget Demo/Testnet), ma un ipotetico ambiente di
produzione futuro dovrebbe richiedere approvazione manuale del sync, non best-effort automatico —
stesso schema "auto fino a un certo punto, poi manuale" già maturo nel dominio trading, riapplicato
al dominio deploy.

---

## 1. CI/CD: estendere la pipeline esistente, non scriverne una nuova

`.github/workflows/ci.yml` esiste già (restore → build → test → audit vulnerabilità NuGet, su push a
qualunque branch e su PR) — questa fase lo **estende**, non lo sostituisce. Nuovo job, condizionato a
`github.ref == 'refs/heads/master'` (a differenza del job di test, che deve restare ampio su ogni
branch/PR):

1. **Build immagine** (Fase 1 §1: Dockerfile multi-stage, base runtime Debian per compatibilità
   LightGBM) e **bundle di migrazione** (Fase 1 §2: `dotnet ef migrations bundle`).
2. **Tag**: convenzione già fissata in Fase 0 §7, `<git-sha-corto>` — qui concretizzata con un
   registry reale: **GitHub Container Registry** (`ghcr.io/markcapitanelli/procionemgr-web:<sha>`),
   non Docker Hub né un registry di un cloud provider specifico. Motivazione: il repository è già su
   GitHub, l'autenticazione verso `ghcr.io` usa il `GITHUB_TOKEN` di Actions senza credenziali
   aggiuntive da gestire, e non impegna verso nessun cloud provider — coerente con la decisione già
   presa in Fase 0 §6 di non scegliere ancora una piattaforma cloud.
3. **Push** dell'immagine e del bundle di migrazione.
4. **Aggiornamento manifest**: i manifest K8s vivono nella **stessa repository**, sotto
   `deploy/` (non un repository "ops" separato — pragmatismo da solo-developer, evita la
   complessità di due repository sincronizzati che il PDF non giustifica per questa scala). Il job
   aggiorna il tag immagine in `deploy/kustomization.yaml` (o l'equivalente Helm `values.yaml`) e
   crea un commit su `master` con il nuovo SHA — è questo commit, non il push dell'immagine, che
   ArgoCD osserva (Sezione 2).

**Cosa NON entra in questo job**: nessun manifesto `Secret`, nessuna credenziale exchange, nessuna
`PROCIONE_MGR_MASTER_KEY` — quei valori non transitano mai dalla pipeline CI (coerente con la
Sezione 0.2).

---

## 2. ArgoCD

**Installazione**: nuovo namespace `argocd` sul cluster `kind` (terzo namespace "extra" dopo
`procionemgr-staging`, Fase 0 §3, e `observability`, Fase 3 §5 — ognuno con un ciclo di vita
distinto, scelta deliberata di separazione).

**`Application` manifest**: punta al path `deploy/` di questo stesso repository GitHub, branch
`master`, destinazione namespace `procionemgr-staging`.

**Sync policy — differenziata per ambiente, non uniforme come implicherebbe il PDF**:
- Cluster `kind` locale (staging, Sezione 0.3): `automated: { prune: true, selfHeal: true }` —
  accettabile, nessun rischio reale, coerente con la scala "Demo/Testnet" di tutto questo progetto
  oggi (Fase 0).
- Qualunque ambiente futuro con esposizione reale (Fase 0 §6, decisione di piattaforma ancora
  differita): sync **manuale**, che richiede un click esplicito in ArgoCD — stesso principio del
  gate Testnet→Live già in `PromotionEvaluator`, non una scelta nuova ma un riuso dello stesso
  criterio.

**Cosa ArgoCD gestisce e cosa no**: `Deployment`, `Service`, `ConfigMap` baseline (Fase 2 §3.1),
`PodDisruptionBudget`. **Non gestisce**: `Secret` (provisionato manualmente per ambiente,
`kubectl create secret generic` fuori dal flusso GitOps — Sezione 0.2), i **contenuti** del
`PersistentVolumeClaim` (Fase 2 §3.2) — ArgoCD sincronizza la definizione del `PersistentVolumeClaim`
come oggetto K8s, ma non ha visibilità né controllo sul file `appsettings.json` scritto a runtime da
`AppConfigWriter` dentro quel volume. Questo **non è un conflitto**, è per costruzione: un
aggiornamento del `ConfigMap` baseline (es. un nuovo default nel template) non tocca retroattivamente
un file già seed-ato con eventuali modifiche dell'operatore — esattamente il comportamento
"fail-safe, non fail-open" già descritto in Fase 2 §3.2. Vale la pena dirlo esplicitamente qui perché
l'entusiasmo GitOps del PDF ("Git come unica fonte di verità") potrebbe far pensare, sbagliando, che
tutto lo stato del cluster sia ricostruibile dal solo Git — per il PVC operativo non è vero, per
scelta.

**Meccanica del rollout**: nessuna nuova logica da scrivere — ArgoCD applica i manifest, ma è ancora
la `strategy: Recreate` del `Deployment` (Fase 2 §1) a governare come avviene concretamente
l'aggiornamento del pod. GitOps automatizza *quando* e *da dove* arriva il cambiamento, non *come*
viene eseguito.

---

## 3. Autoscaling: dove si applica davvero

| Meccanismo (dal PDF) | Applicabile al Deployment principale? | Dove si applica davvero in ProcioneMGR |
|---|---|---|
| HPA nativo (CPU/memoria) | **No** — `replicas: 1` è un vincolo di sicurezza, non di capacità (Fase 0 §1.1/§2) | Nessun uso oggi |
| HPA con metriche custom (Prometheus Adapter) | **No**, stesso motivo | Utile in futuro **solo** se un servizio verrà estratto dal monolite (Fase 0 §1.3 — es. un `MarketDataSyncWorker` diventato servizio indipendente, stateless, senza worker di trading dentro) |
| KEDA | **No** per il Deployment principale | **Sì**, candidato concreto: i tool CLI già disaccoppiati (`tools/PlatformExpand`, `tools/StrategyHunter`, `tools/DbBackup`, Fase 0 §1.3), una volta containerizzati come `Job`/`CronJob` K8s, sono idempotenti e a esecuzione occasionale — esattamente il profilo per cui KEDA scala a zero tra un'esecuzione e l'altra invece di tenere risorse allocate permanentemente |
| VPA (modalità automatica) | **No** — anche VPA "Auto" evince/ricrea il pod per applicare nuovi limiti di risorsa, il che per il pod di trading equivale a un riavvio non pianificato: stesso rischio già escluso per l'HPA | — |
| VPA (modalità "suggerimento") | **Sì**, continuazione diretta di Fase 0 §5 | Ora che l'app gira davvero su K8s (post Fase 1-2), VPA in modalità osservativa può sostituire/integrare il profiling `dotnet-counters` fatto pre-container, con dati reali del pod invece di stime dal processo su una macchina di sviluppo |

**Nota su Job di migrazione (Fase 1 §2)**: non è "autoscaling" in senso proprio, ma la sua
esecuzione può ora essere automatizzata dalla stessa pipeline CI/CD di Sezione 1 (il job Actions crea
il `Job` K8s aggiornato invece che richiedere un comando manuale) — chiude un cerchio aperto da Fase 1,
dove il bundle era progettato ma innescato a mano.

**In sintesi**: la tabella del PDF resta tecnicamente corretta come descrizione degli strumenti, ma
il criterio con cui ProcioneMGR li adotta è invertito rispetto all'assunzione implicita del PDF — non
"quale meccanismo scala meglio il servizio principale", ma "quali carichi di lavoro sono
strutturalmente sicuri da scalare, e il servizio principale non lo è".

---

## 4. Verifica su `kind`

1. Commit di prova su `master` con una modifica non funzionale (es. un commento) → la pipeline
   estesa di Sezione 1 builda, tagga, pusha su `ghcr.io`, aggiorna `deploy/kustomization.yaml`.
2. ArgoCD (`automated` su staging, Sezione 2) rileva il commit sul manifest e applica — verificare
   che il rollout avvenga con `Recreate` (un solo pod attivo alla volta, mai due, coerente con Fase
   2 §1) e non con una `RollingUpdate` implicita.
3. Verificare che il `Secret` **non compaia mai** negli `Application` manifest sincronizzati da
   ArgoCD (controllo di conformità alla Sezione 0.2, non solo teorico).
4. Modificare un valore da `/trading` (come già fatto in Fase 2 §5) **prima** del deploy di prova, e
   confermare che sopravviva al rollout — stesso test di Fase 2, ripetuto qui per confermare che
   l'automazione GitOps non introduce una regressione sul comportamento già verificato.
5. Riesecuzione della suite Playwright di Fase 0 §4.

---

## Tabella riassuntiva: attività Fase 4

| # | Attività | Dipende da | Priorità |
|---|---|---|---|
| 1 | Estensione `.github/workflows/ci.yml`: build+push immagine e bundle migrazione | Fase 1 §1/§2 (Dockerfile, bundle) | P0 |
| 2 | Repository `deploy/` con manifest Fase 1-3 | Fase 1/2/3 (contenuto dei manifest) | P0 |
| 3 | Installazione ArgoCD, namespace dedicato | Fase 0 §3 (cluster `kind`) | P0 |
| 4 | `Application` ArgoCD, sync automatico solo su staging | Sezione 0.3 (criterio manuale/automatico) | P0 |
| 5 | Esclusione esplicita di `Secret` dal repository GitOps | Fase 2 §4, `.gitignore` esistente | P0 |
| 6 | VPA in modalità suggerimento sul pod reale | Fase 0 §5 (continuazione, dati reali invece di stime) | P1 |
| 7 | KEDA — solo quando i tool CLI diventeranno Job/CronJob | Fase 0 §1.3 (estrazione non ancora fatta) | Differita |
| 8 | Automazione del Job di migrazione dentro la pipeline | Fase 1 §2 | P1 |

---

## Prossimo passo operativo

Le attività P0 hanno una dipendenza lineare: (1) prima il job CI di build/push/aggiornamento
manifest (attività 1-2), verificato manualmente con un `docker build` locale come già fatto in Fase
1 — senza un'immagine pubblicata non c'è nulla per ArgoCD da sincronizzare; (2) poi l'installazione
di ArgoCD e la sua `Application` puntata su `deploy/` (attività 3-4), con sync automatico limitato al
solo cluster `kind` di staging fin dal primo giorno (attività 5, verificata come criterio di
accettazione, non come nota a margine). L'attività 7 (KEDA) resta esplicitamente **non ancora
attuabile**: richiede prima che almeno uno dei tool CLI di Fase 0 §1.3 venga effettivamente
containerizzato come `Job`/`CronJob` K8s, passo che nessuna fase precedente ha ancora eseguito — va
segnalata come dipendenza aperta, non pianificata in dettaglio finché quel prerequisito non è
soddisfatto.
