using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class ProjectNamingEndpointTests
{
    [Fact]
    public async Task Rename_endpoint_validates_revision_name_and_human_authority()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var human = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), ApplicationUserId = Guid.NewGuid(), PermissionLevel = OrganizationPermissionLevel.Owner };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, Name = "Old title", AccountableManagerOrganizationUserId = human.Id };
        db.AddRange(human, project); await db.SaveChangesAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db); builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ProjectWorkPolicy>(); builder.Services.AddSingleton<ProjectSetupService>();
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication("test").AddCookie("test", options => options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, human.ApplicationUserId!.Value.ToString())], "test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapProjectSetupEndpoints(); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var route = $"/api/core/organizations/{human.OrganizationId}/project-setup/projects/{project.Id}/name";
            using var invalid = await client.PutAsJsonAsync(route, new RenameProjectRequest(" ", 1));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var success = await client.PutAsJsonAsync(route, new RenameProjectRequest("New title", 1));
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            Assert.Equal(new(project.Id, "New title", 2), await success.Content.ReadFromJsonAsync<RenameProjectResponse>());
            using var stale = await client.PutAsJsonAsync(route, new RenameProjectRequest("Overwrite", 1));
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var foreign = await client.PutAsJsonAsync(route.Replace(human.OrganizationId.ToString(), Guid.NewGuid().ToString()), new RenameProjectRequest("Foreign", 2));
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            human.EmployeeType = EmployeeType.Agent; await db.SaveChangesAsync();
            using var denied = await client.PutAsJsonAsync(route, new RenameProjectRequest("Agent", 2));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("New title", project.Name);
        }
        finally { await app.StopAsync(); }
    }
}
