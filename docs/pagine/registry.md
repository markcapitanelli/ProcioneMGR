# Registry Modelli — `/registry`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Registry.razor`](../../ProcioneMGR/Components/Pages/Registry.razor) (~292 righe) |
| **Route** | `/registry` |
| **Sezione navigazione** | Ricerca & Sviluppo |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Governa il **ciclo di vita dei modelli ML**: ogni modello salvato vive in uno stadio
`Staging → Challenger → Champion`, con uscita a `Retired`. Regole cardine (dal `GuidaPanel`,
righe 16–27):

- C'è **un solo Champion per (coppia, timeframe)**.
- La promozione a Champion passa dal **gate del Deflated Sharpe**: un modello con DSR
  inferiore **non sostituisce mai** quello in carica.
- Se un Champion degrada (drift), il monitor lo **ritira e accoda un retrain** — mai
  un'azione diretta sul trading Live.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 16–36 | Le regole del ciclo di vita, e cosa costa il ritiro |
| Card per gruppo | 48–180 | Un card per (Symbol, Timeframe) con badge del Champion corrente (nome + DSR) o "Nessun Champion attivo" |
| Tabella modelli | 65–178 | Modello, tipo, stadio (badge colorato), DSR, versione, note (motivo ritiro, "retrain accodato"), azioni |
| Righe di conferma | 137–177 | Sotto-riga a tutta larghezza (`colspan=7`) per il ritiro e per il rientro: è lì che sta il testo che dichiara i rischi |

Azioni per riga, condizionate allo stadio:
- `Staging` → **→ Challenger**
- `Staging` o `Challenger` → **→ Champion** (passa dal gate DSR)
- Qualsiasi stadio tranne `Retired` → **Ritira** (conferma a due passi)
- `Retired` → **↩ Riporta in Staging** (conferma a due passi)

### Il ritiro e il suo rientro (2026-08-19)

Fino a questa revisione `Retired` era **senza uscita**: la cella delle azioni di una riga ritirata
era vuota, `PromoteToChallengerAsync` accettava solo `Staging`, e un ritiro accidentale si annullava
soltanto scrivendo a mano sul database. Non era una decisione — nessun documento né commento aveva
mai dichiarato l'irreversibilità — ma un percorso mai costruito: il messaggio di rifiuto del
registry indicava da sempre un rientro («va prima ri-portato a Challenger») che non esisteva da
nessuna parte. Aggravante: `Drift:RetireChampionOnAlert` vale `true` per default del POCO (la
sezione `Drift` è assente da `appsettings.json`), quindi anche il ritiro **automatico** finiva in
uno stato senza ritorno; e il pulsante «Esegui check ora» in `/admin/autonomy` invoca
`FeatureDriftWorker.TickAsync` **scavalcando** `Drift:Enabled`.

Sono state prese entrambe le strade, perché coprono percorsi diversi:

- **Il rientro esiste** — `ReinstateToStagingAsync` riporta il modello a `Staging` e **solo** a
  `Staging`: restituisce l'*eleggibilità*, non il regno. Da lì ripassa da Challenger e deve
  ri-superare il gate DSR, quindi il motore — che risolve unicamente `GetChampionAsync` — resta
  fuori dal raggio della transizione. Il metodo restituisce un esito motivato e rifiuta se il
  modello non è `Retired`: la conferma può restare aperta mentre il ciclo drift cambia lo stadio
  sotto, e in quel caso il secondo clic deve dire di no.
- **Il ritiro dichiara cosa costa** — «Ritira» era l'**unica azione distruttiva della pagina a un
  clic solo**, esattamente il difetto corretto in `/trading` il 2026-08-17. Ora ha la stessa
  conferma a due passi legata all'`Id` della riga (non alla posizione: la tabella si riordina),
  dichiara che le corsie che montano il Champion restano senza modello, e lascia **scrivere il
  motivo**, che prima era la costante inutile «Ritirato manualmente dalla UI.».

Due conseguenze di progetto, entrambe deliberate:

