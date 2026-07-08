# ProcioneMGR — Roadmap operativa (autonomia)

Data: 2026-07-05. Stato: piattaforma su PostgreSQL, 3 corsie in Paper (ATOM/DOGE/SHIB 4h),
ri-applica automatica **attiva**, supervisore AI **Claude** selezionato (si attiva appena la
`ANTHROPIC_API_KEY` è presente).

---

## 0. Il principio che regge tutto

L'automazione si ferma a un solo confine: **Testnet → Live è sempre e solo manuale, con la tua
conferma**. Tutto il resto (ricerca, validazione, scelta dell'ensemble, sizing, SL/TP, Paper→Testnet)
la piattaforma lo fa da sola. Nessun automatismo, nessuna AI, può aprire un'operazione con soldi veri
senza che tu la confermi.

---

## 1. Il ciclo autonomo — cosa fa la piattaforma, in ordine e perché

Immagina un anello che gira in continuazione. Ogni "giro" produce strategie migliori di quello prima.

**① Raccolta dati (continuo, ogni 5 min)** — *Worker: MarketDataSync*
Scarica le nuove candele delle coppie in watchlist. È il carburante: senza dati freschi, la ricerca
lavorerebbe sul passato. → *Perché prima di tutto:* ogni decisione successiva legge questi dati.

**② Ricerca schedulata (cron, es. ogni notte)** — *Worker: PipelineScheduler → PipelineEngine*
Alla scadenza del cron, parte un run del pipeline a 15 fasi: dati → feature → regime → volatilità →
pairs → ML → **scoperta strategie** → **scoperta creativa** → **validazione holdout** → **prova di
robustezza** (Monte Carlo, varianti SL/TP, Kelly) → **assemblaggio ensemble** → **sizing/rischio** →
raccomandazione. → *Perché così:* si cercano molte strategie ma se ne tengono solo quelle che
sopravvivono a un test *out-of-sample* mai visto in fase di scelta (anti-overfitting). Un run gira in
Paper: **non piazza mai ordini**.

**③ Giudizio del supervisore AI (subito dopo ogni run)** — *ClaudeSupervisorAgent + LlmSupervisorWorker*
Claude legge il risultato del run e dà un parere qualitativo + eventuali suggerimenti. Sul ciclo di
ri-applica può fare **solo una cosa operativa: porre un veto** a una sostituzione. Non può forzarla,
non può fare trading. → *Perché:* aggiunge un occhio "di buon senso" oltre ai numeri, ma non comanda.

**④ Confronto e ri-applica automatica (subito dopo il giudizio)** — *EnsembleComparator + PipelineApplier*
La piattaforma confronta il **nuovo** ensemble con quello **già schierato** sulle corsie, con numeri
oggettivi (Sharpe medio pesato, RF95, gambe, diversificazione) e una **soglia di isteresi** (serve un
miglioramento reale, non del rumore). Se il nuovo è migliore **E** il supervisore non ha posto veto,
la piattaforma **schiera da sola** il nuovo ensemble sulle corsie 0/1/2, con parametri validati esatti
+ SL/TP automatici. Altrimenti tiene il corrente. → *Perché l'isteresi:* evita di cambiare strategia
ogni giorno per differenze insignificanti (costo/instabilità). Anche qui: scrive **solo
configurazione**, non avvia trading.

**⑤ Trading Paper (continuo)** — *Worker: Trading (uno per corsia)*
Ogni corsia fa girare davvero le sue strategie sulle candele nuove, aprendo/chiudendo posizioni
**simulate** (soldi finti), con SL/TP/trailing applicati in automatico e i freni di sicurezza
(SafetyChecker, emergency stop su drawdown). → *Perché Paper:* è il banco di prova reale senza rischio,
per raccogliere performance vere prima di rischiare qualcosa.

**⑥ Monitor di decadimento (continuo)** — *StrategyDecayMonitor*
Confronta lo Sharpe realizzato dal vivo con quello atteso dal backtest: se una gamba "muore", scatta
un alert. → *Perché:* un edge può svanire; meglio accorgersene con un numero che a intuito.

**⑦ Valutazione promozioni (ogni 6 h)** — *Worker: Promotion → PromotionEvaluator/LanePromoter*
Guarda le metriche realizzate di ogni corsia Paper. Se una corsia ha fatto bene **abbastanza a lungo**
(Sharpe ≥ 0.8, ≥ 30 trade, drawdown ≤ 15%, ≥ 3 settimane, win ≥ 45%), la **promuove automaticamente a
Testnet** (stesso protocollo del Live, ma senza soldi veri). Se una corsia Testnet peggiora, la
**retrocede a Paper**. → *Perché Testnet e non Live:* Testnet convalida l'intera catena di esecuzione
reale (ordini, filtri, liquidazioni) a rischio zero. Il passaggio a Live **non avviene mai da solo**.

**⑧ Il tuo semaforo verde — Live (manuale)**
Quando una corsia ti ha convinto in Testnet, la porti a Live **tu**, dai controlli di modalità in
`/trading`. Da lì ogni singolo ordine reale resta dietro `SafetyChecker` + (se attivo) conferma
manuale ordine per ordine. → *Perché resti tu:* è l'unico punto in cui si rischiano soldi veri.

Poi l'anello riparte da ②: la ricerca notturna successiva cerca di battere l'ensemble corrente, e se
ci riesce lo sostituisce da sola. Nel tempo la piattaforma **migliora sé stessa** senza che tu debba
lanciare cacce a mano.

---

## 2. Cosa devi fare TU (il minimo indispensabile)

1. **[UNA VOLTA — ora] Fornire la API key Anthropic.** Al momento **non è impostata** da nessuna parte
   (né processo, né utente, né sistema). Finché manca, il supervisore AI e il layer advisory restano
   inattivi *senza errori* e la piattaforma decide **solo sui numeri** (perfettamente funzionante).
   Per attivarli, imposta la chiave come variabile d'ambiente **Utente** di Windows (persiste e viene
   ereditata dall'app), poi riavvia la piattaforma:
   ```powershell
   [Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-...", "User")
   ```
   (oppure incollala a me e la imposto + la testo io). La chiave **non va mai** in un file committato.

2. **[UNA VOLTA] Creare una configurazione pipeline schedulata** in `/pipeline`: universo di coppie,
   finestre Selection/Holdout, cron (es. `0 3 * * *` = ogni notte alle 03:00 UTC), modalità **Paper**,
   schedulazione **abilitata**. Da qui in poi la ricerca gira da sola.

3. **[UNA VOLTA] Configurare le credenziali Testnet** in `/settings/exchanges` (Binance Futures
   Testnet / Bitget Demo). Servono perché la promozione Paper→Testnet possa davvero partire; se
   mancano, la promozione fallisce con un messaggio chiaro (non in silenzio).

4. **[QUANDO VUOI] Dare il semaforo verde al Live.** Quando una corsia ti convince in Testnet, la
   promuovi a Live tu da `/trading` e confermi gli ordini. **È l'unica azione ricorrente che resta
   tua.** Tutto il resto è automatico.

Facoltativo: alzare/abbassare le soglie in `appsettings.json` (`EnsembleComparator`, `AutoReapply`,
`PromotionEvaluator`) se vuoi rendere la piattaforma più prudente o più reattiva.

---

## 3. Le pagine che userai

| Pagina | A cosa serve | Quanto spesso |
|---|---|---|
| `/dashboard` | Colpo d'occhio + widget "Promozioni" (corsie pronte/da retrocedere) | Ogni tanto |
| `/pipeline` | Config schedulate, storico run, **Giudizio del supervisore & ri-applica** per run | Setup + controllo |
| `/trading` | Stato corsie, posizioni, **Promozioni** (▲ a Testnet), passaggio a **Live** + conferme | Setup + semaforo Live |
| `/ensemble` | Vedere/ritoccare a mano l'ensemble per corsia (di norma non serve: lo gestisce la ri-applica) | Raramente |
| `/settings/exchanges` | Credenziali Testnet/Live cifrate | Una volta |
| `/admin/ai-supervisor` | Vedere gli advisory AI dei run | Ogni tanto |
| `/admin/backup` | Backup del database | Prima di cambi importanti |

---

## 4. Fasi consigliate (le prossime settimane)

- **Settimana 0 (adesso):** key AI impostata; config pipeline notturna creata; credenziali Testnet
  pronte. Le 3 corsie Paper girano e accumulano trade. Tu osservi.
- **Settimane 1–3:** la ricerca notturna migliora l'ensemble da sola (ri-applica automatica); il
  monitor di decadimento segnala eventuali gambe morte. Tu controlli `/pipeline` ogni tanto per leggere
  il giudizio del supervisore. Nessuna azione richiesta.
- **Settimana ~3+:** appena una corsia supera le soglie, viene **promossa a Testnet** da sola. Tu
  verifichi in `/trading` che gli ordini reali (a rischio zero) si comportino bene.
- **Quando sei convinto:** promuovi la corsia a **Live** tu, con importi piccoli, tenendo la conferma
  manuale ordine per ordine. Da lì la piattaforma opera; tu supervisioni e confermi.

L'obiettivo di regime: **tu dai solo il semaforo verde al Live**; la piattaforma fa ricerca,
validazione, deploy, sizing, protezione e promozione a Testnet in autonomia, 24/7.
