using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

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
        }).RequireRateLimiting(AgentRateLimiting.ImportPolicy);

        group.MapPost("/imports/{importId:guid}/install", async (
            Guid importId,
            InstallAgentRequest request,
            IAgentInstallationService installationService,
            CancellationToken cancellationToken) =>
            await ExecuteInstallationActionAsync(
                () => installationService.InstallAsync(importId, request, cancellationToken)))
            .RequireRateLimiting(AgentRateLimiting.BuildPolicy);

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

        group.MapGet("/installations/{installationId:guid}/configuration", async (
            Guid installationId,
            AgentWorkInbox inbox,
            CSweetDbContext db,
            IAgentInteractiveRuntimeService interactiveRuntime,
            IAgentInstallationConfigurationService configurations,
            CancellationToken cancellationToken) =>
        {
            var readiness = await interactiveRuntime.EnsureReadyAsync(installationId, cancellationToken);
            if (!readiness.IsReady)
            {
                return Results.Accepted($"/api/agents/installations/{installationId}/runtime/status", readiness);
            }

            var result = await InvokeAgentConfigurationCapabilityAsync(
                db,
                inbox,
                installationId,
                AgentConfigurationCapabilities.Describe,
                payload: [],
                cancellationToken);

            if (TryGetFailure(result, out var failure))
            {
                return failure;
            }

            var response = Deserialize<AgentConfigurationSchemaResponse>(result);
            if (response is null)
            {
                return Results.Conflict(new { error = "The agent returned an empty configuration response." });
            }

            var persisted = await configurations.GetAsync(installationId, cancellationToken);
            if (persisted is null)
            {
                return Results.Ok(response);
            }

            var supportedKeys = response.Fields.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var settings = response.Settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            foreach (var setting in persisted.Settings.Where(x => supportedKeys.Contains(x.Key)))
            {
                settings[setting.Key] = setting.Value;
            }

            return Results.Ok(response with { Settings = settings });
        });

        group.MapPost("/installations/{installationId:guid}/configuration", async (
            Guid installationId,
            UpdateAgentConfigurationRequest request,
            AgentWorkInbox inbox,
            CSweetDbContext db,
            IAgentInteractiveRuntimeService interactiveRuntime,
            IAgentInstallationConfigurationService configurations,
            CancellationToken cancellationToken) =>
        {
            var readiness = await interactiveRuntime.EnsureReadyAsync(installationId, cancellationToken);
            if (!readiness.IsReady)
            {
                return Results.Accepted($"/api/agents/installations/{installationId}/runtime/status", readiness);
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
            var result = await InvokeAgentConfigurationCapabilityAsync(
                db,
                inbox,
                installationId,
                AgentConfigurationCapabilities.Update,
                payload,
                cancellationToken);

            if (TryGetFailure(result, out var failure))
            {
                return failure;
            }

            var response = Deserialize<AgentConfigurationUpdateResponse>(result);
            if (response is null)
            {
                return Results.Conflict(new { error = "The agent returned an empty configuration response." });
            }

            if (!response.Succeeded)
            {
                return Results.Ok(response);
            }

            var existing = await configurations.GetAsync(installationId, cancellationToken);
            var persisted = await configurations.SaveAsync(
                installationId,
                request.SchemaVersion ?? existing?.SchemaVersion ?? "1",
                response.Settings,
                cancellationToken);

            return Results.Ok(response with { Settings = persisted.Settings });
        });

        return endpoints;
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
