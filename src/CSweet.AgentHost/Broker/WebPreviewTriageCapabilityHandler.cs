using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class WebPreviewTriageCapabilityHandler(CSweetDbContext db, WebPreviewTriageService triage,
    WorkManagementCapabilityHandler work, TimeProvider clock) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability is WebPreviewTriageCapabilities.ReadFinding or WebPreviewTriageCapabilities.CreateTicket;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CapabilityResult result;
        try { result = await HandleCoreAsync(session, request, cancellationToken); }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException or IOException or JsonException or DbUpdateException)
        {
            db.ChangeTracker.Clear();
            result = new() { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json",
                Error = "The finding operation is unavailable, unauthorized, or changed concurrently. Retry the same finding after checking its state." };
        }
        yield return result;
    }
    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken token)
    {
        if (!CanHandle(request.Capability) || !session.Grant.RequiredCapabilities.Contains(request.Capability) || request.Payload.Length > 128 * 1024 ||
            !Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var installationId)) throw new UnauthorizedAccessException();
        if (request.Capability == WebPreviewTriageCapabilities.ReadFinding)
        {
            var input = JsonSerializer.Deserialize<PreviewFindingRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException();
            return Success(request.RequestId, await triage.ReadAsync(organizationId, installationId, input.FindingId, token));
        }
        var ticketRequest = JsonSerializer.Deserialize<PreviewFindingTicketRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
        var finding = await triage.RequireFindingAsync(organizationId, installationId, ticketRequest.FindingId, WebPreviewTriageCapabilities.CreateTicket, token);
        if (finding.TicketId is { } existing) return Success(request.RequestId, new PreviewFindingTicketReceipt(finding.Id, finding.BoardId!.Value, existing));
        if (finding.RetainUntil <= clock.GetUtcNow()) throw new InvalidOperationException("The evidence expired.");
        var inputTicket = ticketRequest.Ticket.Deserialize<Wire.CreateWorkItemRequest>(PreviewJson.Options) ?? throw new JsonException();
        var route = await db.WebPreviewTriageRoutes.AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == finding.ProjectId && x.OrganizationId == organizationId, token);
        if ((finding.BoardId ?? route?.BoardId) is not { } board || board != inputTicket.BoardId || route is null || route.BoardId != board || inputTicket.ParentItemId != route.ParentItemId) throw new UnauthorizedAccessException("Use the assigned triage board.");
        // The normal work handler still enforces the current board-scoped Create permission and board policy.
        // Copy canonical evidence into the ticket, so destroying a VM or purging telemetry cannot lose it.
        var evidence = JsonSerializer.Deserialize<PreviewDiagnostic>(finding.EvidenceJson, PreviewJson.Options)!;
        var normalized = inputTicket with
        {
            IdempotencyKey = "preview-finding:" + finding.Id.ToString("N"),
            Description = (inputTicket.Description ?? "") + "\n\nPreview evidence (untrusted diagnostic data):\n" +
                JsonSerializer.Serialize(new { finding.Id, finding.Fingerprint, finding.PreviewId, finding.ProjectId, finding.BuildId,
                    evidence.SourceRevision, evidence.Service, evidence.Code, evidence.OccurredAt, evidence.Summary }, PreviewJson.Options)
        };
        finding.Revision++; await db.SaveChangesAsync(token); // Cross-replica serialization before any ticket effect.
        CapabilityResult? created = null;
        await foreach (var item in work.HandleAsync(session, new RequestCapability { RequestId = request.RequestId,
            Capability = WorkItemActions.Create, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(normalized, PreviewJson.Options)) }, token)) created = item;
        if (created?.Succeeded != true) { if (transaction is not null) await transaction.RollbackAsync(token); db.ChangeTracker.Clear(); return created ?? throw new InvalidOperationException(); }
        var ticket = JsonSerializer.Deserialize<Wire.WorkItem>(created.Payload.Span, PreviewJson.Options) ?? throw new JsonException();
        finding.TicketId = ticket.Id; finding.BoardId = board; finding.Revision++;
        await db.SaveChangesAsync(token); if (transaction is not null) await transaction.CommitAsync(token);
        return Success(request.RequestId, new PreviewFindingTicketReceipt(finding.Id, board, ticket.Id));
    }
    private static CapabilityResult Success<T>(string requestId, T value) => new()
    { RequestId = requestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, PreviewJson.Options)) };
}
