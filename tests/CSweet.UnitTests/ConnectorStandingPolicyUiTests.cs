using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CSweet.Api.Agents;
using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Setup;
using CSweet.UI.Components;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace CSweet.UnitTests;

public sealed partial class ConnectorStandingPolicyTests
{
    [Fact]
    public async Task NativeOwnerEndpointCanReviewCompletedWorkApproveFutureRulesAndRevokeWithoutExecuting()
    {
        await using var f = await Fixture.Create();
        var action = await AttachAction(f);
        f.Plan.Status = "Completed"; f.Plan.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Inner.Db.SaveChangesAsync();
        await using var app = PolicyApp(f);
        var get = await Call(app, f, action, "GET"); Assert.Equal(200, get.Status);
        var setup = JsonSerializer.Deserialize<ConnectorStandingPolicySetup>(get.Body, Json)!;
        Assert.NotNull(setup.Review); Assert.NotEmpty(setup.Review.FieldReviews);
        Assert.Equal("Specialist", setup.Review.RequesterName);
        Assert.Null(setup.Policy);
        var editor = new ConnectorStandingPolicyEditor(setup, f.Clock.GetUtcNow());
        var request = editor.Build();
        var put = await Call(app, f, action, "PUT", request); Assert.Equal(200, put.Status);
        var policy = JsonSerializer.Deserialize<ConnectorStandingPolicyView>(put.Body, Json)!;
        Assert.Equal(policy.Id, JsonSerializer.Deserialize<ConnectorStandingPolicyView>((await Call(app, f, action, "PUT", request)).Body, Json)!.Id);
        Assert.Equal("Completed", f.Plan.Status);
        Assert.Single(f.Inner.Db.ActionProposals); // Governance did not create or approve a new action.
        Assert.Empty(f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorStandingPolicyService.AuthorizationKind));
        var revoke = await Call(app, f, action, "POST", new RevokeConnectorStandingPolicyRequest(policy.Id, policy.Revision));
        Assert.Equal(204, revoke.Status);
        Assert.Equal("Revoked", JsonSerializer.Deserialize<ConnectorStandingPolicySetup>((await Call(app, f, action, "GET")).Body, Json)!.Policy!.Status);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("agent-session")]
    [InlineData("manager")]
    [InlineData("agent-owner")]
    [InlineData("foreign")]
    [InlineData("duplicate-identity")]
    [InlineData("duplicate-identifier")]
    public async Task NativeOwnerEndpointsRejectNonHumanOrOtherTenantAuthority(string change)
    {
        await using var f = await Fixture.Create(); var action = await AttachAction(f);
        var request = await f.Request();
        if (change == "manager") f.Owner.PermissionLevel = OrganizationPermissionLevel.Manager;
        if (change == "agent-owner") f.Owner.EmployeeType = EmployeeType.Agent;
        await f.Inner.Db.SaveChangesAsync();
        await using var app = PolicyApp(f);
        foreach (var method in new[] { "GET", "PUT", "POST" })
        {
            var result = await Call(app, f, action, method, method == "POST" ? new RevokeConnectorStandingPolicyRequest(Guid.NewGuid(), 1) : request,
                change == "anonymous" ? null : change == "agent-session" ? "AgentBroker" : IdentityConstants.ApplicationScheme,
                change == "foreign" ? Guid.NewGuid() : null, principalVariation: change);
            Assert.Equal(403, result.Status); Assert.DoesNotContain("first content", result.Body);
        }
        Assert.Empty(f.Inner.Db.PluginOperationalStates);
        foreach (var endpoint in Routes(app))
        {
            var policy = endpoint.Metadata.GetMetadata<AuthorizationPolicy>(); Assert.NotNull(policy);
            Assert.Equal(IdentityConstants.ApplicationScheme, Assert.Single(policy.AuthenticationSchemes));
        }
    }

    [Theory]
    [InlineData("PUT", "missing", 400)]
    [InlineData("PUT", "other-user", 400)]
    [InlineData("PUT", "valid", 200)]
    [InlineData("POST", "missing", 400)]
    [InlineData("POST", "other-user", 400)]
    [InlineData("POST", "valid", 204)]
    public async Task NativePolicyMutationsRequireARealUserBoundAntiforgeryToken(string method, string csrf, int status)
    {
        await using var f = await Fixture.Create(); var action = await AttachAction(f);
        var request = await f.Request();
        var policy = method == "POST" ? await f.Approve() : null;
        await using var app = PolicyApp(f);
        var result = await Call(app, f, action, method,
            policy is null ? request : new RevokeConnectorStandingPolicyRequest(policy.Id, policy.Revision), csrf: csrf);
        Assert.Equal(status, result.Status);
        var stored = await f.Service.GetAsync(f.Org, f.Requester, f.UserId, f.Plan.Capability, default);
        if (status == 400)
        {
            Assert.Contains("invalid_antiforgery_token", result.Body);
            if (policy is null) Assert.Null(stored); else Assert.Equal("Approved", stored!.Status);
        }
        else Assert.Equal(method == "PUT" ? "Approved" : "Revoked", stored!.Status);
        Assert.Equal("Prepared", f.Plan.Status);
        Assert.Empty(f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorStandingPolicyService.AuthorizationKind));
    }

    [Fact]
    public async Task ChangingTheDisplayedAgentRequiresFreshOwnerReview()
    {
        await using var f = await Fixture.Create(); var action = await AttachAction(f); var request = await f.Request();
        f.Inner.Db.CoreOrganizationUsers.Single(x => x.AgentInstallationId == f.Requester).DisplayName = "Different specialist";
        await f.Inner.Db.SaveChangesAsync();
        await using var app = PolicyApp(f);
        Assert.Equal(403, (await Call(app, f, action, "PUT", request)).Status);
        Assert.Empty(f.Inner.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task PolicyRejectsOversizedCombinedLiteralsAndUnknownTimeZones()
    {
        await using var f = await Fixture.Create();
        var definition = f.Definition();
        var values = Enumerable.Range(0, 32).Select(x => JsonSerializer.SerializeToElement(x + new string('x', 1200))).ToArray();
        var large = definition with { Fields = definition.Fields.Select(x => x with { AllowAny = false, AllowedValues = values }).ToArray() };
        Assert.Throws<ArgumentException>(() => ConnectorStandingPolicyRules.Validate(large, f.Operation, f.Clock.GetUtcNow()));
        Assert.Throws<ArgumentException>(() => ConnectorStandingPolicyRules.Validate(definition with { TimeZoneId = "not/a/timezone" }, f.Operation, f.Clock.GetUtcNow()));
        var bounded = definition with { Fields = definition.Fields.Select(x => x with { AllowAny = false, AllowedValues = values.Take(1).ToArray() }).ToArray() };
        ConnectorStandingPolicyRules.Validate(bounded, f.Operation, f.Clock.GetUtcNow());
    }

    [Fact]
    public async Task EditorRejectsAmbiguousAndMissingDaylightSavingExpiryTimes()
    {
        await using var f = await Fixture.Create();
        var review = await f.Service.ReviewAsync(f.Org, f.Requester, f.UserId, f.Plan.Id, f.Plan.PlanHash, default);
        var editor = new ConnectorStandingPolicyEditor(new(review, null), DateTimeOffset.Parse("2026-01-01T00:00:00Z"))
        { TimeZoneId = "America/Los_Angeles", ExpiryDate = new(2026, 3, 8), ExpiryTime = new(2, 30, 0) };
        Assert.Throws<ArgumentException>(() => editor.Build());
        editor.ExpiryDate = new(2026, 11, 1); editor.ExpiryTime = new(1, 30, 0);
        Assert.Throws<ArgumentException>(() => editor.Build());
        editor.ExpiryTime = new(3, 0, 0);
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T11:00:00Z"), editor.Build().Definition.ExpiresAt);
    }

    [Fact]
    public async Task NativePolicySaveRejectsCrossActionAndStaleBindings()
    {
        await using var f = await Fixture.Create(); var action = await AttachAction(f); var request = await f.Request();
        await using var app = PolicyApp(f);
        Assert.Equal(409, (await Call(app, f, action, "PUT", request with { TemplatePlanId = Guid.NewGuid() })).Status);
        Assert.Equal(403, (await Call(app, f, action, "PUT", request with { ReviewHash = "changed" })).Status);
        Assert.Equal(404, (await Call(app, f, Guid.NewGuid(), "GET")).Status);
        Assert.Empty(f.Inner.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task OwnerCanRevokeFromNativePageAfterConnectionAuthorityIsLost()
    {
        await using var f = await Fixture.Create(); var action = await AttachAction(f); var policy = await f.Approve();
        f.Inner.Connection.Status = CSweet.Domain.Setup.PluginConnectionStatus.ReauthorizationRequired;
        await f.Inner.Db.SaveChangesAsync();
        await using var app = PolicyApp(f);
        var setup = JsonSerializer.Deserialize<ConnectorStandingPolicySetup>((await Call(app, f, action, "GET")).Body, Json)!;
        Assert.Null(setup.Review); Assert.NotNull(setup.UnavailableReason); Assert.Equal(policy.Id, setup.Policy!.Id);
        Assert.Equal(204, (await Call(app, f, action, "POST", new RevokeConnectorStandingPolicyRequest(policy.Id, policy.Revision))).Status);
    }

    [Fact]
    public async Task NativeFormRendersProviderTextSafelyAndCannotApproveDuringRendering()
    {
        await using var f = await Fixture.Create();
        var review = await f.Service.ReviewAsync(f.Org, f.Requester, f.UserId, f.Plan.Id, f.Plan.PlanHash, default);
        review = review with { AccountName = "<script>account()</script>", OperationDescription = "<img src=x onerror=attack()>" };
        var services = new ServiceCollection().AddLogging(); services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoPolicyJavaScript>(); services.AddSingleton<NavigationManager, PolicyNavigation>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var called = false;
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<ConnectorStandingPolicyForm>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ConnectorStandingPolicyForm.Setup)] = new ConnectorStandingPolicySetup(review, null),
                [nameof(ConnectorStandingPolicyForm.Approved)] = EventCallback.Factory.Create<ApproveConnectorStandingPolicyRequest>(this, _ => called = true)
            }));
            return output.ToHtmlString();
        });
        Assert.False(called); Assert.Contains("Review these rules", html);
        Assert.DoesNotContain("<script>", html); Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("Approve this policy", html); // The explicit review/consent step has not occurred.
        Assert.DoesNotContain(review.PlanHash, html); Assert.DoesNotContain(review.ReviewHash, html);
        Assert.DoesNotContain("textarea name=\"json", html);
        Assert.Empty(f.Inner.Db.PluginOperationalStates);
    }

    [Fact]
    public async Task EditorPreservesExactValuesOmissionAndByteLimitsWithoutImplicitBroadening()
    {
        await using var f = await Fixture.Create();
        var review = await f.Service.ReviewAsync(f.Org, f.Requester, f.UserId, f.Plan.Id, f.Plan.PlanHash, default);
        review = review with { Media = new("small.mp4", 12345, "video/mp4") };
        var editor = new ConnectorStandingPolicyEditor(new(review, null), f.Clock.GetUtcNow());
        var request = editor.Build();
        Assert.All(request.Definition.Fields, rule => { Assert.False(rule.AllowAny); Assert.False(rule.AllowOmission); Assert.Single(rule.AllowedValues); });
        Assert.Equal(12345, request.Definition.MaximumMediaBytes);
        Assert.Equal("video/mp4", Assert.Single(request.Definition.AllowedMediaTypes!));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, Json),
            JsonSerializer.SerializeToElement(editor.Build(), Json))); // A retry preserves exact values and timestamps.
        var optional = new ConnectorPolicyFieldReview(new("body", "/optional"), "Optional value", null, null, true, []);
        var choice = new ConnectorStandingPolicyEditor.FieldChoice(optional, null);
        Assert.True(choice.Build().AllowOmission); Assert.False(choice.Build().AllowAny); Assert.Empty(choice.Build().AllowedValues);
    }

    [Fact]
    public async Task NamedTimeZoneFollowsDaylightSavingAndOvernightWindowsUseTheirStartingDay()
    {
        await using var f = await Fixture.Create();
        var plan = await f.Inner.Service.RevalidateAsync(f.Org, f.Requester, f.Plan.Id, f.Plan.PlanHash, default);
        var definition = f.Definition() with { TimeZoneId = "America/Los_Angeles", DaysOfWeek = [1], StartMinute = 9 * 60, EndMinute = 17 * 60,
            NotBefore = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), ExpiresAt = DateTimeOffset.Parse("2026-12-31T00:00:00Z") };
        Assert.True(ConnectorStandingPolicyRules.Matches(definition, plan, DateTimeOffset.Parse("2026-07-06T16:30:00Z")));
        Assert.False(ConnectorStandingPolicyRules.Matches(definition, plan, DateTimeOffset.Parse("2026-01-05T16:30:00Z")));
        Assert.True(ConnectorStandingPolicyRules.Matches(definition, plan, DateTimeOffset.Parse("2026-01-05T17:30:00Z")));
        definition = definition with { StartMinute = 21 * 60, EndMinute = 6 * 60 };
        Assert.True(ConnectorStandingPolicyRules.Matches(definition, plan, DateTimeOffset.Parse("2026-07-07T12:00:00Z")));
        Assert.False(ConnectorStandingPolicyRules.Matches(definition, plan, DateTimeOffset.Parse("2026-07-07T13:00:00Z")));
    }

    private static async Task<Guid> AttachAction(Fixture f)
    {
        var actionId = Guid.NewGuid(); f.Plan.ApprovalId = actionId;
        f.Inner.Db.ActionProposals.Add(new() { Id = actionId, OrganizationId = f.Org, AgentInstallationId = f.Requester,
            ActionType = ConnectorActionApprovalService.ActionType, Status = ProposalStatus.Approved });
        await f.Inner.Db.SaveChangesAsync(); return actionId;
    }
    private static WebApplication PolicyApp(Fixture f)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Services.AddSingleton(f.Inner.Db); builder.Services.AddSingleton<IAuditEventWriter>(f.Audit);
        builder.Services.AddSingleton<TimeProvider>(f.Clock); builder.Services.AddScoped<ConnectorPlanService>();
        builder.Services.AddScoped<ConnectorStandingPolicyService>();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(options => { options.Cookie.Name = "PolicyAntiforgery"; options.HeaderName = "X-CSWEET-CSRF"; });
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddCookie(IdentityConstants.ApplicationScheme,
            options => options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        var app = builder.Build(); app.MapConnectorStandingPolicyEndpoints(); return app;
    }
    private static IEnumerable<RouteEndpoint> Routes(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>();
    private static async Task<(int Status, string Body)> Call(WebApplication app, Fixture f, Guid actionId, string method,
        object? body = null, string? scheme = "Identity.Application", Guid? organization = null,
        string? principalVariation = null, string? csrf = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method; context.Request.RouteValues["organizationId"] = (organization ?? f.Org).ToString("D");
        context.Request.RouteValues["actionId"] = actionId.ToString("D");
        context.Request.Path = $"/api/core/organizations/{organization ?? f.Org:D}/connector-actions/{actionId:D}/standing-policy";
        if (scheme is not null) context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.UserId.ToString("D"))], scheme));
        if (principalVariation == "duplicate-identity") context.User.AddIdentity(new ClaimsIdentity(context.User.Claims, scheme));
        if (principalVariation == "duplicate-identifier") ((ClaimsIdentity)context.User.Identity!).AddClaim(new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")));
        if (method != "GET")
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json";
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());
        }
        await using var output = new MemoryStream(); context.Response.Body = output;
        var endpoint = Routes(app).Single(x => x.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
        if (csrf is null) await endpoint.RequestDelegate!(context);
        else
        {
            var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
            if (csrf != "missing")
            {
                var issuer = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = csrf == "valid" ? context.User :
                    new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))], IdentityConstants.ApplicationScheme)) };
                issuer.Request.Scheme = "https";
                var tokens = antiforgery.GetAndStoreTokens(issuer);
                context.Request.Headers.Cookie = "PolicyAntiforgery=" + tokens.CookieToken;
                context.Request.Headers["X-CSWEET-CSRF"] = tokens.RequestToken;
            }
            await new ApiAntiforgeryMiddleware(endpoint.RequestDelegate!).InvokeAsync(context, antiforgery, app.Environment);
        }
        return (context.Response.StatusCode, Encoding.UTF8.GetString(output.ToArray()));
    }
    private sealed class BodyFeature : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class NoPolicyJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private sealed class PolicyNavigation : NavigationManager
    {
        public PolicyNavigation() => Initialize("https://host.example/", "https://host.example/policy");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
