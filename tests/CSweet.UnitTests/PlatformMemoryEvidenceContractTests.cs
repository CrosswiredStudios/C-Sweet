using CSweet.AgentHost.Broker;
using CSweet.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    // Query/write policy fixtures intentionally isolate store semantics. Production and the
    // PostgreSQL dispatch tests use PlatformMemoryReadEvidence and persisted work leases.
    private sealed class QueryContractEvidence : IPlatformMemoryReadEvidence
    {
        public Task<MemoryReadInvocation> BeginAsync(AgentSession session, string capability, CancellationToken token) =>
            Task.FromResult(new MemoryReadInvocation(Guid.Empty, 1, Guid.Empty, Guid.Empty, "query-contract"));
        public Task RecordAsync(AgentSession session, string capability, MemoryReadInvocation invocation, object? result,
            MemoryPartition? searchPartition, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task MissingReadEvidenceTrackingCannotReturnMemory()
    {
        await AppendAsync(EmployeePartition(), "private memory");
        var handler = new PlatformMemoryCapabilityHandler(_store, NullLogger<PlatformMemoryCapabilityHandler>.Instance, new AgentMemoryIdentityResolver(_db));
        var result = await handler.HandleAsync(Session(), Request(CSweetMemoryCapabilities.Query, "search",
            new MemorySearchRequest(EmployeePartition(), MemoryScope.Agent, "private")), default);
        Assert.False(result.Succeeded); Assert.Equal("memory_policy_denied", result.FailureCode);
        Assert.DoesNotContain("private memory", result.Payload.ToStringUtf8());
    }
}
