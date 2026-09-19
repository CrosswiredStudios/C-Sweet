namespace CSweet.Application.Compute;

public sealed record ComputeDefaults(string State, Guid? WorkstreamId, string? TemplateId, string? ErrorCode);

public interface IComputeDefaults
{
    Task<ComputeDefaults> ReadProjectAsync(Guid organizationId, Guid installationId, Guid projectId, CancellationToken token) => throw new InvalidOperationException("Project compute defaults are unavailable.");
    Task<ComputeDefaults> ReadAsync(Guid organizationId, Guid installationId, CancellationToken token);
}
