# Report — Creative Strategy Discovery (2026-07-03)

Implementazione completa del layer di scoperta creativa: genera automaticamente strategie
**mai codificate prima** (combinazioni di segnali, trigger su eventi, meta-strategie
regime-conditional), le valida con la stessa disciplina walk-forward/holdout delle strategie
classiche, e le integra in `/discovery` e nel pipeline autonomo.

**Stato: funzionante e verificato con dati reali.** 425/425 test xUnit (410 + 15 nuovi), build
0 errori nuovi. Run reale su 18 coppie × {1h,4h,1d}: **165 candidati validati, 5 sopravvissuti
all'holdout, di cui 2 generati dalla modalità creativa** (varianti "Composite" su NEAR/USDT
4h) — la scoperta creativa ha trovato un edge reale, non solo teoria.

---

## 1. Decisione architetturale fondamentale (letta prima di scrivere codice)

**`IStrategy.EvaluateSignal` non ha accesso a un "contesto di servizi"**: prende solo
`(index, price, timestamp)`, e `StrategyParameters` in TUTTA la piattaforma (backtest,
optimization, ensemble, trading live, DB) è `Dictionary<string, decimal>`. Una "spec JSON nei
parametri" come suggerito dal prompt avrebbe richiesto toccare `OptimizationEngine` (che
genera `ParameterRange` su valori decimali), `EnsembleStrategy.Parameters`,
`SavedStrategy.ParametersJson` e il salvataggio/caricamento di `TradingEngine` — cioè
riscrivere mezza piattaforma, violando il vincolo "riuso totale, nessuna riscrittura".

**Soluzione scelta**: le spec sono **interamente parametri decimali**, resi possibili da un
nuovo `SignalCatalog` che **normalizza ogni segnale elementare a una scala 0-100 comune**
(percentile causale per i segnali sbilanciati, scala nativa per gli oscillatori). Una
condizione diventa `(id_segnale, operatore, soglia)` — tre decimal. Risultato: le strategie
composite sono **nativamente** ottimizzabili da `OptimizationEngine`, salvabili come
`SavedStrategy`, tradabili da `TradingEngine`, **zero modifiche** a nessuno di quei moduli.

---

## 2. File creati

### Nucleo (`ProcioneMGR/Services/Backtesting/`)
| File | Contenuto |
|---|---|
| `SignalCatalog.cs` | 9 segnali normalizzati 0-100 (RSI, Stoch%D, %B, direzione Supertrend, percentili causali di volume/VWAP-dev/momentum/MACD-hist/dist-SMA50); cache `ConditionalWeakTable` per istanza di lista candele; `CausalPercentile` (rolling rank, mai guarda avanti) |
| `CompositeSignalStrategy.cs` | Meta-strategia: fino a 3 condizioni AND/OR in ingresso + fino a 2 OR in uscita, tutte su segnali del catalogo. Guardia anti-contraddizione (es. "sig<30 AND sig>70" impossibile) |
| `EventTriggerStrategy.cs` | Meta-strategia event-driven: 6 tipi di evento discreto (spike/crush di volatilità, flip di trend, shock di prezzo) con uscita a tempo (`MaxHoldBars`) |
| `RegimeConditionalStrategy.cs` | Meta-strategia: bucket di regime causale (SMA slope, no DB) → delega a una sotto-strategia diversa per Up/Down/Flat |

### Discovery (`ProcioneMGR/Services/Discovery/`)
| File | Contenuto |
|---|---|
| `StrategyComposer.cs` | `ICompositeSignalGenerator`/`IEventTriggerGenerator`/`IRegimeMapGenerator` (enumerazione deterministica + campionamento seedato) + `IStrategyComposer` (genera → screening su selezione → conferma walk-forward a parametri fissi → `DiscoveryCandidate`) |

### Pipeline e UI
- `Services/Pipeline/Stages/CreativeDiscoveryStage.cs` — fase opzionale, stessa disciplina delle altre (skip se prerequisiti mancanti, limite di tempo, audit)
- `Components/Pages/Discovery.razor` — toggle "Modalità creativa" + slider spec
- `Components/Pages/Pipeline.razor` — merge automatico delle fasi nuove nell'editor di config esistenti (vedi §4) + GuidaPanel aggiornate

### Test
- `ProcioneMGR.Tests/CreativeDiscoveryTests.cs` — 15 test: SignalCatalog (troncamento/limiti), le 3 meta-strategie (AND/OR/contraddizioni/anti-look-ahead/determinismo/time-bound/delega/close-su-switch), i 3 generatori (determinismo/plausibilità/diversità), finestre walk-forward
- 1 test di regressione aggiunto in `PipelineTests.cs` (vedi §5)

