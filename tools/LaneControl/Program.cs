// =================================================================================================
//  LaneControl — avviare, fermare e guardare le corsie di trading dalla riga di comando.
//
//  PERCHE' ESISTE (2026-08-23). Fino a oggi le corsie si comandavano da UN SOLO posto: la pagina
//  /trading, dietro login. Va benissimo finche' si e' davanti allo schermo — ma dopo un riavvio del
//  pod del motore le corsie restano inerti (difetto D1, 2026-08-21), e riaccenderle richiedeva
//  sedersi, aprire il browser e ricordarsi la password. Le otto corsie sono rimaste ferme NOVE
//  GIORNI, con l'ultimo ordine del 2026-08-14, e nessuno strumento poteva nemmeno DIRLO senza
//  aprire la UI.
//
//  Questo strumento chiude il buco per la strada gia' prevista dall'architettura: lo stesso gRPC,
//  lo stesso segreto condiviso, le stesse RPC che chiama il guscio. Non inventa un percorso nuovo,
//  ne apre uno da console a quello che c'era.
//
//  COSA NON FA, DI PROPOSITO
//
//  · NON avvia in modalita' Live. Mai, nemmeno con un flag. La regola del progetto e' che verso
//    Live non esiste nessun percorso automatico: Live si sceglie a mano, dalla UI, con la conferma
//    umana che `Trading:Safety:RequireManualConfirmationForLive` pretende. Uno strumento da riga di
//    comando che sa dire «Live» e' esattamente il percorso che non deve esistere.
//  · NON ha un default per la modalita'. Va scritta. Un default su un parametro che decide se gli
//    ordini sono simulati o veri e' un default che prima o poi qualcuno non legge.
//  · NON tocca il database, la master key, le credenziali. Riferisce solo i Contracts.
//
//  USO
//    LaneControl stato
//    LaneControl avvia tutte --modalita paper
//    LaneControl avvia 1,2,3 --modalita paper
//    LaneControl ferma tutte
// =================================================================================================
using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using ProcioneMGR.Contracts.Grpc;
using Proto = ProcioneMGR.Contracts.Trading.V1;

// I simboli di stato e la cornice sono UTF-8. Se la console non li accetta si prosegue lo stesso:
// uno strumento che non parte perche' non sa disegnare una riga sarebbe assurdo.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var comando = args.Length > 0 ? args[0].ToLowerInvariant() : "aiuto";

if (comando is "aiuto" or "help" or "-h" or "--help")
{
    Aiuto();
    return 0;
}

// --- da dove si leggono indirizzo e segreto ------------------------------------------------------
// Dal repository PRINCIPALE, anche girando da un worktree: ogni worktree ha un appsettings.json
// proprio, gitignorato e fermo al giorno in cui e' nato. Puntare il motore sbagliato — o fallire
// l'autenticazione con un segreto vecchio — sarebbe la stessa classe di guasto gia' pagata due
// volte (backup dal worktree, guscio dal worktree).
var repo = RadicePrincipale();
var configurazione = Path.Combine(repo, "ProcioneMGR", "appsettings.json");

var url = Environment.GetEnvironmentVariable("Trading__RemoteUrl") ?? Leggi("Trading", "RemoteUrl");
var segreto = Environment.GetEnvironmentVariable("Trading__GrpcSharedSecret") ?? Leggi("Trading", "GrpcSharedSecret");

if (string.IsNullOrWhiteSpace(url))
{
    Errore($"Trading:RemoteUrl non trovato in {configurazione}.");
    return 2;
}
if (string.IsNullOrWhiteSpace(segreto))
{
    // Fail-closed, come il lato server: senza segreto NON si prova a chiamare senza.
    Errore("Trading:GrpcSharedSecret non configurato: il motore rifiuterebbe comunque la chiamata.");
    return 2;
}

using var canale = GrpcChannel.ForAddress(url);
var client = new Proto.TradingCommandService.TradingCommandServiceClient(
    canale.Intercept(new SharedSecretClientInterceptor(segreto)));

// Il numero di corsie non si indovina: lo dice la configurazione, ed e' lo stesso valore che il
// guscio e il motore confrontano all'avvio (LaneCountCoherenceProbe).
var quante = int.TryParse(Leggi("Trading", "LaneCount"), out var n) ? n : 8;

