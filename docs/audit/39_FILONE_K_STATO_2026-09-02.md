# Filone K — dove siamo, e cosa resta davvero

> **Data:** 2026-09-02 · Chiude la Fase 2, porta la Fase 3 a un punto difendibile, apre la Fase 4.
>
> Questo documento esiste perché il filone ha prodotto **quattro documenti di misura, tre PR e
> diciannove item** in tre giorni, e serviva un posto solo che dica cosa è vero adesso — comprese le
> **cinque affermazioni mie che sono state smentite** lungo la strada.

---

## 0. La cosa più importante: la catena ha funzionato per intero, una volta

Il 2026-09-01 alle 23:17 la Regina ha schierato **da sola** `Composite XLM/USDT 4h` sulla corsia 4.
È la prima volta in tutta la vita della piattaforma, e la sequenza è questa:

```
19:51  il proprietario libera uno slot (ritira il doppione, tetto a 4)
23:02  Assign · corsia 4 · Applied=false
       └─ K33 rifiuta la TERZA copia di #5d0ed1f8, già in corsa sulla corsia 6
23:17  Assign · corsia 4 · Applied=TRUE
       └─ salta il doppione, prende il candidato successivo, lo schiera
23:32  Blocked — tetto grigio di nuovo saturo, correttamente
```

**Ogni anello di quella sequenza è stato costruito in questi tre giorni**, e ognuno era rotto in un
modo diverso: la colonna del journal era troppo stretta (K45), la guardia non esisteva (K33), il
tetto perdeva il proprio denominatore (K38), e il difetto che ha reso tutto visibile era un tick che
falliva in silenzio (K46).

---

## 1. Le otto volte che mi sono sbagliato

Le elenco per prime perché sono state usate per decidere, e perché il metodo del filone è che una
misura smentita vale più di una confermata.

| # | Cosa avevo scritto | Cos'era vero |
|---|---|---|
| 1 | «Il backfill K13 non ha trovato il candidato delle corsie 5 e 7» | **Non è mai stato eseguito.** L'assembly che lo contiene è posteriore alle etichette. Le chiavi combaciavano carattere per carattere |
| 2 | «`SourceVerdict` lo scrive `GreyDeployer`» | Gli scrittori erano **tre**. E per la *configurazione* sono **dieci** |
| 3 | «Il ritiro per inedia matura il 4-5 settembre» | Il 4-5 settembre **non succede niente**: la corsia 7 è ferma e non viene esaminata, la corsia 5 ha 1 trade contro una soglia di 0,243 |
| 4 | «Il journal tace perché il ramo del ritiro non confermato non journalizza» | Il tick **non ci arrivava nemmeno**: falliva sull'INSERT per una colonna `varchar(16)` |
| 5 | «Recuperare i 65 trade fuori dalla finestra di giudizio» | Almeno **27 sono righe di replay**: darebbero al criterio trade che quella corsia non ha mai fatto |
| 6 | «Un 404 del provider AI prova che il modello non esiste più» (K52, scritto la mattina) | **Falso, e misurato lo stesso giorno**: 10 tentativi identici su NVIDIA → 6 successi e 4 volte lo stesso 404, restituito in 753 ms. Il 410 è inequivocabile, il 404 no |
| 7 | «GET /v1/models funziona con quella chiave, quindi la chiave è sana» | Quell'endpoint è **pubblico**: risponde 200 con gli stessi 82 modelli anche con una chiave inventata. L'indizio era vero e **vuoto** |
| 8 | «La chiave NVIDIA è rotta a livello di account» | È rotta **per-modello**: sono deployment ritirati che sopravvivono nel catalogo. Prova: 14 chiamate riuscite oggi su 3 modelli diversi con la stessa chiave |

Più una correzione al documento 37 che è arrivata da un avversario: la forma degli arresti
(`Details = {}`, `UserId = null`) **non discrimina il chiamante**, perché *tutte* le 36 righe
`StopEngine` del database sono così.

