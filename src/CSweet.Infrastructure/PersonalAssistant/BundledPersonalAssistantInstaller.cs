using System.IO.Compression;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Application.PersonalAssistant;
using CSweet.Contracts.Agents;
using CSweet.Contracts.PersonalAssistant;
using CSweet.Domain.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.PersonalAssistant;

/// <summary>Ships source with the platform, then uses its ordinary reviewed build/install pipeline.</summary>
public sealed class BundledPersonalAssistantInstaller(CSweetDbContext db, IPluginArchiveImportService archives,
    IBundledAssistantInstallationService installations)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<Guid> EnsureAsync(Guid userId, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var existing = await db.PersonalAssistantProfiles.SingleOrDefaultAsync(x => x.ApplicationUserId == userId, token);
            if (existing is not null) return existing.AgentInstallationId;
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"personal-assistant-install"}, 0))", token);
            existing = await db.PersonalAssistantProfiles.SingleOrDefaultAsync(x => x.ApplicationUserId == userId, token);
            if (existing is not null) return existing.AgentInstallationId;
            using var source = CreateSourceArchive();
            var preview = await archives.PreviewSourceArchiveAsync(source, "csweet-personal-assistant.zip", token);
            var version = await db.AgentPackageVersions.Include(x => x.PackageSource).SingleAsync(x => x.Id == preview.ImportId, token);
            version.PackageSource!.IsBundled = true;
            await db.SaveChangesAsync(token);
            var result = await installations.InstallAsync(preview.ImportId, userId, new InstallAgentRequest(
                PersonalAssistantCapabilities.UserScope(userId), "OnDemand", 300, "Skip",
                [PersonalAssistantCapabilities.Converse, PersonalAssistantCapabilities.Analyze,
                 "agent.configuration.describe.v1", "agent.configuration.update.v1"], [], [], [], [], 1800, 1024, 50)
            {
                PluginScope = "User",
                GrantedRequestedCapabilities = ["platform.llm.chat-stream.v1"],
                ConfigurationSchemaVersion = "1.0",
                ConfigurationSettings = settings
            }, token);
            var installation = await db.AgentInstallations.SingleAsync(x => x.Id == result.Id, token);
            installation.OwnerApplicationUserId = userId;
            db.PersonalAssistantProfiles.Add(new PersonalAssistantProfile
            { ApplicationUserId = userId, AgentInstallationId = result.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return result.Id;
        }
        finally { Gate.Release(); }
    }

    internal static MemoryStream CreateSourceArchive()
    {
        var assembly = typeof(BundledPersonalAssistantInstaller).Assembly;
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var name in assembly.GetManifestResourceNames().Where(x => x.StartsWith("PersonalAssistant/", StringComparison.Ordinal)).Order())
            {
                var path = name["PersonalAssistant/".Length..].Replace('\\', '/');
                var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var source = assembly.GetManifestResourceStream(name)!;
                using var destination = entry.Open();
                source.CopyTo(destination);
            }
        output.Position = 0;
        return output;
    }

    internal static CSweet.Contracts.Plugins.PluginManifest ReadManifest()
    {
        using var source = typeof(BundledPersonalAssistantInstaller).Assembly.GetManifestResourceStream("PersonalAssistant/csweet-plugin.json")!;
        return JsonSerializer.Deserialize<CSweet.Contracts.Plugins.PluginManifest>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
