# 07 — Controllo nel browser

**Data:** 2026-08-04, ~20:15 CEST
**Target:** `http://localhost:5199`
**Strumento:** browser integrato (Chromium headful) + sonda HTTP diretta.

---

## Premessa onesta su come è andata

L'app **non era in esecuzione** all'inizio dell'audit, nonostante la consegna la desse per attiva:
il black-out l'aveva spenta. Nessun processo in ascolto su 5199.

Cosa ho trovato e fatto, in ordine:

1. Docker era ripartito da solo; il cluster kind (`procionemgr-dev-control-plane`) e il proxy
   `kind-apiproxy` erano su, ma **l'API server del cluster non rispondeva** (`TLS handshake
   timeout`) — il kubeconfig punta già a `127.0.0.1:16443`, quindi il problema sta a valle del
   proxy. Non l'ho risolto: è fuori dal perimetro di un audit read-only.
2. PostgreSQL era su, come servizio Windows `postgresql-x64-18`, in ascolto su 5432.
3. `scripts/run-postgres.ps1` **è morto** prima di lanciare l'app (difetto reale, vedi
   [09 — R2](09_RISKS_AND_TECH_DEBT.md#r2)).
4. Ho avviato l'app direttamente con `dotnet run -c Release` dal repo principale
   (`ASPNETCORE_ENVIRONMENT=Production`, `ASPNETCORE_URLS=http://localhost:5199`), saltando i
   port-forward verso un cluster comunque irraggiungibile.

**Di conseguenza il controllo è avvenuto in stato parzialmente degradato**, con il core caldo di
trading non raggiungibile. Lo dico perché cambia la lettura di `/trading`: quello che ho verificato
lì è *come l'app si comporta quando il motore non risponde*, non il funzionamento nominale.

Nota sull'accesso: la sessione del browser era **già autenticata** con un cookie preesistente
dell'utente (`procionemgr@gmail.com`). Non ho inserito credenziali — non lo faccio mai. Ho
verificato l'area autenticata sfruttando la sessione già aperta.

---

## Homepage

`GET /` → **200**. Renderizza correttamente in entrambi gli stati:

- **Anonimo:** landing con titolo, sottotitolo, pulsanti *Login* / *Registrati*.
- **Autenticato:** «Bentornato, procionemgr@gmail.com», quattro azioni rapide (Nuovo Backtest,
  Aggiorna Dati, Avvia Trading, Apri Dashboard) e quattro tessere KPI.

Dati reali letti a schermo:

| KPI | Valore |
|---|---|
| Serie tracciate | 221 |
| Candele in archivio | ≈ 12.181.001 |
| Strategie salvate | 17 |
| Trading | Paper attivo |

Sotto, un pannello di allerta: **«32 fattori in deriva»**, con l'elenco dei fattori e l'IC che si è
spento (es. *MeanReversion su DOGE/BTC 4h — IC da 0,144 a 0,024, soglia 0,051*), la nota
«Segnalazione soltanto: nessun fattore viene escluso in automatico» e la copertura
(«220 serie già calcolate su 221 in watchlist; ultimo calcolo 2026-08-04 09:00 UTC»).

Questo pannello è un buon esempio del carattere del progetto: dice il numero, la soglia, cosa
**non** fa in automatico, e quanto è coperto.

## Errori in console

Sulla home e sulle pagine interne: **nessun errore**. Solo informativi del framework:

```
Information: Normalizing '_blazor' to 'http://localhost:5199/_blazor'.
Information: WebSocket connected to ws://localhost:5199/_blazor?id=…
```

Sulla **pagina di login** compaiono due errori:

```
[error] [object DOMException]
[error] NotAllowedError: The operation either timed out or was not allowed.
        See: https://www.w3.org/TR/webauthn-2/#sctn-privacy-considerations-client
```

Sono la richiesta passkey/WebAuthn in *conditional UI* che scade. Nessun impatto funzionale: il
login con password resta disponibile. Resta rumore in console → [U3](05_UI_PAGES_AND_ROUTES.md).

## Errori di rete 4xx / 5xx

**Nessuno** lato browser. Le richieste osservate:

```
GET  /_blazor/initializers            → 200
POST /_blazor/negotiate?…             → 200
GET  data:image/webp;base64,…         → 200  (favicon inline)
```

Lato **server**, invece, il log mostra i 4xx delle integrazioni esterne — ForexFactory 403 e
FXStreet-CentralBanks 404, dettagliati in [06](06_API_AND_INTEGRATIONS.md). Non arrivano al
browser perché avvengono in background.

## Asset mancanti

Nessuno. `MapStaticAssets()` serve CSS e font correttamente; la favicon è una data-URI inline.

## Navigazione e route

Sonda HTTP su **37 route** senza seguire i redirect. Esito:

| Gruppo | Atteso | Ottenuto |
|---|---|---|
| `/`, `/not-found`, `/Account/Login`, `/Account/Register` | 200 | ✅ 200 |
| `/health` | 200 anonimo | ✅ 200 |
| 28 route protette | 302 → `/Account/Login?ReturnUrl=…` | ✅ **28 su 28** |
| `/rotta-inesistente-xyz` | 404 | ✅ 404 |

