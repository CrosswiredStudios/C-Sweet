using CSweet.Api.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using CSweet.Application.Setup;

namespace CSweet.UnitTests;

public sealed class ToolchainSetupTests
{
    [Fact]
    public async Task SetupAndCertificationActionsRemainDeploymentAdministratorOnly()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddDbContext<CSweetDbContext>(x => x.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        builder.Services.AddScoped<IExecutionWorkloadOrchestrator>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IGuestImageRegistry>(_ => throw new NotSupportedException());
        await using var app = builder.Build(); app.MapToolchainCertificationEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Contains(endpoints, x => x.RoutePattern.RawText == "/api/admin/toolchain-certifications/setup");
        Assert.All(endpoints, x => Assert.Contains(x.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "HostAdministration"));
    }

    [Theory]
    [InlineData("eligible", 1)] [InlineData("other-business", 0)] [InlineData("disabled", 0)]
    [InlineData("staged", 0)] [InlineData("wrong-package-version", 0)] [InlineData("unbuilt", 0)]
    [InlineData("system-scope", 0)] [InlineData("needs-setup", 0)]
    public async Task SetupListsOnlyExactBuiltActiveBusinessProviderWithoutCreatingCertification(string scenario, int count)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organization = Guid.NewGuid();
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test.toolchain", Version = "1.0.1",
            PluginKind = PluginKind.Service, Status = AgentPackageVersionStatus.Built };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = organization.ToString(),
            PackageVersion = package, PackageVersionId = package.Id };
        var definition = new ToolchainAdapterDefinitionRecord { Id = Guid.NewGuid(), ProviderPackageId = package.AgentId,
            ProviderPackageVersion = package.Version, DisplayName = "Node", DefinitionJson = """{"recipes":[{"requiredEnvironmentProfileKeys":["office.node.v1"]}]}""" };
        if (scenario == "other-business") installation.BusinessId = Guid.NewGuid().ToString();
        if (scenario == "disabled") installation.IsEnabled = false;
        if (scenario == "staged") installation.RevisionStatus = PluginRevisionStatus.Staged;
        if (scenario == "wrong-package-version") definition.ProviderPackageVersion = "1.0.0";
        if (scenario == "unbuilt") package.Status = AgentPackageVersionStatus.Approved;
        if (scenario == "system-scope") installation.Scope = PluginInstallationScope.System;
        if (scenario == "needs-setup") installation.SetupState = PluginSetupState.NeedsSetup;
        db.AgentInstallations.Add(installation); db.ToolchainAdapterDefinitions.Add(definition); await db.SaveChangesAsync();
        var result = await ToolchainCertificationEndpoints.ReadSetupAsync(organization, db, default);
        Assert.Equal(count, result.Providers.Count);
        if (count == 1) { var provider = Assert.Single(result.Providers); Assert.Equal(installation.Id, provider.InstallationId);
            Assert.Equal(definition.Id, provider.DefinitionId); Assert.Equal("office.node.v1", Assert.Single(provider.EnvironmentProfiles)); }
        Assert.Empty(await db.ToolchainCertificationRuns.ToArrayAsync()); Assert.Empty(await db.ToolchainInstallationEligibilities.ToArrayAsync());
    }

    [Theory]
    [InlineData("ready", 1)] [InlineData("offline", 0)] [InlineData("unavailable", 0)] [InlineData("no-toolchain-support", 0)]
    public async Task SetupOffersOnlyCurrentReadyOfficeCapacityWithoutClaimingItCertified(string scenario, int count)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var node = new ExecutionNode { Id = Guid.NewGuid(), Name = "Office", Status = ExecutionNodeStatus.Ready,
            LastHeartbeatAt = scenario == "offline" ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow };
        node.Providers.Add(new() { Id = Guid.NewGuid(), ExecutionNodeId = node.Id, GuestImageDigest = "sha256:image",
            IsAvailable = scenario != "unavailable", SupportsToolchainBuildWorkloads = scenario != "no-toolchain-support" });
        db.ExecutionNodes.Add(node); await db.SaveChangesAsync();
        var setup = await ToolchainCertificationEndpoints.ReadSetupAsync(Guid.NewGuid(), db, default);
        Assert.Equal(count, setup.Images.Count); Assert.Empty(await db.ToolchainInstallationEligibilities.ToArrayAsync());
    }
}
