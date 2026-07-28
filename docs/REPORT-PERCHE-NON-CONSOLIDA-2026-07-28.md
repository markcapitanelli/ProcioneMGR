# Perché i candidati non consolidano mai (2026-07-28)

*Risponde a un'osservazione del proprietario: «di candidati se ne trovano un buon numero, ma non
raggiungono mai la consolidazione — capire perché, e se è effettivamente corretto».*

**La risposta è doppia, e le due metà portano a decisioni opposte.** Il 90% dei candidati muore
perché perde davvero, e lì i gate hanno ragione. Ma i pochi che guadagnano incontrano poi un gate
che, sull'holdout attuale di quattro mesi, è **insuperabile per aritmetica**: non è severo, è mal
dimensionato rispetto alla finestra di dati.

---

## 1. L'imbuto vero, dai dati e non dai ricordi

Ricostruito dagli artefatti `ValidatedCandidates` di 50 run di pipeline: **3.472 candidati validati**.

| Dove muoiono | Quanti | Sharpe holdout medio | Trade medi | Rendimento medio |
|---|---:|---:|---:|---:|
| Sharpe holdout sotto soglia | **3.141** (90,5%) | **−1,87** | 23,6 | −1,79% |
| Troppo pochi trade in holdout | **232** (6,7%) | **+1,01** | 6,1 | +1,90% |
| Deflated Sharpe (anti-overfitting) | **61** (1,8%) | **+1,14** | 23,5 | +3,20% |
| Rischio Monte Carlo | 1 | — | — | — |
| Sopravvissuti | 37 (1,1%) | +1,19 | 26,1 | +3,44% |

Fino a oggi la piattaforma registrava solo `Candidates` e `Survivors`: 32 run, 2.049 candidati, zero
sopravvissuti, e **nessun modo di sapere quale gate li stesse uccidendo**. Sono tre diagnosi opposte,
e distinguerle era il primo passo. Ora l'imbuto è registrato a ogni run (`Rejected_*` nelle metriche
dell'esperimento).

### I 37 "sopravvissuti" non contano

Sono tutti datati **2 – 9 luglio**, e hanno `DeflatedSharpe` **nullo**: vengono da prima che il gate
anti-overfitting fosse cablato. Al netto dei duplicati fra run sono ~8 candidati distinti.
**Da quando il DSR è attivo, i sopravvissuti sono zero, sempre.**

---

## 2. Il 90% muore perché perde. I gate hanno ragione.

Non è un caso limite: è il grosso della distribuzione.

| Fascia di Sharpe holdout | Candidati |
|---|---:|
| sotto −1,0 | **1.941** (56%) |
| −1,0 … −0,5 | 272 |
| −0,5 … 0 | 216 |
| 0 … 0,25 | 624 |
| 0,25 … 0,50 (*appena sotto la soglia*) | **88** (2,5%) |
| 0,50 … 1,0 | 174 |
| 1,0 … 2,0 | 147 |
| oltre 2,0 | 10 |

Due letture che chiudono la questione «forse la soglia è troppo alta»:

- la fascia «appena sotto» contiene **88 candidati su 3.472**. Abbassare il gate da 0,50 a 0,25
  ammetterebbe il 2,5% in più, non un raccolto nascosto;
- **Sharpe medio in selezione +0,47 → in holdout −1,59**: una caduta di **2,06 punti**. È la firma
  dell'overfitting della selezione, non di un gate cattivo. La ricerca trova combinazioni che
  spiegano la finestra di selezione e collassano fuori.

**Quindi sì, è corretto**: quei candidati non sono edge non riconosciuti, sono rumore adattato.

---

## 3. Ma il gate anti-overfitting, su 4 mesi, non può confermare NESSUNO

Qui la risposta si ribalta. Per i ~100 candidati che *guadagnano davvero* in holdout, il DSR è
l'ultimo giudice — e va misurata la sua **potenza**: qual è l'edge più piccolo che riuscirebbe a
confermare, se fosse vero?

Il gate non è stato modellato, è stato **invertito numericamente** (`GatePowerAnalyzer`): si cerca lo
Sharpe che porta il DSR esattamente a 0,95.

**Anni di holdout necessari per confermare un edge** (tentativi *effettivi*, cioè dopo il
raggruppamento per correlazione che la piattaforma già applica):

| Sharpe vero | 2 tent. | 3 | 5 | 20 |
|---|---:|---:|---:|---:|
| 0,5 | 18,7 | 25,0 | 32,2 | 50,3 |
| **1,0** | **4,7** | **6,2** | **8,1** | 12,6 |
| 1,5 | 2,1 | 2,8 | 3,6 | 5,6 |
| 2,0 | 1,2 | 1,6 | 2,0 | 3,2 |
| 3,0 | 0,5 | 0,7 | 0,9 | 1,4 |

