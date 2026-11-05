using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ActivationReviewRegressionTests
{
    [Theory]
    [InlineData("stale-consumer-revision")]
    [InlineData("missing-approved-base")]
    public async Task NativeChoiceDoesNotClaimSelectionWhenActivationWillRejectTheBindingSet(string change)
    {
        await using var fixture = await ConnectorActivationApprovalTests.Native();
        var db = fixture.Legacy.Db;
        if (change == "stale-consumer-revision")
            fixture.Legacy.Requester.Grant!.GrantRevision++;
        else
            db.Remove(await db.AgentCapabilityBindings.SingleAsync(x => x.Capability == "example.api.categories.v1"));
        await db.SaveChangesAsync();
        var service = new ConnectorBindingService(db);
        var dependency = Assert.Single(await service.GetChoicesAsync(fixture.Organization, fixture.Legacy.Requester.Id, default));
        Assert.False(Assert.Single(dependency.ResourceChoices).Selected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateRequiredBindingsAsync(fixture.Legacy.Requester, default));
    }
}
