using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.IntegrationTests;

public sealed class AnalyticsEndpointTests
{
    [Fact]
    public async Task Inference_EnforcesManagementMembershipAndValidatesWindow()
    {
        await using var factory = CreateFactory();
        var organizationId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var contributorId = Guid.NewGuid();
        var agentManagerId = Guid.NewGuid();
        await SeedAsync(factory, organizationId, ownerId, managerId, contributorId, agentManagerId);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, organizationId, ownerId, "30d")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, organizationId, managerId, "24h")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(factory, organizationId, contributorId, "7d")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(factory, organizationId, agentManagerId, "7d")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(factory, organizationId, Guid.NewGuid(), "7d")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(factory, organizationId, ownerId, "all")).StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/organizations/{organizationId:D}/analytics/inference?window=30d")).StatusCode);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        WebApplicationFactory<Program> factory,
        Guid organizationId,
        Guid userId,
        string window)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Analytics-Test-UserId", userId.ToString("D"));
        return await client.GetAsync(
            $"/api/organizations/{organizationId:D}/analytics/inference?window={window}");
    }

    private static async Task SeedAsync(
        WebApplicationFactory<Program> factory,
        Guid organizationId,
        Guid ownerId,
        Guid managerId,
        Guid contributorId,
        Guid agentManagerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SystemConfigurations.Add(new SystemConfiguration
        {
            Id = Guid.NewGuid(), IsFirstRunComplete = true, CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId, Name = "Analytics company", Status = OrganizationStatus.Active,
            CreatedAt = now, UpdatedAt = now
        });
        db.CoreOrganizationUsers.AddRange(
            Member(organizationId, ownerId, EmployeeType.Human, OrganizationPermissionLevel.Owner, "Owner", now),
            Member(organizationId, managerId, EmployeeType.Human, OrganizationPermissionLevel.Manager, "Manager", now),
            Member(organizationId, contributorId, EmployeeType.Human, OrganizationPermissionLevel.Contributor, "Contributor", now),
            Member(organizationId, agentManagerId, EmployeeType.Agent, OrganizationPermissionLevel.Manager, "Agent manager", now));
        await db.SaveChangesAsync();
    }

    private static OrganizationUser Member(
        Guid organizationId,
        Guid applicationUserId,
        EmployeeType employeeType,
        OrganizationPermissionLevel permissionLevel,
        string name,
        DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        ApplicationUserId = applicationUserId,
        DisplayName = name,
        EmployeeType = employeeType,
        PermissionLevel = permissionLevel,
        IsActive = true,
        CreatedAt = createdAt
    };

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = AnalyticsAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = AnalyticsAuthenticationHandler.SchemeName;
                    options.DefaultScheme = AnalyticsAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, AnalyticsAuthenticationHandler>(
                    AnalyticsAuthenticationHandler.SchemeName,
                    _ => { });
            });
        });
    }
}

file sealed class AnalyticsAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AnalyticsTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Analytics-Test-UserId", out var value) ||
            !Guid.TryParse(value, out var id))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString("D"))],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