E un errore che non è di ragionamento ma di **operazione**, ed è costato di più di tutti gli altri:
ho applicato la migrazione di K51 al database **vivo** mentre il codice di K51 stava in un ramo non
fuso. Il guscio in esecuzione, compilato da master, non conosce la colonna `Outcome`; senza un
default la sua INSERT prendeva NULL e il vincolo la respingeva. **Cinque ore e mezza di Regina
ferma**, con la stessa forma di guasto di K45 presa dall'altro verso. Il § 4-ter la racconta.

---

## 2. Fase 2 — CHIUSA

Le quattro decisioni del proprietario, applicate: **tetto grigio a 4 col ritiro del doppione**,
**corsia 7 liberata**, **`AutoPromoteToTestnet` spento**, **taglio sulle gambe lente rimandato** col
suo criterio di sufficienza dichiarato (~30 trade vivi, ≈2 mesi su quattro corsie).

E le tarature **non si sono toccate**, per una ragione misurata e non per prudenza: i timestamp di
`TradeRecords` sono **tempi di candela scritti in differita** — 35 righe precedono la creazione della
gamba a cui appartengono — e i trade di forward test veri sono **0 · 0 · 1 · 0 · 0**.

| | |
|---|---|
| K33 | la stessa ipotesi non occupa due corsie (predicato a due gradini) |
| K34 | `/ensemble` diceva `STOPPED` sopra una corsia che operava |
| K35 | il tetto \|PnL\| era cieco alle perdite non realizzate |
| K36 | posizione aperta su corsia ferma: nessuno la vedeva |
| K37 | provenienza dal run di schieramento, e tre stati d'archivio non due |
| K38 | il tetto grigio perdeva il proprio denominatore |
| K42 | la condanna a metà strada si scrive |
| K43 | una riga non è un trade (367 righe = 301 entità) |
| K44 | la soglia di ritiro ha una sola unità |

---

## 3. Fase 3 — CHIUSA, meno due item rimandati col loro numero

**Il tema della fase era uno solo: la piattaforma faceva cose senza lasciare traccia.**

| | |
|---|---|
| K39 | il monitor di decadimento giudicava su replay (65 righe su 66) |
| K40 | «non so leggere niente» diventava «le corsie sono impegnate» |
| K41 | `RecordedAtUtc`: l'ora di parete accanto a quella di candela, messa dal database |
| K45 | `Source` era `varchar(16)` contro stringhe da 23 — **teneva ferma la Regina** |
| K46 | un tick che non arriva in fondo non è più silenzioso |
| K47 | l'archivio degli episodi di identità — il numero che sette avversari su sette hanno chiesto |
| K48 | chi riscrive una corsia lascia il suo nome (contesto **obbligatorio**, dieci scrittori) |
| K49 | la guardia anche dall'auto-apply, che è la porta dell'impronta *e* di `/bot` |
| K49b (=K27) | l'universo si pota: potare **stringe** il gate, non lo allenta |
| K51 | il journal come **intento**: si scrive prima, e ciò che resta aperto si dichiara |
| K22 | il timbro di nascita ricostruito da una data **registrata**, mai inventata (§ sotto) |
| K21 | `MaxGreyLegs` scritto esplicitamente al valore che aveva già |

### K22 — il timbro di nascita, in forma non distruttiva

Il rimedio del PRD («rimuovere e ri-aggiungere le gambe da `/ensemble`») è **distruttivo**: conia un
nuovo `StrategyId`, quindi azzera l'identità della corsia e l'orologio dell'osservazione — dieci
giorni di cancello per guadagnare un campo.

