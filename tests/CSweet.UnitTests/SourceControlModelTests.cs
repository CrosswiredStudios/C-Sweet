using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CSweet.UnitTests;

public sealed class SourceControlModelTests
{
    [Fact]
    public void RepositoryConnectionForeignKeyIncludesOrganizationBoundary()
    {
        using var db = CreateDbContext();
        var repository = RequireEntity<SourceControlRepository>(db);
        var foreignKey = Assert.Single(repository.GetForeignKeys(), candidate =>
            candidate.PrincipalEntityType.ClrType == typeof(SourceControlConnection));

        Assert.Equal(
            [nameof(SourceControlRepository.OrganizationId), nameof(SourceControlRepository.ConnectionId)],
            foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(SourceControlConnection.OrganizationId), nameof(SourceControlConnection.Id)],
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void DeliveryRecordsUseTenantSafeCompositeForeignKeys()
    {
        using var db = CreateDbContext();

        AssertTenantForeignKey<SourceControlWorkspace, SourceControlRepository>(
            db,
            nameof(SourceControlWorkspace.RepositoryId));
        AssertTenantForeignKey<SourceControlPublication, SourceControlWorkspace>(
            db,
            nameof(SourceControlPublication.WorkspaceId));
        AssertTenantForeignKey<SourceControlValidation, SourceControlPublication>(
            db,
            nameof(SourceControlValidation.PublicationId));
        AssertTenantForeignKey<SourceControlMergeAuthorization, SourceControlPublication>(
            db,
            nameof(SourceControlMergeAuthorization.PublicationId));
        AssertTenantForeignKey<SourceControlMergeJob, SourceControlMergeAuthorization>(
            db,
            nameof(SourceControlMergeJob.LeadAuthorizationId));
    }

    [Fact]
    public void ConnectionDoesNotPersistCredentialMaterial()
    {
        using var db = CreateDbContext();
        var connection = RequireEntity<SourceControlConnection>(db);
        var properties = connection.GetProperties().ToDictionary(property => property.Name);

        Assert.DoesNotContain(properties.Keys, name =>
            name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ProtectedPayload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrimaryRepositoryIndexAllowsOnlyOneActivePrimaryPerTeam()
    {
        using var db = CreateDbContext();
        var policy = RequireEntity<TeamRepositoryPolicy>(db);
        var primaryIndex = Assert.Single(policy.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
            [
                nameof(TeamRepositoryPolicy.OrganizationId),
                nameof(TeamRepositoryPolicy.TeamId),
                nameof(TeamRepositoryPolicy.IsPrimary)
            ]));

        Assert.True(primaryIndex.IsUnique);
        Assert.Contains(nameof(TeamRepositoryPolicy.DisabledAt), primaryIndex.GetFilter());
    }

    [Fact]
    public void ApprovedTemplateAndProvisioningRequestRemainTenantBound()
    {
        using var db = CreateDbContext();
        AssertTenantForeignKey<SourceControlRepositoryTemplate, SourceControlConnection>(
            db,
            nameof(SourceControlRepositoryTemplate.ConnectionId));
        AssertTenantForeignKey<RepositoryProvisioningRequest, SourceControlRepositoryTemplate>(
            db,
            nameof(RepositoryProvisioningRequest.TemplateId));
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase($"source-control-model-{Guid.NewGuid():N}")
            .Options;
        return new CSweetDbContext(options);
    }

    private static IEntityType RequireEntity<TEntity>(CSweetDbContext db) where TEntity : class =>
        db.Model.FindEntityType(typeof(TEntity)) ??
        throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the EF model.");

    private static void AssertTenantForeignKey<TDependent, TPrincipal>(
        CSweetDbContext db,
        string resourceIdProperty)
        where TDependent : class
        where TPrincipal : class
    {
        var dependent = RequireEntity<TDependent>(db);
        var foreignKey = Assert.Single(dependent.GetForeignKeys(), candidate =>
            candidate.PrincipalEntityType.ClrType == typeof(TPrincipal) &&
            candidate.Properties.Any(property => property.Name == resourceIdProperty));

        Assert.Equal(
            ["OrganizationId", resourceIdProperty],
            foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            ["OrganizationId", "Id"],
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
    }
}
