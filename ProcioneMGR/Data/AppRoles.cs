namespace ProcioneMGR.Data;

/// <summary>
/// Ruoli applicativi di ProcioneMGR. Centralizzati come costanti per evitare
/// "magic strings" sparse tra registrazione, seeding e attributi [Authorize].
/// </summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string User = "User";

    /// <summary>Tutti i ruoli, usati dal seeder all'avvio.</summary>
    public static readonly string[] All = [Admin, Manager, User];
}
