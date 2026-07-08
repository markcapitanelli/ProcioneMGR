# Report — Wiring Stop-Loss/Trailing dal Backtest al Trading Live

## Obiettivo

Lo stop-loss/trailing-stop validato nel backtest (`BestStopVariant` di una strategia
sopravvissuta) non arrivava mai automaticamente al `TradingEngine`: andava impostato a mano
su ogni posizione da `/trading`, con il rischio di operare a leva senza nessuna delle
protezioni che avevano reso "sopravvissuta" quella strategia. Questo lavoro chiude il gap:
lo stop configurato su una gamba dell'ensemble viene ora applicato automaticamente
all'apertura di ogni posizione, con la modifica manuale che resta sempre l'ultima parola.

## File modificati

- `Services/Ensemble/EnsembleModels.cs` — `EnsembleStrategy` +3 campi nullable
  (`StopLossPercent`, `TakeProfitPercent`, `TrailingStopPercent`).
- `Services/Trading/TradingModels.cs` — `OpenPosition` +2 campi
  (`TrailingStopPercent`, `BestPriceSinceEntry`, il high-water-mark per il trailing).
- `Services/Trading/ITradingEngine.cs` / `TradingEngine.cs` —
  - nuovo helper privato `ApplyAutoStops(pos, order)`, chiamato una volta sola alla creazione
    di ogni `OpenPosition` (sia Spot che Futures), che legge lo stop configurato sulla
    `EnsembleStrategy` corrispondente (via `order.StrategyId`) e lo traduce in prezzi assoluti;
  - tick loop (`ProcessCandleAsync`) esteso con la logica di trailing (livello calcolato sul
    `BestPriceSinceEntry` PRIMA di considerare il prezzo della candela corrente — stesso
    principio causale del motore di backtest; se stop statico e trailing sono entrambi attivi
    vince quello più protettivo, come le varianti combinate SL+TRAIL del backtest);
  - `SetStopLossTakeProfitAsync` esteso con un terzo parametro `trailingStopPercent` (l'editing
    manuale resta l'unico punto che tocca questi campi dopo l'apertura: l'automatismo gira
    SOLO alla creazione, quindi non c'è modo che sovrascriva una scelta manuale successiva);
  - audit log (`PlaceOrder`) arricchito con `autoStopLoss`/`autoTakeProfit`/`autoTrailingStopPercent`.
- `Services/Pipeline/PipelineModels.cs` — `ProposedLeg` +1 campo (`BestStopVariant`), per non
  perdere l'informazione di stop quando l'`EnsembleAssemblyStage` costruisce la proposta
  (prima restava incorporata SOLO nella `DisplayName`, una stringa non pensata per essere
  riparsata dal wiring).
- `Services/Pipeline/Stages/DecisionStages.cs` — `EnsembleAssemblyStage` popola il nuovo campo.
- `Components/Pages/Pipeline.razor` — `ApplyRecommendationAsync` traduce `BestStopVariant` in
  percentuali riusando `RobustnessProbeStage.ApplyVariant` (nessuna duplicazione del parsing
  "SLx"/"TRAILx"/"base", già testato altrove) e li imposta sulla `EnsembleStrategy` creata.
- `Components/Pages/Ensemble.razor` — 3 colonne editabili (SL %/TP %/Trailing %) nella tabella
  delle gambe, salvate col normale "Save Configuration"; `GuidaPanel` aggiornato.
- `Components/Pages/Trading.razor` — colonna "Trailing %" editabile nella tabella posizioni
  (stesso pattern di SL/TP già esistente), `SetStopLossTakeProfitAsync` chiamato con il terzo
  parametro; `GuidaPanel` aggiornato.
- `ProcioneMGR.Tests/TradingEngineStopTests.cs` (nuovo) — 4 test end-to-end sul motore.
- `ProcioneMGR.Tests/PipelineTests.cs` — +1 test (`EnsembleAssemblyStageTests`).
- `Data/Migrations/20260704004623_AddTrailingStopToOpenPosition.cs` (nuova, applicata).

## Decisioni architetturali

1. **`EnsembleStrategy` non ha richiesto migrazione EF.** `EnsembleConfiguration` è persistita
   come JSON in `EnsembleState.ConfigurationJson` (non una tabella EF con colonne fisse), quindi
   i 3 nuovi campi round-trippano gratis. Il prompt originale prevedeva una migrazione qui: non
   serve, ed è stata l'unica vera semplificazione rispetto al piano.

2. **`OpenPosition`, invece, È una tabella EF reale** e ha richiesto la migrazione
   `AddTrailingStopToOpenPosition` per i 2 campi trailing (`TrailingStopPercent`,
   `BestPriceSinceEntry`) — applicata e verificata contro il DB reale durante la verifica browser
   (log EF: `ALTER TABLE "OpenPositions" ADD "BestPriceSinceEntry"/"TrailingStopPercent"`).

3. **Trailing stop dal vivo non esisteva affatto prima di questo lavoro** (solo SL/TP a prezzo
   assoluto). È stato implementato mirroring esatto della logica già presente e testata in
   `BacktestEngine.cs` (righe 236-277): livello calcolato sul best-since-entry PRIMA della
   candela corrente, per non usare l'estremo di oggi per definire lo stop di oggi. La differenza
   rispetto al backtest è che il live controlla solo la Close (non High/Low): è la stessa
   semplificazione "solo Close" già preesistente per SL/TP prima di questo lavoro (commentata nel
   codice), qui semplicemente estesa al trailing invece di introdurne una nuova.

4. **"Priorità: manuale vince sempre" è garantita per costruzione, non da un controllo
   esplicito.** `ApplyAutoStops` gira UNA sola volta, alla creazione della `OpenPosition`
   (quando per definizione non esiste ancora nessun valore manuale da rispettare). Da quel
   momento in poi l'unico altro punto che scrive `pos.StopLoss/TakeProfit/TrailingStopPercent`
   è `SetStopLossTakeProfitAsync` (l'editing manuale da `/trading`): l'automatismo non viene mai
   più invocato per quella posizione. Verificato esplicitamente dal test
   `ManualStopLoss_TakesPriorityOverAutomaticOne`.

5. **`Discovery.razor` NON è stato toccato — deviazione dal prompt, con motivazione.** Il prompt
   assumeva che la ricerca di `/discovery` (StrategyDiscoveryEngine + StrategyComposer)
   producesse una `BestStopVariant` come il pipeline. Verificato che **non è così**:
   `DiscoveryCandidate` (`Services/Discovery/StrategyDiscoveryModels.cs`) non ha né uno
   stop-variant né un campo di stop di nessun tipo — la ricerca di varianti-stop
   (`RobustnessProbeStage`) esiste SOLO nel pipeline autonomo, non nella caccia manuale da
   `/discovery`. Salvare una strategia da lì (`SaveAsync` in `Discovery.razor`) non ha quindi
   nessuna informazione di stop validata da propagare: inventarne una sarebbe stato peggio che
   ometterla (uno stop non validato dà una falsa sensazione di sicurezza). La via pulita per
   applicare uno stop a una strategia salvata da `/discovery` resta impostarlo a mano in
   `/ensemble` (ora possibile grazie alle 3 colonne aggiunte).

6. **Nessuna colonna/flag "applicato automaticamente" nella UI.** Valutato un badge "auto"
   accanto allo Stop Loss in `/trading`, scartato: senza un campo di provenienza dedicato
   (ulteriore colonna EF solo per un'etichetta cosmetica) non si può distinguere in modo
   affidabile "impostato dall'automatismo" da "impostato a mano in precedenza". La
   tracciabilità richiesta dai criteri di qualità è coperta dall'audit log (`PlaceOrder` ora
   riporta `autoStopLoss`/`autoTakeProfit`/`autoTrailingStopPercent`), non da uno stato di UI.

7. **Nessuna modifica a `Trading.razor` per "precompilare l'apertura manuale di una
   posizione"** (punto 3.5/Passo 7 del prompt): verificato che `/trading` non ha, e non aveva,
   nessun form di apertura manuale di posizione — le posizioni sono SEMPRE aperte dal motore in
   base ai segnali delle strategie attive nell'ensemble (`TryOpenAsync`). L'unica interazione
   manuale esistente è l'editing di SL/TP di una posizione GIÀ aperta dal motore, che è
   esattamente dove ho aggiunto la colonna Trailing %.

## Test

- **4 nuovi test** in `TradingEngineStopTests.cs`, con un `TradingEngine` reale (DB SQLite su
  file temp, `IStrategyFactory` scriptato per controllo deterministico del segnale — niente
  bisogno di tarare un indicatore reale):
  - `AutoStopLoss_AppliedAtOpen_ClosesPositionWhenHit` — lo stop configurato sulla gamba (5%)
    diventa il prezzo assoluto corretto alla prima apertura, SENZA nessuna chiamata manuale, e
    chiude la posizione quando il prezzo lo attraversa.
  - `ManualStopLoss_TakesPriorityOverAutomaticOne` — dopo un editing manuale, il valore
    automatico (95) smette di contare: solo il manuale (90) governa la chiusura.
  - `TrailingStop_RatchetsUpAndClosesOnPullback` — il best-since-entry sale con il prezzo, il
    livello di chiusura si calcola SEMPRE sul best della candela precedente, e la posizione si
    chiude solo al pullback sotto quel livello.
  - `NoStopConfigured_LegacyEnsemble_NeverSetsAutomaticStop` — retrocompatibilità: un
    `EnsembleStrategy` senza questi campi (creato prima di questo lavoro) si comporta
    ESATTAMENTE come prima, nessuno stop automatico.
- **1 nuovo test** in `PipelineTests.cs` (`EnsembleAssemblyStageTests`) — il `BestStopVariant`
  validato in walk-forward arriva nel `ProposedLeg`, non solo nella `DisplayName`.
- **Suite completa**: 430/430 verdi (425 preesistenti + 5 nuovi), 0 regressioni.
- **Build**: 0 errori, 0 warning nuovi (i 4 warning presenti sono preesistenti, non toccati da
  questo lavoro — verificato che riguardano `ApplicationDbContext.cs`/`ExchangeSettings.razor`,
  file non modificati qui).

## Verifica browser

- `/ensemble`: le 3 nuove colonne (SL %/TP %/Trailing %) compaiono nella tabella delle gambe
  reali già salvate dall'utente (es. "Momentum NEAR/USDT 4h [SL5]"); valorizzate, salvate con
  "Save Configuration" e **verificate persistenti dopo un reload completo della pagina** (log EF
  confermano l'update sulla riga `EnsembleStates`), poi ripristinate al loro stato originale
  (vuoto) per non lasciare modifiche non richieste alla configurazione reale dell'utente.
- `/trading`: colonna "Trailing %" presente e renderizzata correttamente, nessun errore in
  console né nei log del server.
- Migrazione EF applicata con successo contro il DB reale allo start del server (log:
  `Applying migration '20260704004623_AddTrailingStopToOpenPosition'`).
- Non è stato avviato un ciclo di Paper trading end-to-end sui dati reali dell'utente (avrebbe
  richiesto di aspettare un segnale reale su mercato live, con tempi non deterministici, e
  avrebbe mutato lo stato condiviso di trading); la correttezza del meccanismo di apertura +
  applicazione automatica + chiusura è invece provata in modo deterministico e ripetibile dai
  4 test di `TradingEngineStopTests.cs`, che esercitano lo stesso `TradingEngine` reale (non un
  doppio) end-to-end.

## Prossimi passi consigliati

Questo elemento era il punto 1 (priorità massima) della roadmap discussa in precedenza. I
successivi, nell'ordine già proposto, restano:
2. Monitor di decadimento strategia (realizzato vs atteso in backtest).
3. Verifica/provisioning credenziali futures.
4. Periodo di osservazione Paper obbligatorio prima di Testnet/Live.
5. Schedulazione automatica delle cacce del pipeline.
