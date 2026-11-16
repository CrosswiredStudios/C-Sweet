using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.AgentHost.Broker;

public sealed class WorkRecoveryCapabilityHandler(CSweetDbContext db, WorkRecoveryService recovery) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability == WorkRecoveryCapabilities.Read;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        CapabilityResult result;
        try {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) || !Guid.TryParse(session.BusinessId, out var org) || !Guid.TryParse(session.InstallationId, out var installation)) throw new UnauthorizedAccessException();
            var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(u => u.OrganizationId == org && u.AgentInstallationId == installation && u.IsActive && u.ArchivedAt == null, ct) ?? throw new UnauthorizedAccessException();
            var input = JsonSerializer.Deserialize<ReadWorkRecoveryRequest>(request.Payload.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Select assigned work.");
            var facts = await recovery.ReadAsync(org, new PersonalTodoActor(actor.Id, installation), input, ct);
            // Schema/implementation changes are evidence for a new manager assessment,
            // not a retry grant. Unchanged notifications retain the same recovery basis.
            var tool = facts.FailedCapability is { } capability
                ? new McpToolCatalog([]).List(new HashSet<string> { capability }).SingleOrDefault() : null;
            facts = facts with {
                Basis = MutationContent.Digest(new { facts.Basis,
                    schema = tool?.InputSchema.GetRawText(), implementation = typeof(WorkManagementCapabilityHandler).Module.ModuleVersionId }),
                Explanation = facts.Explanation + " Recorded failure: " + facts.FailureCode +
                    (facts.FailedCapability is null ? "." : " in " + facts.FailedCapability + ".") +
                    " This assessment includes the current broker implementation and input contract. A changed contract permits review, not automatic execution."
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        } catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException) {
            var code = ex is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied : PlatformCapabilityErrorCode.ValidationFailed;
            result = new() { RequestId = request.RequestId, Succeeded = false, FailureCode = code.ToString(), Error = ex.Message,
                ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, ex.Message), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        }
        yield return result;
    }
}
