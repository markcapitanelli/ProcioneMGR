using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ProcioneMGR.Tests;

/// <summary>
/// NESSUN POCO DI CONFIGURAZIONE HA PROPRIETÀ CALCOLATE.
///
/// <para><b>Perché.</b> <c>AppConfigWriter.SaveSectionAsync</c> — il writer dietro i pannelli di
/// /trading e /admin/autonomy — riscrive la sezione serializzando il POCO <b>intero</b>, e
/// System.Text.Json serializza anche le proprietà in sola lettura. Una <c>get-only</c> su un POCO di
/// configurazione diventa quindi, al primo «Salva», una <b>chiave inventata</b> dentro
/// appsettings.json: un valore che nessun setter rilegge, che il binder ignora, e che al giro dopo
/// qualcuno scambia per configurazione vera perché sta nel file accanto alle altre.</para>
///
/// <para><b>Perché serve un guardiano e non solo attenzione.</b> Il difetto è già stato pagato due
/// volte: le tre calcolate di <c>FactorDriftOptions</c> (convertite in metodi prima del rilascio del
/// pannello) e <c>SentimentHeritageGuardOptions.EffectiveFundingSymbols</c>, che è arrivata fino al
/// pannello Sentiment vivo — non ha sporcato il file solo perché quella sezione non era ancora mai
/// stata salvata. <see cref="ConfigurationKeyUiCoverageTests"/> non poteva vederle: guarda le chiavi
/// per pretendere che abbiano un pannello, e per farlo <b>salta le sole-lettura</b>. Resta verde
/// mentre il file si sporca.</para>
///
/// <para>La forma giusta è un <b>metodo</b>, come <c>DriftMonitorOptions.EffectiveStages()</c>: dice
/// la stessa cosa, non è serializzabile, e non costringe a ricordarsi di un attributo.</para>
/// </summary>
public sealed class ConfigPocoComputedPropertyTests
{
    /// <summary>
    /// Proprietà calcolate ammesse, con la ragione. Vuoto: <c>[JsonIgnore]</c> è già la deroga
    /// scritta nel codice, e questa mappa esiste solo perché un'eventuale eccezione sia una
    /// decisione presa a voce alta invece che una riga tolta dal guardiano.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatamenteCalcolate = new();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// I POCO legati a una sezione di configurazione, letti da <c>Program.cs</c>: la stessa sorgente
    /// dei due guardiani di copertura UI, così i tre non possono divergere su <i>cosa</i> è
    /// configurazione.
    /// </summary>
    private static IReadOnlyList<Type> RadiciDiConfigurazione()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "ProcioneMGR", "Program.cs"));
        var nomi = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(program, @"Configure<\s*([A-Za-z0-9_.]+)\s*>"))
        {
            nomi.Add(m.Groups[1].Value.Split('.')[^1]);
        }
        foreach (Match m in Regex.Matches(program, @"GetSection\(""[^""]+""\)\s*\.\s*Get<\s*([A-Za-z0-9_.]+)\s*>"))
        {
            nomi.Add(m.Groups[1].Value.Split('.')[^1]);
        }

        var assembly = typeof(ProcioneMGR.Services.Carry.CarryConfiguration).Assembly;
        return [.. assembly.GetTypes().Where(t => t.IsClass && nomi.Contains(t.Name))];
    }

    /// <summary>
    /// Scende nei POCO annidati (è lì che viveva <c>Sentiment:HeritageGuard</c>) raccogliendo le
    /// proprietà che <b>verrebbero scritte</b> e che <b>nessuno rileggerebbe</b>: getter pubblico,
    /// nessun setter, nessun <c>[JsonIgnore]</c>.
    /// </summary>
    private static void Raccogli(Type t, string percorso, HashSet<Type> visti, List<string> calcolate)
    {
        if (!visti.Add(t)) return;

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0) continue;      // gli indicizzatori non si serializzano
            if (p.GetGetMethod() is null) continue;               // solo-scrittura: non finisce nel file

            if (p.GetSetMethod() is null)
            {
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is null) calcolate.Add($"{percorso}:{p.Name}");
                continue;
            }

            var tipo = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var annidato = tipo.IsClass
                           && tipo != typeof(string)
                           && !tipo.IsArray
                           && !typeof(System.Collections.IEnumerable).IsAssignableFrom(tipo)
                           && tipo.Assembly == t.Assembly;

            if (annidato) Raccogli(tipo, $"{percorso}:{p.Name}", visti, calcolate);
        }
    }

    [Fact]
    public void NessunPocoDiConfigurazione_HaProprietaCalcolate()
    {
        var calcolate = new List<string>();
        var visti = new HashSet<Type>();
        var radici = RadiciDiConfigurazione();

        Assert.True(radici.Count > 20,
            $"solo {radici.Count} radici di configurazione trovate: il guardiano non sta guardando la configurazione vera");

        foreach (var radice in radici) Raccogli(radice, radice.Name, visti, calcolate);

        var scoperte = calcolate
            .Where(k => !DeliberatamenteCalcolate.ContainsKey(k))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(scoperte.Count == 0,
            $"{scoperte.Count} proprietà calcolate su POCO di configurazione: SaveSectionAsync le scriverebbe "
            + "in appsettings.json come chiavi inventate, che nessun setter rilegge.\n  "
            + string.Join("\n  ", scoperte)
            + "\n\nLa strada è trasformarle in METODI (come DriftMonitorOptions.EffectiveStages()), "
            + "non aggiungere una riga a DeliberatamenteCalcolate.");
    }

    // --- Quanto era grave: la misura, non il ragionamento ------------------------------------

    /// <summary>La forma vecchia di <c>SentimentHeritageGuardOptions</c>, tenuta viva per misurarla.</summary>
    private sealed class FormaVecchia
    {
        public List<string> FundingSymbols { get; set; } = [];

        public static readonly IReadOnlyList<string> Default = ["BTC", "ETH", "SOL", "BNB", "XRP", "DOGE"];

        // Nota la trappola nella trappola: quando la lista configurata NON è vuota, la calcolata
        // restituisce la STESSA istanza di FundingSymbols. Se il binder ci scrivesse dentro (sulle
        // liste APPENDE, non sostituisce), i simboli del funding raddoppierebbero a ogni riavvio.
        public IReadOnlyList<string> EffectiveFundingSymbols =>
            FundingSymbols.Count > 0 ? FundingSymbols : Default;
    }

    /// <summary>
    /// <b>La chiave inventata sporca il file ma non corrompe il dato.</b> Un appsettings.json già
    /// salvato con la forma vecchia contiene <c>EffectiveFundingSymbols</c> accanto a
    /// <c>FundingSymbols</c>: la domanda che valeva la pena porre è se al riavvio il binder, che
    /// sulle liste <i>appende</i> agli elementi esistenti, raddoppiasse i simboli del funding —
    /// visto che la calcolata restituisce la stessa istanza della lista configurata.
    ///
    /// <para>La risposta misurata è no: il binder salta le proprietà che non può assegnare, quindi
    /// la chiave resta inerte. Il test la pinna perché è una garanzia del framework e non del nostro
    /// codice: se cambiasse, la stessa classe di difetto smetterebbe di essere solo sporcizia nel
    /// file e diventerebbe corruzione di dato.</para>
    /// </summary>
    [Fact]
    public void ChiaveInventata_NonRaddoppiaLaListaConfigurata()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["G:FundingSymbols:0"] = "BTC",
            ["G:FundingSymbols:1"] = "ETH",
            // Ciò che SaveSectionAsync avrebbe scritto con la forma vecchia.
            ["G:EffectiveFundingSymbols:0"] = "BTC",
            ["G:EffectiveFundingSymbols:1"] = "ETH",
        }).Build();

        var opzioni = new FormaVecchia();
        config.GetSection("G").Bind(opzioni);

        Assert.Equal(["BTC", "ETH"], opzioni.FundingSymbols);
        Assert.Equal(["BTC", "ETH"], opzioni.EffectiveFundingSymbols);
    }

    /// <summary>
    /// L'altra metà della misura: System.Text.Json <b>scrive davvero</b> le get-only. È l'assunto su
    /// cui poggia tutto il guardiano — se un giorno smettesse di valere, questo test lo direbbe
    /// invece di lasciare in giro una regola diventata superstizione.
    /// </summary>
    [Fact]
    public void SystemTextJson_SerializzaLeGetOnly()
    {
        var json = JsonSerializer.Serialize(new FormaVecchia { FundingSymbols = ["BTC"] });

        Assert.Contains("EffectiveFundingSymbols", json);
    }
}
