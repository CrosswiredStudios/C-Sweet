using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class StrategicObjectiveService : IStrategicObjectiveService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditEventWriter;
    private readonly IAgentAttentionInvalidationService? _attention;

    public StrategicObjectiveService(
        CSweetDbContext dbContext,
        IAuditEventWriter auditEventWriter,
        IAgentAttentionInvalidationService? attention = null)
    {
        _dbContext = dbContext;
        _auditEventWriter = auditEventWriter;
        _attention = attention;
    }

    public async Task<IReadOnlyList<StrategicObjectiveResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CoreStrategicObjectives
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.ToResponse())
            .ToListAsync(cancellationToken);
    }

    public async Task<StrategicObjectiveResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var obj = await _dbContext.CoreStrategicObjectives
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        return obj?.ToResponse();
    }

    public async Task<CoreActionResponse> CreateAsync(Guid organizationId, CreateStrategicObjectiveRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _dbContext.CoreOrganizations.AnyAsync(x => x.Id == organizationId, cancellationToken))
        {
            return Failure("organization_not_found", "Organization was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Failure("validation_error", "Objective title is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var obj = new StrategicObjective
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Title = request.Title.Trim(),
            Description = request.Description ?? string.Empty,
            Status = (ObjectiveStatus)request.Status,
            TargetDate = request.TargetDate,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.CoreStrategicObjectives.Add(obj);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (_attention is not null)
            await _attention.InvalidateManagersAsync(
                organizationId, "objective.created", obj.Id, cancellationToken);

        await _auditEventWriter.WriteAsync(
            "strategic_objective.created",
            "StrategicObjective",
            obj.Id,
            $"Strategic objective '{obj.Title}' created.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Strategic objective created successfully.", StrategicObjective: obj.ToResponse());
    }

    public async Task<CoreActionResponse> UpdateAsync(Guid id, UpdateStrategicObjectiveRequest request, CancellationToken cancellationToken = default)
    {
        var obj = await _dbContext.CoreStrategicObjectives
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (obj is null)
        {
            return Failure("not_found", "Strategic objective was not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
            obj.Title = request.Title.Trim();
        if (!string.IsNullOrEmpty(request.Description))
            obj.Description = request.Description;
        if (request.Status.HasValue)
            obj.Status = (ObjectiveStatus)request.Status.Value;
        if (request.TargetDate.HasValue)
            obj.TargetDate = request.TargetDate.Value;

        obj.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (_attention is not null)
            await _attention.InvalidateManagersAsync(
                obj.OrganizationId, "objective.revised", obj.Id, cancellationToken);

        await _auditEventWriter.WriteAsync(
            "strategic_objective.updated",
            "StrategicObjective",
            obj.Id,
            $"Strategic objective '{obj.Title}' updated.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Strategic objective updated successfully.", StrategicObjective: obj.ToResponse());
    }

    public async Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var obj = await _dbContext.CoreStrategicObjectives
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (obj is null)
        {
            return Failure("not_found", "Strategic objective was not found.");
        }

        var title = obj.Title;
        var organizationId = obj.OrganizationId;
        _dbContext.CoreStrategicObjectives.Remove(obj);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (_attention is not null)
            await _attention.InvalidateManagersAsync(
                organizationId, "objective.deleted", obj.Id, cancellationToken);

        await _auditEventWriter.WriteAsync(
            "strategic_objective.deleted",
            "StrategicObjective",
            obj.Id,
            $"Strategic objective '{title}' deleted.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Strategic objective deleted successfully.");
    }

    static CoreActionResponse Failure(string errorCode, string message) =>
        new CoreActionResponse(false, errorCode, message);
}
