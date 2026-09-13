using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.UI.Components.Employees;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class EmployeePersonalBoardTests
{
    [Fact]
    public void PreviewKeeps150TextElementsWithoutSplittingEmojiOrCombiningCharacters()
    {
        var text = string.Concat(Enumerable.Repeat("👩🏽‍💻e\u0301", 76));
        var expected = string.Concat(Enumerable.Repeat("👩🏽‍💻e\u0301", 75)) + "…";
        Assert.Equal(expected, PersonalBoardPresentation.Excerpt(text));
        Assert.Equal(new string('a', 150), PersonalBoardPresentation.Excerpt(new string('a', 150)));
        Assert.Equal("", PersonalBoardPresentation.Excerpt(null));
    }

    [Fact]
    public void KnownEnvelopeShowsHumanRequestAndOtherDescriptionsStayIntact()
    {
        const string payload = "{\"kind\":\"csweet-direct-development-v1\",\"request\":\"Build Tetris\",\"environmentId\":\"abc\"}";
        Assert.Equal("Build Tetris", PersonalBoardPresentation.Description(payload));
        Assert.Equal("{bad json", PersonalBoardPresentation.Description("{bad json"));
        const string other = "{\"request\":\"Business data\"}";
        Assert.Equal(other, PersonalBoardPresentation.Description(other));
    }

    [Fact]
    public void FiltersCombineStatusPriorityAndArchiveWithoutLosingCompletedTickets()
    {
        var item = Item() with { Status = Wire.PersonalTodoStatuses.Completed, Priority = "Normal" };
        Assert.True(PersonalBoardPresentation.Matches(item, new HashSet<string>(), "", "current"));
        Assert.True(PersonalBoardPresentation.Matches(item, new HashSet<string> { item.Status }, "Medium", "current"));
        Assert.False(PersonalBoardPresentation.Matches(item, new HashSet<string> { Wire.PersonalTodoStatuses.Blocked }, "", "all"));
        Assert.False(PersonalBoardPresentation.Matches(item, new HashSet<string>(), "High", "all"));
        item = item with { ArchivedAt = DateTimeOffset.UtcNow };
        Assert.False(PersonalBoardPresentation.Matches(item, new HashSet<string>(), "", "current"));
        Assert.True(PersonalBoardPresentation.Matches(item, new HashSet<string>(), "", "all"));
        Assert.True(PersonalBoardPresentation.Matches(item, new HashSet<string>(), "", "archived"));
    }

    [Theory]
    [InlineData("Ready", "Running", true, false, true)]
    [InlineData("Completed", "Backlog", true, false, true)]
    [InlineData("Blocked", "Ready", false, true, true)]
    [InlineData("Backlog", "Ready", false, true, true)]
    [InlineData("Ready", "Running", false, true, false)]
    [InlineData("Running", "Completed", false, true, false)]
    [InlineData("Blocked", "Ready", false, false, false)]
    [InlineData("Ready", "Bogus", true, true, false)]
    public void DragAndDialogUseSamePermissionRules(string from, string to, bool owner, bool manager, bool expected)
    {
        var item = Item() with { Status = from };
        Assert.Equal(expected, PersonalBoardPresentation.CanMove(item, to, owner, manager));
        Assert.False(PersonalBoardPresentation.CanMove(item with { ArchivedAt = DateTimeOffset.UtcNow }, to, owner, manager));
    }

    [Fact]
    public async Task RenderedBoardShowsPreviewAndNoDebugBannerOrCheckboxes()
    {
        var item = Item() with { Description = new string('a', 150) + "HIDDEN_END" };
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton(new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) }))
            { BaseAddress = new Uri("http://localhost") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<EmployeePersonalBoard>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(EmployeePersonalBoard.Board)] = Board(item), [nameof(EmployeePersonalBoard.CanAdd)] = true }));
            return result.ToHtmlString();
        });
        Assert.Contains("Build Tetris", html);
        Assert.Contains(new string('a', 150), html);
        Assert.DoesNotContain("HIDDEN_END", html);
        Assert.DoesNotContain("operational work queue", html);
        Assert.DoesNotContain("type=\"checkbox\"", html);
        Assert.Contains("Filter", html);
        Assert.Contains("New task", html);
        Assert.Contains("draggable=\"false\"", html);
    }

    [Fact]
    public async Task EditingAndMovingUsesReturnedRevisionAndKeepsMentionInputs()
    {
        var item = Item() with
        {
            Title = "@Daniel Build Tetris",
            MentionSpans = [new Wire.WorkItemMentionSpan(Guid.NewGuid(), "Daniel", "Human", Wire.WorkItemMentionFields.Title, 0, 7, "@Daniel")]
        };
        long? statusRevision = null;
        var refreshes = 0;
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(request);
            if (request.Method == HttpMethod.Put)
            {
                var update = await request.Content!.ReadFromJsonAsync<Wire.UpdatePersonalTodoItemRequest>();
                Assert.Equal(item.Revision, update!.ExpectedRevision);
                Assert.Single(update.Mentions!);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(item with { Priority = "High", Revision = item.Revision + 1 }) };
            }
            var status = await request.Content!.ReadFromJsonAsync<Wire.SetHumanPersonalTodoStatusRequest>();
            statusRevision = status!.ExpectedRevision;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(item with { Priority = "High", Status = "Running", Revision = item.Revision + 2 }) };
        })) { BaseAddress = new Uri("http://localhost") };
        var component = Component(item, http);
        ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EmployeePersonalBoard.RefreshRequested)] = EventCallback.Factory.Create(new object(), () => refreshes++) }).SetParameterProperties(component);
        Invoke(component, "OpenDetails", item);
        Set(component, "_editPriority", "High"); Set(component, "_editStatus", "Running");
        await InvokeTask(component, "SaveAsync");
        Assert.Equal(item.Revision + 1, statusRevision);
        Assert.Equal(2, requests.Count); Assert.Equal(1, refreshes);
        Assert.False(Get<bool>(component, "_dialogOpen"));
    }

    [Fact]
    public async Task DroppingIntoBlockedPromptsBeforeSendingAndFailedSaveKeepsDialogOpen()
    {
        var item = Item(); var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        { calls++; return new(HttpStatusCode.Conflict); })) { BaseAddress = new Uri("http://localhost") };
        var component = Component(item, http);
        Invoke(component, "StartDrag", item);
        await InvokeTask(component, "DropAsync", "Blocked");
        Assert.True(Get<bool>(component, "_dialogOpen")); Assert.Equal(0, calls);
        await InvokeTask(component, "SaveAsync"); Assert.Equal(0, calls);
        Set(component, "_blockReason", "Waiting for environment");
        await InvokeTask(component, "SaveAsync");
        Assert.Equal(1, calls); Assert.True(Get<bool>(component, "_dialogOpen"));
        Assert.Contains("Close and reopen", Get<string>(component, "_error"));
        Assert.Equal("Ready", Get<Wire.PersonalTodoItem>(component, "_selectedItem").Status);
    }

    private static EmployeePersonalBoard Component(Wire.PersonalTodoItem item, HttpClient http)
    {
        var component = new EmployeePersonalBoard { Http = http };
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(EmployeePersonalBoard.Board)] = Board(item),
            [nameof(EmployeePersonalBoard.CanExecute)] = true,
            [nameof(EmployeePersonalBoard.CanAdd)] = true,
            [nameof(EmployeePersonalBoard.EmployeeId)] = item.OwnerOrganizationUserId,
            [nameof(EmployeePersonalBoard.OrganizationId)] = Guid.NewGuid()
        }).SetParameterProperties(component);
        return component;
    }
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Task InvokeTask(object target, string name, params object[] args) => (Task)Invoke(target, name, args)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Wire.PersonalTodoBoard Board(Wire.PersonalTodoItem item) => new(item.BoardId, item.OwnerOrganizationUserId, "Daniel Kim", null, null, 1, [item]);
    private static Wire.PersonalTodoItem Item() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Manager", "Build Tetris", "Description", "Ready", "Medium", 0, 4, null, null, null, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(request => Task.FromResult(respond(request))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
