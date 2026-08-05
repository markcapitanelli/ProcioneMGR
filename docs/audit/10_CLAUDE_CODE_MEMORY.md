# 10 — Memoria per Claude Code

> Documento operativo. Leggilo prima di toccare il codice di ProcioneMGR.
> Aggiornato al 2026-08-04.

---

## Contesto in dieci righe

ProcioneMGR è una piattaforma personale di **quant research e trading algoritmico su cripto**,
.NET 10 / Blazor Server, PostgreSQL unico provider. Un solo operatore, nessun team.

Il progetto ha due valori dichiarati e difesi nel codice: **onestà statistica** (nessun risultato
viene abbellito: se il Deflated Sharpe dice "non significativo", la UI lo scrive) e **sicurezza per
costruzione** (cinque barriere indipendenti impediscono al codice di arrivare al denaro reale senza
un umano).

Stato reale: la piattaforma è uno **strumento di misura maturo** che, ad oggi, **non contiene una
strategia che guadagni** quando misurata onestamente. 445.280 combinazioni provate, zero
significative. Non è un fallimento del progetto: è il progetto che funziona e dice la verità.

**Principio fondante: Safety > Solidità > Velocità.**

---

## Comandi

```bash
dotnet run --project ProcioneMGR --no-launch-profile -c Release
```

```bash
dotnet test
```

```bash
dotnet build ProcioneMGR.sln -c Release
```

```bash
dotnet ef database update --project ProcioneMGR.Migrations.Postgres --startup-project ProcioneMGR
```

L'avvio ufficiale sarebbe `./scripts/run-postgres.ps1`, che aggiunge i port-forward verso il
cluster kind — **ma oggi si rompe se il cluster è giù** (R2). L'app gira su `http://localhost:5199`.

`dotnet test` richiede **Docker attivo** (Testcontainers).

## Architettura sintetica

```
Blazor Server (:5199)  ── il "guscio freddo": UI + orchestrazione
   │ gRPC (toggle)
   ├── .Ingestion  (:18080 via port-forward)  sync OHLCV
   ├── .Ml                                     inferenza
   └── .Trading    (:18092 via port-forward)  ← "core caldo", il motore vero
                              │
                        PostgreSQL
```

Tre toggle decidono se un motore gira in-process o remoto: `MarketData:UseRemoteIngestion`,
`Trading:UseRemoteTrading`, `Ml:Enabled`. Oggi i primi due sono `true`: il guscio **comanda**, non
esegue.

Trading isolato in **corsie** (lane) indipendenti via keyed DI — 8 attive, tutte in Paper.

## File importanti

| File | Perché |
|---|---|
| `ProcioneMGR/Program.cs` (674 righe) | tutta la composizione; i commenti spiegano il *perché* di ogni scelta — **leggili prima di cambiare una registrazione** |
| `Services/Trading/TradingEngine.cs` (87,8 KB) | il motore; il file più grande del progetto |
| `Services/Trading/SafetyChecker.cs` | **statico e puro**, chiamato dentro il motore; è la barriera finale su ogni ordine |
| `Services/Trading/PromotionEvaluator.cs` | non restituisce **mai** `Live` |
| `Services/Trading/LanePromoter.cs` | solleva eccezione se gli si chiede Live |
| `Services/Security/AesGcmEncryptionService.cs` + `EncryptedStringConverter.cs` | cifratura a riposo |
| `Services/Validation/` (10 file) | DSR, PBO, CPCV, permutation test, gemello sintetico |
| `Data/ApplicationDbContext.cs` | 30 DbSet; riceve `IEncryptionService` nel costruttore |
| `docs/ROADMAP.md` | la roadmap viva |
| `docs/STANDARD-VERIFICA.md` | i 4 livelli di verifica obbligatori |
| `docs/pagine/` | un documento per pagina UI; nome file = slug della route |

## Convenzioni di codice