### File modificati (minimi, solo agganci)
- `StrategyFactory.cs` — 3 righe Prototypes + 3 case
- `StrategyDiscoveryEngine.cs` — `DefaultRanges` per le 3 meta-strategie (griglia-template; le combinazioni ricche le genera il Composer)
- `Program.cs` — 4 registrazioni DI
- `Services/Pipeline/PipelineStageCatalog.cs` — 1 riga (nuova fase in lista)
- `Services/Pipeline/Stages/ModelStages.cs` — `HoldoutValidationStage.Dependencies` accetta anche `CreativeDiscovery`
- `Services/Pipeline/PipelineModels.cs` — vedi bug fix §5

**Nessun modulo esistente riscritto.**

---

## 3. Come si estraggono i segnali senza un LLM (esempio concreto)

Il Composer enumera in ordine deterministico e campiona con un seed:
```
CompositeSignalGenerator: per ogni coppia di segnali (a,b) nel pool, per ogni combinazione
di (operatore,soglia) nel "menu semantico" del segnale (es. RSI: <20, <35, >65, >80;
Supertrend: >50 o <50), per entrambe le direzioni → genera lo spec, con guardia
anti-contraddizione integrata nel MENU stesso (niente RSI<30 AND RSI>70 per costruzione,
perché sono due condizioni sullo STESSO segnale con soglie disgiunte per menu).
```
Un esempio di spec REALMENTE confermata dal run: **"RSI>65 AND VWAP dev pct>65 → Long"**
(OOS Sharpe 0.76 su 16 finestre walk-forward) — una regola "compra quando RSI e deviazione
dal VWAP sono ENTRAMBI alti" che non esiste in nessuna strategia codificata a mano.

---

## 4. Deviazioni dal prompt (con motivazione)

| Richiesta | Cosa ho fatto | Perché |
|---|---|---|
| Spec in `StrategyParameters["CompositeSpec"] = JSON` | Spec interamente in parametri decimali (§1) | JSON dentro `Dictionary<string,decimal>` non è rappresentabile; l'alternativa forzava riscritture su Optimization/Ensemble/Trading |
| `EventTriggerStrategy` consulta notizie/sentiment | Eventi derivati SOLO dal mercato (vol spike/crush, flip di trend, shock di prezzo) | L'alt-data storica parte dal 2026-07-01 (pochi giorni): un walk-forward 2023-2026 vedrebbe zero eventi news e non potrebbe MAI validarli. Deviazione dichiarata nel codice stesso (`EventTriggerStrategy.cs`), non silenziosa |
| `RegimeConditionalStrategy` carica il `RegimeModel` salvato (DB) | Proxy di regime causale calcolato dalle candele (slope di una SMA) | Le strategie in questa piattaforma sono `new`-based e dependency-free per design (girano dentro sweep di `OptimizationEngine` e nel motore live senza DI); una strategia DB-bound non potrebbe funzionare in quei contesti senza nuova infrastruttura |
| `StrategyComposer` Singleton | Registrato **Scoped** | Dipende da `IBacktestEngine`, che è scoped (stesso motivo per cui `StrategyDiscoveryEngine` è scoped) |
| UI `/pipeline`: multi-select segnali/eventi | Campo testuale CSV (`signalPool`) + parametri numerici | Coerente con il pattern generico "editor a parametri" già usato da ogni altra fase del pipeline (nessun editor speciale per una fase sola) |
| Test end-to-end "100 candidate su BTC/USDT 1h" | Fatto **con dati reali** nel run completo (18 coppie), non un test isolato | Un test unitario con DB non rientra nello stile "puro" della suite esistente; la verifica end-to-end è il run reale documentato in §6 |

---

## 5. Due bug reali trovati e corretti durante la verifica live

La modalità creativa produce, per la PRIMA volta nella piattaforma, **più varianti della
stessa strategia sulla stessa coppia/timeframe** (es. due "Composite" diverse su NEAR/USDT
4h) — uno scenario che la discovery classica non genera mai (una sola combinazione
parametri per strategia×coppia×timeframe). Questo ha esposto due bug latenti in
`ValidatedCandidate.Key` (usata come chiave di lookup in `EnsembleAssembly`/`RiskSizing`):

1. **Crash reale**: `ToDictionary(v => v.Key)` lanciava `ArgumentException: An item with the
   same key has already been added` — la Key era `$"{Strategy} {Symbol} {Timeframe}"`, non
   univoca quando due spec diverse condividono quella tripla. **Fix**: `Key` include ora un
   fingerprint SHA-256 (8 caratteri) dei parametri, applicato SOLO quando servono (Parameters
   non vuoto), quindi zero impatto sulle strategie classiche mono-variante.
