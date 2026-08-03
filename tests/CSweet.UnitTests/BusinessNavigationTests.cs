using CSweet.Contracts.Core;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;

namespace CSweet.UnitTests;

public sealed class BusinessNavigationTests
{
    [Fact]
    public void ResolveSelection_PrefersRouteThenPersistedThenFirstBusiness()
    {
        var first = Business("First");
        var persisted = Business("Persisted");
        var route = Business("Route");
        var businesses = new[] { first, persisted, route };

        Assert.Equal(route.Id, BusinessNavigation.ResolveSelection(
            $"organizations/{route.Id}/employees", persisted.Id, businesses));
        Assert.Equal(persisted.Id, BusinessNavigation.ResolveSelection(
            "settings/agents", persisted.Id, businesses));
        Assert.Equal(first.Id, BusinessNavigation.ResolveSelection(
            "settings/agents", Guid.NewGuid(), businesses));
    }

    [Fact]
    public void ResolveSelection_ReturnsNullForEmptyCollection()
    {
        Assert.Null(BusinessNavigation.ResolveSelection("", Guid.NewGuid(), []));
    }

    [Theory]
    [InlineData("organizations/{0}/command-center", "command-center")]
    [InlineData("organizations/{0}/employees", "employees")]
    [InlineData("organizations/{0}/employees/00000000-0000-0000-0000-000000000001/memory", "employees")]
    [InlineData("organizations/{0}/communications/00000000-0000-0000-0000-000000000001", "communications")]
    [InlineData("organizations/{0}/analytics", "analytics")]
    [InlineData("settings/security", "command-center")]
    public void SwitchDestination_PreservesSafeBusinessSection(string pathTemplate, string expectedSection)
    {
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        var path = string.Format(pathTemplate, currentId);

        Assert.Equal($"/organizations/{nextId}/{expectedSection}", BusinessNavigation.SwitchDestination(path, nextId));
    }

    [Fact]
    public void RouteParsing_HandlesAbsoluteStylePathQueryAndFragment()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, BusinessNavigation.OrganizationIdFromPath($"/organizations/{id}/employees?view=graph#team"));
        Assert.False(BusinessNavigation.IsEmployeePath($"organizations/{id}/communications/{Guid.NewGuid()}"));
        Assert.False(BusinessNavigation.IsEmployeePath($"organizations/{id}/command-center"));
    }

    [Fact]
    public void SettingsPages_ExposeCanonicalAndLegacyRoutes()
    {
        Assert.Contains("/organizations/{OrganizationId:guid}/communications", RoutesFor<CSweet.UI.Pages.Communications>());
        Assert.Contains("/organizations/{OrganizationId:guid}/communications/{ChatId:guid}", RoutesFor<CSweet.UI.Pages.Communications>());
        Assert.Contains("/organizations/{OrganizationId:guid}/analytics", RoutesFor<CSweet.UI.Pages.Analytics>());
        Assert.Contains("/settings/llm-providers", RoutesFor<CSweet.UI.Pages.LlmProviders>());
        Assert.Contains("/settings/agents", RoutesFor<CSweet.UI.Pages.Agents>());
        Assert.Contains("/settings/agents/marketplace", RoutesFor<CSweet.UI.Pages.AgentMarketplace>());
        Assert.Contains("/settings/agents/runtime", RoutesFor<CSweet.UI.Pages.AgentRuntimeSettings>());
        Assert.Contains("/settings/security", RoutesFor<CSweet.UI.Pages.AccountSecurity>());

        var legacyRoutes = RoutesFor<CSweet.UI.Pages.LegacyRouteRedirect>();
        Assert.Contains("/settings", legacyRoutes);
        Assert.Contains("/llm-providers", legacyRoutes);
        Assert.Contains("/agents", legacyRoutes);
        Assert.Contains("/configuration", legacyRoutes);
        Assert.Contains("/account/security", legacyRoutes);
        Assert.Contains("/organizations", legacyRoutes);
    }

    [Fact]
    public void SettingsPages_UseTheSettingsDialogLayout()
    {
        Type[] settingsPages =
        [
            typeof(CSweet.UI.Pages.LlmProviders),
            typeof(CSweet.UI.Pages.Agents),
            typeof(CSweet.UI.Pages.AgentMarketplace),
            typeof(CSweet.UI.Pages.AgentRuntimeSettings),
            typeof(CSweet.UI.Pages.Plugins),
            typeof(CSweet.UI.Pages.CommunicationSettings),
            typeof(CSweet.UI.Pages.CommunicationProviderSetup),
            typeof(CSweet.UI.Pages.AccountSecurity)
        ];

        Assert.All(settingsPages, page =>
            Assert.Equal(
                typeof(CSweet.UI.Layout.SettingsLayout),
                page.GetCustomAttributes(typeof(LayoutAttribute), inherit: false)
                    .Cast<LayoutAttribute>()
                    .Single()
                    .LayoutType));
    }

    private static IReadOnlyList<string> RoutesFor<T>() =>
        typeof(T).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(x => x.Template)
            .ToArray();

    private static OrganizationResponse Business(string name) => new(
        Guid.NewGuid(), name, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
