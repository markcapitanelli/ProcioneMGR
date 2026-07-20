# ProcioneMGR — Roadmap Kubernetes, Fase 3: Osservabilità e Monitoraggio Avanzato

**Continuazione di `docs/ROADMAP-K8S-FASE0/1/2.md`** — il Deployment che gira su `kind`
(`procionemgr-staging`, Fase 2) espone già `/healthz/live` e `/healthz/ready` (Fase 1 §3); questa
Fase 3 aggiunge i tre pilastri di osservabilità del PDF (metriche, tracciamento, log) sopra
un'infrastruttura di strumentazione **che in buona parte esiste già**, a differenza di quanto la
Fase 3 del PDF presuppone.

---

## 0. Premessa: quanto del PDF è già fatto, cosa manca davvero

Il PDF tratta i tre pilastri come da costruire da zero. Verificato sul codice, la situazione è
disomogenea — un pilastro è già progettato correttamente e serve solo esporlo, uno manca al 100%, e
uno emerge da una scoperta non prevista (un conflitto di path):

| Pilastro | Cosa dice il PDF | Stato reale verificato |
|---|---|---|
| Metriche | Strumentare da zero con `prometheus-net` o OpenTelemetry | **Già strumentato**: `Services/Observability/ProcioneMetrics.cs` — un `Meter` BCL (`System.Diagnostics.Metrics`, `MeterName = "ProcioneMGR"`) con 7 strumenti di dominio (`procione.lane.promotions`, `procione.drift.alerts`, `procione.models.retired`, `procione.pipeline.runs`, `procione.trades.executed`, `procione.execution.jobs`, `procione.execution.slippage_bps`). Export OTLP **già cablato** ma opt-in (`Program.cs:257-268`, flag `Observability:Enabled`, default off) — manca solo un endpoint **in formato Prometheus** da far scrapare dal cluster |
| Tracciamento distribuito | Da aggiungere con `OpenTelemetry.Instrumentation.AspNetCore` | **Assente al 100%** — `ProcioneMGR.csproj` referenzia solo `OpenTelemetry.Extensions.Hosting` e `OpenTelemetry.Exporter.OpenTelemetryProtocol` (usati oggi solo per le metriche); nessun pacchetto di instrumentation, nessuna chiamata `.WithTracing()` in `Program.cs`. Qui il PDF ha ragione senza correzioni |
| Log centralizzato | Log strutturati JSON su stdout | **Assente**: nessun Serilog, nessun formatter JSON configurato — solo il provider console di default (`Microsoft.Extensions.Logging`), testo semplice |

