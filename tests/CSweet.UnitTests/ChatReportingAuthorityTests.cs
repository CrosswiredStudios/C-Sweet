using CSweet.Api.Chat;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ChatReportingAuthorityTests
{
    [Theory]
    [InlineData("ancestor", true)]
    [InlineData("direct", true)]
    [InlineData("colleague", false)]
    [InlineData("self", false)]
    [InlineData("foreign", false)]
    [InlineData("inactive", false)]
    [InlineData("archived", false)]
    [InlineData("cycle", false)]
    public async Task Sender_authority_follows_only_the_active_same_organization_reporting_chain(string scenario, bool expected)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var sender = Guid.NewGuid(); var manager = Guid.NewGuid(); var target = Guid.NewGuid();
        var top = new OrganizationUser { Id = sender, OrganizationId = org, IsActive = true };
        var middle = new OrganizationUser { Id = manager, OrganizationId = org, IsActive = true, ReportsToOrganizationUserId = sender };
        var worker = new OrganizationUser { Id = target, OrganizationId = org, IsActive = true, ReportsToOrganizationUserId = manager };
        if (scenario == "foreign") top.OrganizationId = Guid.NewGuid();
        if (scenario == "inactive") middle.IsActive = false;
        if (scenario == "archived") top.ArchivedAt = DateTimeOffset.UtcNow;
        if (scenario == "cycle") middle.ReportsToOrganizationUserId = target;
        db.AddRange(top, middle, worker); await db.SaveChangesAsync();
        var caller = scenario switch { "colleague" => Guid.NewGuid(), "self" => target, "direct" => manager, _ => sender };
        Assert.Equal(expected, await ChatReportingAuthority.IsAncestorAsync(db, org, target, caller, default));
    }
}
