using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class ToolchainCertificationCompletionTests
{
    [Theory]
    [InlineData("matching", true)] [InlineData("mismatched", false)] [InlineData("missing", false)] [InlineData("failed", false)]
    public async Task CertificationReadsTheProductionWebJsonProvenanceAndRetainsRealFailureGates(string scenario, bool expected)
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection(); services.AddDbContext<CSweetDbContext>(x => x.UseInMemoryDatabase(name));
        await using var provider = services.BuildServiceProvider();
        var org = Guid.NewGuid(); var runId = Guid.NewGuid(); var installationId = Guid.NewGuid(); var definitionId = Guid.NewGuid();
        await using (var seed = provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test.node", Version = "1.0.2", PackageDigest = new string('c',64) };
            db.AddRange(new AgentInstallation { Id = installationId, BusinessId = org.ToString(), PackageVersion = package, PackageVersionId = package.Id },
                new ToolchainAdapterDefinitionRecord { Id = definitionId, ProviderPackageId = package.AgentId, ProviderPackageVersion = package.Version, DefinitionDigest = "definition" },
                new ToolchainCertificationRunRecord { Id = runId, OrganizationId = org, ProviderInstallationId = installationId, ToolchainDefinitionId = definitionId,
                    ProviderPackageDigest = "sha256:" + package.PackageDigest, DefinitionDigest = "definition", Status = W.ToolchainCertificationStatuses.Running,
                    EnvironmentProfileKey = "node", EnvironmentImageDigest = "sha256:image", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            foreach (var recipe in new[] { "phaser", "babylon" })
            foreach (var pass in new[] { 1, 2 })
            {
                var provenance = new W.BuildExecutionProvenance(package.AgentId, package.Version, "definition", "sha256:image",
                    JsonSerializer.SerializeToElement(new { node = "24.15.0" }), "lock", ["npm ci --offline"], "source",
                    scenario == "mismatched" && pass == 2 ? "different" : recipe + "-manifest", []);
                db.DeliveryBuilds.Add(new DeliveryBuildRecord { Id = Guid.NewGuid(), OrganizationId = org, CertificationRunId = runId,
                    ProviderInstallationId = installationId, ToolchainDefinitionId = definitionId, RecipeKey = recipe, TargetKey = "web",
                    CertificationFixtureKey = recipe, CertificationPass = pass,
                    Status = scenario == "failed" ? W.DeliveryBuildStatuses.Failed : W.DeliveryBuildStatuses.Succeeded,
                    ProvenanceJson = scenario == "missing" ? "{}" : JsonSerializer.Serialize(provenance, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            }
            await db.SaveChangesAsync();
        }
        var worker = new ToolchainCertificationWorker(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ToolchainCertificationWorker>.Instance);
        await worker.ReviewAsync(default); await worker.ReviewAsync(default);
        await using var read = provider.CreateAsyncScope(); var current = read.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Equal(expected ? W.ToolchainCertificationStatuses.Certified : W.ToolchainCertificationStatuses.Failed,
            (await current.ToolchainCertificationRuns.SingleAsync()).Status);
        Assert.Equal(expected ? 1 : 0, await current.ToolchainInstallationEligibilities.CountAsync());
    }
}
