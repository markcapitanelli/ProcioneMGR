# `procione` — la plancia di comando da console

*Nata il 2026-08-17 su richiesta del proprietario: «un'applicazione master anche da console per
gestire e controllare tutti i processi e gli avvii della piattaforma».*

Un solo comando per **vedere** in che stato è la piattaforma e per **agire** su tutti i suoi pezzi:
Docker, il cluster kind e il suo proxy, i tre servizi in-cluster, i port-forward, il guscio,
Postgres, le automazioni, i backup, ArgoCD, l'observability.

```bash
procione
```

Senza argomenti apre la plancia interattiva: il quadro si aggiorna da solo ogni 12 secondi e i
comandi si danno con un tasto. Con argomenti fa una cosa sola e se ne va, quindi sta anche dentro
uno script.

---

## Le automazioni girano qui dentro

*Aggiunto il 2026-08-23, su richiesta del proprietario: «vedere una finestra PowerShell che si
esegue ogni x minuti aprendosi sopra tutti gli altri lavori».*

Fino a quel giorno la piattaforma si faceva avviare e sorvegliare da **tre meccanismi distinti**,
tutti fuori dalla plancia:

| Meccanismo | Cosa faceva | Cosa si vedeva |
|---|---|---|
| Task «ProcioneMGR Watchdog» | `powershell.exe -File watchdog.ps1`, `PT5M`, `LogonType=Interactive` | una console PowerShell davanti a tutto, **288 volte al giorno** |
| Task «ProcioneMGR Backup DB» | `powershell.exe -File db-backup.ps1`, ogni notte alle 03:30 | idem, una volta a notte |
| `Startup\ProcioneMGR-BringUp.cmd` | `start /min powershell -File bringup.ps1` | una finestra minimizzata a ogni logon |

Il fastidio era la parte visibile. La parte seria è che quegli esiti si potevano leggere **solo**
aprendo il Task Scheduler — ed è esattamente così che il dump notturno è fallito **sei notti di
fila** senza che nessuno se ne accorgesse (2026-08-17): il task usciva `1`, e quel codice non lo
leggeva nessuno.

Adesso c'è **un supervisore dentro questo stesso programma**. Esegue gli **stessi script**, con gli
**stessi argomenti** e la **stessa cadenza** — cambia chi li chiama, e come: l'output è *catturato*
(`CreateNoWindow`), quindi nessuna finestra nasce mai, l'esito finisce nel quadro accanto a tutto il
resto, e c'è un log solo.

```bash
procione attivita migra     # da tre meccanismi a uno. Si fa una volta.
```

### I lavori

| Lavoro | Cosa | Quando | Acceso |
|---|---|---|---|
| `veglia` | `watchdog.ps1` — guscio, motore, Postgres, freschezza dei backup | ogni 5 minuti | sì |
| `backup` | `db-backup.ps1 -KeepDays 14` | ogni giorno alle 03:30 | sì |
| `avvio` | `bringup.ps1` | all'accensione del supervisore | **no** |

`avvio` nasce spento perché dura minuti e tocca cluster e tunnel: non è ciò che ci si aspetta
aprendo una console per guardare uno stato. La migrazione lo **accende** se toglie un bring-up al
logon che c'era già — togliere qualcosa che funzionava senza rimpiazzarlo sarebbe un peggioramento
travestito da pulizia.

```bash
procione servizio                      # accendi il supervisore adesso, qui
procione servizio ferma                # fermalo (serve anche per ricompilare: l'exe è in uso)
procione lavoro                        # cadenza, ultimo esito, prossima scadenza
procione lavoro backup ora             # eseguine uno adesso, fuori cadenza
procione lavoro avvio accendi|spegni   # è una preferenza, sopravvive al riavvio
procione log supervisore -f            # il log che prima non esisteva
```

### Chi lo tiene acceso

Il supervisore vive in tre modi, e sono lo stesso programma:

