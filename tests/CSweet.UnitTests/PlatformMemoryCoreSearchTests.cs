using System.Text.Json;
using CSweet.Memory;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokerCoreSearchKeepsTopicAndPersistentContextDistinctWithoutReleasingRestrictedEvidence(bool includePinned)
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "Confirmed response preferences.");
        var restrictedSource = await AppendAsync(partition, "Private response notes.", MemorySensitivity.Restricted);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var digest = new MemoryBlock(Guid.NewGuid(), partition, "Friday digest", "Report unresolved blockers first.",
            1, 128, true, MemoryTrustTier.ConfirmedUser, now)
            { Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = [source.Id] };
        var workshop = digest with { Id = Guid.NewGuid(), Name = "Workshop notes", Content = "List decisions and owners." };
        var restricted = digest with { Id = Guid.NewGuid(), Name = "Private digest", Content = "Restricted Friday digest details.",
            SourceEpisodeIds = [restrictedSource.Id] };
        await _store.WriteBlockAsync(digest);
        await _store.WriteBlockAsync(workshop);
        await _store.WriteBlockAsync(restricted);
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(partition, MemoryScope.Agent, "Friday digest", Layers: new HashSet<MemoryLayer> { MemoryLayer.Core })
                { IncludePinnedCore = includePinned }), default);
        Assert.True(response.Succeeded);
        var candidates = JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!;
        Assert.Contains(candidates, x => x.Id == digest.Id);
        Assert.Equal(includePinned, candidates.Any(x => x.Id == workshop.Id));
        Assert.DoesNotContain(candidates, x => x.Id == restricted.Id);
    }
}
