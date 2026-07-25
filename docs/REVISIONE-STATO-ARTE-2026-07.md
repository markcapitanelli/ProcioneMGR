# Revisione contro lo stato dell'arte — 2026-07-25

Revisione completa su richiesta: ogni area della piattaforma (pagine, roadmap, codice) confrontata
con i metodi migliori documentati in letteratura e nella pratica corrente, verificati online il
2026-07-25. Per ogni area: **verdetto** (allineata / oltre / gap), e dove c'è un gap, un **numero
misurato sui nostri dati** — non una citazione.

Contesto di giornata che alimenta questa revisione: il run A della pipeline (generazione senza
time-box) ha chiuso **0/87 sopravvissuti** — la generazione *era* troncata (28→87 candidati), ma
l'edge direzionale-tecnico resta assente. Ottavo esito negativo coerente.

---

## 1. Validazione anti-overfitting — ALLINEATA, in parte OLTRE

**Stato dell'arte**: la letteratura raccomanda CPCV (Combinatorial Purged Cross-Validation) come
superiore a K-fold e walk-forward semplice, il Deflated Sharpe Ratio per correggere il selection
bias da data mining, e la PBO (Probability of Backtest Overfitting) come misura di pannello.

**La piattaforma**: ha CPCV in `/optimization` e nel percorso strategie, DSR **con N effettivo**
(cluster di tentativi correlati contano come un test solo — `trialCorrelationThreshold`), PBO gate,
holdout, embargo al confine IS/OOS, e in più due cose che la maggior parte della pratica pubblicata
non ha: il **gemello sintetico** (mercati nulli con le stesse proprietà statistiche) e
l'**esperimento di controllo** (edge piantato che la pipeline deve saper trovare — e trova, DSR 1,00).

**Errore trovato e corretto oggi**: il giudice del gemello sintetico negli strumenti CLI usava 15
gemelli — con quindici campioni il "95° percentile" coincide quasi col massimo osservato, e la
soglia era essa stessa rumore. Un falso positivo (SEI/USDT) l'ha superata ed è stato smascherato
solo dal torchio (200 gemelli: dal "P95" 0,85 al P95 vero 2,51). Ora: 200 gemelli, soglia al 99°.
**La pipeline interna era già più rigorosa dei tool CLI** (DSR+PBO): il suo 0/28 era più onesto dei
"2 confermati" della caccia esterna.

## 2. Classi di edge — DIAGNOSI CONFERMATA dalla letteratura, un'azione mancante

**Stato dell'arte 2026** su cosa funziona ancora in crypto: carry sul funding (rendimenti compressi
a ~5-15% annuo in condizioni normali, contro il 10-30% di prima), stat-arb su coppie cointegrate
(studi su dati **giornalieri** 2022-24 riportano Sharpe 1,5-2,2 su BTC-ETH), market-neutral. Nessuna
fonte seria sostiene il direzionale-tecnico su OHLCV singolo.

**La piattaforma**: otto cacce direzionali-tecniche a zero — **coerente col consenso del campo**,
non un difetto nostro. E il carry misurato da noi (5-12% netto sul funding vero di Bitget) **coincide
con la forchetta compressa della letteratura**: la nostra misura è credibile.

**Il disallineamento**: il carry è l'**unica classe con edge positivo misurato**, ed è spento
(`Carry:Enabled=false`). La coerenza fra la nostra misura e la letteratura toglie l'ultima scusa:
→ **azione: accendere il forward test Paper del carry** (mai Live; Testnet resta gated dal wallet
demo).

**Secondo disallineamento**: il nostro verdetto negativo sulle coppie era su 4h/1h e su un universo
largo; gli studi positivi sono su **1d, BTC-ETH**. Non è una contraddizione — orizzonti diversi —
ma è un test economico che non abbiamo fatto: → **azione: ri-test pairs su 1d, majors**, col motore
irrigidito (log-prezzi, ADF).

## 3. Rilevamento dei regimi — GAP, e adesso è MISURATO

**Stato dell'arte**: i regimi da K-means durano in media ~2 giorni — non operabili coi costi reali;
gli HMM 21-40 giorni; l'ibrido K-means→HMM (K-means per i cluster, HMM per le transizioni
probabilistiche) batte entrambi negli studi 2026.

