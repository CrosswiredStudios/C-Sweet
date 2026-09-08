using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;

namespace CSweet.UnitTests;

public sealed class AgentOnboardingRecoveryTests
{
    [Fact]
    public async Task DispatcherPersistsRecoveryOfAnExistingFailedHireWithoutCreatingDuplicateWork()
    {
        var services = new ServiceCollection();
        var database = Guid.NewGuid().ToString();
        services.AddDbContext<CSweetDbContext>(x => x.UseInMemoryDatabase(database));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AgentWorkInbox>();
        services.AddScoped<AgentWorkRouter>();
        await using var provider = services.BuildServiceProvider();
        var organization = Guid.NewGuid(); var package = Guid.NewGuid(); var installation = Guid.NewGuid();
        var employee = Guid.NewGuid(); var eventId = Guid.NewGuid(); var deliveryId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            db.Add(new AgentInstallation { Id = installation, PackageVersionId = package, BusinessId = organization.ToString(),
                IsEnabled = true, RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready });
            db.Add(new OrganizationUser { Id = employee, OrganizationId = organization, EmployeeType = EmployeeType.Agent,
                AgentInstallationId = installation, IsActive = true });
            db.Add(new AgentOnboardingEventOutboxItem { Id = eventId, OrganizationId = organization,
                AgentOrganizationUserId = employee, Status = AgentOnboardingEventOutboxStatus.Pending,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1), Attempts = 80 });
            db.Add(new AgentWorkItem { Id = deliveryId, AgentInstallationId = installation, OrganizationId = organization.ToString(),
                Kind = AgentWorkKind.Event, Name = CSweet.Agent.SDK.AgentLifecycleEvents.Onboarded,
                IdempotencyKey = $"{AgentOnboardingEventDispatcher.CreateDeliveryKey(eventId, package)}:{installation:D}",
                SourceId = eventId.ToString(), Status = AgentWorkStatus.DeadLetter, AttemptCount = 3, MaximumAttempts = 3,
                LastError = "agent-failure:v1;code=runtime.transport;retryable=true;diagnosticId=test" });
            await db.SaveChangesAsync();
        }
        await new AgentOnboardingEventDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            Options.Create(new AgentOnboardingDeliveryOptions()), NullLogger<AgentOnboardingEventDispatcher>.Instance)
            .DispatchPendingAsync(default);
        await using var verification = provider.CreateAsyncScope();
        var check = verification.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var work = Assert.Single(await check.AgentWorkItems.ToListAsync());
        Assert.Equal(deliveryId, work.Id);
        Assert.Equal(AgentWorkStatus.Pending, work.Status);
        Assert.Equal(3, work.AttemptCount);
        Assert.Equal(12, work.MaximumAttempts);
        var onboarding = await check.AgentOnboardingEventOutbox.SingleAsync();
        Assert.Equal(80, onboarding.Attempts);
        Assert.Contains("Retrying", onboarding.LastError);
    }
    [Fact]
    public void TransientFailureResumesTheSameDeliveryWithoutLosingAttemptHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new AgentOnboardingEventOutboxItem { Status = AgentOnboardingEventOutboxStatus.Pending, Attempts = 80 };
        var delivery = new AgentWorkItem { Id = Guid.NewGuid(), Status = AgentWorkStatus.DeadLetter,
            AttemptCount = 3, MaximumAttempts = 3, DeadlineAt = now.AddHours(-1), IdempotencyKey = "stable-delivery",
            SourceId = item.Id.ToString(), LastError = "agent-failure:v1;code=runtime.transport;retryable=true;diagnosticId=test" };
        AgentOnboardingEventDispatcher.ReconcileDelivery(item, delivery, now, 12);
        Assert.Equal(AgentWorkStatus.Pending, delivery.Status);
        Assert.Equal(3, delivery.AttemptCount);
        Assert.Equal(12, delivery.MaximumAttempts);
        Assert.Equal("stable-delivery", delivery.IdempotencyKey);
        Assert.Equal(now.AddSeconds(30), delivery.AvailableAt);
        Assert.Equal(now.AddHours(1), delivery.DeadlineAt);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Pending, item.Status);
        Assert.Contains("Retrying", item.LastError);
    }

    [Theory]
    [InlineData(3, "agent-failure:v1;code=capability.failed;retryable=false")]
    [InlineData(12, "agent-failure:v1;code=runtime.transport;retryable=true")]
    public void TerminalFailuresAreVisibleAndDoNotRestartIndefinitely(int attempts, string error)
    {
        var item = new AgentOnboardingEventOutboxItem();
        var delivery = new AgentWorkItem { Status = AgentWorkStatus.DeadLetter, AttemptCount = attempts, LastError = error };
        AgentOnboardingEventDispatcher.ReconcileDelivery(item, delivery, DateTimeOffset.UtcNow, 12);
        Assert.Equal(AgentWorkStatus.DeadLetter, delivery.Status);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Failed, item.Status);
        Assert.Contains(error, item.LastError);
    }

    [Fact]
    public void CompletedDeliveryWithoutAcknowledgementIsNotReportedAsWaiting()
    {
        var item = new AgentOnboardingEventOutboxItem();
        AgentOnboardingEventDispatcher.ReconcileDelivery(item,
            new AgentWorkItem { Status = AgentWorkStatus.Completed }, DateTimeOffset.UtcNow, 12);
        Assert.Equal(AgentOnboardingEventOutboxStatus.Failed, item.Status);
        Assert.Contains("without acknowledging", item.LastError);
    }
}