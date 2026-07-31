using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Auth;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CSweet.IntegrationTests;

public sealed class AuthenticationEndpointTests
{
    [Fact]
    public async Task FreshInstall_RegistersWithoutMail_SignsInAndReturnsRecoveryCodes()
    {
        await using var factory = CreateFactory(out var emailSender);
        var client = CreateHttpsClient(factory);

        var initialStatus = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        Assert.NotNull(initialStatus);
        Assert.True(initialStatus.RegistrationOpen);
        Assert.False(initialStatus.IsAuthenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/setup/status")).StatusCode);

        var registration = await client.PostAsJsonAsync("/api/auth/register", ValidRegistration());
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var registrationResult = await registration.Content.ReadFromJsonAsync<AuthActionResponse>();
        Assert.NotNull(registrationResult);
        Assert.Equal(10, registrationResult.RecoveryCodes?.Count);
        Assert.Empty(emailSender.Confirmations);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            Assert.Equal("Admin User", (await db.Users.SingleAsync()).DisplayName);
        }

        var authenticated = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        Assert.NotNull(authenticated);
        Assert.True(authenticated.IsAuthenticated);
        Assert.False(authenticated.IsEmailConfirmed);
        Assert.False(authenticated.EmailRecoveryAvailable);
        Assert.False(authenticated.RegistrationOpen);
        Assert.False(string.IsNullOrWhiteSpace(authenticated.AntiforgeryToken));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/setup/status")).StatusCode);

        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", authenticated.AntiforgeryToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/setup/status")).StatusCode);
    }

    [Fact]
    public async Task Registration_UsesIdentityValidationAndClosesAfterSingleConcurrentWinner()
    {
        await using var factory = CreateFactory(out _);
        var namelessClient = CreateHttpsClient(factory);
        var nameless = await namelessClient.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterAdminRequest(" ", "admin@example.com", "Password1!", "Password1!"));
        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);

        var weakClient = CreateHttpsClient(factory);
        var weak = await weakClient.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterAdminRequest("Admin User", "admin@example.com", "password", "password"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var firstClient = CreateHttpsClient(factory);
        var secondClient = CreateHttpsClient(factory);
        var attempts = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/auth/register", ValidRegistration("first@example.com")),
            secondClient.PostAsJsonAsync("/api/auth/register", ValidRegistration("second@example.com")));

        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UnverifiedRoot_CanLoginAndRecoverWithSingleUseOfflineCode()
    {
        await using var factory = CreateFactory(out var emailSender);
        var client = CreateHttpsClient(factory);
        var registration = await client.PostAsJsonAsync("/api/auth/register", ValidRegistration());
        var registered = await registration.Content.ReadFromJsonAsync<AuthActionResponse>();
        var recoveryCode = Assert.Single(registered!.RecoveryCodes!.Take(1));

        var session = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", session!.AntiforgeryToken);
        await client.PostAsync("/api/auth/logout", null);
        client.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");

        var rootLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ADMIN@example.com", "Password1!"));
        Assert.Equal(HttpStatusCode.OK, rootLogin.StatusCode);
        var loggedIn = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", loggedIn!.AntiforgeryToken);
        await client.PostAsync("/api/auth/logout", null);
        client.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");

        var resetResponse = await client.PostAsJsonAsync("/api/auth/recover-root",
            new RecoverRootRequest("admin@example.com", recoveryCode, "NewPassword1!", "NewPassword1!"));
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        var reused = await client.PostAsJsonAsync("/api/auth/recover-root",
            new RecoverRootRequest("admin@example.com", recoveryCode, "AnotherPassword1!", "AnotherPassword1!"));
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);

        var oldLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", "Password1!"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", "NewPassword1!"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task UnverifiedNonRoot_CannotLogin()
    {
        await using var factory = CreateFactory(out _);
        var client = CreateHttpsClient(factory);
        await client.PostAsJsonAsync("/api/auth/register", ValidRegistration());

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var invited = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                DisplayName = "Invited Person",
                UserName = "person@example.com",
                Email = "person@example.com",
                IsInitialAdministrator = false,
                CreatedAt = DateTimeOffset.UtcNow
            };
            Assert.True((await users.CreateAsync(invited, "Password1!")).Succeeded);
        }

        var session = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", session!.AntiforgeryToken);
        await client.PostAsync("/api/auth/logout", null);
        client.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("person@example.com", "Password1!"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var result = await login.Content.ReadFromJsonAsync<AuthActionResponse>();
        Assert.Equal("email_not_confirmed", result!.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentRecoveryCodeRedemption_HasSingleWinner()
    {
        await using var factory = CreateFactory(out _);
        var registrationClient = CreateHttpsClient(factory);
        var registration = await registrationClient.PostAsJsonAsync("/api/auth/register", ValidRegistration());
        var result = await registration.Content.ReadFromJsonAsync<AuthActionResponse>();
        var code = result!.RecoveryCodes![0];

        var first = CreateHttpsClient(factory);
        var second = CreateHttpsClient(factory);
        var attempts = await Task.WhenAll(
            first.PostAsJsonAsync("/api/auth/recover-root", new RecoverRootRequest("admin@example.com", code, "NewPassword1!", "NewPassword1!")),
            second.PostAsJsonAsync("/api/auth/recover-root", new RecoverRootRequest("admin@example.com", code, "OtherPassword1!", "OtherPassword1!")));

        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmailDeliveryProfiles_EncryptPasswordAndRequireSuccessfulDefaultTestForRecovery()
    {
        await using var factory = CreateFactory(out var sender);
        var client = CreateHttpsClient(factory);
        await client.PostAsJsonAsync("/api/auth/register", ValidRegistration());
        var session = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", session!.AntiforgeryToken);

        var update = new CSweet.Contracts.Setup.SaveEmailDeliveryProfileRequest(
            "Primary SMTP", CSweet.Contracts.Setup.EmailDeliveryProviderKeys.CustomSmtp,
            "smtp.example.com", 587, true, "smtp-user", "top-secret", false,
            "sender@example.com", "C-Sweet", "https://csweet.example.com");
        var saved = await client.PostAsJsonAsync("/api/setup/email-delivery/profiles", update);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedResult = await saved.Content.ReadFromJsonAsync<CSweet.Contracts.Setup.EmailDeliveryProfileActionResponse>();
        var profileId = savedResult!.Profile!.Id;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var persisted = await db.EmailDeliveryProfiles.SingleAsync();
            Assert.NotEqual("top-secret", persisted.EncryptedPassword);
            Assert.False(string.IsNullOrWhiteSpace(persisted.EncryptedPassword));
        }

        var beforeTest = await client.GetFromJsonAsync<IReadOnlyList<CSweet.Contracts.Setup.EmailDeliveryProfileResponse>>("/api/setup/email-delivery/profiles");
        Assert.True(Assert.Single(beforeTest!).HasPassword);
        Assert.False(Assert.Single(beforeTest!).IsReady);

        var tested = await client.PostAsync($"/api/setup/email-delivery/profiles/{profileId:D}/test", null);
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.Single(sender.Tests);
        var afterTest = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        Assert.True(afterTest!.EmailRecoveryAvailable);
    }

    [Fact]
    public async Task Login_RememberMeControlsWhetherAuthenticationCookieIsPersistent()
    {
        await using var factory = CreateFactory(out _);
        var client = CreateHttpsClient(factory);
        await client.PostAsJsonAsync("/api/auth/register", ValidRegistration());

        var session = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", session!.AntiforgeryToken);
        await client.PostAsync("/api/auth/logout", null);
        client.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");

        var sessionLogin = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.com", "Password1!", RememberMe: false));
        var sessionCookie = Assert.Single(sessionLogin.Headers.GetValues("Set-Cookie"), value => value.Contains("CSweet.Auth", StringComparison.Ordinal));
        Assert.DoesNotContain("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);

        session = await client.GetFromJsonAsync<AuthStatusResponse>("/api/auth/session");
        client.DefaultRequestHeaders.Add("X-CSWEET-CSRF", session!.AntiforgeryToken);
        await client.PostAsync("/api/auth/logout", null);
        client.DefaultRequestHeaders.Remove("X-CSWEET-CSRF");

        var rememberedLogin = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.com", "Password1!", RememberMe: true));
        var persistentCookie = Assert.Single(rememberedLogin.Headers.GetValues("Set-Cookie"), value => value.Contains("CSweet.Auth", StringComparison.Ordinal));
        Assert.Contains("expires=", persistentCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static RegisterAdminRequest ValidRegistration(string email = "admin@example.com") =>
        new("Admin User", email, "Password1!", "Password1!");

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

    private static WebApplicationFactory<Program> CreateFactory(out FakeAccountEmailSender emailSender)
    {
        var sender = new FakeAccountEmailSender();
        emailSender = sender;
        var databaseName = Guid.NewGuid().ToString();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("AuthenticationTesting");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.RemoveAll<IAccountEmailSender>();
                services.AddSingleton<IAccountEmailSender>(sender);
            });
        });
    }

    private sealed class FakeAccountEmailSender : IAccountEmailSender
    {
        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public ConcurrentQueue<EmailToken> Confirmations { get; } = new();
        public ConcurrentQueue<EmailToken> PasswordResets { get; } = new();
        public ConcurrentQueue<string> Tests { get; } = new();

        public Task SendConfirmationAsync(string email, Guid userId, string code, CancellationToken cancellationToken)
        {
            Confirmations.Enqueue(new EmailToken(email, userId, code));
            return Task.CompletedTask;
        }

        public Task SendPasswordResetAsync(string email, Guid userId, string code, CancellationToken cancellationToken)
        {
            PasswordResets.Enqueue(new EmailToken(email, userId, code));
            return Task.CompletedTask;
        }

        public Task SendTestAsync(string email, Guid profileId, CancellationToken cancellationToken)
        {
            Tests.Enqueue(email);
            return Task.CompletedTask;
        }
    }

    private sealed record EmailToken(string Email, Guid UserId, string Code);
}
