using System.Net;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Routing;

namespace CSweet.IntegrationTests;

public sealed class DocumentEndpointRouteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollaborativeDocumentRoutesHaveOneAuthenticatedEndpoint(bool includeDocumentId)
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var organizationId = Guid.NewGuid();
        var path = $"/api/organizations/{organizationId:D}/documents";
        if (includeDocumentId) path += $"/{Guid.NewGuid():D}";

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void LegacyPlanningDocumentRoutesAreNotMapped()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(routes,
            route => route.Contains("planning-documents", StringComparison.OrdinalIgnoreCase));
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                    services.AddDbContext<CSweetDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                });
            });
}
