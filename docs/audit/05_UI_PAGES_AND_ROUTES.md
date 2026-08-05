# 05 — Pagine, route e protezioni

**66 route** dichiarate con `@page`: 34 pagine applicative + 32 pagine Identity scaffolded.
La colonna "Protezione" è estratta dagli `@attribute [Authorize…]` dei `.razor`; la colonna
"Verificata" riporta l'esito del test HTTP reale fatto durante l'audit (vedi
[07](07_BROWSER_CHECK_REPORT.md)).

---

## Pagine applicative

| Route | File | Protezione | Verificata |
|---|---|---|---|
| `/` | `Home.razor` | **pubblica** | 200 |
| `/dashboard` | `Dashboard.razor` | `[Authorize]` | 302 → login |
| `/market/watchlist` | `Watchlist.razor` | Admin, Manager | 302 → login |
| `/market-analysis` | `MarketAnalysis.razor` | `[Authorize]` | 302 → login |
| `/market/bars` | `InformationBars.razor` | Admin, Manager | 302 → login |
| `/metrics` | `Metrics.razor` | Admin, Manager | 302 → login |
| `/backtest` | `Backtest.razor` | `[Authorize]` | 302 → login |
| `/optimization` | `Optimization.razor` | Admin, Manager | 302 → login |
| `/feature-selection` | `FeatureSelection.razor` | Admin, Manager | 302 → login |
| `/ml` | `MlLab.razor` | Admin, Manager | 302 → login |
| `/ensemble` | `Ensemble.razor` | Admin, Manager | 302 → login |
| `/portfolio` | `PortfolioOptimization.razor` | Admin, Manager | 302 → login |
| `/registry` | `Registry.razor` | Admin, Manager | 302 → login |
| `/experiments` | `Experiments.razor` | Admin, Manager | 302 → login |
| `/discovery` | `Discovery.razor` | Admin, Manager | 302 → login |
| `/pipeline` | `Pipeline.razor` | Admin, Manager | 302 → login |
| `/alpha-mining` | `AlphaMining.razor` | Admin, Manager | 302 → login |
| `/regimes` | `Regimes.razor` | Admin, Manager | 302 → login |
| `/pairs-trading` | `PairsTrading.razor` | Admin, Manager | 302 → login |
| `/volatility` | `Volatility.razor` | Admin, Manager | 302 → login |
| `/sentiment` | `Sentiment.razor` | Admin, Manager | 302 → login |
| `/strategies` | `Strategies.razor` | `[Authorize]` | 302 → login |
| `/execution` | `ExecutionLab.razor` | Admin, Manager | 302 → login |
| `/trading` | `Trading.razor` | Admin, Manager | 302 → login |
| `/bot` | `Bot.razor` | Admin, Manager | 302 → login |
| `/campaign` | `Campaign.razor` | Admin, Manager | 302 → login |
| `/settings/exchanges` | `ExchangeSettings.razor` | `[Authorize]` | 302 → login |
| `/admin/ai-supervisor` | `Admin/AiSupervisor.razor` | Admin, Manager | 302 → login |
| `/admin/autonomy` | `Admin/Autonomy.razor` | **Admin** | 302 → login |
| `/admin/users` | `AdminUsers.razor` | **Admin** | 302 → login |
| `/admin/backup` | `Admin/Backup.razor` | **Admin** | 302 → login |
| `/admin/protections` | `Admin/Protections.razor` | **Admin** | 302 → login |
| `/Error` | `Error.razor` | pubblica | — |
| `/not-found` | `NotFound.razor` | pubblica | 200 |

**Route non documentate nel README:** `/bot`, `/campaign`, `/admin/protections` esistono nel codice
ma non compaiono nella "Mappa delle pagine" del [README.md](../../README.md).

## Pagine Identity

32 route sotto `/Account/…` (Login, Register, ForgotPassword, ResetPassword, LoginWith2fa,
LoginWithRecoveryCode, ExternalLogin, Lockout, AccessDenied, ConfirmEmail…) più `/Account/Manage/…`
(ChangePassword, Email, EnableAuthenticator, Passkeys, PersonalData, DeletePersonalData,
TwoFactorAuthentication, GenerateRecoveryCodes, ResetAuthenticator, SetPassword, ExternalLogins,
RenamePasskey/{Id}). Sono lo scaffolding standard ASP.NET Core Identity.

---

## Nota sul modello di autorizzazione

Tre livelli, coerenti con la sensibilità dell'operazione:

- **`[Authorize]` semplice** — pagine che agiscono sui *propri* dati o sono di sola consultazione:
  `/dashboard`, `/backtest`, `/market-analysis`, `/strategies`, `/settings/exchanges`.
- **Admin + Manager** — tutto ciò che tocca ricerca pesante o trading.
- **Admin soltanto** — gestione utenti, backup, autonomia, protezioni.

