# Dosaggio della volatilità — un risultato che NON ha replicato

> **Leggi prima questo.** La prima versione di questo documento presentava il dosaggio come "il primo
> risultato positivo" della ricerca, sulla base di un miglioramento di Sharpe da 0,12 a 0,43 su un
> paniere di 24 monete, validato con un controllo a esposizione costante e un walk-forward.
> **Quel risultato non ha replicato.** Ripetendo la stessa identica misura su 12 simboli — sia uno
> per uno, sia aggregati in paniere — l'effetto sparisce e spesso si inverte. La sezione
> "Non ha replicato" più sotto ha la prova; il resto del documento è conservato perché la sequenza
> di come ci si è arrivati è essa stessa il contenuto utile.
>
> **Conclusione operativa: il dosaggio non è una fonte affidabile di rendimento corretto per il
> rischio.** Le misure si contraddicono a seconda del periodo e della composizione (+0,31 / −0,22 /
> +0,33 su 4 finestre su 5 / positivo in 2 simboli su 12): è un effetto che dipende dal regime, non
> una proprietà stabile. Quello che fa in modo affidabile, misurato ovunque, è ridurre l'esposizione
> media e quindi l'ampiezza delle oscillazioni. È una manopola di controllo del drawdown.
>
> Il documento contiene anche un **errore di disegno sperimentale commesso e poi corretto** (400
> panieri correlati che producevano una t di 141 priva di significato): vale la pena leggerlo, perché
> è il modo più facile di fabbricare una certezza falsa.

*2026-07-20. Universo: 24 alt su Binance, 1d, 2021-05-11 → 2026-07-20 (1897 giorni allineati).
Costi: 0,10% fee + 0,05% slippage per lato, applicati su ogni unità di nozionale scambiata.*

## Perché questo angolo è diverso

Le cinque ricerche precedenti (`REPORT-RICERCA-2026-07.md`) e il momentum trasversale qui sotto
provano tutte a **prevedere**: quale simbolo salirà, quando entrare, quale regime è in corso. Tutte
negative, e la variabile che le uccide è il turnover.

Il dosaggio della volatilità non prevede niente. Tiene il paniere equipesato e regola **quanto**
capitale esporre, puntando a una volatilità costante: quando il mercato si agita si riduce
l'esposizione, quando si calma la si alza. Turnover basso per costruzione, nessuna direzione da
indovinare.

## Il risultato

| | rendimento | Sharpe | maxDD |
|---|---|---|---|
| paniere equipesato (riferimento) | −64,6% | 0,12 | 81,3% |
| dosato a volatilità obiettivo 30% | **+55,1%** | **0,43** | 48,6% |

Costi del dosaggio: **1,4% del capitale** sull'intero periodo. Esposizione media 48%.

*(I numeri di questa sezione e del controllo sono quelli della fase `xsection`, riproducibile con il
comando in fondo. Il walk-forward più sotto è stato calcolato con la stessa logica in un'armatura
separata, e va riportato dentro la fase per essere riproducibile allo stesso modo.)*

## Il controllo che rende credibile il risultato

L'obiezione ovvia: il dosaggio ha tenuto in media il 48% di mercato in un periodo in cui il mercato
ha perso il 64%. Avrà semplicemente **tenuto meno mercato mentre scendeva** — che non è un merito
ripetibile, è fortuna di calendario.

Il controllo è un'esposizione **costante** alla stessa media:

| a parità di esposizione media (48%) | rendimento | Sharpe | maxDD |
|---|---|---|---|
| costante 48% | −11,0% | 0,12 | 50,2% |
| dosata sulla volatilità | **+55,1%** | **0,43** | 48,6% |

Lo Sharpe passa da 0,12 a 0,43 **a parità di mercato tenuto**. Non è "meno esposizione": è
esposizione nel momento giusto. È l'effetto documentato in letteratura come *volatility timing*
(Moreira & Muir, *Volatility-Managed Portfolios*, Journal of Finance 2017), e si riproduce su questi
dati.

## Walk-forward: la volatilità obiettivo si sceglie sul training, mai sul test