**L'holdout attuale è di 4 mesi, cioè 0,33 anni.** Anche uno Sharpe 3 — che al netto dei costi in
pratica non esiste — ne richiederebbe 0,5. Un edge realistico (Sharpe 1,0–1,5) ne richiede **da 2,8 a
8 anni**.

Il gate non è severo: sta facendo l'aritmetica giusta della conferma statistica. È la **finestra** a
essere troppo corta perché quell'aritmetica possa concludere qualcosa.

### Due cose che ho dovuto correggere per arrivarci

1. **La dispersione cross-trial non è un'assunzione.** Misurata sui 31 run reali vale **1,76**
   annualizzata (fra 0,99 e 2,57), contro **1,72** previsti dal nullo teorico su 123 osservazioni.
   Teoria e dati coincidono — il che rende la tabella sopra affidabile.
2. **Contano i tentativi EFFETTIVI, non i nominali.** La prima versione del controllo incrociato
   falliva: prediceva DSR ≈ 0,11 dove la piattaforma ne registra 0,31–0,77. Il motivo è
   `EffectiveTrials`, che collassa per correlazione i candidati che sono la stessa idea in salsa
   diversa: **165 candidati nominali diventano ~3 test effettivi**. Con quel valore il modello
   riproduce la banda osservata, ed è un test permanente.

Un dettaglio che sembra un errore e non lo è: le righe della prima tabella sono quasi identiche fra
timeframe. Su una finestra di **calendario** fissa, l'incertezza annualizzata dello Sharpe vale
~1/√anni **qualunque sia il timeframe** — campionare più fitto aggiunge punti alla stessa storia, non
informazione sul rendimento atteso. Quattro mesi sono quattro mesi.

---

## 4. La perdita evitabile: 232 candidati che guadagnano, buttati per il conteggio trade

Il gate dei **10 trade minimi in holdout** ha bocciato 232 candidati con Sharpe medio **+1,01** e
rendimento **+1,90%**. Sono il **70% di tutti quelli che superano il gate di Sharpe** (232 su 331).

Non è un giudizio sul mercato: è la finestra troppo corta per la loro frequenza. Il gate è un
surrogato di «abbastanza evidenza», ma è espresso in numero assoluto su una finestra fissa, quindi
penalizza le strategie lente indipendentemente dalla loro qualità.

Distribuzione per timeframe: 4h 8,9% · 1h 6,5% · 1d 3,7% · 15m 2,6% · **5m 0,0%**.

---

## 5. La ricerca non guarda dove il proprietario vuole operare

Preferenza dichiarata: **intraday / swing / operazioni veloci**. Distribuzione reale dei 3.472
candidati:

| Timeframe | Candidati |
|---|---:|
| 4h | 1.455 |
| 1h | 1.023 |
| 1d | 832 |
| 15m | 117 |
| 5m | **37** |

**Il 4,4% dello sforzo di ricerca sta sui timeframe che interessano.** E i pochi candidati 5m mai
prodotti (RsiOversold su LINK, Sharpe 1,70 con 65 trade; su FIL, 0,88 con 77 trade) hanno superato
tutto quello che c'era da superare, con abbondanza di trade.

---

## 6. Cosa farne

In ordine di rapporto fra valore e costo:

1. **Spostare la ricerca su 5m/15m.** Non perché lì l'edge sia maggiore — non lo sappiamo — ma perché
   è dove il proprietario vuole operare, dove il gate del conteggio trade non morde (0% di bocciature
   contro 8,9% sui 4h), e dove un anno di calendario produce abbastanza operazioni da rendere
   informativo il forward test.
2. **Allungare l'holdout, o smettere di aspettarsi che il DSR confermi.** Con 4 mesi il gate può solo
   respingere. Non va abbassato — va riconosciuto per quello che è: un filtro anti-illusione, non un
   promotore.
3. **Rendere il gate del conteggio trade relativo alla frequenza** invece che assoluto (es. «almeno N
   trade *attesi* dalla frequenza misurata in selezione»), oppure allungare l'holdout per le
   strategie lente. Oggi butta via il 70% di ciò che guadagna.
4. **Il forward test in Paper non è un ripiego: è l'unico giudice disponibile a questa scala di
   dati**, ed è immune al multiple testing per costruzione. La pratica attuale — tre corsie in Paper
   con candidati che non hanno passato l'anti-overfitting — è la scelta *giusta*, e ora si sa perché.

## 7. Come rifare le misure

```bash
dotnet run --project tools/PlatformExpand -c Release -- gatepower
```

L'imbuto si ricostruisce dagli artefatti `ValidatedCandidates` in `PipelineArtifacts`; dai run
successivi al 2026-07-28 è anche nelle metriche dell'esperimento come `Rejected_*`.
