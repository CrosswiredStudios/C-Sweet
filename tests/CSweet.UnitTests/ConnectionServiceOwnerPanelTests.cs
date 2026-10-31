using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CSweet.UI.Setup;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace CSweet.UnitTests;

// Actual production component and MudBlazor events; HTTP is synthetic. These tests do not
// emulate a browser's focus/change synthesis or establish the historical failed event sequence.
#pragma warning disable BL0006 // Renderer frames expose the actual registered native event handlers.
public sealed class ConnectionServiceOwnerPanelTests
{
    private const string Tenant = "synthetic-tenant.001";
    private static readonly (string Label, string Value)[] Fields =
    [
        ("Tenant ID", Tenant), ("Installation name", "Synthetic installation"),
        ("Public key from customer pairing", "synthetic-public-key"), ("Key ID", "synthetic-key-001"),
        ("Exact HTTPS browser return URI", "https://localhost:7125/settings/plugins/managed-return")
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enrollment_uses_all_five_current_input_values_without_waiting_for_change(bool blurAndChange)
    {
        await WithPanel(async (renderer, root, api) =>
        {
            foreach (var (label, value) in Fields)
            {
                var field = renderer.Field(root, label);
                Assert.True(((MudTextField<string>)field.Instance).Immediate);
                Assert.Contains(renderer.Events(field.Id), e => e.Name == "oninput");
                await renderer.Emit(field.Id, "oninput", new ChangeEventArgs { Value = value });
                if (blurAndChange)
                {
                    await renderer.EmitIfBound(field.Id, "onchange", new ChangeEventArgs { Value = value });
                    await renderer.Emit(field.Id, "onblur", new FocusEventArgs());
                }
            }
            await renderer.Click(root, "Approve customer enrollment");
            var payload = Assert.Single(api.Enrollments);
            Assert.Equal(Tenant, payload.GetProperty("request").GetProperty("tenantId").GetString());
            Assert.Equal(1, payload.GetProperty("grantRevision").GetInt64());
            var deployment = payload.GetProperty("request").GetProperty("deployment");
            Assert.Equal(Fields[1].Value, deployment.GetProperty("name").GetString());
            Assert.Equal(Fields[2].Value, deployment.GetProperty("publicKey").GetString());
            Assert.Equal(Fields[3].Value, deployment.GetProperty("keyId").GetString());
            Assert.Equal(Fields[4].Value, Assert.Single(deployment.GetProperty("returnUris").EnumerateArray()).GetString());
            Assert.Equal(new[] { "/api/plugins/connection-service/owner/enroll" }, api.Paths);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" tenant")]
    [InlineData("tenant ")]
    [InlineData("tenant\n")]
    [InlineData("tenant\u200b")]
    [InlineData("_tenant")]
    [InlineData(".tenant")]
    [InlineData("-tenant")]
    [InlineData("tenant/path")]
    [InlineData("tenant\u2011id")]
    [InlineData("SENSITIVE_INVALID_TENANT<script>")]
    public async Task Invalid_tenant_is_rejected_without_http_or_echoing_the_value(string? tenant)
    {
        await WithPanel(async (renderer, root, api) =>
        {
            await renderer.Emit(renderer.Field(root, "Tenant ID").Id, "oninput", new ChangeEventArgs { Value = tenant });
            await renderer.Click(root, "Approve customer enrollment");
            Assert.Empty(api.Paths); Assert.Empty(api.Enrollments);
            Assert.Contains("Enter a tenant ID", renderer.Text(root));
            Assert.DoesNotContain("SENSITIVE_INVALID_TENANT", renderer.Text(root));
            Assert.Single(renderer.Components(root), c => c.Instance is MudTextField<string> f && f.Label == "Tenant ID");
        }, scope: "*");
    }

    [Fact]
    public async Task Overlong_tenant_is_rejected_without_http()
    {
        await WithPanel(async (renderer, root, api) =>
        {
            await renderer.Emit(renderer.Field(root, "Tenant ID").Id, "oninput", new ChangeEventArgs { Value = new string('a', 129) });
            await renderer.Click(root, "Approve customer enrollment");
            Assert.Empty(api.Paths); Assert.Contains("Enter a tenant ID", renderer.Text(root));
        }, scope: "*");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("tenant.with_underscores-and-digits123")]
    [InlineData("11111111-2222-4333-8444-555555555555")]
    public async Task Contract_identifiers_are_supported_without_rewriting_or_guid_only_restriction(string tenant)
    {
        await WithPanel(async (renderer, root, api) =>
        {
            await renderer.Emit(renderer.Field(root, "Tenant ID").Id, "oninput", new ChangeEventArgs { Value = tenant });
            await renderer.Click(root, "Approve customer enrollment");
            Assert.Equal(tenant, Assert.Single(api.Enrollments).GetProperty("request").GetProperty("tenantId").GetString());
        }, scope: "*");
    }

    [Fact]
    public async Task Maximum_length_identifier_is_supported()
    {
        var tenant = new string('a', 128);
        await WithPanel(async (renderer, root, api) =>
        {
            await renderer.Emit(renderer.Field(root, "Tenant ID").Id, "oninput", new ChangeEventArgs { Value = tenant });
            await renderer.Click(root, "Approve customer enrollment");
            Assert.Equal(tenant, Assert.Single(api.Enrollments).GetProperty("request").GetProperty("tenantId").GetString());
        }, scope: "*");
    }

    [Theory]
    [InlineData("another-valid-tenant")]
    [InlineData("SYNTHETIC-tenant.001")]
    public async Task Different_tenant_cannot_fall_back_to_the_granted_tenant(string tenant)
    {
        await WithPanel(async (renderer, root, api) =>
        {
            await renderer.Emit(renderer.Field(root, "Tenant ID").Id, "oninput", new ChangeEventArgs { Value = tenant });
            await renderer.Click(root, "Approve customer enrollment");
            Assert.Empty(api.Paths); Assert.Empty(api.Enrollments);
            Assert.Contains("must match your current service enrollment permission", renderer.Text(root));
        });
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public async Task Service_failure_has_safe_error_and_never_retries_or_prepares_another_key(int status)
    {
        await WithPanel(async (renderer, root, api) =>
        {
            foreach (var (label, value) in Fields)
                await renderer.Emit(renderer.Field(root, label).Id, "oninput", new ChangeEventArgs { Value = value });
            await renderer.Click(root, "Approve customer enrollment");
            await Task.Yield();
            Assert.Single(api.Enrollments);
            Assert.Equal(new[] { "/api/plugins/connection-service/owner/enroll" }, api.Paths);
            Assert.Contains("check service state before retrying", renderer.Text(root));
            Assert.DoesNotContain("SENSITIVE_REMOTE_BODY", renderer.Text(root));
            Assert.DoesNotContain("synthetic-public-key", renderer.Text(root));
        }, failureStatus: (HttpStatusCode)status);
    }

    private static async Task WithPanel(Func<EventRenderer, int, FakeApi, Task> test, string scope = Tenant, HttpStatusCode? failureStatus = null)
    {
        var api = new FakeApi(scope, failureStatus);
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton(new HttpClient(api) { BaseAddress = new("https://offline.invalid/") });
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new EventRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.Add<ConnectionServiceOwnerPanel>();
            await renderer.Components(root).Select(c => c.Instance).OfType<MudExpansionPanel>().Single().ExpandAsync();
            await renderer.Click(root, "Check my service permissions");
            Assert.Equal(new[] { "/api/plugins/connection-service/owner/access" }, api.Paths);
            api.Paths.Clear(); // Count only subsequent form submission requests.
            await test(renderer, root, api);
        });
    }

    private sealed class FakeApi(string scope, HttpStatusCode? failureStatus) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<JsonElement> Enrollments { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("offline.invalid", request.RequestUri!.Host); Paths.Add(request.RequestUri.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri.AbsolutePath.EndsWith("/access", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { actorId = Guid.Empty, grantRevision = 1, tenantScope = scope, capabilities = new[] { "deployments.enroll" } }) };
            Assert.EndsWith("/owner/enroll", request.RequestUri.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Enrollments.Add(document.RootElement.Clone());
            if (failureStatus is not null) return new(failureStatus.Value) { Content = new StringContent("SENSITIVE_REMOTE_BODY") };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { deploymentId = Guid.Empty, tenantId = document.RootElement.GetProperty("request").GetProperty("tenantId").GetString(), revision = 1 }) };
        }
    }

    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory logs) : Renderer(services, logs)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Component render failed", exception);
        public async Task<int> Add<T>() where T : IComponent { var id = AssignRootComponentId(InstantiateComponent(typeof(T))); await RenderRootComponentAsync(id); return id; }
        private RenderTreeFrame[] Frames(int id) { var frames = GetCurrentRenderTreeFrames(id); return frames.Array.Take(frames.Count).ToArray(); }
        public List<(int Id, IComponent Instance)> Components(int root)
        {
            var result = new List<(int, IComponent)>(); var visited = new HashSet<int>();
            void Walk(int id) { if (!visited.Add(id)) return; foreach (var frame in Frames(id)) if (frame.FrameType == RenderTreeFrameType.Component) { result.Add((frame.ComponentId, frame.Component)); Walk(frame.ComponentId); } }
            Walk(root); return result;
        }
        private IEnumerable<RenderTreeFrame> Subtree(int root) => Frames(root).Concat(Components(root).SelectMany(c => Frames(c.Id)));
        public (int Id, IComponent Instance) Field(int root, string label) => Components(root).Single(c => c.Instance is MudTextField<string> f && f.Label == label);
        public List<(string Name, ulong Id)> Events(int root) => Subtree(root).Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeEventHandlerId != 0).Select(f => (f.AttributeName, f.AttributeEventHandlerId)).ToList();
        public string Text(int root) => string.Concat(Subtree(root).Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
        public Task Emit(int root, string name, EventArgs args) => DispatchEventAsync(Assert.Single(Events(root), e => e.Name == name).Id, null!, args);
        public async Task EmitIfBound(int root, string name, EventArgs args) { if (Events(root).Any(e => e.Name == name)) await Emit(root, name, args); }
        public async Task Click(int root, string label)
        {
            var button = Components(root).Where(c => c.Instance is MudButton).Single(c => Text(c.Id).Trim() == label);
            await Emit(button.Id, "onclick", new MouseEventArgs());
        }
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://offline.invalid/", "https://offline.invalid/settings/connections");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Unexpected test navigation");
    }
}
#pragma warning restore BL0006
