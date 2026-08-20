using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentUpdateService : IAgentUpdateService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAgentImportPreviewService _previewService;
    private readonly ILogger<AgentUpdateService> _logger;

    public AgentUpdateService(
        CSweetDbContext dbContext,
        IAgentImportPreviewService previewService,
        ILogger<AgentUpdateService> logger)
    {
        _dbContext = dbContext;
        _previewService = previewService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AgentUpdateAvailabilityResponse>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var installations = await _dbContext.AgentInstallations
            .AsNoTracking()
            .Include(x => x.PackageVersion)!
                .ThenInclude(x => x!.PackageSource)
            .OrderBy(x => x.PackageVersion!.AgentName)
            .ThenBy(x => x.BusinessId)
            .ToListAsync(cancellationToken);
        var previews = new Dictionary<Guid, AgentImportPreviewResponse>();
        var errors = new Dictionary<Guid, string>();

        foreach (var source in installations
                     .Select(x => x.PackageVersion!.PackageSource!)
                     .DistinctBy(x => x.Id))
        {
            try
            {
                previews[source.Id] = await _previewService.PreviewAsync(
                    new PreviewAgentImportRequest(source.RepositoryUrl),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not check agent source {RepositoryUrl} for updates.", source.RepositoryUrl);
                errors[source.Id] = exception.Message;
            }
        }

        var checkedAt = DateTimeOffset.UtcNow;
        return installations.Select(installation =>
        {
            var current = installation.PackageVersion!;
            var sourceId = current.PackageSourceId;
            if (!previews.TryGetValue(sourceId, out var preview))
            {
                return ToResponse(installation, checkedAt, null, errors.GetValueOrDefault(sourceId));
            }

            var updateAvailable = string.Equals(preview.AgentId, current.AgentId, StringComparison.Ordinal) &&
                SemanticVersionComparer.Compare(preview.AgentVersion, current.Version) > 0;
            return ToResponse(installation, checkedAt, updateAvailable ? preview : null, null);
        }).ToList();
    }

    public async Task<IReadOnlyList<AgentDefinitionUpdateAvailabilityResponse>> CheckDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await _dbContext.AgentDefinitions
            .AsNoTracking()
            .Include(x => x.PackageVersion)!
                .ThenInclude(x => x!.PackageSource)
            .OrderBy(x => x.PackageVersion!.AgentName)
            .ToListAsync(cancellationToken);
        var previews = new Dictionary<Guid, AgentImportPreviewResponse>();
        var errors = new Dictionary<Guid, string>();

        foreach (var source in definitions
                     .Select(x => x.PackageVersion!.PackageSource!)
                     .DistinctBy(x => x.Id))
        {
            try
            {
                previews[source.Id] = await _previewService.PreviewAsync(
                    new PreviewAgentImportRequest(source.RepositoryUrl),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not check agent definition source {RepositoryUrl} for updates.", source.RepositoryUrl);
                errors[source.Id] = exception.Message;
            }
        }

        var checkedAt = DateTimeOffset.UtcNow;
        return definitions.Select(definition =>
        {
            var current = definition.PackageVersion!;
            var sourceId = current.PackageSourceId;
            if (!previews.TryGetValue(sourceId, out var preview))
            {
                return ToDefinitionResponse(definition, checkedAt, null, errors.GetValueOrDefault(sourceId));
            }

            var updateAvailable = string.Equals(preview.AgentId, current.AgentId, StringComparison.Ordinal) &&
                SemanticVersionComparer.Compare(preview.AgentVersion, current.Version) > 0;
            return ToDefinitionResponse(definition, checkedAt, updateAvailable ? preview : null, null);
        }).ToList();
    }

    private static AgentUpdateAvailabilityResponse ToResponse(
        AgentInstallation installation,
        DateTimeOffset checkedAt,
        AgentImportPreviewResponse? update,
        string? error)
    {
        var current = installation.PackageVersion!;
        return new AgentUpdateAvailabilityResponse(
            installation.Id,
            current.AgentId,
            current.AgentName,
            installation.BusinessId,
            current.Version,
            current.CommitSha,
            update is not null,
            update?.ImportId,
            update?.AgentVersion,
            update?.CommitSha,
            checkedAt,
            error);
    }

    private static AgentDefinitionUpdateAvailabilityResponse ToDefinitionResponse(
        AgentDefinition definition,
        DateTimeOffset checkedAt,
        AgentImportPreviewResponse? update,
        string? error)
    {
        var current = definition.PackageVersion!;
        return new AgentDefinitionUpdateAvailabilityResponse(
            definition.Id,
            current.AgentId,
            current.AgentName,
            current.Version,
            current.CommitSha,
            update is not null,
            update?.ImportId,
            update?.AgentVersion,
            update?.CommitSha,
            checkedAt,
            error);
    }
}
