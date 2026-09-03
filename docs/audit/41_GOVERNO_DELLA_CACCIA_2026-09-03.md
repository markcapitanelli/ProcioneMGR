# Il governo della caccia

> **Data:** 2026-09-03 · Nasce da tre richieste del proprietario: tagliare sulla config 18, far
> girare la caccia **su tutte** le configurazioni e non su una sola campagna, e far **proporre
> dall'AI** cacce nuove sensate, organizzate dalla Regina.
>
> Due delle mie premesse sono cadute misurandole, e la seconda è caduta *dopo* che avevo già
> proposto il piano.

---

## 1. Che cosa c'era davvero

**Una sola campagna**, con **4 configurazioni** in rotazione — su **13 attive**. Nove non giravano
mai in automatico:

| stato | quali | perché |
|---|---|---|
| 4 a timeframe misto | 8, 10, 12, 13 | respinte dal gate di `932eb21` |
| 5 sane, mai in rotazione | 9 (1d), 11 (1h, **mai girata**), 14 (15m ALT), 15 (30m), 16 (1d largo) | nessuno le aveva aggiunte |

---

## 2. Fatto: la rotazione passa da 4 a 9

Le cinque sane entrano con la cadenza propria di K56, scelta per **non alzare il monte-ore**:

| cfg | timeframe | min/run misurati | cadenza |
|---|---|---|---|
| 9 | 1d, 10 serie | 0,6 | 24h |
| 11 | 1h, 22 serie | **mai girata** | 48h |
| 14 | 15m, 5 serie | 5,0 | 24h |
| 15 | 30m, 5 serie | 4,2 | 24h |
| 16 | 1d, 34 serie | 2,1 | 24h |

Con i tagli su 18 e 19 (entrambe a 48h) il monte-ore passa da **~68 a ~35 h/mese**: più del doppio
delle cacce, metà del costo.

---

## 3. NON fatto: spezzare le miste — e la ragione è un numero

Il piano diceva «spezzare le 4 miste in una per timeframe». **Verificato prima di eseguirlo, non
aggiungono nulla:**

- **10 e 12 hanno esattamente l'universo della 8**: 20 celle su 20 in comune. Sono le varianti A/B
  di un esperimento di luglio.
- Le **5 celle della 13** sono già coperte da 19 e 20.
- Le **20 celle della 8** sono già coperte da 17 (4h) e 18 (1h).

Spezzarle avrebbe aggiunto **tentativi senza copertura nuova** — il danno esatto che il piano voleva
evitare.

---

## 4. E l'argomento con cui avevo motivato il piano era falso

Avevo scritto: *«l'1h caccia solo Composite, EventTrigger, Ml e RegimeConditional; le famiglie a
indicatore classico stanno solo a 4h — ed è un buco di copertura. E cinque delle sette gambe
schierate appartengono proprio a quelle famiglie.»*

I numeri sono giusti. **La conclusione no.**

**Tutte le configurazioni hanno la stessa identica catena di diciotto fasi** — verificato su 8, 17,
18 e 19, sequenza per sequenza. Il pool di strategie non è per-configurazione: quelle famiglie
**vengono cercate ovunque**. Che compaiano solo a 4h non è ciò che la caccia guarda, è **ciò che
sopravvive allo screening** e arriva a `HoldoutValidation`, che è lo stadio dove i candidati vengono
registrati.

**È un risultato, non una lacuna.** Trattarlo come lacuna avrebbe fabbricato una caccia per cercare
qualcosa che è già stato cercato e non ha retto — e ogni tentativo in più alza SR\*.

---

## 5. Il buco vero: 97 celle che si pagano e nessuno guarda

| timeframe | serie seguite | cacciate | **mai cacciate** |
|---|---|---|---|
| 15m | 44 | 10 | **34** |
| 5m | 30 | 10 | **20** |
| 4h | 49 | 33 | **16** |
| 1d | 49 | 33 | **16** |
| 1h | 49 | 39 | **10** |
| 1m | 1 | 0 | 1 |

