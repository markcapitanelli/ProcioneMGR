using Microsoft.AspNetCore.DataProtection;

namespace ProcioneMGR.Services.Security;

/// <summary>
/// Composizione di Data Protection, estratta da Program.cs per essere verificabile da test.
///
/// Data Protection è ciò che firma e cifra i cookie di autenticazione. La sua discriminante
/// applicativa decide quali chiavi vengono derivate: due processi con discriminanti diverse non
/// possono leggere i cookie l'uno dell'altro.
/// </summary>
public static class DataProtectionSetup
{
    /// <summary>
    /// Nome applicativo FISSO. Il default di ASP.NET Core lo deriva dal <c>ContentRootPath</c>, e
    /// quello è il difetto: due copie dello STESSO repository in cartelle diverse — un worktree git
    /// accanto al checkout principale — otterrebbero discriminanti diverse pur condividendo il
    /// keyring del profilo utente. I cookie emessi dall'una risultano illeggibili all'altra, e il
    /// sintomo è particolarmente sgradevole perché muto: si arriva alla pagina di login, si accede,
    /// e si resta fuori senza alcun messaggio d'errore.
    /// </summary>
    public const string ApplicationName = "ProcioneMGR";

    /// <summary>
    /// Registra Data Protection. Il nome applicativo si imposta SEMPRE; il keyring su filesystem
    /// solo se <c>DataProtection:KeyRingPath</c> è valorizzato.
    ///
    /// Prima di questa estrazione il nome veniva impostato SOLO dentro il ramo del keyring, cioè
    /// mai in sviluppo locale (dove il path è vuoto per scelta: fuori da un container il default
    /// di ASP.NET Core scrive già in una cartella del profilo utente, persistente fra i riavvii).
    ///
    /// Dentro un container serve invece un percorso persistito: senza, il keyring vive in memoria e
    /// OGNI riavvio del pod disconnette tutti gli utenti — e non serve un deploy, basta un OOM-kill
    /// o una liveness probe fallita. In K8s si monta una PVC (vedi infra/k8s/ui/deployment.yaml).
    /// </summary>
    public static IServiceCollection AddProcioneDataProtection(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        var keyRingPath = configuration["DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyRingPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        }

        return services;
    }
}
