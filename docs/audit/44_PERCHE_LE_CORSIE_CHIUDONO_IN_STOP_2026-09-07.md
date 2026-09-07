# Perché le corsie chiudono in stop, e perché sembravano attive mentre erano ferme

**2026-09-07** · seguito operativo dell'audit 43 · corsie 0-7, tutte Paper · nessun percorso Live toccato

---

## 0. Le due domande del proprietario, e le due risposte

> «Mi sembra che ci siano corsie che risultano attive anche se sono ferme, inoltre avrei la
> necessità di un sistema che studi tutte le strategie sulle varie corsie e capisca come auto
> correggersi e migliorarsi poiché ho notato che molte operazioni si chiudono con uno stop loss
> invece che con un take profit, dovrei capire perché.»

| Domanda | Risposta | Stato |
|---|---|---|
| Corsie accese che non fanno niente | Vero, e peggio dell'etichetta: **non decidevano affatto** | corretto, PR #147 |
| Perché tanti stop | **Aritmetica del bracket**, non guasti né segnale | misurato, e reso permanente |

---

## 1. «Attive ma ferme»: era vero, ed era un piano più sotto di un'etichetta

Il buffer delle candele di `TradingEngine` vive **solo in memoria** e lo riempie unicamente
`ProcessCandleAsync`. Dopo ogni riavvio del processo la corsia riprendeva dal segnalibro, marcava a
mercato e onorava gli stop — `ApplyProtectiveExitsAsync` sta prima del cancello — ma il
`if (closes.Count >= 5)` le impediva di **interrogare una sola strategia** finché non aveva
accumulato barre nuove. E cinque non bastano: gli indicatori veri ne vogliono 14 (Supertrend),
circa 29 (MacdTrend), 60 (GridMeanReversion con ancora a 60). Su una corsia a 4 ore sono **dieci
giorni di processo vivo senza interruzioni**, mentre il pod si rischiera a ogni merge.

Misura del 2026-09-06:

| Fatto | Valore |
|---|---|
| Corsie con meno di 5 barre in memoria | 6 su 8 |
| Ordini di flotta nelle 40 ore precedenti | 0 |
| Corsie senza un solo `TradeRecord` | 2, 3, 4, 5 (da 14, 7, 5, 14 giorni) |
| Superfici che lo mostravano | nessuna |

Nessuna superficie poteva vederlo **per costruzione**: i chip e la Home leggono `IsRunning`, il
battito di `/trading` conta le candele **consegnate**, e la sonda E6 misura lo stesso campo — che si
chiama «ultima candela valutata» ma è scritto **prima** del cancello.

