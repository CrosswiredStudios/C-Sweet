namespace CSweet.Domain.WorkManagement;

/// <summary>Immutable consent to publish one selected human instruction, without copying its private chat.</summary>
public sealed class WorkInstructionPublication
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public Guid SourceEmployeeId { get; set; }
    public Guid SourceConversationId { get; set; }
    public Guid SourceMessageId { get; set; }
    public string SourceChecksum { get; set; } = "";
    public int SelectionOffset { get; set; }
    public int SelectionLength { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid PublishedBoardId { get; set; }
    public Guid CommentId { get; set; }
    public string InstructionChecksum { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
