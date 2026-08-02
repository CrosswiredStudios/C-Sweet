using CSweet.Contracts.Core;
using CSweet.UI.Components.Hiring;

namespace CSweet.UnitTests;

public sealed class SuggestedTeamStructureGraphBuilderTests
{
    [Fact]
    public void Build_PlacesManagerProductManagerAndDirectRolesInOrder()
    {
        var request = Request(
            Role("quality", "Quality Engineer", 2, 2),
            Role("design", "Product Designer", 1, 1));

        var graph = SuggestedTeamStructureGraphBuilder.Build(
            request,
            " Product Manager ",
            "Chief of Staff");

        Assert.Equal("Chief of Staff", graph.ManagerDisplayName);
        Assert.Equal("Product Manager", graph.ProductManagerDisplayName);
        Assert.Equal(["design", "quality"], graph.RoleCohorts.Select(role => role.RoleKey));
        Assert.Empty(graph.RoleCohorts[0].Children);
    }

    [Fact]
    public void Build_PreservesNestedReportingAgainstTheParentCohort()
    {
        var request = Request(
            Role("engineering", "Engineering Lead", 2, 1),
            Role("developer", "Software Developer", 3, 2, "engineering"),
            Role("quality", "Quality Engineer", 1, 3, "developer"));

        var graph = SuggestedTeamStructureGraphBuilder.Build(request, null, null);

        Assert.Equal("Manager", graph.ManagerDisplayName);
        Assert.Equal("Product manager", graph.ProductManagerDisplayName);
        var engineering = Assert.Single(graph.RoleCohorts);
        Assert.Equal(2, engineering.Seats.Count);
        var developer = Assert.Single(engineering.Children);
        Assert.Equal("developer", developer.RoleKey);
        Assert.Equal(3, developer.Seats.Count);
        Assert.Equal("quality", Assert.Single(developer.Children).RoleKey);
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(5, 5, false)]
    [InlineData(6, 6, true)]
    [InlineData(100, 6, true)]
    public void Build_ExpandsUpToFiveSeatsAndAddsRemainder(
        int headcount,
        int expectedNodes,
        bool expectedRemainder)
    {
        var graph = SuggestedTeamStructureGraphBuilder.Build(
            Request(Role("developer", "Software Developer", headcount, 1)),
            "Product Manager",
            "Manager");

        var cohort = Assert.Single(graph.RoleCohorts);
        Assert.Equal(headcount, cohort.Headcount);
        Assert.Equal(expectedNodes, cohort.Seats.Count);
        Assert.Equal(expectedRemainder, cohort.Seats.Any(seat => seat.IsRemainder));
        if (headcount > 5)
            Assert.Equal($"+{headcount - 5} more", cohort.Seats[^1].Label);
    }

    [Fact]
    public void Build_UsesFullDesiredRolesRatherThanChangeDeltas()
    {
        var retained = Role("retained", "Retained Role", 1, 3);
        var added = Role("added", "Added Role", 1, 3);
        var removed = Role("removed", "Removed Role", 1, 1);
        var request = Request(retained, added) with
        {
            Deltas =
            [
                new ResourceChangeRoleDelta("Add", added, null),
                new ResourceChangeRoleDelta("Remove", removed, removed)
            ]
        };

        var graph = SuggestedTeamStructureGraphBuilder.Build(request, "PM", "Manager");

        Assert.Equal(["added", "retained"], graph.RoleCohorts.Select(role => role.RoleKey));
        Assert.DoesNotContain(graph.RoleCohorts, role => role.RoleKey == "removed");
    }

    private static ResourceChangeRole Role(
        string key,
        string title,
        int headcount,
        int priority,
        string? reportsToRoleKey = null) =>
        new(
            key,
            "Product",
            title,
            $"Own {title} outcomes.",
            headcount,
            priority,
            "Now",
            ["delivery"],
            false,
            reportsToRoleKey is null ? Guid.NewGuid() : null,
            reportsToRoleKey);

    private static ResourceChangeRequestResponse Request(params ResourceChangeRole[] roles) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Ship the product",
            "Build the smallest complete team.",
            1,
            roles,
            roles.Select(role => new ResourceChangeRoleDelta("Add", role, null)).ToArray(),
            [],
            [],
            null,
            "Pending",
            "DeliveredInChat",
            null,
            DateTimeOffset.UtcNow,
            null);
}
