using System.Security.Claims;
using CSweet.Api.Agents;
using CSweet.Api.Auth;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace CSweet.UnitTests;

public sealed class PluginAccountUpgradeEndpointTests
{
    [Fact]
    public async Task EveryUpgradeRouteRequiresAuthenticationAndUnauthorizedAdministratorAccessDoesNotFenceSource()
    {
        await using var f=await PluginAccountUpgradeTests.F.Create();var builder=WebApplication.CreateBuilder();
        builder.Services.AddScoped(_=>f.Service);builder.Services.AddSingleton<IAuthenticationService,Auth>();await using var app=builder.Build();app.MapPluginAccountEndpoints();
        var routes=((IEndpointRouteBuilder)app).DataSources.SelectMany(x=>x.Endpoints).OfType<RouteEndpoint>()
            .Where(x=>x.RoutePattern.RawText!.Contains("/upgrade",StringComparison.Ordinal)).ToArray();
        Assert.Equal(5,routes.Length);Assert.All(routes,r=>Assert.NotEmpty(r.Metadata.GetOrderedMetadata<IAuthorizeData>()));
        var review=Assert.Single(routes,r=>r.RoutePattern.RawText!.EndsWith("/upgrade/review",StringComparison.Ordinal));
        var context=new DefaultHttpContext{RequestServices=app.Services};context.Request.Method="GET";context.Response.Body=new MemoryStream();
        context.User=new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,f.Inner.Manager.ToString("D"))],"synthetic"));
        context.Request.RouteValues["installationId"]=f.Target.Id.ToString("D");
        await review.RequestDelegate!(context);Assert.Equal(StatusCodes.Status403Forbidden,context.Response.StatusCode);
        Assert.True(f.Source.IsEnabled);Assert.Equal(0,f.Gateway.Adopts);
    }
    [Theory]
    [InlineData("/upgrade")][InlineData("/upgrade/intent/resume")][InlineData("/upgrade/intent/cancel")]
    public async Task ProductionAntiforgeryGateRejectsUpgradeMutationsBeforeTheyReachTheirHandler(string suffix)
    {
        var services=new ServiceCollection().AddLogging().AddOptions().AddProblemDetails().AddRouting();
        await using var provider=services.BuildServiceProvider();var reached=false;
        var middleware=new ApiAntiforgeryMiddleware(_=>{reached=true;return Task.CompletedTask;});
        var context=new DefaultHttpContext{RequestServices=provider};context.Response.Body=new MemoryStream();context.Request.Method="POST";
        context.Request.Path="/api/plugins/installations/synthetic/account-management"+suffix;
        context.User=new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,Guid.NewGuid().ToString("D"))],"synthetic"));
        var antiforgery=new RejectAntiforgery();await middleware.InvokeAsync(context,antiforgery,new Production());
        Assert.False(reached);Assert.Equal(1,antiforgery.Validations);Assert.Equal(StatusCodes.Status400BadRequest,context.Response.StatusCode);
    }
    private sealed class RejectAntiforgery:IAntiforgery
    {
        public int Validations;
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context)=>throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext context)=>throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext context)=>Task.FromResult(false);
        public Task ValidateRequestAsync(HttpContext context){Validations++;throw new AntiforgeryValidationException("synthetic missing proof");}
        public void SetCookieTokenAndHeader(HttpContext context)=>throw new NotSupportedException();
    }
    private sealed class Production:IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="SyntheticUpgradeTests";public string EnvironmentName{get;set;}="Production";
        public string ContentRootPath{get;set;}=AppContext.BaseDirectory;public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
        public string WebRootPath{get;set;}=AppContext.BaseDirectory;public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
    private sealed class Auth:IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context,string? scheme)=>Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context,string? scheme,AuthenticationProperties? properties){context.Response.StatusCode=401;return Task.CompletedTask;}
        public Task ForbidAsync(HttpContext context,string? scheme,AuthenticationProperties? properties){context.Response.StatusCode=403;return Task.CompletedTask;}
        public Task SignInAsync(HttpContext context,string? scheme,ClaimsPrincipal principal,AuthenticationProperties? properties)=>throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context,string? scheme,AuthenticationProperties? properties)=>throw new NotSupportedException();
    }
}
