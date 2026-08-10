using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkBoardBehavior
{
    WorkBoardKind BoardKind { get; }
    EmployeeType? OwnerType { get; }
    bool UsesClaimLease { get; }
    bool CanOwnerCreate { get; }
    bool CanOwnerTransitionDirectly { get; }
}