- **Lingua: italiano.** Commenti, log, messaggi UI, nomi dei documenti. I nomi di tipi e membri
  restano in inglese. Rispetta questa divisione.
- **I commenti spiegano il *perché*, mai il *cosa*.** Molti contengono la data e la ragione di una
  scelta (`[B3 2026-07-26] Il motore vive nel servizio…`). Quando modifichi una decisione così,
  aggiorna il commento: è documentazione, non rumore.
- **Interfaccia + implementazione** per tutto ciò che è sostituibile (`IReturnPredictor`,
  `IStrategy`, `IExchangeClient`, `ITradingEngine`).
- **`IDbContextFactory`, non il DbContext scoped**, nei servizi a vita lunga e nei componenti
  interattivi.
- **Page service** per l'orchestrazione delle pagine pesanti: la logica non sta nel markup.
- **Test accanto alla feature**: `<Cosa>Tests.cs` in `ProcioneMGR.Tests/`.

## Pattern da rispettare

1. **La sicurezza non passa dalla DI.** `SafetyChecker.Evaluate` è statico apposta: non è
   sostituibile, non è mockabile, non è aggirabile da configurazione. **Non renderlo iniettabile.**
2. **Un solo scrittore.** Mai due motori vivi, mai due scrittori sulla stessa serie OHLCV. I toggle
   e `LaneExecutionLease` esistono per questo.
3. **Fail-closed sulla sicurezza, fail-open sulla diagnostica.** Un limite che non sa decidere
   blocca; un controllo di correlazione che non sa stimare lascia passare. Sono due politiche
   diverse e deliberate — non uniformarle.
4. **Degradare dicendolo.** Quando un dato non è aggiornato, la UI lo deve dichiarare (vedi il
   banner di `/trading`). **Mai mostrare un valore vecchio come se fosse attuale.**
5. **Una sola implementazione per una sola politica.** "Valuta e applica" esiste una volta sola ed è
   condivisa fra scheduler e campaign planner. Non duplicarla.
6. **Anti-look-ahead.** Ogni fattore nuovo deve passare l'invariante anti-look-ahead già testata
   sull'intero catalogo Alpha158.

## Errori comuni

| Errore | Conseguenza |
|---|---|
| Saltare `dotnet ef database update` | database senza tabelle; l'app **non** applica migrazioni all'avvio |
| Dimenticare `@attribute [Authorize]` su una pagina nuova | pagina **pubblica**: non c'è fallback policy che ti salvi |
| Lanciare `run-postgres.ps1` col cluster giù | lo script muore prima di `dotnet run` (R2) |
| Aspettarsi che `appsettings.json` committato descriva la realtà | è gitignorato; quello versionato dice 3 corsie, l'istanza ne ha 8 |
| Modificare `LaneCount` in un posto solo | guscio e core divergono; `LaneCountCoherenceProbe` se ne accorge **solo se il core risponde** |
| Inventare i nomi delle chiavi dei Secret Kubernetes | leggili da `scripts/k8s-*-secret.ps1` |
| Testare senza Docker | i test d'integrazione con Testcontainers falliscono |
| Credere che il file si chiami come il suo contenuto | `AnthropicLlmClient.cs` contiene cinque provider e quello attivo è NVIDIA |

## Cose da non rompere

1. **Le cinque barriere verso Live.** `PromotionEvaluator`, `PromotionWorker`, `LanePromoter`,
   `TradingEngine`, `SafetyChecker`. Nessuna metrica promuove automaticamente in Live. Mai.
2. **Il confine advisory del layer AI.** Nessun servizio di esecuzione va iniettato in
   `Services/Llm/`. Il supervisore può porre un veto, mai forzare.
3. **La cifratura a riposo.** Le credenziali non devono comparire in chiaro: né in log, né in
   eccezioni, né nella UI.