Misurato il 2026-09-02: **4 gambe su 7 non hanno il timbro** (corsie 1, 2 e 5; le corsie 3, 4 e 6 ce
l'hanno perché schierate dal deployer grigio dal 31/08 in poi). Da K39 quelle 4 non sono misurabili
dal monitor di decadimento — verso giusto, ma per sempre.

**L'ancora esiste, ed è già registrata**: `FleetLaneObservations.FirstSeenUtc`, il primo tick in cui
la flotta ha visto quell'identità in corsa. Non è la nascita — è il primo momento in cui qualcuno
l'ha vista — ma **per costruzione non può precederla**, e questa asimmetria è tutto:

| se l'ancora è… | conseguenza |
|---|---|
| più **tardi** del vero | si escludono trade legittimi: il monitor ha meno dati e non condanna — errore prudente |
| più **presto** del vero | entrano i trade dell'ipotesi precedente — **è il difetto che K39 ha corretto** |

Il timbro si scrive **solo se la riga del ledger porta lo stesso `StrategyId`**: il ledger è per
corsia e una riassegnazione lo riscrive, quindi senza quel confronto si metterebbe sulla gamba di
oggi la data in cui è stata vista quella di ieri — un'invenzione con l'aria di una misura. Dove il
ledger tace, la gamba **resta senza timbro**.

Anteprima e scrittura sono due pulsanti separati in `/admin/autonomy`, come gli altri due backfill,
e la scrittura passa dal contesto obbligatorio di K48: **`Backfill` — «K22: timbro di nascita dal
ledger di osservazione»**.

### Cosa resta, e perché non è stato fatto

- **K25 (funding reale) e K26 (DSR ai bocciati)** restano rimandati **con il loro numero**: 0,102 di
  effetto contro 0,130 di rumore, e la trappola del `RejectReason` che porterebbe la fascia grigia da
  114 chiavi a 7.

---

## 4. Fase 4 — due item, e il secondo ha cambiato l'ordine degli altri

**K50 — quale caccia produce, e quale consuma budget per niente.**

Il criterio che il PRD suggeriva è una trappola, e la misura lo dimostra: **`ensembleLegs` è vuoto in
173 run su 173**, su tutte e cinque le configurazioni attive. «Zero gambe assemblate» le
addormenterebbe **tutte**, perché il collo di bottiglia è il gate, non la caccia.

Ciò che discrimina è la fascia grigia — chiavi *distinte* per run:

```
cfg 17 : 62 run →  82 chiavi → 1,32/run
cfg 20 : 15 run →  13 chiavi → 0,87
cfg 18 : 63 run →  26 chiavi → 0,41
cfg 19 : 16 run →   5 chiavi → 0,31
cfg  8 : 17 run →   1 chiave → 0,06   ← ventidue volte meno della migliore
```

Confronto **relativo** (universi diversi, domande diverse), chiavi **distinte** e non righe, mediana
**solo sulle giudicabili**. **Nessuna azione automatica**: mettere in sonno una caccia è una
decisione del proprietario, e questo numero non era mai esistito.

---

## 4-bis. K52 — il comitato non poteva votare, e nessuno poteva saperlo

**K30 chiedeva di portare l'AI dal veto alla proposta. Aprendolo si è scoperto che l'AI non
riusciva nemmeno a scegliere fra due opzioni già preparate — da sedici giorni.**

I voti sono nel journal, righe 129 e 130, e non c'era bisogno di dedurre niente:

```
Nvidia : HTTP 410 — «model 'meta/llama-3.3-70b-instruct' has reached its end of life on 2026-08-26»
Groq   : HTTP 404 — «model `llama-3.3-70b-versatile` does not exist or you do not have access»
Gemini : voto valido, confidenza 0,95, con una motivazione nel merito
```

Un voto valido su tre contro `MinValidVotes = 2`: **quorum aritmeticamente impossibile**. Confermato
dal consumo persistito, che conta solo le chiamate *riuscite*:

| provider | modello configurato | ultima risposta valida |
|---|---|---|
| gemini | `models/gemini-3.6-flash` | **oggi** |
| huggingface | `meta-llama/Llama-3.3-70B-Instruct` | **oggi** |
| nvidia | `meta/llama-3.3-70b-instruct` | **2026-08-25** (8 giorni) |
| groq | `llama-3.3-70b-versatile` | **2026-08-17** (16 giorni) |

### Perché è rimasto invisibile — che è la parte che conta

Il comitato è progettato perché un'astensione non costi nulla: *«il 503 di un free tier non deve
costare più di un voto»*. Il principio è giusto. Ma applicato **senza distinguere** copre anche il
votante che non tornerà: un modello ritirato dal catalogo non è un provider che ha una brutta
giornata. Ed è la quarta istanza, in questo filone, dello stesso difetto già pagato nel filone E —
**un controllo che rassicura a prescindere dalla realtà**.

Tre superfici lo dicevano, tutte e tre in modo sbagliato:

1. `/admin/ai-supervisor` → **«operativo»**, verde. Significava solo «il breaker non è aperto» — e il
   breaker non si muoveva perché il failover riusciva su Gemini. Non mentiva sul proprio
   significato: mentiva su quello che chi legge gli attribuisce.
2. Lo stesso pannello, sul verdetto del comitato → **«è il comportamento previsto, non un guasto»**,
   scritto invariabilmente, anche con due votanti su tre morti.
3. Il journal → `default:quorum-mancato`, che nella tassonomia di I8 vuol dire «hanno risposto e la
   maggioranza non si è formata». Non era vero: **non avevano risposto affatto**.

### Cosa è stato fatto

- **Una categoria nuova nel classificatore**: 404/410 → `modello assente`, separata da tutte le altre
  per una proprietà sola — le altre guariscono da sole (il rate-limit passa, il credito si ricarica,
  il server torna su), questa **no**. Nessun retry, failover o cooldown la risolve.
- **Il voto porta la causa** (`CommitteeVote.FaultCause`), e due predicati puri dicono chi è caduto
  per sempre e se il quorum sia ancora **aritmeticamente** possibile.
- **Il journal distingue**: `default:provider-guasti`, e questo ramo precede gli altri — stessa
  regola di K40, la causa prima del sintomo. (22 caratteri: la colonna è `varchar(32)` da K45, ed è
  stato verificato *prima*, non dopo.)
- **La flotta lo dichiara**: notifica critica una per episodio, con il testo che cambia a seconda che
  il quorum sia irraggiungibile o solo più fragile — e la guarigione è una notizia quanto il guasto.
- **Tre superfici corrette**: il riquadro in `/admin/autonomy`, il verdetto onesto in
  `/admin/ai-supervisor`, e una tabella nuova — *ultima risposta valida per provider*, dai dati che
  c'erano già in `LlmUsageRecords` e che nessuno guardava.

> Su quella tabella vale la lezione di «gate senza strumento»: mostra il **numero**, non un verdetto.
> Un provider silenzioso non è per forza rotto — se sta in fondo alla catena di failover può non
> essere mai stato interpellato, e chiamarlo «guasto» fabbricherebbe un allarme dal nulla. Il
> silenzio dice «vai a provarlo». L'unica eccezione, dichiarata, è l'**AI attiva**: quella è il primo
> anello e viene chiamata ogni volta, quindi per lei il silenzio è informazione.

### E la configurazione è stata riparata, dal vivo

Il pannello sapeva già rilevare i modelli scomparsi e proporre un sostituto — **ma solo all'apertura
della pagina, e nessuno la apriva.** La sua scelta automatica per Groq era inoltre `allam-2-7b`, un
7B in arabo al posto di un 70B generalista: un ripiego che *sembra* una riparazione.

| | prima | ora | perché |
|---|---|---|---|
| Groq | `llama-3.3-70b-versatile` (404) | `openai/gpt-oss-120b` | generalista grande, e **famiglia diversa** dagli altri due |
| NVIDIA | `meta/llama-3.3-70b-instruct` (410) | `mistralai/mistral-large-2-instruct` | ma **non basta**: vedi sotto |
| Comitato | `Nvidia,Groq,Gemini` | `Groq,Gemini,HuggingFace` | tre votanti vivi, tre lignaggi distinti |
| Timeout voto | 30 s | 50 s | Gemini scadeva a 30 |

La diversità non è un dettaglio estetico: **tre votanti che girano lo stesso modello sono un votante
con tre cappelli**, e il quorum diventa una moltiplicazione di sé stesso. È la stessa trappola già
pagata quando si randomizzava su asset correlati per stimare la significatività.

**Verificato dal vivo, col pulsante «Prova il comitato»:**

```
2 voti validi su 3 provider interrogati.
Verdetto: B — per QUORUM
Groq         B  1,00  ✓
HuggingFace  B  1,00  ✓
Gemini       ASTENUTO — timeout
```

Il comitato ha deliberato per la prima volta dal **17 agosto**.

### Cosa resta al proprietario, su questo

**La chiave NVIDIA non riesce a usare il proprio catalogo.** Con due modelli diversi, presi
dall'elenco che l'API stessa restituisce per quella chiave, la risposta è la stessa:

```
HTTP 404 — «Function '…': Not found for account 'Bh1HDHM770Pk0W…'»
```

Non è il nome del modello: è l'account. Serve una verifica sul profilo build.nvidia.com (crediti,
tier, modelli abilitati) che non si può fare da qui. **Finché resta così, NVIDIA è fuori dal comitato
e va tolta anche dalla testa della catena di failover** — oggi è ancora l'«AI attiva», quindi ogni
chiamata del layer paga un 404 prima di arrivare a chi risponde.

