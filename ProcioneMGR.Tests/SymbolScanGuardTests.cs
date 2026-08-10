using System.Text.RegularExpressions;

namespace ProcioneMGR.Tests;

/// <summary>
/// [E-04, Fase 2 PRD-RISANAMENTO] Il guardiano del catalogo simboli. La 2.1 aveva sostituito con
/// <c>ISymbolCatalog</c> le scansioni <c>OhlcvData…Select(Symbol)…Distinct()</c> che ogni pagina
/// rifaceva per conto proprio — una scansione solo-indice su ~12M righe per ~30 stringhe — ma
/// QUATTRO copie erano sfuggite (FeatureSelection, e Backtest/Optimization/MlLab attraverso i loro
/// page service, che il censimento per pagine non aveva contato), scoperte alla verifica browser
/// del 2026-08-10 dai ~5 s di apertura di /feature-selection e /backtest.
///
/// <para>Questo test impedisce che il buco si riapra, come <see cref="ConfigurationUiCoverageTests"/>
/// per i pannelli: scandisce i sorgenti e pretende che ogni scansione diretta o non esista, o sia
/// iscritta nell'inventario qui sotto con la ragione per cui il catalogo non le basta.</para>
/// </summary>
public sealed class SymbolScanGuardTests
{
    /// <summary>
    /// Scansioni dirette AMMESSE, con la ragione. La strada normale è <c>ISymbolCatalog</c>
    /// (cache condivisa con TTL, politica dichiarata e testata, invalidazione dalla watchlist):
    /// una riga qui si aggiunge solo se il catalogo non può rispondere alla domanda.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedScans = new()
    {
        // Il catalogo stesso: l'UNICA scansione che deve esistere, è il suo mestiere.
        ["ProcioneMGR/Services/MarketData/SymbolCatalog.cs"] =
            "punto unico dichiarato della scansione, con cache e politica testata",
        // Chiede le COPPIE (Symbol, Timeframe) realmente presenti a DB: il catalogo copre solo
        // l'asse simboli, e il prodotto cartesiano simboli × timeframe mentirebbe sulle serie
        // senza dati. Candidata a un'estensione del catalogo, non a una sostituzione ingenua.
        ["ProcioneMGR/Components/Pages/Discovery.razor"] =
            "coppie simbolo+timeframe con dati: semantica non coperta dal catalogo",
    };

    /// <summary>
    /// Una scansione diretta: <c>OhlcvData</c> in una catena che arriva a <c>.Distinct()</c> con
    /// <c>Symbol</c> nella proiezione (il filtro sta in <see cref="IsSymbolScan"/>). La finestra
    /// di 160 caratteri copre le forme in uso — con o senza <c>AsNoTracking()</c>, proiezione
    /// semplice o anonima — senza scavalcare lo statement.
    /// </summary>
    private static readonly Regex DirectScan = new(
        @"OhlcvData\s*\.[\s\S]{0,160}?\.Distinct\s*\(\s*\)", RegexOptions.Compiled);

    private static bool IsSymbolScan(Match m) => m.Value.Contains("Symbol", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Sorgenti (C# e Razor) del guscio e del motore; bin/obj sono copie generate.</summary>
    private static IEnumerable<(string RelativePath, string Text)> SourceFiles()
    {
        var root = RepoRoot();
        foreach (var project in new[] { "ProcioneMGR", "ProcioneMGR.Trading" })
        {
            var dir = Path.Combine(root, project);
            foreach (var pattern in new[] { "*.cs", "*.razor" })
            {
                foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                    var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                    yield return (relative, File.ReadAllText(file));
                }
            }
        }
    }

    [Fact]
    public void NoDirectSymbolScan_OutsideTheDeclaredAllowList()
    {
        var offenders = SourceFiles()
            .Where(f => !AllowedScans.ContainsKey(f.RelativePath))
            .Where(f => DirectScan.Matches(f.Text).Any(IsSymbolScan))
            .Select(f => f.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Scansione diretta dei simboli su OhlcvData fuori dal catalogo: " +
            string.Join(", ", offenders) +
            ". Usa ISymbolCatalog.GetKnownSymbolsAsync() (iniettato, cache condivisa); se il " +
            "catalogo davvero non può rispondere, iscrivi il file in AllowedScans con la ragione.");
    }

    [Fact]
    public void TheAllowListHasNoStaleEntries()
    {
        // Il rovescio: una voce rimasta nell'inventario dopo che la scansione è sparita farebbe
        // passare inosservata una scansione REINTRODOTTA in quel file.
        var withScan = SourceFiles()
            .Where(f => DirectScan.Matches(f.Text).Any(IsSymbolScan))
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        var stale = AllowedScans.Keys.Where(k => !withScan.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "Voci di AllowedScans senza più una scansione nel file: " + string.Join(", ", stale));
    }
}
