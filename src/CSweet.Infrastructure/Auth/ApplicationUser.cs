using Microsoft.AspNetCore.Identity;

namespace CSweet.Infrastructure.Auth;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsInitialAdministrator { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Supported UI culture code (see <c>CSweet.Contracts.Localization.SupportedCultures</c>); null follows the device language.</summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>IANA time zone id used when presenting times; null follows the device time zone.</summary>
    public string? TimeZoneId { get; set; }
}