---

## 4-ter. K53 — le due rettifiche del pomeriggio, e il primo verdetto del comitato

### Il 404 non è una diagnosi

K52, scritto la mattina, dichiarava il guasto alla **prima** risposta 404/410. Campione controllato
del pomeriggio su NVIDIA — dieci tentativi identici, stesso modello, stessa chiave:

```
6 successi · 4 volte «HTTP 404 Function '…': Not found for account '…'»
il 404 tornava in 753 ms → è l'instradamento che rifiuta, non il modello che manca
```

Con la regola della mattina la piattaforma avrebbe emesso una notifica critica «il modello non esiste
più» **ogni due giri, su un provider che funziona**. L'allarme che grida sempre è l'allarme che non
si guarda: avrei sostituito il silenzio con il rumore, che è lo stesso difetto in un'altra forma.

Il 410 «end of life» resta inequivocabile. Il 404 no. Quindi la classificazione descrive **la
risposta**; il giudizio «configurazione stantia» richiede la **ripetizione** e vive dove può
contarla: `ConfermaGuastoGiri = 3`, con la serie per provider azzerata da ogni voto valido e **non
toccata** da un'astensione d'altra causa — un timeout non è prova né a favore né contro. Stessa
isteresi di K42 e K46. Col tick a 15 minuti la conferma arriva in 45 minuti; il caso vero, Groq
morto, durava **sedici giorni**.

