# Observability locale (Fase 0) — Grafana + Loki + Prometheus + OTel Collector

Stack di logging e monitoring centralizzato per la migrazione a microservizi. Scelto al posto di
ELK perché: binari Go leggeri (parte in secondi su Docker Desktop), copre sia metriche che log,
e si integra nativamente con l'exporter OTLP già referenziato dall'app (`OpenTelemetry.Exporter.OpenTelemetryProtocol`)
— zero pacchetti NuGet aggiuntivi. Il tracing distribuito (Tempo) arriverà con le chiamate gRPC
tra servizi (Fase 2+).

## Avvio / arresto

```powershell
.\scripts\observability-up.ps1     # docker compose up -d
.\scripts\observability-down.ps1   # docker compose down (con -Purge rimuove anche i volumi)
```

## Collegare l'app

L'export è **opt-in** dietro il flag `Observability:Enabled` (default OFF, zero impatto).
In `appsettings.json` locale (o via env):

```json
"Observability": { "Enabled": true, "OtlpEndpoint": "http://localhost:4317" }
```

oppure `$env:Observability__Enabled = "true"` prima di lanciare l'app.

Con il flag ON l'app esporta:
- **metriche** del meter `ProcioneMGR` (contatori `procione.*`) → Prometheus
- **log** `ILogger` applicativi → Loki

La dashboard in-app `/metrics` continua a funzionare in ogni caso (legge i contatori in-process).

## URL

| Servizio | URL | Note |
|---|---|---|
| Grafana | http://localhost:3000 | admin / procione-local (solo locale) |
| Prometheus | http://localhost:9090 | query dirette |
| OTLP gRPC | localhost:4317 | endpoint per l'app |
| OTLP HTTP | localhost:4318 | alternativa |

I datasource Prometheus e Loki sono auto-provisionati in Grafana (Explore → scegli datasource).

## Verifica rapida

1. `.\scripts\observability-up.ps1` → 4 container up.
2. Avvia l'app con `Observability__Enabled=true`, genera attività (es. un tick Paper).
3. Grafana → Explore → Prometheus → cerca `procione_` (i punti diventano underscore).
4. Grafana → Explore → Loki → query `{service_name=~".+"}` per i log applicativi.
