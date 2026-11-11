using CSweet.Agent.SDK;
using CSweet.Infrastructure.Setup;
using CSweet.TrustedServices;

namespace CSweet.AgentHost.Broker;

public sealed partial class GitWorkspaceCapabilityHandler
{
    internal async Task<AgentBrokerWorkspaceGitResult> GitAsync(AgentSession session, Guid workspaceId,
        long assignmentRevision, string service, bool advertise, byte[] body, CancellationToken ct)
    {
        if (service is not ("git-upload-pack" or "git-receive-pack"))
            throw new ArgumentException("Unsupported Git service.");
        var push = service == "git-receive-pack";
        var capability = push ? GitWorkspaceCapabilities.Publish : GitWorkspaceCapabilities.Prepare;
        if (!session.Grant.RequestedCapabilities.Contains(capability) ||
            !Guid.TryParse(session.BusinessId, out var organization) ||
            !Guid.TryParse(session.InstallationId, out var installation) ||
            !Guid.TryParse(session.RuntimeInstanceId, out var runtime))
            throw new UnauthorizedAccessException("The runtime has no authority for this Git operation.");
        ValidateAssignmentRevision(assignmentRevision);
        if (body.Length > AgentWorkspaceGitEndpoints.MaximumBodyBytes || advertise && body.Length != 0)
            throw new ArgumentException("Invalid workspace Git body.");

        async Task<AgentBrokerWorkspaceGitResult> ExchangeAsync()
        {
            var context = await RequireWorkspaceContextAsync(organization, installation, workspaceId,
                assignmentRevision, capability, ct);
            // Resolve every authority-bearing value through the same persisted context as file locks.
            return await gitHost.GitAsync(Operation(context, "git:" + Guid.NewGuid().ToString("N")),
                service, advertise, body, ct);
        }

        return push && !advertise
            ? await RuntimeCheckpointGate.ExecuteAsync(db, installation, organization.ToString(), runtime,
                session.Grant.Revision, ExchangeAsync, ct)
            : await ExchangeAsync();
    }
}