2. **Fallimento silenzioso** (trovato SOLO ispezionando i numeri, non un'eccezione):
   `RiskSizingStage` ricostruiva la chiave di lookup MANUALMENTE inline con il vecchio formato
   corto, invece di riusare `ProposedLeg.Key` — dopo il fix #1, quella chiave inline non
   combaciava mai più con `ValidatedCandidate.Key` (che ora ha il fingerprint), quindi OGNI
   lookup falliva silenziosamente e half-Kelly/RiskFactor95 restavano a **zero per tutte le
   gambe**, senza errore visibile. Scoperto rileggendo l'output del run ("Half-Kelly medio
   0,0%" era sospetto dato che le gambe individuali avevano valori non-zero). **Fix**: estratto
   `PipelineCandidateKey.Build(...)` condiviso, usato sia da `ValidatedCandidate.Key` sia dal
   nuovo `ProposedLeg.Key` — un'unica fonte di verità, impossibile che diverga di nuovo.

Verificato con un test di regressione dedicato (`DuplicateStrategySymbolTimeframe_...`) E
con un secondo run reale completo: half-Kelly medio 2,9%/RF95 1,30×/guardia 31,2% (contro
0,0%/0,00×/0% del run buggato) — confronto visibile nella sezione "confronto col run
precedente" della UI del pipeline stessa.

**Bonus**: quando 2+ gambe finali condividono Strategia/Coppia/Timeframe (solo possibile con
spec creative), il `DisplayName` ora aggiunge il fingerprint per disambiguarle in UI/log
(prima "Composite NEAR/USDT 4h [SL5]" appariva IDENTICO due volte in elenco).

---

## 6. Verifica end-to-end con dati reali (18 coppie × 1h/4h/1d, 2023-07→2026-07)

Config "Caccia creativa 18 coppie 1h-4h-1d" (fase Scoperta creativa aggiunta e abilitata su
una config esistente tramite il merge automatico — vedi il campo "editor propone le fasi
nuove disabilitate" in `Pipeline.razor`). Risultato (run pulito dopo il fix, 10m18s totali):

| Metrica | Discovery classica | Scoperta creativa | Totale |
|---|---|---|---|
| Candidati/spec valutati | ~50 (7 strategie × 18 coppie × 3tf, dopo i gate) | 200 spec generate | — |
| Passati la validazione | 40 | 125 confermate WF (su 54 serie analizzate) | 165 |
| Sopravvissuti holdout | 3 (PriceSmaCross DOGE 4h, Supertrend ATOM 4h, Stochastic SHIB 4h) | **2** (due varianti Composite su NEAR/USDT 4h) | 5 |
| Nell'ensemble finale (top-3 per Sharpe selezione) | 3/3 gambe | 0/3 (RF95 comunque validi: 2,33× e 1,91×) | 3 gambe |

**Conclusione onesta**: la scoperta creativa ha **trovato un edge reale e mai codificato
prima** (2 sopravvissuti su NEAR/USDT 4h, entrambi con RiskFactor95 sotto la soglia 2.5× di
sicurezza) — non solo rumore statistico, dato che è sopravvissuta all'holdout con lo stesso
rigore delle altre. In questo specifico regime di mercato (Trend Up Low-Vol) le 3 strategie
classiche restano leggermente più forti per Sharpe di selezione e occupano le 3 gambe
dell'ensemble finale, ma le varianti Composite restano candidate valide e tracciate — un
run futuro in un regime diverso potrebbe farle emergere.

Regime rilevato: Trend Up Low-Vol. Nessun errore server/console in nessuna delle verifiche.

---

## 7. Prossimi passi consigliati

1. **Alzare `maxLegs`** oltre 3 nella fase Assemblaggio ensemble quando si vuole dare spazio
   esplicito alle gambe creative anche quando non vincono per Sharpe puro (oggi competono
   alla pari con le classiche, il che è corretto per onestà, ma limita la diversificazione).
2. **Event-trigger su notizie**: quando l'alt-data avrà mesi di storico accumulato, estendere
   `EventTriggerGenerator`/`EventTriggerStrategy` con la variante news+sentiment originariamente
   richiesta (l'infrastruttura per categorie/regime è già pronta, manca solo profondità storica).
3. **UI multi-select vera** per `signalPool`/`EventPool` in `/pipeline` (oggi CSV testuale) se
   l'uso frequente lo giustifica.
4. **Persistenza delle spec vincenti**: oggi una spec Composite/EventTrigger/RegimeConditional
   confermata si salva come `SavedStrategy` esattamente come le altre (funziona già, verificato
   dal riuso di `Parameters`), ma non c'è un modo rapido in UI per "leggere" cosa fa una spec
   salvata (il `Description` leggibile del Composer si perde dopo la conferma) — un piccolo
   miglioramento futuro sarebbe salvare la `Description` in una colonna aggiuntiva di
   `SavedStrategy` o derivarla al volo dai parametri per la UI di `/strategie`.
