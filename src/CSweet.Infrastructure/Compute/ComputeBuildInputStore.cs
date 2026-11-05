using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Only the submitting installation's hash-bound operating-state parts are eligible input.</summary>
internal static class ComputeBuildInputStore
{
    internal static async Task<byte[]> ReadAsync(CSweetDbContext db, Guid organization, Guid installation,
        ComputeBuildArchive archive, int offset, CancellationToken token)
    {
        archive.Validate();
        if (offset < 0 || offset >= archive.Length || offset % ComputeBuildArchive.PartBytes != 0)
            throw new ArgumentException("Invalid build input offset.");
        var key = archive.PartKey(offset);
        var row = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organization && x.AgentInstallationId == installation && x.Kind == "agent-operating-state" && x.ExternalKey == key, token)
            ?? throw new InvalidDataException("The staged build input is unavailable.");
        using var envelope = JsonDocument.Parse(row.PayloadJson);
        var root = envelope.RootElement;
        if (root.GetProperty("schemaId").GetString() != ComputeBuildArchive.SchemaId)
            throw new InvalidDataException("The staged build input schema differs.");
        var part = root.GetProperty("payload").Deserialize<ComputeBuildInputPart>(ComputeProtocol.Json);
        if (part is null || part.SourceCommitSha != archive.SourceCommitSha || part.BuildSha256 != archive.BuildSha256 ||
            part.Length != archive.Length || part.Offset != offset || part.Content is not { } content ||
            content.Length != Math.Min(ComputeBuildArchive.PartBytes, archive.Length - offset) ||
            Convert.ToHexStringLower(SHA256.HashData(content)) != archive.PartSha256[offset / ComputeBuildArchive.PartBytes])
            throw new InvalidDataException("The staged build input differs from its pinned identity.");
        return content;
    }

    internal static async Task VerifyAsync(CSweetDbContext db, Guid organization, Guid installation,
        ComputeBuildArchive archive, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < archive.Length; offset += ComputeBuildArchive.PartBytes)
            hash.AppendData(await ReadAsync(db, organization, installation, archive, offset, token));
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != archive.BuildSha256)
            throw new InvalidDataException("The staged archive digest differs.");
    }
}
