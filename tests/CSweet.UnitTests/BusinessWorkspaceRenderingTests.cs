using System.Net;
using CSweet.Contracts.Communications;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using CSweet.Contracts.Core;
using CSweet.UI.Components;
using CSweet.UI.Layout;
using CSweet.UI.Pages;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed class BusinessWorkspaceRenderingTests
{
    [Fact]
    public async Task Analytics_RendersPendingAndUnknownInsteadOfZeroUsage()
    {
        var html = await Render(typeof(Analytics), "analytics-coverage", exercise: instance =>
        {
            var pending = new CSweet.Contracts.Analytics.InferenceUsageCoverage { PendingCalls = 1 };
            var unknown = new CSweet.Contracts.Analytics.InferenceUsageCoverage { UnknownCalls = 1 };
            Set(instance, "_analytics", new CSweet.Contracts.Analytics.InferenceAnalyticsResponse("30d", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(2, 0, 0, 0) { Coverage = pending with { UnknownCalls = 1 } },
                [new(Guid.NewGuid(), "Pending Victor", true, "test", null, null, "test", true, 1, 0, 0, 0, null) { Coverage = pending },
                 new(Guid.NewGuid(), "Unknown usage", true, "test", null, null, "test", true, 1, 0, 0, 0, null) { Coverage = unknown }]));
            return Task.CompletedTask;
        });
        Assert.Contains(">Pending<", html); Assert.Contains(">Unknown<", html);
        Assert.Contains("1 pending", html); Assert.Contains("1 unknown", html);
        Assert.DoesNotContain("DataLabel=\"Input\">0<", html, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Guid BusinessId = Guid.Parse("ab2184e9-9ac3-441d-9194-3cd65fe60052");
    private static readonly Guid ItemId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    [Theory]
    [InlineData(typeof(SourceControl), "source", "Create repository")]
    [InlineData(typeof(CSweet.UI.Pages.Compute), "compute", "Prism Break")]
    [InlineData(typeof(Approvals), "approvals", "Review launch plan")]
    [InlineData(typeof(Calendar), "calendar", "New event")]
    [InlineData(typeof(Documents), "documents", "Creative brief")]
    [InlineData(typeof(Analytics), "analytics", "Naomi Chen")]
    [InlineData(typeof(Marketplace), "marketplace", "matching agents")]
    [InlineData(typeof(CSweet.UI.Pages.Communications), "communications", "Search conversations")]
    [InlineData(typeof(Security), "security", "Review completed")]
    [InlineData(typeof(ExecutiveBriefings), "briefings", "Save briefing settings")]
    [InlineData(typeof(ChiefHiringSettings), "hiring", "Save hiring preferences")]
    [InlineData(typeof(ConnectorStandingPolicy), "policy", "Automatic approval rules")]
    [InlineData(typeof(PluginSetup), "plugin", "Back to employees")]
    public async Task BusinessPagesRenderWithTheirControlsAndSharedTheme(Type page, string name, string expected)
    {
        var html = await Render(page, name);
        Assert.Contains("aurora-page business-workspace", html);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(typeof(SourceControl), "connections", "_section", "Connections", "Repository providers")]
    [InlineData(typeof(SourceControl), "source-activity", "_section", "Activity", "Activity")]
    [InlineData(typeof(SourceControl), "source-settings", "_section", "Settings", "Repository defaults")]
    [InlineData(typeof(Approvals), "approval-history", "_showHistory", "True", "History")]
    [InlineData(typeof(Calendar), "agenda", "QueryView", "Agenda", "calendar-agenda")]
    [InlineData(typeof(Calendar), "week", "QueryView", "Week", "Week calendar")]
    [InlineData(typeof(Calendar), "event-editor", "_editing", "True", "Save event")]
    [InlineData(typeof(Documents), "document-detail", "_selectedId", "10000000-0000-0000-0000-000000000001", "artifact-workspace")]
    [InlineData(typeof(CSweet.UI.Pages.Communications), "conversations", "_mobileConversations", "True", "show-conversations")]
    public async Task AlternateViewsKeepTheirContent(Type page, string name, string field, string value, string expected)
    {
        var html = await Render(page, name, field, value);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(typeof(Analytics), "efficiency", 1, "Usage coverage")]
    [InlineData(typeof(Documents), "document-edit", 1, "Save draft")]
    [InlineData(typeof(Documents), "document-history", 2, "History")]
    [InlineData(typeof(Documents), "document-access", 3, "Grant access")]
    public async Task DetailTabsRemainAvailable(Type page, string name, int tab, string expected)
    {
        var html = await Render(page,name,page == typeof(Documents) ? "_selectedId" : null, ItemId.ToString(), tab:tab);
        Assert.Contains(expected, WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationAndChannelEditorPreserveTheirControls(bool editor)
    {
        var html = await Render(typeof(CSweet.UI.Pages.Communications), editor ? "channel-editor" : "conversation", exercise: instance =>
        {
            var chat = new CommunicationChatResponse(ItemId,"Creative direction", "Prism Break",false,false,false,true,DateTimeOffset.UtcNow,[],null,null,0);
            Set(instance,"_selected",chat);
            ((List<CommunicationHubMessageResponse>)Get(instance,"_messages")!).Add(new(ItemId,1,ItemId,ItemId,"Naomi Chen","Agent","The creative brief is ready. Let’s review the next milestone.",DateTimeOffset.UtcNow));
            if(editor) instance.GetType().GetMethod("OpenCreateGroup",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(instance,null);
            return Task.CompletedTask;
        });
        Assert.Contains(editor ? "Create a place to work" : "creative brief is ready",WebUtility.HtmlDecode(html));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenEndedChatQuestionShowsImmediateAnswerInputAndRespectsReadOnlyPerspective(bool readOnly)
    {
        var html = await Render(typeof(CSweet.UI.Pages.Communications), "open-ended-question", exercise: instance =>
        {
            Set(instance, "_selected", new CommunicationChatResponse(ItemId, "Creative direction", null, false, false, false, true, DateTimeOffset.UtcNow, [], null, null, 0));
            if (readOnly)
                Set(instance, "_hub", ((CommunicationHubResponse)Get(instance, "_hub")!) with { IsReadOnlyPerspective = true });
            var decision = new ExecutiveDecisionCardResponse(Guid.NewGuid(), "What initial game direction should I build on?", "Pending", [], "", null, null, DateTimeOffset.UtcNow, null);
            ((List<CommunicationHubMessageResponse>)Get(instance, "_messages")!).Add(new(ItemId, 1, ItemId, ItemId, "Naomi Chen", "Agent", "Your involvement preference is saved.", DateTimeOffset.UtcNow) { Decision = decision });
            var canSubmit = instance.GetType().GetMethod("CanSubmitDecision", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Equal(false, canSubmit.Invoke(instance, [decision]));
            ((Dictionary<Guid, string>)Get(instance, "_decisionOther")!)[decision.Id] = "Cozy exploration\nPC, no combat";
            Assert.Equal(true, canSubmit.Invoke(instance, [decision]));
            var answerKey = instance.GetType().GetMethod("DecisionAnswerKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Equal(answerKey.Invoke(instance, [decision.Id]), answerKey.Invoke(instance, [decision.Id]));
            return Task.CompletedTask;
        });
        Assert.Contains("What initial game direction", html);
        if (readOnly)
        {
            Assert.Contains("read-only", html);
            Assert.DoesNotContain("aria-label=\"Your answer\"", html);
        }
        else
        {
            Assert.Contains("aria-label=\"Your answer\"", html);
            Assert.Contains("rows=\"4\"", html);
            Assert.Contains("Cozy exploration", html);
            Assert.DoesNotContain("Something else", html);
        }
    }

    [Theory]
    [InlineData("Code", "README.md")]
    [InlineData("Changes", "Changes")]
    [InlineData("Branches", "Create ref")]
    [InlineData("Access", "Team access")]
    [InlineData("Settings", "Save repository")]
    public async Task RepositorySectionsKeepTheirControls(string section,string expected)
    {
        var html = await Render(typeof(InternalRepositoryDetail),"repository-"+section.ToLowerInvariant(),"_section",section);
        Assert.Contains(expected,WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task AnalyticsLinksToEmployeeDetails()
    {
        var html = await Render(typeof(Analytics), "analytics");
        Assert.Contains($"/organizations/{BusinessId}/employees/{ItemId}", html);
    }

    [Fact]
    public async Task DocumentFilterResetClearsAdvancedFiltersAndKeepsSelectedDocument()
    {
        await Render(typeof(Documents), "document-filter-reset", exercise: async instance =>
        {
            Set(instance, "_search", "missing"); Set(instance, "_documentType", "brief");
            Set(instance, "_creator", "Naomi"); Set(instance, "_includeArchived", true);
            Set(instance, "_packageId", ItemId); Set(instance, "_selectedId", ItemId);
            await (Task)instance.GetType().GetMethod("ClearFiltersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null)!;
            Assert.Equal("", Get(instance, "_search")); Assert.Equal("", Get(instance, "_creator"));
            Assert.Null(Get(instance, "_packageId")); Assert.Equal(false, Get(instance, "_includeArchived"));
            Assert.Equal(ItemId, Get(instance, "_selectedId"));
        });
    }

    private static object? Get(object instance, string field) => instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void Set(object instance, string field, object? value) => instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static async Task<string> Render(Type page, string name, string? field = null, string? value = null, Func<ComponentBase, Task>? exercise = null, int? tab = null)
    {
        var api = new BusinessApi();
        var services = new ServiceCollection().AddLogging(); services.AddMudServices(); services.AddCSweetApiClients();
        var activator = new TrackingActivator(); services.AddSingleton<IComponentActivator>(activator);
        services.AddSingleton<IJSRuntime, NoJavaScript>(); services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new Uri("http://localhost/") });
        services.AddSingleton<IBusinessContext, TestBusinessContext>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var theme = await renderer.RenderComponentAsync<MudThemeProvider>();
            var dialogs = await renderer.RenderComponentAsync<MudDialogProvider>();
            ComponentBase? instance = null;
            RenderFragment body = builder => {
                if(page == typeof(InternalRepositoryDetail)) { builder.OpenElement(0,"div"); builder.AddAttribute(1,"class","aurora-page business-workspace"); }
                builder.OpenComponent(2, page); builder.AddAttribute(3, "OrganizationId", BusinessId);
                if(page == typeof(InternalRepositoryDetail)) { builder.AddAttribute(4,"RepositoryId",ItemId); builder.AddAttribute(5,"CanManage",true); }
                builder.AddComponentReferenceCapture(6, value => instance = (ComponentBase)value); builder.CloseComponent();
                if(page == typeof(InternalRepositoryDetail)) builder.CloseElement();
            };
            var rendered = await renderer.RenderComponentAsync<TestHost>(ParameterView.FromDictionary(new Dictionary<string,object?> { [nameof(TestHost.ChildContent)] = body }));
            if (field is not null)
            {
                var member = page.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
                var property = page.GetProperty(field);
                var type = member?.FieldType ?? property!.PropertyType;
                object parsed = type == typeof(bool) ? bool.Parse(value!) : type == typeof(Guid?) ? Guid.Parse(value!) : type.IsEnum ? Enum.Parse(type,value!) : value!;
                if (member is not null) member.SetValue(instance,parsed); else property!.SetValue(instance,parsed);
            }
            if (exercise is not null) await exercise(instance!);
            if (field is not null || exercise is not null) { typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance,null); await Task.Yield(); }
            if (tab.HasValue) { await activator.Tabs.Last().ActivatePanelAsync(tab.Value, false); await Task.Yield(); }
            var html = theme.ToHtmlString() + rendered.ToHtmlString() + dialogs.ToHtmlString();
            Assert.DoesNotContain("could not be loaded", html, StringComparison.OrdinalIgnoreCase);

            if (Environment.GetEnvironmentVariable("CSWEET_BUSINESS_PREVIEWS") is string path) await File.WriteAllTextAsync(Path.Combine(path,$"business-{name}.html"),html);
            return html;
        });
    }
    private sealed class TrackingActivator : IComponentActivator
    {
        public List<MudTabs> Tabs { get; } = [];
        public IComponent CreateInstance(Type type) { var c=(IComponent)Activator.CreateInstance(type)!; if(c is MudTabs tabs) Tabs.Add(tabs); return c; }
    }
    private sealed class TestHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
    }
    private sealed class BusinessApi : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                // The existing lock API represents its read-only list operation as a POST.
                Assert.Equal(HttpMethod.Post,request.Method); Assert.EndsWith("/locks",path);
                var body = await request.Content!.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
                Assert.Equal("list",body.GetProperty("operation").GetString());
            }
            string? json = null;
            if (path.EndsWith("/source-control/dashboard")) json = """{"canManageSourceControl":true,"repositories":[],"connections":[],"platformReadiness":{"existingGitHubAvailable":true}}""";
            else if (path.EndsWith("/internal/repositories")) json = "[]";
            else if (path.EndsWith("/source-control/activity")) json = """{"items":[]}""";
            else if(path.Contains("/internal/repositories/")) json = path.EndsWith("/team") || path.EndsWith("/proposals") ? "[]" : path.EndsWith("/access") ? """{"credentials":[],"cloneUrl":"https://example.test/prism.git"}""" : path.EndsWith("/locks") ? """{"locks":[]}""" : """{"repository":{"id":"10000000-0000-0000-0000-000000000001","name":"Prism Break","canonicalPath":"studio/prism-break","defaultBranch":"main","status":"Ready","isPrivate":true},"revision":1,"inspection":{"defaultBranch":"main","refs":[{"name":"refs/heads/main","sha":"1234567890abcdef"}],"commits":[{"sha":"1234567890abcdef","author":"Daniel Kim","subject":"Create arcade prototype"}],"files":["README.md","src/game.ts"]}}""";
            else if(path.EndsWith("/teams")) json = """{"teams":[]}""";
            else if (path.EndsWith("/compute")) json = """{"checkedAt":"2026-10-11T15:00:00Z","resources":[{"id":"10000000-0000-0000-0000-000000000001","agent":"Daniel Kim","project":"Prism Break","state":"Running","desiredState":"Running","operatingSystem":"Linux","cpuCount":4,"memoryMiB":8192,"diskMiB":64000,"updatedAt":"2026-10-11T15:00:00Z","leaseExpiresAt":"2026-10-12T15:00:00Z"}]}""";
            else if (path.EndsWith("/approvals")) json = """{"pendingCount":1,"items":[{"id":"10000000-0000-0000-0000-000000000001","kind":"Artifact","title":"Review launch plan","summary":"The release plan is ready for your review.","status":"Pending","requestedBy":"Naomi Chen","assignedTo":"Matt","createdAt":"2026-10-11T15:00:00Z","canDecide":true}]}""";
            else if (path.EndsWith("/calendar")) json = """{"timeZoneId":"UTC","canCreate":true,"canConfigure":true,"members":[],"occurrences":[]}""";
            else if (path.EndsWith("/calendar/reminders")) json = "[]";
            else if (path.EndsWith("/documents")) json = "[" + Document + "]";
            else if (path.EndsWith("/documents/" + ItemId)) json = "{\"document\":" + Document + ",\"latestRevision\":{\"id\":\"10000000-0000-0000-0000-000000000001\",\"content\":\"# Creative brief\\nA polished arcade experience.\",\"number\":1,\"status\":\"Draft\"},\"revisions\":[],\"grants\":[],\"accessRequests\":[]}";
            else if (path.EndsWith("/documents/folders") || path.EndsWith("/documents/packages")) json = "[]";
            else if (path.Contains("/analytics/inference")) json = """{"totals":{"totalTokens":145000,"inputTokens":100000,"outputTokens":45000,"requestCount":350},"employees":[{"employeeId":"10000000-0000-0000-0000-000000000001","employeeName":"Naomi Chen","agentKey":"Creative Director","model":"Creative model","providerName":"Studio provider","totalTokens":145000,"inputTokens":100000,"outputTokens":45000,"requestCount":350,"isActive":true}]}""";
            else if (path.Contains("/efficiency")) json = """{"businessTotal":{},"businessOverhead":{},"unattributed":{},"projects":[],"workItems":[],"coverageNote":"Usage is reported by providers."}""";
            else if (path.EndsWith("/agents/available")) json = "{\"agents\":[],\"sources\":[]}";
            else if (path.EndsWith("/communications/hub")) json = """{"canManageChats":true,"chats":[],"people":[],"audiences":[]}""";
            else if (path.Contains("/notifications")) json = "[]";
            else if (path.EndsWith("/security/events")) json = """{"items":[{"id":"10000000-0000-0000-0000-000000000001","occurredAt":"2026-10-11T15:00:00Z","summary":"Review completed","eventType":"document.review","category":"DocumentAccess","actorDisplayName":"Naomi Chen","actorKind":"Agent","outcome":"Completed","integrityStatus":"Verified"}]}""";
            else if (path.EndsWith("/executive-briefings/settings")) json = """{"cadence":"Daily","weeklyDay":"Monday","localTime":"09:00","timeZone":"UTC"}""";
            else if (path.EndsWith("/executive-briefings/history") || path.EndsWith("/users")) json = "[]";
            else if (path.Contains("/hiring/policies/")) json = """{"settings":{}}""";
            else if (path.EndsWith("/hiring")) json = "{\"recommendations\":[]}";
            else if (path.EndsWith("/standing-policy")) json = "{\"unavailableReason\":\"No automatic approval rule has been proposed.\"}";
            else if (path.Contains("/plugin-setup/")) json = path.EndsWith("/dependencies") ? "[]" : """{"state":"Ready","flow":{"title":"Connect your account","steps":[]},"cleanup":[],"connections":[],"completedStepIds":[],"fields":[],"values":{}}""";
            else if (path.EndsWith("/" + BusinessId)) json = "{\"name\":\"Super Awesome Games\"}";
            return new HttpResponseMessage(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json") };
        }
        private const string Document = """{"id":"10000000-0000-0000-0000-000000000001","title":"Creative brief","documentType":"brief","status":"Draft","latestRevisionNumber":1,"creatorDisplayName":"Naomi Chen","updatedAt":"2026-10-11T15:00:00Z"}""";
    }
    private static readonly OrganizationResponse Business = new(BusinessId, "Super Awesome Games", null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private sealed class TestBusinessContext : IBusinessContext
    {
        public IReadOnlyList<OrganizationResponse> Businesses => [Business];
        public OrganizationResponse? SelectedBusiness => Business;
        public bool IsLoading => false;
        public string? ErrorMessage => null;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectAsync(Guid businessId, bool navigate = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
