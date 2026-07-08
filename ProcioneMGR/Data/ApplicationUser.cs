using Microsoft.AspNetCore.Identity;

namespace ProcioneMGR.Data;

// Add profile data for application users by adding properties to the ApplicationUser class
public class ApplicationUser : IdentityUser
{
    /// <summary>Nome visualizzato opzionale per la UI.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Momento di creazione dell'account (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