Il pannello ora distingue «2 di 3 giri» in grigio da «guasto confermato» in rosso, e il pulsante di
rimedio compare solo alla conferma.

### La migrazione che ha tenuto ferma la Regina

`AddDecisionOutcome` applicata al database **vivo** mentre il codice di K51 stava in un ramo non
fuso. Il guscio in esecuzione — compilato da master — non conosce la proprietà `Outcome`, quindi la
sua INSERT non la elenca; senza default la colonna prende NULL:

```
23502: il valore nullo nella colonna "Outcome" viola il vincolo non nullo
```

Journal fermo alla riga **137 delle 07:46 UTC**, tick abortito, nessuno schieramento e nessun ritiro
per **cinque ore e mezza**. È K45 preso dall'altro verso: là la colonna era troppo stretta per la
stringa, qui troppo severa per il binario che scrive.

> **La regola che ne discende, e vale per ogni migrazione futura.** Con le migrazioni applicate
> all'avvio e un database condiviso, fra l'istante in cui lo schema cambia e quello in cui il codice
> nuovo entra in servizio c'è **sempre** una finestra in cui il binario vecchio scrive sullo schema
> nuovo. Una colonna che nasce obbligatoria dev'essere scrivibile **anche da chi non sa che esiste**
> — cioè avere un default — oppure nascere annullabile e diventare obbligatoria in una seconda
> migrazione, dopo il rilascio.

### E poi, alle 14:04:48 UTC

```
Id 143 · Assign · corsia 7 · Source=committee · Applied=TRUE
«[J14] Scelto dal comitato fra 5 candidati grigi: Supertrend TRX/USDT 4h»

Groq        0,92  → «il più alto Sharpe (3,05), ~3,3 trade/mese, timeframe 4h»
HuggingFace 0,80  → stesso candidato, stesso ragionamento nel merito
Gemini      astenuto (timeout)
```

`Source = committee`, non `default:...`. **È la prima volta che il comitato AI decide davvero uno
schieramento**: fino a ieri ogni riga diceva che aveva deciso la regola deterministica, e per sedici
giorni non poteva essere altrimenti.

### Il pilota automatico riportava NVIDIA sul modello morto

