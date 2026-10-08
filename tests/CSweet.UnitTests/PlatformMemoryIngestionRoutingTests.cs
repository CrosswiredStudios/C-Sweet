using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EpisodeBrokerUsesDurableAcceptanceAfterServerAttributionAndNeverFallsBackOnFailure(bool fail)
    {
        var ingestion = new RecordingIngestion(fail);
        var handler = new PlatformMemoryCapabilityHandler(_store, NullLogger<PlatformMemoryCapabilityHandler>.Instance,
            new AgentMemoryIdentityResolver(_db), new QueryContractEvidence(), ingestion);
        var now = DateTimeOffset.UtcNow;
        var input = new MemoryEpisode(Guid.NewGuid(), EmployeePartition(), MemoryScope.Tenant, "memory proposal", "text/plain",
            new("user", "forged"), "bad", now, now, LegalHold: true, Sensitivity: MemorySensitivity.Public);
        var result = await handler.HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write, "append-episode", input), default);
        Assert.Equal(!fail, result.Succeeded);
        Assert.Equal((_organization, _employee, _installation), ingestion.Owner);
        var accepted = Assert.IsType<MemoryEpisode>(ingestion.Episode);
        Assert.Equal(new("agent-proposal", accepted.Id.ToString("D"), _employee.ToString("D")), accepted.Source);
        Assert.False(accepted.LegalHold); Assert.Equal(MemorySensitivity.Personal, accepted.Sensitivity);
        Assert.Equal(MemoryScope.Agent, accepted.Scope);
        Assert.Empty((await _store.ExportAsync(input.Partition)).Episodes);
    }

    private sealed class RecordingIngestion(bool fail) : IAgentMemoryIngestion
    {
        public (Guid, Guid, Guid) Owner { get; private set; }
        public MemoryEpisode? Episode { get; private set; }
        public Task<MemoryWriteResult> AcceptProposalAsync(Guid organizationId, Guid employeeId, Guid installationId,
            MemoryEpisode episode, CancellationToken cancellationToken = default)
        {
            Owner = (organizationId, employeeId, installationId); Episode = episode;
            if (fail) throw new InvalidOperationException("Acceptance transaction failed.");
            return Task.FromResult(new MemoryWriteResult(episode.Id, true));
        }
    }
}
