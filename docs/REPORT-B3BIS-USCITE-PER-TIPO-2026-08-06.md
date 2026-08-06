# B3-bis — Il verdetto sulle uscite, separato per tipo (2026-08-06)

*Riapre [REPORT-B3-EXITLAG-2026-07-28](REPORT-B3-EXITLAG-2026-07-28.md) su obiezione del
proprietario. Esito: **il verdetto aggregato nascondeva due effetti di segno opposto**.*

## 1. L'obiezione

Il proprietario ha osservato che tenere d'occhio candele più corte — 1m, 5m, o i tick — darebbe una
definizione più precisa di quando chiudere. Il report del 28 luglio sembrava averlo già escluso:
«uscire al tocco è peggio, 24 configurazioni su 24».

Ma quel verdetto **somma stop loss e take profit in un numero solo**, e la tabella di sensibilità
fa variare solo `SL%`. Il meccanismo che il report stesso nomina per spiegare il segno — «lo stop
preso sull'ombra», il prezzo che buca il livello e rientra — **ha segno opposto sui due lati**:

- su un long, il prezzo tocca lo **stop** sotto e risale ⇒ chi ha aspettato esce più in alto, meglio;
- lo stesso prezzo tocca il **target** sopra e ridiscende ⇒ chi ha aspettato esce più in basso, PEGGIO.

Il controllo sulla passeggiata aleatoria del report originale dice che in assenza di ritorno alla
media il costo dell'attesa è **zero da entrambe le parti**. Quindi l'asimmetria osservata sui dati
veri è una proprietà del mercato, e sommarla su due lati opposti la cancella.

## 2. Misura

Stesso strumento (`ProtectiveExitLagAnalyzer`), stesso controllo, stessi bracket reali. Unica
aggiunta: `ByKind`, che ripete il calcolo separando per tipo di uscita. Solo le uscite **concordi**
— dove entrambi i percorsi escono per la stessa ragione — perché fra un target sul percorso fine e
uno stop su quello a candele la differenza di prezzo misura due eventi diversi, non il ritardo.

| Corsia | Serie | Surrogato | Stop loss | Take profit |
|---|---|---|---|---|
| 1 | DOT/USDT 15m (SL 3,72% TP 10,32%) | 5m | 4.889 casi, **−7,4 bps** | 405 casi, **+10,6 bps** |
| 6 | LTC/USDT 15m | 5m | 9.994 casi, **−2,4 bps** | 3.429 casi, **+1,9 bps** |
| 5 | DOT/USDT 1h (SL 2,37% TP 5,89%) | 5m | 2.533 casi, **−10,7 bps** | 859 casi, **+3,5 bps** |
| 4 | XRP/USDT 4h (SL 3,15% TP 9,9%) | 1m | 414 casi, **−9,7 bps** | 121 casi, **−8,8 bps** |

*(negativo = aspettare la barra chiusa conviene; positivo = uscire al tocco conviene)*

## 3. Cosa dicono i numeri

**Sullo stop il verdetto del 28 luglio regge, e si rafforza**: negativo su 4 corsie su 4, con
mediane fra −2,4 e −10,7 bps. Lo stop preso sull'ombra è reale.

**Sul target il verdetto si rovescia su 3 corsie su 4.** Ed era invisibile prima per una ragione
aritmetica: sulla corsia 1 la media pesa 4.889 stop contro 405 target, **dodici a uno**. Il −6,1 bps
del report originale era il lato stop che copriva il lato target.

**L'eccezione va presa sul serio.** La corsia 4 (4h) dice −8,8 anche sul target, ed è l'unico
orizzonte lungo del gruppo. Ipotesi plausibile: su una barra da 4 ore raggiungere un target del 9,9%
richiede un movimento direzionale forte, che tende a proseguire dentro la stessa barra — mentre su
15 minuti un target vicino lo tocca il rumore, che rientra. **È un'ipotesi post-hoc su quattro punti
e un campione di 121 uscite: non la si tratti come acquisita.**

## 4. Onestà sulle grandezze

Le mediane sono **piccole**: da +1,9 a +10,6 bps per uscita. Sulla corsia 6, +1,9 bps è
plausibilmente **sotto i costi di transazione**, quindi lì il vantaggio è nominale. Solo la corsia 1
(+10,6) ha un margine che sopravvive a commissioni e slippage con un po' di respiro.

Inoltre il percorso fine registra il fill **al livello**, mentre nella realtà un ordine a mercato al
tocco paga slippage. Il vantaggio vero è quindi minore di quello misurato — la misura è ottimista
sul lato tocco, esattamente come è ottimista sul lato candela per la contabilità del fill (§5 del
report originale).

## 5. Cosa NON dice

- **Non dice che l'incidente ETC del 6 agosto si risolva accendendo i tick.** Quello era un guasto
  (il motore consumava la candela in FORMAZIONE, quindi valutava High/Low parziali) ed è corretto a
  parte. Col motore riparato quel take profit scatta alla chiusura della barra delle 08:00, perché
  l'evaluator guarda il minimo della barra: 6,31 ≤ 6,3786. La questione tocco-vs-chiusura riguarda
  solo il **prezzo ottenibile** in quell'istante, non se l'uscita avvenga.
- **Non autorizza un interruttore globale.** Con un'eccezione su quattro, la decisione è per corsia.
- **Non riguarda il trailing**, che è uno stop: resta a barra chiusa insieme all'altro.

## 6. Cosa servirebbe per accendere

`MarketData:Realtime:DriveProtectiveExits` accende oggi i tick su **entrambi** i lati: non esiste il
comando che servirebbe. Servono, nell'ordine:

1. un interruttore separato per il solo take profit (codice, non configurazione);
2. la decisione **per corsia**, con la misura di questo pannello come criterio, non un default;
3. i costi di transazione dentro il criterio, altrimenti si accende su margini che le commissioni si
   mangiano.

Nessuno di questi tre passi è stato fatto. Questo report chiude la misura, non la decisione.
