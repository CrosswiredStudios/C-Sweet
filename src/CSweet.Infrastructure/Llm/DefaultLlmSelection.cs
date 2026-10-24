using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Llm;

internal sealed record DefaultLlmSelection(Guid ProviderId, string Model)
{
    internal static async Task<DefaultLlmSelection?> ReadAsync(CSweetDbContext db, CancellationToken token)
    {
        var providerId = await db.SystemConfigurations.AsNoTracking().OrderBy(x => x.CreatedAt)
            .Select(x => x.DefaultChatProviderId).FirstOrDefaultAsync(token);
        if (providerId is null) return null;
        var profile = await db.LlmProviderProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == providerId && x.IsEnabled, token);
        return string.IsNullOrWhiteSpace(profile?.DefaultChatModel) ? null : new(profile.Id, profile.DefaultChatModel);
    }
}
