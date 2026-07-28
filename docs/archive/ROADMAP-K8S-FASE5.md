# ProcioneMGR — Roadmap Kubernetes, Fase 5: Frontend e Integrazione Finale

**Continuazione di `docs/ROADMAP-K8S-FASE0/1/2/3/4.md`** — chiude la struttura a 6 fasi del PDF
generico. È la fase in cui lo scarto tra il PDF e la realtà di ProcioneMGR è più ampio: il PDF
descrive la costruzione di un frontend React separato con autenticazione JWT; niente di tutto ciò
si applica qui, e questo documento lo dice fin dall'inizio invece di forzare un adattamento
artificiale. Il contenuto reale di questa fase è diverso: l'ultimo pezzo di raggiungibilità esterna
rimasto deliberatamente in sospeso dalla Fase 0, e la validazione end-to-end di tutto ciò che le
cinque fasi precedenti hanno progettato.

---

## 0. Premessa: perché non c'è un frontend da costruire

Non è una scoperta nuova di questa fase — è la conclusione di evidenze già raccolte e citate in ogni
documento precedente, qui solo riepilogate:

| Assunzione del PDF (Fase 5) | Evidenza già raccolta | Dove |
|---|---|---|
| Frontend React separato, SPA statica | 93 file `.razor`, nessun `package.json`, nessun frontend pianificato o esistente — l'app **è** Blazor Server | Fase 0 §0 |
| Comunicazione via API RESTful | Superficie REST oggi: solo gli endpoint Minimal API interni dello scaffolding Identity (`/Account/*`), nessun consumer esterno | Fase 1 §4 |
| Auth JWT access+refresh token, `HttpOnly` cookie | Cookie auth ASP.NET Identity già in uso; introdurre JWT ora sarebbe infrastruttura senza consumer | Fase 1 §5 |
| `ProtectedRoute` React, stato globale via Context/Redux | Blazor Server gestisce autenticazione (`AuthorizeView`, `[Authorize]`) e stato (DI scoped, campi C#) nativamente, già in uso in tutte le 26+ pagine esistenti | Fase 0 §0, Fase 2 §3 |

**Non è nemmeno un caso limite ambiguo**: costruire un frontend React ora significherebbe duplicare
un'interfaccia già esistente e funzionante, in violazione diretta della disciplina "niente
infrastruttura speculativa senza un consumer reale" già applicata in Fase 1 §4/§5 e radicata nel
progetto stesso (`docs/ROADMAP-QLIB.md` §4, "additività architetturale"). **Quando** questo cambierà:
solo se nascerà un consumer headless reale (app mobile, script di automazione esterno, integrazione
di terze parti) — lo stesso trigger già identificato in Fase 1 §5 per JWT, non ripetuto qui.

Il contenuto utile della Fase 5, per questo sistema, è quindi altro: **raggiungibilità esterna** (mai
affrontata fin qui — Fase 0 §3 e Fase 2 §2 si sono fermate a `ClusterIP`/porta interna) e
**validazione end-to-end** di tutta la catena costruita da Fase 1 a Fase 4.

---

## 1. Ingress + TLS: l'ultimo pezzo deliberatamente rimandato

Fase 0 §3 aveva esplicitamente escluso Ingress/cert-manager ("non c'è ancora nulla da distribuire");
Fase 2 §2 si è fermata a un `Service` `ClusterIP` (raggiungibile solo dentro al cluster o via
`kubectl port-forward`). Con Fase 4 che automatizza il deploy, questo è il momento naturale per
chiudere il cerchio: rendere l'app raggiungibile da un browser reale, non solo da `kubectl`.

**Controller**: `ingress-nginx` — scelta standard per `kind`, con un manifesto di installazione
specifico per questa distribuzione (a differenza di un cluster cloud, `kind` richiede una
configurazione di `extraPortMappings` nel file di config del cluster, coerente con quanto già
previsto in Fase 0 §3 "file di configurazione kind minimale").

**Attenzione specifica a Blazor Server, che il PDF non poteva prevedere (pensava a una SPA React
stateless)**: il circuito Blazor Server usa **WebSocket** (SignalR) per la comunicazione persistente
UI↔server. `ingress-nginx` inoltra correttamente gli header `Upgrade`/`Connection` di default, ma i
timeout di proxy default (`proxy-read-timeout`/`proxy-send-timeout`, tipicamente 60s) sono pensati
per richieste HTTP request/response brevi, non per una connessione persistente che dura quanto la
sessione dell'utente — un timeout troppo basso interromperebbe il circuito a metà sessione anche con
l'utente ancora attivo. Va alzato esplicitamente via annotazioni sull'`Ingress`
(`nginx.ingress.kubernetes.io/proxy-read-timeout`, `proxy-send-timeout` a un valore nell'ordine delle
ore, non dei secondi).

**Session affinity**: **non serve** — non perché il problema identificato in Fase 0 §1.2 (circuito
SignalR pinnato a un'istanza server) sia sparito, ma perché `replicas: 1` (Fase 2 §1) lo rende
strutturalmente non applicabile: non esiste un secondo pod a cui l'Ingress potrebbe instradare per
errore. Stessa conclusione già raggiunta in Fase 2 §2 per il `Service`, qui riconfermata per
l'`Ingress` — nessuna nuova configurazione di affinity da aggiungere.

**TLS**: `cert-manager` (Helm chart, stesso pattern di installazione degli add-on già usato in Fase 3
§5 per `kube-prometheus-stack`/Tempo/Loki) con un `ClusterIssuer` **self-signed** per il cluster
`kind` locale — non ha senso richiedere un certificato ACME/Let's Encrypt reale senza un dominio
pubblico, e la scelta della piattaforma/dominio di produzione resta esplicitamente differita (Fase 0
§6). Il browser segnalerà il certificato self-signed come non attendibile durante i test su `kind` —
comportamento atteso, non un difetto da correggere ora.

**Nessuna modifica di codice necessaria**: `AllowedHosts: "*"` (`appsettings.json.example:88`) è già
permissivo verso qualunque hostname; `app.UseHsts()` (`Program.cs:433`, branch non-Development) e
`app.UseHttpsRedirection()` (`Program.cs:436`) sono già presenti e coerenti con l'arrivo di un
Ingress TLS-terminated — l'app si aspettava già HTTPS in produzione, semplicemente non è mai stata
testata dietro un reverse proxy reale fino a questa fase.

---

## 2. Ottimizzazioni statiche: deliberatamente rimandate, non ignorate

`app.MapStaticAssets()` (`Program.cs:440`) serve già gli asset statici di Blazor (CSS/JS/immagini)
con fingerprinting e compressione integrati nel framework. Un CDN davanti a questi asset (S3/Azure
Blob, come suggerirebbe il PDF per il bundle React) porterebbe un beneficio reale solo a scala
multi-utente/multi-region che questo progetto non ha oggi — e richiederebbe comunque la scelta di un
provider cloud, decisione già esplicitamente differita in Fase 0 §6. Non è nella lista delle attività
di questa fase, per lo stesso criterio di disciplina già applicato a JWT e Minimal API.

---

## 3. Validazione end-to-end: la vera "integrazione finale"

Con Ingress+TLS in piedi, questo è il primo momento in cui l'intera catena costruita da Fase 1 a
Fase 4 può essere esercitata **come la userebbe davvero un operatore** — da un browser, attraverso
un URL reale, non tramite `kubectl port-forward` o chiamate dirette al `Service`. Questo è il vero
contenuto di "integrazione finale" per ProcioneMGR, al posto del collegamento frontend↔API del PDF:

1. **Accesso reale**: apri l'URL Ingress (es. `https://procionemgr.kind.local`, con la relativa voce
   in `/etc/hosts` per la risoluzione locale su `kind`) da browser, accetta il certificato
   self-signed (Sezione 1), effettua login.
2. **Riesecuzione della suite Playwright di Fase 0 §4**, questa volta puntata sull'URL Ingress
   invece che sul processo `dotnet run` locale o sul `Service` interno — è il primo test che passa
   davvero attraverso il salto WebSocket dell'Ingress (Sezione 1), non solo attraverso il `Deployment`
   diretto come nelle verifiche di Fase 1 §6/Fase 2 §5. Se il circuito Blazor si comporta in modo
   anomalo (disconnessioni premature, "Reconnecting..." nella UI) è qui che emergerebbe un timeout
   di proxy troppo basso (Sezione 1).
3. **Persistenza della configurazione attraverso un deploy reale**: ripeti il test già eseguito in
   Fase 2 §5 e Fase 4 §4 (modifica un valore da `/trading`, forza un rollout tramite GitOps — Fase 4
   §2 — e verifica che il valore sopravviva) — questa volta come parte di un'unica sessione utente
   continua attraverso l'Ingress, a chiusura del cerchio invece che come test isolato.
4. **Osservabilità della sessione stessa**: conferma in Grafana (Fase 3 §5) che il traffico generato
   da questa sessione di verifica compaia nelle metriche (`procione_pipeline_runs_total` se hai
   lanciato un run di prova), nelle tracce Tempo (una richiesta HTTP reale, non sintetica) e nei log
   Loki (in formato JSON, Fase 3 §3) — prova che l'intero stack di Fase 3 osserva traffico reale
   attraverso il percorso reale, non solo traffico generato da `curl` durante la verifica isolata di
   quella fase.

Se tutti e quattro i controlli passano, l'intera catena Dockerfile→manifest→GitOps→osservabilità
(Fase 1-4) è verificata end-to-end nel modo in cui verrà effettivamente usata — non fase per fase in
isolamento, come è stato necessariamente fatto finora.

---

## 4. Cosa resta aperto oltre queste sei fasi

Chiusura onesta, non un invito a inventare una Fase 6 non richiesta: diversi punti sono stati
esplicitamente differiti lungo tutta questa serie di documenti, non risolti. Elencarli qui, una sola
volta, invece di lasciarli sparsi:

- **Scelta della piattaforma cloud di produzione** (Fase 0 §6) — mai decisa, correttamente: nessun
  dato da Fase 1-5 la rende urgente, il sistema resta su Bitget Demo/Testnet.
- **Secret store esterno** (Vault/Azure Key Vault, Fase 2 §4) — rimandato insieme alla scelta della
  piattaforma; il `Secret` nativo K8s resta sufficiente finché il cluster è solo `kind` locale.
- **Estrazione Strangler Fig di `MarketDataSyncWorker`** (Fase 0 §1.3) — mai eseguita; è il
  prerequisito sia per un autoscaling HPA/KEDA reale su un servizio genuinamente stateless (Fase 4
  §3), sia per il giorno in cui JWT/Minimal API (Fase 1 §4/§5) smetterebbero di essere differiti.
- **Containerizzazione dei tool CLI come `Job`/`CronJob`** (Fase 0 §1.3) — prerequisito diretto di
  KEDA (Fase 4 §3), non ancora fatta.
- **Profiling risorse reale** (Fase 0 §5) — se non ancora eseguito con `dotnet-counters` o con VPA in
  modalità suggerimento (Fase 4 §3) sul pod reale, i `resources.requests/limits` del `Deployment`
  (Fase 2 §1) restano placeholder da affinare con dati, non con stime.

Nessuno di questi è bloccante per dichiarare completo il percorso descritto dal PDF originale (Fasi
0-5) — sono, semplicemente, le prossime domande legittime una volta che questa base sarà stata
eseguita e non solo pianificata.

---

## Tabella riassuntiva: attività Fase 5

| # | Attività | Dipende da | Priorità |
|---|---|---|---|
| 1 | `ingress-nginx` su `kind` (con `extraPortMappings`) | Fase 0 §3 (config cluster) | P0 |
| 2 | Annotazioni timeout WebSocket sull'`Ingress` | Natura SignalR di Blazor Server (Fase 0 §1.2) | P0 |
| 3 | `cert-manager` + `ClusterIssuer` self-signed | Pattern add-on Helm di Fase 3 §5 | P0 |
| 4 | Verifica end-to-end via browser reale (Sezione 3) | Fase 0 §4, Fase 2 §5, Fase 3 §6, Fase 4 §4 | P0 |
| 5 | Frontend React separato | — | **Non applicabile** (Sezione 0) |
| 6 | Auth JWT | — | **Non applicabile**, già differita in Fase 1 §5 |
| 7 | CDN per asset statici | Scelta piattaforma cloud (Fase 0 §6) | Differita |

---

## Prossimo passo operativo

Ordine consigliato: (1) `ingress-nginx` con la configurazione `extraPortMappings` sul cluster `kind`
esistente (Fase 0 §3), verificato con una richiesta HTTP semplice prima di toccare Blazor; (2)
annotazioni di timeout WebSocket, verificate aprendo l'app da browser e lasciando una sessione
inattiva più a lungo del timeout di default (60s) per confermare che il circuito non cada; (3)
`cert-manager` con issuer self-signed; (4) la validazione end-to-end completa di Sezione 3, che è
anche il criterio di accettazione per dichiarare concluso l'intero percorso Fase 0→5. Nessuna
attività di questa fase richiede la scelta della piattaforma cloud di produzione (Fase 0 §6, ancora
aperta) — tutto qui gira sul cluster `kind` locale, come in ogni fase precedente.