try
{
    return comando switch
    {
        "stato" or "status" => await Stato(),
        "avvia" or "start" => await Avvia(),
        "ferma" or "stop" => await Ferma(),
        _ => Sconosciuto(comando),
    };
}
catch (RpcException ex)
{
    Errore($"il motore ha rifiutato o non risponde: {ex.Status.StatusCode} — {ex.Status.Detail}");
    if (ex.Status.StatusCode == StatusCode.Unauthenticated)
        Console.WriteLine("    il segreto condiviso non combacia con quello del motore in esecuzione.");
    if (ex.Status.StatusCode == StatusCode.Unavailable)
        Console.WriteLine($"    {url} non risponde: il tunnel e' aperto? `procione ripara tunnel`.");
    return 2;
}

// =================================================================================================

async Task<int> Stato()
{
    Console.WriteLine($"Corsie sul motore {url}\n");
    Console.WriteLine("  id  stato      modalita'  mercato  coppia            aggiornata");
    Console.WriteLine("  ──  ─────────  ─────────  ───────  ────────────────  ──────────");
    for (var lane = 0; lane < quante; lane++)
    {
        var s = await client.GetLaneStatusAsync(new Proto.GetLaneStatusRequest { LaneId = lane });
        Console.WriteLine($"  {lane,2}  {(s.Running ? "IN MARCIA" : "ferma    "),-9}  " +
                          $"{Modalita(s.Mode),-9}  {Mercato(s.MarketType),-7}  " +
                          $"{(string.IsNullOrWhiteSpace(s.Symbol) ? "(nessuna)" : s.Symbol),-16}  " +
                          $"{s.ExchangeName}");
    }
    return 0;
}

async Task<int> Avvia()
{
    var modalita = Opzione("--modalita") ?? Opzione("--mode");
    if (modalita is null)
    {
        Errore("manca --modalita. Va scritta: decide se gli ordini sono simulati o veri.");
        Console.WriteLine("    LaneControl avvia tutte --modalita paper");
        return 2;
    }

    if (modalita.Equals("live", StringComparison.OrdinalIgnoreCase))
    {
        // Non e' un controllo che si puo' forzare: e' il punto.
        Errore("Live NON si avvia da qui, e non esiste un flag per farlo.");
        Console.WriteLine("    Verso Live non c'e' nessun percorso automatico: si passa dalla pagina /trading,");
        Console.WriteLine("    dove la conferma umana e' obbligatoria (Trading:Safety:RequireManualConfirmationForLive).");
        return 2;
    }

    var modo = modalita.ToLowerInvariant() switch
    {
        "paper" => Proto.TradingMode.Paper,
        "testnet" => Proto.TradingMode.Testnet,
        _ => Proto.TradingMode.Unspecified,
    };
    if (modo == Proto.TradingMode.Unspecified)
    {
        Errore($"modalita' non riconosciuta: '{modalita}'. Attese: paper, testnet.");
        return 2;
    }

    var corsie = Corsie();
    if (corsie.Count == 0) return 2;

    Console.WriteLine($"Avvio di {corsie.Count} corsie in modalita' {Modalita(modo)} sul motore {url}\n");

    var esito = 0;
    foreach (var lane in corsie)
    {
        var prima = await client.GetLaneStatusAsync(new Proto.GetLaneStatusRequest { LaneId = lane });
        if (prima.Running)
        {
            Console.WriteLine($"  ·  corsia {lane}  gia' in marcia ({Modalita(prima.Mode)}), lasciata com'e'");
            continue;
        }

        await client.StartLaneAsync(new Proto.StartLaneRequest { LaneId = lane, Mode = modo });

        // Il verdetto e' la VERIFICA, non l'assenza di eccezioni: si richiede lo stato e si guarda.
        var dopo = await client.GetLaneStatusAsync(new Proto.GetLaneStatusRequest { LaneId = lane });
        if (dopo.Running)
        {
            Console.WriteLine($"  ●  corsia {lane}  avviata — {Modalita(dopo.Mode)} {Mercato(dopo.MarketType)} " +
                              $"{dopo.ExchangeName} {dopo.Symbol}");
        }
        else
        {
            Console.WriteLine($"  ✖  corsia {lane}  il comando non ha lanciato ma la corsia risulta ancora FERMA");
            esito = 1;
        }
    }
    return esito;
}

