using System.Text.Json;
using CSweet.Contracts.Communications;

namespace CSweet.Application.Communications;

public interface IUserActionService
{
    Task<SuggestedUserActionResponse> SuggestAsync(
        Guid organizationId,
        Guid originatingInstallationId,
        SuggestUserActionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IUserActionWorkflowResolver
{
    string WorkflowType { get; }

    UserActionWorkflowResolution Resolve(Guid organizationId, JsonElement parameters);
}

public sealed record UserActionWorkflowResolution(
    string NavigationUri,
    string NormalizedParametersJson);
