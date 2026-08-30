using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

/// <summary>
/// Domain-neutral media surface. Operation type keys are data from enabled provider configurations;
/// the broker does not branch on image, video, audio, texture, or model semantics.
/// </summary>
public sealed class GenericMediaCapabilityHandler(CSweetDbContext db, IGenAiJobService jobs, TimeProvider clock)
    : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Capabilities =
    [
        W.MediaCapabilityNames.ProviderCatalogReadV1, W.MediaCapabilityNames.JobRequestV1,
        W.MediaCapabilityNames.JobReadV1, W.MediaCapabilityNames.JobCancelV1,
        W.MediaCapabilityNames.AssetReferenceV1
    ];

    public bool CanHandle(string capability) => Capabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var installationId))
        { yield return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent identity is invalid."); yield break; }
        CapabilityResult result;
        try
        {
            object payload = request.Capability switch
            {
                W.MediaCapabilityNames.ProviderCatalogReadV1 => await CatalogAsync(Read<W.ReadMediaProviderCatalogRequest>(request), cancellationToken),
                W.MediaCapabilityNames.JobRequestV1 => await RequestAsync(organizationId, installationId, Read<W.RequestMediaJobRequest>(request), cancellationToken),
                W.MediaCapabilityNames.JobReadV1 => await ReadAsync(organizationId, installationId, Read<W.ReadMediaJobRequest>(request), cancellationToken),
                W.MediaCapabilityNames.JobCancelV1 => await CancelAsync(organizationId, installationId, Read<W.CancelMediaJobRequest>(request), cancellationToken),
                W.MediaCapabilityNames.AssetReferenceV1 => await ReferenceAsync(organizationId, installationId, Read<W.ReferenceMediaAssetRequest>(request), cancellationToken),
                _ => throw new KeyNotFoundException("The media capability is unavailable.")
            };
            result = Success(request.RequestId, payload);
        }
        catch (JsonException exception) { result = Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (ArgumentException exception) { result = Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (UnauthorizedAccessException exception) { result = Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message); }
        catch (KeyNotFoundException exception) { result = Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message); }
        catch (InvalidOperationException exception) { result = Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        yield return result;
    }

    private async Task<IReadOnlyList<W.MediaProviderSummary>> CatalogAsync(W.ReadMediaProviderCatalogRequest request, CancellationToken token)
    {
        var operations = await db.GenAiOperationConfigurations.AsNoTracking().Include(x => x.ProviderProfile)
            .Where(x => x.IsEnabled && x.ProviderProfile != null && x.ProviderProfile.IsEnabled && x.OperationTypeKey != "")
            .ToListAsync(token);
        return operations.GroupBy(x => x.ProviderProfileId).Select(group =>
        {
            var profile = group.First().ProviderProfile!;
            var keys = group.Select(x => x.OperationTypeKey).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList();
            return new W.MediaProviderSummary(profile.Id, $"core.media-provider.{profile.ProviderType.ToString().ToLowerInvariant()}",
                "1.0.0", keys, JsonSerializer.SerializeToElement(group.ToDictionary(x => x.OperationTypeKey,
                    x => ParseSchema(x.DefaultsJson))), request.OperationTypeKeys is not { Count: > 0 } ||
                    request.OperationTypeKeys.All(key => keys.Contains(key, StringComparer.Ordinal)));
        }).Where(x => x.Eligible).ToList();
    }

    private async Task<W.MediaJob> RequestAsync(Guid organizationId, Guid installationId, W.RequestMediaJobRequest request, CancellationToken token)
    {
        await RequireWorkstreamAccessAsync(organizationId, installationId, request.WorkstreamId, token);
        if (string.IsNullOrWhiteSpace(request.OperationTypeKey) || request.Input.ValueKind != JsonValueKind.Object ||
            request.Configuration.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("A media request requires an operation type key, object input/configuration, and idempotency key.");
        var operationId = request.Configuration.TryGetProperty("operationConfigurationId", out var configuredId) &&
                          configuredId.TryGetGuid(out var id) ? id : (Guid?)null;
        var query = db.GenAiOperationConfigurations.AsNoTracking().Include(x => x.ProviderProfile).Where(x =>
            x.ProviderProfileId == request.ProviderInstallationId && x.OperationTypeKey == request.OperationTypeKey &&
            x.IsEnabled && x.ProviderProfile != null && x.ProviderProfile.IsEnabled);
        if (operationId.HasValue) query = query.Where(x => x.Id == operationId.Value);
        var configured = await query.ToListAsync(token);
        if (configured.Count != 1) throw new InvalidOperationException(configured.Count == 0
            ? "The selected provider does not offer that operation type key."
            : "Select one exact provider operation configuration.");
        var input = request.Input.Deserialize<GenAiMediaRequest>(JsonOptions)
            ?? throw new ArgumentException("The media input does not match the selected provider schema.");
        input = input with { OperationConfigurationId = configured[0].Id, IdempotencyKey = request.IdempotencyKey };
        var response = await jobs.StartAsync(organizationId, installationId, configured[0].OperationType, input, token);
        var job = await db.GenAiJobs.SingleAsync(x => x.Id == response.Id, token);
        job.WorkstreamId = request.WorkstreamId; job.WorkItemId = request.WorkItemId;
        job.ProviderProfileId = request.ProviderInstallationId; job.OperationTypeKey = request.OperationTypeKey;
        job.Revision++; job.UpdatedAt = clock.GetUtcNow(); await db.SaveChangesAsync(token);
        return await MapAsync(job, token);
    }

    private async Task<IReadOnlyList<W.MediaJob>> ReadAsync(Guid organizationId, Guid installationId, W.ReadMediaJobRequest request, CancellationToken token)
    {
        var query = db.GenAiJobs.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installationId);
        if (request.JobId.HasValue) query = query.Where(x => x.Id == request.JobId);
        if (request.WorkstreamId.HasValue)
        {
            await RequireWorkstreamAccessAsync(organizationId, installationId, request.WorkstreamId.Value, token);
            query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        }
        var records = await query.OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(token);
        var result = new List<W.MediaJob>(); foreach (var job in records) result.Add(await MapAsync(job, token)); return result;
    }

    private async Task<W.MediaJob> CancelAsync(Guid organizationId, Guid installationId, W.CancelMediaJobRequest request, CancellationToken token)
    {
        var record = await db.GenAiJobs.SingleOrDefaultAsync(x => x.Id == request.JobId && x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId, token) ?? throw new KeyNotFoundException("The media job was not found.");
        if (record.Revision != request.ExpectedRevision) throw new InvalidOperationException("The media job changed before cancellation.");
        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Cancellation requires a reason and idempotency key.");
        await jobs.CancelAsync(record.Id, organizationId, installationId, token);
        record = await db.GenAiJobs.SingleAsync(x => x.Id == record.Id, token); record.Revision++;
        record.ErrorMessage = $"Canceled: {request.Reason.Trim()}"; await db.SaveChangesAsync(token); return await MapAsync(record, token);
    }

    private async Task<W.MediaAssetReference> ReferenceAsync(Guid organizationId, Guid installationId,
        W.ReferenceMediaAssetRequest request, CancellationToken token)
    {
        await RequireWorkstreamAccessAsync(organizationId, installationId, request.WorkstreamId, token);
        if (string.IsNullOrWhiteSpace(request.PurposeTypeKey)) throw new ArgumentException("A media reference purpose type key is required.");
        var asset = await db.MediaAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.AssetId &&
            x.OrganizationId == organizationId && x.WorkstreamId == request.WorkstreamId, token)
            ?? throw new KeyNotFoundException("The project media asset was not found.");
        var secret = RandomNumberGenerator.GetBytes(32); var now = clock.GetUtcNow();
        var grant = new MediaAssetReferenceGrantRecord { Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = installationId, WorkstreamId = request.WorkstreamId, AssetId = asset.Id,
            PurposeTypeKey = request.PurposeTypeKey, SecretHash = Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant(),
            CreatedAt = now, ExpiresAt = now.AddMinutes(15) };
        db.MediaAssetReferenceGrants.Add(grant); await db.SaveChangesAsync(token);
        var opaque = $"csweet-media-ref.v1.{grant.Id:N}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        return new W.MediaAssetReference(asset.Id, request.WorkstreamId, request.PurposeTypeKey, asset.ContentType,
            asset.Sha256, asset.SizeBytes, opaque, grant.ExpiresAt);
    }

    private async Task RequireWorkstreamAccessAsync(Guid organizationId, Guid installationId, Guid workstreamId, CancellationToken token)
    {
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == installationId && x.IsActive, token) ?? throw new UnauthorizedAccessException("The agent is not active.");
        var workstream = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workstreamId && x.OrganizationId == organizationId, token)
            ?? throw new KeyNotFoundException("The Workstream was not found.");
        var teamIds = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x => x.WorkstreamId == workstreamId && x.EndsAt == null)
            .Select(x => x.TeamId).ToListAsync(token);
        var authorized = workstream.AccountableManagerOrganizationUserId == actor.Id ||
            await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x => x.WorkstreamId == workstreamId && x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null, token) ||
            await db.TeamMemberships.AsNoTracking().AnyAsync(x => teamIds.Contains(x.TeamId) && x.OrganizationUserId == actor.Id && x.EndedAt == null, token);
        if (!authorized) throw new UnauthorizedAccessException("The agent is not assigned to or supervising this Workstream.");
    }

    private async Task<W.MediaJob> MapAsync(GenAiJob job, CancellationToken token)
    {
        var assets = await db.MediaAssets.AsNoTracking().Where(x => x.GenAiJobId == job.Id).OrderBy(x => x.CreatedAt).ToListAsync(token);
        return new W.MediaJob(job.Id, job.WorkstreamId ?? Guid.Empty, job.WorkItemId, job.ProviderProfileId,
            job.OperationTypeKey, job.Status.ToString(), assets.Select(x => new W.MediaJobAsset(x.Id, x.FileName,
                x.ContentType, x.SizeBytes, x.Sha256, x.Width, x.Height, x.DurationSeconds)).ToList(),
            job.ErrorCode, job.ErrorMessage, job.Revision, job.CreatedAt, job.UpdatedAt);
    }

    private static JsonElement ParseSchema(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return JsonSerializer.SerializeToElement(new { type = "object" });
        try { return JsonDocument.Parse(value).RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { type = "object" }); }
    }
    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new JsonException("The media payload is empty.");
    private static CapabilityResult Success<T>(string requestId, T value) => new() { RequestId = requestId,
        Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)) };
    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string error) => new() {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = error,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { isError = true, code = code.ToString(), error }, JsonOptions)) };
}
