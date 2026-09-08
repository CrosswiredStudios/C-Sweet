using System.Net;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public sealed class CalendarEndpointTests
{
    [Theory]
    [InlineData("?from=2026-09-01T00:00:00Z&to=2026-10-01T00:00:00Z")]
    [InlineData("/reminders")]
    public async Task CalendarRoutesRequireAuthentication(string suffix)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                services.AddDbContext<CSweetDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            });
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/api/organizations/{Guid.NewGuid()}/calendar{suffix}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
