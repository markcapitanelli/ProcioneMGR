# B3 — Il gate tick-vs-candela, sbloccato e misurato (2026-07-28)

*Chiude il secondo tempo del gate B3 del [PRD-INTEGRAZIONE-CORE-CALDO](PRD-INTEGRAZIONE-CORE-CALDO.md).
Esito: **negativo per l'accensione**. `DriveProtectiveExits` resta a `false`, e questa volta per
misura, non per prudenza.*

## 1. Il gate era circolare

Il PRD chiedeva, come condizione per passare a `DriveProtectiveExits=true`, il «confronto
tick-vs-candela nelle metriche». La ricetta operativa era scritta anche nel
[report R1](REPORT-REALTIME-FEED-R1.md): confrontare `procione.trading.protective_exits` fra
`source=tick` e `source=candle`.

Quel confronto non poteva esistere. In assetto osservativo il worker **scarta** i tick
(`RealtimePriceWorker.ConsumeTicksAsync`, ramo `!DriveProtectiveExits`), e la metrica si incrementa
solo quando un'uscita **scatta davvero** (`TradingEngine.CloseAndCountAsync`). La serie
`source=tick` nasce solo dopo l'accensione: il confronto che doveva autorizzarla ne presupponeva
una già fatta. Il commento in `infra/k8s/trading/trading-config.env` («i tick alimentano metriche e
confronto tick-vs-candela») descriveva un comportamento che il codice non aveva.

Non era un dettaglio di strumentazione: era la ragione per cui il gate stava fermo da due giorni
senza che nessuno potesse dire perché.

## 2. Come si è chiusa senza accendere niente

`ProtectiveExitLagAnalyzer` risponde alla stessa domanda **offline**, usando le candele a
risoluzione fine come surrogato dei tick: 5m per AAVE e DOT, 15m per XLM (che 5m non ne ha). Ogni
posizione simulata vive due volte — una col solo percorso a barre di corsia, una col percorso fine —
e le due vite si confrontano su due grandezze:

- **anticipo**: quanti minuti prima il percorso fine *scopre* che il livello è stato toccato;
- **costo del ritardo**: la differenza, in punti base dell'ingresso e orientata sulla posizione, fra
  il prezzo ottenibile al momento del tocco e quello ottenibile alla chiusura della barra di corsia,
  che è quando il percorso a candele agisce.

Tre scelte tengono in piedi la misura:

1. **Lo stesso evaluator del motore** (`ProtectiveExitEvaluator`) decide su entrambi i percorsi. Due
   regole diverse avrebbero misurato la differenza fra le regole, non fra le risoluzioni.
2. **Gli stati non si toccano.** Il `BestPriceSinceEntry` del trailing è per-percorso: condividerlo
   avrebbe fatto muovere lo stop del percorso a candela col ritmo dei tick, cioè avrebbe dato al
   feed proprio il potere che l'assetto osservativo gli nega. Un test lo verifica con un valore di
   anticipo (4.500 s) che è ottenibile **solo** se i due stati sono separati.
3. **I bracket sono quelli veri**, letti dagli `EnsembleStates` delle corsie in esecuzione, non
   parametri scelti per l'occasione.

Il surrogato è **conservativo**: la scoperta è datata alla chiusura della barra fine e non al tick
esatto, quindi l'anticipo misurato è un limite inferiore.

## 3. Il controllo che decide se credere al risultato

Il numero che segue è controintuitivo, quindi prima il controllo. Su una **passeggiata aleatoria
senza deriva** il costo del ritardo deve valere zero in media: per il teorema d'arresto opzionale,
sapendo che il prezzo ha toccato il livello dentro la barra, il valore atteso della chiusura di
quella barra è il livello stesso. Il test lo verifica su 12 semi, con le barre di corsia costruite
come **aggregazione esatta** di quelle fini — nessuna informazione compare o sparisce fra i due
percorsi, cambia solo la risoluzione — e pretende che lo zero stia entro tre errori standard.

Passa. Quindi il segno che si vede sui dati veri è una proprietà del mercato, non una firma della
costruzione. Senza questo controllo il risultato del §4 non varrebbe niente: sarebbe indistinguibile
da un errore di un passo nel datare le scoperte.

## 4. Il risultato: il ritardo non costa, rende

Corsie vive, storia dal 2025-01-01, ingressi campionati 1 ogni 4 barre.

| Corsia | Serie | Surrogato | Uscite | Anticipo mediano | Costo mediano | Costo medio | Il ritardo conviene |
|---|---|---|---|---|---|---|---|
| 0 | AAVE/USDT 1d (SL 17,03% TP 33,94%) | 5m | 119 | 445 min | **−77,4 bps** | −95,8 bps | 58,0% |
| 1 | DOT/USDT 15m (SL 3,72% TP 10,32% TSL 8%) | 5m | 5.272 | 5 min | **−6,1 bps** | −2,5 bps | 56,8% |
| 2 | XLM/USDT 1h (SL 5%) | 15m | 1.865 | 15 min | **−5,2 bps** | −25,6 bps | 54,6% |

Segno negativo = uscire **al tocco** è peggio che uscire alla chiusura della barra. È l'effetto
noto dello stop preso sull'ombra: il prezzo buca il livello e rientra, e chi ha aspettato la
chiusura esce meglio.

