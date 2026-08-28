using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
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

public class CoreBusinessDomainTests
{
    [Fact]
    public async Task CreateOrganization_ReturnsCreated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var request = new CreateOrganizationRequest(
            "Acme Corp", "Technology", null, null, null, null);

        var response = await client.PostAsJsonAsync("/api/organizations", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var org = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(org);
        Assert.Equal("Acme Corp", org.Name);
        Assert.Equal("Technology", org.Industry);
    }

    [Fact]
    public async Task CreateOrganization_WithEmptyName_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var request = new CreateOrganizationRequest(
            "  ", null, null, null, null, null);

        var response = await client.PostAsJsonAsync("/api/organizations", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CoreActionResponse>();
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task GetOrganizations_ReturnsList()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var createRequest = new CreateOrganizationRequest(
            "Acme Corp", "Technology", null, null, null, null);
        await client.PostAsJsonAsync("/api/organizations", createRequest);

        var list = await client.GetFromJsonAsync<IReadOnlyList<OrganizationResponse>>("/api/organizations");

        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("Acme Corp", list[0].Name);
    }

    [Fact]
    public async Task CreateRole_ReturnsCreated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);

        var request = new CreateRoleRequest(
            "Custom Role", "Custom description", "[]", 0);

        var response = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/roles", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var role = await response.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.NotNull(role);
        Assert.Equal("Custom Role", role.Name);
    }

    [Fact]
    public async Task CreateDuplicateRole_ReturnsBadRequest()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);

        var request = new CreateRoleRequest(
            "Custom Role", "Test", "[]", 0);

        var response1 = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/roles", request);
        Assert.Equal(HttpStatusCode.Created, response1.StatusCode);

        var response2 = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/roles", request);
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
    }

    [Fact]
    public async Task CreateWorker_ReturnsCreated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);

        var request = new CreateWorkerRequest(
            "Test Worker", "Test", 0, 0, "[]", null, null, true, false);

        var response = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/workers", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var worker = await response.Content.ReadFromJsonAsync<WorkerResponse>();
        Assert.NotNull(worker);
        Assert.Equal("Test Worker", worker.Name);
    }

    [Fact]
    public async Task CreateTask_ReturnsCreated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);

        var request = new CreateWorkTaskRequest(
            "Test Task", "Test description", null, null, null, 0, 0, null, false);

        var response = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/tasks", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<WorkTaskResponse>();
        Assert.NotNull(task);
        Assert.Equal("Test Task", task.Title);
    }

    [Fact]
    public async Task CreateArtifact_ReturnsCreated()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);
        await AuthorizeOwnerAsync(factory, client, org.Id);

        var request = new CreateArtifactRequest(
            null, null, 0, "Test Artifact", "Test content", 1, 0);

        var response = await client.PostAsJsonAsync($"/api/core/artifacts/organization/{org.Id}", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var artifact = await response.Content.ReadFromJsonAsync<ArtifactResponse>();
        Assert.NotNull(artifact);
        Assert.Equal("Test Artifact", artifact.Title);
    }

    [Fact]
    public async Task ApproveArtifact_ReturnsOk()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);
        await AuthorizeOwnerAsync(factory, client, org.Id);
        var artifact = await CreateArtifactAsync(client, org);

        var request = new CreateApprovalRequest(2, "Looks good");

        var response = await client.PostAsJsonAsync($"/api/artifacts/{artifact.Id}/approve", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approval = await response.Content.ReadFromJsonAsync<ApprovalResponse>();
        Assert.NotNull(approval);
        Assert.Equal(2, approval.Status); // Approved
    }

    [Fact]
    public async Task RejectArtifact_ReturnsOk()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var org = await CreateOrganizationAsync(client);
        await AuthorizeOwnerAsync(factory, client, org.Id);
        var artifact = await CreateArtifactAsync(client, org);

        var request = new CreateApprovalRequest(3, "Needs revision");

        var response = await client.PostAsJsonAsync($"/api/artifacts/{artifact.Id}/reject", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approval = await response.Content.ReadFromJsonAsync<ApprovalResponse>();
        Assert.NotNull(approval);
        Assert.Equal(3, approval.Status); // Rejected
    }

    [Fact]
    public async Task FullWorkflow_CreateOrg_AddRole_AddWorker_CreateTask_CreateArtifact_Approve()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        // 1. Create organization
        var org = await CreateOrganizationAsync(client);
        await AuthorizeOwnerAsync(factory, client, org.Id);

        // 2. Add role (default roles are seeded, but add a custom one)
        var roleRequest = new CreateRoleRequest(
            "Engineering Lead", "Tech lead", "[]", 2);
        var roleResponse = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/roles", roleRequest);
        Assert.Equal(HttpStatusCode.Created, roleResponse.StatusCode);
        var role = await roleResponse.Content.ReadFromJsonAsync<RoleResponse>();
        Assert.NotNull(role);

        // 3. Add worker
        var workerRequest = new CreateWorkerRequest(
            "AI Assistant", "Test worker", 0, 0, "[]", null, null, true, false);
        var workerResponse = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/workers", workerRequest);
        Assert.Equal(HttpStatusCode.Created, workerResponse.StatusCode);
        var worker = await workerResponse.Content.ReadFromJsonAsync<WorkerResponse>();
        Assert.NotNull(worker);

        // 4. Create task
        var taskRequest = new CreateWorkTaskRequest(
            "Build Feature", "Implement new feature", null, role.Id, worker.Id, 0, 1, null, true);
        var taskResponse = await client.PostAsJsonAsync($"/api/organizations/{org.Id}/tasks", taskRequest);
        Assert.Equal(HttpStatusCode.Created, taskResponse.StatusCode);
        var task = await taskResponse.Content.ReadFromJsonAsync<WorkTaskResponse>();
        Assert.NotNull(task);

        // 5. Create artifact
        var artifactRequest = new CreateArtifactRequest(
            task.Id, null, 0, "Feature Spec", "Feature specification content", 1, 1); // Pending
        var artifactResponse = await client.PostAsJsonAsync($"/api/core/artifacts/organization/{org.Id}", artifactRequest);
        Assert.Equal(HttpStatusCode.Created, artifactResponse.StatusCode);
        var artifact = await artifactResponse.Content.ReadFromJsonAsync<ArtifactResponse>();
        Assert.NotNull(artifact);

        // 6. Approve artifact
        var approveRequest = new CreateApprovalRequest(2, "Approved");
        var approveResponse = await client.PostAsJsonAsync($"/api/artifacts/{artifact.Id}/approve", approveRequest);
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approval = await approveResponse.Content.ReadFromJsonAsync<ApprovalResponse>();
        Assert.NotNull(approval);
        Assert.Equal(2, approval.Status); // Approved
    }

    private static async Task<OrganizationResponse> CreateOrganizationAsync(HttpClient client)
    {
        var request = new CreateOrganizationRequest(
            "Test Corp", "Technology", null, null, null, null);
        var response = await client.PostAsJsonAsync("/api/organizations", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var organization = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(organization);
        return organization;
    }

    private static async Task<ArtifactResponse> CreateArtifactAsync(HttpClient client, OrganizationResponse org)
    {
        var request = new CreateArtifactRequest(
            null, null, 0, "Test Artifact", "Test content", 1, 1); // Pending
        var response = await client.PostAsJsonAsync($"/api/core/artifacts/organization/{org.Id}", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var artifact = await response.Content.ReadFromJsonAsync<ArtifactResponse>();
        Assert.NotNull(artifact);
        return artifact;
    }

    private static async Task AuthorizeOwnerAsync(WebApplicationFactory<Program> factory, HttpClient client,
        Guid organizationId)
    {
        var applicationUserId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Test Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Remove("X-Core-Test-UserId");
        client.DefaultRequestHeaders.Add("X-Core-Test-UserId", applicationUserId.ToString("D"));
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                    services.AddDbContext<CSweetDbContext>(options =>
                        options.UseInMemoryDatabase(databaseName));
                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = CoreTestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = CoreTestAuthenticationHandler.SchemeName;
                        options.DefaultScheme = CoreTestAuthenticationHandler.SchemeName;
                    }).AddScheme<AuthenticationSchemeOptions, CoreTestAuthenticationHandler>(
                        CoreTestAuthenticationHandler.SchemeName, _ => { });
                });
            });
    }
}

file sealed class CoreTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CoreBusinessTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Core-Test-UserId", out var value) || !Guid.TryParse(value, out var id))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString("D"))], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
