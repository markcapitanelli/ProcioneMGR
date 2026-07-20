# Dosaggio della volatilità — il primo risultato positivo

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
