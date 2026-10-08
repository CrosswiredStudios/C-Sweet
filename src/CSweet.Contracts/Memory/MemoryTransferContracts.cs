using System.Text.Json.Serialization;

namespace CSweet.Contracts.Memory;

public sealed record MemoryTransferSelection(string Kind, Guid Id);
public sealed record PrepareMemoryTransferRequest(Guid OperationId, Guid TargetEmployeeId, string SourceScope,
    IReadOnlyList<MemoryTransferSelection> Items, string Debrief, string DebriefSensitivity = "Personal",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SourceAudienceId = null);
public sealed record MemoryTransferAudience(string Scope, Guid AudienceId, string Name);
public sealed record TransitionMemoryTransferRequest(Guid OperationId, string ExpectedToken, string Action);
public sealed record MemoryTransferResponse(Guid PackageId, Guid TargetEmployeeId, string Status, string ReviewToken,
    string Content, string Sensitivity, bool CanApprove, bool CanApply, bool CanReject, Guid? AppliedEpisodeId);
public sealed record MemoryTransferResult(Guid ReceiptId, Guid PackageId, string Status, Guid? AppliedEpisodeId, bool WasReplay);
public sealed record MemoryTransferListItem(Guid PackageId, Guid TargetEmployeeId, DateTimeOffset CreatedAt);
public sealed record MemoryTransferPage(IReadOnlyList<MemoryTransferListItem> Items, Guid? NextCursor);