Un verdetto su un solo bracket vale per quel bracket, quindi la sensibilità alla larghezza dello
stop — che è il caso in cui il ritardo dovrebbe fare più male:

| SL% | AAVE 1d | DOT 15m | XLM 1h |
|---|---|---|---|
| 0,5 | −25,4 | −1,2 | −1,4 |
| 1 | −4,3 | −2,7 | −3,5 |
| 2 | −13,3 | −4,0 | −3,7 |
| 3 | −8,6 | −5,5 | −2,5 |
| 5 | −5,9 | −4,2 | −5,2 |
| 8 | −53,1 | −4,1 | −16,1 |
| 12 | −37,5 | −4,1 | −28,7 |
| 17 | −74,4 | −4,1 | −24,8 |

*(costo mediano del ritardo in bps; negativo = aspettare la chiusura conviene)*

**24 configurazioni su 24 hanno mediana negativa.** Non c'è una larghezza di stop, fra quelle
provate, alla quale la reattività del feed paghi.

## 5. Cosa questo NON dice

- **Non dice che R1 sia inutile.** Il feed fa altre due cose che restano intatte e accese: consegna
  le candele chiuse al motore senza aspettare il giro REST, e allerta quando smette di ricevere
  (`StaleAfterSeconds`). Qui è in discussione solo il terzo uso, le uscite protettive guidate dai
  tick.
- **Non dice che il ritardo sia innocuo nella coda.** I percentili sono grossomodo simmetrici
  (AAVE: p10 −543 bps, p90 +438), cioè il feed salva tanto quanto costa nei casi estremi. Ma la
  finestra 2025-01 → 2026-07 potrebbe non contenere un crollo con gap vero, che è lo scenario in
  cui aspettare la chiusura di una barra giornaliera fa un danno di categoria diversa. Su questo la
  misura è muta, e va detto.
- **Non vale per i Futures a leva.** Le tre corsie sono Spot. Sulla liquidazione l'asimmetria è
  reale — lì il ritardo non ha una faccia buona — e il verdetto andrebbe rifatto.
- **Il fill registrato è conservativo, non ottimista.** Lo scarto fra ciò che il motore registra
  (il livello) e ciò che sarebbe ottenibile alla chiusura ha lo stesso segno negativo: la
  contabilità dei forward test **sottostima** il risultato di 5-77 bps per uscita protettiva,
  non lo gonfia.

## 6. Decisione

`MarketData__Realtime__DriveProtectiveExits` **resta `false`**, e il gate B3 si chiude con esito
negativo sulla sua seconda gamba — come C1, e per lo stesso tipo di ragione: la cosa è stata
misurata e non paga.

Riaprirebbe il tema una sola cosa: una corsia **Futures a leva**, dove la coda della liquidazione
cambia il conto e va misurata a parte.

## 7. La sentinella dal vivo, e perché non è una seconda misura

Il §5 dice cosa questa misura non può vedere. Due di quelle cose si possono guardare solo dal vivo:
il momento vero del tick, e il crollo con gap che nella finestra non c'è stato. Da qui la
**sentinella d'ombra**: in assetto osservativo i tick non vengono più scartati, il motore guarda se
FAREBBERO scattare un'uscita, e quando il percorso a candele chiude davvero quella posizione nasce
una riga in `ProtectiveExitShadows`.

**Non è una seconda misura, ed è importante non usarla come tale.** Le tre corsie producono 3-6
uscite protettive al mese: per distinguere una mediana di −6 bps da zero servirebbero centinaia di
osservazioni, cioè anni. La domanda «quanto costa in media» è già chiusa qui sopra, su migliaia di
posizioni.

Il meccanismo è la **soglia**: sopra 200 bps *a sfavore del ritardo* si allerta sul caso **singolo**.
A quella grandezza non si sta più misurando l'effetto dell'ombra sullo stop, si sta guardando un
salto di prezzo dentro la barra — cioè esattamente lo scenario che la finestra del replay non
conteneva, e che con n = 1 è già una notizia. Sotto soglia, e nel caso opposto (il ritardo conviene),
silenzio: è il verdetto già noto.

Due proprietà da non perdere di vista:

- **La sentinella è inerte.** Non chiude nulla e non tocca `BestPriceSinceEntry`. Se lo toccasse, il
  livello di trailing del percorso a candele si sposterebbe col ritmo dei tick: il feed deciderebbe
  le uscite senza che nessun toggle lo dica, e l'effetto si vedrebbe solo come uno stop scattato «un
  po' prima» del previsto — cioè non si vedrebbe. Un test lo verifica esplicitamente.
- **Il segno è lo stesso di questo report** (positivo = il feed avrebbe fatto uscire meglio), perché
  il senso della sentinella è dire se il mercato continua a comportarsi come nel replay: due
  convenzioni diverse renderebbero il confronto un esercizio di traduzione.

**Per la potenza statistica, il posto giusto resta il replay**, ri-eseguito sui dati freschi: 30
simboli invece di 3, migliaia di posizioni invece di una manciata, e nessun rischio operativo. È lì
che un cambio di verdetto si vedrebbe per primo.

## 8. Come rifare la misura

```bash
dotnet run --project tools/PlatformExpand -c Release -- exitlag 4 sweep
```

Legge le corsie in esecuzione e i loro bracket dal database, sceglie da sé la risoluzione fine più
fine disponibile per ogni serie, e stampa anticipo, costo e sensibilità. Read-only.
