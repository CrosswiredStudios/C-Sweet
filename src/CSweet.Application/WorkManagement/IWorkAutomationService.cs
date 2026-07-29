using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkAutomationService
{
    Task<WorkAutomationDirectoryResponse> ListAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default);

    Task<WorkAutomationRuleResponse> CreateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CreateWorkAutomationRuleRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkAutomationRuleResponse?> UpdateAsync(
        Guid organizationId,
        Guid boardId,
        Guid ruleId,
        Guid applicationUserId,
        UpdateWorkAutomationRuleRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid organizationId,
        Guid boardId,
        Guid ruleId,
        Guid applicationUserId,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface IWorkAutomationDispatcher
{
    Task<int> DispatchBatchAsync(
        int batchSize = 50,
        CancellationToken cancellationToken = default);
}
