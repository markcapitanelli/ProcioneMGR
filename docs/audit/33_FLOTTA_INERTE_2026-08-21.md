# La flotta era viva e non poteva aprire — 2026-08-21

> Trovato mentre liberavo e correggevo le corsie, su richiesta del proprietario.
> Non è un difetto nuovo introdotto ieri: è vecchio quanto la ripresa dopo riavvio.

---

## 1. Il fatto, prima delle spiegazioni

Alle 03:00 UTC del 2026-08-21, sul database di produzione:

| corsia | stato | simbolo | ultima candela vista | **ultimo ordine** |
|---|---|---|---|---|
| 1 | in esecuzione | DOT/USDT 15m | 2026-08-21 02:30 | **2026-07-27 12:15** |
| 2 | in esecuzione | ADA/USDT 4h | 2026-08-20 20:00 | 2026-08-10 16:00 |
| 3 | in esecuzione | ETC/USDT 4h | 2026-08-20 20:00 | 2026-07-26 16:00 |
| 4 | in esecuzione | XRP/USDT 4h | 2026-08-20 20:00 | 2026-07-21 12:00 |
| 5 | in esecuzione | UNI/USDT 4h | 2026-08-20 20:00 | 2026-08-14 08:00 |

Il feed è **puntuale su tutte e cinque**. In sette giorni l'intera flotta ha piazzato
**un solo ordine**. Su quattro corsie su cinque `LastOrderUtc` è **precedente a
`StartedAtUtc`**: dall'avvio della sessione non è mai partito niente.

Non era il mercato. Sulla sola corsia 1 — `RsiOversold` DOT/USDT 15m, soglia oversold 20 —
l'RSI a 14 periodi è sceso **sotto 20 cinquantasette volte** nei 25 giorni dall'avvio
(2.388 candele; minimo 7,69). Ogni volta la strategia emette `Signal.Long`. Nessun ordine.

E nessuna riga, da nessuna parte, lo diceva: `/trading` mostrava cinque corsie verdi.

---

## 2. La causa

`TradingEngine` tiene tre cose che **solo `StartAsync` valorizza**:

```
_active    le gambe attive, fotografate all'avvio
_creds     le credenziali exchange (Testnet/Live)
_filters   i filtri LOT_SIZE/PRICE del simbolo (Testnet/Live)
```

`EnsureLoadedAsync` — la strada percorsa a **ogni riavvio del processo** — restaura
`_state` (compreso `IsRunning = true`), le posizioni aperte e i piani di esecuzione.
**Nessuna delle tre.**

Il risultato, dopo ogni riavvio:

- `ProcessCandleAsync` supera il cancello `if (!_state.IsRunning) return;`
- riempie il buffer, riconcilia, applica le uscite protettive sulle posizioni **già aperte**
- arriva a `foreach (var strat in _active)` — e `_active` è **vuota**

La corsia marca a mercato, onora gli stop di ciò che era già aperto, aggiorna il segnalibro
delle candele. Sembra viva sotto ogni misura. **Non può aprire più nulla, per sempre.**

Nessuna eccezione, nessun log, nessun contatore a zero: il ciclo semplicemente non ha niente
su cui girare.

### La verifica sul motore vivo, non sul codice

Il motore non gira nel guscio locale: gira nel pod `procionemgr-trading` in cluster. Nei suoi
log, per l'istanza corrente:

```
TradingEngine: stato ripristinato dal DB (running=True,  emergency=False, posizioni=0).   ×5
TradingEngine: stato ripristinato dal DB (running=False, emergency=False, posizioni=0).   ×3
TradingWorker: sessione Paper ripresa dal segnalibro, cursore da 2026-08-21 02:30:00Z.    ×5
```

**Zero righe `«Trading engine avviato in modalità … con N strategie»`.** `StartAsync` non è mai
stato chiamato in questa istanza: cinque corsie restaurate vive, cinque feed ripresi
correttamente dal segnalibro, e `_active` vuota su tutte e cinque.

Il pod ha **27 restart in 3 giorni e 9 ore** (~8 al giorno, leader election del cluster
kind). Ogni restart azzera le gambe. La flotta non è stata inerte per sette giorni: lo è stata
**quasi ininterrottamente**, con finestre operative lunghe quanto l'intervallo fra un
`StartAsync` manuale e il restart successivo.

### E la scoperta che viene con essa: **il motore vivo è fermo al 17 agosto**