**222 celle seguite, 125 cacciate.** Ognuna delle 97 restanti costa ingestione a ogni giro del
worker, e nessuna superficie lo diceva.

**K58** la misura e la mostra in `/pipeline`. È la risposta alla domanda «che tipo di caccia
aggiungere»: il buco più grande, senza inventare niente.

---

## 6. Il tetto è in ORE, non in numero di cacce (K59)

Contare le cacce non dice niente: la mediana per run va da **0,6 minuti** (cfg 9) a **43,8**
(cfg 19) — **settanta volte**. Un tetto «al massimo N cacce» tratterebbe come uguali due cose che
non lo sono: è lo stesso errore per cui K54b ha dovuto mettere il costo accanto alla resa.

Si rallenta partendo da chi rende meno **per ora** — non per run, perché il numero di run al
denominatore è una scelta di pianificazione e non una proprietà della caccia. E **chi non ha un costo
misurato non si tocca**: rallentare una caccia di cui non si conosce il prezzo non è una decisione,
è un tiro a indovinare.

Due limiti dichiarati: non si rallenta **oltre le due settimane** (una caccia che non raggiunge i 12
run di K50 non si può giudicare, e tenerla accesa senza poterne dire nulla è peggio che spegnerla),
e **senza tetto impostato non si tocca niente**.

---

## 7. Il proponitore a menù chiuso (K60)

Il **codice** costruisce le proposte, già valide per costruzione:
- solo serie **abilitate** e **mai cacciate**;
- forma e finestre **copiate** da una configurazione dello stesso timeframe **che gira davvero** —
  se non esiste, la proposta non si fa (proporre una forma mai provata a quel ritmo è indovinare);
- costo **stimato** scalando la durata misurata del modello sul numero di serie, e **dichiarato come
  stima**;
- cadenza scelta come la più fitta che entra nel budget residuo — e se non entra nemmeno al ritmo più
  lento, **la proposta non si fa**.

L'**AI sceglie fra quelle e argomenta**, esattamente come il comitato AF3 sceglie fra i candidati
grigi: menù chiuso, contratto JSON severo, quorum, default deterministico. Comitato assente o senza
quorum ⇒ decide la regola, e lo dice.

### Il vincolo che governa tutto il progetto

**Il gate del DSR deflaziona per i tentativi del proprio run** (`trialsExplored`) **e non vede le
altre cacce.** Aggiungerne non rende il gate più severo: **nessun freno scatta da solo**, e la
disciplina dev'essere esplicita.

Il controllo che invece *scala* col numero di cacce è **K57** — «sopravvive alla rimisurazione?» —
che guarda **fra** i run invece che dentro uno. Senza K57, aggiungere cacce sarebbe solo comprare più
occasioni di essere fortunati.

Per la stessa ragione l'universo di una proposta resta **piccolo** (10 serie): un universo grande
moltiplica i tentativi *dentro* il run, cioè alza SR\* — l'unico posto dove la molteplicità è davvero
contata. È il contrario dell'istinto «più serie, più possibilità».

**Nessuna proposta si adotta da sola**: una caccia nuova costa ore e aggiunge tentativi, ed entrambe
sono decisioni del proprietario.

---

## 8. I quattro punti rimasti — tre chiusi il 2026-09-03, uno in attesa di misura

> **Rivisto la sera stessa** ([doc 42](42_REVISIONE_FILONE_K_2026-09-03.md)): la prima versione del
> guardiano proiettava le ore dalle sole durate osservate (con `BudgetAutoApply` le cadenze sarebbero
> raddoppiate a ogni giro fino a 336 h), «Guarda adesso» scriveva, senza tetto il proponitore riceveva
> un residuo di 4,5·10³⁰⁷ ore e la cadenza non valeva per i run a cron. Corretto: proiezione al ritmo
> in vigore (`HuntBudget.ProiettaOreAlMese`), `MisuraAsync` per i pulsanti, residuo `null` = «senza
> tetto» dichiarato, cadenza onorata anche dallo scheduler, un giro per volta.

