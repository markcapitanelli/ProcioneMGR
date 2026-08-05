# PRD — Prestazioni e risorse (Filone H, 2026-08-05)

*Nato dalla richiesta del proprietario: «ottimizzare tutti i processi della piattaforma e il suo
utilizzo di risorse … assicurati di non peggiorare le prestazioni, piuttosto migliorarle, ma senza
causare danni». Il vincolo è quindi doppio: guadagnare, e non rompere.*

## 0. Metodo

Prima misurare, poi toccare — e ogni intervento porta con sé il numero prima/dopo che lo
giustifica e il modo di tornare indietro. È lo stesso metodo di F1-F3, che sono le uniche
ottimizzazioni di questo progetto ad aver retto: quelle nate da un profilo, non da un'intuizione.

**Non-obiettivi**: riscritture architetturali; cambi di schema del database (la chiave primaria di
`OhlcvData` pesa 309 MB ed è quasi inutile, ma toccarla è un rischio senza un guadagno
proporzionato); ottimizzazioni che risparmiano memoria dove la memoria non è il collo.

## 1. La fotografia (misurata il 2026-08-05)

### Macchina

| Cosa | Numero |
|---|---|
| RAM totale / libera | 7.835 MB / **317 MB (4%)** |
| VM WSL2 (Docker) | 1.372 MB, **senza tetto** (`.wslconfig` assente) |
| Nodo kind (container) | **2,35 GiB** su 3,65 GiB, 39% CPU |
| Guscio ProcioneMGR | **111 MB** |
| Postgres (30 processi) | 294 MB |
| Disco | NVMe SSD |

**Il guscio non è il problema**: 111 MB su 7,8 GB. La pressione viene dal cluster.

### Il cluster sta girando a vuoto

| Pod | Riavvii |
|---|---|
| `kube-scheduler` | **418** |
| `kube-controller-manager` | **413** |
| `argocd-repo-server` | 94 |
| `argocd-server` | 84 |
| `calico-kube-controllers` | 44 |

Causa, dai log dello scheduler: `Failed to renew lease … context deadline exceeded` — non
raggiunge l'API server entro 5 secondi, perde il lease e esce. **Non è un guasto: è fame di
risorse.** Ogni riavvio costa CPU e memoria, che rendono il prossimo riavvio più probabile.
Sul cluster girano **7 pod ArgoCD** per un cluster locale mono-nodo di un solo sviluppatore, più
un pod `e2e-smoke-probe-3823` in `Error` da 7 giorni.

### Database

| Cosa | Numero |
|---|---|
| `OhlcvData` | 2.641 MB (1.445 dati + **1.195 indici**), 12,18M righe |
| Scansioni sequenziali su `OhlcvData` | 159, **4.236.269 righe ciascuna** |
| Cache hit ratio | **70,08%** (sano: >99%) |
| `shared_buffers` | **128 MB** (default) per una tabella da 2,6 GB |
| `random_page_cost` | **4** (valore da disco rotante) su **SSD NVMe** |
| `effective_cache_size` | 4 GB (default, irrealistico qui) |
| `work_mem` | 4 MB (default) |
| Connessioni | 20 idle + 1 attiva |
| Indici mai usati | 12, ma tutti ≤40 kB — irrilevanti |

**La misura che decide il primo intervento** — stessa query, stessi dati, solo il parametro cambiato:

| `random_page_cost` | Piano | Tempo di esecuzione |
|---|---|---|
| 4 (attuale) | Bitmap Heap Scan | **92,3 ms** |
| 1,1 (da SSD) | Index Scan puro | **6,0 ms** |

Quindici volte, senza toccare una riga di codice. Il `4` dice al planner che una lettura casuale
costa quattro volte una sequenziale: vero su un disco a piatti, falso su NVMe. Da lì la preferenza
per le scansioni sequenziali, e da lì i 673 milioni di righe lette in sequenza.

**Onestà sui limiti**: la stessa prova su una query di aggregazione su tutta la tabella non cambia
piano né tempo (6,5 s in entrambi i casi). `random_page_cost` aiuta le letture per intervallo e per
chiave — che sono la stragrande maggioranza di quelle dell'app — non i pieni-tabella.

## 2. Gli interventi, in ordine di valore su rischio

