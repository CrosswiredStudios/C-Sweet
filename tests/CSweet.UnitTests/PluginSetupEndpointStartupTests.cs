using CSweet.Api.Agents;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class PluginSetupEndpointStartupTests
{
    [Fact]
    public async Task ConnectorBindingEndpointBuildsWithoutTreatingServiceMethodAsHttpBinder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IPluginSetupService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IPluginBootstrapCapabilityService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IPluginSecretStore>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IAuditEventWriter>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<CSweetDbContext>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ConnectorBindingService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<ConnectorProfileApprovalService>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.MapPluginSetupEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText!.EndsWith("/dependencies/{dependencyId}", StringComparison.Ordinal));
        Assert.DoesNotContain(endpoints, endpoint => endpoint.RoutePattern.RawText!.Contains("standing-policy", StringComparison.Ordinal));
    }
}