- **aprendo la plancia** (`procione` senza argomenti) — le automazioni partono con lei;
- **`procione servizio`** — in primo piano, con il log a schermo;
- **al logon**, dall'unica attività pianificata rimasta, che lancia `procione servizio --muto`.

`--muto` nasconde la propria finestra di console appena parte. Non è un vezzo: un'applicazione
console avviata dal Task Scheduler riceve una console **dall'host** e non esiste nessun flag di
avvio che la sopprima — l'unico modo è che il processo la nasconda da sé. Il lampo che resta dura
qualche millisecondo, **una volta al logon**, al posto di una finestra ogni cinque minuti per
sempre.

### Un solo scrittore, anche qui

Due supervisori vivi significherebbero due `pg_dump` nella stessa notte e due watchdog che si
contendono la riparazione del tunnel. L'esclusione è un mutex di sessione: chi non lo ottiene
degrada a **osservatore** — legge lo stato dell'altro e lo mostra, senza duplicare nulla. Aprire la
plancia mentre il supervisore residente gira è quindi sicuro, e il quadro dice `pid N` invece di
«in questa finestra».

Finché un task vecchio esiste *e* il supervisore c'è, il quadro lo segnala come **DOPPIONE**: è la
regola 2 applicata alle automazioni. Ma finché il supervisore *non* c'è, quello stesso task è
l'unica cosa che veglia sulla piattaforma, e viene giudicato sul suo esito — non sgridato.

### La prova di vita è il battito, non il PID

Un PID si riusa, e un processo può essere vivo e piantato. Il supervisore scrive un battito ogni
10 secondi in `%TEMP%\procionemgr-supervisore.json`, e lo **azzera uscendo**: un file lasciato con
l'ultimo battito fresco farebbe credere per un'ora che le automazioni stiano girando. Il battito
azzerato è anche la firma di un'uscita *ordinata*: per questo il quadro distingue «è stato fermato»
da «è morto senza chiudere», invece di mandare a cercare un crash che non c'è stato.

### Quattro cose che la revisione ha corretto prima del merge

Meritano di stare scritte, perché sono tutte casi in cui il codice *sembrava* giusto.

**Il cambio dell'ora.** La prossima occorrenza giornaliera veniva costruita ereditando l'offset
dell'ultima esecuzione. La notte del 25 ottobre l'ultimo backup porta `+02:00`, quindi «il 25 alle
03:30+02:00» è in realtà le **02:30** dell'orologio a muro: il dump parte un'ora prima, viene
registrato con l'offset nuovo, e la scadenza ricade *dentro la stessa notte*. **Due `pg_dump`** —
esattamente ciò che il supervisore esiste per non fare. Ora l'offset si chiede al fuso, e il fuso è
un **parametro**: così la notte del cambio si può provare invece di scoprirla. La notte in cui
l'ora avanza, un orario che non esiste **slitta** e non sparisce.

**Il lavoro rimasto a metà.** Se il supervisore muore mentre `pg_dump` scrive (riavvio, finestra
chiusa, tetto scaduto), sul disco resta un dump **troncato** — più recente di ogni altro. La semina
dal disco lo prendeva per buono e il recupero dell'occorrenza persa spariva in silenzio. Ora
l'esecuzione si dichiara *prima* di partire (`RunningSince`): al giro successivo, un campo ancora
valorizzato significa **INTERROTTO**, la semina non si applica, e la scadenza persa resta persa —
cioè viene recuperata.

**Mai un worktree in un'attività che deve vivere per mesi.** `procione attivita migra` lanciato da
un worktree avrebbe registrato l'eseguibile *di quel worktree* e poi cancellato i task veri: al
`git worktree remove` la macchina sarebbe rimasta senza veglia e senza backup, in silenzio. È
l'incidente del 2026-08-17, con un eseguibile al posto di uno script. Ora la plancia registra
l'eseguibile del repository **principale**, e se non c'è **rifiuta**.

