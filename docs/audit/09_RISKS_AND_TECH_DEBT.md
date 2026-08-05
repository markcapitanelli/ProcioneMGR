# 09 — Rischi e debito tecnico

Ordinati per priorità. Ogni voce ha: cosa, dove si verifica, perché conta, cosa farci.

---

## 🔴 Priorità ALTA

### R1 — Segreti committati per errore, rotazione obbligatoria {#r1}

**Il problema più grave trovato in questo audit.**

> 🔒 **Il dettaglio operativo di questo rilievo è deliberatamente fuori dal repository.**
> Percorso del file, commit di origine ed elenco puntuale delle chiavi coinvolte vivono nel notebook
> NotebookLM privato del progetto (documento `09` completo). Questo repository è **pubblico**: finché
> la rotazione non è completata, scrivere qui dove guardare aumenterebbe il danno invece di ridurlo.
> Il proprietario ha già le informazioni complete.

**In sintesi, senza indicare dove.** Durante l'audit del 2026-08-04 è emerso che un file di
configurazione contenente segreti reali è finito **tracciato da git** — non coperto dal
`.gitignore`, che intercettava il nome esatto ma non le sue varianti con suffisso — ed è arrivato
su un ramo pubblicato. Fra i segreti coinvolti c'è la **chiave di cifratura a riposo**, quella che
protegge le credenziali API degli exchange salvate sul database.

**Perché conta, e non è teorico.** Il giro dell'interfaccia ha confermato che nel database ci sono
**tre credenziali exchange reali** ([12](12_UI_WALKTHROUGH.md)) cifrate proprio con quella chiave.
Esiste quindi materiale concreto che quella chiave decifra. Il file conteneva anche un commento che
diceva testualmente di non committare quel segreto: il promemoria c'era, la disciplina è saltata.

**Cosa farci** — in quest'ordine. Le prime tre valgono *a prescindere* dalla rimozione dal
repository, perché **ciò che è stato pubblico va considerato compromesso**:

1. **Ruotare la chiave di cifratura** e ri-cifrare le credenziali exchange esistenti.
2. **Revocare e rigenerare le chiavi API** su Binance e Bitget.
3. **Cambiare la password del database** e rigenerare il segreto condiviso fra i servizi.
4. Rimuovere il file dal tracking e correggere il `.gitignore` con un **pattern che copra le
   varianti** del nome, non solo il nome esatto, con eccezione esplicita per il template `.example`.
5. Decidere se riscrivere la storia (`git filter-repo`) o accettare che il file vi resti — come già
   accaduto per altro materiale. La rotazione resta comunque obbligatoria.

> Non ho eseguito nessuna di queste azioni: l'audit è read-only e la rotazione di segreti in
> produzione è una decisione del proprietario.

**Lezione trasversale, questa sì da tenere in repo:** un `.gitignore` che elenca nomi esatti non
protegge dai file di backup con timestamp. Il pattern va scritto sulla famiglia, non sull'istanza.

### R2 — `run-postgres.ps1` muore se il cluster kind è giù {#r2}

**Verificato dal vivo durante questo audit.** Lo script ha `$ErrorActionPreference = "Stop"` (riga
21) e invoca `kubectl` alle righe 42 e 58 con `2>$null`. Quando il cluster non risponde, PowerShell
5.1 converte lo stderr del comando nativo in un `NativeCommandError` **terminante**: lo script muore
prima di arrivare a `dotnet run` (riga 72).

Il punto è che i rami `else` **sono scritti apposta** per il caso "cluster giù" e stampano un
avviso giallo:

```powershell
Write-Host "Ingestion: cluster kind non raggiungibile - sync manuale UI indisponibile…"
```

Quel messaggio non viene mai raggiunto. L'intenzione best-effort è annullata dalla configurazione
degli errori.

**Impatto:** dopo ogni riavvio di Windows o Docker — cioè esattamente quando serve — **l'app non si
avvia con lo script ufficiale**. Ed è il primo comando che chiunque prova.

**Cosa farci:** avvolgere le due chiamate `kubectl` in `try { … } catch {}` con
`-ErrorAction Stop`, oppure abbassare localmente `$ErrorActionPreference = "Continue"` intorno ai
due blocchi. Fix di poche righe, alto ritorno.

### R3 — Il conteggio dei test nel README non corrisponde {#r3}

Il [README.md](../../README.md) dichiara "988 test" in tre punti. Il conteggio statico degli
attributi in `ProcioneMGR.Tests/` restituisce ~1999 metodi. Le memorie di progetto citano 712 e poi
1011 in momenti diversi.

**Perché è ALTA e non cosmetica:** questo README è il documento che un lettore usa per decidere se
fidarsi. Una piattaforma che fa dell'onestà statistica la sua bandiera non può avere il numero
sbagliato in copertina. E se il numero è vecchio, il lettore non sa quali altre affermazioni lo sono.

