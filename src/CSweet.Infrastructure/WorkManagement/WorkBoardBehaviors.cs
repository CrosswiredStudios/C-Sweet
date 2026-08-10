using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class StandardBoardBehavior : IWorkBoardBehavior
{
    public WorkBoardKind BoardKind => WorkBoardKind.Standard;
    public EmployeeType? OwnerType => null;
    public bool UsesClaimLease => false;
    public bool CanOwnerCreate => false;
    public bool CanOwnerTransitionDirectly => false;
}

public sealed class HumanPersonalBoardBehavior : IWorkBoardBehavior
{
    public WorkBoardKind BoardKind => WorkBoardKind.Personal;
    public EmployeeType? OwnerType => EmployeeType.Human;
    public bool UsesClaimLease => false;
    public bool CanOwnerCreate => true;
    public bool CanOwnerTransitionDirectly => true;
}

public sealed class AgentPersonalBoardBehavior : IWorkBoardBehavior
{
    public WorkBoardKind BoardKind => WorkBoardKind.Personal;
    public EmployeeType? OwnerType => EmployeeType.Agent;
    public bool UsesClaimLease => true;
    public bool CanOwnerCreate => true;
    public bool CanOwnerTransitionDirectly => false;
}