**Nascondere la console solo se è la propria.** `--muto` chiama `ShowWindow(SW_HIDE)`. Lanciato a
mano da una finestra `cmd`, il processo **eredita** quella console: nasconderla farebbe sparire la
finestra dell'utente con tutto lo scrollback. Si nasconde solo quando `GetConsoleProcessList`
restituisce 1, cioè quando la console l'ha creata l'host.

---

## Il principio: il verdetto è la risposta, non lo stato dichiarato

Ogni sonda arriva fino al dato osservabile, perché questa piattaforma ha già pagato tre volte per
la differenza:

| Sembra sano | Ma non lo è | Come lo verifica la plancia |
|---|---|---|
| `kind-apiproxy` è `running` | il socat inoltra all'IP che il nodo aveva **prima** del riavvio di Docker (2026-08-04 e 2026-08-11: un'ora di TLS handshake timeout con tutto verde) | interroga `https://127.0.0.1:16443/livez` **attraverso** il proxy |
| la porta 18092 è in ascolto | il pod è stato sostituito, o il container è ripartito dentro lo stesso pod: il tunnel è morto e la porta resta in ascolto | confronta il marcatore lasciato da `ensure-trading-portforward.ps1` con l'identità **nome + conteggio riavvii** del pod vivo adesso |
| il motore risponde su 18092 | la 18092 è gRPC h2c: a un GET HTTP/1.x risponde **400 sempre**, il che rende il controllo strutturalmente incapace di dire «sano» (il watchdog ci ha creduto per mesi) | interroga la porta **health** 18093 |

Corollario, applicato ovunque: **ciò che non è previsto in questo assetto non è un guasto.** I
tunnel kubectl sull'assetto Compose sono grigi, non rossi — un rosso che sta sempre acceso è un
rosso che si smette di guardare.

## Cosa non fa, di proposito

La plancia **non riscrive** gli script di `scripts/`: li chiama. Quegli script non sono comandi
qualsiasi, sono sedimentazione di incidenti; riprodurne la logica in C# significherebbe farla
divergere il giorno in cui una delle due copie viene corretta — e la copia sbagliata sarebbe quella
dentro lo strumento che l'operatore apre per primo. La plancia aggiunge due cose che gli script non
hanno: i **guardrail** e la **verifica dell'esito**.

Non ha inoltre **nessun riferimento** a `ProcioneMGR.csproj` né a pacchetti NuGet. Deve poter dire
«il guscio non compila» anche quando il guscio non compila, e partire a rete staccata — che è
esattamente lo scenario in cui la piattaforma va rimessa in piedi.

## I guardrail

