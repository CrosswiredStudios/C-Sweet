using System.Text.Json;
using CSweet.Application.GenAi;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.GenAi;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>One fenced exchange per dispatch. The durable job owns progress; no runtime holds a transfer.</summary>
public sealed class ConnectorMediaTransferService(CSweetDbContext db, ConnectorActionApprovalService approvals,
    IConnectorMediaTransport transport, IConnectorHttpTransport reads, IMediaAssetService assets,
    IPluginSecretStore secrets, IAuditEventWriter audit)
{
    public const string ActiveKind = "host-connector-media-active";
    public const string FinishedKind = "host-connector-media-finished";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Progress(string PlanHash, string Phase, long Offset = 0, long MaximumSentBytes = 0,
        int Failures = 0, bool InFlight = false);

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var job = await db.PluginOperationalStates.Where(x => (x.Kind == ActiveKind || x.Kind == FinishedKind) && x.AvailableAt <= now)
            .OrderBy(x => x.AvailableAt).FirstOrDefaultAsync(ct);
        if (job is null) return false;
        if (!Guid.TryParse(job.ExternalKey, out var id)) throw new InvalidOperationException("Invalid media job correlation.");
        var execution = await db.ConnectorExecutions.SingleAsync(x => x.Id == id && x.OrganizationId == job.OrganizationId &&
            x.ConnectorInstallationId == job.AgentInstallationId, ct);
        if (job.Kind == FinishedKind)
        {
            await secrets.RemoveAsync(execution.ConnectorInstallationId, $"response.media-session.{execution.Id:N}", ct);
            job.AvailableAt = null; job.Revision++; job.UpdatedAt = now; await db.SaveChangesAsync(ct); return true;
        }
        if (execution.Status != "Executing")
        {
            job.Kind = FinishedKind; job.AvailableAt = null; job.Revision++;
            await db.SaveChangesAsync(ct); return true;
        }
        var observedRevision = execution.Revision;
        try { await AdvanceAsync(execution, job, ct); }
        catch (DbUpdateConcurrencyException) { return true; }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            await db.Entry(execution).ReloadAsync(ct);
            await db.Entry(job).ReloadAsync(ct);
            if (execution.Status == "Executing" && execution.Revision == observedRevision && db.Entry(job).State != EntityState.Detached)
            {
                // A failed preflight on an already-started job must not become an endless retry loop.
                execution.Status = "Indeterminate"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
                job.Kind = FinishedKind; job.AvailableAt = null; job.Revision++;
                await approvals.QueueExecutionEventAsync(execution, ct);
                await db.SaveChangesAsync(ct);
            }
        }
        return true;
    }

    public Task StartAsync(ConnectorExecution execution, CancellationToken ct) => AdvanceAsync(execution, null, ct);

    private async Task AdvanceAsync(ConnectorExecution execution, PluginOperationalState? job, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        ct = deadline.Token;
        var frozen = await approvals.RequireApprovedAsync(execution.OrganizationId, execution.RequesterInstallationId,
            execution.Id, execution.PlanHash, ct);
        using var validation = ConnectorResumableProtocol.CreateRequest(frozen, ConnectorMediaStep.Begin);
        var progress = job is null ? new Progress(execution.PlanHash, "Begin") : JsonSerializer.Deserialize<Progress>(job.PayloadJson, Json)!;
        if (progress.PlanHash != execution.PlanHash || progress.Offset < 0 || progress.MaximumSentBytes < progress.Offset ||
            progress.MaximumSentBytes > frozen.Media!.SizeBytes || progress.Phase is not ("Begin" or "Probe" or "Chunk"))
            throw new InvalidOperationException("The media checkpoint does not match this approved plan.");
        if (job is null && execution.Status != "Approved") throw new InvalidOperationException("Only an unstarted approved action can create a transfer.");
        if (job is null) await MediaAssetIntegrity.RequireProofsAsync(db, frozen.Media!, ct);
        var now = DateTimeOffset.UtcNow;
        job ??= new() { Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
            AgentInstallationId = execution.ConnectorInstallationId, Kind = ActiveKind, ExternalKey = execution.Id.ToString("D"), CreatedAt = now };
        if (db.Entry(job).State == EntityState.Detached) db.PluginOperationalStates.Add(job);
        var wasApproved = execution.Status == "Approved";
        execution.Status = "Executing"; execution.Revision++; execution.UpdatedAt = now;
        var claim = execution.Revision;
        Save(job, progress, now.AddMinutes(5));
        if (wasApproved) await approvals.QueueExecutionEventAsync(execution, ct);
        await db.SaveChangesAsync(ct); // Optimistic revisions fence competing workers and disconnects before I/O.
        var secretKey = $"response.media-session.{execution.Id:N}";
        async Task Revalidate(CancellationToken token)
        {
            var revision = await db.ConnectorExecutions.AsNoTracking().Where(x => x.Id == execution.Id && x.Status == "Executing")
                .Select(x => (long?)x.Revision).SingleOrDefaultAsync(token);
            if (revision != claim) throw new UnauthorizedAccessException("The media worker no longer owns this exchange.");
            _ = await approvals.RequireApprovedAsync(execution.OrganizationId, execution.RequesterInstallationId,
                execution.Id, execution.PlanHash, token);
        }
        var sent = false;
        try
        {
            var session = await secrets.GetAsync(frozen.ConnectorInstallationId, secretKey, ct);
            if (progress.InFlight) progress = progress with { Phase = "Probe" };
            if (session is not null && progress.Phase == "Begin") progress = progress with { Phase = "Probe" };
            if (session is null && progress.Phase != "Begin")
                throw new InvalidOperationException("The interrupted upload has no recoverable session; do not start a replacement.");
            var step = progress.Phase switch { "Begin" => ConnectorMediaStep.Begin, "Probe" => ConnectorMediaStep.Probe, _ => ConnectorMediaStep.Chunk };
            foreach (var resource in frozen.Request.ResourceChecks)
            {
                var check = resource.Declaration;
                var request = frozen.Request with { Method = "GET", Body = null, MediaAssetId = null, MediaProtocol = null,
                    ResourceChecks = [], SecretResponseFields = [], IfMatch = null, Url = ConnectorRequestMaterializer.Query(check.Endpoint,
                        check.QueryConstants.Append(new KeyValuePair<string, string>(check.ResourceQuery, resource.ResourceId))) };
                var ownership = await reads.SendAsync(frozen.ConnectorInstallationId, frozen.ConnectionId, request, Revalidate, ct);
                if (ownership.StatusCode != 200) throw new UnauthorizedAccessException("Media resource ownership cannot be verified.");
                using var document = JsonDocument.Parse(ownership.Body);
                _ = ConnectorRequestMaterializer.Hash(document.RootElement);
                var owner = ConnectorRequestMaterializer.At(document.RootElement, check.OwnerPointer);
                if (owner is not { ValueKind: JsonValueKind.String } || owner.Value.GetString() != frozen.ResourceId)
                    throw new UnauthorizedAccessException("The media resource belongs to another account.");
            }
            byte[] chunk = [];
            if (step == ConnectorMediaStep.Chunk)
            {
                if (progress.Offset >= frozen.Media!.SizeBytes) step = ConnectorMediaStep.Probe;
                else chunk = await ReadChunkAsync(frozen, progress.Offset, ct);
            }
            if (step == ConnectorMediaStep.Begin)
            {
                // Register before any vault write so disconnect cleanup knows every possible secret key.
                db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = frozen.OrganizationId,
                    AgentInstallationId = frozen.ConnectorInstallationId, Kind = "response-secret-reference",
                    ExternalKey = $"media-session:{execution.Id:N}", PayloadJson = JsonSerializer.Serialize(new
                        { key = secretKey, connectionId = frozen.ConnectionId, pointer = "/Location" }),
                    CreatedAt = now, UpdatedAt = now, Revision = 1 });
            }
            progress = progress with { InFlight = true,
                MaximumSentBytes = Math.Max(progress.MaximumSentBytes, progress.Offset + chunk.Length) };
            Save(job, progress, DateTimeOffset.UtcNow.AddMinutes(5));
            await db.SaveChangesAsync(ct);
            await Revalidate(ct); sent = true;
            var response = await transport.SendAsync(frozen, step, session, step == ConnectorMediaStep.Chunk ? progress.Offset : 0,
                chunk, progress.MaximumSentBytes, Revalidate, ct);
            await Revalidate(ct);
            if (step == ConnectorMediaStep.Begin)
            {
                if (response.StatusCode is not (200 or 201) || response.SessionLocation is null)
                    throw new InvalidOperationException("Upload initiation was not confirmed; do not initiate again.");
                _ = ConnectorResumableProtocol.ValidateSession(frozen, response.SessionLocation);
                try
                {
                    await secrets.SetAsync(frozen.ConnectorInstallationId, secretKey, response.SessionLocation, ct);
                    await Revalidate(ct);
                }
                catch { await secrets.RemoveAsync(frozen.ConnectorInstallationId, secretKey, CancellationToken.None); throw; }
                Save(job, progress with { Phase = "Probe", InFlight = false, Failures = 0 }, response.RetryAfter ?? DateTimeOffset.UtcNow);
            }
            else if (response.StatusCode == 308)
            {
                if (response.CommittedBytes is not { } offset || offset < progress.Offset || offset > progress.MaximumSentBytes)
                    throw new InvalidOperationException("The server upload checkpoint regressed or exceeded the attempted bytes.");
                var failures = offset > progress.Offset ? 0 : progress.MaximumSentBytes > 0 ? progress.Failures + 1 : 0;
                if (failures >= 8) throw new InvalidOperationException("The provider is not making progress; manual review is required.");
                Save(job, progress with { Phase = "Chunk", Offset = offset, InFlight = false, Failures = failures },
                    Later(response.RetryAfter, failures == 0 ? DateTimeOffset.UtcNow : Backoff(failures)));
            }
            else if (response.StatusCode is 200 or 201)
            {
                if (progress.MaximumSentBytes != frozen.Media!.SizeBytes)
                    throw new InvalidOperationException("Completion preceded the approved media bytes.");
                // Media responses use the same fail-closed sanitizer; do not release raw stream keys or upload URLs.
                ConnectorResponseResourceValidator.Validate(response.Body, frozen.Request);
                var sanitized = await SecretResponseSanitizer.SanitizeAsync(response.Body, frozen.Request.SecretResponseFields,
                    async (pointer, value, token) =>
                    {
                        var reference = Guid.NewGuid().ToString("N"); var key = $"response.{reference}";
                        await Revalidate(token);
                        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = frozen.OrganizationId,
                            AgentInstallationId = frozen.ConnectorInstallationId, Kind = "response-secret-reference", ExternalKey = reference,
                            PayloadJson = JsonSerializer.Serialize(new { key, connectionId = frozen.ConnectionId, pointer }),
                            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Revision = 1 });
                        await db.SaveChangesAsync(token);
                        try { await secrets.SetAsync(frozen.ConnectorInstallationId, key, value, token); await Revalidate(token); }
                        catch { await secrets.RemoveAsync(frozen.ConnectorInstallationId, key, CancellationToken.None); throw; }
                        return $"plugin-secret:{reference}";
                    }, ct);
                using var result = JsonDocument.Parse(sanitized, new JsonDocumentOptions { MaxDepth = 32 });
                var canonical = ConnectorRequestMaterializer.Canonical(result.RootElement);
                var manifestJson = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == frozen.ConnectorInstallationId)
                    .Select(x => x.PackageVersion!.ManifestJson).SingleAsync(ct);
                var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, Json)!;
                RequestSchemaValidator.Validate(result.RootElement, manifest.Provides.Single(x => x.Name == frozen.Capability).OutputSchema);
                await Revalidate(ct);
                execution.ResultJson = canonical; execution.Status = "Completed"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
                Save(job, progress with { Phase = "Completed", Offset = frozen.Media.SizeBytes, InFlight = false }, DateTimeOffset.UtcNow);
                job.Kind = FinishedKind;
                await approvals.QueueExecutionEventAsync(execution, ct);
                await db.SaveChangesAsync(ct);
                await secrets.RemoveAsync(frozen.ConnectorInstallationId, secretKey, ct);
                job.AvailableAt = null; job.Revision++; await db.SaveChangesAsync(ct);
                await audit.WriteAsync("connector.media.completed", nameof(ConnectorExecution), execution.Id,
                    "Saved a sanitized result for the approved media transfer.", cancellationToken: ct);
                return;
            }
            else if (response.StatusCode is 429 or 500 or 502 or 503 or 504)
            {
                if (progress.Failures >= 7) throw new InvalidOperationException("Upload recovery needs manual review.");
                Save(job, progress with { Phase = "Probe", InFlight = false, Failures = progress.Failures + 1 },
                    Later(response.RetryAfter, Backoff(progress.Failures + 1)));
            }
            else throw new InvalidOperationException("The upload session no longer confirms safe continuation.");
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (Exception error)
        {
            await db.Entry(execution).ReloadAsync(CancellationToken.None);
            if (execution.Status == "Completed") return; // Result is durable; the queued cleanup may retry independently.
            if (execution.Status != "Executing" || execution.Revision != claim) throw;
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            if (db.Entry(job).State == EntityState.Detached) throw;
            // Network interruption can recover only against an existing session. Initiation never repeats.
            var recoverable = sent && progress.Phase != "Begin" && progress.Failures < 7 &&
                error is HttpRequestException or IOException or OperationCanceledException;
            if (recoverable)
                Save(job, progress with { Phase = "Probe", InFlight = false, Failures = progress.Failures + 1 }, Backoff(progress.Failures + 1));
            else
            {
                execution.Status = sent || progress.MaximumSentBytes > 0 || progress.InFlight ? "Indeterminate" : "Blocked";
                execution.ResultJson = null; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
                Save(job, progress with { Phase = execution.Status, InFlight = false }, null); job.Kind = FinishedKind;
                await approvals.QueueExecutionEventAsync(execution, CancellationToken.None);
            }
            await db.SaveChangesAsync(CancellationToken.None);
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
        }
    }

    private Task<byte[]> ReadChunkAsync(FrozenConnectorPlan plan, long offset, CancellationToken ct) =>
        MediaAssetIntegrity.ReadRangeAsync(db, assets, plan.OrganizationId, plan.Media!, offset,
            ConnectorResumableProtocol.ChunkSize, ct);

    private static DateTimeOffset Backoff(int failures) => DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 5 * Math.Pow(2, failures)));
    private static DateTimeOffset Later(DateTimeOffset? provider, DateTimeOffset local) => provider > local ? provider.Value : local;
    private static void Save(PluginOperationalState job, Progress progress, DateTimeOffset? available)
    {
        job.PayloadJson = JsonSerializer.Serialize(progress, Json); job.AvailableAt = available;
        job.UpdatedAt = DateTimeOffset.UtcNow; job.Revision++;
    }
}