`ModelAutoSelector` sceglie per euristica sul **nome**, e il nome non dice se l'account può invocare
quel modello. Per NVIDIA la regola è «llama + instruct + 70b», e nel catalogo del 2026-09-02 esiste
**un solo** candidato che la soddisfa: `nvidia/llama-3.1-nemotron-70b-instruct` — che risponde 404.
Ogni apertura del pannello riproponeva il modello morto, **annunciandolo come una riparazione**.

Ora la prova precede l'indovinello: `LlmUsageRecords` registra solo le chiamate riuscite, quindi un
modello che ha prodotto token è la prova che quell'account può invocarlo. Senza storico si torna
alle euristiche — mai peggio di prima.

E il filtro di forma diceva `"embedding"`, mentre i nomi veri sono `embed-qa`, `nv-embedqa`,
`arctic-embed`, `nemotron-3-embed`: **non ne prendeva uno**. Un filtro scritto sulla parola del
dominio invece che sui nomi che esistono.

---

## 4-quater. La caccia n° 8, e perché la domanda era sbagliata — compresa la mia metrica

Il proprietario ha chiesto il quadro prima di decidere. Il quadro **demolisce la premessa**, la mia
inclusa.

### La config 8 è già ferma, e non è stata giudicata: è stata dimenticata

**Ultimo run: 2026-08-20**, tredici giorni fa. Metterla in sonno libera **0 ore su 48,7 al mese.**

Il commit `932eb21` del **2026-08-20** — lo stesso giorno del suo ultimo run — aggiunge a
`HoldoutValidationStage.ValidateInput` un rifiuto degli universi a timeframe misti, col commento
«*le campagne reali sono già tutte a timeframe singolo, quindi nessun run esistente cambia*».
**È falso: 28 run su 29 della config 8 sono misti.** E in database non c'è un solo run `Failed`
della 8 — non ha mai sbattuto contro il gate, ha semplicemente **smesso di essere invocata**
(`ScheduleEnabled = false`, mentre le altre quattro girano su `Campaign`/`Event`). In parallelo
`FleetStateReader` e `ResearchPageService` escludono i run misti: tutte le sue 1.625 righe sono già
fuori da `/research` e dalla coda della flotta.

### E la metrica di K50 non regge il confronto — l'ho costruita io stamattina

| l'obiezione | il numero |
|---|---|
| dipende dal denominatore | stessa config, stesso motore, stesso universo: cfg 17 fino al 20/08 → resa **0,477**; cfg 17 nei soli 21-22/08 → resa **7,250**. Tasso di grigi piatto (14,6 % → 13,3 %), resa **×15,2** |
| dipende dall'ampiezza del paniere | **86,6 %** delle chiavi grigie di cfg 17 (71 su 82) è su simboli che la config 8 **non caccia**. Sulle 10 majors condivise cfg 17 scende a 11 chiavi |
| dipende da quale motore ha eseguito | il motore walk-forward è stato **sostituito il 2026-08-23**. Copertura post-fix: cfg 20 = 92 % · cfg 19 = 90 % · cfg 18 = 30 % · cfg 17 = 23 % · **cfg 8 = 0 %** |
| **premia ciò che dovrebbe punire** | la resa è correlata **ρ = 0,90 (Spearman) col PBO medio** dei candidati che premia. Le due configurazioni con la resa più alta hanno PBO medio **sopra `maxPbo = 0,5`**: cfg 20 = 0,726 e cfg 17 = 0,603, contro **cfg 8 = 0,319** |

A motore uguale le rese diventano `17 = 0,476 · 20 = 0,200 · 18 = 0,115 · 8 = 0,034`: il divario con
la 18 passa da 12× a **3,4×**, non 22×.

E il confronto testa a testa, stesso terreno: **sull'1h la config 18 non ha prodotto un solo
candidato con Sharpe holdout ≥ 0,5 in 639 righe; la config 8 ne ha prodotti 28 su 838.**

> Resta però un fatto a carico della 8, e sopravvive a ogni obiezione: il **p95** del suo Sharpe
> holdout è **0,483**, cioè sotto la sua stessa soglia di 0,5 (0,026 deduplicando per chiave). Nella
> forma pre-fix non poteva passare. Ma è un giudizio su un motore che la piattaforma ha smesso di
> considerare valido tre giorni dopo.