**La scoperta che il PDF non poteva fare**: esiste già una pagina `/metrics` (`Components/Pages/
Metrics.razor:1`, `@page "/metrics"`), dashboard Blazor per Admin/Manager che legge
`MetricsCollector` (`Services/Observability/MetricsCollector.cs`) — un `MeterListener` in-processo
che accumula gli stessi 7 contatori **senza bisogno di un backend OpenTelemetry**. Il proprio
commento nel file lo dichiara esplicitamente: *"i totali sono dalla partenza del processo: si
azzerano a un riavvio"* — e la sua stessa `GuidaPanel` nella UI lo conferma all'utente finale. Con la
strategia `Recreate` decisa in Fase 2 §1 (ogni deploy termina e ricrea l'unico pod), questi contatori
si azzereranno **a ogni deploy**, non solo occasionalmente — un limite che oggi (processo persistente
su una macchina) è raro, ma diventa strutturale una volta su Kubernetes. Due conseguenze pratiche per
questa fase: (1) il path Prometheus standard `/metrics` **non è disponibile**, va scelto un path
diverso; (2) Prometheus (storico, sopravvive ai riavvii) diventa il complemento naturale — non un
sostituto imposto — della dashboard esistente, che resta utile come vista "a caldo, senza dipendenze"
per un rapido controllo manuale.

---

## 1. Metriche: endpoint Prometheus accanto al Meter esistente

**Nessuna modifica a `ProcioneMetrics.cs`** — il Meter e i 7 strumenti restano identici, sono già
nella forma corretta per essere esposti. Aggiunta minima in `Program.cs`, nello stesso blocco opt-in
esistente (righe 257-268):

- Nuovo pacchetto `OpenTelemetry.Exporter.Prometheus.AspNetCore`.
- Nel builder `.WithMetrics(m => ...)` già presente, aggiungere `m.AddPrometheusExporter()` accanto
  a `m.AddOtlpExporter(...)` (i due exporter coesistono sullo stesso `Meter` — nessun conflitto,
  l'SDK OpenTelemetry supporta più export target contemporaneamente).
- Mappare l'endpoint su un path **diverso da `/metrics`** per evitare la collisione con
  `Metrics.razor` — proposto `/metrics/prometheus` (`app.MapPrometheusScrapingEndpoint("/metrics/prometheus")`).

**Conversione nomi**: l'exporter Prometheus di OpenTelemetry traduce automaticamente i punti in
underscore e aggiunge suffissi di unità/tipo (es. `procione.trades.executed` → contatore
`procione_trades_executed_total` in formato testo Prometheus) — nessuna modifica ai nomi già scelti
in `ProcioneMetrics.cs`, la traduzione è automatica.

**Nel cluster** (Sezione 5): un `ServiceMonitor` (se si installa la variante con Prometheus Operator,
raccomandata in Sezione 5) che punta al `Service` già definito in Fase 2 §2, sulla porta HTTP
esistente, path `/metrics/prometheus`.

**Nota a margine, in ambito di questa fase perché è la sua stessa area**: la sezione
`Observability` (`Enabled`, `OtlpEndpoint`) **non è documentata in `appsettings.json.example`**
(verificato: nessuna occorrenza) — a differenza di ogni altra sezione di configurazione del
template. Va aggiunta come parte di questa fase, non è un problema a sé: è la stessa area di codice
che questa fase sta completando.

---

## 2. Tracciamento distribuito: instrumentazione da zero (il PDF qui è corretto)

Nessuna correzione necessaria al principio del PDF — va solo eseguito. Nuovi pacchetti:
`OpenTelemetry.Instrumentation.AspNetCore` (traccia ogni richiesta HTTP in ingresso automaticamente),
`OpenTelemetry.Instrumentation.Http` (chiamate HTTP in uscita verso Binance/Bitget — utile per capire
dove va il tempo in una chiamata exchange lenta), e per Postgres uno strumentatore Npgsql
compatibile con OpenTelemetry (traccia le query EF Core). Wiring: stesso blocco opt-in di
`Observability:Enabled` (Sezione 1), aggiungendo `.WithTracing(t => t.AddAspNetCoreInstrumentation()
.AddHttpClientInstrumentation().AddNpgsql().AddOtlpExporter(...))` — riuso deliberato del pattern
già esistente ("opt-in, costo ~0 se spento", commento a `Program.cs:254-256`), non un meccanismo
parallelo.

**Valore specifico per questo sistema, oltre l'auto-instrumentation generica del PDF**:
`PipelineSchedulerWorker` esegue una pipeline a 15 stadi (Fase 0 §5) interamente in-process — nessuna
chiamata di rete tra stadi da tracciare automaticamente, ma è esattamente il tipo di flusso interno
dove un operatore vuole sapere "quale stadio ha impiegato di più" in un run lento. `System.Diagnostics.
ActivitySource` (stessa famiglia BCL di `System.Diagnostics.Metrics` già in uso in `ProcioneMetrics.cs`
— nessuna nuova dipendenza per crearlo) può wrappare ogni stadio con uno span manuale, dando in Tempo
una vista a cascata dei 15 stadi di un singolo run, anche senza microservizi reali. Non implementato
in questa fase (fuori scope per un documento di sola pianificazione), ma è la prima estensione
naturale da fare quando si passerà all'esecuzione.

**Destinazione export**: **Grafana Tempo**, non Jaeger né Azure Application Insights — coerente con
la decisione già presa in Fase 0 §6 di non impegnarsi su un provider cloud, e perché condivide la
stessa istanza Grafana usata per le metriche (Sezione 5), evitando una terza UI separata.

---

## 3. Log centralizzato: JSON su stdout, senza nuove dipendenze

Verificato in Fase 1: nessun Serilog nel progetto, solo il provider console di default. Il PDF
implica (senza dirlo esplicitamente) che serva una libreria come Serilog per ottenere log JSON — **non
è così per questo stack**: `Microsoft.Extensions.Logging.Console`, già parte dell'SDK .NET (nessun
pacchetto NuGet aggiuntivo), supporta nativamente un formatter JSON
(`options.FormatterName = ConsoleFormatterNames.Json` o l'equivalente `AddJsonConsole()`). Cambiare
il formatter da testo semplice a JSON è una modifica di configurazione minima in `Program.cs`, zero
nuove dipendenze — coerente con la disciplina "C# puro, nessuna dipendenza pesante senza decisione
esplicita" già seguita nel resto del progetto (`docs/ROADMAP-QLIB.md` §4).

Kubernetes cattura stdout/stderr del container automaticamente (comportamento nativo, nessuna
configurazione applicativa aggiuntiva) — il solo cambio di formatter è sufficiente perché un
aggregatore esterno (Sezione 5) possa poi fare query per livello/categoria/eccezione invece di
regex su testo libero.

---

## 4. Cosa NON cambia: `Metrics.razor`/`MetricsCollector` restano

Questa fase **non rimuove** la dashboard esistente — è fuori scope per un documento di sola
pianificazione, e comunque resta utile per un controllo rapido senza dover aprire Grafana. Va solo
tenuta presente la sua limitazione ormai precisa (contatori azzerati a ogni `Recreate`, Sezione 0)
quando in futuro si deciderà se mantenerla, ridurla a link verso Grafana, o ritirarla — decisione da
prendere con dati reali di utilizzo, non ora.

---

## 5. Cosa aggiungere al cluster `kind` per questa fase

Prima volta in questa serie di documenti in cui il cluster locale (Fase 0 §3) riceve componenti
oltre al bare minimum — Fase 0/1/2 avevano deliberatamente evitato Ingress/cert-manager/secret store
perché non c'era ancora nulla da testare con essi. Qui c'è:

- **`kube-prometheus-stack`** (Helm chart, include Prometheus Operator + Prometheus + Grafana +
  Alertmanager in un solo install) in un namespace dedicato `observability` (separato da
  `procionemgr-staging`, Fase 0 §3 — la piattaforma applicativa e lo stack di osservabilità hanno
  cicli di vita diversi). Scelto invece di un Prometheus standalone perché porta Grafana "gratis",
  necessaria comunque per il passo successivo.
- **Grafana Tempo** (Helm chart `grafana/tempo`, storage locale sufficiente per uno staging
  solo-dev) — riceve le tracce OTLP di Sezione 2.
- **Grafana Loki + Promtail** (Helm chart `grafana/loki-stack`) — Promtail gira come `DaemonSet`,
  legge stdout/stderr di ogni pod (incluso quello di ProcioneMGR, Sezione 3) e lo inoltra a Loki.
- **`ServiceMonitor`** (CRD del Prometheus Operator) che punta al `Service` di Fase 2 §2, path
  `/metrics/prometheus` (Sezione 1).

Questi tre componenti condividono la stessa istanza Grafana (pattern "Grafana LGTM": Loki, Grafana,
Tempo, Metrics/Prometheus) — un'unica UI per tutti e tre i pilastri, invece di Prometheus+Grafana
separati da Kibana (ELK) come alternativa più pesante citata dal PDF. Per uno staging locale
solo-dev è la scelta con meno footprint operativo a parità di copertura.

---

## 6. Verifica

1. Deploy dello stack di Sezione 5 nel namespace `observability`.
2. Attiva `Observability:Enabled=true` nel `Deployment` di ProcioneMGR (Fase 2 §3.1 — sezione letta
   una sola volta all'avvio, quindi richiede un riavvio del pod, non hot-reload).
3. Conferma in Prometheus/Grafana che il target ProcioneMGR risulti `UP` e che almeno un contatore
   (es. `procione_pipeline_runs_total` dopo un run di test del `PipelineSchedulerWorker`) sia
   interrogabile.
4. Conferma in Tempo che una richiesta HTTP verso l'app produca una traccia visibile (auto-
   instrumentation ASP.NET Core, Sezione 2).
5. Conferma in Loki che i log del pod siano interrogabili in formato JSON per campo (es. filtro per
   `Level=Error`).
6. Riesecuzione della suite Playwright di Fase 0 §4 — nessuna delle modifiche di questa fase tocca
   comportamento applicativo, solo telemetria in uscita, quindi deve restare verde identica.

---

## Tabella riassuntiva: attività Fase 3

| # | Attività | Richiede codice? | Priorità |
|---|---|---|---|
| 1 | Endpoint Prometheus (`/metrics/prometheus`, no `/metrics`) | Sì (1 pacchetto + poche righe in `Program.cs`) | P0 |
| 2 | Documentare `Observability:*` in `appsettings.json.example` | Sì (solo template, nessuna logica) | P1 |
| 3 | Tracing OpenTelemetry (AspNetCore + Http + Npgsql) | Sì (2-3 pacchetti + estensione blocco opt-in esistente) | P1 |
| 4 | Log JSON su stdout (formatter nativo, zero nuove dipendenze) | Sì (config in `Program.cs`) | P0 |
| 5 | `kube-prometheus-stack` + Tempo + Loki su `kind` | No (solo Helm/manifest) | P0 |
| 6 | `ServiceMonitor` verso il Service di Fase 2 | No (solo manifest) | P0 |
| 7 | Verifica end-to-end (Sezione 6) | No | P0 |

---

## Prossimo passo operativo

Ordine consigliato: (1) endpoint Prometheus + log JSON (attività 1 e 4) sono le più economiche e
indipendenti, vanno per prime; (2) stack Grafana/Prometheus/Tempo/Loki su `kind` (attività 5-6) può
procedere in parallelo, non dipende dal codice applicativo finché non serve verificare lo scraping
reale; (3) tracing (attività 3) per ultimo — è l'unica attività di questa fase con più di una riga di
modifica a `Program.cs` e beneficia di avere già Tempo pronto per la verifica immediata. La
documentazione del template (attività 2) è a costo quasi zero e può essere fatta insieme
all'attività 1, stessa area di codice.
