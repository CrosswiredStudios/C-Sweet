using CSweet.Contracts.Core;

namespace CSweet.Promo.Demo;

public sealed partial class DemoData
{
    public ProjectPortfolioResponse Portfolio => new(Epoch, 1, 1,
        [new(ProjectId, ProjectName, Brief, "Active", "First milestone", null, null, Id(20),
            Epoch.AddDays(21), null, null, 1, Epoch, 1, 1, Board.Items.Count(x => x.Status != "Completed"), 0, 0, null)
            { AccountableManagerName = Cast[0].Name }]);
    public IReadOnlyList<RoleResponse> Roles => Cast.Select((p, i) => new RoleResponse(
        Id(40+i), OrganizationId, p.Role, p.Role, "[]", 1, Epoch, Epoch)).ToArray();
    public TeamDirectoryResponse Teams => new(OwnerId, false,
        [new(Id(100), "product", "Product team", "The team delivering the first milestone.", Id(20), Cast[0].Name,
            1, false, Cast.Length, 0, Cast.Length,
            Cast.Select((p,i) => new TeamMembershipResponse(Id(200+i), Id(20+i), p.Name, "Agent", Id(40+i), p.Role, i == 0, Epoch, null)).ToArray())
            { WorkstreamId = ProjectId, BoardId = BoardId }]);
}
