using CSweet.Domain.Core;

namespace CSweet.Application.Core;

public sealed record ManagedActionExecutionResult(Guid ResourceId, long Revision, string Summary);

/// <summary>Executes an approval-bound command. Implementations must be deterministic and idempotent.</summary>
public interface IManagedActionExecutor
{
    bool CanExecute(string actionType);

    Task<ManagedActionExecutionResult> ExecuteAsync(
        ActionProposal proposal,
        OrganizationUser approvingActor,
        CancellationToken cancellationToken = default);
}
