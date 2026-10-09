namespace CSweet.Contracts.WorkManagement;

public sealed record SelectWorkInstructionRequest(Guid ConversationId, Guid MessageId, int Offset, int Length);
public sealed record WorkInstructionPreview(string Instruction, string WorkItemTitle, string BoardName, string ReviewToken);
public sealed record PublishWorkInstructionRequest(Guid OperationId, SelectWorkInstructionRequest Selection, string ReviewToken);
public sealed record WorkInstructionPublicationResponse(Guid Id, Guid WorkItemId, Guid CommentId, string Status, bool Replayed);
public sealed record WorkInstructionPublicationPage(IReadOnlyList<WorkInstructionPublicationResponse> Items, Guid? NextCursor);
