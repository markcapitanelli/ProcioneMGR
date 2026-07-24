# Report giornata operativa — 2026-07-24

*Sessione autonoma su mandato dell'utente: caccia a nuove strategie, rinnovo delle configurazioni
della Pipeline autonoma, test generale + stress test, revisione del cablaggio, e trading continuo
sulle corsie. Ogni numero viene da un run sul database/mercato reale.*

---

## 1. Caccia a nuove strategie col motore onesto (fase `huntedge`)

Nuova fase `huntedge`: caccia **creativa** (Composite/Composer) sulle 10 majors × 1h/4h col pool
**completo** di segnali — che ora include Post-Crash/Surge (id 12-13, F3), MFI (10), OBV slope
(11), Ora UTC (9) oltre ai classici. Costi onesti (fee + slippage), funding, walk-forward. I
candidati passano **tre giudici indipendenti**:

1. **Selezione** (Sharpe OOS ≥ 0,4, ≥ 15 trade)
2. **Holdout** su un periodo mai visto (2026-03 → 2026-07)
3. **Gemello sintetico** (I2): lo Sharpe reale deve battere il P95 di 15 mercati nulli

**Esito (onesto):**

| Fase | Risultato |
|---|---|
| Candidati oltre selezione + holdout | **5** (uno usa il nuovo MFI: "SOL LONG: Dist SMA50 > 65 AND MFI < 35") |
| Candidati oltre il gemello sintetico | **0** — tutti e 5 con holdout 0,95-1,43 vs P95 nullo 1,87-3,31 |

**Quarta conferma indipendente** che le strategie discovery su OHLCV non hanno edge difendibile —
e il gemello sintetico ha **catturato 5 falsi positivi** che un gate più debole (solo
selezione+holdout) avrebbe promosso. Con soli 5-7 trade nell'holdout, quegli Sharpe non sono
distinguibili dal caso. La piattaforma funziona esattamente come deve: dice la verità.

**Fix di cablaggio trovato di passaggio**: il `CompositeSignalGenerator` trattava l'MFI (id 10,
oscillatore nativo 0-100 come l'RSI) col menu percentile, perdendo la soglia < 35. Corretto: ora è
nel menu oscillatore, così la caccia esplora le soglie giuste sull'MFI.

---

## 2. Rinnovo delle configurazioni della Pipeline autonoma

**Prima**: 7 config, di cui 3 stantie/inoperabili — 4/5/6 usavano 5m/15m che R2 ha dimostrato
distrutte dai costi (cost drag fino al 77%), 1 era uno smoke test di sviluppo.

**Verifica + cancellazione + rinnovo** (le 7 vecchie eliminate, 2 nuove create):

| Id | Nome | Universo | Trigger | Miglioramenti |
|---|---|---|---|---|
| 8 | Caccia onesta majors 1h-4h | 10 majors × 1h/4h | schedulata (giornaliera) | **embargo 50 barre**, minTrades **20** (era 12), Sharpe **0,4** (era 0,3), pool completo coi segnali nuovi |
| 9 | Caccia swing majors 1d | 10 majors × 1d dal 2020 | manuale | embargo 30, minTrades 20, storia profonda |

I gate più severi sono la lezione del `huntedge`: candidati con pochi trade sono rumore, e il
gate va stretto per non promuoverli.

**Verifica "funzionante" — stress/integration test superato**: la config 8 rinnovata ha girato
**in autonomia via scheduler** e **completato tutte e 15 le fasi** in 7m24s:

```
DataIngestion→AltDataSync→FeatureEngineering→RegimeAnalysis→VolatilityRegime→PairsScreening
→MlModelTraining→StrategyDiscovery→CreativeDiscovery→HoldoutValidation→RobustnessProbe
→EnsembleAssembly→RiskSizing→NewsImpactCheck→Recommendation→ExecutionPlan
```

Conclusione prodotta (onesta e sinergica — tutte le integrazioni hanno sparato):
- **Regime**: Trend Up Low-Vol (vol 0,36, trend +0,16)
- **Volatilità**: forecast 0,44%/periodo — Bassa
- **Sentiment** (integrazione 2.0): composite +0,015 neutro; Fear&Greed 28 (Fear); per-simbolo
  funding-z, long/short-z, OI 24h (BTC funding z +0,7, OI +1,7%; ETH funding z −1,3, OI −2,9%)
- **Candidati valutati: 57 · Sopravvissuti holdout: 0 · Miglior candidato: nessuno**
- Robustezza/Ensemble/RiskSizing **saltate con grazia** (nulla da processare — comportamento corretto)
- Alert dalle news (integrazione alt-data attiva)

La pipeline è **onesta**: valuta 57 candidati e ne promuove 0, esattamente come i gate rinforzati
devono fare. Nessun falso positivo schierato.

---

## 3. Test generale + revisione del cablaggio

**Suite completa: 1481/1481 verde** (nessun flaky, ~6 min). Include i test nuovi di oggi.

**Revisione del cablaggio** (ricerca ↔ trading ↔ pipeline ↔ sentiment) — tutto collegato al
percorso decisionale, non solo presente:

