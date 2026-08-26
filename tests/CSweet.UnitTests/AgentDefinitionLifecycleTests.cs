using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentDefinitionLifecycleTests
{
    [Fact]
    public async Task Import_QueuesOnlyBuilderAndCreatesNoBusinessOrRuntimeRows()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Previewed, requiredConfiguration: false);
        await db.SaveChangesAsync();

        var definition = await new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db))
            .ImportAsync(package.Id, Request("AlwaysOn"));

        Assert.Equal(AgentDefinitionStatus.Building.ToString(), definition.Status);
        Assert.Single(await db.AgentDefinitions.ToListAsync());
        Assert.Single(await db.AgentBuildJobs.ToListAsync());
        Assert.Empty(await db.AgentInstallations.ToListAsync());
        Assert.Empty(await db.AgentSchedules.ToListAsync());
        Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task BuiltDefinition_WithMissingRequiredDefault_IsNotHireable()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: true);
        package.PackageDigest = $"sha256:{new string('a', 64)}";
        package.ArtifactSignature = "test-signature";
        await db.SaveChangesAsync();

        var definition = await new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db))
            .ImportAsync(package.Id, Request("OnDemand"));

        Assert.False(definition.IsAvailableForHire);
        Assert.Equal(AgentDefinitionStatus.NeedsConfiguration.ToString(), definition.Status);
        Assert.Empty(await db.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task RetryBuild_QueuesTheDefinitionsPackageInsteadOfLookingForAnInstallation()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Failed, requiredConfiguration: false);
        var definition = SeedDefinition(db, package, ActivationMode.AlwaysOn);
        definition.Status = AgentDefinitionStatus.BuildFailed;
        definition.IsAvailableForHire = false;
        var failedJob = new AgentBuildJob
        {
            Id = Guid.NewGuid(), PackageVersionId = package.Id, PackageVersion = package,
            Attempt = 1, QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        failedJob.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        db.AgentBuildJobs.Add(failedJob);
        await db.SaveChangesAsync();
        var builds = new RecordingBuildService(db);
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), builds);

        var result = await service.RetryBuildAsync(definition.Id);

        Assert.Equal(package.Id, builds.QueuedPackageVersionId);
        Assert.Equal(AgentDefinitionStatus.Building.ToString(), result.Status);
        Assert.False(result.IsAvailableForHire);
        Assert.Equal("Queued", result.Build?.Status);
        Assert.Equal(2, result.Build?.Attempt);
        Assert.Empty(await db.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task Update_UnbuiltDefinitionLeavesExistingHiresPinnedUntilThePackageIsVerified()
    {
        await using var db = CreateDb();
        var current = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        current.PackageDigest = $"sha256:{new string('a', 64)}";
        current.ArtifactSignature = "current-signature";
        var definition = SeedDefinition(db, current, ActivationMode.OnDemand);
        var hire = RuntimeInstallation(current, Guid.NewGuid().ToString("D"));
        hire.AgentDefinitionId = definition.Id;
        definition.Installations.Add(hire);
        db.AgentInstallations.Add(hire);
        var update = CreateUpdatePackage(current, "1.1.0");
        db.AgentPackageVersions.Add(update);
        await db.SaveChangesAsync();
        var builds = new RecordingBuildService(db);
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), builds);

        var result = await service.UpdateAsync(definition.Id, new UpdateAgentDefinitionRequest(update.Id));

        Assert.Equal(update.Id, result.PackageVersionId);
        Assert.Equal("1.1.0", result.AgentVersion);
        Assert.Equal(AgentDefinitionStatus.Building.ToString(), result.Status);
        Assert.Equal(update.Id, builds.QueuedPackageVersionId);
        Assert.Equal(current.Id, (await db.AgentInstallations.SingleAsync()).PackageVersionId);
    }

    [Fact]
    public async Task Update_BuiltDefinitionDeploysToEveryHireAndRevokesOldRuntimeSessions()
    {
        await using var db = CreateDb();
        var current = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        current.PackageDigest = $"sha256:{new string('a', 64)}";
        current.ArtifactSignature = "current-signature";
        var definition = SeedDefinition(db, current, ActivationMode.AlwaysOn);
        var firstHire = RuntimeInstallation(current, Guid.NewGuid().ToString("D"));
        var secondHire = RuntimeInstallation(current, Guid.NewGuid().ToString("D"));
        firstHire.AgentDefinitionId = definition.Id;
        secondHire.AgentDefinitionId = definition.Id;
        definition.Installations.Add(firstHire);
        definition.Installations.Add(secondHire);

        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = firstHire.Id,
            AgentInstallation = firstHire, QueuedAt = now
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now);
        runtime.TransitionTo(AgentRuntimeStatus.Running, now);
        firstHire.RuntimeInstances.Add(runtime);
        var session = new McpAgentSession
        {
            Id = Guid.NewGuid(), RuntimeInstanceId = runtime.Id, RuntimeInstance = runtime,
            TickId = runtime.TickId, AgentInstallationId = firstHire.Id,
            AgentInstallation = firstHire, OrganizationId = firstHire.BusinessId,
            PackageVersionId = current.Id, PackageDigest = current.PackageDigest,
            GrantRevision = firstHire.Grant!.GrantRevision, AccessTokenHash = Guid.NewGuid().ToString("N"),
            EstablishedAt = now, LastRenewedAt = now, ExpiresAt = now.AddHours(1)
        };

        var update = CreateUpdatePackage(current, "1.1.0");
        var updateManifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(update.ManifestJson)!;
        updateManifest["requires"] = new[]
        {
            new { name = "work.sprint.read", scope = "team", purpose = "Verify planned sprint state." }
        };
        update.ManifestJson = JsonSerializer.Serialize(updateManifest);
        update.Status = AgentPackageVersionStatus.Built;
        update.PackageDigest = $"sha256:{new string('b', 64)}";
        update.ArtifactSignature = "updated-signature";
        db.AddRange(firstHire, secondHire, runtime, session, update);
        await db.SaveChangesAsync();
        var service = new AgentDefinitionService(
            db, new TestAuditEventWriter(), new RecordingBuildService(db));

        var result = await service.UpdateAsync(
            definition.Id, new UpdateAgentDefinitionRequest(update.Id));

        Assert.Equal("1.1.0", result.AgentVersion);
        var installations = await db.AgentInstallations.OrderBy(x => x.BusinessId).ToListAsync();
        Assert.Equal(2, installations.Count);
        Assert.All(installations, installation => Assert.Equal(update.Id, installation.PackageVersionId));
        Assert.All(installations, installation => Assert.Equal(2, installation.RevisionNumber));
        Assert.All(installations, installation => Assert.Contains(
            "work.sprint.read",
            JsonSerializer.Deserialize<string[]>(installation.Grant!.RequiredCapabilitiesJson)!));
        Assert.All(installations, installation => Assert.True(installation.Grant!.GrantRevision > 0));
        Assert.Equal(AgentConfigurationSyncStatus.Restarting,
            installations.Single(x => x.Id == firstHire.Id).ConfigurationSyncStatus);
        Assert.Equal(AgentConfigurationSyncStatus.PendingNextStart,
            installations.Single(x => x.Id == secondHire.Id).ConfigurationSyncStatus);
        Assert.NotNull((await db.McpAgentSessions.SingleAsync()).RevokedAt);
        Assert.Contains("global agent definition",
            (await db.McpAgentSessions.SingleAsync()).RevocationReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reconciliation_RepairsDefinitionDriftThatWasPersistedWhileAnOfficeWasOffline()
    {
        await using var db = CreateDb();
        var oldPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        oldPackage.PackageDigest = $"sha256:{new string('a', 64)}";
        oldPackage.ArtifactSignature = "old-signature";
        var newPackage = CreateUpdatePackage(oldPackage, "1.1.0");
        newPackage.Status = AgentPackageVersionStatus.Built;
        newPackage.PackageDigest = $"sha256:{new string('b', 64)}";
        newPackage.ArtifactSignature = "new-signature";
        var definition = SeedDefinition(db, newPackage, ActivationMode.AlwaysOn);
        var offlineHire = RuntimeInstallation(oldPackage, Guid.NewGuid().ToString("D"));
        offlineHire.AgentDefinitionId = definition.Id;
        definition.Installations.Add(offlineHire);
        db.AddRange(newPackage, offlineHire);
        await db.SaveChangesAsync();

        var changed = await new AgentDefinitionInstallationSynchronizer(
            db, new TestAuditEventWriter()).SynchronizeAsync();

        Assert.Equal(1, changed);
        Assert.Equal(newPackage.Id, (await db.AgentInstallations.SingleAsync()).PackageVersionId);
        Assert.Equal(AgentConfigurationSyncStatus.PendingNextStart,
            (await db.AgentInstallations.SingleAsync()).ConfigurationSyncStatus);
        Assert.Equal(0, await new AgentDefinitionInstallationSynchronizer(
            db, new TestAuditEventWriter()).SynchronizeAsync());
    }

    [Fact]
    public async Task SequentialRequesterAndProviderUpdates_MigrateVersionedCapabilityBinding()
    {
        await using var db = CreateDb();
        const string designV1 = "software-architecture.design.v1";
        const string designV2 = "software-architecture.design.v2";

        var requesterCurrent = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        requesterCurrent.AgentId = "com.example.product-manager";
        requesterCurrent.PackageDigest = $"sha256:{new string('a', 64)}";
        requesterCurrent.ArtifactSignature = "pm-current";
        SetManifestCapabilities(requesterCurrent, required: designV1);
        var requesterDefinition = SeedDefinition(db, requesterCurrent, ActivationMode.AlwaysOn);
        requesterDefinition.DefaultRequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { designV1 });
        var requester = RuntimeInstallation(requesterCurrent, Guid.NewGuid().ToString("D"));
        requester.AgentDefinitionId = requesterDefinition.Id;
        requester.Grant!.RequiredCapabilitiesJson = requesterDefinition.DefaultRequiredCapabilitiesJson;
        requesterDefinition.Installations.Add(requester);

        var providerCurrent = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        providerCurrent.AgentId = "com.example.architect";
        providerCurrent.PackageDigest = $"sha256:{new string('b', 64)}";
        providerCurrent.ArtifactSignature = "architect-current";
        SetManifestCapabilities(providerCurrent, provided: designV1);
        var providerDefinition = SeedDefinition(db, providerCurrent, ActivationMode.AlwaysOn);
        providerDefinition.DefaultProvidedCapabilitiesJson = JsonSerializer.Serialize(new[] { designV1 });
        var provider = RuntimeInstallation(providerCurrent, requester.BusinessId);
        provider.AgentDefinitionId = providerDefinition.Id;
        provider.Grant!.ProvidedCapabilitiesJson = providerDefinition.DefaultProvidedCapabilitiesJson;
        providerDefinition.Installations.Add(provider);

        var binding = new AgentCapabilityBinding
        {
            Id = Guid.NewGuid(), OrganizationId = requester.BusinessId,
            RequesterInstallationId = requester.Id, RequesterInstallation = requester,
            Capability = designV1,
            ProviderInstallationId = provider.Id, ProviderInstallation = provider,
            GrantRevision = requester.Grant.GrantRevision,
            ApprovedAt = DateTimeOffset.UtcNow
        };
        var requesterUpdate = CreateUpdatePackage(requesterCurrent, "2.0.0");
        requesterUpdate.Status = AgentPackageVersionStatus.Built;
        requesterUpdate.PackageDigest = $"sha256:{new string('c', 64)}";
        requesterUpdate.ArtifactSignature = "pm-updated";
        SetManifestCapabilities(requesterUpdate, required: designV2);
        var providerUpdate = CreateUpdatePackage(providerCurrent, "2.0.0");
        providerUpdate.Status = AgentPackageVersionStatus.Built;
        providerUpdate.PackageDigest = $"sha256:{new string('d', 64)}";
        providerUpdate.ArtifactSignature = "architect-updated";
        SetManifestCapabilities(providerUpdate, provided: designV2);
        db.AddRange(requester, provider, binding, requesterUpdate, providerUpdate);
        await db.SaveChangesAsync();

        var service = new AgentDefinitionService(
            db, new TestAuditEventWriter(), new RecordingBuildService(db));
        _ = await service.UpdateAsync(
            requesterDefinition.Id, new UpdateAgentDefinitionRequest(requesterUpdate.Id));

        var migrationHint = await db.AgentCapabilityBindings.SingleAsync();
        Assert.Equal(designV1, migrationHint.Capability);
        Assert.Null(migrationHint.RevokedAt);
        Assert.DoesNotContain(designV1,
            JsonSerializer.Deserialize<string[]>(requester.Grant.RequiredCapabilitiesJson)!);

        _ = await service.UpdateAsync(
            providerDefinition.Id, new UpdateAgentDefinitionRequest(providerUpdate.Id));

        var bindings = await db.AgentCapabilityBindings.OrderBy(x => x.ApprovedAt).ToListAsync();
        Assert.Equal(2, bindings.Count);
        Assert.NotNull(bindings.Single(x => x.Capability == designV1).RevokedAt);
        var active = bindings.Single(x => x.Capability == designV2);
        Assert.Null(active.RevokedAt);
        Assert.Equal(provider.Id, active.ProviderInstallationId);
        Assert.Equal(requester.Grant.GrantRevision, active.GrantRevision);
        Assert.Equal(AgentCapabilityBindingOrigins.VersionMigration, active.Origin);
    }

    [Fact]
    public async Task Reconciliation_BindsAnExistingRequesterWhenItsSoleProviderIsHiredLater()
    {
        await using var db = CreateDb();
        const string capability = "software-architecture.design.v2";
        var requesterPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        requesterPackage.AgentId = "com.example.pm";
        SetManifestCapabilities(requesterPackage, required: capability);
        var requester = RuntimeInstallation(requesterPackage, Guid.NewGuid().ToString("D"));
        requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { capability });
        var providerPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        providerPackage.AgentId = "com.example.architect";
        SetManifestCapabilities(providerPackage, provided: capability);
        var provider = RuntimeInstallation(providerPackage, requester.BusinessId);
        provider.Grant!.ProvidedCapabilitiesJson = JsonSerializer.Serialize(new[] { capability });
        db.AddRange(requester, provider);
        await db.SaveChangesAsync();

        var changed = await new AgentCapabilityBindingReconciler(db, new TestAuditEventWriter())
            .ReconcileAsync(requester.BusinessId);

        Assert.Equal(1, changed);
        var binding = await db.AgentCapabilityBindings.SingleAsync();
        Assert.Equal(requester.Id, binding.RequesterInstallationId);
        Assert.Equal(provider.Id, binding.ProviderInstallationId);
        Assert.Equal(capability, binding.Capability);
        Assert.Equal(AgentCapabilityBindingOrigins.AutomaticUnique, binding.Origin);
        Assert.NotNull(requester.Schedule!.NextAttentionReviewAt);
        Assert.Equal(0, await new AgentCapabilityBindingReconciler(db, new TestAuditEventWriter())
            .ReconcileAsync(requester.BusinessId));
    }

    [Fact]
    public async Task Reconciliation_DoesNotGuessWhenMultipleProvidersAreEligible()
    {
        await using var db = CreateDb();
        const string capability = "software-architecture.design.v2";
        var requesterPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        SetManifestCapabilities(requesterPackage, required: capability);
        var requester = RuntimeInstallation(requesterPackage, Guid.NewGuid().ToString("D"));
        requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { capability });
        var firstPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        var secondPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        SetManifestCapabilities(firstPackage, provided: capability);
        SetManifestCapabilities(secondPackage, provided: capability);
        var first = RuntimeInstallation(firstPackage, requester.BusinessId);
        var second = RuntimeInstallation(secondPackage, requester.BusinessId);
        first.Grant!.ProvidedCapabilitiesJson = JsonSerializer.Serialize(new[] { capability });
        second.Grant!.ProvidedCapabilitiesJson = JsonSerializer.Serialize(new[] { capability });
        db.AddRange(requester, first, second);
        await db.SaveChangesAsync();

        var changed = await new AgentCapabilityBindingReconciler(db, new TestAuditEventWriter())
            .ReconcileAsync(requester.BusinessId);

        Assert.Equal(0, changed);
        Assert.Empty(await db.AgentCapabilityBindings.ToListAsync());
    }

    [Fact]
    public async Task Remove_RejectsDefinitionsUsedByAgentEmployees()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        var definition = SeedDefinition(db, package, ActivationMode.OnDemand);
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example", CreatedAt = DateTimeOffset.UtcNow };
        var hire = RuntimeInstallation(package, organization.Id.ToString("D"));
        hire.AgentDefinitionId = definition.Id;
        definition.Installations.Add(hire);
        var employee = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, Organization = organization,
            DisplayName = "Researcher", EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true,
            AgentInstallationId = hire.Id, AgentInstallation = hire, CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(organization, hire, employee);
        await db.SaveChangesAsync();
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db));

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() => service.RemoveAsync(definition.Id));

        Assert.Contains("Researcher", exception.Message);
        Assert.NotNull(await db.AgentDefinitions.FindAsync(definition.Id));
    }

    [Fact]
    public async Task Remove_DeletesAnUnusedGlobalDefinition()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        var definition = SeedDefinition(db, package, ActivationMode.OnDemand);
        await db.SaveChangesAsync();
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db));

        var result = await service.RemoveAsync(definition.Id);

        Assert.Equal(definition.Id, result.DefinitionId);
        Assert.Empty(await db.AgentDefinitions.ToListAsync());
        Assert.Empty(await db.AgentPackageVersions.ToListAsync());
        Assert.Empty(await db.AgentPackageSources.ToListAsync());
    }

    [Fact]
    public async Task Remove_DetachesFailedHireOperationFromDefinitionThatNeverInstalled()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Failed, requiredConfiguration: false);
        var definition = SeedDefinition(db, package, ActivationMode.OnDemand);
        var operation = new AgentHireOperation
        {
            Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), OrganizationId = Guid.NewGuid(),
            AgentDefinitionId = definition.Id, Status = AgentHireOperationStatus.Failed,
            Error = "Package validation failed.", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AgentHireOperations.Add(operation);
        await db.SaveChangesAsync();
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db));

        await service.RemoveAsync(definition.Id);

        Assert.Null(operation.AgentDefinitionId);
        Assert.NotNull(operation.DismissedAt);
        Assert.Empty(await db.AgentDefinitions.ToListAsync());
    }

    [Theory]
    [InlineData(ActivationMode.AlwaysOn, 1)]
    [InlineData(ActivationMode.Scheduled, 0)]
    [InlineData(ActivationMode.OnDemand, 0)]
    public async Task Hiring_CreatesFreshBusinessInstallation_AndStartsOnlyAlwaysOn(
        ActivationMode activationMode, int expectedRuntimeRequests)
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        package.PackageDigest = $"sha256:{new string('b', 64)}";
        package.ArtifactSignature = "test-signature";
        var definition = SeedDefinition(db, package, activationMode);
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example", CreatedAt = DateTimeOffset.UtcNow };
        var manager = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(organization, manager);
        await db.SaveChangesAsync();
        var runtimes = new RecordingRuntimeManager();
        var service = new OrganizationUserService(db, new TestAuditEventWriter(), agentRuntimeManager: runtimes);

        var result = await service.CreateAsync(organization.Id, new CreateOrganizationUserRequest(
            "Agent", null, (int)OrganizationPermissionLevel.Contributor, (int)EmployeeType.Agent,
            ReportsToOrganizationUserId: manager.Id, AgentDefinitionId: definition.Id));

        Assert.True(result.Succeeded, result.Message);
        var installation = await db.AgentInstallations.Include(x => x.Schedule).Include(x => x.Configuration).SingleAsync();
        Assert.Equal(organization.Id.ToString("D"), installation.BusinessId);
        Assert.Equal(definition.Id, installation.AgentDefinitionId);
        Assert.Equal(activationMode, installation.Schedule!.ActivationMode);
        Assert.Equal("{}", installation.Configuration!.SettingsJson);
        Assert.Equal(installation.Id, result.OrganizationUser!.AgentInstallationId);
        Assert.Equal(expectedRuntimeRequests, runtimes.RequestCount);
        Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task RuntimeEligibility_RejectsUnassignedAgents_ButAllowsSystemServices()
    {
        await using var db = CreateDb();
        var agentPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        agentPackage.PackageDigest = $"sha256:{new string('e', 64)}";
        agentPackage.ArtifactSignature = "agent-signature";
        var definition = SeedDefinition(db, agentPackage, ActivationMode.AlwaysOn);
        var agentInstallation = RuntimeInstallation(agentPackage, "00000000-0000-0000-0000-000000000001");
        agentInstallation.AgentDefinitionId = definition.Id;
        agentInstallation.AgentDefinition = definition;
        definition.Installations.Add(agentInstallation);

        var servicePackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        servicePackage.AgentId = "com.example.system-service";
        servicePackage.PluginKind = PluginKind.Service;
        servicePackage.PackageDigest = $"sha256:{new string('f', 64)}";
        servicePackage.ArtifactSignature = "service-signature";
        var serviceInstallation = RuntimeInstallation(servicePackage, "system");
        serviceInstallation.Scope = PluginInstallationScope.System;
        db.AgentInstallations.AddRange(agentInstallation, serviceInstallation);
        await db.SaveChangesAsync();
        var configurations = new AgentInstallationConfigurationService(db, new TestAuditEventWriter());
        var eligibility = new AgentRuntimeEligibilityService(db, configurations);

        var denied = await eligibility.EvaluateAsync(agentInstallation.Id);
        var allowed = await eligibility.EvaluateAsync(serviceInstallation.Id);

        Assert.False(denied.IsEligible);
        Assert.Contains("active hired employee", denied.Reason);
        Assert.True(allowed.IsEligible);
        Assert.True(allowed.IsSystemService);
    }

    private static AgentPackageVersion SeedPackage(
        CSweetDbContext db, AgentPackageVersionStatus status, bool requiredConfiguration)
    {
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/example/agent",
            RepositoryOwner = "example", RepositoryName = "agent", DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        object[] configuration = requiredConfiguration
            ? [new { key = "apiRegion", type = "text", label = "API region", required = true, secret = false }]
            : [];
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = source.Id, PackageSource = source,
            AgentId = "com.example.agent", AgentName = "Example Agent", Version = "1.0.0",
            PublisherId = "example", PublisherName = "Example", RuntimeType = "dotnet-project",
            CommitSha = new string('c', 40), ManifestDigest = new string('d', 64),
            ManifestJson = JsonSerializer.Serialize(new
            {
                manifestVersion = "2.0", kind = "agent", id = "com.example.agent",
                name = "Example Agent", version = "1.0.0",
                publisher = new { id = "example", name = "Example" },
                runtime = new { type = "dotnet-project", projectPath = "src/Agent.csproj", targetFramework = "net10.0", defaultActivationMode = "OnDemand" },
                protocol = new { minimumVersion = "2.0", maximumVersion = "2.x" },
                provides = Array.Empty<object>(), requires = Array.Empty<object>(),
                events = new { subscribes = Array.Empty<string>() }, configuration,
                credentials = Array.Empty<object>(), webAccess = new { mode = "None", rules = Array.Empty<object>() }
            }),
            Status = status, ImportedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(source, package);
        return package;
    }

    private static AgentDefinition SeedDefinition(
        CSweetDbContext db, AgentPackageVersion package, ActivationMode activationMode)
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(), PackageSourceId = package.PackageSourceId, AgentId = package.AgentId,
            PackageVersionId = package.Id, PackageVersion = package, Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true, DefaultActivationMode = activationMode,
            DefaultTickFrequencySeconds = 3600, DefaultOverlapPolicy = OverlapPolicy.Skip,
            DefaultMaxRuntimeSeconds = 600, DefaultMemoryMb = 1024, DefaultCpuPercent = 50,
            CreatedAt = now, UpdatedAt = now
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(), AgentDefinitionId = definition.Id, SchemaVersion = "1",
            SettingsJson = "{}", Revision = 1, CreatedAt = now, UpdatedAt = now
        };
        db.AgentDefinitions.Add(definition);
        return definition;
    }

    private static AgentPackageVersion CreateUpdatePackage(AgentPackageVersion current, string version)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.ManifestJson)!;
        values["version"] = version;
        return new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = current.PackageSourceId,
            AgentId = current.AgentId, AgentName = current.AgentName, Version = version,
            PublisherId = current.PublisherId, PublisherName = current.PublisherName,
            RuntimeType = current.RuntimeType, ProjectPath = current.ProjectPath,
            TargetFramework = current.TargetFramework, CommitSha = new string('e', 40),
            ManifestDigest = new string('f', 64), ManifestJson = JsonSerializer.Serialize(values),
            Status = AgentPackageVersionStatus.Previewed, ImportedAt = DateTimeOffset.UtcNow
        };
    }

    private static void SetManifestCapabilities(
        AgentPackageVersion package,
        string? provided = null,
        string? required = null)
    {
        var manifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(package.ManifestJson)!;
        manifest["id"] = package.AgentId;
        manifest["version"] = package.Version;
        manifest["provides"] = provided is null
            ? Array.Empty<object>()
            :
            [
                new
                {
                    name = provided,
                    description = "Provide a versioned test capability.",
                    inputSchema = new { type = "object", additionalProperties = true },
                    outputSchema = new { type = "object", additionalProperties = true },
                    executionTimeoutSeconds = 30,
                    idempotency = "caller-key"
                }
            ];
        manifest["requires"] = required is null
            ? Array.Empty<object>()
            : [new { name = required, scope = "team", purpose = "Use the bound versioned test capability." }];
        package.ManifestJson = JsonSerializer.Serialize(manifest);
    }

    private static AgentInstallation RuntimeInstallation(AgentPackageVersion package, string businessId)
    {
        var now = DateTimeOffset.UtcNow;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id,
            PackageVersion = package, BusinessId = businessId, IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready,
            CreatedAt = now, UpdatedAt = now
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn, TickFrequencySeconds = 60,
            MaxRuntimeSeconds = 600, OverlapPolicy = OverlapPolicy.Skip, IsEnabled = true
        };
        installation.Grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id, MaxRuntimeSeconds = 600,
            MemoryMb = 1024, CpuPercent = 50, ApprovedAt = now
        };
        installation.Configuration = new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id, SchemaVersion = "1",
            SettingsJson = "{}", Revision = 1, CreatedAt = now, UpdatedAt = now
        };
        return installation;
    }

    private static InstallAgentRequest Request(string activationMode) => new(
        "ignored-global-definition", activationMode, 3600, "Skip", [], [], [], [], [], 600, 1024, 50);

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingRuntimeManager : IAgentRuntimeManager
    {
        public int RequestCount { get; private set; }
        public Task<bool> EnsureRuntimeQueuedAsync(Guid installationId, string reason, bool interactive = false,
            CancellationToken cancellationToken = default)
        { RequestCount++; return Task.FromResult(true); }
        public Task<bool> RestartRuntimeAsync(Guid installationId, string reason, bool interactive = false,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RecordingBuildService(CSweetDbContext db) : IAgentBuildService
    {
        public Guid? QueuedPackageVersionId { get; private set; }

        public async Task<Guid> QueueAsync(
            Guid packageVersionId,
            CancellationToken cancellationToken = default)
        {
            QueuedPackageVersionId = packageVersionId;
            var package = await db.AgentPackageVersions.Include(x => x.BuildJobs)
                .SingleAsync(x => x.Id == packageVersionId, cancellationToken);
            var job = new AgentBuildJob
            {
                Id = Guid.NewGuid(),
                PackageVersionId = packageVersionId,
                PackageVersion = package,
                Attempt = (package.BuildJobs.Max(x => (int?)x.Attempt) ?? 0) + 1,
                QueuedAt = DateTimeOffset.UtcNow
            };
            package.Status = AgentPackageVersionStatus.Approved;
            db.AgentBuildJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            return job.Id;
        }

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
