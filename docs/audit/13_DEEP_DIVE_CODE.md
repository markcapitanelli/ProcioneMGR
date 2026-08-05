# 13 — Approfondimento del codice

Secondo passaggio, più in profondità del primo. Qui si guarda dentro i file, non solo la struttura.

---

## Il `SafetyChecker`, riga per riga

[Services/Trading/SafetyChecker.cs](../../ProcioneMGR/Services/Trading/SafetyChecker.cs) — 122 righe,
una sola funzione statica pura. È la barriera finale su ogni ordine, e merita di essere letta per
intero perché è il punto in cui il progetto decide che tipo di progetto vuole essere.

**Firma:** `Evaluate(Order, TradingEngineStatus, SafetyConfiguration, DateTime nowUtc)` — nessuna
I/O, nessun orologio interno, nessuna dipendenza. A parità di input, stesso output. Testabile senza
mock.

**Non si ferma alla prima violazione:** raccoglie tutte le violazioni in una lista, «così l'operatore
vede l'intero quadro». Piccola scelta, grande differenza in diagnostica.

I dieci controlli:

| # | Controllo | Nota |
|---|---|---|
| 0 | `capital <= 0` → rifiuto | fail-**closed**: senza denominatore valido nessun ordine è dimensionabile |
| 1 | dimensione singola posizione vs `MaxPositionSizePercent` | |
| 2 | esposizione totale vs `MaxTotalExposurePercent` | |
| 3 | perdita giornaliera ≥ `MaxDailyLossPercent` | **critica** → emergency stop |
| 4 | drawdown ≥ `MaxDrawdownPercent` | **critica** → emergency stop |
| 5 | posizioni aperte ≥ `MaxOpenPositions` | |
| 6 | intervallo minimo fra ordini | anti-spam |
| 7 | ordine Live senza conferma manuale | |
| 8 | emergency stop già attivo | |
| 9 | quantità ≤ 0 o prezzo ≤ 0 | sanità di base |
| 10 | leva > `MaxLeverageAllowed` | solo Futures |

Il commento al controllo 3 documenta un cambio di simbolo di confronto:

> *«Confronto `>=` (fail-closed): AL limite si ferma, coerente col drawdown al punto 4 — prima la
> perdita giornaliera usava `>` (permessa esattamente al limite), un'asimmetria senza motivo.»*

È il tipo di dettaglio che di solito non viene né notato né scritto.

## `SafetyConfiguration`: i default sono l'argomento

[SafetyConfiguration.cs](../../ProcioneMGR/Services/Trading/SafetyConfiguration.cs) — 130 righe di cui
la maggior parte sono commenti che spiegano *perché* quel default.

Due invarianti nascoste nei valori:

**`MaxExposureMultiplier = 1.0`** — il dosaggio sulla volatilità può quindi solo **ridurre** la
dimensione, mai aumentarla. Il commento è esplicito: *«Alzarlo sopra 1,0 toglie questa garanzia»*,
perché sopra 1 il dosaggio potrebbe far superare i cap validati a `StartAsync`.

**Coerenza validata all'avvio:** `PositionSizePercent × leva ≤ MaxPositionSizePercent ≤
MaxTotalExposurePercent`, «altrimenti il SafetyChecker rifiuterebbe OGNI ordine e la corsia non farebbe
mai trading». Il controllo esiste perché è un modo realistico di rendere una corsia muta senza
accorgersene.

Default conservativi: posizione 10%, esposizione 50%, perdita giornaliera 5%, drawdown 20%,
5 posizioni, 10 s fra ordini, conferma manuale Live **true**, leva max **5x** (con il commento che
`LeverageAdvisor` tipicamente sconsiglia oltre 3-5x anche con edge reale).

## `PromotionEvaluator`: il confine, e la sua unica eccezione

[PromotionEvaluator.cs](../../ProcioneMGR/Services/Trading/PromotionEvaluator.cs) — la logica
decisionale è nel metodo statico `Decide`, puro e deterministico.

Nel primo audit avevo scritto che l'evaluator «non restituisce mai Live». È vero e resta vero, ma la
lettura completa aggiunge una sfumatura importante che merita di essere registrata: **[AF4a] esiste
un percorso automatico che agisce su una corsia Live**, e va nella sola direzione che riduce il
rischio.

```
Live → Testnet   ammesso, ma: opt-in (AutoDemoteLiveToTestnet, default false)
                              + dry-run acceso di default (annuncia, non agisce)
                              + mai Paper diretto, mai un avvio
                              + flatten reduce-only prima del cambio modalità
