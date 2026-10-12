using CSweet.Contracts.Core;
using CSweet.UI.Components.Employees;
using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class EmployeeWorkspaceRenderingTests
{
    [Fact]
    public async Task GraphLinksToExistingEmployeeRouteAndExposesStateSeparately()
    {
        var employee = People()[0];
        var html = await Render<EmployeeGraphNode>(new() { ["Employee"] = employee });
        Assert.Contains($"href=\"/organizations/{employee.Source.OrganizationId}/employees/{employee.Id}\"", html);
        Assert.Contains("Agent state: Online", html);
        Assert.Contains("Collapse Naomi Chen's reports", System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("employee-details", html);
        Assert.DoesNotContain("employee-team-chips", html);
    }

    [Fact]
    public async Task TeamWorkspaceLinksEveryPersonAndKeepsManagementScopedToManagers()
    {
        var people = People();
        var team = Team(people);
        foreach (var canManage in new[] { true, false })
        {
            var html = await Render<TeamClustersView>(new()
            {
                ["Employees"] = people,
                ["Directory"] = new TeamDirectoryResponse(people[0].Id, canManage, [team])
            });
            foreach (var employee in people)
                Assert.Contains($"href=\"/organizations/{employee.Source.OrganizationId}/employees/{employee.Id}\"", html);
            Assert.Equal(canManage, html.Contains("Create team"));
            Assert.Equal(canManage, html.Contains("Remove Gabriel Reyes from Video Game Team"));
            Assert.DoesNotContain("Remove Naomi Chen", html);
            Assert.DoesNotContain("revision", html);
            if (canManage && Environment.GetEnvironmentVariable("CSWEET_WORKSPACE_PREVIEW") is { } path)
            {
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path, "teams.html"), html);
                await File.WriteAllTextAsync(Path.Combine(path, "graph.html"), await Render<EmployeeGraphView>(new() { ["Employees"] = people }));
            }
        }
    }

    private static IReadOnlyList<EmployeeViewModel> People()
    {
        var org = Guid.Parse("fd2b9ab0-ffff-495d-9113-12df3cc8a7d0");
        var director = new OrganizationUserResponse(Guid.Parse("652c43a6-2bc5-41ce-b77b-c49e929e34dc"), org, null, null, null, "Naomi Chen", null, 1, 0, DateTimeOffset.UtcNow);
        var producer = new OrganizationUserResponse(Guid.NewGuid(), org, director.Id, null, null, "Gabriel Reyes", null, 1, 0, DateTimeOffset.UtcNow);
        return [new(director, director.DisplayName, "NC", "Creative Director", null, null, true, false, EmployeeRuntimeStatus.Online, 1, [producer.Id], false, false, false, []),
            new(producer, producer.DisplayName, "GR", "Video Game Producer", null, director.DisplayName, true, false, EmployeeRuntimeStatus.Transitional, 0, [], false, false, false, [])];
    }

    private static TeamSummaryResponse Team(IReadOnlyList<EmployeeViewModel> people) => new(
        Guid.NewGuid(), "game", "Video Game Team", "Building the next great game, together.", people[0].Id, people[0].Name,
        1, false, 2, 0, 2, people.Select((p, i) => new TeamMembershipResponse(Guid.NewGuid(), p.Id, p.Name, "Agent", null, p.RoleLabel, i == 0, DateTimeOffset.UtcNow, null)).ToArray());

    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
    private sealed class TestNavigation : NavigationManager { public TestNavigation() => Initialize("http://localhost/", "http://localhost/"); }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
