using System.Text.Json;
using CSweet.Application.Analytics;
using CSweet.Contracts.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

public sealed class InferenceAnalyticsService(
    CSweetDbContext dbContext,
    TimeProvider timeProvider) : IInferenceAnalyticsService
{
    public async Task<InferenceAnalyticsResponse> GetAsync(
        Guid organizationId,
        InferenceAnalyticsWindow window,
        CancellationToken cancellationToken = default)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var windowStart = generatedAt - WindowDuration(window);

        var logs = await dbContext.AgentRunLogs
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.StartedAt >= windowStart)
            .ToListAsync(cancellationToken);

        var employees = await dbContext.CoreOrganizationUsers
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Agent)
            .ToListAsync(cancellationToken);
        var employeesById = employees.ToDictionary(x => x.Id);

        var activeInstallationIds = employees
            .Where(x => x.IsActive && x.AgentInstallationId.HasValue)
            .Select(x => x.AgentInstallationId!.Value)
            .Distinct()
            .ToArray();
        var installations = await dbContext.AgentInstallations
            .AsNoTracking()
            .Include(x => x.PackageVersion)
            .Include(x => x.Configuration)
            .Where(x => activeInstallationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var providerIds = logs.Select(x => x.ProviderProfileId)
            .Concat(installations.Values.Select(CurrentProviderId).Where(x => x.HasValue).Select(x => x!.Value))
            .Distinct()
            .ToArray();
        var providerNames = await dbContext.LlmProviderProfiles
            .AsNoTracking()
            .Where(x => providerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var rows = logs
            .GroupBy(x => new UsageKey(x.EmployeeId, x.ProviderProfileId, x.Model))
            .Select(group => BuildUsageRow(group, employeesById, providerNames, installations))
            .ToList();

        foreach (var employee in employees.Where(x => x.IsActive))
        {
            var current = CurrentConfiguration(employee, installations);
            var hasCurrentRow = rows.Any(x =>
                x.EmployeeId == employee.Id &&
                x.ProviderProfileId == current.ProviderProfileId &&
                string.Equals(x.Model, current.Model, StringComparison.Ordinal));
            if (hasCurrentRow)
            {
                continue;
            }

            rows.Add(new EmployeeModelInferenceUsageResponse(
                employee.Id,
                employee.DisplayName,
                true,
                current.AgentKey,
                current.ProviderProfileId,
                ProviderName(current.ProviderProfileId, providerNames),
                current.Model,
                IsCurrentModel: current.ProviderProfileId.HasValue && !string.IsNullOrWhiteSpace(current.Model),
                RequestCount: 0,
                InputTokens: 0,
                OutputTokens: 0,
                TotalTokens: 0,
                LastUsedAt: null));
        }

        rows = rows
            .OrderByDescending(x => x.TotalTokens)
            .ThenBy(x => x.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inputTokens = logs.Sum(x => (long)(x.TokenInputCount ?? 0));
        var outputTokens = logs.Sum(x => (long)(x.TokenOutputCount ?? 0));
        return new InferenceAnalyticsResponse(
            WindowKey(window),
            windowStart,
            generatedAt,
            generatedAt,
            new InferenceAnalyticsTotalsResponse(
                logs.Count,
                inputTokens,
                outputTokens,
                inputTokens + outputTokens),
            rows);
    }

    private static EmployeeModelInferenceUsageResponse BuildUsageRow(
        IGrouping<UsageKey, AgentRunLog> group,
        IReadOnlyDictionary<Guid, OrganizationUser> employees,
        IReadOnlyDictionary<Guid, string> providerNames,
        IReadOnlyDictionary<Guid, AgentInstallation> installations)
    {
        var first = group.First();
        var employee = first.EmployeeId.HasValue
            ? employees.GetValueOrDefault(first.EmployeeId.Value)
            : null;
        var current = employee is null
            ? CurrentAgentConfiguration.Empty
            : CurrentConfiguration(employee, installations);
        var inputTokens = group.Sum(x => (long)(x.TokenInputCount ?? 0));
        var outputTokens = group.Sum(x => (long)(x.TokenOutputCount ?? 0));

        return new EmployeeModelInferenceUsageResponse(
            first.EmployeeId,
            employee?.DisplayName ?? "Unattributed agent",
            employee?.IsActive ?? false,
            current.AgentKey.Length > 0 ? current.AgentKey : group.OrderByDescending(x => x.StartedAt).First().AgentKey,
            first.ProviderProfileId,
            ProviderName(first.ProviderProfileId, providerNames),
            first.Model,
            employee?.IsActive == true &&
                current.ProviderProfileId == first.ProviderProfileId &&
                string.Equals(current.Model, first.Model, StringComparison.Ordinal),
            group.Count(),
            inputTokens,
            outputTokens,
            inputTokens + outputTokens,
            group.Max(x => x.StartedAt));
    }

    private static CurrentAgentConfiguration CurrentConfiguration(
        OrganizationUser employee,
        IReadOnlyDictionary<Guid, AgentInstallation> installations)
    {
        if (!employee.AgentInstallationId.HasValue ||
            !installations.TryGetValue(employee.AgentInstallationId.Value, out var installation))
        {
            return CurrentAgentConfiguration.Empty;
        }

        var (providerId, model) = ReadConfiguration(installation.Configuration?.SettingsJson);
        return new CurrentAgentConfiguration(
            installation.PackageVersion?.AgentId ?? string.Empty,
            providerId,
            model);
    }

    private static Guid? CurrentProviderId(AgentInstallation installation) =>
        ReadConfiguration(installation.Configuration?.SettingsJson).ProviderProfileId;

    private static (Guid? ProviderProfileId, string? Model) ReadConfiguration(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            var providerId = root.TryGetProperty("llmProviderId", out var provider) &&
                provider.ValueKind == JsonValueKind.String &&
                Guid.TryParse(provider.GetString(), out var parsedProviderId)
                    ? parsedProviderId
                    : (Guid?)null;
            var model = root.TryGetProperty("llmModel", out var configuredModel) &&
                configuredModel.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(configuredModel.GetString())
                    ? configuredModel.GetString()!.Trim()
                    : null;
            return (providerId, model);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ProviderName(
        Guid? providerProfileId,
        IReadOnlyDictionary<Guid, string> providerNames) =>
        providerProfileId.HasValue
            ? providerNames.GetValueOrDefault(providerProfileId.Value) ?? "Deleted provider"
            : null;

    private static TimeSpan WindowDuration(InferenceAnalyticsWindow window) => window switch
    {
        InferenceAnalyticsWindow.Last24Hours => TimeSpan.FromHours(24),
        InferenceAnalyticsWindow.Last7Days => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30)
    };

    private static string WindowKey(InferenceAnalyticsWindow window) => window switch
    {
        InferenceAnalyticsWindow.Last24Hours => "24h",
        InferenceAnalyticsWindow.Last7Days => "7d",
        _ => "30d"
    };

    private sealed record CurrentAgentConfiguration(
        string AgentKey,
        Guid? ProviderProfileId,
        string? Model)
    {
        public static CurrentAgentConfiguration Empty { get; } = new(string.Empty, null, null);
    }

    private sealed record UsageKey(Guid? EmployeeId, Guid ProviderProfileId, string? Model);
}
