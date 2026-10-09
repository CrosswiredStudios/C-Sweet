using CSweet.Memory;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Fact]
    public async Task BrokerSearchBudgetExhaustionIsSanitizedNonretryableAndNotAnEmptySuccess()
    {
        var partition = EmployeePartition();
        var source = await AppendAsync(partition, "memory source");
        var body = "memory private large fixture " + new string('x', 9 * 1024 * 1024);
        await _store.WriteBlockAsync(new(Guid.NewGuid(), partition, "memory", body, 1, 100, true,
            MemoryTrustTier.ConfirmedUser, DateTimeOffset.UtcNow.AddSeconds(-1))
            { SourceEpisodeIds = [source.Id], Sensitivity = MemorySensitivity.Personal });
        var response = await Handler().HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(partition, MemoryScope.Agent, "memory", Layers: new HashSet<MemoryLayer> { MemoryLayer.Core })), default);
        Assert.False(response.Succeeded);
        Assert.Equal(MemorySearchBudgetExceededException.ErrorCode, response.FailureCode);
        Assert.False(response.Retryable);
        Assert.DoesNotContain("private large fixture", response.Error ?? string.Empty);
        Assert.Contains("Narrow the query", response.Error ?? string.Empty);
    }
}