| # | Cosa | Guadagno atteso | Rischio | Ritorno indietro |
|---|---|---|---|---|
| **H1** | `random_page_cost` 4 → 1.1 sul database (SSD) | ~15× sulle letture per intervallo, misurato | molto basso: cambia solo la stima dei costi del planner | `ALTER DATABASE … RESET random_page_cost` |
| **H2** | ArgoCD scalato a 0 quando non si deploya (7 pod) | il maggior rilascio di RAM e di carico sull'API server; probabile fine del crash-loop del control plane | basso, ma tocca il meccanismo di deploy: va detto | `kubectl scale --replicas=1` sui deployment |
| **H3** | Tetto alla VM WSL2 (`.wslconfig`) | impedisce a Docker di affamare Windows | basso, ma **richiede `wsl --shutdown`**, che ferma il motore di trading | cancellare il file |
| **H4** | Memoria di Postgres (`shared_buffers`, `effective_cache_size`, `work_mem`) | cache hit dal 70% verso il 99% | **medio se fatto ORA**: con 317 MB liberi, alzare i buffer peggiora | solo DOPO H2/H3, e rimisurando |
| **H5** | Pulizia: pod morto da 7 giorni, ConfigMap orfani | marginale in RAM, zero rumore in meno nei pannelli | nullo | — |
| **H6** | Cadenza dei 17 worker del guscio: cercare lavoro inutile | piccolo sul guscio (111 MB), ma ogni tick inutile è carico sul database | basso | config |

**L'ordine non è negoziabile**: H4 prima di H2/H3 significherebbe aggiungere consumo a una macchina
che ha il 4% di RAM libera — cioè fare esattamente il danno che si vuole evitare.

## 2bis. Esito degli interventi

### H1 — `random_page_cost` 4 → 1,1 · **FATTO**

Applicato con `ALTER DATABASE procionemgr SET` (non `ALTER SYSTEM`): resta circoscritto a questo
database e si annulla con un `RESET`. Aggiunto anche `effective_io_concurrency = 200`, sensato su
NVMe.

**Verifica su connessione NUOVA, senza `SET` manuali** — cioè quello che vedrà l'app:

| Query | Prima | Dopo |
|---|---|---|
| Candele BTC/USDT 1h su 7 mesi | Bitmap Heap Scan, 92,3 ms | **Index Scan, 25,4 ms** |
| Conteggio ETH/USDT 4h dal 2025 | — | **Index Only Scan** (non tocca la tabella) |

Il guadagno durevole non è il millisecondo — è il cambio di PIANO: da «leggi mezza tabella e poi
filtra» a «segui l'indice». Con la cache calda la stessa query scende a 6 ms.

### H6 — Rumore nei log · **FATTO, con un errore mio da ricordare**

`Microsoft.EntityFrameworkCore.Database.Command` e `System.Net.Http.HttpClient` portati a
`Warning`. Prima ogni query SQL veniva stampata col testo completo (a 37 transazioni/s a riposo:
37 righe/s) e ogni richiesta HTTP produceva quattro righe. Una singola lettura dei log durante il
lavoro ha reso **147.000 caratteri**, quasi tutti SQL: i messaggi che contano erano illeggibili.

**Il guadagno di CPU è piccolo e va detto**: l'app a riposo stava all'1,2% di un core e 100 MB —
era già magra. Il valore vero è l'osservabilità, non i cicli.

**L'errore**: ho messo un commento `_comment_ef` DENTRO `Logging:LogLevel`. Lì ogni chiave deve
essere un livello di log valido, quindi il commento è stato letto come una regola e **l'app non è
ripartita** (`Configuration value '…' is not supported`). Trovato in un minuto perché il riavvio
falliva subito; il commento va accanto a `LogLevel`, dentro `Logging` — che è esattamente dove lo
tiene `appsettings.Development.json`, e adesso so perché.

### H2, H3, H4, H5 · **NON eseguiti**

- **H2** (ArgoCD a 0, pod morto): il comando è stato **bloccato dal classifier** come operazione
  infrastrutturale distruttiva. Non è stato forzato: serve un via libera esplicito del
  proprietario. È l'intervento col guadagno maggiore ancora sul tavolo.
- **H3** (`.wslconfig`): richiede `wsl --shutdown`, che ferma il motore di trading. Va fatto in un
  momento scelto, non di sorpresa.
- **H4** (memoria di Postgres): resta gated su H2/H3 per costruzione — alzarla ora, col 4% di RAM
  libera, sarebbe il danno che questa roadmap vuole evitare.
- **H5**: la pulizia del pod morto è dentro il blocco di H2.

## 3. Criterio di accettazione comune

Ogni intervento è accettato solo se, dopo, valgono tutte e tre:
1. il numero che lo motivava è migliorato, misurato allo stesso modo di prima;
2. la suite dei test resta verde;
3. la piattaforma nel browser fa quello che faceva prima — corsie vive, pagine che rendono,
   nessun errore nei log.

Se una delle tre non vale, si torna indietro e si scrive perché.
