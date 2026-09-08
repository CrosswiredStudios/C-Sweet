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
        Assert.Null(portraits.Find(human.Id));
        Assert.Null(portraits.Find(missing.Id));
        Assert.Null(portraits.Find(null));
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
    }

    private static OrganizationUserResponse Person(string name, Guid installation) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, null, null, name, null, 1, 0, DateTimeOffset.UtcNow)
        { AgentInstallationId = installation };
    private static AgentInstallationResponse Installation(Guid id) =>
        JsonSerializer.Deserialize<AgentInstallationResponse>($$"""{"Id":"{{id}}","AgentId":"com.csweet.chief-of-staff"}""")!;
    private static AvailableAgent Agent() =>
        JsonSerializer.Deserialize<AvailableAgent>("""{"AgentId":"com.csweet.chief-of-staff","Name":"Evelyn Brooks","ImageUrl":"_content/CSweet.UI/images/agents/chief-of-staff-v1.jpg"}""")!;
}
