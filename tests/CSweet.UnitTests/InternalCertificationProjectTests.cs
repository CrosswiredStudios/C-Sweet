using System.Reflection;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class InternalCertificationProjectTests
{
    [Fact]
    public async Task HumanPortfolioAndDeepLinkExcludeFixturesButPreserveCertificationEvidence()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var appUser = Guid.NewGuid(); var owner = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new() { Id = owner, OrganizationId = org, ApplicationUserId = appUser,
            IsActive = true, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner });
        var game = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Name = "Game", Status = WorkstreamStatus.Active };
        var fixture = new Workstream { Id = Guid.NewGuid(), OrganizationId = org, Name = "Toolchain certification",
            ProfileKey = WorkstreamPurpose.ToolchainCertificationProfile, Status = WorkstreamStatus.Active,
            AccountableManagerOrganizationUserId = owner };
        db.Workstreams.AddRange(game, fixture); await db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, appUser.ToString()) }, "test")) };
        var portfolioMethod = typeof(WorkstreamInspectionEndpoints).GetMethod("InspectPortfolioAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (Task<IResult>)portfolioMethod.Invoke(null, new object[] { org, http, db, CancellationToken.None })!;
        var portfolio = Assert.IsType<Ok<ProjectPortfolioResponse>>(result).Value!;
        Assert.Equal(1, portfolio.Total);
        Assert.Equal(game.Id, Assert.Single(portfolio.Projects).Id);
        var detailMethod = typeof(WorkstreamInspectionEndpoints).GetMethod("InspectAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.IsType<NotFound>(await (Task<IResult>)detailMethod.Invoke(null,
            new object[] { org, fixture.Id, http, db, CancellationToken.None })!);
        Assert.Equal(fixture.Id, (await db.Workstreams.SingleAsync(x => x.ProfileKey == WorkstreamPurpose.ToolchainCertificationProfile)).Id);
    }
}

