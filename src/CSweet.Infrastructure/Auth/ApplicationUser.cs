using Microsoft.AspNetCore.Identity;

namespace CSweet.Infrastructure.Auth;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsInitialAdministrator { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