1. **I campi del ritiro non si azzerano più.** `RetiredAtUtc` / `RetiredReason` /
   `RetrainRequestedAtUtc` descrivono l'**ultimo** ritiro, lo `Stage` dice se è in corso. La
   pulizia che stava in `TryPromoteToChampionAsync` era codice morto (un `Retired` veniva respinto
   prima) e col rientro sarebbe diventata viva e dannosa: avrebbe cancellato la cicatrice che serve
   a chi rivaluta un modello ritirato per drift. Nessuna migrazione: nessuna query in tutta la
   soluzione filtra su quei campi.
2. **La colonna Note è consapevole dello stadio.** Prima rendeva per sola nullità del campo: un
   modello riportato in Staging avrebbe esibito «drift: 3 feature in alert» accanto a un badge
   vivo. Ora su una riga non-`Retired` il motivo compare in grigio come *«già ritirato il … — …»*
   (regola 5: mai un valore vecchio mostrato come attuale).

Il rischio residuo è dichiarato a schermo invece che risolto in codice: il DSR di un modello
ritirato è la misura **precedente** al ritiro, e se l'alert di drift persiste il check successivo
può ri-ritirarlo appena torna Champion. La conferma lo scrive, insieme al motivo del ritiro salvato.
`RetireChampionOnAlert` e `MinAlertsToRetire` **non** sono stati toccati: hanno la loro manopola in
`/admin/autonomy` e sono una decisione separata.

## Come funziona (flusso del codice)

### Caricamento — `LoadAsync` (righe 198–219)
Legge tutti i `SavedMlModels` e li raggruppa per (Symbol, Timeframe); dentro ogni gruppo
l'ordinamento mette prima il Champion, in fondo i Retired, e nel mezzo ordina per DSR
decrescente. Chiude anche le conferme aperte: **è per questo riordino** che le conferme sono legate
all'`Id` e non a un booleano di pagina — una domanda armata su dati vecchi punterebbe l'operatore su
una riga che nel frattempo si è spostata.

### Transizioni (righe 221–279)
- `ToChallenger` → `IModelRegistry.PromoteToChallengerAsync(id)`.
- `ToChampion` → `IModelRegistry.TryPromoteToChampionAsync(id)`: restituisce un **outcome
  con motivazione** (`Promoted` + `Reason`) — se il DSR non batte il Champion in carica, la
  promozione è rifiutata e la UI mostra il perché. La logica di confronto sta tutta nel
  registry, la pagina si limita a riportare l'esito.
- `Retire` → `RetireAsync(id, motivo, requestRetrain: false)`: il ritiro manuale non accoda
  retrain (a differenza del ritiro automatico da drift). Il motivo arriva dal campo di testo della
  conferma, non più da una costante.