| test da | test a | target scelto | dosata | b&h | Sharpe dosata | Sharpe b&h |
|---|---|---|---|---|---|---|
| 2023-05-11 | 2023-11-11 | 50% | +21,3% | +20,8% | 1,10 | 1,03 |
| 2023-11-11 | 2024-05-11 | 20% | +34,2% | +59,0% | 2,14 | 1,73 |
| 2024-05-11 | 2024-11-11 | 20% | +6,4% | +9,2% | 0,68 | 0,59 |
| 2024-11-11 | 2025-05-11 | 20% | +14,3% | +29,4% | 1,28 | 1,04 |
| 2025-05-11 | 2025-11-11 | 20% | +1,5% | −5,4% | 0,24 | 0,20 |
| 2025-11-11 | 2026-05-11 | 20% | **−10,4%** | **−38,2%** | −1,04 | −1,34 |

- **Composto dosata: +80,0%** contro **+58,7%** del buy&hold.
- Batte sul rendimento grezzo solo in **3/6** finestre — ma migliora lo Sharpe in **6/6**.
- Il valore sta quasi tutto nell'ultima riga: nel semestre negativo perde il 10% invece del 38%.

## Cosa NON dice questo risultato

- **Non è una fonte di alpha.** Non prevede i rendimenti: ne gestisce l'esposizione. Chi si aspetta
  che "trovi le monete giuste" resterà deluso — non è quello che fa.
- **Il walk-forward sceglie il 20% in 5 finestre su 6**, cioè il bordo inferiore della griglia
  provata. L'ottimo potrebbe stare sotto, e parte dell'effetto è "essere poco esposti". Il controllo
  a esposizione costante mostra però che non è *solo* quello.
- **Sei finestre sono poche**, e coprono un unico ciclo cripto. Il dosaggio funziona quando la
  volatilità è persistente e correlata negativamente ai rendimenti: è un fatto stilizzato robusto,
  non una legge.
- Il maxDD resta **48%**. È metà di 81%, ma non è un prodotto prudente.

## Non ha replicato

Il risultato sopra è su un paniere di **24** monete, dal 2021-05. Rifatto su un insieme diverso
(**12** simboli maggiori, dal 2021-01), tenendo tutto il resto identico — stesso target, stessa
finestra di stima, stesso ribilanciamento, stesso controllo:

| stessi 12 simboli, aggregati | rendimento | Sharpe |
|---|---|---|
| buy & hold equipesato | +460,9% | **0,79** |
| dosato (esposizione media 48%) | +113,5% | **0,57** |
| costante equivalente al 48% | +248,2% | **0,79** |

**Il dosaggio peggiora lo Sharpe (0,57 contro 0,79), invece di migliorarlo.** L'effetto non compare
nemmeno aggregando: quindi non era un fenomeno di paniere, dipendeva da *quali* monete e da *quale*
periodo.

La spiegazione più semplice regge a entrambe le osservazioni: nell'universo a 24 il mercato è finito
**in perdita** (−64,6%) e ridurre l'esposizione ha aiutato; nell'universo a 12 è finito **in forte
guadagno** (+460,9%) e ridurre l'esposizione ha fatto perdere il rialzo. Il controllo a esposizione
costante distingue le due cose solo se il campione è abbastanza lungo e vario da contenere entrambi
i regimi — e cinque anni di un solo ciclo cripto non lo sono.

**Un campione favorevole e uno sfavorevole non fanno un edge.** Il walk-forward su 6 finestre non
bastava a proteggere da questo, perché tutte e sei venivano dallo stesso universo.

## La randomizzazione, e un errore di disegno da non ripetere

Con due panieri che dicevano il contrario, ho provato a randomizzare: 400 panieri casuali estratti
dall'universo, ciascuno col suo controllo. Risultato: **399/399 positivi, differenza media +0,353,
statistica t = 141,6.**

**Quel numero non vale niente, e il difetto è nel disegno.** Le cripto si muovono quasi tutte
insieme: 400 panieri estratti dallo **stesso periodo** non sono 400 esperimenti indipendenti, sono
un esperimento solo ripetuto 400 volte. La t di 141 misura quante volte ho ripetuto la stessa cosa,
non quanto l'effetto sia solido. È la stessa classe di errore del multiple testing che il Deflated
Sharpe esiste per correggere, in una forma che il DSR non copre — e ci sono cascato dentro
producendo il numero più impressionante e più vuoto di tutta la ricerca.

La regola che ne resta: **randomizzare su asset correlati dentro una finestra fabbrica significatività
finta.** L'unica randomizzazione che dice qualcosa è quella sulla dimensione lungo cui i dati sono
davvero indipendenti — qui, il tempo.

### La prova rifatta come andava fatta

