using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Application.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ProjectIntakeCapabilityHandler(CSweetDbContext db, ProjectIntakeService service, ProjectWorkPolicy policy, IWorkItemMutationEngine engine) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => ProjectIntakeCapabilities.All.Contains(capability);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await HandleCoreAsync(session, request, ct);
    }
    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken ct)
    {
        try
        {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var org) || !Guid.TryParse(session.InstallationId, out var agent))
                throw new UnauthorizedAccessException("The installation does not have this capability.");
            var installed = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x =>
                x.Id == agent && x.BusinessId == org.ToString("D") && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, ct);
            if (!(JsonSerializer.Deserialize<string[]>(installed?.Grant?.RequiredCapabilitiesJson ?? "[]") ?? []).Contains(request.Capability))
                throw new UnauthorizedAccessException("The capability grant is no longer active.");
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.OrganizationId == org && x.AgentInstallationId == agent && x.IsActive && x.ArchivedAt == null, ct))
                throw new UnauthorizedAccessException("This installation is not an active employee.");
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            await policy.LockAsync(org, ct);
            object result = request.Capability switch
            {
                ProjectIntakeCapabilities.Retain => await service.RetainAsync(org, agent, Read<RetainProjectIntakeRequest>(), ct),
                ProjectIntakeCapabilities.Read => await service.ReadAsync(org, agent, Read<ProjectIntakeReference>().IntakeId, ct),
                ProjectIntakeCapabilities.List => await service.ListAsync(org, agent, ct),
                ProjectIntakeCapabilities.Discover => await service.DiscoverAsync(org, agent, Read<ProjectIntakeReference>().IntakeId, ct),
                ProjectIntakeCapabilities.Choose => await service.ChooseAsync(org, agent, Read<ChooseProjectIntakeRequest>(), ct),
                ProjectIntakeCapabilities.Start => await ((WorkItemMutationEngine)engine).StartProjectIntakeAsync(org, agent, Read<StartProjectIntakeRequest>(), ct),
                ProjectIntakeCapabilities.Manager => await service.RequestManagerAsync(org, agent, Read<ChooseProjectIntakeRequest>(), ct),
                ProjectIntakeCapabilities.Staffing => await service.StaffAsync(org, agent, Read<ProjectStaffingRequest>(), ct),
                ProjectIntakeCapabilities.AssistanceList => await service.ListAssistanceAsync(org, agent, ct),
                ProjectIntakeCapabilities.ManagerSetup => await service.ManagerSetupAsync(org, agent, Read<ProjectIntakeReference>().IntakeId, ct),
                _ => throw new ArgumentException("Unknown project intake operation.")
            };
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
            T Read<T>() => JsonSerializer.Deserialize<T>(request.Payload.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Request payload is missing.");
        }
        catch (Exception error) when (error is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or DbUpdateConcurrencyException)
        {
            var code = error is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied :
                error is KeyNotFoundException ? PlatformCapabilityErrorCode.NotFound : PlatformCapabilityErrorCode.Conflict;
            return new() { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json", Error = error.Message, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, error.Message), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        }
    }
}
