using System.Text;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ConnectorReadExecutorTests
{
    [Fact]
    public async Task CompletedReadCannotReturnDataFromATamperedPlan()
    {
        await using var fixture = await ConnectorPlanServiceTests.Fixture.Create();
        var transport = new FakeTransport("confirmed");
        var executor = new ConnectorReadExecutor(fixture.Db, fixture.Service, transport, new NoSecrets(), new TestAuditEventWriter());
        await executor.ExecuteAsync(fixture.Organization, fixture.Requester.Id, ConnectorPlanServiceTests.Fixture.Capability,
            ConnectorPlanServiceTests.Fixture.Input("resource"), "once", default);
        var plan = await fixture.Db.ConnectorExecutions.SingleAsync();
        plan.PlanJson = plan.PlanJson.Replace("confirmed", "another-channel", StringComparison.Ordinal);
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Organization, fixture.Requester.Id,
            ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("resource"), "once", default));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task OwnershipIsCheckedBeforeReadingResource_AndCompletedResultIsDurable()
    {
        await using var fixture = await ConnectorPlanServiceTests.Fixture.Create(ownershipCheck: true);
        var transport = new FakeTransport("confirmed");
        var executor = new ConnectorReadExecutor(fixture.Db, fixture.Service, transport, new NoSecrets(), new TestAuditEventWriter());
        for (var i = 0; i < 2; i++)
        {
            var result = await executor.ExecuteAsync(fixture.Organization, fixture.Requester.Id,
                ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("resource"), "once", default);
            Assert.Equal("value", result.GetProperty("data").GetString());
        }
        Assert.Equal(2, transport.Requests.Count);
        Assert.Contains("/ownership?id=resource", transport.Requests[0]);
        Assert.Contains("/items?owner=confirmed", transport.Requests[1]);
        var plan = await fixture.Db.ConnectorExecutions.SingleAsync();
        Assert.Equal("Completed", plan.Status); Assert.NotNull(plan.ResultJson);
    }

    [Fact]
    public async Task WrongOwnerPreventsTheResourceRead()
    {
        await using var fixture = await ConnectorPlanServiceTests.Fixture.Create(ownershipCheck: true);
        var transport = new FakeTransport("another-channel");
        var executor = new ConnectorReadExecutor(fixture.Db, fixture.Service, transport, new NoSecrets(), new TestAuditEventWriter());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Organization, fixture.Requester.Id,
            ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("resource"), "once", default));
        Assert.Single(transport.Requests);
        Assert.Null((await fixture.Db.ConnectorExecutions.SingleAsync()).ResultJson);
    }

    [Fact]
    public async Task RevokedConnectionCannotRetrieveCachedData()
    {
        await using var fixture = await ConnectorPlanServiceTests.Fixture.Create();
        var transport = new FakeTransport("confirmed");
        var executor = new ConnectorReadExecutor(fixture.Db, fixture.Service, transport, new NoSecrets(), new TestAuditEventWriter());
        await executor.ExecuteAsync(fixture.Organization, fixture.Requester.Id, ConnectorPlanServiceTests.Fixture.Capability,
            ConnectorPlanServiceTests.Fixture.Input("resource"), "once", default);
        fixture.Connection.Status = CSweet.Domain.Setup.PluginConnectionStatus.Revoked;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Organization, fixture.Requester.Id,
            ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("resource"), "once", default));
        Assert.Single(transport.Requests);
    }

    private sealed class FakeTransport(string owner) : IConnectorHttpTransport
    {
        public List<string> Requests { get; } = [];
        public async Task<ConnectorProviderResponse> SendAsync(Guid connectorId, Guid connectionId,
            ConnectorPreparedRequest request, Func<CancellationToken, Task> revalidate, CancellationToken token)
        {
            await revalidate(token); Requests.Add(request.Url);
            return new(200, Encoding.UTF8.GetBytes(request.Url.Contains("/ownership", StringComparison.Ordinal)
                ? System.Text.Json.JsonSerializer.Serialize(new { owner }) : "{\"data\":\"value\"}"));
        }
    }
    private sealed class NoSecrets : IPluginSecretStore
    {
        public Task SetAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<string?> GetAsync(Guid installationId, string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task RemoveAsync(Guid installationId, string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