**Correzione (PR #147, `809ebf8`)**: il buffer si ricostruisce dall'archivio all'avvio, fermandosi
al segnalibro per non spostare la frontiera anti-replay; il motore espone `BufferedBars` e
`LastStrategyEvaluationUtc` (decisione, non consegna); `/trading` mostra un badge giallo «STRATEGIE
NON VALUTATE»; `LaneInvariantWatchdog` grida se una corsia accesa non decide dopo la grazia.

---

## 2. Perché tanti stop: le tre ipotesi, e quale regge

Il proprietario ne aveva enumerate tre. Sono state misurate separatamente su 60 giorni di storia.

### 2.1 «Il server è andato giù» — **scartata**

| Controllo | Esito |
|---|---|
| Stop entro 60 min da un crash o da un rilascio | **0 su 82** (attesi 9,9% per puro caso) |
| Stop con un buco di candele nelle 3 barre precedenti | **0 su 119** |
| Prezzo d'uscita dentro il minimo-massimo della barra | **119 su 119** |

### 2.2 «Gli stop e i take sono messi male» — **vero, ma non nel senso atteso**

`ExcursionAnalyzer.Aggregate` scarta le operazioni perdenti (`if (!s.FavorableOutcome) continue;`) e
poi prende la MAE per lo stop e la MFE per il target **allo stesso percentile**. È una selezione da
sopravvivenza, e produce meccanicamente un target più lontano dello stop.

L'identità che lo dimostra: per un long l'escursione avversa è il drawdown e la favorevole è il
runup; per uno short i ruoli si scambiano. Sulle stesse candele la distribuzione della MAE dei long
**è** quella della MFE degli short. Siccome `AutoBracket` media i due lati, **senza** il filtro stop
e take escono *identici*; **con** il filtro il target si allontana. Inchiodato da
`SopravvivenzaDelBracketTests` (uguaglianza al centesimo).

### 2.3 «Il segnale entra male» — **non distinguibile: l'osservato è il nominale**

Corsa a primo tocco sulle otto serie vere, entrate a ogni barra e su entrambi i lati, orizzonte 10
barre, regola d'uscita del motore:

| Corsia | Rapporto target/stop | Stop previsti |
|---|---|---|
| 0 (ADA 5m) | 2,66 | 85,1% |
| 7 (TRX 4h) | 4,19 | 93,6% |

Osservato in esercizio: **85%** (119 stop contro 21 target). È **dentro la banda nominale**. Il
segnale non sta aggiungendo nulla di misurabile su questa metrica, e non c'è niente da correggere:
l'85% è il numero che quel rapporto produce da solo.

---

## 3. Correggere il bracket non farebbe guadagnare

Valore atteso **onesto** per operazione, contando anche il 51-90% di finestre che non toccano
nessuna barriera: da **−0,093%** a **−0,205%**, cioè in pratica la commissione di andata e ritorno
(0,20 punti). La geometria del bracket cambia la **forma** dell'esito — quante volte si vince contro
quanto si vince — non la media.

> Scartare le finestre senza barriera è la trappola: sulla stessa serie porta il valore atteso da
> −0,2% a −1,4%, un ordine di grandezza, e farebbe sembrare rovinoso qualunque bracket.

Per questo `winnersOnly` nasce `true` e **il default non è cambiato**. Il parametro esiste per
misurare, non per correggere di nascosto i parametri di rischio delle corsie.

---

## 4. Il sistema che studia le corsie e dice come stanno

Tre pezzi, tutti *advisory-only*: nessuno scrive configurazioni o parametri di rischio.

**`BracketRaceAnalyzer`** (puro, nessun I/O) fa correre stop contro target sulle candele vere,
riusando `ProtectiveExitEvaluator.EvaluateStopAndTarget` — la funzione d'uscita **del motore**, non
un suo sosia. Restituisce stop-primo, target-primo, nessuna barriera, valore atteso onesto, barre
mediane a ciascuna barriera, e i **pareggi di barra** (la barra che ha toccato entrambi i livelli:
il motore assegna sempre lo stop, quindi dove i pareggi sono tanti la misura è sistematicamente
pessimista, e va detto).

**`BracketDiagnosisService`** mette il nominale accanto all'osservato per ogni corsia. Quattro
verdetti, tutti stati del mondo e nessuno un errore:

| Verdetto | Significato |
|---|---|
| `NonMisurabile` | gamba senza protezioni, o serie troppo corta — col motivo scritto |
| `NonGiudicabile` | meno di 20 uscite vive: **non si imputa**, si dice quante mancano e in quanti mesi |
| `ComePrevisto` | l'osservato coincide col nominale: gli stop non hanno altra causa |
| `ScartoDalPrevisto` | oltre 15 punti di scarto, e la notifica dice da che parte pende |

Ogni conteggio dichiara i propri scarti: repliche della stessa operazione, righe fabbricate dal
replay (`RecordedAtUtc` − `ClosedAtUtc` oltre tre barre più mezz'ora), righe senza ora di parete che
**non si possono giudicare** né in un senso né nell'altro. Sul campione reale: delle 401 righe, 371
non hanno ora di parete, 25 sono replay, 5 sono Live; delle 119 uscite in stop, **60 non sono mai
avvenute**.

**`BracketDiagnosisWorker`** rimisura ogni 24 ore e parla **solo ai cambiamenti** di verdetto: quando
una corsia esce dal previsto, e quando per la prima volta accumula abbastanza uscite per essere
giudicata. Il silenzio è l'esito normale, ed è deliberato: questa piattaforma ha già cinque
meccanismi che misurano bene e tacciono, ma un avviso che suona a ogni giro è ugualmente inutile.

Superfici: la tabella per corsia e le geometrie alternative stanno in `/trading`; le soglie e
l'interruttore del guardiano in `/admin/protections`, scheda «Diagnosi del bracket».

---

## 5. Quanto tempo serve prima di poter dire qualcosa sui soldi

Al ritmo di operazioni dichiarato dalle gambe, ogni corsia impiega **da 7 a 18 mesi** ad accumulare
un campione che regga un verdetto sul forward test. La corsa a primo tocco è l'**unico** segnale
disponibile su scala settimanale, perché ha bisogno soltanto di candele: nessuna operazione, nessuna
attesa.

---

## 6. Aperto

1. **La calibrazione del bracket resta com'è.** Cambiare il default sposterebbe la forma degli esiti
   senza spostarne la media, e va deciso dal proprietario, non da un automatismo.
2. **Il lavoro `deploy` della plancia è spento** in `~/.procione/lavori.json`: il motore resta
   indietro rispetto a master finché non viene riacceso. Non l'ho riacceso io.
3. **Il tetto aggregato del carry** (6 × 50% = 300% per lato) resta senza vincolo, come nell'audit 43.
4. `MarketDataSyncWorkerTests.RunCycle_ChiamataAppesa_SiFermaAlBudgetInveceCheMai` è fragile a
   tempo su runner carico: da rendere deterministico.