L'immagine del pod è `procionemgr-trading:local-9a3e8dbe`, cioè il commit **9a3e8db del
2026-08-17 19:04**. Verificato leggendo il codice a quel commit:

| cosa | nel motore vivo? |
|---|---|
| `IPositionMirroringStrategy` (specchio della posizione, corretto il 2026-08-20) | **NO** |
| `RunningStrategyIds` (I13a, il confronto configurazione-vs-fatto) | **NO** |

Quindi:

- **La correzione dello specchio della posizione non sta girando.** `GridMeanReversion` apriva
  e non prendeva mai il proprio profitto perché lo specchio ripartiva «flat» a ogni barra; la
  correzione è in master dal 2026-08-20, ma le corsie 4 e 5 — quelle che ho lasciato apposta
  intatte come «unico test pulito della correzione» — stanno eseguendo la **versione rotta**.
  Quell'esperimento non è mai partito.
- **Tutto ciò che è stato corretto fra il 18 e il 21 agosto non ha mai raggiunto la cosa che
  opera.** La revisione degli algoritmi, la guardia A2, il funding in selezione, lo specchio:
  vivono in master e nel guscio locale, non nel processo che decide gli ordini.

Il guscio locale (`/trading`, `/ensemble`) è aggiornato; il core no. È l'altra faccia
dell'architettura core caldo/guscio freddo: le due metà si aggiornano per strade diverse, e
l'immagine del core si promuove **a mano**.

### Perché nessuno se n'è accorto

Il rilevatore **esisteva**. Il 2026-08-19 l'item I13a aveva aggiunto `RunningStrategyIds`
allo stato del motore proprio per confrontare *ciò che è configurato* con *ciò che gira*, e
`/ensemble` ha i due riquadri che lo dicono.

Ma `EnsemblePageService` conteneva questa nota:

> «un motore in corsa esegue sempre almeno una gamba — altrimenti non sarebbe partito
> (StartAsync fotografa le attive). Quindi "in corsa + lista vuota" è impossibile se il campo
> c'è, e diagnostico se non c'è.»

**Il presupposto è falso.** `IsRunning` non lo scrive solo `StartAsync`: lo *restaura* dal
database `EnsureLoadedAsync`. «In corsa + lista vuota» non era impossibile — era lo **stato
normale di ogni corsia dopo un riavvio**, ed era esattamente il guasto.

Il ramo lo prendeva e lo convertiva in `null`, cioè in «non te lo so dire»: il riquadro
grigio che rassicura. È la stessa classe del filone E — *controlli che rassicurano a
prescindere dalla realtà* — nella sua forma più beffarda: il sensore giusto, puntato sul
segnale giusto, con istruzione di scartarlo.

---

## 3. Il gemello peggiore, sulla strada che non può fallire aperta

Stessa radice, conseguenza diversa. In `PositionCloser`, entrambe le chiusure — spot e
futures — piazzano l'ordine reale sotto questa guardia:

```csharp
if (state.Mode != TradingMode.Paper && credsOrNull is TradingCredentials creds)
{
    // ... ordine market di chiusura verso l'exchange
}
```

Con le credenziali **null** la condizione è falsa e il codice **prosegue**: rimuove la
posizione locale, scrive il `TradeRecord`, registra `ClosePosition`. **Senza aver mai chiuso
nulla sull'exchange.**

Su Testnet/Live, dopo un riavvio, un'uscita protettiva avrebbe quindi: cancellato la traccia
locale di un'esposizione **ancora aperta e reale**, e scritto a registro un trade mai
avvenuto. È fail-**open** sull'unica strada che non può esserlo — regola 4.

Oggi nessuna corsia è Live e nessuna è Testnet, quindi non è costato niente. È latente, ed è
raggiungibile da qualunque promozione.

---

## 4. Le correzioni

### D1 — la sessione porta con sé le proprie gambe

Nuova colonna `TradingEngineState.ActiveStrategiesJson`: `StartAsync` ci scrive la fotografia
delle gambe attive, `EnsureLoadedAsync` la rilegge.

**Congelata, non riletta dalla configurazione viva**, per la stessa ragione per cui lo sono
già `Symbol` e `Timeframe`: la configurazione può essere riscritta mentre la corsia opera
(auto-apply della flotta, o un semplice Salva da `/ensemble`), e una ripresa che leggesse di
lì farebbe operare gambe che nessun backtest ha validato per quella sessione. È «si valida
una strategia e se ne opera un'altra» — la classe corretta il 2026-08-20 sullo specchio della
posizione.

