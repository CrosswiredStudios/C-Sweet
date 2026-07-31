using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class EmailDeliveryProfileServiceTests
{
    [Fact]
    public async Task ProfilesSupportMultipleEntriesAndDeterministicDefaultPromotion()
    {
        await using var db = CreateDbContext();
        var service = new EmailDeliveryProfileService(db, new FakeConfigurationProvider(), new FakeEmailSender());

        var first = await service.CreateAsync(Request("Primary", "smtp.one.example"));
        var second = await service.CreateAsync(Request("Secondary", "smtp.two.example"));

        Assert.True(first.Profile!.IsDefault);
        Assert.False(second.Profile!.IsDefault);
        Assert.Equal(2, (await service.ListAsync()).Count);

        var selected = await service.SetDefaultAsync(second.Profile.Id);
        Assert.True(selected.Succeeded);
        Assert.True((await service.ListAsync()).Single(x => x.Id == second.Profile.Id).IsDefault);

        var deleted = await service.DeleteAsync(second.Profile.Id);
        Assert.True(deleted.Succeeded);
        Assert.True(Assert.Single(await service.ListAsync()).IsDefault);
    }

    [Fact]
    public async Task TestTargetsSelectedProfileAndMarksItReady()
    {
        await using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            DisplayName = "Admin User",
            UserName = "admin@example.com",
            Email = "admin@example.com",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var sender = new FakeEmailSender();
        var service = new EmailDeliveryProfileService(db, new FakeConfigurationProvider(), sender);
        var profile = (await service.CreateAsync(Request("Primary", "smtp.example.com"))).Profile!;

        var result = await service.TestAsync(profile.Id, userId);

        Assert.True(result.Succeeded);
        Assert.Equal(profile.Id, sender.TestedProfileId);
        Assert.True(result.Profile!.IsReady);
    }

    private static SaveEmailDeliveryProfileRequest Request(string name, string host) => new(
        name,
        EmailDeliveryProviderKeys.CustomSmtp,
        host,
        587,
        true,
        "smtp-user",
        "secret",
        false,
        "sender@example.com",
        "C-Sweet",
        "https://csweet.example.com");

    private static CSweetDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FakeConfigurationProvider : IEmailDeliveryConfigurationProvider
    {
        public Task<EffectiveEmailDeliverySettings> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EffectiveEmailDeliverySettings> GetAsync(Guid profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public string Encrypt(string value) => $"encrypted:{value}";
    }

    private sealed class FakeEmailSender : IAccountEmailSender
    {
        public Guid? TestedProfileId { get; private set; }
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task SendConfirmationAsync(string email, Guid userId, string code, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendPasswordResetAsync(string email, Guid userId, string code, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendTestAsync(string email, Guid profileId, CancellationToken cancellationToken)
        {
            TestedProfileId = profileId;
            return Task.CompletedTask;
        }
    }
}