**Cosa farci:** eseguire `dotnet test --list-tests`, aggiornare il numero, e valutare di generarlo
in CI invece di scriverlo a mano.

---

### R15 — La quantità dell'ordine può partire non arrotondata verso l'exchange {#r15}

**Difetto con evidenza di produzione.** Nello storico ordini di `/trading` ci sono ordini realmente
inviati a Binance e rifiutati:

```
HTTP 400: {"code":-1100,"msg":"Illegal characters found in parameter 'quantity';
           legal range is '^([0-9]{1,20})(\.[0-9]{1,20})?$'."}
```

La catena, ricostruita nel codice ([13 — Il difetto della quantità](13_DEEP_DIVE_CODE.md#quantita)):
`qty = notional / price` produce fino a 28-29 cifre; `RoundQuantity` **non arrotonda** se
`StepSize == 0` e `IsTradable` **approva tutto** se i minimi sono zero. Quindi un `SymbolFilters`
restituito ma non popolato fa passare la quantità grezza fino alla API.

È la classe di difetto che il progetto ha già catalogato — *«controlli che rassicurano a prescindere
dalla realtà»* — ricomparsa in un punto nuovo. `RoundPrice` ha la stessa identica forma
(`TickSize > 0m ? … : price`) e quindi lo stesso problema latente.

**Cosa farci:** trattare `StepSize == 0` come "filtri assenti" e rifiutare l'ordine in Testnet/Live;
troncamento incondizionato prima dell'invio come rete finale; un test con `SymbolFilters` a zero.

### R16 — Nessun backup del database esiste {#r16}

`/admin/backup` riporta: **«Nessun backup presente in …\ProcioneMGR\backup»**.

Il database contiene ≈12,18 milioni di candele (anni di ingestione), 196 run di esperimenti con i
loro artefatti, decine di modelli addestrati, 17 strategie salvate e **le credenziali cifrate di tre
account exchange reali**. Non esiste una copia.

La funzione di backup c'è, funziona ed è a un clic. Semplicemente non è mai stata usata, e non c'è
nulla che la esegua da sola.

**Cosa farci:** un backup adesso, e schedularlo. Esiste già `tools/DbBackup` e un job
`dbbackup-smoke2` nel cluster: manca il CronJob vero.

## 🟡 Priorità MEDIA

### R17 — Il 38% delle chiamate al supervisore AI fallisce {#r17}

`/admin/ai-supervisor` mostra: **advisory ok 31, in errore 19**. Su 50 tentativi, 19 fallimenti.

Il failover automatico (Nvidia → Groq → Gemini → HuggingFace) esiste e ha funzionato, quindi il
sistema non si è mai fermato. Ma un tasso di errore così alto o è normale per il provider scelto — e
allora va detto — o nasconde un problema di formato/timeout che nessuno ha guardato perché il
degrado è silenzioso e innocuo (il layer è advisory).

**Cosa farci:** loggare la causa per errore e distinguere timeout, rate limit e risposta malformata.

### R4 — Nessuna fallback policy di autorizzazione {#r4}

In [Program.cs](../../ProcioneMGR/Program.cs) non esiste alcuna `AddAuthorization(...)` con
`FallbackPolicy`: l'unica occorrenza della parola "Authoriz" è lo `using` a riga 2. La protezione
dipende **interamente** dall'attributo `@attribute [Authorize]` su ogni pagina.

**Stato attuale: pulito.** Ho controllato tutte le pagine con `@page` in `Components/Pages/`: le sole
senza attributo sono `Home`, `Error`, `NotFound`, tutte volutamente pubbliche. E la sonda HTTP su 28
route protette dà **28 redirect su 28**.

Il rischio è **latente**: una pagina nuova che dimentichi l'attributo nasce pubblica, e niente lo
segnala — né compilatore, né test, né CI.

**Cosa farci:** o una `FallbackPolicy` che richieda autenticazione con `[AllowAnonymous]` esplicito
sulle tre pubbliche, oppure un test che enumeri i `.razor` con `@page` e fallisca se ne trova uno
senza attributo e non in whitelist. La seconda è meno invasiva e documenta l'intenzione.

### R5 — Due fonti di dati alternativi sono rotte (rilievo corretto) {#r5}

- `ForexFactory` → **403 Forbidden** (`https://www.forexfactory.com/calendar`)
- `FXStreet-CentralBanks` → **404 Not Found** (`https://www.fxstreet.com/rss/news/central-banks`)

> ✏️ **Correzione.** Nella prima stesura avevo scritto che queste fonti fallivano "in silenzio".
> **Era sbagliato.** Il giro completo dell'interfaccia ([12](12_UI_WALKTHROUGH.md)) ha mostrato che
> `/sentiment` ha un pannello *Salute delle fonti* con un badge per fonte: le due rotte sono
> marcate `bg-danger` (rosso) con tooltip che riporta l'errore esatto e l'ora. La segnalazione
> esiste, è precisa, e funziona.

Il rilievo reale, più stretto: le due fonti sono **rotte da tempo e nessuno le ha sistemate**, e non
esiste un allarme *proattivo* — bisogna aprire `/sentiment` per accorgersene. Il 403 di ForexFactory
ha l'aria di un blocco anti-scraping: non tornerà da sola.

**Cosa farci:** portare lo stato rosso dove si guarda ogni giorno (Home o `/metrics`), oppure una
notifica dopo N fallimenti consecutivi. Il canale Telegram esiste già.

**Nota secondaria — DA VERIFICARE:** quasi tutte le fonti *verdi* riportano *(0 elementi)* all'ultima
sync. Probabile deduplica di elementi già acquisiti, ma va confermato che il flusso notizie non sia
di fatto vuoto.

### R6 — File monstre difficili da modificare in sicurezza {#r6}

| File | Dimensione | Nota |
|---|---|---|
| `Services/Trading/TradingEngine.cs` | **87,8 KB** | il doppio del secondo classificato; mitigato da `Internal/` (13 collaboratori estratti) ma ancora enorme |
| `Components/Pages/Admin/Autonomy.razor` | **107,6 KB** | la pagina più grande, **senza page service**: la logica vive nel markup |
| `Components/Pages/Sentiment.razor` | 55,0 KB | idem, senza page service |
| `Program.cs` | 674 righe | tutta la composizione in un file |

Sei pagine hanno già l'orchestrazione estratta in page service testabili (refactor P1-5). `Autonomy`
e `Sentiment` sono rimaste indietro — e `Autonomy` è la pagina che governa l'autonomia del sistema,
cioè quella dove un errore costa di più.

**Cosa farci:** estrarre `AutonomyPageService` seguendo il pattern già stabilito. È un refactor con
un modello di riferimento in casa, quindi a basso rischio.

### R7 — `AnthropicLlmClient.cs` contiene cinque provider {#r7}

Il file [Services/Llm/AnthropicLlmClient.cs](../../ProcioneMGR/Services/Llm/AnthropicLlmClient.cs)
dichiara gli endpoint di NVIDIA, Google Gemini, Groq, HuggingFace **e** Anthropic. Il nome mente su
cosa contiene, e il provider realmente attivo oggi è NVIDIA (`meta/llama-3.3-70b-instruct`).

**Cosa farci:** rinominare in `LlmProviderCatalog.cs` o separare gli endpoint in un file di
configurazione dei provider. Costo quasi nullo, evita che il prossimo lettore cerchi nel posto
sbagliato.

### R8 — Tre CLI fuori dalla soluzione {#r8}

`tools/` contiene cinque progetti, ma `ProcioneMGR.sln` ne referenzia **due** (`DbBackup`,
`StrategyHunter`). Restano fuori: **`FuturesVerify`, `PlatformExpand`, `SpotVerify`**.

Conseguenza concreta: non vengono compilati da `dotnet build ProcioneMGR.sln`, quindi **non si
accorge nessuno se si rompono** quando cambia un'API che usano. Il [README.md](../../README.md) li
elenca tutti e cinque come se fossero di pari dignità.

**Cosa farci:** aggiungerli alla soluzione, oppure dichiarare nel README che sono strumenti
occasionali non mantenuti.

### R9 — Query EF senza `OrderBy` {#r9}

Warning nel log di avvio:

```
warn: Microsoft.EntityFrameworkCore.Query[10103]
      The query uses the 'First'/'FirstOrDefault' operator without 'OrderBy' and filter operators.
      This may lead to unpredictable results.
```

Su PostgreSQL l'ordine di riga non è garantito senza `ORDER BY`. Se la query in questione sceglie
un modello attivo o una configurazione, può restituire righe diverse fra un'esecuzione e l'altra —
il tipo di non determinismo che una piattaforma che si vanta di essere deterministica non dovrebbe
avere.

**DA VERIFICARE:** il warning non nomina la query. Serve `EnableSensitiveDataLogging` in
Development, o una ricerca dei `FirstOrDefault` senza `OrderBy` su tabelle multi-riga.

---

## 🟢 Priorità BASSA

### R10 — Pagine Identity non tradotte

Tutta l'app è in italiano; `/Account/Login` e le pagine sorelle sono in inglese
("Log in", "Remember me", "Forgot your password?"). È lo scaffolding standard mai localizzato.

### R11 — Errori WebAuthn in console sulla pagina di login

Due errori (`NotAllowedError`) dalla richiesta passkey in *conditional UI*. Nessun impatto
funzionale; rumore che rende più difficile notare errori veri.

### R12 — `<select>` Exchange troppo stretto su `/dashboard`

La voce "Binance" viene tagliata. Cosmetico.

### R13 — Corsie "non configurata" senza indicazione

Le corsie 3 e 7 appaiono nella barra di `/trading` come "non configurata", senza dire cosa farne o
come rimuoverle.

### R14 — Nessun linter configurato

Nessun `.editorconfig` con regole di analisi, nessun `dotnet format` in CI. La qualità è retta da
compilazione e test. **DA VERIFICARE** se sia una scelta deliberata.

---

## Codice duplicato

Non ho trovato duplicazione strutturale significativa. Anzi, il progetto mostra il contrario in più
punti:

- `AddTradingLanes` è **condivisa verbatim** fra app Blazor e host `ProcioneMGR.Trading` — una sola
  composizione per due processi.
- `RunApplyEvaluator` / `PipelineApplier` sono la catena "valuta e applica" **condivisa** fra
  scheduler e campaign planner, esplicitamente una sola implementazione.
- I predittori ML condividono `RegressionPredictorBase`.

## Configurazioni fragili

| Cosa | Perché fragile |
|---|---|
| **Coerenza `LaneCount` guscio ↔ core** | due valori in due posti (`appsettings.json` e `trading-config.env`); esiste `LaneCountCoherenceProbe` a controllarlo, ma **non può verificare nulla se il core è irraggiungibile** — ed è esattamente ciò che è successo durante l'audit |
| **Migrazioni non applicate all'avvio** | scelta deliberata (migrate-on-deploy), ma chi salta il passo trova un DB vuoto senza un errore che lo spieghi |
| **Toggle `UseRemoteTrading` / `UseRemoteIngestion`** | il vincolo "mai due motori vivi" è affidato a un commento in configurazione, non a un controllo automatico |
| **`appsettings.json` fuori dal repo** | corretto per i segreti, ma significa che i file versionati **non descrivono la configurazione reale**: quello committato dice 3 corsie, l'istanza vera ne ha 8 |

## Sicurezza — quadro d'insieme

Cose **fatte bene**, e sono parecchie:

- Cifratura AES-256-GCM a riposo, con value converter EF: i segreti non toccano il DB in chiaro.
- Decifratura per-riga resiliente: una riga con chiave sbagliata non abbatte la pagina.
- Fail-fast in Production sulla master key placeholder.
- Cinque barriere indipendenti verso Live, con `SafetyChecker` **statico e puro** — non
  sostituibile via DI, quindi non aggirabile per configurazione.
- Layer AI strutturalmente incapace di eseguire: nessun servizio di esecuzione gli è iniettato.
- Autorizzazione a tre livelli, verificata 28/28 sul campo.
- Credenziali exchange correttamente isolate per utente.

Il quadro è quello di un progetto che alla sicurezza ci ha pensato sul serio. Il che rende **R1**
tanto più doloroso: tutta questa architettura protegge un segreto che è stato pubblicato su
GitHub. La catena è lunga e robusta, e l'anello che ha ceduto è un file di backup dimenticato.

## Dipendenze obsolete o rischiose

**Non verificato in questo audit.** Da eseguire:

```bash
dotnet list package --outdated
```

```bash
dotnet list package --vulnerable --include-transitive
```

---

## Riepilogo per priorità

| ID | Rischio | Priorità |
|---|---|---|
| R1 | Segreti reali su repo pubblico (3 credenziali exchange reali a rischio) | 🔴 **ALTA — agire subito** |
| R15 | Quantità ordine non arrotondata → ordini rifiutati dall'exchange | 🔴 ALTA |
| R16 | **Nessun backup del database esiste** | 🔴 ALTA |
| R2 | `run-postgres.ps1` muore col cluster giù | 🔴 ALTA |
| R3 | Conteggio test errato nel README | 🔴 ALTA |
| R17 | 38% di fallimenti nelle chiamate al supervisore AI | 🟡 MEDIA |
| R4 | Nessuna fallback policy di autorizzazione | 🟡 MEDIA |
| R5 | Due fonti AltData rotte (segnalate in UI, mai riparate) | 🟡 MEDIA |
| R18 | Incoerenze fra pannelli sulle corsie (`/trading` vs `/ensemble`) | 🟢 BASSA |
| R19 | Ordini duplicati per candela, assorbiti dall'anti-spam | 🟡 MEDIA |
| R6 | File monstre senza page service | 🟡 MEDIA |
| R7 | `AnthropicLlmClient.cs` mal chiamato | 🟡 MEDIA |
| R8 | Tre CLI fuori dalla soluzione | 🟡 MEDIA |
| R9 | Query EF senza `OrderBy` | 🟡 MEDIA |
| R10–R14 | UX, i18n, linting | 🟢 BASSA |
