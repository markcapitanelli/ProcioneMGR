# Volatilità — `/volatility`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/Volatility.razor`](../../ProcioneMGR/Components/Pages/Volatility.razor) (~290 righe) |
| **Route** | `/volatility` |
| **Sezione navigazione** | Strumenti Avanzati |
| **Accesso** | `[Authorize(Roles = Admin, Manager)]` |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Stima la **volatilità futura** con un modello **GARCH(1,1)**, sfruttando il fatto empirico
che la volatilità è "a grappoli": i periodi turbolenti tendono a essere seguiti da altra
turbolenza. Usi pratici: position sizing (quanto rischiare per operazione) e piazzamento
degli stop.

Il `GuidaPanel` (righe 22–49) spiega con trasparenza i parametri stimati:
- **ω** livello base, **α** reattività agli shock recenti, **β** persistenza della
  volatilità recente;
- **persistenza (α+β)** — vicina a 1 (tipico crypto) = gli shock si esauriscono lentamente;
- **vol. di lungo periodo vs corrente** — se la corrente è molto sopra, il modello si
  aspetta un ritorno alla calma (mean-reversion), visibile nella tabella di previsione.

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 22–49 | Concetti e lettura dei parametri |
| Configurazione | 51–103 | Exchange, symbol, timeframe, periodo, bottone "Stima GARCH", `DataAvailability` |
| KPI del fit | 107–114 | ω, α, β, persistenza (verde se < 0.98), vol. annualizzata lungo periodo/corrente |
| Grafico | 116–124 | Volatilità condizionale in-sample (dev. standard per periodo) — i "grappoli" si vedono a occhio |
| Tabella previsione | 126–145 | Orizzonti 1/5/10/20/50/100 barre: varianza prevista, vol. per periodo, vol. annualizzata |

## Come funziona (flusso del codice)

### `RunAsync` (righe 200–270)
1. Carica le candele (minimo 40) e deriva i **rendimenti semplici** close-su-close.
2. `Garch.Fit(returns)` stima ω/α/β via maximum likelihood e restituisce un `GarchFit` con
   le varianze condizionali in-sample e il metodo `ForecastVariance(h)`.
3. L'annualizzazione usa `Statistics.PeriodsPerYear(timeframe)` (riga 236): la stessa
   convenzione del resto della piattaforma.
4. Il grafico traccia `sqrt(varianza condizionale)` per periodo su scala oscillatore.

La previsione in tabella è calcolata **on-render** chiamando `ForecastVariance(h)` per ogni
orizzonte: la formula chiusa GARCH converge alla varianza di lungo periodo al crescere di h.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IGarchModel` / `GarchModel` | Stima MLE di GARCH(1,1) e previsione | [`Services/TimeSeries/GarchModel.cs`](../../ProcioneMGR/Services/TimeSeries/GarchModel.cs) |
| `GarchFit` | Il risultato: parametri, varianze condizionali, `ForecastVariance` | [`Services/TimeSeries/GarchFit.cs`](../../ProcioneMGR/Services/TimeSeries/GarchFit.cs) |
| `Statistics.PeriodsPerYear` | Annualizzazione coerente col timeframe | [`Services/Optimization/Statistics.cs`](../../ProcioneMGR/Services/Optimization/Statistics.cs) |

## Dati letti / scritti

- **Legge**: `OhlcvData`.
- **Scrive**: `UserPageConfigs` (memoria configurazione). Nessun modello persistito: la
  stima è on-demand.

## Collegamenti con le altre pagine

- [Pipeline](pipeline.md) — lo stage di volatilità usa la stessa stima per l'etichetta
  Alta/Bassa nella raccomandazione (riduzione del sizing in alta volatilità, da
  `pipeline_rules.json`).
- [Backtest](backtest.md) — la stima informa la scelta di SL e position size.

## Note di design

- La persistenza è colorata (soglia 0.98): α+β ≥ 1 renderebbe il processo non stazionario
  e la previsione inaffidabile — il semaforo lo segnala subito.
- Dall'audit algoritmico 2026-07: il sizing gaussiano su code grasse è uno dei punti 🔴 —
  la volatilità GARCH è un input, non una garanzia di normalità dei rendimenti.
