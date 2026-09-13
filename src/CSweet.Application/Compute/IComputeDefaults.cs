namespace CSweet.Application.Compute;

public sealed record ComputeDefaults(string State, Guid? WorkstreamId, string? TemplateId, string? ErrorCode);

public interface IComputeDefaults
{
    Task<ComputeDefaults> ReadAsync(Guid organizationId, Guid installationId, CancellationToken token);
}