| Componente | Stato |
|---|---|
| Segnali nuovi 12/13 (Post-Crash/Surge) | ✅ raggiungibili dal Composer (SignalCount 14) e dal motore LIVE (le corsie li usano) |
| MFI/OBV (10/11) | ✅ nel Composer (menu MFI corretto oggi) + catalogo |
| Fattori order-flow (TakerImbalance/AvgTradeSize) | ✅ nei prototipi → selezionabili nel ML Lab |
| Breadth interna (3.8a) | ✅ DI registrata (app + Trading host) |
| VolForecastEvaluator (1.V fase 2) | ✅ nel ML Lab |
| Funding storico firmato | ✅ consumato nel BacktestEngine |
| Sentiment 2.0 (funding/OI/L-S z-score) | ✅ nella raccomandazione della pipeline (visto dal vivo) |
| Pipeline discovery: embargo + slippage onesto | ✅ cablati (verificato nel codice) |

**Gap minore rilevato (non decisionale)**: l'event-study rigoroso (`EventStudy.StudyRigorous`, T2.7)
è consumato solo dal tool (fase `eventstudy`), non da una pagina UI. `NewsImpactAnalyzer.Analyze`
(la versione semplice) è in `/sentiment`. Non blocca nulla — l'analisi rigorosa è uno strumento di
ricerca, meglio da tool; annotato come possibile miglioramento futuro.

---

## 4. Bug trovati e corretti (solo eseguendo dal vivo)

1. **Stream futures Binance BLOCCATO in EEA (MiCA)** — la diagnostica `streamdiag` (nuova) prova
   che il WebSocket futures (`fstream.binance.com`) si connette ma **non consegna alcun dato** da
   questa postazione (0 messaggi anche su `btcusdt@aggTrade`, che dovrebbe inondare); lo **SPOT**
   invece riceve 35 msg/15s. È il market-data derivati Binance bloccato per l'EEA — stesso
   perimetro della restrizione che impedisce all'utente di operare sui futures Binance.
   **Conseguenza**: F4 (accumulo liquidazioni via `!forceOrder@arr`) non può raccogliere nulla da
   qui, e il worker riconnetteva a vuoto ogni 15 min all'infinito. **Corretto**: degrado grazioso
   — dopo 3 connessioni mute e mai un frame, lo dichiara una volta e passa a retry orario
   (`IsEndpointLikelyBlocked`, puro + Theory di regressione); si auto-ripristina da una postazione
   non bloccata.

2. **Registrazione DI del Composer mancante nel tool** — la caccia `huntedge` falliva all'avvio
   ("No service for IStrategyComposer"). Aggiunte le registrazioni (composer + 3 generatori).

*(In continuità con i fix già fatti in questa sessione: cache SignalCatalog per il motore live,
overflow Decimal MFI, pool Postgres nei test, staleness liquidazioni 120→900s.)*

---

## 5. Trading continuo — le corsie operano

Per tutta la sessione le 3 corsie hanno operato senza interruzioni (tutte su **Bitget**, come da
vincolo utente; Binance solo dati):

| Corsia | Exchange | Simbolo | Modalità | Strategia | Stato |
|---|---|---|---|---|---|
| 0 | Bitget | BTC/USDT | Paper | PostSurge 1h | RUNNING |
| 1 | Bitget | DOGE/USDT | Paper | PostSurge 1h | RUNNING |
| 2 | Bitget | ETH/USDT | **Testnet spot** | PostSurge 1h | RUNNING |

PostSurge (l'unico edge che ha passato un controllo onesto — il timing-control contro entrate
casuali, 94-99° percentile su 7/10 simboli) è in **forward test**: attende un surge >4σ per
piazzare, evento raro. È l'unica risposta onesta a "iniziare a operare": forward-test su
Paper/Testnet, e scalare solo se regge dal vivo. La caccia di oggi conferma, per la quarta volta,
che non esistono scorciatoie discovery che sopravvivano al gate completo.

---

## 6. Conclusione onesta per l'utente

La giornata ha reso la piattaforma **più onesta e più robusta**, non ha trovato una macchina da
soldi — perché, misurato quattro volte con metodi indipendenti, sui dati OHLCV in nostro possesso
non c'è un edge discovery difendibile. Ciò che è difendibile e in corso:

- **PostSurge in forward test** sulle 3 corsie (l'unico che ha passato un controllo onesto).
- **Il carry sul funding** (F1.b, misurato 5-12%/anno lordo netto costi) — il candidato
  economicamente più solido, che aspetta il `MarketContext` per diventare segnale e la strada
  operativa Bitget (spot demo funded).
- **La pipeline autonoma rinnovata** che ogni giorno caccia onestamente e promuoverà solo ciò che
  passa tre gate — oggi zero, ma è la verità, non un fallimento.

Il modo più veloce di "iniziare a operare" con basi solide resta il forward test già attivo:
lasciar girare PostSurge sulle corsie e giudicarlo sui fill reali, non su un backtest.
