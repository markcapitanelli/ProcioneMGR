# Roadmap Rendimento — attuazione del §10 della revisione (2026-07-25)

**Settima roadmap**, la più corta: quattro azioni, tutte già motivate e quantificate dalla
[revisione contro lo stato dell'arte](REVISIONE-STATO-ARTE-2026-07.md). Qui ci sono solo il *come*,
i *gate* e l'esito. Ordine di esecuzione = costo crescente, così ogni passo produce un risultato
prima che il successivo cominci.

| # | Azione | Gate per dire "fatto" | Esito |
|---|---|---|---|
| R1 | **Carry Paper ON** | worker avviato, simboli valutati, decisioni registrate dal vivo | ✅ vedi §1 |
| R2 | **Ri-test pairs su 1d** | verdetto misurato su majors 1d (selezione+holdout, costi onesti) | ✅ vedi §2 |
| R3 | **Fix generatore EventTrigger** | zero candidati flip duplicati; suite verde | ✅ vedi §3 |
| R4 | **Ibrido K-means→HMM** | persistenza mediana ≥ ~10× l'attuale (2,2 gg → ≥ 20 gg) SENZA collassare i regimi (tutti gli stati visitati, accordo con le etichette grezze > 55%) | vedi §4 |

Il carry resta **mai Live** (il parsing rifiuta il valore per costruzione); l'HMM non tocca il
detector finché il gate non passa: prima si misura, poi si cabla.

---

## 1. R1 — Carry Paper ON

Il carry è l'unica classe con edge positivo **misurato** (5-12% netto sul funding vero), coerente
con la forchetta compressa della letteratura (5-15%). Era spento per prudenza da forward-test mai
avviato: da oggi il `CarryWorker` gira in Paper — valuta il funding reale ogni ora e registra le
decisioni che *avrebbe* preso, senza toccare alcun exchange. È il dato che mancava per decidere il
passo Testnet (che resta gated dal wallet demo Bitget da finanziare).

Modifica: `Carry:Enabled=true` nella configurazione reale (Mode=Paper). Verifica dal vivo: log di
avvio del worker + prima valutazione sui 6 simboli.

## 2. R2 — Pairs su 1d

Il nostro verdetto negativo era su 4h/1h; gli studi positivi (Sharpe 1,5-2,2) usano dati
**giornalieri**. La fase `pairs` del tool ora accetta il timeframe: su 1d la selezione parte dal
2022 (stessa profondità degli studi), holdout da marzo 2026, motore irrigidito (log-prezzi, ADF,
banda di elasticità, costi onesti).

## 3. R3 — Generatore EventTrigger onesto

`Threshold` non lega sugli eventi flip (2=FlipUp, 3=FlipDown: sono cambi di segno del Supertrend,
non percentili), ma il generatore emetteva comunque le varianti 85/95 — **duplicati esatti** che
gonfiano il conteggio dei trial del DSR e producono i "confermati" doppi visti nella caccia densa.
Fix: sugli eventi flip si emette una sola variante canonica. I candidati flip si dimezzano, il
conteggio dei tentativi torna onesto.

## 4. R4 — Ibrido K-means→HMM (gated)

Il gap misurato: i nostri regimi durano in mediana 2,2 giorni (letteratura: K-means ~2 gg "non
operabile", HMM 21-40 gg). L'ibrido della letteratura tiene i cluster K-means (interpretabili,
già profilati per strategia) e sostituisce lo smoothing con una decodifica HMM delle transizioni.

Implementazione: `StickyHmmSmoother` — Viterbi su stati = cluster K-means, osservazioni = etichette
grezze per-barra, emissione a rumore di etichetta (p di emettere la propria etichetta, il resto
uniforme), transizione "sticky" (autotransizione ρ; durata attesa = 1/(1−ρ)). Puro e testabile;
ρ e p si scelgono misurando il compromesso persistenza/accordo, non a gusto.

**Gate**: mediana ≥ 20 giorni E accordo con le etichette grezze > 55% E tutti i K stati ancora
visitati. Se passa → opzione opt-in nel detector (default off, come tutto). Se non passa → resta
uno strumento di analisi e il router continua in osservazione.

---

## Esiti (2026-07-25, stessa giornata)

### R3 — FATTO
Il generatore emette una sola variante di `Threshold` sugli eventi flip. Quattro test, incluso
quello che verifica la **premessa** sulla strategia reale (segnali identici a soglia 60 e 95 su un
flip): se un giorno la soglia cominciasse a legare sui flip, quel test fallirebbe e il fix andrebbe
ritirato insieme. I candidati flip si dimezzano; il conteggio dei trial del DSR torna onesto.

### R1 — FATTO e verificato dal vivo
`Carry:Enabled=true` (Mode=Paper) nella configurazione reale, con backup a fianco. La verifica non
è una riga di log ma la **decisione stessa**, ricalcolata dagli stessi dati con la stessa funzione
pura del worker (nuova fase `carrynow`):

| Simbolo | Funding annualizzato | Decisione Paper |
|---|---|---|
| BTC | 5,29% | **Open** |
| BNB | 8,39% | **Open** |
| DOGE | 5,06% | **Open** |
| ETH / SOL / XRP | 0,6-2,5% | Hold |

Tre carry sopra soglia già al primo giro: il forward test ha cominciato a produrre il dato che
mancava. **Scoperta collaterale della verifica**: il funding di SOL/BNB/XRP/DOGE era fermo al
23/07 perché il worker del sentiment — l'unica fonte che AGGIORNA il funding — seguiva solo
BTC/ETH. Ora `Sentiment:Symbols` copre i 6 simboli del carry: senza, il carry avrebbe deciso su
dati vecchi di giorni, che per un edge sul funding è come decidere bendati.

### R2 — VERDETTO: non replica sui nostri dati
Ri-test su 1d, 20 majors, selezione 2022→2026-03 (stessa profondità degli studi), motore irrigidito:

- coppie operabili in selezione: **5/190 (3%)** — il test irrigidito (log-prezzi, ADF, banda di
  elasticità) è selettivo come deve essere, altro che "cointegrazione liberale";
- **BTC-ETH, la coppia degli studi, non passa nemmeno la selezione** sulla nostra finestra
  2022-2026 — gli studi si fermano al 2024, e la relazione da allora è cambiata;
- holdout: **0/5 sopravvissuti**, ma con un limite strutturale dichiarato: in 4 mesi di holdout
  giornaliero le coppie generano 1-2 operazioni, sotto qualunque soglia di significatività. Il
  verdetto onesto è: *la premessa degli studi non si presenta più nei nostri dati*, e un giudizio
  definitivo richiederebbe un forward di un anno — che non vale la candela con 5 coppie su 190.

### R4 — GATE NON SUPERATO, e il negativo restringe il campo
Lo `StickyHmmSmoother` è costruito e testato (8 test: denoising, cambi veri che sopravvivono,
monotonia in ρ, causalità verificata). Il gate, sull'intera griglia dichiarata (ρ ∈ {0,99…0,999},
p ∈ {0,35…0,75}), **fallisce senza ambiguità**:

| decodifica | mediana | transizioni | accordo |
|---|---|---|---|
| attuale (rolling+3) | 2,2 gg | 95 | 44% |
| miglior causale (ρ=0,999, p=0,35) | **1,5 gg** | 208 | 68% |
| miglior viterbi (non causale) | 2,6 gg | 101 | 81% |

Il dato inatteso che decide: le etichette **grezze** oscillano così tanto che perfino un prior con
durata attesa di 41 giorni (ρ=0,999) le segue. Il problema non è la decodifica della sequenza — è
**a monte, nei cluster**: le feature (finestre 20-50 barre) attraversano i confini dei centroidi con
frequenza giornaliera, e nessuna rietichettatura a valle può fabbricare regimi da 20 giorni da
osservazioni che si alternano ogni giorno. Gli ibridi che funzionano in letteratura fittano l'HMM
**sulle feature** (emissioni gaussiane, Baum-Welch), imparando stati e dinamica insieme: è un
lavoro diverso e più grosso, e questo negativo economico lo dimostra *prima* di costruirlo.

Come da gate: lo smoother resta strumento di analisi, il detector non cambia, il **router resta in
osservazione**. Nono esito negativo della piattaforma, e come gli altri: più informativo di un
falso sì.
