# ProcioneMGR — istruzioni per Claude Code

## Memoria primaria: il notebook NotebookLM

Questo progetto ha una memoria esterna già costruita e mantenuta. **Interrogala prima di esplorare
il codice.**

```
notebook_id : aea57009-dfdf-4152-86a8-c843c45c5e10
titolo      : ProcioneMGR
url         : https://notebooklm.google.com/notebook/aea57009-dfdf-4152-86a8-c843c45c5e10
```

Strumento: `mcp__notebooklm__notebook_query(notebook_id, query)`.
Per una serie di domande collegate, passa `conversation_id` alla successiva.

### Perché

La codebase è grande — 414 file C#, 89 pagine `.razor`, 384 servizi in 38 cartelle. Ricostruire il
contesto leggendo i file costa decine di migliaia di token **ogni sessione**, e produce una
comprensione peggiore di quella già scritta nel notebook, che contiene anche cose non deducibili dal
codice: misure, decisioni con la loro motivazione, esiti negativi, trappole già pagate.

### Protocollo

1. **Prima domanda al notebook, sempre**, quando ti serve capire architettura, scelte, convenzioni,
   rischi noti, dove sta una cosa, o perché una cosa è fatta così.
2. **Poi apri i file**, ma solo quelli che il notebook ti ha indicato, e solo se devi *modificarli*
   o verificare un dettaglio che la memoria non copre.
3. **Non ri-derivare** ciò che il notebook già dice. Se hai la risposta, agisci.
4. **Non fidarti della memoria per lo stato attuale del codice.** Il notebook è una fotografia del
   **2026-08-04**: nomi di file, righe e numeri possono essere invecchiati. Per la struttura e il
   *perché* fidati; prima di citare una riga o un flag come fatto attuale, verifica nel file.
5. **Quando scopri qualcosa di nuovo e duraturo** — una decisione, una misura, una trappola —
   aggiorna `docs/audit/` e ricarica il documento nel notebook. La memoria si mantiene, altrimenti
   invecchia e smette di far risparmiare.

### Domande a cui il notebook risponde bene

Architettura e pattern · confine di sicurezza verso Live · perché un flag è spento · dove vive un
modulo · rotte e protezioni · integrazioni esterne e loro stato · rischi aperti con priorità ·
convenzioni di codice · errori comuni · glossario del dominio · decisioni architetturali con
motivazione.

### Quando NON basta

Lo stato runtime (usa l'app su `http://localhost:5199`) · il contenuto esatto di un file che stai per
modificare (leggilo) · qualsiasi cosa introdotta dopo il 2026-08-04.

---

## Documentazione in repo

| Percorso | Cosa |
|---|---|
| `docs/audit/00_INDEX.md` | indice dell'audit completo, 14 documenti |
| `docs/audit/10_CLAUDE_CODE_MEMORY.md` | **la versione lunga di questo file**: convenzioni, cose da non rompere, errori comuni |
| `docs/audit/09_RISKS_AND_TECH_DEBT.md` | rischi aperti con priorità |
| `docs/ROADMAP.md` | roadmap viva |
| `docs/STANDARD-VERIFICA.md` | i 4 livelli di verifica obbligatori per ogni fase |
| `docs/pagine/<slug-route>.md` | un documento per pagina UI |

---

## Comandi

```bash
dotnet run --project ProcioneMGR --no-launch-profile -c Release
```

```bash
dotnet test
```

```bash
dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR
```

L'app gira su `http://localhost:5199`. `dotnet test` richiede **Docker attivo** (Testcontainers).

> Lo script ufficiale `./scripts/run-postgres.ps1` aggiunge i port-forward verso il cluster kind, ma
> **muore se il cluster è giù** (`$ErrorActionPreference="Stop"` + stderr di `kubectl`). Finché non
> è corretto, usa `dotnet run` diretto.

---

## Le sette regole da non violare

1. **`SafetyChecker` resta statico e puro.** Non è mockabile né iniettabile: è il punto.
2. **Un solo scrittore.** Mai due motori vivi, mai due scrittori sulla stessa serie OHLCV.
3. **Verso Live nessun percorso automatico.** L'unica automazione ammessa che tocca una corsia Live
   è la retrocessione a Testnet, opt-in e in dry-run.
4. **Fail-closed sulla sicurezza, fail-open sulla diagnostica.** Due politiche diverse, deliberate.
5. **Degradare dicendolo.** Mai mostrare un valore vecchio come se fosse attuale.
6. **Nessun servizio di esecuzione dentro `Services/Llm/`.** Il layer AI dà pareri e veti, non esegue.
7. **`DriveProtectiveExits = false` e `RegimeRouting:DriveDecisions = false` non sono sviste**, sono
   risultati di misure. Non "correggerli".

## Convenzioni

- **Italiano** per commenti, log e messaggi UI; nomi di tipi e membri in inglese.
- I commenti spiegano il **perché**, spesso con data e riferimento al report che ha motivato la
  scelta. Se cambi la decisione, aggiorna il commento.
- `IDbContextFactory`, non il DbContext scoped, nei servizi a vita lunga.
- Page service per l'orchestrazione delle pagine pesanti: la logica non sta nel markup.

## Quando misuri qualcosa

Dichiara sempre **trade/mese e durata mediana della posizione** — l'orizzonte di riferimento è
intraday/swing breve. Nessun risultato senza gate anti-overfitting. Non randomizzare su asset
correlati per stimare la significatività: fabbrica falsa significatività, è un errore già pagato.