**`/settings/exchanges` con `[Authorize]` semplice non è un difetto**, anche se maneggia chiavi API:
`ExchangeCredential` ha una FK `UserId`
([ExchangeCredential.cs:25](../../ProcioneMGR/Data/ExchangeCredential.cs#L25)) e la pagina carica
esclusivamente `CredentialReader.LoadForUserAsync(userId)`
([ExchangeSettings.razor:245](../../ProcioneMGR/Components/Pages/ExchangeSettings.razor#L245));
la cancellazione è vincolata con `.Where(c => c.Id == id && c.UserId == userId)`
([riga 287](../../ProcioneMGR/Components/Pages/ExchangeSettings.razor#L287)). Ogni utente vede e
tocca solo le proprie credenziali.

Resta il punto strutturale già segnalato in [02](02_ARCHITECTURE.md): **non c'è fallback policy
globale**, quindi la protezione dipende dalla disciplina di ricordare l'attributo su ogni pagina
nuova. Oggi lo stato è pulito su tutte e 34.

---

## Componenti e stato per pagina

Il pattern dominante, dopo il refactor P1-5:

```
Pagina.razor  →  PageService (scoped, testabile)  →  servizi di dominio  →  DB / gRPC
```

| Pagina | Page service | Stato principale |
|---|---|---|
| `/trading` | `TradingPageService` | `TradingEngineState`, `OpenPosition`, `Order` via gRPC |
| `/ml` | `MlLabService` | `SavedMlModel`, dataset in memoria |
| `/backtest` | `BacktestPageService` | risultato di run, non persistito finché non salvato |
| `/optimization` | `OptimizationPageService` | griglia/bayesiano, `ExperimentRun` |
| `/pipeline` | `PipelinePageService` | `PipelineRun`, `PipelineArtifact` |
| `/ensemble` | `EnsemblePageService` | `EnsembleState` per corsia |
| `/campaign` | `CampaignPageService` | `VettingCampaign` |
| `/admin/autonomy` | **nessuno** | logica nel markup (107,6 KB) |
| `/sentiment` | **nessuno** | logica nel markup (55,0 KB) |

Preset e ultima configurazione usata sono persistiti per utente in `UserPageConfig`, condivisi da
tutte le pagine che espongono parametri.

## API chiamate dalle pagine

**Nessuna.** Essendo Blazor Server, non esiste una API REST interna: le pagine invocano
direttamente servizi C# in-process. L'unica chiamata di rete che parte "per conto della pagina" è
il gRPC verso il core caldo (`/trading`, `/admin/autonomy`, `/execution`) e le REST verso gli
exchange nei percorsi di ingestione. Dettaglio in [06](06_API_AND_INTEGRATIONS.md).

## Form, input e validazioni

- **Form Identity** — `EditForm` + `DataAnnotations` standard.
- **`/settings/exchanges`** — `CredentialInput` con `[Required]`/validazione DataAnnotations;
  il Secret non viene **mai** rimostrato dopo il salvataggio, l'API Key appare mascherata
  (`abcd••••wxyz`). Buona pratica, implementata.
- **Form di parametri** (backtest, optimization, ml…) — input tipizzati con range; la validazione
  vera è a valle, nei servizi.
- **`/dashboard`** — form OHLCV: Exchange, Symbol, Timeframe, intervallo date, "Scarica dati".

## Layout e componenti condivisi

- `Components/Layout/` — layout principale con sidebar a sezioni (Overview, Dati & Monitoraggio,
  Ricerca & Sviluppo, Strumenti Avanzati, Trading, Configurazione).
- `Components/Shared/` — componenti riusabili, fra cui `GuidaPanel`, il pannello "Guida" collassabile
  presente in cima a quasi ogni pagina.
- **Ricerca globale** — pulsante "Cerca…" con scorciatoia `Ctrl K` (command palette), presente nella
  topbar di ogni pagina.
- **Breadcrumb** — `Home › Sezione › Pagina`.

## Osservazioni UX

Cose **fatte bene**, verificate a schermo:

1. **Il banner di dati stantii su `/trading`** dice esattamente cosa non sa: *«DATI TRADING NON
   AGGIORNATI da 0s: il servizio di trading non risponde (Unavailable). Quanto vedi qui sotto è
   l'ultimo stato noto, non quello attuale — posizioni e PnL potrebbero essere cambiati. I comandi
   falliranno finché il servizio non torna.»* È l'opposto del controllo che rassicura a prescindere.
2. **Empty state espliciti** su `/metrics`: «Nessun trade eseguito in questa sessione» invece di uno
   zero ambiguo.
3. **`GuidaPanel` in ogni pagina** — la piattaforma spiega sé stessa a chi la riapre dopo settimane.
4. **Responsive funzionante** — a 375 px la sidebar collassa in hamburger, le tessere KPI passano a
   due colonne, nessuno scroll orizzontale.

Cose **migliorabili**:

| # | Osservazione | Dove |
|---|---|---|
| U1 | Le pagine Identity sono **in inglese** ("Log in", "Use a local account to log in", "Remember me", "Forgot your password?") mentre tutta l'app è in italiano. Scaffolding mai tradotto. | `/Account/Login` e sorelle |
| U2 | Il `<select>` **Exchange** è troppo stretto: la voce "Binance" viene tagliata. | `/dashboard` |
| U3 | La pagina di login tenta una richiesta **passkey/WebAuthn in conditional UI** che fallisce e lascia due errori in console (`NotAllowedError`). Nessun impatto funzionale, ma sporca la console. | `/Account/Login` |
| U4 | `/admin/autonomy` (107,6 KB) e `/sentiment` (55 KB) non hanno page service: sono le due pagine più difficili da testare e da modificare in sicurezza. | vedi [09](09_RISKS_AND_TECH_DEBT.md) |
| U5 | Le corsie **3 e 7** appaiono come "non configurata" fra corsie attive: occupano spazio nella barra senza dire cosa farne. | `/trading` |