### Cosa si perderebbe archiviandola, misurato

- **27 delle 49 triple** strategia × simbolo × timeframe che valutava **non sono valutate da nessuna
  configurazione attiva** dopo il 2026-08-23. Post-fix la cfg 18 valuta solo Composite,
  EventTrigger, RegimeConditional e Ml: **zero righe** di Supertrend, EmaCross, MacdTrend, Momentum,
  DonchianBreakout, Stochastic. **E cinque delle sette gambe oggi schierate appartengono proprio a
  quelle famiglie.**
- L'unico **banco di regressione deterministico** della macchina di ricerca: 58 chiavi con Sharpe
  holdout bit-identico su fino a 28 notti. Nessun'altra lo dà — cfg 17 ha l'11 % di chiavi
  deterministiche, cfg 18 il 10 %, **cfg 19 e 20 zero**.

### Il vero consumo è la config 19

**30,19 h/mese = 62 % del budget**, mediana 43,7 minuti a run, 5 chiavi grigie, **zero gambe
schierate**, e **11,2 valori distinti per la stessa ipotesi** in circa dodici notti. Qualunque cosa
si decida sulla 8 non sposta il budget; la 19 sì.

---

## 4-quinquies. K54 — le corsie portano il MASSIMO di misure ripetute

Il rilievo è emerso dall'analisi della caccia 8 e l'ho verificato da solo, **confrontando l'ipotesi
esatta** (strategia + simbolo + timeframe + *parametri*), non la sola terna — la scorciatoia che oggi
mi aveva già ingannato.

| corsia | ipotesi | `expectedSharpe` | misure | mediana | massimo | valori distinti |
|---|---|---|---|---|---|---|
| 6 | GridMeanReversion DOGE 15m | **1,875** | 12 | 0,498 | **1,875** | **12 su 12** |
| 2 | Supertrend ADA 4h | **3,195** | 45 | 3,195 | **3,195** | 4 |
| 2 | Composite ADA 4h | **1,062** | 15 | −2,482 | **1,062** | 3 |
| 4 | Composite XLM 4h | **1,291** | 48 | 1,243 | **1,291** | 2 |
| 7 | Supertrend TRX 4h | **3,053** | 5 | 2,734 | **3,053** | **5 su 5** |
| 3 | MacdTrend AAVE 4h | 3,961 | 3 | 4,123 | 4,137 | 3 — *porta il **minimo*** |
| 5 | GridMeanReversion UNI 4h | 1,187 | 44 | 1,187 | 1,187 | 1 — *deterministica* |

**Cinque gambe su sette portano esattamente il massimo** delle misure ripetute della stessa ipotesi.
Il caso peggiore è la corsia 6: dodici misure, **dodici valori diversi**, e la gamba porta 1,875
contro una mediana di 0,498 — **3,8 volte**.

**Perché succede, e perché non è un dettaglio.** La fascia grigia si ordina per Sharpe: fra molte
misure rumorose della stessa ipotesi, quella che viene proposta è, per costruzione, la notte in cui
il rumore ha spinto più in alto. È selezione del massimo, la trappola che questo progetto ha già
pagato altrove.

**La conseguenza è sul ritiro.** Il criterio confronta lo Sharpe vivo con `expectedSharpe`: le corsie
vengono giudicate contro un numero preso dalla cima di una distribuzione rumorosa, quindi
«decadranno» anche se non è cambiato niente. Il ritiro per Sharpe, che oggi è irraggiungibile per
altre ragioni, sarebbe **sistematicamente ingiusto** appena diventasse raggiungibile.

> Da notare che **non è universale**: la corsia 3 porta il minimo e la corsia 5 è deterministica. La
> non-determinismo delle misure non è la regola dappertutto — ma dove c'è, lo schieramento prende
> sempre la cima.

---

### Cosa resta della Fase 4