async Task<int> Ferma()
{
    var corsie = Corsie();
    if (corsie.Count == 0) return 2;

    Console.WriteLine($"Arresto di {corsie.Count} corsie sul motore {url}\n");
    var esito = 0;
    foreach (var lane in corsie)
    {
        await client.StopLaneAsync(new Proto.StopLaneRequest { LaneId = lane });
        var dopo = await client.GetLaneStatusAsync(new Proto.GetLaneStatusRequest { LaneId = lane });
        if (!dopo.Running) Console.WriteLine($"  ●  corsia {lane}  ferma (verificato)");
        else { Console.WriteLine($"  ✖  corsia {lane}  risulta ancora in marcia"); esito = 1; }
    }
    return esito;
}

// =================================================================================================
//  Lettura degli argomenti
// =================================================================================================

List<int> Corsie()
{
    var quali = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
    if (quali is null)
    {
        Errore("manca l'elenco delle corsie. Attesi: `tutte`, oppure `0,1,2`.");
        return [];
    }
    if (quali.Equals("tutte", StringComparison.OrdinalIgnoreCase) ||
        quali.Equals("all", StringComparison.OrdinalIgnoreCase))
        return [.. Enumerable.Range(0, quante)];

    var lista = new List<int>();
    foreach (var pezzo in quali.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        if (!int.TryParse(pezzo.Trim(), out var id) || id < 0 || id >= quante)
        {
            Errore($"corsia non valida: '{pezzo.Trim()}'. Le corsie vanno da 0 a {quante - 1}.");
            return [];
        }
        lista.Add(id);
    }
    return lista;
}

string? Opzione(string nome)
{
    var i = Array.FindIndex(args, a => string.Equals(a, nome, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// =================================================================================================
//  Utilita'
// =================================================================================================

string? Leggi(string sezione, string chiave)
{
    try
    {
        if (!File.Exists(configurazione)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(configurazione));
        return doc.RootElement.TryGetProperty(sezione, out var s) && s.TryGetProperty(chiave, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()
            : null;
    }
    catch { return null; }
}

static string RadicePrincipale()
{
    const string segno = @"\.claude\worktrees\";
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
    var radice = dir?.FullName ?? Directory.GetCurrentDirectory();
    var i = radice.IndexOf(segno, StringComparison.OrdinalIgnoreCase);
    return i < 0 ? radice : radice[..i];
}

static string Modalita(Proto.TradingMode m) => m switch
{
    Proto.TradingMode.Paper => "Paper",
    Proto.TradingMode.Testnet => "Testnet",
    Proto.TradingMode.Live => "LIVE",
    _ => "—",
};

static string Mercato(Proto.MarketType t) => t switch
{
    Proto.MarketType.Spot => "Spot",
    Proto.MarketType.Futures => "Futures",
    _ => "—",
};

static int Sconosciuto(string c)
{
    Errore($"comando sconosciuto: '{c}'.");
    Aiuto();
    return 2;
}

static void Errore(string testo)
{
    var vecchio = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"  ✖ {testo}");
    Console.ForegroundColor = vecchio;
}

static void Aiuto() => Console.WriteLine("""

  LaneControl — le corsie di trading dalla riga di comando

    LaneControl stato                        cosa sta girando, e in che modalita'
    LaneControl avvia tutte --modalita paper avvia tutte le corsie
    LaneControl avvia 1,2,3 --modalita paper avvia solo quelle elencate
    LaneControl ferma tutte                  le ferma

  Modalita' accettate: paper, testnet.

  LIVE NO. Verso Live non esiste nessun percorso automatico in questo progetto: si passa dalla
  pagina /trading, dove la conferma umana e' obbligatoria. Non c'e' un flag per aggirarlo, ed e'
  deliberato — uno strumento da riga di comando che sa dire "Live" e' il percorso che non deve
  esistere.

  Indirizzo e segreto si leggono da ProcioneMGR/appsettings.json del repository PRINCIPALE
  (sovrascrivibili con Trading__RemoteUrl e Trading__GrpcSharedSecret).

""");