### 1. La cfg 11 non ha mai girato ⏳ *si misura da sola*

Il suo costo resta una stima (~10 minuti, scalati da cfg 18 sul numero di serie) finché non gira. È
in rotazione a 48h, e la campagna si riarma a `RearmHours = 13` dall'ultimo run: la misura arriva al
primo giro utile. Forzarla dall'interfaccia richiederebbe le credenziali del proprietario, e non è
un lavoro che valga una scorciatoia.

### 2. Le serie a 30m ✅ *esistevano già, ferme da 39 giorni*

`OhlcvData` conteneva **129.605 candele a 30m** — esattamente i cinque simboli che la cfg 15 vuole
(BTC, ETH, SOL, LINK, LTC), **25.921 candele ciascuno** — ma l'ultima era del **2026-07-26**:
trentanove giorni. Nessuna riga in `TrackedSeries`, quindi nessuno le aggiornava.

Le cinque righe sono state aggiunte e abilitate: **+5 celle su 222** (+2,3 % di ingestione), con lo
storico già a terra. La cfg 15 è rientrata in rotazione, che ora conta **nove configurazioni**.

### 3. Il tetto in ore ✅ *agganciato, e propone*

`HuntBudgetWorker` gira ogni `Campaign:BudgetTickMinutes` (default 60), misura il costo di ogni
configurazione attiva dalle **durate osservate**, e propone che cosa rallentare per stare dentro
`Campaign:MonthlyHourBudget`.

**Non scrive**: `Campaign:BudgetAutoApply` nasce **spento**, ed è la stessa scelta già fatta per
`GreyAutoDeploy` e per il sonno di una caccia. Con lo spento il worker misura, dichiara e notifica —
che è ciò che serve per guardarlo girare prima di dargli il potere di riscrivere le cadenze.

> **Un difetto trovato dal suo stesso test.** La prima versione ordinava i tagli per sola resa
> crescente — e le quattro configurazioni entrate in rotazione oggi hanno resa **0** perché non hanno
> ancora girato, non perché siano sterili. Finivano **prime** nella fila: la caccia appena aggiunta
> sarebbe stata la prima a essere frenata, senza aver potuto dimostrare nulla. Ora chi non ha
> abbastanza run (`HuntYield.MinRunsForVerdict`) va **in fondo**, e si tocca solo se rallentare le
> giudicabili non basta.

### 4. Il proponitore ✅ *ha la sua superficie*

Due pulsanti in `/pipeline`: **«Guarda adesso»** (il budget, con le proposte e il loro risparmio) e
**«Proponi una caccia»** (il menù dei buchi, la scelta del comitato e la sua motivazione, con le
alternative scartate in un dettaglio richiudibile).

Le ore residue passano al proponitore: una proposta che non entra nel budget **non si fa**. E la
riga finale del riquadro lo dice a chiare lettere — *nessuna proposta si adotta da sola*.

E le tre chiavi nuove hanno la loro manopola in `/admin/autonomy`, perché il guardiano
`ConfigurationKeyUiCoverageTests` non lascia passare una chiave senza pannello: è la regola del
2026-08-09, e ha fatto rosso al primo giro su `BudgetTickMinutes`.

---

## 9. Cosa resta davvero

1. **Il tetto non è impostato** (`MonthlyHourBudget = 0`): il consumo misurato è di **~32 ore/mese**
   su nove cacce, ed è il numero da cui scegliere. Finché è zero il pannello lo dice — «il consumo
   non è governato da niente» — invece di tacere.
2. **Il proponitore non crea la configurazione**: mostra la proposta col suo prezzo, e la si crea a
   mano da «+ Nuova». Adottarla da solo significherebbe spendere ore e aggiungere tentativi senza
   che nessuno abbia detto sì.
3. **Il DSR non è mai stato raggiunto**: 0 righe su 711, massimo 0,817 contro 0,95. Finché è così il
   forward test resta l'unico giudice.