Universo fisso, finestre temporali **non sovrapposte**:

| finestra | dosato | costante | differenza |
|---|---|---|---|
| 2023-03 → 2023-09 | −0,75 | −0,67 | **−0,08** |
| 2023-09 → 2024-03 | 5,14 | 4,51 | +0,63 |
| 2024-03 → 2024-09 | −0,61 | −1,12 | +0,51 |
| 2024-09 → 2025-03 | 0,99 | 0,58 | +0,42 |
| 2025-03 → 2025-09 | 1,29 | 1,15 | +0,15 |

4 su 5 positive, media +0,33, deviazione standard 0,29 — e una che cambia segno.

**Il quadro complessivo resta contraddittorio**, ed è giusto lasciarlo scritto così invece di
sceglierne il pezzo che piace:

- paniere 24 monete, 2021-2026 → **+0,31** di Sharpe;
- paniere 12 monete, 2021-2026 → **−0,22**;
- 5 sotto-finestre 2023-2025, universo 37 → **+0,33 in media, 4 su 5 positive**;
- 12 simboli presi uno per uno, 2021-2026 → positivo in **2 su 12**.

Non è "l'effetto non esiste" e non è "l'effetto c'è": è **un effetto che dipende dal regime**, con
misure che si contraddicono a seconda di finestra e composizione. Su una base così non si schiera
capitale, e soprattutto non si promette un miglioramento del rapporto rendimento/rischio.

Comando: `dotnet run --project tools/PlatformExpand -- volrobust`

## ⚠️ Su un simbolo SOLO l'effetto non c'è — e le corsie operano su un simbolo solo

Il risultato sopra è su un **paniere** di 24 monete. Ripetuto su singoli simboli (`volsingle`), con lo
stesso controllo a esposizione media costante:

| simbolo | b&h Sharpe | dosata Sharpe | costante Sharpe | esposiz. media |
|---|---|---|---|---|
| BTC/USDT | 0,54 | **0,43** | 0,54 | 62% |
| ETH/USDT | 0,61 | **0,33** | 0,61 | 47% |
| SOL/USDT | 1,16 | **0,75** | 1,16 | 36% |
| BNB/USDT | 0,99 | **0,87** | 0,99 | 57% |
| XRP/USDT | 0,75 | **0,83** | 0,75 | 45% |
| DOGE/USDT | 0,76 | 0,76 | 0,76 | 39% |
| ADA/USDT | 0,46 | **0,41** | 0,46 | 41% |
| LINK/USDT | 0,44 | **0,31** | 0,44 | 37% |
| AVAX/USDT | 0,65 | **0,31** | 0,65 | 36% |
| LTC/USDT | 0,22 | **0,30** | 0,22 | 45% |
| DOT/USDT | 0,06 | **−0,00** | 0,06 | 41% |
| ATOM/USDT | 0,27 | **0,12** | 0,27 | 40% |

**Dosare batte l'esposizione costante equivalente in 2 casi su 12.** Negli altri dieci lo Sharpe
peggiora, a volte molto (AVAX 0,65 → 0,31).

Nota di lettura: la colonna "costante" coincide sempre con lo Sharpe del buy&hold, ed è un'identità
matematica — moltiplicare l'esposizione per una costante scala rendimento e volatilità nella stessa
misura. È proprio per questo che il confronto con la costante, e non con il buy&hold, è l'unico che
dice qualcosa.

**Conseguenza onesta sull'implementazione.** Una corsia di trading opera su **un simbolo solo**:
il dosaggio è quindi implementato esattamente nel contesto in cui questa misura dice che perlopiù
*non* migliora il rendimento corretto per il rischio. Quello che continua a fare, e che è misurato
sia qui sia in `voloverlay`, è **ridurre l'ampiezza delle perdite**: l'esposizione media scende al
36–62% e i drawdown si comprimono di conseguenza. È una scelta di gestione del rischio legittima —
"voglio oscillare meno, accettando di guadagnare meno" — ma **non** è un miglioramento del rapporto
rendimento/rischio su singolo simbolo, e non va venduta come tale.

Perché sul paniere funziona e sul singolo no, resta una domanda aperta. L'ipotesi più naturale è che
la volatilità di un paniere sia più persistente e quindi più prevedibile di quella di una singola
moneta, che è dominata da salti idiosincratici; ma è un'ipotesi, non l'ho misurata.

## Il dosaggio NON recupera le strategie del catalogo

