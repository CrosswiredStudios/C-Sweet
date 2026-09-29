using CSweet.Infrastructure.Communications;

namespace CSweet.UnitTests;

/// <summary>Work-item support pairs the exact assigned implementer with the team's technical lead.</summary>
public sealed class CoordinationSupportRoleTests
{
    [Theory]
    [InlineData("Software Architect", true)]
    [InlineData("Video Game Technical Director", true)]
    [InlineData("Video Game Producer", false)]
    [InlineData("Video Game QA", false)]
    public void Technical_lead_includes_the_game_technical_director(string role, bool expected) =>
        Assert.Equal(expected, AgentCoordinationService.IsTechnicalLeadRole(role));

    [Theory]
    [InlineData("Software Developer", true)]
    [InlineData("Video Game Engineer", true)]
    [InlineData("Video Game Creative Director", false)]
    public void Implementer_includes_game_engineers(string role, bool expected) =>
        Assert.Equal(expected, AgentCoordinationService.IsImplementerRole(role));
}
