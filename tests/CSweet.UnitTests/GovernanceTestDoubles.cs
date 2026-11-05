using CSweet.Application.Agents;
using CSweet.Application.Security;
using CSweet.Contracts.Agents;

namespace CSweet.UnitTests;

/// <summary>
/// Governance collaborators are required constructor dependencies, so a test that is not about
/// governance states which behaviour it assumes instead of inheriting a silent default.
/// </summary>
internal sealed class PermissivePostureAuthorization : IAgentPostureAuthorization
{
    public ValueTask<bool> CanChangeAsync(Guid installationId, Guid applicationUserId, bool administrator,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}

/// <summary>Auto posture with an active installation: the most permissive legitimate state.</summary>
internal sealed class AutoPostureReader : IAgentPostureReader
{
    public ValueTask<AgentPostureAssignment> GetAsync(Guid installationId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentPostureAssignment(installationId, AgentPosture.Auto));

    public ValueTask<bool> IsActiveAsync(Guid installationId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);
}

/// <summary>
/// Fails loudly. A test reaching approval admission under Auto posture is exercising a path it did not
/// intend to, so the failure names the assumption rather than returning a fabricated approval.
/// </summary>
internal sealed class UnusedApprovalService : IAgentCapabilityApprovalService
{
    public Task<CapabilityAdmission> AdmitAsync(Guid organization, Guid installation, string capability,
        byte[] payload, long grantRevision, CapabilityRiskClass risk, CancellationToken token = default)
        => throw new NotSupportedException("This test does not expect approval admission.");

    public Task DeferAsync(Guid approval, CancellationToken token = default)
        => throw new NotSupportedException("This test does not expect approval deferral.");

    public Task CompleteAsync(Guid approval, string resultsJson, bool succeeded, CancellationToken token = default)
        => throw new NotSupportedException("This test does not expect approval completion.");

    public Task<IReadOnlyList<CapabilityApprovalResponse>> ListAsync(Guid organization, Guid user, bool administrator,
        CancellationToken token = default) => Task.FromResult<IReadOnlyList<CapabilityApprovalResponse>>([]);

    public Task<CapabilityApprovalResponse> DecideAsync(Guid organization, Guid approval, Guid user, bool administrator,
        bool approve, CancellationToken token = default)
        => throw new NotSupportedException("This test does not expect approval decisions.");

    public Task<CapabilityApprovalResponse> AbandonAsync(Guid organization, Guid approval, Guid user, bool administrator,
        CancellationToken token = default)
        => throw new NotSupportedException("This test does not expect approval abandonment.");
}

/// <summary>Admits every destructive/outward reservation so the test isolates the path it targets.</summary>
internal sealed class PermissiveTemporalPolicy : IAgentPolicyService
{
    public Task RecordAsync(AgentPolicyEvent policyEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<int> CountWithinAsync(AgentPolicyWindow window, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<AgentPolicyDecision> EvaluateAsync(AgentPolicyThreshold threshold, AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default) => Task.FromResult(AgentPolicyDecision.Permit);

    public Task<AgentPolicyEvaluation> CheckAsync(AgentPolicyThreshold threshold, AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This test does not expect a policy evaluation projection.");

    public Task<AgentPolicyDecision> TryAdmitAsync(AgentPolicyThreshold threshold, AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default) => Task.FromResult(AgentPolicyDecision.Permit);

    public Task<AgentPolicyEventPage> BrowseAsync(AgentPolicyEventQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult(new AgentPolicyEventPage(query.BusinessId, query.InstallationId, 0, 0, []));
}