**Misurato oggi sui nostri dati** (nuova fase `regimepersistence`, BTC/USDT 1h, 365 giorni, 8.710
barre): i nostri regimi — che hanno già smoothing a maggioranza mobile + conferma a 3 candele —
durano in **mediana 2,2 giorni** (media 3,8), con **26 tratti su 96 sotto il giorno** e ~8
transizioni al mese. Lo smoothing **non** ci porta nella zona HMM: siamo esattamente sul valore che
la letteratura chiama "non operabile".

Conseguenze:
- la scelta di tenere il **router in osservazione** è confermata a posteriori: regole su regimi che
  cambiano 8 volte al mese pagherebbero commissioni sul rumore;
- il candidato di potenziamento più fondato di questa revisione è l'**ibrido K-means→HMM**, da
  misurare contro l'attuale su due metriche certificabili: persistenza (mediana in giorni) e
  stabilità della performance per-regime delle strategie. Se non allunga la persistenza di almeno
  un ordine di grandezza, non sostituisce niente.

## 4. Etichettatura ML — piano GIUSTO, aspettative da tarare

**Stato dell'arte**: risultati misti sul triple-barrier da solo (miglioramenti modesti, Sharpe
debole); la combinazione **campionamento event-based + triple-barrier + meta-labeling** migliora;
lo studio 2025 su crypto con **barre informative** + TBL + deep learning è positivo; l'AEDL
(etichettatura adattiva event-driven) fa 0,48 di Sharpe medio dove il TBL base fa −0,03.

**La piattaforma**: M1 (triple-barrier + meta-labeling) è aperto ⭐ nella roadmap intraday; M2
(barre informative) è "da rivalutare". La letteratura suggerisce che **M1 da solo delude** e che il
valore sta nella combinazione: → **azione: M1 e M2 vanno fatte insieme**, con aspettative tarate
sul "modesto" della letteratura, non sul miracolo.

## 5. Rischio e sizing — ALLINEATA

Rischio per trade 1-2%, tetti giornalieri, kill-switch, half-Kelly advisory, vol targeting: tutto
presente e conforme alla pratica raccomandata. In più: il nostro vol targeting è **default-off
perché la nostra stessa misura** ha mostrato che non replica su singolo simbolo — più onesto della
divulgazione, che lo dà per scontato. Il limite di correlazione fra corsie (Fase 2, acceso a 30%)
copre il buco che restava.

## 6. Esecuzione e TCA — OLTRE la pratica comune retail

**Stato dell'arte**: l'implementation shortfall contro il prezzo di decisione è il benchmark
preferito dalla letteratura (superiore al VWAP come misura del costo di una *decisione*); i piccoli
ordini possono riempirsi meglio di metà spread.

**La piattaforma**: da Fase 1 misura lo shortfall su **tutti** gli ordini reali (non solo i job a
fette), la latenza a P50/P95/P99, e confronta assunto-vs-pagato in `/metrics`. Il fill model maker
considera la coda (penetrazione, non touch). Per una piattaforma retail è oltre la prassi. Le
assunzioni di costo (5 bps slippage per fill) risultano conservative rispetto alla nota sui piccoli
ordini — va bene così: meglio sovrastimare un costo.

## 7. Dati — ALLINEATA al piano dichiarato

30 simboli × 5 timeframe, funding storico firmato, liquidazioni in accumulo, sentiment
multi-fonte. Il gap microstruttura (order book, tape) è noto, **misurato** (124× lo storico per il
tape grezzo di 3 simboli) e pianificato con pilota a termine e misura di valore (Fase 3 rivista).
Nessuna correzione dalla letteratura: la sequenza "aggrega all'origine, pilota, misura il valore
predittivo incrementale" è la forma corretta.

## 8. Pagine e UI — verificate dal vivo oggi

Tutte le pagine rispondono (302 di login dove atteso); i tre bug trovati nel giro browser (asset
statici in Production, JS Bootstrap assente, modalità che non seguiva la corsia) sono corretti e
coperti da regressione. Il fix della finestra di etichettatura ha effetto visibile: la
configurazione 1d ora mostra il regime reale ("Sideways") invece di "sconosciuto" perenne.

---

## 9. Gli errori che stavamo facendo (sintesi onesta)

1. **Giudice nullo debole nei tool CLI** (15 gemelli, soglia al 95° con ~15.000 tentativi) —
   corretto: 200 gemelli, 99°.
2. **Unità disallineate** nella finestra di etichettatura dei regimi (giorni vs barre) — il regime
   1d era "sconosciuto" per sempre, in silenzio. Corretto con minimo derivato dal timeframe.
