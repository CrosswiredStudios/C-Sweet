using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CSweet.Api.WorkManagement;
using CSweet.Application.Security;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task InstructionPublicationRenderedUiCommitsReviewedSelectionThroughRealHttpAndPostgres()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture, "Private 😀\r\n");
        await using var db = fixture.Context();
        var source = (await db.CoreConversationMessages.AsNoTracking().SingleAsync(x => x.Id == fixture.MessageId)).Content;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("instruction-test").AddScheme<AuthenticationSchemeOptions, InstructionTestAuthentication>("instruction-test", _ => { });
        builder.Services.AddScoped<CSweetDbContext>(_ => fixture.Context());
        builder.Services.AddScoped<IScopedActionAuthorizationService, ScopedActionAuthorizationService>();
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddScoped<IWorkInstructionPublicationService, WorkInstructionPublicationService>();
        await using var app = builder.Build(); app.UseAuthentication();
        app.MapGroup("/api/organizations/{organizationId:guid}/work/boards").MapWorkInstructionEndpoints();
        // The directory is a fixture seam. Preview/publish/discovery use the real
        // authenticated service and PostgreSQL authority/consent transaction.
        var directory = $"/api/organizations/{fixture.OrganizationId:D}/work/boards";
        var board = new WorkBoardSummaryResponse(setup.Board, fixture.OrganizationId, null, "Production", "", true, false, false, null,1,1,1,
            DateTimeOffset.UtcNow,DateTimeOffset.UtcNow, []);
        app.MapGet(directory, (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new WorkBoardDirectoryResponse([board], false)) : Results.Unauthorized());
        app.MapGet(directory + $"/{setup.Board:D}", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new WorkBoardDetailResponse(board, [], [new(setup.Item, setup.Board, Guid.NewGuid(),null,null,"Task","Paddle change","","Ready","Medium",null,0,1,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)]))
            : Results.Unauthorized());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("X-Instruction-Test-User", setup.User.ToString("D"));
        var html = await WorkInstructionUiTests.PublishThroughUiAsync(client,fixture.OrganizationId,setup.Selection.ConversationId,
            fixture.MessageId,setup.Board,setup.Item,source,setup.Selection.Offset,setup.Selection.Length);
        Assert.Contains("Instruction published", html); Assert.DoesNotContain(PrivateInstructionContext, html);
        var comment = await db.WorkItemComments.AsNoTracking().SingleAsync();
        Assert.Equal(SharedInstruction, comment.Body); Assert.Equal(fixture.HumanId, comment.AuthorSubjectId);
        Assert.Single(await db.WorkInstructionPublications.AsNoTracking().ToListAsync());
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.EventType == WorkItemDiscussion.Changed).ToListAsync());
        Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged).ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task InstructionPublicationHttpUsesAuthenticatedHumanAndSupportsReviewedReplayAndDiscovery()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var setup = await PrepareInstructionAsync(fixture);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = [] });
        builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("instruction-test").AddScheme<AuthenticationSchemeOptions, InstructionTestAuthentication>("instruction-test", _ => { });
        builder.Services.AddScoped<CSweetDbContext>(_ => fixture.Context());
        builder.Services.AddScoped<IScopedActionAuthorizationService, ScopedActionAuthorizationService>();
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddScoped<IWorkInstructionPublicationService, WorkInstructionPublicationService>();
        await using var app = builder.Build(); app.UseAuthentication();
        app.MapGroup("/api/organizations/{organizationId:guid}/work/boards").MapWorkInstructionEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var path = $"/api/organizations/{fixture.OrganizationId:D}/work/boards/{setup.Board:D}/items/{setup.Item:D}/instructions";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path + "/preview", setup.Selection)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Instruction-Test-User", setup.User.ToString("D"));
        var previewResponse = await client.PostAsJsonAsync(path + "/preview", setup.Selection);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<WorkInstructionPreview>())!;
        Assert.Equal(SharedInstruction, preview.Instruction);
        var request = new PublishWorkInstructionRequest(Guid.NewGuid(), setup.Selection, preview.ReviewToken);
        var publicationResponse = await client.PostAsJsonAsync(path + "/publish", request);
        Assert.Equal(HttpStatusCode.OK, publicationResponse.StatusCode);
        var publication = (await publicationResponse.Content.ReadFromJsonAsync<WorkInstructionPublicationResponse>())!;
        var replay = (await (await client.PostAsJsonAsync(path + "/publish", request)).Content.ReadFromJsonAsync<WorkInstructionPublicationResponse>())!;
        Assert.Equal(publication.Id, replay.Id); Assert.True(replay.Replayed);
        var listResponse = await client.GetAsync(path + "/publications");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(publication.Id, Assert.Single((await listResponse.Content.ReadFromJsonAsync<WorkInstructionPublicationPage>())!.Items).Id);
        Assert.DoesNotContain(PrivateInstructionContext, await listResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/publish", request with { ReviewToken = new string('a', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/preview", setup.Selection with { Offset = -1 })).StatusCode);
        await using var db = fixture.Context();
        await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId && x.Action == WorkItemActions.ReadComments)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/publications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/publish", request)).StatusCode);
        Assert.Single(await db.WorkInstructionPublications.ToListAsync()); Assert.Single(await db.WorkItemComments.ToListAsync());
    }

    private sealed class InstructionTestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Guid.TryParse(Request.Headers["X-Instruction-Test-User"], out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString("D"))], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
