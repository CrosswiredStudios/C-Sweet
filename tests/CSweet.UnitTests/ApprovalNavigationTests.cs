using System.Reflection;
using CSweet.Contracts.Core;
using CSweet.UI.Pages;

namespace CSweet.UnitTests;

public sealed class ApprovalNavigationTests
{
    [Theory]
    [InlineData("Pending", true, true)]
    [InlineData("Approved", true, false)]
    [InlineData("Pending", false, false)]
    public void SelectedApprovalOnlyMovesFirstWhenItIsActionable(string status, bool canDecide, bool actionable)
    {
        var other = Item("Pending", true);
        var selected = Item(status, canDecide);
        var page = new Approvals { SelectedApprovalId = selected.Id };
        typeof(Approvals).GetField("_dashboard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new ApprovalDashboardResponse(Guid.NewGuid(), 1, [other, selected]));
        Assert.Equal(actionable, Read<bool>(page, "HasActionableSelectedApproval"));
        var visible = Read<IReadOnlyList<ApprovalDashboardItemResponse>>(page, "VisibleItems");
        Assert.Equal(actionable ? selected.Id : other.Id, visible[0].Id);
        if (!actionable) Assert.DoesNotContain(visible, x => x.Id == selected.Id);
    }

    [Fact]
    public void UnknownApprovalKeepsTheNormalInboxAndReportsItUnavailable()
    {
        var item = Item("Pending", true);
        var page = new Approvals { SelectedApprovalId = Guid.NewGuid() };
        typeof(Approvals).GetField("_dashboard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new ApprovalDashboardResponse(Guid.NewGuid(), 1, [item]));
        Assert.False(Read<bool>(page, "HasActionableSelectedApproval"));
        Assert.Equal(item, Assert.Single(Read<IReadOnlyList<ApprovalDashboardItemResponse>>(page, "VisibleItems")));
    }

    private static T Read<T>(Approvals page, string name) => (T)typeof(Approvals)
        .GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static ApprovalDashboardItemResponse Item(string status, bool canDecide) =>
        new(Guid.NewGuid(), ApprovalDashboardKinds.ResourceChange, "Plan", "Summary", status,
            "Producer", "Manager", DateTimeOffset.UtcNow, null, "/approvals", canDecide);
}
