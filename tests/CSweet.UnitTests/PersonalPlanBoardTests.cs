using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.UI.Components.Employees;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class EmployeePersonalBoardTests
{
    [Fact]
    public async Task PlanBoardRendersDistinctTypesAndNavigableChildDetails()
    {
        var epic = Item() with { Kind = "Epic", Title = "Tetris Clone MVP" };
        var story = epic with { Id = Guid.NewGuid(), Kind = "Story", Title = "Playable rules", ParentItemId = epic.Id, PlanRootId = epic.Id };
        var task = story with { Id = Guid.NewGuid(), Kind = "Task", Title = "Grid and pieces", ParentItemId = story.Id, AcceptanceCriteria = ["Grid has 10x20 cells"], Status = "Completed" };
        Assert.False(PersonalBoardPresentation.CanMove(task, "Ready", true, true));
        Assert.True(PersonalBoardPresentation.CanMove(epic, "Running", true, true));
        var board = Board(epic) with { Items = [epic, story, task] };
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddScoped<AppRealtimeState>();
        services.AddSingleton(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) })) { BaseAddress = new Uri("http://localhost") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<EmployeePersonalBoard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(EmployeePersonalBoard.Board)] = board }));
            var hierarchy = await renderer.RenderComponentAsync<PersonalWorkHierarchy>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(PersonalWorkHierarchy.Item)] = story, [nameof(PersonalWorkHierarchy.Items)] = board.Items }));
            return result.ToHtmlString() + hierarchy.ToHtmlString();
        });
        foreach (var kind in new[] { "Epic", "Story", "Task" }) Assert.Contains("aria-label=\"" + kind + "\"", html);
        Assert.Contains("Parent ticket", html);
        Assert.Contains("Child tickets", html);
        Assert.Contains("1 / 1 complete", html);
        Assert.Contains("Grid and pieces", html);
        Assert.Contains("Tetris Clone MVP", html);
    }

}
