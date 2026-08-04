using CSweet.Contracts.SourceControl;

namespace CSweet.Application.SourceControl;

public interface ISourceControlApprovalService
{
    Task<SourceControlApprovalDecisionResponse> DecideAsync(
        Guid organizationId,
        Guid applicationUserId,
        Guid approvalId,
        DecideSourceControlApprovalRequest request,
        CancellationToken cancellationToken = default);
}
