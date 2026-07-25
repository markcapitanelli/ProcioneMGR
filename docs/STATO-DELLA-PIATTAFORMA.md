# Stato della piattaforma — cosa c'è, cosa funziona, cosa non c'è

*Aggiornato 2026-07-20. Da leggere prima di usare la piattaforma per decidere qualcosa.*

## In una riga

La piattaforma è matura come **strumento di misura**. Non contiene una strategia che, misurata
onestamente, guadagni. Le due cose non sono in contraddizione: serve a scoprire se un'idea funziona,
e finora la risposta è stata no.

## Cosa è stato misurato, e cosa è venuto fuori

| ricerca | esito |
|---|---|
| 5 angoli di ricerca (`REPORT-RICERCA-2026-07.md`) | tutti negativi |
| esperimento di **controllo** con edge sintetico piantato | la pipeline lo trova (DSR 1,00) — gli strumenti funzionano |
| caccia su **445.280 combinazioni** | 6 oltre i gate, **0** significative al DSR, **0/6** sopravvissute all'holdout |
| momentum trasversale (`xsection`) | negativo: composta +28,6% contro +66,6% del buy&hold |
| ordini maker con modello di fill (`makerfill`) | il margine promesso non regge |
| dosaggio della volatilità (`REPORT-DOSAGGIO-VOLATILITA.md`) | regime-dipendente, misure contraddittorie |

L'esperimento di controllo è il più importante di tutti: dimostra che quando un edge **c'è**, questi
strumenti lo trovano. Quindi i risultati negativi dicono qualcosa sui dati, non sugli strumenti.

## Cosa NON troverai qui

- **Una strategia pronta da schierare.** Non ne esiste una, in questa piattaforma, che sia
  sopravvissuta a una validazione onesta.
- **Un numero che dica quanto guadagnerai.** Ogni Sharpe che vedi accanto a una strategia salvata è
  quello della *selezione*: misura il passato con cui è stata scelta. I sei migliori candidati su
  445.280 avevano selezione 1,28–1,61 e hanno reso fra −0,79 e −4,75 su dati mai visti.

## I freni che ti proteggono, e che conviene non togliere

- **Live richiede conferma manuale** per ogni singolo ordine (`RequireManualConfirmationForLive`).
- **Nessuna promozione automatica a Live**: Paper→Testnet può essere automatica, Testnet→Live mai.
- **Deflated Sharpe**: corregge lo Sharpe per il numero di configurazioni provate. Con 445.280
  tentativi, uno Sharpe di 3 non è un risultato — è il massimo atteso dal caso.
- **Costi onesti di default** in Backtest, Ottimizzazione, Discovery e Pipeline: 0,1% di fee per lato
  e 0,05% di slippage per fill. Se metti slippage a 0 la pagina Backtest te lo dice in giallo.
- **Watchdog di invarianti per corsia** e **sanity check sui fill** (un testnet ha già riportato una
  volta quantità 100× e prezzo 0: senza il controllo, il motore le adottò e produsse −1,8M su 10k).

## Il dosaggio della volatilità, se decidi di accenderlo

Spento di default, in `/trading` → pannello sicurezza (solo Admin).

- **Cosa fa in modo affidabile**: riduce l'esposizione media (36–62%) e con essa l'ampiezza delle
  oscillazioni. Sulle strategie del catalogo dimezza le perdite.
- **Cosa NON fa**: migliorare il rapporto rendimento/rischio in modo stabile, e recuperare una
  strategia che perde (misurato: 0 su 6).
- **Garanzia strutturale**: col tetto a 1,0 può solo *ridurre* la dimensione, quindi non può violare
  i limiti di sicurezza. Alzarlo sopra 1,0 toglie la garanzia, e la UI lo segnala.

Accendilo se vuoi oscillare meno accettando di guadagnare meno. Non per far funzionare qualcosa.

## Se vuoi continuare la ricerca

> **Aggiornamento 2026-07-20**: esiste ora una roadmap dedicata —
> **[ROADMAP-MACCHINA-RICERCA.md](ROADMAP-MACCHINA-RICERCA.md)** — nata dall'obiezione del
> proprietario alla conclusione "servono dati di un altro mercato". Il censimento del codice gli ha
> dato ragione: molti dati preziosi sono già raccolti ma scollegati dal motore (funding storico,
> order flow scartato al parsing, purged CV esistente ma non usata dal walk-forward). La roadmap
> collega l'esistente prima di inventare. I punti qui sotto restano validi e sono assorbiti lì.

Quello che **non** vale la pena rifare (già esaurito): altre cacce a strategia singola sugli stessi
dati variando finestre; timeframe sotto i 15m (i costi li rendono inoperabili); fidarsi di uno Sharpe
alto senza guardare il numero di operazioni.

Quello che resta aperto e sensato:

1. **Dati di un mercato diverso** (azionario, o un ciclo cripto precedente). È l'unica via per sapere
   se il dosaggio della volatilità sia un effetto reale o un artefatto di questo ciclo: con i dati in
   casa la domanda non è decidibile.
2. **Orizzonti più lunghi del giornaliero**, dove il rapporto fra ampiezza del movimento e costo è
   più favorevole di qualunque cosa testata finora.
3. **Portafoglio di coppie** market-neutral: i drawdown al 2–5% sono l'unico profilo di rischio
   strutturalmente favorevole emerso, anche se il rendimento non c'è.

## Una trappola metodologica, documentata perché è facile ricascarci

Nel tentativo di validare il dosaggio ho randomizzato 400 panieri dallo stesso periodo e ottenuto
**399/399 positivi con t = 141,6**. È un numero privo di significato: le cripto sono quasi tutte
correlate, quindi erano 400 ripetizioni di un solo esperimento. **Randomizzare su asset correlati
dentro una finestra fabbrica significatività finta.** L'unica randomizzazione che dice qualcosa è
lungo la dimensione in cui i dati sono indipendenti — qui, il tempo.

## Comandi di ricerca

```
dotnet run --project tools/PlatformExpand -- stats         # inventario dei dati
dotnet run --project tools/PlatformExpand -- hunt          # caccia ampia
dotnet run --project tools/PlatformExpand -- holdout       # valida l'ultima caccia fuori campione
dotnet run --project tools/PlatformExpand -- control       # l'edge piantato: gli strumenti funzionano?
dotnet run --project tools/PlatformExpand -- costfrontier  # a quale costo un candidato diventa profittevole
dotnet run --project tools/PlatformExpand -- makerfill     # quanto del maker sopravvive al mancato riempimento
dotnet run --project tools/PlatformExpand -- xsection      # momentum trasversale + dosaggio volatilità
dotnet run --project tools/PlatformExpand -- volsingle     # il dosaggio su singolo simbolo
dotnet run --project tools/PlatformExpand -- volrobust     # è un effetto o era rumore?
dotnet run --project tools/PlatformExpand -- voloverlay    # il dosaggio recupera le strategie?
```