Testnet → Live   NON ESISTE, in nessuna configurazione
```

Il commento nel codice non lascia margini: *«Verso Live non esiste e non esisterà alcun percorso
automatico»*. E dichiara la difesa: *«Il fuzz a 20k combinazioni difende tutti questi confini»*.

Le soglie di promozione Paper→Testnet: Sharpe ≥ 0,8 · ≥ 30 trade · DD ≤ 15% · ≥ 3 settimane ·
win rate ≥ 45%, con un **blocco assoluto** a DD > 20% che nega la promozione anche se tutto il resto
è ottimo.

Quando i criteri non sono soddisfatti, il messaggio elenca esattamente quali mancano e di quanto —
`"Sharpe 0,31<0,80, trade 12<30, osservazione 8gg<21gg"`. Niente "non idonea" opaco.

---

## 🔴 Il difetto della quantità degli ordini {#quantita}

Questo è il risultato più concreto del secondo passaggio: un **difetto reale, con evidenza di
produzione**.

### L'evidenza

Nello storico ordini di `/trading`, ordini realmente inviati a Binance e rifiutati:

```
HTTP 400: {"code":-1100,"msg":"Illegal characters found in parameter 'quantity';
           legal range is '^([0-9]{1,20})(\.[0-9]{1,20})?$'."}
```

Non è un errore di rete né di credenziali: è l'exchange che rifiuta la **stringa della quantità**.

### La catena

**1.** La quantità nasce da una divisione, in
[SignalOrderBuilder.cs:57](../../ProcioneMGR/Services/Trading/Internal/SignalOrderBuilder.cs#L57):

```csharp
var qty = notional / price;
```

Una divisione fra `decimal` produce fino a **28-29 cifre significative**. `500m / 58412.00m` non è
periodico "corto": genera un'espansione lunghissima.

**2.** L'arrotondamento è condizionato
([SignalOrderBuilder.cs:67](../../ProcioneMGR/Services/Trading/Internal/SignalOrderBuilder.cs#L67)):

```csharp
if (state.Mode != TradingMode.Paper && filters is not null)
{
    qty = filters.RoundQuantity(qty);
    if (!filters.IsTradable(qty, price)) { /* salta */ }
}
else
{
    qty = Math.Round(qty, 5, MidpointRounding.ToZero);
}
```

**3.** Ma `RoundQuantity` **non arrotonda** se `StepSize` è zero
([ExchangeTrading.cs:108](../../ProcioneMGR/Services/Exchanges/ExchangeTrading.cs#L108)):

```csharp
public decimal RoundQuantity(decimal qty)
    => StepSize > 0m ? Math.Floor(qty / StepSize) * StepSize : qty;   // ← passa attraverso
```

**4.** E `IsTradable` **approva tutto** se i minimi sono zero
([ExchangeTrading.cs:116](../../ProcioneMGR/Services/Exchanges/ExchangeTrading.cs#L116)):

```csharp
public bool IsTradable(decimal qty, decimal price)
    => qty >= MinQty && qty * price >= MinNotional;   // 0 >= 0 → true
```

### Il risultato

Se `GetSymbolFiltersAsync` restituisce un `SymbolFilters` **non popolato** — LOT_SIZE assente nella
risposta, parsing fallito, forma diversa su testnet — allora:

- `filters is not null` è **vero**, quindi si entra nel ramo "sicuro";
- `RoundQuantity` restituisce la quantità **grezza**, con la sua coda di decimali;
- `IsTradable` dice **sì** perché i minimi sono zero;
- alla API parte una stringa con **più di 20 decimali**;
- Binance risponde **400 / -1100**.

Il guard che doveva proteggere si è trasformato in un no-op silenzioso. È esattamente la classe di
difetto che il progetto ha già catalogato con un nome proprio: **«controlli che rassicurano a
prescindere dalla realtà»**.

Spiega anche perché i rifiuti sono **intermittenti**: passano solo le quantità la cui espansione
decimale sta entro 20 cifre.

### Cosa farci

1. Un `SymbolFilters` con `StepSize == 0` va trattato come **filtri assenti**, non come filtri validi:
   `RoundQuantity` dovrebbe alzare un'eccezione o il chiamante rifiutarsi di procedere in Testnet/Live.
2. Un troncamento di sicurezza incondizionato prima dell'invio (es. 8 decimali) come rete finale.
3. Un test che invochi `SignalOrderBuilder` con `SymbolFilters` a zero in modalità Testnet e verifichi
   che l'ordine **non** venga costruito.

---

## Il pattern degli ordini duplicati

Sempre dallo storico reale, uno schema che si ripete decine di volte:

```
07-03 12:00  Buy  Market  502,82840  1,60  Filled
07-03 12:00  Buy  Market  498,75311  1,60  Rejected  Ordini troppo ravvicinati: 0,0s dall'ultimo, minimo 10s.
```

Due ordini **con lo stesso timestamp di candela**, quantità leggermente diverse, il secondo respinto
dall'anti-spam a `0,0s`.

Il `SafetyChecker` fa il suo lavoro. Ma la sua funzione dichiarata è *anti-spam*, non
*deduplicazione di segnale*: qui sta assorbendo, a valle, una duplicazione che nasce a monte.
Se un giorno `MinOrderIntervalSeconds` venisse abbassato o azzerato per una strategia intraday,
la duplicazione arriverebbe fino all'exchange.

**DA VERIFICARE:** se l'origine sia un ensemble con due gambe che valutano la stessa barra, o una
doppia valutazione della stessa candela nel `TradingWorker`.

---

## Dove vive il rigore statistico

`Services/Validation/` — 10 file, il modulo più piccolo e più importante:

| File | Cosa implementa |
|---|---|
| `DeflatedSharpeRatio.cs` | Sharpe corretto per il numero di trial (Bailey–López de Prado) |
| `BacktestOverfitting.cs` | Probability of Backtest Overfitting |
| `CombinatorialPurgedCv.cs` | percorsi OOS multipli dallo stesso storico |
| `PermutationTest.cs` | significatività non parametrica |
| `NullTwinGenerator.cs` / `NullTwinJudge.cs` | serie senza edge: se la pipeline ci trova qualcosa, la pipeline è rotta |
| `GatePowerAnalyzer.cs` | potenza statistica del gate |
| `MinTrackRecord.cs` | storia minima necessaria per un giudizio |
| `EffectiveTrials.cs` | trial *indipendenti*, non trial totali |
| `SelectionValidator.cs` | validatore della selezione |

L'esistenza di `NullTwinJudge` è il dettaglio che distingue questo progetto: non basta misurare, si
verifica anche che **lo strumento di misura sappia dire di no**.

## L'isolamento delle corsie

Tre meccanismi, sovrapposti:

1. **Keyed DI** — `serviceProvider.GetRequiredKeyedService<ITradingEngine>(laneId)`: ogni corsia ha
   la propria istanza di motore, worker ed ensemble.
2. **Filtro `LaneId` sulle query** — con un commento che vale la pena citare, da
   [TradingEngine.cs:329](../../ProcioneMGR/Services/Trading/TradingEngine.cs#L329):
   > *«CRITICO: senza il filtro LaneId, avviare/riavviare una corsia in Paper cancellerebbe le
   > posizioni aperte di TUTTE le altre corsie condividendo lo stesso DB.»*
3. **`LaneExecutionLease`** — un solo scrittore per corsia.

E una distinzione fine: la pulizia dello stato all'avvio vale **solo in Paper**, perché su
Testnet/Live «le posizioni sono REALI sull'exchange — cancellare la riga locale a un riavvio
perderebbe traccia di un'esposizione ancora aperta».

## Il motore remoto, dietro la stessa interfaccia

`RemoteTradingEngineClient` implementa `ITradingEngine` parlando gRPC. La UI non sa quale
implementazione stia usando: `AddTradingLanes` commuta in base a `Trading:UseRemoteTrading`, e la
stessa composizione è **condivisa verbatim** con l'host `ProcioneMGR.Trading`.

Conseguenza pratica verificata dal vivo: quando il core torna raggiungibile, **il guscio si
riconnette da solo**, senza riavvio.

## Osservazioni sulla qualità del codice

**A favore**, e non sono banalità:

- I commenti spiegano il *perché*, con data e spesso col riferimento al report che ha motivato la
  scelta (`[B3 2026-07-26]`, `[AF4a]`, `[B1]`, `[P0-5]`).
- Le funzioni critiche sono **pure e statiche**: `SafetyChecker.Evaluate`, `PromotionEvaluator.Decide`.
  Non sono mockabili, e questo è il punto.
- I default sono conservativi e argomentati.
- Le decisioni negative sono documentate come tali: `DriveProtectiveExits = false` non è pigrizia,
  è il risultato di 24 misure su 24.

**Contro:**

- `TradingEngine.cs` a 87,8 KB resta il doppio del secondo file più grande.
- Il difetto della quantità mostra che il pattern «guard che degrada a no-op» non è ancora stato
  cercato sistematicamente ovunque. `RoundPrice` ha esattamente la stessa forma di `RoundQuantity`
  (`TickSize > 0m ? … : price`) e quindi lo stesso problema latente.
- `Autonomy.razor` (107,6 KB) e `Sentiment.razor` (55 KB) non hanno page service, mentre sei pagine
  minori ce l'hanno.
