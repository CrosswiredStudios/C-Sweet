using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CSweet.Memory;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [Fact]
    public async Task ConversationRecallRequiresTheCurrentDirectRecipientAndHumanSender()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        await using var db = fixture.Context();
        var message = await db.CoreConversationMessages.SingleAsync();
        await fixture.Store.AppendEpisodeAsync(new(Guid.NewGuid(), fixture.Partition, MemoryScope.User, "name Alice private preference",
            "text/plain", new("user", "fixture"), "checksum", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
            Sensitivity: MemorySensitivity.Personal));
        var service = fixture.Service(db, new UsageProviderFactory());
        Assert.Null(await service.RecallForConversationRecipientAsync(message.ConversationId, Guid.NewGuid(), fixture.HumanId, "name"));
        Assert.Null(await service.RecallForConversationRecipientAsync(message.ConversationId, fixture.EmployeeId, Guid.NewGuid(), "name"));
        Assert.Empty(await db.AgentMemoryRecallUses.ToListAsync());
        Assert.Contains("Alice", await service.RecallForConversationRecipientAsync(message.ConversationId, fixture.EmployeeId, fixture.HumanId, "name"));
        foreach (var kind in new[] { ConversationKind.Team, ConversationKind.AgentChannel, ConversationKind.Project })
        {
            var conversation = await db.CoreConversations.SingleAsync(); conversation.Kind = kind; await db.SaveChangesAsync();
            Assert.Null(await service.RecallForConversationRecipientAsync(message.ConversationId, fixture.EmployeeId, fixture.HumanId, "name"));
        }
        var current = await db.CoreConversations.SingleAsync(); current.Kind = ConversationKind.DirectHumanAgent;
        current.ArchivedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        Assert.Null(await service.RecallForConversationRecipientAsync(message.ConversationId, fixture.EmployeeId, fixture.HumanId, "name"));
        Assert.Single(await db.AgentMemoryRecallUses.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task EnrichmentSdkRetryRevalidatesSourceAndPersistsExclusionWithoutSendingAgain()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        using var wire = new InvalidatingEnrichmentWire(async () =>
        {
            await using var changed = fixture.Context();
            await changed.CoreConversationMessages.Where(x => x.Id == fixture.MessageId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "Changed after the first provider attempt"));
        });
        using var http = new HttpClient(wire);
        var factory = new OpenAiCompatibleLlmProviderFactory(db, new InMemoryLlmProviderSecretStore(),
            NullLogger<OpenAiCompatibleLlmProviderFactory>.Instance) { TransportOverride = new HttpClientPipelineTransport(http) };
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(1, wire.Calls);
        var job = await db.MemoryCaptureOutbox.SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status); Assert.Equal("memory_enrichment_source_invalidated", job.LastError);
        Assert.Null(job.AcceptedExtractionJson); Assert.Equal(fixture.MessageId, (await db.MemoryCaptureExclusions.SingleAsync()).SourceMessageId);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        var log = await db.AgentRunLogs.SingleAsync(); Assert.Equal("Denied", log.Status); Assert.NotNull(log.ProviderStartedAt);
    }

    private sealed class InvalidatingEnrichmentWire(Func<Task> change) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; await change();
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{\"error\":{\"message\":\"retry fixture\",\"type\":\"server_error\"}}", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("Retry-After", "0"); return response;
        }
    }
}