Sulle righe scritte prima della colonna la fotografia manca. In quel caso la ripresa
**ripiega sulla configurazione viva e lo dichiara**: log critico + `ActiveLegsRestoredFromConfig`
in audit. Una corsia viva e muta è peggio di una corsia viva e dichiaratamente approssimata.

### D2 — una chiusura non-Paper senza credenziali non si finalizza

`RefusesLocalOnlyCloseAsync`: se la modalità non è Paper e le credenziali mancano, la
chiusura **non avviene**. La posizione resta, si grida (`LogCritical`), si registra
`CloseRefusedNoCredentials`, e si ritenta alla prossima candela.

Stessa forma del ramo «chiusura incerta» che già esisteva: *una chiusura non si finalizza mai
da uno stato ignoto*.

### D3 — credenziali e filtri tornano anche loro

`RestoreExchangeContextAsync` li ricarica alla ripresa per Testnet/Live. Se non ci riesce,
grida: le aperture restano bloccate dal presidio R15 (fail-closed, già corretto), e le
chiusure dal presidio D2.

### Il rilevatore, rimesso a puntare

La nota falsa è stata sostituita con la spiegazione di perché era falsa. Il ramo «non
determinabile» **resta**, perché l'altro caso è reale — un motore remoto con un'immagine
precedente al campo 25 risponde vuoto mentre esegue, e proto3 non distingue *assente* da
*vuoto*. Ma ora la pagina **nomina entrambe le cause** invece di affermarne una sola, e la
prima che nomina è quella che è appena costata sette giorni di flotta ferma.

---

## 5. Sotto rete

`ProcioneMGR.Tests/RipresaDopoRiavvioTests.cs` — il riavvio è simulato per quello che è: una
**seconda istanza** di `TradingEngine` sullo stesso database, senza che `StartAsync` venga mai
chiamato su di essa.

| test | cosa inchioda |
|---|---|
| `DopoIlRiavvio_LaCorsiaAPRE_ANCORA` | il difetto in sé: dopo la ripresa la corsia apre |
| `..._LeGambeSonoQUELLE_DELLA_SESSIONE_...` | la configurazione riscritta sotto la sessione **non** entra in gioco |
| `DopoIlRiavvio_SoloLeGambeATTIVE` | la fotografia rispetta `IsActive` |
| `SessioneSenzaFotografia_RipiegaSullaConfigurazione_E_LO_DICHIARA` | il ripiego esiste **e** finisce in audit |
| `FotografiaIlleggibile_NonFaEsplodereLaRipresa` | JSON corrotto ⇒ ripiego, non eccezione |
| `CorsiaFERMA_NonRipristinaNulla` | su una corsia ferma «zero gambe» è la verità |
| `ChiusuraTestnetSenzaCredenziali_NON_SiFinalizza_ELaPosizioneRESTA` | D2: posizione reale intatta, nessun TradeRecord, audit scritto |

---

## 6. Due residui storici trovati per strada

Non sono guasti vivi. Sono righe già scritte che sporcano gli aggregati.

**Il fill rotto della corsia 2.** Un trade SUI/USDT del 9 luglio, entrato a 0,7694 e uscito a
1748,18: **−227.340%**. È il bug B1 dei fill patologici del testnet, chiuso il 18 luglio dal
`FillSanityCheck` — che però protegge le righe *nuove*. Quella storica è ancora in tabella, e
una riga così decide da sola lo Sharpe «realizzato» di una gamba.

Il filtro sul simbolo (I13b) oggi la esclude solo perché la corsia 2 è passata ad ADA. Se
tornasse su SUI, rientrerebbe. Aggiunto un tetto dichiarato in `DecayMonitorOptions`
(`MaxPlausibleTradeReturnPercent = 1000%`): oltre quella soglia una riga non è
un'operazione, è un fill rotto — si toglie dal calcolo, **si conta**, e `/ensemble` lo dice.
Il valore è volutamente assurdo e non una soglia statistica: serve a togliere ciò che non è un
rendimento, non a togliere le code. Un test lo verifica in entrambi i versi (una perdita reale
del −85% **resta** nel calcolo).

