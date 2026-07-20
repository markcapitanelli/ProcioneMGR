using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcioneMGR.Services.Security;

namespace ProcioneMGR.Tests;

/// <summary>
/// Il nome applicativo di Data Protection è la discriminante con cui vengono derivate le chiavi che
/// firmano i cookie di autenticazione: due processi con discriminanti diverse non possono leggere i
/// cookie l'uno dell'altro.
///
/// BUG REALE (2026-07-20): <c>SetApplicationName</c> veniva applicato SOLO dentro il ramo
/// <c>if (keyRingPath)</c> — cioè mai in sviluppo locale, dove quel path è vuoto per scelta. Il
/// default di ASP.NET Core deriva allora la discriminante dal <c>ContentRootPath</c>, e due copie
/// dello stesso repository in cartelle diverse (un worktree git accanto al checkout principale) ne
/// ottengono due diverse pur condividendo il keyring del profilo utente. Sintomo osservato: si
/// arriva alla pagina di login, si accede, e si resta fuori — senza alcun messaggio d'errore.
///
/// I test esercitano la composizione REALE (<see cref="DataProtectionSetup"/>, usata da Program.cs)
/// e non una sua copia: una regressione nell'app fa fallire questi test.
/// </summary>
public class DataProtectionApplicationNameTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProcioneDataProtection(config);
        return services.BuildServiceProvider();
    }

    private static string? DiscriminatorOf(ServiceProvider sp) =>
        sp.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator;

    [Fact]
    public void ApplicationName_IsSet_EvenWithoutAKeyRingPath()
    {
        // La regressione esatta: senza keyring path il nome non veniva impostato affatto.
        using var sp = Build();

        Assert.Equal(DataProtectionSetup.ApplicationName, DiscriminatorOf(sp));
    }

    [Fact]
    public void ApplicationName_IsTheSame_WithAndWithoutAKeyRingPath()
    {
        // Migrare da bare-metal (nessun path) a container (PVC montata) non deve cambiare la
        // discriminante, altrimenti la migrazione invaliderebbe tutti i cookie esistenti.
        using var local = Build();
        using var container = Build(("DataProtection:KeyRingPath", Path.Combine(Path.GetTempPath(), "procione-keyring-test")));

        Assert.Equal(DiscriminatorOf(local), DiscriminatorOf(container));
        Assert.Equal(DataProtectionSetup.ApplicationName, DiscriminatorOf(container));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyKeyRingPath_IsTreatedAsAbsent_ButTheNameStaysSet(string? path)
    {
        // Un path vuoto in appsettings.json (il default del template) non deve tentare di creare
        // una directory, ma nemmeno far perdere il nome applicativo.
        using var sp = Build(("DataProtection:KeyRingPath", path));

        Assert.Equal(DataProtectionSetup.ApplicationName, DiscriminatorOf(sp));
    }

    [Fact]
    public void ApplicationName_IsAStableLiteral_NotDerivedFromThePath()
    {
        // Se qualcuno un giorno lo rendesse dinamico (per esempio ricavandolo dall'assembly o dal
        // percorso), il bug tornerebbe silenziosamente: il valore deve restare una costante.
        Assert.Equal("ProcioneMGR", DataProtectionSetup.ApplicationName);
    }
}
