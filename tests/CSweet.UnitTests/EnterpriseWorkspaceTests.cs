using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using CSweet.UI.Localization;

namespace CSweet.UnitTests;

public sealed class EnterpriseWorkspaceTests
{
    [Theory]
    [InlineData("aurora")]
    [InlineData("gold")]
    public async Task LandingPagePreservesBusinessLinksAndTheme(string theme)
    {
        await Run(new Api(), theme, async (page, api, render) =>
        {
            var html = await render("page");
            Assert.Contains("Your enterprise.", html);
            Assert.Contains($"data-appearance=\"{theme}\"", html);
            Assert.Contains($"/organizations/{api.Business.Id}/projects", html);
            Assert.Contains($"/organizations/{api.Business.Id}/employees", html);
            Assert.Contains("Focused business", html);
            Assert.Contains("API connected", html);
            Invoke(page, "ShowCreateForm");
            await render("create");
            Invoke(page, "ShowEditForm", api.Business);
            await render("edit");
            Invoke(page, "RequestDelete", api.Business);
            await render("delete");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditClosesOnSuccessAndKeepsInputOnFailure(bool fail)
    {
        await Run(new Api { FailUpdate = fail }, "aurora", async (page, api, render) =>
        {
            Invoke(page, "ShowEditForm", api.Business);
            Set(page, "_formName", "  Renamed business  ");
            await (Task)Invoke(page, "SaveEditAsync")!;
            Assert.Equal(fail, Get<bool>(page, "_showForm"));
            Assert.False(Get<bool>(page, "_saving"));
            Assert.Equal("Renamed business", api.UpdatedName);
            if (fail) Assert.Contains("Please try again", Get<string>(page, "_formError"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteRequiresConfirmationAndReportsFailure(bool fail)
    {
        await Run(new Api { FailDelete = fail }, "aurora", async (page, api, render) =>
        {
            Invoke(page, "RequestDelete", api.Business);
            Assert.Equal(0, api.Deletes);
            Invoke(page, "CancelDelete");
            await (Task)Invoke(page, "DeleteBusinessAsync")!;
            Assert.Equal(0, api.Deletes);
            Invoke(page, "RequestDelete", api.Business);
            await (Task)Invoke(page, "DeleteBusinessAsync")!;
            Assert.Equal(1, api.Deletes);
            Assert.Equal(!fail, Get<bool>(page, "_deleteSucceeded"));
            var html = await render("delete-result");
            Assert.Contains(fail ? "could not be deleted" : "was deleted", html);
            Assert.Equal(fail ? 1 : 0, Get<IReadOnlyList<OrganizationResponse>>(page, "_organizations").Count);
        });
    }

    [Theory]
    [InlineData("compute_cleanup_pending")]
    [InlineData("agent_cleanup_pending")]
    public async Task DeleteKeepsTileBusyAndRemovesItWhenBackgroundDeletionCompletes(string code)
    {
        await Run(new Api { DeleteErrorCode = code }, "aurora", async (page, api, render) =>
        {
            Invoke(page, "RequestDelete", api.Business);
            await (Task)Invoke(page, "DeleteBusinessAsync")!;
            Assert.Single(Get<IReadOnlyList<OrganizationResponse>>(page, "_organizations"));
            var html = await render("compute-cleanup");
            Assert.Contains("Cleaning up resources", html);
            Assert.Contains("aria-busy=\"true\"", html);
            Assert.Contains("deleted automatically", html);
            Assert.DoesNotContain("Open business", html);
            Assert.DoesNotContain("could not be deleted", html);

            api.BusinessDeleted = true;
            await (Task)Invoke(page, "RefreshDeletionStatusAsync", CancellationToken.None)!;
            Assert.True(Get<bool>(page, "_deleteSucceeded"));
            Assert.Empty(Get<IReadOnlyList<OrganizationResponse>>(page, "_organizations"));
            Assert.Equal(1, api.Deletes);
            Assert.DoesNotContain("Cleaning up resources", await render("compute-cleanup-complete"));
        });
    }

    [Theory]
    [InlineData("Deleting")]
    [InlineData("DeletionFailed")]
    public async Task ReloadRestoresDeletionProgressOrRecoveryAction(string status)
    {
        var api = new Api();
        api.Business = api.Business with { Status = status };
        await Run(api, "aurora", async (page, client, render) =>
        {
            var html = await render("restored-deletion");
            Assert.Contains(status == "Deleting" ? "Cleaning up resources" : "Retry deletion", html);
            Assert.Contains(status == "Deleting" ? "aria-busy=\"true\"" : "needs attention", html);
            Assert.Equal(0, client.Deletes);
            Assert.Contains($"/organizations/{client.Business.Id}/compute", html);
            Assert.Contains("Retry deletion", html);
            Invoke(page, "RequestDelete", client.Business);
            Assert.True(Get<bool>(page, "_showDelete"));
            Assert.Equal(0, client.Deletes);
            await (Task)Invoke(page, "DeleteBusinessAsync")!;
            Assert.Equal(1, client.Deletes);
        });
    }

    [Fact]
    public async Task RetryOfPendingDeletionShowsCleanupFailureImmediately()
    {
        var api = new Api { DeleteErrorCode = "compute_cleanup_failed" };
        api.Business = api.Business with { Status = "Deleting" };
        await Run(api, "aurora", async (page, client, render) =>
        {
            Invoke(page, "RequestDelete", client.Business);
            await (Task)Invoke(page, "DeleteBusinessAsync")!;
            var html = await render("retry-cleanup-failed");
            Assert.Contains("needs attention", html);
            Assert.Contains("Retry deletion", html);
            Assert.DoesNotContain("Cleaning up resources", html);
        });
    }

    [Fact]
    public async Task LoadingFailureIsVisibleAndRetryRecovers()
    {
        await Run(new Api { FailList = true }, "aurora", async (page, api, render) =>
        {
            Assert.Contains("Unable to load businesses", await render("error"));
            api.FailList = false;
            await (Task)Invoke(page, "LoadAsync")!;
            var html = await render("recovered");
            Assert.Contains("Super Games", html);
            Assert.DoesNotContain("Unable to load businesses", html);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationValidatesNameAndPreservesHiringHandoff(bool fail)
    {
        await Run(new Api { FailCreate = fail }, "aurora", async (page, api, render) =>
        {
            Invoke(page, "ShowBasicCreateForm");
            Set(page, "_formName", "   ");
            await (Task)Invoke(page, "CreateBusinessAsync")!;
            Assert.Equal(0, api.Creates);
            Set(page, "_formName", "  New venture  ");
            await (Task)Invoke(page, "CreateBusinessAsync")!;
            Assert.Equal(1, api.Creates);
            Assert.Equal("New venture", api.CreatedName);
            Assert.Equal(fail, Get<bool>(page, "_showForm"));
            Assert.False(Get<bool>(page, "_saving"));
            if (fail) Assert.Contains("Please try again", Get<string>(page, "_formError"));
            else Assert.Contains("/marketplace?role=Chief", api.CurrentUri());
        });
    }

    private static object? Invoke(object instance, string name, params object[] args) => typeof(Organizations).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static T Get<T>(object instance, string name) => (T)typeof(Organizations).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Set(object instance, string name, object value) => typeof(Organizations).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static async Task Run(Api api, string theme, Func<Organizations, Api, Func<string, Task<string>>, Task> test)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMudServices(); services.AddCSweetLocalization();
        services.AddCSweetApiClients();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        var navigation = new TestNavigation();
        services.AddSingleton<NavigationManager>(navigation);
        api.CurrentUri = () => navigation.Uri;
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        var capture = new Capture();
        services.AddSingleton<IComponentActivator>(capture);
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IBusinessContext>().InitializeAsync();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Host.Theme)] = theme }));
            var page = capture.Page!;
            await test(page, api, async name =>
            {
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
                await Task.Yield();
                var html = root.ToHtmlString();
                if (Environment.GetEnvironmentVariable("CSWEET_ENTERPRISE_PREVIEWS") is { } path)
                {
                    var dialog = capture.Dialogs.LastOrDefault(d => d.GetState(x => x.Visible));
                    if (dialog is not null)
                    {
                        var preview = await renderer.RenderComponentAsync<MudPaper>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(MudPaper.ChildContent)] = dialog.DialogContent, [nameof(MudPaper.Class)] = "mud-dialog" }));
                        html += "<div class=\"preview-dialog-backdrop\">" + preview.ToHtmlString() + "</div>";
                    }
                    await File.WriteAllTextAsync(Path.Combine(path, $"enterprise-{theme}-{name}.html"), html);
                }
                return html;
            });
        });
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public string Theme { get; set; } = "aurora";
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<MudThemeProvider>(0); b.CloseComponent();
            b.OpenComponent<MudPopoverProvider>(1); b.CloseComponent();
            b.OpenComponent<MudDialogProvider>(2); b.CloseComponent();
            b.OpenElement(3, "div"); b.AddAttribute(4, "class", "mud-layout"); b.AddAttribute(5, "data-appearance", Theme);
            b.OpenElement(6, "main"); b.AddAttribute(7, "class", "mud-main-content");
            b.OpenElement(8, "div"); b.AddAttribute(9, "class", "content");
            b.OpenComponent<Home>(10); b.CloseComponent();
            b.CloseElement(); b.CloseElement(); b.CloseElement();
        }
    }
    private sealed class Capture : IComponentActivator
    {
        public Organizations? Page;
        public List<MudDialog> Dialogs = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is Organizations page) Page = page;
            if (component is MudDialog dialog) Dialogs.Add(dialog);
            return component;
        }
    }
    private sealed class Api : HttpMessageHandler
    {
        public OrganizationResponse Business = new(Guid.Parse("ab2184e9-9ac3-441d-9194-3cd65fe60052"), "Super Games", "Creative & Product", "We make amazing web games.", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public bool FailList, FailUpdate, FailDelete, FailCreate, BusinessDeleted;
        public string? DeleteErrorCode;
        public int Deletes, Creates;
        public string? UpdatedName, CreatedName;
        public Func<string> CurrentUri = () => "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/health") return new(HttpStatusCode.OK);
            if (request.Method == HttpMethod.Post)
            {
                Creates++;
                CreatedName = (await request.Content!.ReadFromJsonAsync<StartBusinessOnboardingRequest>(cancellationToken))!.BusinessName;
                return new(FailCreate ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = FailCreate ? JsonContent.Create(new { message = "Please try again." }) : JsonContent.Create(new BusinessOnboardingOperationResponse(Guid.NewGuid(), CreatedName, "", "NeedsSetup", "", "", 0, 0, null, Business.Id, $"/organizations/{Business.Id}/marketplace?role=Chief%20of%20Staff", null, DateTimeOffset.UtcNow)) };
            }
            if (request.Method == HttpMethod.Put)
            {
                UpdatedName = (await request.Content!.ReadFromJsonAsync<UpdateOrganizationRequest>(cancellationToken))!.Name;
                return new(FailUpdate ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = FailUpdate ? JsonContent.Create(new { message = "Please try again." }) : JsonContent.Create(Business with { Name = UpdatedName! }) };
            }
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                if (DeleteErrorCode is not null)
                    return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new CoreActionResponse(false,
                        DeleteErrorCode, "Compute cleanup has been requested. Wait for teardown, then retry deletion.")) };
                return new(FailDelete ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent) { Content = JsonContent.Create(new { message = "Please try again." }) };
            }
            return new(FailList ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = JsonContent.Create(BusinessDeleted ? Array.Empty<OrganizationResponse>() : new[] { Business }) };
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
