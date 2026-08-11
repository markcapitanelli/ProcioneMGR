# Report di chiusura — ondata Risanamento (Fase 6, 2026-08-11)

Chiude il `PRD-RISANAMENTO-2026-08.md` (quattordicesima ondata). Le fasi 0-5 sono nel registro del
PRD; qui vivono le tre verifiche finali che la Fase 6 chiedeva: la rimisura del carry, la tabella
di verifica a 4 livelli per le fasi 1-2, e il diff dei documenti meccanici come changelog.

---

## 1. Rimisura del carry col funding reale — numero storico RIPRODOTTO

**Prima la sorpresa, dichiarata**: alla prima rimisura il database conteneva solo **~14 mesi** di
funding (1.250 eventi/simbolo dal 2025-06) — il backfill profondo dal 2019 (T0.2) era andato
perso **di nuovo**, in silenzio, nonostante l'esenzione dalla purge sia al suo posto (la causa
radice non è ricostruibile con certezza: la finestra osservata combacia con `MetricRetentionDays`
= 400 giorni, il sospetto è una perdita della tabella con re-backfill della sola finestra del
worker). Ripristinato con `fundingbackfill` (idempotente): **+35.006 punti**, storia di nuovo dal
2019-09.

**Confronto documentato** (soglie 5% ingresso / 0% uscita, costi 0,42%/episodio — identici):

| Sym | netto %/anno — storico 2026-07-24 | netto %/anno — rimisura 2026-08-11 | eventi (st. → oggi) |
|---|---:|---:|---|
| BTC | 9,0 | **9,00** | 7.526 → 7.583 |
| ETH | 11,9 | **11,80** | 7.292 → 7.349 |
| SOL | 5,6 | **5,53** | 6.493 → 6.550 |
| BNB | 5,6 | **5,62** | 7.067 → 7.124 |
| XRP | 12,1 | **11,93** | 7.172 → 7.229 |
| DOGE | 7,9 | **7,83** | 6.614 → 6.671 |

Robustezza (isteresi 10%/3%): storico 5,0-12,5 → rimisura **4,97-12,43** %/anno. Il numero
storico è riprodotto entro il rumore delle ~3 settimane di dati in più: **la catena dati è
intatta e la misura è stabile** attraverso tutta l'ondata (fee-clamp di PR #76 compreso: il
carry non passa dal BacktestEngine, e il backtest a leva ora legge la STESSA serie storica —
badge «funding serie storica (94 eventi, firmati)» verificato a browser).

**Due letture di merito emerse dall'incidente**:
- il tratto recente da solo (2025-06 → oggi) dà carry **negativo al netto** con soglie 5/0
  (solo BTC positivo, +2,3%/anno): il regime di funding attuale è più magro del 2019-2024.
  Aspettativa corretta per il forward test Paper: il grosso del numero storico viene dai
  regimi caldi (2021, 2024), non dal presente;
- una serie-patrimonio può sparire senza che nessun controllo lo dica: serve un guardiano
  sulla PROFONDITÀ delle serie esenti (FundingRate, FearGreed, Liquidations), non solo
  sull'esenzione dalla purge.

## 2. Verifica a 4 livelli — fasi 1-2

L1 = unità/riferimento indipendente · L2 = controllo sul rumore/edge piantato ·
L3 = integrazione reale (Postgres/Testcontainers o cluster) · L4 = browser sull'app viva.

| Fix | L1 | L2 | L3 | L4 |
|---|---|---|---|---|
| D-01 conteggio DSR | TrialsCountPropagationTests (4) | retrocompat a trialsExplored=0 | PipelineTests | run reale: «N=12.263 effettivi, 18.394 provate» (2026-08-11) |
| D-02 esposizione nozionale | SafetyCheckerFuturesExposureTests (4) | — (aritmetica pura) | motore in-cluster su build locale (Fase 4) | /trading collegato al motore nuovo |
| C-02 default B3 | SecurityDefaultsTests (9 default) | il default È la misura B3 (24/24) | — | misura accanto al toggle, verificata |
| E-01 funding nel backtest | FundingHistoryTests | A/B: funding zero ⇒ netto = −costi | Postgres | badge «serie storica (94 eventi, firmati)» |
| D-03 range obbligati | PipelineRangeValidationTests (6) | fixture {} respinte (CI rossa poi corretta) | CI Testcontainers | — |
| D-04 stoppini specchiati | NullTwinWickAsymmetryTests (3) | è esso stesso il nullo | — | — |
| 2.1 ISymbolCatalog | SymbolCatalogTests + SymbolScanGuardTests | — | Postgres | DISTINCT una sola volta (2026-08-11) |
| 2.6 filtro incrementale | IncrementalFactorFilterTests (6) | edge piantato: echo scartato, indipendente tenuto | stage test | dal vivo: 10 fattori → 1 tenuto, 9 ridondanti |
| 2.7 JumpModel dietro flag | RegimeModelSelectionTests (11) | default KMeans bit-identico | — | pannello Model/λ + contratto C1 |
| 2.8 optimizer per nome | PortfolioOptimizerSelectionTests (4) | profili decorrelati distinguono HRP/ERC | — | parametro in /pipeline |
| 2.9 deriva sul Champion | FactorDriftMonitorTests (+3) | Staging esclusi; FactorsJson rotto non rompe | Postgres | alert deriva in Home |

Suite completa alla chiusura dell'ondata: **2.474/2.474** (Docker/Testcontainers).

## 3. Documenti meccanici rigenerati — il diff è il changelog

Rigenerati 21-27 sulla base di master a fine ondata (28 invariato): **+426/−150 righe**.
In sintesi il diff racconta: API nuove (`IncrementalFactorFilter`, `SaveValueAsync`,
`ChampionSpecsAsync`, `BitgetAttestationOptions`, keyring di rotazione), 2.164 metodi di test
(dai 2.096 di inizio ondata), chiavi di configurazione tutte con UI (DeliberatelyNotExposed
vuota), schema invariato (34 entità), 2 cicli di modulo noti e invariati.

## 4. Cosa resta aperto (fuori ondata, tracciato)

- guardiano sulla profondità delle serie-patrimonio (chip creato);
- `git filter-repo` differito per decisione (storia innocua a segreti ruotati);
- Job one-shot `strategyhunter-discover`: template nuovo alla prossima ricreazione;
- fallback di connessione nel tool PlatformExpand rimosso in questa chiusura (era la password
  PRE-rotazione hardcoded: morta e fuorviante — ora il tool esige la env var).
