using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class OfficeSchemaRepairTests
{
    [Fact]
    public void ArtifactGrantSchemaRepair_IsIdempotentAndDiscovered()
    {
        using var db = new CSweetDbContext(
            new DbContextOptionsBuilder<CSweetDbContext>()
                .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
                .Options);

        Assert.Contains(
            "20260813171938_RepairExecutionArtifactGrantColumns",
            db.Database.GetMigrations());
        Assert.Contains(
            "20260815204140_AddAssistedLocalOfficeSetup",
            db.Database.GetMigrations());
        Assert.Contains(
            "20260815235118_TrackLocalOfficeSetupLaunch",
            db.Database.GetMigrations());
        Assert.Contains(
            "20260817050354_AddLocalOfficeRecovery",
            db.Database.GetMigrations());

        var root = RepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.Infrastructure", "Persistence", "Migrations",
            "20260813171938_RepairExecutionArtifactGrantColumns.cs"));
        Assert.Contains("ADD COLUMN IF NOT EXISTS", migration, StringComparison.Ordinal);
        Assert.Contains("ArtifactGrantTransferHash", migration, StringComparison.Ordinal);
        Assert.Contains("ArtifactGrantInUseUntil", migration, StringComparison.Ordinal);
        Assert.Contains("ArtifactGrantConsumedAt", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficeApprovalUi_AttributesApiFailureToControlPlane()
    {
        var razor = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.UI", "Setup", "AgentHostOnboardingStep.razor"));

        Assert.Contains("catch (ApiClientException exception)", razor, StringComparison.Ordinal);
        Assert.Contains("control plane could not complete approval", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not be approved. Check the host", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalOfficeWizard_UsesFourProfilesAndRemovesLegacyManualActions()
    {
        var root = RepositoryRoot();
        var razor = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Setup", "AgentHostOnboardingStep.razor"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "CSweet.UI", "Setup", "AgentHostOnboardingStep.razor.css"));

        Assert.Contains("Where should your agents work?", razor, StringComparison.Ordinal);
        Assert.Contains("new(\"small\", \"Small\"", razor, StringComparison.Ordinal);
        Assert.Contains("new(\"balanced\", \"Balanced\"", razor, StringComparison.Ordinal);
        Assert.Contains("new(\"performance\", \"Performance\"", razor, StringComparison.Ordinal);
        Assert.Contains("new(\"custom\", \"Custom\"", razor, StringComparison.Ordinal);
        Assert.Contains("@if (_capacityPreset == \"custom\")", razor, StringComparison.Ordinal);
        Assert.Contains("preset.MemoryMb >= 16 * 1024", razor, StringComparison.Ordinal);
        Assert.Contains("Create an Office", razor, StringComparison.Ordinal);
        Assert.Contains("Disabled=\"@(_busy || !LocalAllocationIsValid)\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose another machine", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows reserved", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C-Sweet Office allocation", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Download installer", razor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Open C-Sweet Office", razor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Try Windows setup again", razor, StringComparison.Ordinal);
        Assert.Contains("Why administrator approval is needed", razor, StringComparison.Ordinal);
        Assert.Contains("Continue to Windows approval", razor, StringComparison.Ordinal);
        Assert.Contains("RequestAdministratorApprovalAsync", razor, StringComparison.Ordinal);
        Assert.Contains("Complete the Windows administrator prompt if it is still open", razor, StringComparison.Ordinal);
        Assert.Contains("GetActiveLocalOfficeSetupSessionAsync", razor, StringComparison.Ordinal);
        Assert.Contains("disabled=\"@(_busy || LocalSetupLocksLocation)\"", razor, StringComparison.Ordinal);
        Assert.Contains("if (target == \"remote\" && LocalSetupLocksLocation) return;", razor, StringComparison.Ordinal);
        Assert.Contains("RefreshLocalOfficeSetupSessionHandoffAsync", razor, StringComparison.Ordinal);
        Assert.Contains("private int LocalStepNumber => _localSession is null", razor, StringComparison.Ordinal);
        Assert.Contains("_localSession.State == \"ready\"", razor, StringComparison.Ordinal);
        Assert.Contains("<span>Setup complete</span>", razor, StringComparison.Ordinal);
        Assert.Contains("_ => 2", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("saveLocalOfficeSetup", razor, StringComparison.Ordinal);
        Assert.Contains("LocalProgressEta", razor, StringComparison.Ordinal);
        Assert.Contains("Windows setup needs attention", razor, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(45)", razor, StringComparison.Ordinal);
        Assert.Contains("ActiveLocalProvisioning is null", razor, StringComparison.Ordinal);
        Assert.Contains("_localSession.PhaseKey == \"install\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("No download or command is required", razor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@media (max-width:", css, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalOfficeWizard_RendersBoundRecoveryActionsAndDestructiveConfirmation()
    {
        var razor = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.UI", "Setup", "AgentHostOnboardingStep.razor"));

        Assert.Contains("_localSession.State == \"recoveryrequired\"", razor, StringComparison.Ordinal);
        Assert.Contains("Reassign this Office", razor, StringComparison.Ordinal);
        Assert.Contains("RecoveryCanReconnect", razor, StringComparison.Ordinal);
        Assert.Contains("no assignments or Office-owned virtual machines remain", razor, StringComparison.Ordinal);
        Assert.Contains("Remove Office", razor, StringComparison.Ordinal);
        Assert.Contains("cannot be recovered", razor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SelectLocalOfficeRecoveryAsync", razor, StringComparison.Ordinal);
        var fleet = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.Infrastructure", "Setup", "ExecutionFleetService.cs"));
        Assert.Contains("CSweet:ExecutionGateway:BootstrapUrl", fleet, StringComparison.Ordinal);
        Assert.Contains("uri.IsLoopback", fleet, StringComparison.Ordinal);
        Assert.Contains("Office reassignment is ready.", fleet, StringComparison.Ordinal);
        Assert.Contains("ContinueAfterRemovedOfficeAsync", razor, StringComparison.Ordinal);
        Assert.Contains("removed.AllocatableCpuCount", razor, StringComparison.Ordinal);
        Assert.Contains("removed.AllocatableMemoryMb", razor, StringComparison.Ordinal);
        Assert.Contains("removed.AllocatableDiskMb", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("or \"removed\")", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistedOfficeLauncherForwardsPinnedCertificateWithoutInteractiveTrust()
    {
        var launcher = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.Api", "Setup", "Start-CSweetDevelopmentOfficeSetup.ps1"));

        Assert.Contains("controlPlaneCertificateSha256", launcher, StringComparison.Ordinal);
        Assert.Contains("Get-OptionalObjectProperty $redemption 'controlPlaneCertificateSha256'", launcher, StringComparison.Ordinal);
        Assert.Contains("-ControlPlaneCertificateSha256 $controlPlaneCertificateSha256", launcher, StringComparison.Ordinal);
        Assert.Contains("local-sessions/preflight", launcher, StringComparison.Ordinal);
        Assert.Contains("PhaseKey connect-control-plane", launcher, StringComparison.Ordinal);
        Assert.Contains("$progressHelperLoaded = $false", launcher, StringComparison.Ordinal);
        Assert.Contains("$redemption = $null", launcher, StringComparison.Ordinal);
        Assert.Contains("ServerCertificateValidationCallback", launcher, StringComparison.Ordinal);
        Assert.Contains("$expectedCertificateSha256", launcher, StringComparison.Ordinal);
        Assert.Contains("GetRawCertData()", launcher, StringComparison.Ordinal);
        Assert.Contains("$failureAlreadyReported", launcher, StringComparison.Ordinal);
        Assert.Contains("Windows setup started, but could not continue", launcher, StringComparison.Ordinal);
        Assert.Contains("Secure connection to C-Sweet failed", launcher, StringComparison.Ordinal);
        Assert.Contains("certificate-pinned connection", launcher, StringComparison.Ordinal);
        Assert.Contains("$originUri.IsLoopback", launcher, StringComparison.Ordinal);
        Assert.True(launcher.IndexOf("local-sessions/preflight", StringComparison.Ordinal) <
            launcher.IndexOf("local-sessions/redeem", StringComparison.Ordinal));
        Assert.Contains("existingInstallationAction", launcher, StringComparison.Ordinal);
        Assert.Contains("setupReceipt", launcher, StringComparison.Ordinal);
        Assert.Contains("-ProgressWorkflow 'developer-bootstrap'", launcher, StringComparison.Ordinal);
        Assert.Contains("Starting your fresh Office", launcher, StringComparison.Ordinal);
        Assert.Contains("The old Office was removed, but C-Sweet could not start the fresh installation.", launcher, StringComparison.Ordinal);
        Assert.Contains("existingInstallationState = 'none'", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Return to C-Sweet to install a new Office", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistedOfficeLauncherInstallsTheRedeemedCapacityAllocation()
    {
        var launcher = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "CSweet.Api", "Setup", "Start-CSweetDevelopmentOfficeSetup.ps1"));

        Assert.Contains("Get-RequiredPositiveIntProperty $redemption 'allocatableCpuCount'", launcher, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredPositiveIntProperty $redemption 'allocatableMemoryMb'", launcher, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredPositiveIntProperty $redemption 'allocatableDiskMb'", launcher, StringComparison.Ordinal);
        Assert.Contains("Get-RequiredPositiveIntProperty $redemption 'maximumConcurrentWorkloads'", launcher, StringComparison.Ordinal);
        Assert.Contains("-SkipInstall", launcher, StringComparison.Ordinal);
        Assert.Contains("-AssistedSetupSessionId $sessionId", launcher, StringComparison.Ordinal);
        Assert.Contains("-AllocatableCpuCount $allocatableCpuCount", launcher, StringComparison.Ordinal);
        Assert.Contains("-AllocatableMemoryMb $allocatableMemoryMb", launcher, StringComparison.Ordinal);
        Assert.Contains("-AllocatableDiskMb $allocatableDiskMb", launcher, StringComparison.Ordinal);
        Assert.Contains("-MaximumConcurrentWorkloads $maximumConcurrentWorkloads", launcher, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CSweet.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
