using System.Reflection;
using System.Text.RegularExpressions;

namespace ProcioneMGR.Tests;

/// <summary>
/// LA REGOLA D'ORO, GUARDATA PER <b>CHIAVE</b> E NON PER SEZIONE.
///
/// <para><see cref="ConfigurationUiCoverageTests"/> pretende che ogni <i>sezione</i> di
/// configurazione abbia una pagina. È il guardiano giusto per la domanda che pone — ma lascia un
/// buco largo: <b>una chiave nuova dentro una sezione già mappata passa senza pannello e nessun
/// test protesta</b>. La sezione c'è, la pagina la nomina, il test è verde, e la manopola si può
/// toccare solo editando <c>appsettings.json</c> a mano.</para>
///
/// <para>Lo spoglio del 2026-08-20 ha trovato così <b>19 chiavi su 190</b> senza pannello. Fra
/// queste, tre di <c>SafetyConfiguration</c>: la banda di plausibilità del prezzo di fill e della
/// quantità — che esistono per il bug B1, dove un testnet rispondeva «Filled @ 0» e il PnL segnava
/// −1,8 milioni — e l'interruttore degli stop piazzati sull'exchange. E i cinque pesi del composite
/// di sentiment, che decidono quanto ogni fonte conta nel segnale.</para>
///
/// <para>Nessuna di quelle era una svista clamorosa: erano il risultato naturale di un guardiano che
/// guarda un livello più in alto di dove nasce il difetto. Questo test guarda al livello giusto.</para>
/// </summary>
public sealed class ConfigurationKeyUiCoverageTests
{
    /// <summary>
    /// Chiavi che possono non avere un controllo <i>editabile</i>, con la ragione. Vuoto: il mandato
    /// del proprietario (2026-08-09) non ammette eccezioni implicite, e per quelle che non sono
    /// manopole — un percorso di deploy — la strada è <b>mostrarle</b> accanto a ciò che governano,
    /// non elencarle qui. La mappa resta perché una futura eccezione sia una decisione scritta.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatamenteSenzaControllo = new();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// I POCO legati a una sezione di configurazione, letti da <c>Program.cs</c> — la stessa
    /// sorgente del guardiano per sezione, così i due non possono divergere su <i>cosa</i> è
    /// configurazione.
    /// </summary>
    private static IReadOnlyList<Type> RadiciDiConfigurazione(string root)
    {
        var program = File.ReadAllText(Path.Combine(root, "ProcioneMGR", "Program.cs"));
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
    /// Tutte le proprietà scrivibili raggiungibili da una radice, scendendo nei POCO ANNIDATI (per
    /// esempio <c>Sentiment:HeritageGuard</c>): il binder ci arriva, quindi ci deve arrivare anche
    /// il guardiano.
    /// </summary>
    private static void Raccogli(Type t, string percorso, HashSet<Type> visti, List<(string Percorso, string Nome)> fuori)
    {
        if (!visti.Add(t)) return;

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetSetMethod() is null) continue;

            var tipo = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var annidato = tipo.IsClass
                           && tipo != typeof(string)
                           && !tipo.IsArray
                           && !typeof(System.Collections.IEnumerable).IsAssignableFrom(tipo)
                           && tipo.Assembly == t.Assembly;

            if (annidato) Raccogli(tipo, $"{percorso}:{p.Name}", visti, fuori);
            else fuori.Add(($"{percorso}:{p.Name}", p.Name));
        }
    }

    private static string TuttoIlMarkup(string root)
    {
        var dir = Path.Combine(root, "ProcioneMGR", "Components");
        return string.Join('\n', Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    /// <summary>
    /// <b>Ogni chiave di configurazione è nominata da almeno una pagina.</b>
    ///
    /// <para>«Nominata» e non «editabile» di proposito: alcune chiavi non sono manopole ma vincoli
    /// (un percorso di deploy), e per quelle la forma giusta è <i>mostrarle</i> accanto a ciò che
    /// governano — così un comportamento inspiegabile ha una spiegazione a portata di mano. Il test
    /// pretende la visibilità; è la revisione umana a decidere se serve anche un campo.</para>
    /// </summary>
    [Fact]
    public void OgniChiaveDiConfigurazione_ENominataDaAlmenoUnaPagina()
    {
        var root = RepoRoot();
        var markup = TuttoIlMarkup(root);

        var fuori = new List<(string Percorso, string Nome)>();
        var visti = new HashSet<Type>();
        foreach (var radice in RadiciDiConfigurazione(root))
        {
            Raccogli(radice, radice.Name, visti, fuori);
        }

        Assert.True(fuori.Count > 100,
            $"solo {fuori.Count} chiavi raggiunte: il guardiano non sta guardando la configurazione vera");

        var scoperte = fuori
            .Where(k => !DeliberatamenteSenzaControllo.ContainsKey(k.Percorso))
            .Where(k => !Regex.IsMatch(markup, $@"\b{Regex.Escape(k.Nome)}\b"))
            .Select(k => k.Percorso)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.True(scoperte.Count == 0,
            $"{scoperte.Count} chiavi di configurazione non compaiono in NESSUNA pagina — si possono toccare solo "
            + "editando appsettings.json a mano, che il mandato del 2026-08-09 vieta:\n  "
            + string.Join("\n  ", scoperte)
            + "\n\nLa strada per sistemarle è dare loro un pannello (o mostrarle, se sono vincoli e non manopole), "
            + "non aggiungere una riga a DeliberatamenteSenzaControllo.");
    }

    /// <summary>
    /// L'inventario delle eccezioni non contiene voci morte: una chiave rinominata o rimossa che
    /// resta elencata qui è un permesso che protegge qualcosa che non esiste più, e la prossima
    /// chiave con lo stesso nome lo eredita in silenzio.
    /// </summary>
    [Fact]
    public void LInventarioDelleEccezioni_NonHaVociMorte()
    {
        var root = RepoRoot();
        var fuori = new List<(string Percorso, string Nome)>();
        var visti = new HashSet<Type>();
        foreach (var radice in RadiciDiConfigurazione(root))
        {
            Raccogli(radice, radice.Name, visti, fuori);
        }
        var esistenti = fuori.Select(k => k.Percorso).ToHashSet(StringComparer.Ordinal);

        var morte = DeliberatamenteSenzaControllo.Keys.Where(k => !esistenti.Contains(k)).ToList();

        Assert.True(morte.Count == 0, $"eccezioni per chiavi che non esistono più: {string.Join(", ", morte)}");
    }
}
