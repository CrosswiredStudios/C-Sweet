using System.Text.Json;
using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PacketResponse = CSweet.Contracts.Agents.DelegationResponse;

namespace CSweet.Api.Core;

/// <summary>Packet metadata over the canonical tracked delegation workflow.</summary>
public static class DelegationEndpoints
{
    public static IEndpointRouteBuilder MapDelegationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/delegations")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser());
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { error = "Refresh the delegation and try again." }); }
            catch (InvalidOperationException error) { return Results.Conflict(new { error = error.Message }); }
        });
        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{delegationId:guid}", async (Guid organizationId, Guid delegationId, HttpContext http,
            CSweetDbContext db, CancellationToken token) =>
        {
            var actor = await ActorAsync(organizationId, http, db, token);
            var row = await db.Delegations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == delegationId && x.BusinessId == organizationId.ToString("D"), token)
                ?? throw new KeyNotFoundException();
            if (!await CanReadAsync(db, actor, row, token)) return Results.Forbid();
            return Results.Ok((await MapAsync(db, row, token)) with { CanRespond = await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, row.ToInstallationId, false, token) });
        });
        foreach (var disposition in new[] { "Accepted", "Declined", "Completed" })
        {
            var action = disposition;
            var route = action switch { "Accepted" => "accept", "Declined" => "decline", _ => "complete" };
            group.MapPost("/{delegationId:guid}/" + route, async (Guid organizationId, Guid delegationId,
                DecideDelegationRequest request, HttpContext http, CSweetDbContext db, ITrackedDelegationService service, CancellationToken token) =>
            {
                var actor = await ActorAsync(organizationId, http, db, token);
                var row = await db.Delegations.SingleOrDefaultAsync(x => x.Id == delegationId && x.BusinessId == organizationId.ToString("D"), token)
                    ?? throw new KeyNotFoundException();
                if (!await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, row.ToInstallationId, false, token)) return Results.Forbid();
                if (row.TrackedDelegationId is not { } tracked) return Results.Conflict(new { error = "This legacy packet has no tracked workflow. Create a new delegation." });
                var recipient = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == organizationId && x.AgentInstallationId == row.ToInstallationId && x.IsActive && x.ArchivedAt == null, token);
                await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
                await service.RespondAsync(organizationId, recipient.Id, new DelegationResponseRequest(tracked, request.Revision, action, request.Summary, request.IdempotencyKey), token);
                Evidence(db, organizationId, row.Id, actor, action);
                await db.SaveChangesAsync(token);
                if (transaction is not null) await transaction.CommitAsync(token);
                return Results.Ok((await MapAsync(db, row, token)) with { CanRespond = await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, row.ToInstallationId, false, token) });
            });
        }
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(Guid organizationId, CreateDelegationRequest request,
        HttpContext http, CSweetDbContext db, ITrackedDelegationService service, CancellationToken token)
    {
        var actor = await ActorAsync(organizationId, http, db, token);
        if (!string.IsNullOrWhiteSpace(request.BusinessId) && (!Guid.TryParse(request.BusinessId, out var business) || business != organizationId))
            throw new ArgumentException("The business scope does not match.");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 || string.IsNullOrWhiteSpace(request.Scope) || request.Scope.Length > 4000 || request.ReturnContract?.Length > 4000)
            throw new ArgumentException("A bounded title, scope and return contract are required.");
        if (request.SourceConversationId == Guid.Empty || request.SourceMessageId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("The source conversation, source message and stable idempotency key are required.");
        if (!await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, request.FromInstallationId, false, token)) return Results.Forbid();
        var from = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.AgentInstallationId == request.FromInstallationId && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new ArgumentException("The delegating employee is unavailable.");
        var to = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.AgentInstallationId == request.ToInstallationId && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new ArgumentException("The recipient employee is unavailable.");
        // A human may forward their own published instruction through an agent they manage.
        if (!await db.CoreConversationMessages.AnyAsync(x => x.Id == request.SourceMessageId && x.ConversationId == request.SourceConversationId && x.SenderOrganizationUserId == actor.Id, token) ||
            !await db.ConversationParticipants.AnyAsync(x => x.ConversationId == request.SourceConversationId && x.OrganizationUserId == actor.Id && x.LeftAt == null, token))
            throw new UnauthorizedAccessException();
        var artifacts = (request.InputArtifactIds ?? []).Distinct().Order().ToArray();
        if (artifacts.Length > 64 || await db.CoreArtifacts.CountAsync(x => x.OrganizationId == organizationId && artifacts.Contains(x.Id), token) != artifacts.Length)
            throw new ArgumentException("Input artifacts must belong to this business and be bounded to 64.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var outcome = request.ReturnContract ?? request.Title;
        if (artifacts.Length > 0) outcome += "\nInput artifact IDs: " + JsonSerializer.Serialize(artifacts);
        if (outcome.Length > 4000) throw new ArgumentException("The return contract and artifact references exceed the delegation budget.");
        var tracked = await service.CreateAsync(organizationId, from.Id,
            new DelegateWithTrackingRequest(to.Id, request.Scope, outcome,
                request.SourceConversationId, request.SourceMessageId, request.IdempotencyKey), token);
        var packet = await db.Delegations.SingleOrDefaultAsync(x => x.Id == tracked.Id, token);
        if (packet is null)
        {
            packet = new Delegation { Id = tracked.Id, TrackedDelegationId = tracked.Id, BusinessId = organizationId.ToString("D"),
                FromInstallationId = request.FromInstallationId, ToInstallationId = request.ToInstallationId,
                Title = request.Title, Scope = request.Scope, ReturnContract = request.ReturnContract,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            packet.SetInputArtifactIds(artifacts);
            db.Delegations.Add(packet);
            Evidence(db, organizationId, packet.Id, actor, "Created");
            await db.SaveChangesAsync(token);
        }
        else if (packet.Title != request.Title || packet.Scope != request.Scope || packet.ReturnContract != request.ReturnContract || !packet.InputArtifactIds.SequenceEqual(artifacts))
            throw new DbUpdateConcurrencyException("The idempotency key was reused with different packet metadata.");
        if (transaction is not null) await transaction.CommitAsync(token);
        return Results.Created($"/api/core/organizations/{organizationId:D}/delegations/{packet.Id:D}", await MapAsync(db, packet, token));
    }

    private static async Task<IResult> ListAsync(Guid organizationId, string? status, int? offset, int? take,
        HttpContext http, CSweetDbContext db, CancellationToken token)
    {
        var actor = await ActorAsync(organizationId, http, db, token);
        if (offset is < 0) throw new ArgumentException("Offset cannot be negative.");
        // Authorize in SQL before pagination, so unrelated packets cannot hide the reader's work.
        var managed = db.CoreOrganizationUsers.Where(x => x.OrganizationId == organizationId && x.IsActive && x.ArchivedAt == null && x.AgentInstallationId != null &&
            (actor.PermissionLevel == OrganizationPermissionLevel.Owner || actor.PermissionLevel >= OrganizationPermissionLevel.Manager && x.ReportsToOrganizationUserId == actor.Id))
            .Select(x => x.AgentInstallationId!.Value);
        var rows = await db.Delegations.AsNoTracking().Where(x => x.BusinessId == organizationId.ToString("D") && (managed.Contains(x.FromInstallationId) || managed.Contains(x.ToInstallationId)))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset ?? 0).Take(Math.Clamp(take ?? 25, 1, 100)).ToListAsync(token);
        var result = new List<PacketResponse>();
        foreach (var row in rows)
        {
            var mapped = await MapAsync(db, row, token);
            if (string.IsNullOrWhiteSpace(status) || mapped.Status.Equals(status, StringComparison.OrdinalIgnoreCase)) result.Add(mapped with { CanRespond = await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, row.ToInstallationId, false, token) });
        }
        return Results.Ok(result);
    }

    private static Task<bool> CanReadAsync(CSweetDbContext db, OrganizationUser actor, Delegation row, CancellationToken token) =>
        CanReadCoreAsync(db, actor, row, token);
    private static async Task<bool> CanReadCoreAsync(CSweetDbContext db, OrganizationUser actor, Delegation row, CancellationToken token) =>
        await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, row.FromInstallationId, false, token) ||
        await AgentGovernanceAccess.CanManageAsync(db, actor.ApplicationUserId!.Value, row.ToInstallationId, false, token);

    private static async Task<PacketResponse> MapAsync(CSweetDbContext db, Delegation row, CancellationToken token)
    {
        var tracked = row.TrackedDelegationId is { } id ? await db.TrackedDelegations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token) : null;
        var status = tracked?.Status switch { "AwaitingAcknowledgement" => "Pending", "Active" => "Accepted", null => "Legacy", var current => current };
        return new(row.Id, row.FromInstallationId, row.ToInstallationId, row.Title, row.Scope, status, row.CreatedAt,
            tracked?.Status == "Completed" ? tracked.UpdatedAt : null)
        { InputArtifactIds = row.InputArtifactIds, ReturnContract = row.ReturnContract, TrackedDelegationId = tracked?.Id,
            WorkItemId = tracked?.WorkItemId, Revision = tracked?.Revision ?? 0 };
    }
    private static void Evidence(CSweetDbContext db, Guid organization, Guid id, OrganizationUser actor, string state) =>
        db.QueueAudit(new AuditEventWriteRequest("core.delegation." + state.ToLowerInvariant(), OrganizationId: organization,
            EntityType: nameof(Delegation), EntityId: id, Summary: "Tracked delegation packet recorded as " + state,
            Actor: new AuditActor("Human", ApplicationUserId: actor.ApplicationUserId, OrganizationUserId: actor.Id)));
    private static async Task<OrganizationUser> ActorAsync(Guid organization, HttpContext http, CSweetDbContext db, CancellationToken token)
    {
        var user = http.User.GetApplicationUserId() ?? throw new UnauthorizedAccessException();
        return await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.ApplicationUserId == user && x.IsActive && x.ArchivedAt == null, token)
            ?? throw new UnauthorizedAccessException();
    }
}
