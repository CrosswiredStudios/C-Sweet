using CSweet.Application.Localization;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Localization;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkspaceLanguagePolicyTests
{
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public void English_adds_no_instruction_and_other_cultures_do()
    {
        Assert.Null(AgentLanguageInstructions.For(null));
        Assert.Null(AgentLanguageInstructions.For("en"));
        Assert.Null(AgentLanguageInstructions.For("en-US"));
        var german = AgentLanguageInstructions.For("de-AT");
        Assert.NotNull(german);
        Assert.Contains("Workspace language: German", german, StringComparison.Ordinal);
        Assert.Contains("JSON keys", german, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_scope_and_empty_database_fall_back_to_english()
    {
        WorkspaceLanguagePolicy.Invalidate();
        await using var db = CreateDb();
        Assert.Equal("en", await WorkspaceLanguagePolicy.ResolveAsync(db, null, CancellationToken.None));
        Assert.Equal("en", await WorkspaceLanguagePolicy.ResolveAsync(db, Guid.NewGuid().ToString("D"), CancellationToken.None));
    }

    [Fact]
    public async Task User_scoped_agents_follow_that_user()
    {
        WorkspaceLanguagePolicy.Invalidate();
        await using var db = CreateDb();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "ada", PreferredLanguage = "ja" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Assert.Equal("ja", await WorkspaceLanguagePolicy.ResolveAsync(db, $"user:{user.Id:D}", CancellationToken.None));
    }

    [Fact]
    public async Task Business_agents_follow_the_owner_then_the_root_administrator()
    {
        WorkspaceLanguagePolicy.Invalidate();
        await using var db = CreateDb();
        var root = new ApplicationUser { Id = Guid.NewGuid(), UserName = "root", IsInitialAdministrator = true, PreferredLanguage = "fr" };
        var owner = new ApplicationUser { Id = Guid.NewGuid(), UserName = "owner", PreferredLanguage = "pt-BR" };
        db.Users.AddRange(root, owner);
        var organizationId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = owner.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = root.Id, DisplayName = "Viewer",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Viewer, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.Equal("pt-BR", await WorkspaceLanguagePolicy.ResolveAsync(db, organizationId.ToString("D"), CancellationToken.None));
        // A business without linked members falls back to the root administrator.
        Assert.Equal("fr", await WorkspaceLanguagePolicy.ResolveAsync(db, Guid.NewGuid().ToString("D"), CancellationToken.None));
    }

    [Fact]
    public async Task Cache_is_dropped_on_invalidate()
    {
        WorkspaceLanguagePolicy.Invalidate();
        await using var db = CreateDb();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "ada", PreferredLanguage = "es" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var scope = $"user:{user.Id:D}";
        Assert.Equal("es", await WorkspaceLanguagePolicy.ResolveAsync(db, scope, CancellationToken.None));
        user.PreferredLanguage = "ko";
        await db.SaveChangesAsync();
        Assert.Equal("es", await WorkspaceLanguagePolicy.ResolveAsync(db, scope, CancellationToken.None));
        WorkspaceLanguagePolicy.Invalidate();
        Assert.Equal("ko", await WorkspaceLanguagePolicy.ResolveAsync(db, scope, CancellationToken.None));
    }
}
