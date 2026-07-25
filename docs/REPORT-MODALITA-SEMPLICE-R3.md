# R3 — Modalità Semplice e profili di rischio per corsia

Data: 2026-07-20 · Branch `claude/procione-trading-bot-roadmap-faa381` · Suite **1358/1358**

**Esito in una riga:** l'utente sceglie due cose — capitale e profilo di rischio — e vede *prima di
scegliere* quanto quel profilo gli costerà in commissioni; sotto, le soglie di sicurezza smettono di
essere globali e diventano una proprietà della corsia.

## Il vincolo che ha definito lo scope

Prima di R3:

- **capitale, simbolo, timeframe, strategie** erano già **per corsia** (JSON in `EnsembleStates`);
- **le soglie di rischio** (`SafetyConfiguration`) erano **globali** — un'unica sezione di
  `appsettings.json` condivisa da tutte e 3 le corsie.

Un profilo di rischio è per gran parte *soglie di rischio*. Quindi non era esprimibile come concetto
per corsia: due corsie non potevano avere appetiti al rischio diversi, e un secondo utente avrebbe
operato con le soglie del primo.

## Come è stato risolto: `LaneSafetyMonitor`

Le soglie sono lette in **~19 punti** fra `TradingEngine`, `SafetyChecker`, `PositionOpener`,
`PositionCloser`, `SignalOrderBuilder` ed `ExecutionSlicePlanner` — tutti già scritti contro
`IOptionsMonitor<SafetyConfiguration>`.

`LaneSafetyMonitor` **implementa quell'interfaccia** e sovrappone il profilo della corsia alla
configurazione globale. Il profilo entra così in vigore ovunque **senza toccare un solo punto di
lettura** — e senza il rischio, in un cambiamento a tappeto sul percorso dei soldi, di dimenticarne
uno.

L'hot-reload resta intatto: `CurrentValue` ricompone a ogni accesso partendo dal valore corrente del
monitor globale, quindi una modifica ad `appsettings.json` continua a propagarsi entro ~1s sia alle
corsie senza profilo sia ai campi che il profilo non possiede.

### Divisione delle responsabilità

| Il **profilo** possiede | Il **globale** possiede |
|---|---|
| dimensione posizione, esposizione massima | commissione reale dell'exchange |
| perdita giornaliera, drawdown massimo | margine di mantenimento |
| numero di posizioni aperte | bande di plausibilità dei fill (la rete che fermò il bug B1) |
| frequenza operativa, leva massima | conferma manuale obbligatoria in Live |

Un utente non deve poter "scegliere" la commissione del proprio exchange, e un profilo non deve
poter disattivare la conferma manuale degli ordini Live.

Persistenza: `EnsembleConfiguration.RiskProfileName`, dentro il JSON già serializzato per corsia —
**nessuna migrazione**. Le righe scritte prima di R3 deserializzano con il campo a `null`, che
significa esattamente "questa corsia non usa la Modalità Semplice" e mantiene il comportamento
precedente.

## I profili, calibrati sui numeri di R2

| | Prudente | Equilibrato | Dinamico |
|---|---|---|---|
| Capitale per operazione | 5% | 8% | 10% |
| Esposizione massima | 20% | 40% | 60% |
| Perdita giornaliera / drawdown | 2% / 10% | 4% / 15% | 6% / 20% |
| Posizioni aperte | 2 | 3 | 5 |
| Leva | nessuna | 2x | 3x |
| **Operazioni al giorno** | **≤0,5** | **≤0,75** | **≤1,5** |
| **Commissioni a pieno regime** | **~2,7%/anno** | **~6,6%/anno** | **~16%/anno** |

Il turnover è il parametro principale, e non per opinione: R2 ha misurato che il costo
dell'operatività è funzione del turnover, con cost drag dal 3,4% (~0,6 operazioni/giorno) al 77%
(~28/giorno) sulla stessa finestra.

Il tetto è **vero**, non decorativo: si traduce in `MinOrderIntervalSeconds`, che il `SafetyChecker`
applica a ogni apertura. Un test end-to-end verifica che le rientrate oltre il tetto vengano
davvero rifiutate.

