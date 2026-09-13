using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;
using Fixture = CSweet.UnitTests.McpAgentSessionServiceTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeAuthenticatedSessionTests
{
    [Fact]
    public async Task Issued_session_discovers_approved_compute_tool_and_reuses_one_durable_request()
    {
        await using var f = await Fixture.CreateAsync(); var workstream = await PrepareAsync(f);
        var issue = await f.EstablishAsync();
        var input = Input(workstream);
        var first = await InvokeAsync(f, issue.AccessToken, issue.Session.SessionId, input);
        var second = await InvokeAsync(f, issue.AccessToken, issue.Session.SessionId, input);
        Assert.True(first.Succeeded, first.Error); Assert.True(second.Succeeded, second.Error);
        Assert.Equal(first.Payload.ToElement().GetProperty("id").GetGuid(), second.Payload.ToElement().GetProperty("id").GetGuid());
        var environment = await f.Db.ComputeEnvironments.SingleAsync();
        Assert.Equal(f.Installation.Id, environment.InstallationId);
        Assert.Equal(Guid.Parse(f.Installation.BusinessId), environment.OrganizationId);
        Assert.Single(await f.Db.ComputeOperations.ToListAsync());
    }

    [Fact]
    public async Task Grant_revision_change_invalidates_existing_token_before_compute_dispatch()
    {
        await using var f = await Fixture.CreateAsync(); var workstream = await PrepareAsync(f);
        var issue = await f.EstablishAsync();
        f.Installation.Grant!.GrantRevision++;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InvokeAsync(f, issue.AccessToken, issue.Session.SessionId, Input(workstream)));
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
    }

    [Fact]
    public async Task Wrong_session_id_and_disabled_installation_cannot_use_a_previously_issued_compute_token()
    {
        await using var f = await Fixture.CreateAsync(); var workstream = await PrepareAsync(f);
        var issue = await f.EstablishAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InvokeAsync(f, issue.AccessToken, "different-session", Input(workstream)));
        f.Installation.IsEnabled = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InvokeAsync(f, issue.AccessToken, issue.Session.SessionId, Input(workstream)));
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
    }

    [Fact]
    public async Task Valid_session_still_requires_a_current_scoped_compute_grant()
    {
        await using var f = await Fixture.CreateAsync(); var workstream = await PrepareAsync(f);
        var issue = await f.EstablishAsync();
        (await f.Db.ScopedActionGrants.SingleAsync()).RevokedAt = f.Clock.GetUtcNow();
        await f.Db.SaveChangesAsync();
        Assert.NotNull(await f.Service.AuthenticateAsync(issue.AccessToken, issue.Session.SessionId, default));
        var result = await InvokeAsync(f, issue.AccessToken, issue.Session.SessionId, Input(workstream));
        Assert.False(result.Succeeded); Assert.Equal("compute_authority_denied", result.FailureCode);
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
    }
    private static async Task<Guid> PrepareAsync(Fixture f)
    {
        var organization = Guid.NewGuid(); var workstream = Guid.NewGuid(); var actor = Guid.NewGuid();
        f.Installation.BusinessId = organization.ToString("D"); f.Installation.SetupState = PluginSetupState.Ready;
        f.Installation.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { InfrastructureActions.Provision });
        f.Package.ManifestJson = JsonSerializer.Serialize(new { requires = new[]
        { new { name = InfrastructureActions.Provision, scope = "organization", purpose = "Test isolated compute", modelVisible = true } } });
        f.Db.CoreOrganizations.Add(new() { Id = organization, Name = "Authenticated compute" });
        f.Db.CoreOrganizationUsers.Add(new() { Id = actor, OrganizationId = organization, AgentInstallationId = f.Installation.Id,
            EmployeeType = EmployeeType.Agent, IsActive = true });
        f.Db.Workstreams.Add(new() { Id = workstream, OrganizationId = organization, AccountableManagerOrganizationUserId = actor });
        f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = f.Installation.Id, ScopeKind = GrantScopeKind.Workstream, ScopeId = workstream, Action = InfrastructureActions.Provision,
            GrantedAt = f.Clock.GetUtcNow().AddMinutes(-1), ExpiresAt = f.Clock.GetUtcNow().AddHours(1),
            ConstraintsJson = JsonSerializer.Serialize(new ComputeGrantConstraints(1, new(2, 4096, 20480), 1, 600,
                ["linux"], ["x64"], ["ubuntu-clean"]), ComputeProtocol.Json) });
        await f.Db.SaveChangesAsync(); return workstream;
    }

    private static async Task<CapabilityResult> InvokeAsync(Fixture f, string token, string sessionId, JsonElement input)
    {
        // Same authentication, catalog resolution, schema validation and dispatcher sequence used by tools/call.
        // This test is in-process and does not replace full HTTP gateway acceptance.
        var session = await f.Service.AuthenticateAsync(token, sessionId, default) ?? throw new UnauthorizedAccessException();
        var handler = new ComputeCapabilityHandler(new ComputeBroker(f.Db, new ComputeBrokerTests.Catalog(), f.Clock, new ComputeBrokerTests.Audit()));
        var tool = await new McpToolCatalog([handler]).FindAsync("request_compute_environment", session, f.Db, default)
            ?? throw new UnauthorizedAccessException();
        Assert.True(tool.ModelVisible);
        JsonSchemaValidator.Validate(input, tool.InputSchema);
        var results = new List<CapabilityResult>();
        await foreach (var result in new PlatformCapabilityDispatcher([handler]).InvokeAsync(session,
            new() { RequestId = "authenticated-request", Capability = tool.Capability, Payload = JsonPayload.FromUtf8(input.GetRawText()) }, default))
            results.Add(result);
        return Assert.Single(results);
    }

    private static JsonElement Input(Guid workstream) => JsonSerializer.SerializeToElement(new
    {
        workstreamId = workstream, desiredEnvironmentKey = "installer-test", idempotencyKey = "stable-request",
        specification = new { operatingSystem = "linux", architecture = "x64", templateId = "ubuntu-clean",
            resources = new { cpuCount = 2, memoryMiB = 4096, diskMiB = 20480 }, lifetimeSeconds = 600 }
    });
}
