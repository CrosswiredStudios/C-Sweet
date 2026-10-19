using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.UI.Components.Projects;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class ProjectNameEditorTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Editor_cancels_without_writing_and_submits_the_revision_captured_when_editing(HttpStatusCode status)
    {
        var handler = new Handler(status); var activator = new Activator();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetLocalization();
        services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton(new HttpClient(handler) { BaseAddress = new("https://example.test/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        RenameProjectResponse? saved = null; var refreshes = 0;
        var project = new ProjectInspectionResource(Guid.NewGuid(), "Workstream", null, "Old title", "Active", 7, null, null, null, null, null,
            null, "/project", JsonSerializer.SerializeToElement(new { }));
        var parameters = new Dictionary<string, object?>
        {
            [nameof(ProjectNameEditor.OrganizationId)] = handler.Organization,
            [nameof(ProjectNameEditor.Project)] = project,
            [nameof(ProjectNameEditor.Renamed)] = EventCallback.Factory.Create<RenameProjectResponse>(handler, result => saved = result),
            [nameof(ProjectNameEditor.RefreshRequested)] = EventCallback.Factory.Create(handler, () => refreshes++)
        };
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ProjectNameEditor>(ParameterView.FromDictionary(parameters)));
        var editor = activator.Editor!;
        Task Invoke(string name) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (typeof(ProjectNameEditor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null) is Task task) await task;
        });
        await Invoke("Edit"); await Invoke("Cancel"); Assert.Empty(handler.Requests);
        await Invoke("Edit");
        typeof(ProjectNameEditor).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, "New title");
        // A realtime update must not silently upgrade the user's stale submission.
        parameters[nameof(ProjectNameEditor.Project)] = project with { Revision = 8 };
        await renderer.Dispatcher.InvokeAsync(() => editor.SetParametersAsync(ParameterView.FromDictionary(parameters)));
        await Invoke("SaveAsync");
        var request = Assert.Single(handler.Requests);
        Assert.Equal("New title", request.Name); Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(status == HttpStatusCode.OK, saved is not null);
        Assert.Equal(status == HttpStatusCode.Conflict ? 1 : 0, refreshes);
        if (status != HttpStatusCode.OK)
            Assert.NotNull(typeof(ProjectNameEditor).GetField("_error", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
    }

    private sealed class Activator : IComponentActivator
    {
        public ProjectNameEditor? Editor { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            if (component is ProjectNameEditor editor) Editor = editor;
            return component;
        }
    }
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public Guid Organization { get; } = Guid.NewGuid();
        public List<RenameProjectRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.StartsWith($"/api/core/organizations/{Organization}/project-setup/projects/", request.RequestUri!.AbsolutePath);
            Assert.EndsWith("/name", request.RequestUri.AbsolutePath);
            Requests.Add((await request.Content!.ReadFromJsonAsync<RenameProjectRequest>(ct))!);
            return new(status) { Content = JsonContent.Create(new RenameProjectResponse(Guid.NewGuid(), "New title", 8)) };
        }
    }
}
