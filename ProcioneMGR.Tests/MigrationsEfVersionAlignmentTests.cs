using System.Text.RegularExpressions;

namespace ProcioneMGR.Tests;

/// <summary>
/// [Fase 5, 2026-08-11] Il guardiano nato da un primo avvio SENZA schema: il progetto delle
/// migrazioni aveva <c>Microsoft.EntityFrameworkCore.Design</c> a 10.0.9 mentre l'app pubblicava
/// EF 10.0.8 — la DLL delle migrazioni chiedeva Relational 10.0.9, il binder rifiutava (la
/// versione trovata era più bassa della richiesta), OGNI classe Migration falliva il load, EF
/// ingoiava l'eccezione e dichiarava «zero migrazioni» ⇒ «schema già allineato» su un database
/// VUOTO. Rotto in silenzio sia nel container sia sull'host, mascherato solo dallo schema già
/// migrato a mano.
///
/// La regola è una sola e vive qui: le versioni della famiglia EF di app e progetto migrazioni
/// devono combaciare. Chi fa un bump lo fa su ENTRAMBI i csproj, o la suite diventa rossa —
/// che è esattamente il momento giusto per accorgersene.
/// </summary>
public sealed class MigrationsEfVersionAlignmentTests
{
    private static readonly Regex PackageRe = new(
        "Include=\"(?<id>[A-Za-z0-9.]+)\"\\s+Version=\"(?<v>[0-9.]+)\"", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProcioneMGR.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Dictionary<string, string> Packages(string csprojRelative)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), csprojRelative));
        return PackageRe.Matches(text)
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>I pacchetti della famiglia EF core (stesso treno di versioni di Microsoft).</summary>
    private static bool IsEfFamily(string id) =>
        id.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Microsoft.AspNetCore.Identity.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
        || id.Equals("Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void LaFamigliaEfDellApp_ViaggiaSuUnaVersioneSola()
    {
        // Precondizione del confronto col progetto migrazioni: se l'app stessa mescolasse versioni
        // EF, "la versione dell'app" non sarebbe definita e il load dei tipi sarebbe già a rischio.
        var app = Packages("ProcioneMGR/ProcioneMGR.csproj")
            .Where(kv => IsEfFamily(kv.Key))
            .ToList();

        Assert.NotEmpty(app);
        var versions = app.Select(kv => kv.Value).Distinct().ToList();
        Assert.True(versions.Count == 1,
            "La famiglia EF dell'app usa più versioni: " +
            string.Join(", ", app.Select(kv => $"{kv.Key}={kv.Value}")) +
            ". Allineale: versioni miste fanno fallire il load dei tipi in modo silenzioso.");
    }

    [Fact]
    public void IlProgettoMigrazioni_UsaLaStessaVersioneEfDellApp()
    {
        var appVersion = Packages("ProcioneMGR/ProcioneMGR.csproj")
            .Where(kv => IsEfFamily(kv.Key))
            .Select(kv => kv.Value)
            .Distinct()
            .Single();

        var migrations = Packages("ProcioneMGR.Migrations.Postgres/ProcioneMGR.Migrations.Postgres.csproj")
            .Where(kv => IsEfFamily(kv.Key))
            .ToList();

        Assert.NotEmpty(migrations);
        foreach (var (id, version) in migrations)
        {
            Assert.True(version == appVersion,
                $"{id} nel progetto migrazioni è {version} ma l'app usa EF {appVersion}: la DLL " +
                "delle migrazioni chiederebbe assembly che l'app non pubblica, ogni Migration " +
                "fallirebbe il load e il migrate-on-startup direbbe «già allineato» su un DB vuoto. " +
                "Il bump si fa su ENTRAMBI i csproj.");
        }
    }
}
