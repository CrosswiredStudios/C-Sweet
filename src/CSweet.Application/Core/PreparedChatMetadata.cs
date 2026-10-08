using CSweet.WorkManagement.Contracts;

namespace CSweet.Application.Core;

public sealed record PreparedChatSender(Guid OrganizationUserId, string DisplayName, string EmployeeType, string? Role);
public sealed record PreparedChatMention(Guid OrganizationUserId, string DisplayName, string EmployeeType, int Offset, int Length);
public sealed record PreparedChatAttachment(Guid Id, Guid MessageId, string FileName, string ContentType, long SizeBytes, string Sha256);

/// <summary>Frozen platform metadata used in both the prompt and durable event payload.</summary>
public sealed record PreparedChatMetadata(PreparedChatSender Sender, IReadOnlyList<PreparedChatMention> Mentions,
    IReadOnlyList<PreparedChatAttachment> Attachments, IReadOnlyDictionary<string, string> Context, AgentWorkContext? WorkContext);