- `Reinstate` → `IModelRegistry.ReinstateToStagingAsync(id)`: restituisce uno
  `StageChangeOutcome (Changed, Reason)` e la pagina riporta **quello**, non un verde
  incondizionato — se nel frattempo il modello non è più `Retired`, il banner lo dice.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IModelRegistry` / `ModelRegistry` | Transizioni di stadio, gate DSR, ritiri e retrain | [`Services/Registry/ModelRegistry.cs`](../../ProcioneMGR/Services/Registry/ModelRegistry.cs) |
| `IDbContextFactory<ApplicationDbContext>` | Lettura `SavedMlModels` per la vista | [`Data/ApplicationDbContext.cs`](../../ProcioneMGR/Data/ApplicationDbContext.cs) |
| `FeatureDriftWorker` (indiretto) | Il ritiro automatico + retrain quando un Champion degrada | [`Services/Monitoring/Drift/FeatureDriftWorker.cs`](../../ProcioneMGR/Services/Monitoring/Drift/FeatureDriftWorker.cs) |

## Dati letti / scritti

- **Legge**: `SavedMlModels` (stadio, DSR, versione, note).
- **Scrive**: `SavedMlModels` (transizioni di stadio via registry).

## Collegamenti con le altre pagine

- [ML Lab](ml.md) — dove nascono i modelli (arrivano qui in Staging).
- [Esperimenti](experiments.md) — il registro dei run che li ha prodotti (link nel GuidaPanel).
- [Ensemble](ensemble.md) — il Champion è agganciabile come gamba con la sentinella
  `MlChampion` (auto-aggiornante, mai Live).
- [Trading](trading.md) — il motore risolve la sentinella Champion a runtime via
  `MlModelLoader`; il vincolo "Champion mai in Live" è imposto dal motore.

## Note di design

- La pagina non contiene logica di promozione: `TryPromoteToChampionAsync` incapsula il
  gate e restituisce la motivazione, così la regola è testabile e unica.
- Il DSR è mostrato in percentuale (probabilità che lo Sharpe osservato non sia frutto del
  caso dopo la correzione per test multipli).
- `ModelRegistry` è l'**unico scrittore** di `SavedMlModel.Stage` in tutta la codebase, e deve
  restarlo: l'invariante «un solo Champion per (Symbol, Timeframe)» non ha alcun appoggio nel
  database (l'indice non è unico) e regge solo perché ogni transizione passa da lì. Niente
  `ExecuteUpdate` dalla pagina.
- `Retired` **non è una quarantena**, ed è bene non farlo credere all'operatore: `MlModelLoader` non
  guarda lo stadio, quindi un modello ritirato è già selezionabile ed eseguibile da backtest, come
  gamba d'ensemble pinnata per Id e via gRPC per id esplicito. L'unica cosa che il ritiro toglie
  davvero è la sentinella Champion — cioè il motore.
- Il rientro atterra su `Staging` e **mai** su Champion anche per una ragione tecnica oltre che di
  politica: `TryPromoteToChampionAsync` incrementa la `Version`, e la cache del motore è per
  `(Id, Version)`. Scrivere `Stage = Champion` a mano lascerebbe il motore a servire il predittore
  vecchio.
- Copertura: `ModelRegistryTests` (gate, invariante, rientro, ciclo drift) e
  `RegistryPageRenderTests` (bUnit su Postgres vero: che i pulsanti ci siano, che il **primo** clic
  non agisca, che il motivo del ritiro sia sotto gli occhi prima del rientro, e che su una riga
  non-`Retired` il motivo sia reso come storia).

## Verifica operativa (livello 4) — 2026-08-19

Giro completo sull'app vera (`localhost:5199`, profilo `procione-reale`, dati reali) sul modello
**55 «Pipeline f99851dd RandomForest»** (AAVE/USDT 1d, `Staging`, DSR assente, non alimentava
nessuna corsia). Quello che si è visto, in ordine:

1. Primo clic su **Ritira** → compare la riga di conferma, il pulsante «Ritira» sparisce, e lo
   stadio a database resta `Staging` con **nessun banner**. Il clic singolo non ritira più.
2. Motivo scritto a mano nel campo → **Sì, ritira** → banner verde col motivo scritto
   dall'operatore, stadio `Retired`, e la cella azioni — **prima vuota** — mostra
   «↩ Riporta in Staging».
3. Primo clic sul rientro → conferma che cita *«Era stato ritirato il 19/08/2026 08:45 con questo
   motivo: …»*, l'avviso che il DSR è la misura precedente al ritiro e quello sul possibile
   ri-ritiro da drift. Stadio ancora `Retired`.
4. **Sì, riporta in Staging** → banner verde `Riportato in Staging. Motivo del ritiro precedente:
   … Per tornare Champion deve ri-superare il gate DSR.`, stadio `Staging`, azioni tornate a
   «→ Challenger / → Champion / Ritira».
5. Colonna Note dopo il rientro, reso HTML letto dalla pagina:
   `<span class="text-muted fst-italic" title="Storia, non stato attuale: il modello oggi è
   Staging.">già ritirato il 19/08/2026 — …</span>`. La cicatrice c'è ed è **qualificata come
   passato**, che è il punto della regola 5.
6. Log del guscio, livello Warning: `Modello 55 '…' RIPORTATO IN STAGING (AAVE/USDT 1d). Motivo del
   ritiro scavalcato: …`. Console del browser pulita, nessun errore server.

Traccia lasciata sui dati reali: il modello 55 conserva `RetiredAtUtc`/`RetiredReason` di questa
prova. È voluto — è la cicatrice — ed è visibile in pagina come nota storica.
