using System.Text.Json;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Reads extension-owned artifact metadata from an exact, pinned workstream profile.</summary>
internal static class ProfileArtifactMetadata
{
    internal static JsonElement? Find(JsonElement profile, string type)
    {
        if (!profile.TryGetProperty("artifactTypes", out var types)) return null;
        foreach (var item in types.EnumerateArray())
            if (item.GetProperty("key").GetString() == type) return item;
        return null;
    }

    internal static void ValidatePayload(JsonElement profile, string type, string version, JsonElement payload)
    {
        var declaration = Find(profile, type);
        if (declaration is not { } metadata) return;
        if (metadata.GetProperty("schemaVersion").GetString() != version)
            throw new ArgumentException("The artifact schema version does not match the selected profile.");
        if (metadata.TryGetProperty("payloadSchema", out var schema))
            WorkstreamProfileDefinitionValidator.ValidateProfileData(schema, payload);
    }

    internal static async Task<string?> ReadDefinitionAsync(CSweetDbContext db,
        Guid organizationId, Guid? workstreamId, CancellationToken token)
    {
        if (workstreamId is null) return null;
        return await (from workstream in db.Workstreams.AsNoTracking()
                      join profile in db.WorkstreamProfileDefinitions.AsNoTracking()
                          on new { Key = workstream.ProfileKey, Version = workstream.ProfileVersion }
                          equals new { Key = (string?)profile.Key, Version = (int?)profile.Version }
                      where workstream.Id == workstreamId && workstream.OrganizationId == organizationId &&
                            profile.DefinitionDigest == workstream.ProfileDefinitionDigest
                      select profile.DefinitionJson).SingleOrDefaultAsync(token);
    }
}
