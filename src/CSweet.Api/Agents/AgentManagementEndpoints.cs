using System.Text.Json;
using System.Security.Claims;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using CSweet.Domain.Core;

namespace CSweet.Api.Agents;

public static class AgentManagementEndpoints
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CapabilityTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProviderRegistrationGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProviderRegistrationRetryDelay = TimeSpan.FromMilliseconds(100);

    public static IServiceCollection AddAgentManagement(this IServiceCollection services)
    {
        return services;
    }

    public static IEndpointRouteBuilder MapAgentManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents");

        group.MapPost("/imports/preview", async (
            PreviewAgentImportRequest request,
            IAgentImportPreviewService importPreviewService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var preview = await importPreviewService.PreviewAsync(request, cancellationToken);
                return Results.Ok(preview);
            }
            catch (AgentImportPreviewException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).RequireAuthorization("PluginAdministration")
          .RequireRateLimiting(AgentRateLimiting.ImportPolicy);

        group.MapPost("/imports/{importId:guid}/install", async (
            Guid importId,
            InstallAgentRequest request,
            IAgentDefinitionService definitionService,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await definitionService.ImportAsync(importId, request, cancellationToken)); }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        })
            .RequireAuthorization("PluginAdministration")
            .RequireRateLimiting(AgentRateLimiting.BuildPolicy);

        group.MapGet("/definitions", async (IAgentDefinitionService definitions, CancellationToken cancellationToken) =>
            Results.Ok(await definitions.ListAsync(cancellationToken)));

        group.MapGet("/definitions/{definitionId:guid}", async (
            Guid definitionId, IAgentDefinitionService definitions, CancellationToken cancellationToken) =>
            await definitions.GetAsync(definitionId, cancellationToken) is { } definition
                ? Results.Ok(definition) : Results.NotFound());

        group.MapPost("/definitions/{definitionId:guid}/retry-build", async (
            Guid definitionId,
            IAgentDefinitionService definitions,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await definitions.RetryBuildAsync(definitionId, cancellationToken)); }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        })
            .RequireAuthorization("PluginAdministration")
            .RequireRateLimiting(AgentRateLimiting.BuildPolicy);

        group.MapGet("/definitions/{definitionId:guid}/configuration", async (
            Guid definitionId, IAgentConfigurationService configurations, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await configurations.GetDefinitionAsync(definitionId, cancellationToken)); }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization("PluginAdministration");

        group.MapPut("/definitions/{definitionId:guid}/configuration", async (
            Guid definitionId, PutAgentDefinitionConfigurationRequest request,
            IAgentConfigurationService configurations, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await configurations.SaveDefinitionAsync(definitionId, request, cancellationToken)); }
            catch (AgentConfigurationConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message, currentRevision = exception.CurrentRevision });
            }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        }).RequireAuthorization("PluginAdministration");

        group.MapGet("/installations", async (
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            Results.Ok(await installationService.ListAsync(cancellationToken)));

        group.MapPost("/installations/check-updates", async (
            IAgentUpdateService updateService,
            CancellationToken cancellationToken) =>
            Results.Ok(await updateService.CheckAsync(cancellationToken)))
            .RequireRateLimiting(AgentRateLimiting.ImportPolicy);

        group.MapGet("/installations/{installationId:guid}", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
        {
            var installation = await installationService.GetAsync(installationId, cancellationToken);
            return installation is null ? Results.NotFound() : Results.Ok(installation);
        });

        group.MapPut("/installations/{installationId:guid}/schedule", async (
            Guid installationId,
            UpdateAgentScheduleRequest request,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.UpdateScheduleAsync(installationId, request, cancellationToken)));

        group.MapPost("/installations/{installationId:guid}/update", async (
            Guid installationId,
            UpdateAgentInstallationRequest request,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.UpdateAsync(installationId, request, cancellationToken)))
            .RequireRateLimiting(AgentRateLimiting.BuildPolicy);

        group.MapPost("/installations/{installationId:guid}/run-now", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.RunNowAsync(installationId, cancellationToken)))
            .RequireRateLimiting(AgentRateLimiting.RunPolicy);

        group.MapPost("/installations/{installationId:guid}/retry-build", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.RetryBuildAsync(installationId, cancellationToken)))
            .RequireRateLimiting(AgentRateLimiting.BuildPolicy);

        group.MapPost("/installations/{installationId:guid}/retry-startup", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.RetryStartupAsync(installationId, cancellationToken)))
            .RequireRateLimiting(AgentRateLimiting.RunPolicy);

        group.MapPost("/installations/{installationId:guid}/disable", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.DisableAsync(installationId, cancellationToken)));

        group.MapPost("/installations/{installationId:guid}/enable", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.EnableAsync(installationId, cancellationToken)));

        group.MapDelete("/installations/{installationId:guid}", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await installationService.RemoveAsync(installationId, cancellationToken));
            }
            catch (AgentInstallationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapGet("/installations/{installationId:guid}/runs", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await installationService.ListRunsAsync(installationId, cancellationToken)); }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapGet("/installations/{installationId:guid}/build-log", async (
            Guid installationId,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
        {
            var log = await installationService.GetBuildLogAsync(installationId, cancellationToken);
            return log is null ? Results.NotFound() : Results.Ok(log);
        });

        group.MapPost("/installations/{installationId:guid}/runtime/ensure", async (
            Guid installationId,
            IAgentInteractiveRuntimeService interactiveRuntime,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var readiness = await interactiveRuntime.EnsureReadyAsync(installationId, cancellationToken);
                return readiness.IsReady
                    ? Results.Ok(readiness)
                    : Results.Accepted($"/api/agents/installations/{installationId}/runtime/status", readiness);
            }
            catch (AgentInstallationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapGet("/installations/{installationId:guid}/runtime/status", async (
            Guid installationId,
            IAgentInteractiveRuntimeService interactiveRuntime,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await interactiveRuntime.GetStatusAsync(installationId, cancellationToken));
            }
            catch (AgentInstallationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // Temporary compatibility facade: reads/writes the control-plane store only. It never starts a VM.
        group.MapGet("/installations/{installationId:guid}/configuration", async (
            Guid installationId, IAgentInstallationConfigurationService configurations, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await configurations.GetAsync(installationId, cancellationToken);
                return snapshot is null ? Results.NotFound() : Results.Ok(new AgentConfigurationSchemaResponse(
                    installationId.ToString("D"), string.Empty, snapshot.SchemaVersion, [], snapshot.Settings));
            }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        group.MapPost("/installations/{installationId:guid}/configuration", async (
            Guid installationId, UpdateAgentConfigurationRequest request,
            IAgentInstallationConfigurationService configurations, CancellationToken cancellationToken) =>
        {
            try
            {
                var saved = await configurations.SaveAsync(installationId, request.SchemaVersion ?? "1", request.Settings, cancellationToken);
                return Results.Ok(new AgentConfigurationUpdateResponse(true, null, saved.Settings));
            }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });

        var employeeConfiguration = endpoints.MapGroup(
            "/api/core/organizations/{organizationId:guid}/users/{employeeId:guid}/agent-configuration");
        employeeConfiguration.MapGet("/overrides", async (Guid organizationId, Guid employeeId, ClaimsPrincipal principal,
            CSweetDbContext db, IAgentConfigurationService configurations, CancellationToken cancellationToken) =>
        {
            if (!await CanManageEmployeeConfigurationAsync(principal, organizationId, employeeId, db, cancellationToken))
                return Results.Forbid();
            try { return Results.Ok(await configurations.GetEmployeeAsync(organizationId, employeeId, cancellationToken)); }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        employeeConfiguration.MapPut("/overrides", async (Guid organizationId, Guid employeeId,
            PutAgentConfigurationOverridesRequest request, ClaimsPrincipal principal, CSweetDbContext db,
            IAgentConfigurationService configurations, CancellationToken cancellationToken) =>
        {
            if (!await CanManageEmployeeConfigurationAsync(principal, organizationId, employeeId, db, cancellationToken))
                return Results.Forbid();
            try { return Results.Ok(await configurations.SaveEmployeeOverridesAsync(organizationId, employeeId, request, cancellationToken)); }
            catch (AgentConfigurationConflictException exception)
            { return Results.Conflict(new { error = exception.Message, currentRevision = exception.CurrentRevision }); }
            catch (AgentInstallationException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        employeeConfiguration.MapDelete("/overrides/{key}", async (Guid organizationId, Guid employeeId, string key,
            long expectedRevision,
            ClaimsPrincipal principal, CSweetDbContext db, IAgentConfigurationService configurations,
            CancellationToken cancellationToken) =>
        {
            if (!await CanManageEmployeeConfigurationAsync(principal, organizationId, employeeId, db, cancellationToken))
                return Results.Forbid();
            try { return Results.Ok(await configurations.RestoreEmployeeOverrideAsync(organizationId, employeeId, key, expectedRevision, cancellationToken)); }
            catch (AgentConfigurationConflictException exception)
            { return Results.Conflict(new { error = exception.Message, currentRevision = exception.CurrentRevision }); }
        });
        employeeConfiguration.MapDelete("/overrides", async (Guid organizationId, Guid employeeId,
            long expectedRevision,
            ClaimsPrincipal principal, CSweetDbContext db, IAgentConfigurationService configurations,
            CancellationToken cancellationToken) =>
        {
            if (!await CanManageEmployeeConfigurationAsync(principal, organizationId, employeeId, db, cancellationToken))
                return Results.Forbid();
            try { return Results.Ok(await configurations.RestoreAllEmployeeOverridesAsync(organizationId, employeeId, expectedRevision, cancellationToken)); }
            catch (AgentConfigurationConflictException exception)
            { return Results.Conflict(new { error = exception.Message, currentRevision = exception.CurrentRevision }); }
        });

        return endpoints;
    }

    private static async Task<bool> CanManageEmployeeConfigurationAsync(
        ClaimsPrincipal principal, Guid organizationId, Guid employeeId, CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        if (principal.IsInRole(CSweet.Infrastructure.Auth.AuthenticationService.AdministratorRole))
            return true;
        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var applicationUserId))
            return false;
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive,
            cancellationToken);
        if (actor is null) return false;
        if (actor.PermissionLevel == OrganizationPermissionLevel.Owner) return true;
        return actor.PermissionLevel >= OrganizationPermissionLevel.Manager &&
               await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == employeeId &&
                   x.OrganizationId == organizationId && x.ReportsToOrganizationUserId == actor.Id && x.IsActive,
                   cancellationToken);
    }

    private static async Task<IResult> ExecuteInstallationActionAsync(
        Func<Task<AgentInstallationResponse>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (AgentInstallationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<AgentWorkCompletion> InvokeAgentConfigurationCapabilityAsync(
        CSweetDbContext db,
        AgentWorkInbox inbox,
        Guid installationId,
        string capability,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CapabilityTimeout);
        var organizationId = await db.AgentInstallations.AsNoTracking()
            .Where(x => x.Id == installationId && x.IsEnabled)
            .Select(x => x.BusinessId)
            .SingleAsync(timeout.Token);
        var arguments = payload.Length == 0
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : JsonDocument.Parse(payload).RootElement.Clone();
        var work = await inbox.EnqueueAsync(
            organizationId,
            installationId,
            AgentWorkKind.Capability,
            capability,
            arguments,
            $"configuration-request:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.Add(CapabilityTimeout),
            sourceType: "management-api",
            cancellationToken: timeout.Token);
        while (true)
        {
            var state = await inbox.ReadStateAsync(work.Id, timeout.Token);
            if (state.Status == AgentWorkStatus.Completed)
                return state.Completion ?? new AgentWorkCompletion(
                    false,
                    null,
                    "The agent returned no configuration result.");
            if (state.Status is AgentWorkStatus.Cancelled or AgentWorkStatus.DeadLetter)
                return new AgentWorkCompletion(
                    false,
                    null,
                    state.Error ?? "The configuration work did not complete.");
            await Task.Delay(ProviderRegistrationRetryDelay, timeout.Token);
        }
    }

    private static bool TryGetFailure(AgentWorkCompletion result, out IResult failure)
    {
        if (!result.Succeeded)
        {
            failure = Results.Conflict(new
            {
                error = string.IsNullOrWhiteSpace(result.Error)
                    ? "The agent could not complete the configuration request."
                    : result.Error
            });
            return true;
        }

        failure = null!;
        return false;
    }

    private static T? Deserialize<T>(AgentWorkCompletion result) =>
        result.Value is { } value
            ? value.Deserialize<T>(SerializerOptions)
            : default;
}