> **Perché intervalli di ore non bloccano gli stop.** `SafetyChecker.Evaluate` è invocato **solo sul
> percorso di apertura**; stop loss, take profit, trailing, liquidazione ed emergency stop non
> passano da lì. Un intervallo lungo frena i nuovi ingressi e non può in nessun caso impedire a una
> posizione di essere chiusa. Verificato leggendo i chiamanti prima di calibrare.

### Errore di calibrazione corretto dal test

La prima stesura proponeva **3 / 6 / 12 operazioni al giorno**. Il test sul costo annuo lo ha
respinto: quei valori valevano **16% / 66% / 131% l'anno** di sole commissioni, cioè profili che
perdono per costruzione. La scala è stata rifatta su 0,5 / 0,75 / 1,5, che circonda il valore
misurato in R2 invece di superarlo di un ordine di grandezza.

La formula è validata contro la misura: 0,57 operazioni/giorno al 10% di size dà 6,2%/anno, contro
il 3,43% su sei mesi effettivamente misurato in R2.

### Nessun profilo "Scalping"

Il PDF di partenza lo proponeva fra le opzioni per l'utente finale. R2 lo ha escluso per misura: a
1m servono ~8,9 candele tipiche catturate per pareggiare i costi contro 1,1 a 1h, con cost drag 22
volte più alto. Un test verifica che non ricompaia, né nel catalogo né sullo schermo.

## La pagina `/bot`

Due scelte (capitale, profilo), un pulsante, e un pannello di monitoraggio essenziale.

L'elemento non ovvio è l'**anteprima**: prima di scegliere, l'utente vede cosa comporta il profilo
in parole concrete — quanto impegna per operazione in USDT, quando il bot si ferma da solo, quanto
spesso opera — e in fondo *«Commissioni a pieno regime: ~X% del capitale all'anno»*. È la lezione di
R2 tradotta per chi non conosce il dominio: operare più spesso non è gratis, ed ecco quanto costa.

Confini, verificati da test:

- si avvia **solo in Paper**; il passaggio a denaro reale non è raggiungibile da questa pagina;
- avviare senza strategie viene **rifiutato con una spiegazione**, invece di accendere un motore
  inerte e lasciare l'utente davanti a una pagina che dice "IN FUNZIONE" mentre non accade nulla;
- se il timeframe della corsia diverge da quelli preferiti dal profilo, la pagina **avvisa** che
  aspettarsi lunghi periodi di inattività è normale — senza avviso sembrerebbe rotto.

La Modalità Semplice è una **vista, non un motore parallelo**: usa gli stessi `IEnsembleManager` e
`ITradingEngine` della pagina /trading. Nessun percorso alternativo verso l'exchange, nessun
controllo di sicurezza scavalcato. Ciò che semplifica è la scelta, non l'esecuzione.

## Cosa R3 NON fa

**Il ruolo `User` resta senza accesso al trading.** La pagina è per Admin/Manager. Con capitale, PnL
e posizioni ancora condivisi a livello di corsia, dare la pagina a un terzo significherebbe farlo
operare sul denaro dell'operatore. Il profilo per corsia è il *primo* dei pezzi che servono; mancano
proprietà della corsia, isolamento del capitale e del PnL, e instradamento delle credenziali — cioè
R4.

Va inoltre nominato prima e non dopo: gestire capitale di terzi in UE ha implicazioni regolamentari
(MiFID II/MiCA) indipendenti dal codice.

## Verifica

**1358/1358** (erano 1317; +41). Nuovi file:

| File | Copre |
|---|---|
| `RiskProfileTests` (22) | invariante di sizing, coerenza dei limiti, scala fra profili, tetti di turnover contro R2, costo annuo, assenza di "Scalping", sovrapposizione e indipendenza fra corsie |
| `BotPageServiceTests` (9) | solo Paper, rifiuto senza strategie, persistenza, fallback su profilo sconosciuto, scelta del run di ricerca |
| `LaneRiskProfileEndToEndTests` (5) | il profilo governa davvero la dimensione degli ordini e il tetto di turnover; senza profilo il comportamento è quello di prima |
| `BotPageRenderTests` (5) | il costo annuo è VISIBILE, niente pulsante di avvio senza strategie, avviso sul timeframe |

**Non ancora fatto:** verifica visiva nel browser reale. Le pagine sono dietro login e la sessione
non ha credenziali; i test bUnit coprono struttura e contenuti ma non l'aspetto renderizzato.
