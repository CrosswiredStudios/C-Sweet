using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class EmployeePortraitTests
{
    [Fact]
    public void MultipleHiresShareAgentPortraitRegardlessOfTheirCustomNames()
    {
        var installation1 = Guid.NewGuid(); var installation2 = Guid.NewGuid();
        var first = Person("My assistant", installation1);
        var second = Person("Operations lead", installation2);
        var human = Person("My assistant", installation1) with { EmployeeType = 0 };
        var missing = Person("Evelyn Brooks", Guid.NewGuid());
        var portraits = EmployeePortraits.Build([first, second, human, missing],
            [Installation(installation1), Installation(installation2)], [Agent()]);
        Assert.Equal(Agent().ImageUrl, portraits.Find(first.Id));
        Assert.Equal(Agent().ImageUrl, portraits.Find(second.Id));
        Assert.Equal(Agent().RoleName, portraits.FindRole(first.Id));
        Assert.Equal(Agent().RoleName, portraits.FindRole(second.Id));
        Assert.Null(portraits.FindRole(human.Id));
        Assert.Null(portraits.FindRole(missing.Id));
        Assert.Null(portraits.Find(human.Id));
        Assert.Null(portraits.Find(missing.Id));
        Assert.Null(portraits.Find(null));
    }

    [Fact]
    public void InstalledBrandingIsUsedWithoutCatalogLookup()
    {
        var installationId = Guid.NewGuid();
        var person = Person("Research lead", installationId);
        var installation = Installation(installationId) with
        {
            ImageUrl = "https://example.com/cached-agent.jpg",
            RoleName = "Research Lead",
            AccentColor = "#224466"
        };
        var portraits = EmployeePortraits.Build([person], [installation], []);
        Assert.Equal(installation.ImageUrl, portraits.Find(person.Id));
        Assert.Equal(installation.RoleName, portraits.FindRole(person.Id));
        Assert.Equal(installation.AccentColor, portraits.FindAccent(person.Id));
    }

    [Fact]
    public void RoleTitlesResolveCatalogKeysWithoutOverwritingCustomTitles()
    {
        var catalog = EmployeePortraits.Build([], [], [Agent() with
        {
            RoleKey = "game-technical-director", RoleName = "Video Game Technical Director"
        }]);
        Assert.Equal("Video Game Technical Director", catalog.RoleTitle("game-technical-director", "game-technical-director"));
        Assert.Equal("Engine Lead", catalog.RoleTitle("game-technical-director", "Engine Lead"));
        Assert.Equal("unknown-role", catalog.RoleTitle("unknown-role", "unknown-role"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///private.png")]
    [InlineData("_content/CSweet.UI/images/agents/../private.jpg")]
    public void UnsafeImagesAreNotUsed(string image)
    {
        var installation = Guid.NewGuid(); var person = Person("Assistant", installation);
        var portraits = EmployeePortraits.Build([person], [Installation(installation)], [Agent() with { ImageUrl = image }]);
        Assert.Null(portraits.Find(person.Id));
        Assert.Equal(Agent().RoleName, portraits.FindRole(person.Id));
    }

    private static OrganizationUserResponse Person(string name, Guid installation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, null, null, name, null, 1, 0, DateTimeOffset.UtcNow)
        { AgentInstallationId = installation };
    private static AgentInstallationResponse Installation(Guid id) =>
        JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"Id":"{{id}}","AgentId":"com.csweet.chief-of-staff"}""")!;
    private static AvailableAgent Agent() =>
        JsonSerializer.Deserialize<AvailableAgent>("""{"AgentId":"com.csweet.chief-of-staff","Name":"Evelyn Brooks","RoleName":"Chief of Staff","ImageUrl":"_content/CSweet.UI/images/agents/chief-of-staff-v1.jpg"}""")!;
}
