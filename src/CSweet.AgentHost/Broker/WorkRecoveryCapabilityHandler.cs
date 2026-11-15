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
            result = new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        } catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException) {
            var code = ex is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied : PlatformCapabilityErrorCode.ValidationFailed;
            result = new() { RequestId = request.RequestId, Succeeded = false, FailureCode = code.ToString(), Error = ex.Message,
                ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, ex.Message), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        }
        yield return result;
    }
}
