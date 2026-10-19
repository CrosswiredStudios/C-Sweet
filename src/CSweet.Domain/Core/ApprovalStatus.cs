namespace CSweet.Domain.Core;

public enum ApprovalStatus
{
    NotRequired = 0,
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    RevisionRequested = 4,
    Cancelled = 5
}
