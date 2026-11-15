using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class WorkIssueCapabilityHandler(CSweetDbContext db, WorkIssueService issues) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => WorkIssueCapabilities.All.Contains(capability);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        CapabilityResult result;
        try
        {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) || !Guid.TryParse(session.BusinessId, out var org) ||
                !Guid.TryParse(session.InstallationId, out var installation)) throw new UnauthorizedAccessException();
            var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == org && x.AgentInstallationId == installation && x.IsActive, ct)
                ?? throw new UnauthorizedAccessException();
            object response = request.Capability switch {
                WorkIssueCapabilities.Read => await issues.ReadAsync(org, actor.Id, Read<ReadWorkIssuesRequest>(request), ct),
                WorkIssueCapabilities.Reconcile => await issues.ReconcileAsync(org, actor.Id, Read<ReconcileWorkIssuesRequest>(request), ct),
                WorkIssueCapabilities.Commission => await issues.CommissionAsync(org, actor.Id, installation, Read<CommissionWorkIssuesRequest>(request), ct),
                WorkIssueCapabilities.Propose => await issues.ProposeAsync(org, actor.Id, installation, Read<ReconcileWorkIssuesRequest>(request), ct),
                WorkIssueCapabilities.DecideProposal => await issues.DecideProposalAsync(org, actor.Id, Read<DecideWorkIssueProposalRequest>(request), ct),
                _ => throw new UnauthorizedAccessException()
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(response, Json)) };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException or DbUpdateException)
        {
            var code = ex is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied :
                ex is InvalidOperationException or DbUpdateException ? PlatformCapabilityErrorCode.Conflict : PlatformCapabilityErrorCode.ValidationFailed;
            result = new() { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json", Error = ex.Message,
                FailureCode = code.ToString(), Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, ex.Message), Json)) };
        }
        yield return result;
    }
    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, Json) ?? throw new ArgumentException("The issue request is empty.");
}
