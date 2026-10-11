namespace CSweet.Application.Compute;

public sealed record ComputeDefaults(string State, Guid? WorkstreamId, string? TemplateId, string? ErrorCode)
{
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyList<string> CachedImages { get; init; } = [];
    public string LinkReachability { get; init; } = "unavailable";
    public int MaximumLifetimeSeconds { get; init; }
}

public interface IComputeDefaults
{
    Task<ComputeDefaults> ReadProjectAsync(Guid organizationId, Guid installationId, Guid projectId, CancellationToken token) => throw new InvalidOperationException("Project compute defaults are unavailable.");
    Task<ComputeDefaults> ReadAsync(Guid organizationId, Guid installationId, CancellationToken token);
}