| # | Cosa | perché non è un pomeriggio |
|---|---|---|
| K29 | **tuner dei parametri di caccia** — `topN`, ampiezza delle finestre, `confirmTopN` | ogni manopola cambia il numero di tentativi, quindi SR\*: va misurato con il suo nullo o è overfitting sull'overfitting |
| K30 | **l'AI dalla proposta al veto** | il prerequisito è riparato (K52) e il comitato torna a deliberare, ma **un pomeriggio di quorum non è una base**: prima di dargli il potere di proporre va guardato quanto spesso ci riesce davvero, ora che può |
| K31 | **il post-mortem che rientra nella caccia** — l'anello 11, che «non esiste» | è il ritorno del verdetto del forward test dentro la prossima ricerca: serve prima che i forward test producano trade veri, e oggi ne hanno prodotto **uno** |
| K32 | **generatore di candidati** | il PRD stesso lo subordina a K26, che è rimandato con la sua ragione |

> **K31 in particolare va fatto dopo, non prima**: chiudere l'anello fra esecuzione e ricerca su un
> campione di **un trade di forward test** significherebbe insegnare alla caccia una lezione che
> nessuno ha verificato. Il criterio di sufficienza è lo stesso di K16: ~30 trade vivi cumulati,
> circa due mesi su quattro corsie — e da K41 quel conteggio è finalmente misurabile.

---

## 5. Le decisioni del proprietario — prese il 2026-09-02

| | decisione | esito |
|---|---|---|
| **NVIDIA** | chiave rigenerata dal proprietario | ✅ **funziona**. Il 404 era per-modello, non per-account: sono deployment ritirati che restano in catalogo. Modello attivo `nvidia/nemotron-3-super-120b-a12b`, 12 chiamate riuscite. Il 404 resta intermittente al ~40%, e ora l'isteresi lo assorbe invece di gridare |
| **`Fleet:MaxGreyLanes`** | 4 → **6** | ✅ la corsia 7 è ripartita alle 14:04 UTC con un candidato scelto **dal comitato**, e resta uno slot di riserva |
| **`AutoReapply:MaxGreyLegs`** | 0 → **2** | ✅ sblocca 3 run su 18. ⚠️ resta vero, e non è coperto: quelle gambe grigie sulle corsie d'impronta **non entrano** nel tetto `MaxGreyLanes` della flotta — sono due tetti scollegati sullo stesso rischio, e la superficie che lo dichiara non esiste ancora |
| **Config 8** | «prima guardiamola» | ✅ § 4-quater: **è già ferma da 13 giorni** e non è stata giudicata, è stata *dimenticata* da un gate introdotto con un commento falso. Metterla in sonno libera **0 ore su 48,7**. E la metrica con cui l'avevo condannata non regge |

### Cosa resta aperto, in ordine di gravità

1. **K54 — le corsie portano il massimo di misure ripetute** (§ 4-quinquies). Cinque gambe su sette.
   Il ritiro per Sharpe le giudicherebbe contro un numero preso dalla cima del rumore: sarebbe
   sistematicamente ingiusto appena diventasse raggiungibile.
2. **La config 19 consuma il 62 % del budget** (30,19 h/mese) e ha schierato **zero** gambe, dando
   11,2 valori distinti alla stessa ipotesi. È la decisione che sposta il budget, non la 8.
3. **Il doppio tetto scollegato** introdotto dalla decisione su `MaxGreyLegs`: il rischio «gambe di
   fascia grigia in forward test» si accumula su due percorsi contati separatamente, e nessuna
   superficie li somma.
4. **Un gate introdotto con un commento falso** ha spento una configurazione senza che nessuno se ne
   accorgesse (`932eb21`, § 4-quater). Non esiste una superficie che dica «questa caccia non gira
   più»: il difetto di forma è lo stesso di K46, in un altro posto.
5. **Lo snapshot delle migrazioni è alla deriva**: non contiene `OrchestratorDecisions.Outcome`, e un
   `migrations add` rigenera migrazioni sbagliate (vuole ricreare `FleetLaneObservations` e
   riaggiungere `MixedTimeframeUniverse`). Precede K51 e va riallineato prima della prossima
   migrazione vera.
6. **Gemini è il votante lento**: si astiene per timeout anche a 50 s, ed è quello che dà le
   motivazioni più argomentate. Alzare ancora il timeout allunga ogni tick della flotta.