| Situazione | Cosa fa |
|---|---|
| `avvia guscio` con la 5199 già occupata | rifiuta, e dice chi la occupa (incidente del 2026-07-20: istanza di un worktree con master key segnaposto che intercetta l'utente) |
| due processi `ProcioneMGR` vivi insieme | avviso esplicito nel quadro, con il percorso da cui girano |
| `avvia compose` col cluster kind vivo (e viceversa) | rifiuta: regola 2, un solo scrittore |
| kind e Compose attivi insieme | riga **rossa** in cima al quadro |
| `riavvia motore` | chiede conferma, poi **rifà i tunnel** (il rollout sostituisce il pod: senza, il tunnel resta stantio) |
| `cluster distruggi` | conferma da **digitare**, non un `s/n` |

## Comandi

### Guardare

```bash
procione stato            # un quadro e via. Esce 0 se tutto è a posto, 1 con avvisi, 2 con guasti
procione stato --json     # la stessa cosa per un altro script
procione guarda           # il quadro che si ridisegna da solo
procione dottore          # i PREREQUISITI: strumenti, segreti, configurazione
procione log motore -f    # guscio, motore, ingestion, ml, bringup, watchdog, compose, osservabilita
```

Il codice di uscita di `stato` è pensato per gli script: `0` tutto in ordine, `1` avvisi, `2`
almeno un guasto.

### Accendere e spegnere

```bash
procione avvia                    # bring-up completo (scripts/bringup.ps1)
procione avvia guscio             # solo il guscio, come il profilo procione-main
procione avvia cluster            # crea il cluster kind (prerequisito una-tantum)
procione avvia compose --motore   # assetto Docker Compose, col profilo engine
procione ferma guscio|tunnel|compose|tutto
procione riavvia motore|ingestion|ml|guscio
```

`ferma tutto` **non** spegne il cluster: quello è il core caldo, opera da solo anche senza guscio.

### Riparare

```bash
procione ripara            # rilancia il bring-up: idempotente
procione ripara proxy      # ricrea kind-apiproxy verso il NOME DNS del nodo, e VERIFICA
procione ripara tunnel     # rifà i port-forward 18080/18092/18093
procione ripara contesto   # riporta kubectl sul proxy 127.0.0.1:16443
```

### Manutenzione

```bash
procione backup [--verifica]
procione veglia                     # un giro di watchdog.ps1 adesso, sotto i tuoi occhi
procione segreti [tutti|postgres|trading|ui|da-appsettings]
procione postgres                   # il guscio come lo avvia run-postgres.ps1 (Production)
procione immagini [target]          # build locale + import nel nodo kind
procione smoke                      # le cinque asserzioni e2e contro il cluster
procione argocd su|giu|installa|ripunta <rev>
procione osservabilita su|giu
procione apri [rotta]
procione cluster distruggi
```

### Le corsie di trading

```bash
procione corsie stato                        # quali girano, e in che modalità
procione corsie avvia tutte --modalita paper # oppure `avvia 1,2,3`
procione corsie ferma tutte
```

Delega a `tools/LaneControl`, che parla col motore per lo **stesso gRPC del guscio** e con lo stesso
segreto condiviso — non apre un percorso nuovo, ne apre uno da console a quello che c'era. Nasce il
2026-08-23, quando le otto corsie sono rimaste ferme **nove giorni** (ultimo ordine 2026-08-14) dopo
un riavvio del pod del motore: si comandavano solo dalla pagina `/trading`, dietro login, e nessuno
strumento poteva nemmeno *dirlo* senza aprire il browser.

**Live no, e non c'è un flag.** Lo strumento rifiuta `--modalita live` e la modalità non ha un
default: va scritta. Un default su un parametro che decide se gli ordini sono simulati o veri è un
default che prima o poi qualcuno non legge; e uno strumento da riga di comando che sa dire «Live» è
precisamente il percorso automatico che questo progetto non vuole. Verso Live si passa da
`/trading`, dove `Trading:Safety:RequireManualConfirmationForLive` pretende una conferma umana.

Ogni avvio è **verificato**: dopo `StartLane` lo strumento richiede lo stato e guarda se la corsia
risulta davvero in marcia, invece di fidarsi dell'assenza di eccezioni.

### Tutto il resto

Nessuno dei diciannove script di `scripts/` resta fuori: quelli che non hanno un comando proprio si
lanciano dallo sportello, con i loro argomenti.

```bash
procione esegui                             # l'elenco
procione esegui k8s-argocd-retarget master  # uno qualsiasi, coi suoi argomenti
procione strumenti                          # i programmi di tools/
procione strumenti FuturesVerify            # lanciati con la connection string già impostata
```

Due cose la plancia **non** le esegue, e non è prudenza generica: sono le uniche due azioni
irreversibili della cartella, e una plancia serve a rendere le cose facili — che è esattamente ciò
che non va fatto con un ordine di mercato vero e con un ripristino che sovrascrive il database.
`SpotVerify --place-min-order` e `DbBackup restore` vanno lanciati a mano, deliberatamente; la
plancia stampa il comando e si ferma.

I nomi sono in italiano perché sono interfaccia; gli equivalenti inglesi ovvi (`status`, `up`,
`down`, `logs`, `fix`, `doctor`) sono accettati come sinonimi.

## Dai worktree

La plancia deduce il repository dalla posizione del proprio eseguibile. Da un worktree comanderebbe
quindi il worktree — e avviare il guscio da lì sulla 5199 è precisamente l'incidente che la regola
del profilo `procione-main` esiste per impedire. Per comandare sempre il repo principale:

```powershell
$env:PROCIONE_REPO = 'C:\Users\proci\Desktop\ProgettoP'
```

`procione dottore` stampa sempre quale radice sta usando e come l'ha dedotta.

**La registrazione dell'attività fa eccezione, e di proposito.** Un'attività pianificata sopravvive
a chi l'ha creata: registrare l'eseguibile di un worktree significa che al `git worktree remove` il
task fallisce a ogni logon con `0x8007002E`, nessun supervisore parte, e nessuno se ne accorge — è
l'incidente del 2026-08-17, sei notti di backup perse per un task registrato da un worktree.
`procione attivita registra|migra` registra quindi sempre l'eseguibile del repository **principale**
(la stessa `Get-MainRepoRoot` di `db-backup.ps1`), e se lì non è stato compilato **rifiuta** invece
di registrare qualcosa che morirà.

## Com'è fatta

| File | Ruolo |
|---|---|
| `Platform.cs` | nomi, porte e percorsi: nessuno inventato qui, tutti già presenti in `scripts/` o `infra/k8s/` |
| `Proc.cs` | esecuzione di processi esterni, con timeout e senza eccezioni che escano |
| `Probes.cs` | le sonde, tutte in sola lettura e tutte in parallelo |
| `Parsing.cs` | traduzione da testo degli strumenti a dati — **funzioni pure** |
| `Verdicts.cs` | i verdetti che richiedono un ragionamento — **funzioni pure** |
| `Schedule.cs` | quando tocca a un lavoro — **funzione pura**: l'orologio e il fuso sono parametri |
| `Jobs.cs` | la tabella dei lavori e le preferenze su quali sono accesi |
| `Supervisor.cs` | il ciclo residente: esclusione, esecuzione senza finestre, battito, log |
| `Tasks.cs` | le attività di Windows: leggerle, e ridurle a una |
| `Files.cs` | i due file condivisi fra processi, letti e scritti senza incontrarsi a metà |
| `Actions.cs` | le azioni: guardrail + delega agli script + verifica |
| `Ui.cs`, `Dashboard.cs` | resa a schermo e plancia interattiva |

`Parsing` e `Verdicts` stanno a parte perché sono il punto in cui una plancia può mentire in
silenzio: sono provati contro casi noti in `ProcioneMGR.Tests/ProcioneConsoleTests.cs`, incluso il
caso sano — che deve restare muto.

## Verifica (i quattro livelli di `docs/STANDARD-VERIFICA.md`)

1. **Unità contro riferimento indipendente** — il BOM del marcatore non è simulato a mano: il test
   scrive i **byte** che `Set-Content -Encoding utf8` produce davvero e li rilegge come la plancia.
   Gli esempi di output di `docker ps`, `kubectl` e `/health` sono copiati da esecuzioni reali.
2. **Controllo** — sul tunnel sano il verdetto deve **tacere**, su 51 varianti normali; e deve
   accendersi sui tre modi in cui un tunnel muore (pod sostituito, container riavviato nello stesso
   pod, tunnel a metà).
3. **Integrazione** — eseguita contro Docker, il cluster kind e i pod veri.
4. **Operativo** — al primo giro contro la piattaforma vera la plancia ha trovato due cose che
   nessuno stava guardando: il **guscio caduto** da pochi minuti, e il **backup notturno fallito**
   (`0x00000001`) con l'ultimo dump di 5 giorni prima.

> Il test del livello 1 ha inoltre trovato un difetto **nella plancia stessa**: gli esiti delle
> attività pianificate in forma HRESULT senza segno (`2147942401` = `0x80070001`) traboccano da
> `Int32`, e con `int.TryParse` il ripiego dichiarava l'attività **sana**. Un'attività fallita che
> risulta a posto è esattamente il controllo che rassicura e basta. Ora si legge in `long`.