**Cinque righe con la chiusura prima dell'apertura.** `ClosedAtUtc < OpenedAtUtc`, con
`Duration` negativa fino a −29 giorni. Sono l'impronta del feed che rigiocava candele vecchie
contro posizioni vive — **causa già corretta il 2026-08-17** (segnalibro `LastCandleUtc` +
guardia anti-replay), e tutte e cinque precedono quella data. Restano come dati sporchi: la
catena della corsia 2 lo mostra bene — ogni trade apre dove il precedente chiude, e l'ultimo
chiude al **primo** timestamp della catena.

Righe: `TradeRecords` id 159, 248, 269, 283, 292. **Non le ho toccate**: bonificare dati di
produzione è una decisione del proprietario, non mia.

---

## 7. Che cosa serve fare, in ordine

1. ~~**Sbloccare la lettura del motore.**~~ **FATTO**: `scripts/ensure-trading-portforward.ps1`
   ha riaperto 18092+18093 verso `procionemgr-trading-85d489b8f8-s4jst#28` (la prima esecuzione
   era fallita perché `kubectl` era a freddo; alla seconda è passata).
2. **Promuovere l'immagine del core.** È il punto che conta più di tutti: finché il pod resta a
   `local-9a3e8dbe` (17 agosto), nessuna delle correzioni — inclusa questa — sta operando.
   Procedura nota: `docker save | docker exec -i procionemgr-dev-control-plane ctr -n k8s.io
   images import -`, verifica con `crictl images`, poi rollout.
3. **Applicare la migrazione** della nuova colonna. È retrocompatibile: un pod vecchio che non la
   conosce continua a funzionare, e il guscio nuovo la legge come null (ripiego dichiarato).
   Ricordare di costruire prima `ProcioneMGR.Migrations.Postgres`: il build dell'app **non** lo
   ricostruisce.
4. **Riavviare le corsie una per una da `/trading`.** Non basta riavviare il processo: serve un
   `StartAsync` per lasciare la fotografia sulla riga.
   Nota: `StartAsync` azzera capitale e PnL della sessione. Su Paper, con PnL realizzati fra
   −23 e +107 su 10.000, è un costo trascurabile — e l'accounting attuale è comunque sporco.
5. **Poi** valutare le assegnazioni: prima di questa correzione, mettere una strategia su una
   corsia significava metterla su un motore che non l'avrebbe eseguita.

---

## 8. Stato delle corsie a fine intervento

| corsia | stato | perché |
|---|---|---|
| 0 | **libera** | era su AAVE/USDT **1d** — nemmeno intraday — e senza configurazione ensemble |
| 1 | in esecuzione | RsiOversold DOT 15m, Sharpe atteso 4,05 da una caccia sotto gate che non esistono più |
| 2 | in esecuzione | due gambe grigie ADA 4h, allocazione 0% (cosmetica: A3 — l'allocazione non dimensiona) |
| 3 | in esecuzione | RsiOversold grigia ETC 4h |
| 4 | in esecuzione | GridMeanReversion XRP 4h — ~~test pulito della correzione dello specchio~~: **gira la versione rotta**, l'immagine è ferma al 17/8 |
| 5 | in esecuzione | GridMeanReversion UNI 4h — stessa cosa |
| 6 | **libera** | fermata: Composite LTC 15m, 6 trade, −2,87% |
| 7 | **libera** | fermata: BollingerMeanReversion STX 4h, 3 trade, illiquida |

Sei gambe su otto hanno `sourceVerdict = null` e **nessuna dichiara `expectedTradesPerMonth`**:
gli Sharpe attesi (4,05 sulla corsia 1) vengono da cacce fatte sotto regimi di gate che non
esistono più.

## 9. La caccia 5m con la fascia grigia

Completata alle 03:06. 15 candidati valutati, **0 sopravvissuti pieni**, e per la prima volta
**3 gambe proposte dalla fascia grigia su 5 minuti**: `RsiOversold` ADA/USDT 5m (Sharpe holdout
1,34, 19 trade — bocciata per un trade sotto la soglia) e due `Composite` ADA/USDT 5m.

La piattaforma segnala da sé il problema: le due Composite hanno **ρ = 0,99** fra loro, e tutte
e tre sono sullo **stesso simbolo** — su cui la corsia 2 già opera in 4h. Non ho applicato:
«Applica al Trading» riscrive le corsie 0-2 **per indice**, e le corsie 1 e 2 stanno girando.
La decisione su cosa metterci va presa dopo il punto 7, non prima.
