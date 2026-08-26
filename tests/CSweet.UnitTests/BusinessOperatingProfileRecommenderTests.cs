using CSweet.Contracts.Plugins;
using CSweet.UI.Services;

namespace CSweet.UnitTests;

public sealed class BusinessOperatingProfileRecommenderTests
{
    private static readonly IReadOnlyList<PluginConfigurationOption> Options =
    [
        new("general", "General"),
        new("game-studio", "Game Studio"),
        new("saas", "SaaS"),
        new("ecommerce", "E-commerce"),
        new("professional-services", "Professional Services"),
        new("media-content", "Media & Content")
    ];

    [Theory]
    [InlineData("Indie game studio", "Make a cooperative video game", "game-studio")]
    [InlineData("Technology", "A SaaS platform for dental offices", "saas")]
    [InlineData("Retail", "Operate an online store", "ecommerce")]
    [InlineData("Consulting", "Advise small businesses", "professional-services")]
    [InlineData("Media", "Build a YouTube content studio", "media-content")]
    [InlineData("Manufacturing", "Make better fasteners", "general")]
    public void Recommend_SelectsSupportedProfile(string industry, string mission, string expected)
    {
        Assert.Equal(expected, BusinessOperatingProfileRecommender.Recommend(industry, mission, Options));
    }

    [Fact]
    public void Recommend_NeverSelectsProfileNotDeclaredByAgent()
    {
        Assert.Equal("general", BusinessOperatingProfileRecommender.Recommend(
            "Game Studio", "Build games", [new("general", "General")]));
    }
}