4. **Il fail-fast sulla master key placeholder in Production.**
5. **La lettura resiliente delle credenziali** (`ExchangeCredentialReader`, decifratura per-riga):
   è il fix del bug B2, una riga indecifrabile non deve abbattere la pagina.
6. **`DriveProtectiveExits = false`** — non è una svista: è il risultato di una misura (uscire al
   tocco è peggio che a barra chiusa, 24/24 configurazioni).
7. **`RegimeRouting:DriveDecisions = false`** — il router osserva e registra, non decide. Accenderlo
   sui dati attuali sarebbe curve-fitting su 5-37 trade per regime.

## Priorità attuali

1. 🔴 **R1 — rotazione dei segreti esposti su GitHub.** Precede tutto il resto.
2. 🔴 **R2 — riparare `run-postgres.ps1`**: poche righe, sblocca l'avvio dopo ogni riavvio.
3. 🔴 **R3 — allineare il conteggio dei test nel README.**
4. 🟡 R5 — allarme sulle fonti AltData morte (ForexFactory 403, FXStreet-CentralBanks 404).
5. 🟡 R4 — test che impedisca pagine senza `[Authorize]`.
6. 🟡 R6 — estrarre `AutonomyPageService` da `Autonomy.razor` (107,6 KB).

Roadmap di prodotto: `docs/ROADMAP.md` e i PRD in `docs/PRD-*.md`.

## Domande aperte

- Qual è il numero **vero** dei test? (`dotnet test --list-tests`)
- Quale query genera il warning EF 10103 (`First`/`FirstOrDefault` senza `OrderBy`)?
- Esiste un `IEmailSender` reale configurato, o Identity usa il no-op?
- I tre CLI fuori dalla soluzione (`FuturesVerify`, `PlatformExpand`, `SpotVerify`) sono ancora
  usati o sono archeologia?
- Esistono dipendenze vulnerabili? (`dotnet list package --vulnerable`)
- Il cluster kind: perché l'API server non risponde nonostante `kind-apiproxy` sia su?

## Istruzioni operative per modificare il codice

**Prima di iniziare**

1. Leggi `docs/ROADMAP.md` e il PRD pertinente.
2. Se la modifica tocca una pagina, leggi `docs/pagine/<slug-della-route>.md`.
3. Se tocca trading o sicurezza, rileggi la sezione "Cose da non rompere" qui sopra.

**Mentre lavori**

4. Riusa i pattern esistenti: page service per le pagine, keyed DI per le corsie, interfaccia +
   implementazione per ciò che è sostituibile.
5. Scrivi commenti in italiano che spieghino **perché**, con la data se documentano una decisione.
6. Non aggiungere dipendenze senza necessità: il progetto ne ha poche e scelte.

**Prima di dichiarare fatto**

7. `dotnet build` deve passare senza nuovi warning.
8. `dotnet test` deve passare (Docker attivo).
9. Se la modifica è visibile in UI, **aprila davvero** su `http://localhost:5199`. Il progetto ha
   una storia documentata di difetti — "controlli che rassicurano a prescindere dalla realtà" — che
   si trovano **solo** guardando la pagina vera, non leggendo il codice.
10. Applica `docs/STANDARD-VERIFICA.md`: 4 livelli (unità vs riferimento indipendente, controllo sul
    rumore, integrazione reale, browser).
11. Aggiorna la documentazione della pagina in `docs/pagine/` se ne hai cambiato il comportamento.

**Quando misuri qualcosa**

12. Dichiara sempre **trade/mese e durata mediana della posizione**: il proprietario lavora
    intraday/swing breve, un edge che si realizza in 6 mesi non gli serve.
13. Nessun risultato senza il gate anti-overfitting. Se il DSR dice "non significativo", scrivilo:
    è il valore del progetto, non una sconfitta.
14. Non randomizzare su asset correlati per stimare la significatività: fabbrica falsa
    significatività. È un errore già commesso e già pagato.