Domanda naturale una volta implementato: applicato sopra le 6 strategie che la caccia aveva
selezionato — e che l'holdout ha bocciato tutte — le salva? Misurato (`voloverlay`), stesso holdout,
stessi costi, cambia solo il dosaggio:

| strategia | coppia | senza | dosata | b&h |
|---|---|---|---|---|
| Supertrend | DOGE/USDT | −44,9% | −34,2% | −22,7% |
| Supertrend | STX/USDT | −85,7% | −56,1% | −35,7% |
| EmaCross | WIF/USDT | −38,7% | −21,7% | −20,6% |
| Supertrend | SUI/USDT | −23,8% | −7,6% | −14,3% |
| DonchianBreakout | DOGE/USDT | −49,9% | −34,9% | −22,7% |
| DonchianBreakout | ARB/USDT | −36,8% | −21,4% | −9,3% |

**Recuperate: 0/6.** Il dosaggio dimezza quasi le perdite in ogni riga — e in un caso (SUI) porta la
strategia sopra il buy&hold — ma nessuna torna in positivo. È la conferma misurata di ciò che era
già dichiarato prima di misurarlo: **riduce l'esposizione, quindi riduce le perdite, ma un segnale
sbagliato dosato resta un segnale sbagliato.** È gestione del rischio, non una fonte di rendimento.

Comando: `dotnet run --project tools/PlatformExpand -- voloverlay`

## Momentum trasversale: negativo, con una lezione

Provato nello stesso giro (`xsection`): ordinare l'universo per forza relativa e tenere i primi K.

- Holdout singolo (4,5 mesi): **−5,2%** contro −10,3% del b&h.
- Walk-forward, 6 finestre: **4/6 positive**, **4/6 battono il b&h** — e nonostante questo il
  composto è **+28,6% contro +66,6%** del buy&hold.

La lezione vale oltre il caso specifico: *"batte il riferimento nel 67% dei periodi"* e *"guadagna
meno della metà"* sono affermazioni entrambe vere sugli stessi dati. Vince nelle finestre piccole e
perde la grande (+4,8% contro +57,3% nel semestre di rialzo). **Contare le vittorie inganna, il
composto no** — ed è il modo più facile di vendersi una strategia perdente.

## Riproducibilità

```
dotnet run --project tools/PlatformExpand -- xsection
```

## Implementato in piattaforma (2026-07-20)

Il dosaggio non è una delle 13 strategie del catalogo: è un **livello sopra**, che moltiplica la
dimensione decisa dalle strategie. Prima `PositionSizePercent` era una costante di configurazione;
ora può essere una funzione della volatilità corrente.

- `VolatilityScaler` (funzione pura) calcola il moltiplicatore dalla volatilità **realizzata** sulle
  ultime N barre, annualizzata con il timeframe corretto.
- Applicato in `SignalOrderBuilder` al margine, cioè esattamente la grandezza che
  `PositionSizePercent` governava da solo.
- **Lo stesso calcolo è usato dal motore di backtest** (`VolatilityTargetingOptions` in
  `BacktestConfiguration`). Senza questo, accendere la funzione dal vivo avrebbe aperto un divario
  backtest/live: non si sarebbe potuto misurare l'effetto sulle proprie strategie prima di schierarlo.
- Configurabile dal pannello sicurezza di `/trading` (solo Admin).

### La proprietà di sicurezza

`MaxExposureMultiplier` vale **1,0** di default: il fattore può solo **ridurre** la dimensione, mai
aumentarla. Ne segue che accendere il dosaggio **non può** far superare `MaxPositionSizePercent` né
`MaxTotalExposurePercent` — al più li rende più stringenti. Il test
`WithDefaults_CanOnlyReduceExposure_NeverIncrease` presidia questa proprietà, e la UI avvisa in
giallo se si alza il tetto sopra 1,0, perché lì la garanzia decade.

### Perché la volatilità realizzata e non il GARCH

Il GARCH(1,1) è già in piattaforma (`/volatility`) ed è una *previsione*, quindi plausibilmente
migliore di una misura retrospettiva. Ma **per questo uso non lo ha misurato nessuno**: i numeri di
questo report vengono dalla volatilità realizzata a 30 barre. Usare il GARCH significherebbe
schierare qualcosa di non verificato — sostituirlo è un esperimento legittimo, ma va misurato prima,
con lo stesso controllo a esposizione media costante.
