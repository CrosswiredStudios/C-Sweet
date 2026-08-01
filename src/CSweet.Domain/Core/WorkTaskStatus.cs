namespace CSweet.Domain.Core;

public enum WorkTaskStatus
{
    Backlog = 0,
    Ready = 1,
    Assigned = 2,
    Running = 3,
    WaitingForApproval = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
    Blocked = 8
}
