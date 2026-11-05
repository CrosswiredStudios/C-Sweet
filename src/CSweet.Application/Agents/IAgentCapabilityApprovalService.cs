using CSweet.Contracts.Agents;

namespace CSweet.Application.Agents;

public interface IAgentCapabilityApprovalService
{
    Task<CapabilityAdmission> AdmitAsync(Guid organization, Guid installation, string capability,
        byte[] payload, long grantRevision, CapabilityRiskClass risk, CancellationToken token = default);
    Task DeferAsync(Guid approval, CancellationToken token = default);
    Task CompleteAsync(Guid approval, string resultsJson, bool succeeded, CancellationToken token = default);
    Task<IReadOnlyList<CapabilityApprovalResponse>> ListAsync(Guid organization, Guid user, bool administrator,
        CancellationToken token = default);
    Task<CapabilityApprovalResponse> DecideAsync(Guid organization, Guid approval, Guid user, bool administrator,
        bool approve, CancellationToken token = default);

    /// <summary>
    /// Releases an approval stuck in <c>Executing</c> because the host died between claiming and
    /// completing it. The execution is never repeated; it is recorded as failed so it can be reviewed.
    /// </summary>
    Task<CapabilityApprovalResponse> AbandonAsync(Guid organization, Guid approval, Guid user, bool administrator,
        CancellationToken token = default);
}
