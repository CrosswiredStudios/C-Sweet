using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CSweet.AgentHost.Broker;

public sealed partial class PlatformLlmCapabilityHandler
{
    private async Task AuthorizeDispatchAsync(AgentSession session, LlmProviderProfile expected, string selectedModel,
        string? employeeId, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var current = await _dbContext.LlmProviderProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == expected.Id, token);
        if (current is null || !current.IsEnabled || ProviderDispatchConfiguration.Fingerprint(current) != ProviderDispatchConfiguration.Fingerprint(expected) ||
            !Guid.TryParse(session.InstallationId, out var installationId) || !Guid.TryParse(session.RuntimeInstanceId, out var runtimeId) ||
            !Guid.TryParse(session.TickId, out var tickId)) throw new ProviderDispatchDeniedException();
        var installation = await _dbContext.AgentInstallations.AsNoTracking().Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == session.BusinessId && x.IsEnabled, token);
        if (installation?.Grant is not { } grant || grant.GrantRevision != session.Grant.Revision)
            throw new ProviderDispatchDeniedException();
        try
        {
            if (JsonSerializer.Deserialize<string[]>(grant.RequiredCapabilitiesJson)?.Contains(PlatformChatCapabilities.ChatStream, StringComparer.Ordinal) != true)
                throw new ProviderDispatchDeniedException();
        }
        catch (JsonException) { throw new ProviderDispatchDeniedException(); }
        if (!await _dbContext.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == runtimeId && x.TickId == tickId &&
            x.AgentInstallationId == installationId && x.Status == AgentRuntimeStatus.Running &&
            x.MemoryResetRequestedAt == null &&
            (x.RuntimeDeadlineAt == null || x.RuntimeDeadlineAt > now), token)) throw new ProviderDispatchDeniedException();
        if (employeeId is not null && (!Guid.TryParse(employeeId, out var employee) ||
            !await _dbContext.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == employee && x.OrganizationId.ToString() == session.BusinessId &&
                x.AgentInstallationId == installationId && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null, token)))
            throw new ProviderDispatchDeniedException();
        if (!await IsModelApprovedAsync(session, current.Id, selectedModel, current.DefaultChatModel, token)) throw new ProviderDispatchDeniedException();
        var benchmark = await CSweet.Infrastructure.Analytics.BenchmarkModelPolicy.ResolveAsync(_dbContext, installationId, session.BusinessId, token);
        if (benchmark is not null && (benchmark.ProviderProfileId != current.Id || benchmark.Model != selectedModel)) throw new ProviderDispatchDeniedException();
        await new PlatformMemoryReadEvidence(_dbContext).AuthorizeDispatchAsync(session, InferenceExecutionAttribution.Current?.WorkId, token);
        if (InferenceExecutionAttribution.Current is { } attribution)
        {
            if (!await _dbContext.AgentWorkItems.AsNoTracking().AnyAsync(x => x.Id == attribution.WorkId &&
                    x.OrganizationId == session.BusinessId && x.AgentInstallationId == installationId && x.Status == AgentWorkStatus.Leased &&
                    x.DeadlineAt > now, token) ||
                !await _dbContext.AgentWorkAttempts.AsNoTracking().AnyAsync(x => x.AgentWorkItemId == attribution.WorkId &&
                    x.Attempt == attribution.Attempt && x.RuntimeInstanceId == runtimeId && x.FinishedAt == null && x.LeaseExpiresAt > now, token))
                throw new ProviderDispatchDeniedException();
            var work = await _dbContext.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == attribution.WorkId, token);
            await new CSweet.Infrastructure.Core.MemoryRecallDispatchEvidence(_dbContext).AuthorizeWorkAsync(work, employeeId, token);
        }
        else if (await _dbContext.AgentWorkItems.AsNoTracking().AnyAsync(x => x.AgentInstallationId == installationId &&
            x.OrganizationId == session.BusinessId && x.Status == AgentWorkStatus.Leased && x.SourceType == "chat-turn", token))
            // The legacy direct route cannot omit queue attribution to bypass the chat receipt.
            throw new ProviderDispatchDeniedException();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> DispatchUpdatesAsync(IChatClient client, IReadOnlyList<ChatMessage> messages,
        ChatOptions options, Action dispatched, [EnumeratorCancellation] CancellationToken token)
    {
        // Async streams do not dispatch at GetAsyncEnumerator. Check after all pre-call telemetry,
        // at first enumeration; real factory clients also recheck inside every transport attempt.
        await ProviderDispatchScope.AuthorizeCurrentAsync(token);
        if (client.GetService<IProviderDispatchTransport>() is null) dispatched();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, token)) yield return update;
    }
}
