using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class ManagedBuildProviderIdentityTests
{
    [Theory]
    [InlineData("valid", true)] [InlineData("ungranted", false)] [InlineData("other-build", false)]
    [InlineData("other-organization", false)] [InlineData("expired-assignment", false)] [InlineData("disabled", false)]
    [InlineData("ordinary-runtime", false)] [InlineData("wrong-package", false)] [InlineData("staged", false)]
    [InlineData("workstream-scan", false)] [InlineData("employee-capability", false)]
    public async Task ServiceUsesOnlyGrantedExactManagedBuildAuthorityWithoutEmployeeMembership(string scenario, bool expected)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var providerId = Guid.NewGuid(); var runtimeId = Guid.NewGuid();
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test.node", Version = "1.0.1", PluginKind = PluginKind.Service, Status = AgentPackageVersionStatus.Built };
        var provider = new AgentInstallation { Id = providerId, BusinessId = org.ToString(), PackageVersion = package, PackageVersionId = package.Id, Scope = PluginInstallationScope.Organization };
        var definition = new ToolchainAdapterDefinitionRecord { Id = Guid.NewGuid(), ProviderPackageId = package.AgentId, ProviderPackageVersion = package.Version, DefinitionDigest = new string('d',64), DefinitionJson = """
            {"key":"test.v1","version":1,"displayName":"Test","recipes":[],"requiredExecutableVersions":{},"outputPolicy":{"maximumTotalBytes":1024,"maximumFileBytes":1024,"maximumFileCount":10},"supportedContentTypes":[],"previewModes":[]}
            """ };
        var stream = new Workstream { Id = Guid.NewGuid(), OrganizationId = org };
        var build = new DeliveryBuildRecord { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = stream.Id,
            ProviderInstallationId = providerId, ToolchainDefinitionId = definition.Id, DefinitionDigest = definition.DefinitionDigest,
            SourceRevision = new string('a',40), Status = W.DeliveryBuildStatuses.Queued, MaximumAttempts = 3 };
        var runtime = new AgentRuntimeInstance { Id = runtimeId, TickId = Guid.NewGuid(), AgentInstallationId = providerId, DeliveryBuildId = build.Id };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.Running, DateTimeOffset.UtcNow);
        var assignment = new ExecutionWorkloadAssignment { Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id,
            DeliveryBuildId = build.Id, WorkloadKind = ExecutionWorkloadKind.ToolchainBuild, Status = ExecutionAssignmentStatus.Running,
            ExecutionNodeId = Guid.NewGuid(), LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        if (scenario == "expired-assignment") assignment.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        if (scenario == "disabled") provider.IsEnabled = false;
        if (scenario == "ordinary-runtime") runtime.DeliveryBuildId = null;
        if (scenario == "wrong-package") definition.ProviderPackageVersion = "1.0.0";
        if (scenario == "staged") provider.RevisionStatus = PluginRevisionStatus.Staged;
        var readCapability = scenario == "employee-capability" ? W.DeliveryEvidenceCapabilityNames.BuildRequestV2 : W.DeliveryEvidenceCapabilityNames.BuildReadV2;
        var grants = new HashSet<string> { readCapability, W.DeliveryEvidenceCapabilityNames.BuildClaimV1,
            W.DeliveryEvidenceCapabilityNames.BuildHeartbeatV1, W.DeliveryEvidenceCapabilityNames.BuildReportV2 };
        if (scenario == "ungranted") grants.Remove(readCapability);
        var session = new AgentSession("session", package.AgentId, providerId.ToString(),
            scenario == "other-organization" ? Guid.NewGuid().ToString() : org.ToString(), runtime.Id.ToString(), runtime.TickId.ToString(),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), grants, 1));
        db.AddRange(provider, definition, stream, build, runtime, assignment,
            new ToolchainInstallationEligibilityRecord { Id = Guid.NewGuid(), OrganizationId = org, ToolchainDefinitionId = definition.Id,
                ProviderInstallationId = providerId, EnvironmentImageDigest = "sha256:image", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var handler = new DeliveryEvidenceCapabilityHandler(db, TimeProvider.System);
        async Task<CapabilityResult> Invoke(string capability, object payload)
        {
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, new RequestCapability { RequestId = "test", Capability = capability,
                Payload = JsonPayload.From(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, default)) results.Add(result);
            return Assert.Single(results);
        }
        var read = await Invoke(readCapability, new W.ReadBuildV2Request(scenario == "workstream-scan" ? null : scenario == "other-build" ? Guid.NewGuid() : build.Id, stream.Id));
        Assert.Equal(expected, read.Succeeded); Assert.Empty(await db.CoreOrganizationUsers.ToArrayAsync());
        if (!expected) { Assert.Equal(0, build.Attempt); return; }
        Assert.True((await Invoke(W.DeliveryEvidenceCapabilityNames.BuildClaimV1, new W.ClaimBuildRequest(build.Id, build.Revision, TimeSpan.FromMinutes(5), "claim"))).Succeeded);
        Assert.Equal(1, build.Attempt);
        Assert.True((await Invoke(W.DeliveryEvidenceCapabilityNames.BuildHeartbeatV1, new W.HeartbeatBuildRequest(build.Id, build.ClaimId!.Value, build.Revision, TimeSpan.FromMinutes(5), "heartbeat"))).Succeeded);
        Assert.True((await Invoke(W.DeliveryEvidenceCapabilityNames.BuildReportV2, new W.ReportBuildV2Request(build.Id, build.ClaimId!.Value, build.Revision,
            W.DeliveryBuildStatuses.Failed, [], new W.BuildExecutionProvenance(package.AgentId, package.Version, definition.DefinitionDigest, "sha256:image",
                JsonSerializer.SerializeToElement(new { }), "", [], build.SourceRevision, "", []), [], "fixture-failed", "Real fixture failure", "report"))).Succeeded);
        Assert.Equal(W.DeliveryBuildStatuses.Failed, build.Status);
    }
}