3. **Generazione time-boxed** nella scoperta creativa (10 minuti su run da 8) — la pipeline
   valutava 28 candidati credendo di aver finito. Config A senza tetto: 87 candidati (e comunque 0
   sopravvissuti: il troncamento era reale, l'edge no).
4. **Regimi K-means troppo brevi per operare** — ora misurato (mediana 2,2 giorni), router
   giustamente in osservazione.
5. **Famiglia EventTrigger con parametro inerte** (`Threshold` non lega sugli eventi flip): genera
   tentativi quasi-duplicati che gonfiano il conteggio dei trial e producono i falsi positivi più
   convincenti. → azione: nel generatore, non variare `Threshold` sugli eventi in cui non lega
   (o farlo dichiarare dalla strategia), così il conteggio dei trial del DSR resta onesto.

## 10. Cosa potenziare (in ordine di valore atteso / costo)

| # | Azione | Perché | Costo |
|---|---|---|---|
| 1 | **Carry Paper ON** | unica classe con edge misurato positivo, coerente con la letteratura (5-15%) | config |
| 2 | **Ri-test pairs 1d majors** | gli studi positivi sono su 1d; il nostro negativo era su 4h/1h | ore |
| 3 | **Fix generatore EventTrigger** (parametri inerte) | igiene del conteggio trial, meno falsi positivi | ore |
| 4 | **Ibrido K-means→HMM** come candidato misurato | persistenza 2,2 gg vs 21-40 della letteratura | giorni, gated |
| 5 | **M1+M2 insieme** (TBL+meta + barre informative) | la letteratura premia la combinazione, non i pezzi | da roadmap intraday |
| 6 | Fase 3 rivista (pilota microstruttura) | già pianificata, nessun cambiamento | da roadmap |

## 11. Cosa NON cambiare (conferme)

- **RL**: nessuna evidenza nuova che ribalti il verdetto QLIB-5.
- **SOR**: resta senza senso con una venue sola.
- **Altre cacce direzionali-tecniche su majors 1h/4h**: otto zeri nostri + consenso del campo. La
  macchina resta pronta, ma ripetere la stessa domanda non è ricerca.
- **L'impianto di validazione**: è il pezzo migliore della piattaforma, e oggi si è difeso da solo
  — ha bocciato in giornata un falso positivo che aveva superato un giudice esterno più debole.

### Fonti verificate (2026-07-25)

- CPCV/DSR/PBO: [SSRN — Backtest overfitting in the ML era](https://papers.ssrn.com/sol3/Delivery.cfm/SSRN_ID4686376_code4361537.pdf?abstractid=4686376&mirid=1), [Bailey — The Deflated Sharpe Ratio](https://www.davidhbailey.com/dhbpapers/deflated-sharpe.pdf), [CPCV insights](https://www.quantbeckman.com/p/with-code-combinatorial-purged-cross)
- Classi di edge 2026: [Quantt — Crypto quant strategies 2026](https://www.quantt.co.uk/resources/crypto-quant-strategies-2026), [Funding rate arbitrage 2026](https://arbitrageghost.medium.com/funding-rate-arbitrage-in-2026-the-complete-guide-with-real-calculations-40e6cf341e52), [Stoic — market neutral 2026](https://stoic.ai/blog/best-market-neutral-strategies-to-consider-in-2026/)
- Regimi: [K-means vs HMM su equity 2015-2026](https://github.com/francescodemarte/regime-detection), [K-means+HMM ibrido su Bitcoin](https://jdmdc.com/index.php/JDMDC/article/view/57), [Macrosynergy — classifying market regimes](https://macrosynergy.com/research/classifying-market-regimes/)
- Labeling ML: [Springer — information-driven bars + TBL + DL su crypto](https://link.springer.com/article/10.1186/s40854-025-00866-w), [Hudson & Thames — meta-labeling efficacy](https://hudsonthames.org/does-meta-labeling-add-to-signal-efficacy-triple-barrier-method/)
- Pairs su 1d: [IJSRA — stat-arb cointegrazione crypto](https://ijsra.net/sites/default/files/fulltext_pdf/IJSRA-2026-0283.pdf), [Copula-based cointegrated pairs](https://link.springer.com/article/10.1186/s40854-024-00702-7)
- TCA: [QuestDB — implementation shortfall](https://questdb.com/glossary/implementation-shortfall-analysis/), [Kearns — Implementation shortfall algorithms](https://www.cis.upenn.edu/~mkearns/finread/impshort.pdf)
