using System.Text.Json;
using CSweet.Contracts.Plugins;

namespace CSweet.Application.Setup;

public sealed record ManagedActionPolicyInput(
    Guid OrganizationId,
    Guid InstallationId,
    string ChannelId,
    string ActionType,
    JsonElement Payload,
    string PayloadHash,
    string IdempotencyKey);

public sealed record ManagedActionPolicyDecision(bool Authorized, Guid? PolicyId = null, int? PolicyRevision = null,
    string? Reason = null);

public interface IPluginStandingPolicyService
{
    Task<PluginStandingPolicyResponse?> GetAsync(Guid organizationId, Guid installationId,
        CancellationToken cancellationToken = default);
    Task<PluginStandingPolicyResponse> ApproveAsync(Guid organizationId, Guid applicationUserId,
        Guid installationId, ApprovePluginStandingPolicyRequest request,
        CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid organizationId, Guid applicationUserId, Guid installationId,
        CancellationToken cancellationToken = default);
    Task<ManagedActionPolicyDecision> EvaluateAsync(ManagedActionPolicyInput input,
        CancellationToken cancellationToken = default);
}
