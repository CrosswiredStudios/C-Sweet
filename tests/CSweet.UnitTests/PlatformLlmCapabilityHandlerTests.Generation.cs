using System.Runtime.CompilerServices;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformLlmCapabilityHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingMeasurementsPersistBeforeCompletionWithoutReplacingUsage(bool fail)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var provider = await AddProviderAsync(db);
        var client = new GenerationChatClient(db, fail);
        var handler = new PlatformLlmCapabilityHandler(db, new StreamingProviderFactory(client),
            new AgentEmployeeIdentityResolver(db), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            [], new TestMediaAssetService(), NullLogger<PlatformLlmCapabilityHandler>.Instance);
        await ReadAsync(db, handler, provider);
        Assert.Equal(2, client.Observations);
        var completed = await db.AgentRunLogs.AsNoTracking().SingleAsync();
        Assert.Equal(fail ? "Failed" : "Completed", completed.Status);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(50, completed.ReportedOutputTokens);
        Assert.Equal(50, completed.TokenOutputCount);
        Assert.Equal(50d, completed.GenerationOutputTokens);
        Assert.False(completed.GenerationIsEstimated);
    }

    private sealed class GenerationChatClient(CSweetDbContext db, bool fail) : IChatClient
    {
        public int Observations { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new(ChatRole.Assistant, [new TextContent("abcdefgh")]);
            var first = await db.AgentRunLogs.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal("Running", first.Status);
            Assert.Equal(2d, first.GenerationOutputTokens);
            Assert.True(first.GenerationIsEstimated);
            Assert.Null(first.ReportedOutputTokens);
            Assert.NotNull(first.GenerationStartedAt);
            Assert.Equal(first.GenerationStartedAt, first.GenerationObservedAt);
            Assert.NotEmpty(await db.AuditOutbox.AsNoTracking().Where(x => x.SourceEntityId == first.Id).ToListAsync(cancellationToken));
            Observations++;
            yield return new(ChatRole.Assistant, [new UsageContent(new UsageDetails { OutputTokenCount = 50 })]);
            var second = await db.AgentRunLogs.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal(50d, second.GenerationOutputTokens);
            Assert.False(second.GenerationIsEstimated);
            Assert.Null(second.ReportedOutputTokens);
            Observations++;
            if (fail) throw new InvalidOperationException("Provider interrupted after streaming.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
