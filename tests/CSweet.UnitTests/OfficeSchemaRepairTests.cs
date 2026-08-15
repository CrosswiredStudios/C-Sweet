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

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "CSweet.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
