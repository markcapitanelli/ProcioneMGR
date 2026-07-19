# Credenziali Exchange — `/settings/exchanges`

| | |
|---|---|
| **File sorgente** | [`ProcioneMGR/Components/Pages/ExchangeSettings.razor`](../../ProcioneMGR/Components/Pages/ExchangeSettings.razor) (~380 righe) |
| **Route** | `/settings/exchanges` |
| **Sezione navigazione** | Configurazione |
| **Accesso** | `[Authorize]` — ogni utente gestisce le proprie credenziali |
| **Render mode** | `InteractiveServer` |

## A cosa serve

Salva le **chiavi API degli exchange** (Binance/Bitget), necessarie solo per Testnet e Live
— backtest e Paper usano dati pubblici. Le chiavi sono **cifrate nel database
(AES-256-GCM)**: il Secret non viene più mostrato dopo il salvataggio e l'API Key è
visibile solo mascherata.

Avvisi importanti nel `GuidaPanel`:
- **Binance Futures e MiCA**: dal 2026-07-01 Binance ha cessato derivati/leva per i
  residenti SEE (lo Spot resta); per la leva servono credenziali **Bitget**.
- **Passphrase**: obbligatoria solo per Bitget.
- **🔑 Non decifrabile**: se la master key (`Security:MasterKey`) cambia, le credenziali
  salvate con la chiave precedente restano in elenco con un badge di avviso ma non sono
  utilizzabili — rimedio: eliminarle e reinserirle (o ripristinare la chiave).

## Struttura della pagina

| Blocco | Righe | Contenuto |
|---|---|---|
| GuidaPanel | 22–64 | Quando servono le chiavi, cifratura, MiCA, testnet, gestione righe non decifrabili |
| Banner master key | 66–76 | Se il probe rileva credenziali non decifrabili: conteggio, causa e rimedio (`PROCIONE_MGR_MASTER_KEY`) |
| Form "Aggiungi credenziale" | 80–135 | `EditForm` con validazione: exchange, etichetta, key/secret (campi password, `autocomplete="off"`), passphrase (obbligatoria per Bitget — validata sia nel form sia nel dominio), checkbox Testnet |
| Tabella credenziali | 144–200 | Exchange, etichetta, API key mascherata **o badge "Non decifrabile"**, testnet, azioni **Test** (disabilitato per righe non decifrabili) ed Elimina |

## Come funziona (flusso del codice)

### Lettura resiliente — `LoadAsync` (righe 225–233, fix B2)
La pagina **non materializza** `ExchangeCredentials` via converter EF: prima del fix, una
sola riga cifrata con una master key diversa abbatteva l'intera pagina con
`AuthenticationTagMismatchException` (errore 500). Ora `IExchangeCredentialReader`
**decifra riga per riga** e flagga le indecifrabili (`IsDecryptable=false`), che compaiono
con il badge invece di rompere la vista.

### Salvataggio — `AddAsync` (righe 235–266)
Costruisce l'entità e applica la **validazione di dominio** (`ValidateBusinessRules`:
Bitget richiede passphrase — difesa in profondità oltre alla validazione di form). La
cifratura è trasparente: il converter EF (`EncryptedStringConverter`) cifra
ApiKey/Secret/Passphrase alla scrittura.

### Test — `TestAsync` (righe 280–335)
Test **reale** delle chiavi, senza piazzare ordini: ping pubblico + saldo firmato su
**Spot E Futures con esiti separati**. Su Binance testnet Spot e Futures sono ambienti con
chiavi diverse: una coppia valida per un solo lato è normale — il messaggio dice esattamente
cosa funziona e cosa no. Errore "vero" solo se nessun lato firmato funziona o l'exchange è
irraggiungibile.

### Eliminazione — `DeleteAsync` (righe 268–278)
`ExecuteDeleteAsync` vincolato a `Id + UserId`: nessuno elimina credenziali altrui. La
guida ricorda che l'eliminazione non revoca la chiave lato exchange.

## Servizi e classi coinvolte

| Dipendenza | Ruolo | File |
|---|---|---|
| `IExchangeCredentialReader` | Decifratura per-riga resiliente (fix B2) | [`Services/Security/ExchangeCredentialReader.cs`](../../ProcioneMGR/Services/Security/ExchangeCredentialReader.cs) |
| `IMasterKeyProbe` | Diagnosi all'avvio delle righe non decifrabili | [`Services/Security/MasterKeyProbe.cs`](../../ProcioneMGR/Services/Security/MasterKeyProbe.cs) |
| `AesGcmEncryptionService` / `EncryptedStringConverter` | Cifratura AES-256-GCM trasparente via EF | [`Services/Security/AesGcmEncryptionService.cs`](../../ProcioneMGR/Services/Security/AesGcmEncryptionService.cs) |
| `IExchangeClientFactory` | Client Spot e Futures per il test delle chiavi | [`Services/Exchanges/ExchangeClientFactory.cs`](../../ProcioneMGR/Services/Exchanges/ExchangeClientFactory.cs) |
| `BinanceClient` / `BitgetClient` | Le implementazioni per exchange | [`Services/Exchanges/`](../../ProcioneMGR/Services/Exchanges) |

## Dati letti / scritti

- **Legge**: `ExchangeCredentials` dell'utente (decifrate riga per riga).
- **Scrive**: `ExchangeCredentials` (insert cifrato, delete vincolato all'utente).

## Collegamenti con le altre pagine

- [Trading](trading.md) — il consumatore delle credenziali (Testnet/Live); mostra lo stesso
  banner master key.
- [Backup](admin-backup.md) — nel dump del DB le credenziali restano cifrate (ciphertext).

## Note di design

- Il ciphertext è legato alla master key del processo: la coerenza chiave/dati è verificata
  all'avvio dal probe e segnalata in due pagine (qui e in Trading).
- Il test a esiti separati per lato è nato da un finding UI (U3): "Test" deve dire la
  verità operativa, non un generico OK/KO.