**Nessuna route protetta risulta accessibile senza autenticazione**, e il `ReturnUrl` è sempre
valorizzato correttamente (es. `?ReturnUrl=%2Fadmin%2Fautonomy`). È il risultato più solido di
questo controllo.

## Pagine ispezionate a schermo

### `/trading` — il caso interessante

Otto corsie (0–7), tutte in **PAPER**; le corsie 3 e 7 marcate "non configurata". Le altre:
AAVE/USDT, DOT/USDT, XLM/USDT, XRP/USDT, DOT/USDT, LTC/USDT.

In cima, il banner che vale la pena citare per intero:

> ⚠️ **DATI TRADING NON AGGIORNATI da 0s:** il servizio di trading non risponde (Unavailable).
> Quanto vedi qui sotto è l'ultimo stato noto, **non** quello attuale — posizioni e PnL potrebbero
> essere cambiati. I comandi falliranno finché il servizio non torna.

Questo è esattamente il comportamento giusto in stato degradato: non mostra numeri vecchi
spacciandoli per attuali, e avvisa che i comandi falliranno. La pagina resta navigabile, con
Controllo, Modalità, *Avvia trading* e *EMERGENCY STOP* visibili.

### `/dashboard`

Form OHLCV: Exchange, Symbol (`BTC/USDT`), Timeframe (`1h`), intervallo date (05/07/2026 →
04/08/2026), pulsanti *Scarica dati* e *Carica simboli*. Render corretto.
Difetto cosmetico: il `<select>` **Exchange è troppo stretto** e taglia la voce "Binance".

### `/metrics`

Sei tessere (Trade eseguiti, Job esecuzione, Promozioni corsia, Feature in drift, Modelli ritirati,
Run pipeline), tutte a **0** — corretto: i contatori sono per-processo e l'app era appena
riavviata. Auto-aggiornamento dichiarato ogni 5 s, timestamp aggiornato. Empty state espliciti:
«Nessun trade eseguito in questa sessione».

## Stati loading / error / empty

| Stato | Esito |
|---|---|
| **Loading** | non catturato: le pagine rispondono troppo in fretta in locale — **DA VERIFICARE** con throttling |
| **Error** | ✅ verificato dal vivo su `/trading` (servizio gRPC giù): banner esplicito, nessun crash |
| **Empty** | ✅ verificato su `/metrics`: testo esplicito, non zeri ambigui |

## Responsive

| Viewport | Esito |
|---|---|
| 1280×800 desktop | ✅ sidebar fissa, contenuto in colonna |
| **375×812 mobile** | ✅ sidebar → hamburger; KPI da 3 colonne a 2; breadcrumb conservato; **nessuno scroll orizzontale** |

## Screenshot

⚠️ **Non salvati su disco.** Gli screenshot sono stati acquisiti e ispezionati durante l'audit, ma
lo strumento browser li restituisce inline e non può scriverli come file: la cartella
`docs/audit/screenshots/` resterebbe vuota o falsa. Ho preferito non fingere.

Per generarli davvero c'è lo script pronto: [`playwright-smoke.mjs`](playwright-smoke.mjs). Salva
gli screenshot in `docs/audit/screenshots/`, controlla console e rete, e verifica i redirect di
autenticazione.

```bash
npm install -D @playwright/test && npx playwright install chromium
node docs/audit/playwright-smoke.mjs
```

## Checklist manuale per l'utente

Cose che **non ho potuto verificare** e che richiedono una sessione con dati veri e il cluster su:

- [ ] Rimettere in piedi il cluster kind e verificare che il banner di `/trading` sparisca e che
      `LaneCountCoherenceProbe` confermi la coerenza fra le 8 corsie del guscio e quelle del core.
- [ ] `/backtest`: eseguire un backtest completo e controllare che il report mostri Profit Factor,
      Kelly e Montecarlo.
- [ ] `/optimization`: un run walk-forward e verifica che il Deflated Sharpe compaia.
- [ ] `/settings/exchanges`: salvare una credenziale finta e verificare mascheramento e badge master key.
- [ ] `/admin/backup`: eseguire un backup (richiede `pg_dump` nel PATH).
- [ ] `/admin/autonomy`: modificare un'opzione e verificare l'hot-reload senza riavvio.
- [ ] `/execution`: verificare che i comandi restino inerti con Live disabilitato.
- [ ] Riconnessione Blazor: fermare l'app con la pagina aperta e osservare la UI di riconnessione.

## Comandi di verifica rapida

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5199/health
```

```bash
curl -s -o /dev/null -w "%{http_code} -> %{redirect_url}\n" http://localhost:5199/trading
```

```bash
for r in / /health /dashboard /trading /admin/autonomy /rotta-inesistente; do printf "%-20s %s\n" "$r" "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5199$r)"; done
```
