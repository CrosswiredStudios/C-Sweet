using System.Reflection;
using System.Security.Claims;
using CSweet.Api.Core;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class DelegationPacketRegressionTests
{
    [Fact]
    public async Task PacketUsesTrackedWorkAndRetainsItsInputsOnIdempotentRetry()
    {
        await using var f = await TrackedDelegationTests.Fixture.Create();
        f.Matt.ApplicationUserId = Guid.NewGuid(); f.Matt.PermissionLevel = OrganizationPermissionLevel.Owner;
        f.Db.AgentInstallations.AddRange(new CSweet.Domain.Setup.AgentInstallation { Id = f.Naomi.AgentInstallationId!.Value, BusinessId = f.Org.ToString("D") },
            new CSweet.Domain.Setup.AgentInstallation { Id = f.Gabriel.AgentInstallationId!.Value, BusinessId = f.Org.ToString("D") });
        await f.Db.SaveChangesAsync();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, f.Matt.ApplicationUserId.Value.ToString("D"))], IdentityConstants.ApplicationScheme)) };
        var request = new CreateDelegationRequest(f.Org.ToString("D"), f.Naomi.AgentInstallationId.Value,
            f.Gabriel.AgentInstallationId.Value, "Staffing review", f.Request().Instruction, [], "Return readiness evidence")
            { SourceConversationId = f.Source.ConversationId, SourceMessageId = f.Source.Id, IdempotencyKey = "packet-source" };
        var create = typeof(DelegationEndpoints).GetMethod("CreateAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        async Task<IResult> Invoke(CreateDelegationRequest input) => await (Task<IResult>)create.Invoke(null,
            [f.Org, input, http, f.Db, f.Service, CancellationToken.None])!;
        Assert.IsAssignableFrom<IResult>(await Invoke(request));
        await Invoke(request);
        var packet = await f.Db.Delegations.SingleAsync();
        var tracked = await f.Db.TrackedDelegations.SingleAsync();
        Assert.Equal(tracked.Id, packet.TrackedDelegationId);
        Assert.Equal("Return readiness evidence", packet.ReturnContract);
        Assert.NotEmpty(await f.Db.CoreWorkTasks.ToListAsync());
        Assert.NotEmpty(await f.Db.AgentPlatformEventOutbox.ToListAsync());
        Assert.Contains(await f.Db.AuditOutbox.ToListAsync(), x => x.SourceEntityId == packet.Id);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Invoke(request with { Title = "Changed retry" }));
    }
}
