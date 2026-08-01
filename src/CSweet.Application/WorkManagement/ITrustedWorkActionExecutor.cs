using System.Text.Json;

namespace CSweet.Application.WorkManagement;

public sealed record TrustedWorkActionContext(
    Guid OrganizationId,
    Guid BoardId,
    Guid SprintExecutionId,
    Guid ItemExecutionId,
    Guid StageExecutionId,
    Guid WorkItemId,
    string ItemIdentifier,
    string Action,
    JsonElement Input);

public sealed record TrustedWorkActionResult(
    string Disposition,
    string OutcomeCode,
    string Summary,
    JsonElement Output,
    IReadOnlyList<string> Diagnostics);

public interface ITrustedWorkActionExecutor
{
    string Action { get; }
    Task<TrustedWorkActionResult> ExecuteAsync(
        TrustedWorkActionContext context,
        CancellationToken cancellationToken = default);
}
