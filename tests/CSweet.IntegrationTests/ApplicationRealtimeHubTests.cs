using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Application.Notifications;
using CSweet.Contracts.Auth;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public sealed class ApplicationRealtimeHubTests
{
    [Fact]
    public async Task AuthenticatedConnections_ReceiveOnlyEventsForTheirOrganizationUserGroup()
    {
        await using var factory = CreateFactory();
        var first = await CreateUserAndLoginAsync(factory, "first@example.com");
        var second = await CreateUserAndLoginAsync(factory, "second@example.com");
        Guid firstOrganizationUserId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var organization = new Organization { Id = Guid.NewGuid(), Name = "Realtime Co",
                Status = OrganizationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var firstMember = Member(organization.Id, first.UserId, "First");
            var secondMember = Member(organization.Id, second.UserId, "Second");
            firstOrganizationUserId = firstMember.Id;
            db.AddRange(organization, firstMember, secondMember);
            await db.SaveChangesAsync();
        }

        await using var firstConnection = Connection(factory, first.Cookie);
        await using var secondConnection = Connection(factory, second.Cookie);
        var firstEvent = new TaskCompletionSource<AppRealtimeEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEvent = new TaskCompletionSource<AppRealtimeEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstConnection.On<AppRealtimeEventEnvelope>("AppEvent", value =>
        {
            if (value.EventType == "test.event.v1") firstEvent.TrySetResult(value);
        });
        secondConnection.On<AppRealtimeEventEnvelope>("AppEvent", value =>
        {
            if (value.EventType == "test.event.v1") secondEvent.TrySetResult(value);
        });
        await firstConnection.StartAsync();
        await secondConnection.StartAsync();

        using var document = JsonDocument.Parse("{\"value\":1}");
        var envelope = new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, "test.event.v1", null,
            "test/subject", DateTimeOffset.UtcNow, document.RootElement.Clone());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IApplicationRealtimePublisher>();
            await publisher.PublishAsync(new ApplicationRealtimePublication(envelope, [firstOrganizationUserId]));
        }

        Assert.Equal(envelope.EventId, (await firstEvent.Task.WaitAsync(TimeSpan.FromSeconds(5))).EventId);
        await Task.Delay(300);
        Assert.False(secondEvent.Task.IsCompleted);
    }

    [Fact]
    public async Task ConnectionEstablishedBeforeBusinessMembership_ReceivesLaterOrganizationEvents()
    {
        await using var factory = CreateFactory();
        var user = await CreateUserAndLoginAsync(factory, "new-owner@example.com");
        await using var connection = Connection(factory, user.Cookie);
        var received = new TaskCompletionSource<AppRealtimeEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<AppRealtimeEventEnvelope>("AppEvent", value =>
        {
            if (value.Subject == "test/welcome") received.TrySetResult(value);
        });
        await connection.StartAsync();

        Guid organizationUserId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var organization = new Organization
            {
                Id = Guid.NewGuid(), Name = "New Business", Status = OrganizationStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            var member = Member(organization.Id, user.UserId, "New Owner");
            organizationUserId = member.Id;
            db.AddRange(organization, member);
            await db.SaveChangesAsync();
        }

        using var document = JsonDocument.Parse("{\"message\":\"welcome\"}");
        var envelope = new AppRealtimeEventEnvelope(Guid.NewGuid(), 1,
            "com.csweet.communication.message.created.v1", null, "test/welcome",
            DateTimeOffset.UtcNow, document.RootElement.Clone());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<IApplicationRealtimePublisher>();
            await publisher.PublishAsync(new ApplicationRealtimePublication(envelope, [organizationUserId]));
        }

        Assert.Equal(envelope.EventId, (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).EventId);
    }

    [Fact]
    public async Task CommunicationPerspective_ReceivesOnlySelectedAgentsCommunicationEvents()
    {
        await using var factory = CreateFactory();
        var user = await CreateUserAndLoginAsync(factory, "viewer@example.com");
        var organizationId = Guid.NewGuid();
        var firstAgentId = Guid.NewGuid();
        var secondAgentId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var organization = new Organization { Id = organizationId, Name = "Perspective Co",
                Status = OrganizationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.AddRange(
                organization,
                Member(organizationId, user.UserId, "Viewer"),
                Agent(organizationId, firstAgentId, "First Agent"),
                Agent(organizationId, secondAgentId, "Second Agent"));
            await db.SaveChangesAsync();
        }

        await using var connection = Connection(factory, user.Cookie);
        var receivedIds = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>();
        connection.On<AppRealtimeEventEnvelope>("AppEvent", value => receivedIds.TryAdd(value.EventId, 0));
        await connection.StartAsync();

        await connection.InvokeAsync("SetCommunicationPerspective", organizationId, firstAgentId);
        var firstCommunication = Envelope("com.csweet.communication.message.created.v1", organizationId);
        var firstNotification = Envelope(AppRealtimeEvents.NotificationCreated, organizationId);
        await PublishAsync(factory, firstCommunication, firstAgentId);
        await PublishAsync(factory, firstNotification, firstAgentId);
        await WaitForEventAsync(receivedIds, firstCommunication.EventId);
        await Task.Delay(200);
        Assert.False(receivedIds.ContainsKey(firstNotification.EventId));

        await connection.InvokeAsync("SetCommunicationPerspective", organizationId, secondAgentId);
        var oldPerspective = Envelope("com.csweet.communication.message.created.v1", organizationId);
        var newPerspective = Envelope("com.csweet.communication.message.created.v1", organizationId);
        await PublishAsync(factory, oldPerspective, firstAgentId);
        await PublishAsync(factory, newPerspective, secondAgentId);
        await WaitForEventAsync(receivedIds, newPerspective.EventId);
        await Task.Delay(200);
        Assert.False(receivedIds.ContainsKey(oldPerspective.EventId));

        await connection.InvokeAsync("SetCommunicationPerspective", Guid.Empty, null);
        var afterClear = Envelope("com.csweet.communication.message.created.v1", organizationId);
        await PublishAsync(factory, afterClear, secondAgentId);
        await Task.Delay(200);
        Assert.False(receivedIds.ContainsKey(afterClear.EventId));
    }

    [Fact]
    public async Task CommunicationPerspective_RejectsInvalidTargets()
    {
        await using var factory = CreateFactory();
        var user = await CreateUserAndLoginAsync(factory, "security-viewer@example.com");
        var organizationId = Guid.NewGuid();
        Guid humanTargetId;
        Guid inactiveAgentId;
        Guid otherOrganizationAgentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var organization = new Organization { Id = organizationId, Name = "Secure Perspective Co",
                Status = OrganizationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var otherOrganization = new Organization { Id = Guid.NewGuid(), Name = "Other Co",
                Status = OrganizationStatus.Active, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var viewer = Member(organizationId, user.UserId, "Viewer");
            var humanTarget = Member(organizationId, Guid.NewGuid(), "Human Target");
            var inactiveAgent = Agent(organizationId, Guid.NewGuid(), "Inactive Agent");
            inactiveAgent.IsActive = false;
            var otherAgent = Agent(otherOrganization.Id, Guid.NewGuid(), "Other Agent");
            humanTargetId = humanTarget.Id;
            inactiveAgentId = inactiveAgent.Id;
            otherOrganizationAgentId = otherAgent.Id;
            db.AddRange(organization, otherOrganization, viewer, humanTarget, inactiveAgent, otherAgent);
            await db.SaveChangesAsync();
        }

        await using var connection = Connection(factory, user.Cookie);
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync(
            "SetCommunicationPerspective", organizationId, humanTargetId));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync(
            "SetCommunicationPerspective", organizationId, inactiveAgentId));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync(
            "SetCommunicationPerspective", organizationId, otherOrganizationAgentId));
    }

    private static OrganizationUser Member(Guid organizationId, Guid applicationUserId, string name) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
        DisplayName = name, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor,
        IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private static OrganizationUser Agent(Guid organizationId, Guid id, string name) => new()
    {
        Id = id, OrganizationId = organizationId, DisplayName = name,
        EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Contributor,
        IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private static AppRealtimeEventEnvelope Envelope(string eventType, Guid organizationId)
    {
        using var document = JsonDocument.Parse("{\"value\":1}");
        return new AppRealtimeEventEnvelope(Guid.NewGuid(), 1, eventType, organizationId,
            $"test/{Guid.NewGuid():N}", DateTimeOffset.UtcNow, document.RootElement.Clone());
    }

    private static async Task PublishAsync(
        WebApplicationFactory<Program> factory,
        AppRealtimeEventEnvelope envelope,
        Guid recipientOrganizationUserId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IApplicationRealtimePublisher>();
        await publisher.PublishAsync(new ApplicationRealtimePublication(envelope, [recipientOrganizationUserId]));
    }

    private static async Task WaitForEventAsync(
        System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> receivedIds,
        Guid eventId)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!receivedIds.ContainsKey(eventId) && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(25);
        Assert.True(receivedIds.ContainsKey(eventId), $"Realtime event {eventId:D} was not received.");
    }

    private static HubConnection Connection(WebApplicationFactory<Program> factory, string cookie) =>
        new HubConnectionBuilder().WithUrl("http://localhost/hubs/app-events", options =>
        {
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.Headers["Cookie"] = cookie;
        }).Build();

    private static async Task<(Guid UserId, string Cookie)> CreateUserAndLoginAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email,
                EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow };
            Assert.True((await users.CreateAsync(user, "Strong!Pass123")).Succeeded);
            id = user.Id;
        }
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Strong!Pass123"));
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(x => x.Contains("CSweet.Auth", StringComparison.Ordinal)).Split(';')[0];
        return (id, cookie);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("AuthenticationTesting");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
            });
        });
    }
}
