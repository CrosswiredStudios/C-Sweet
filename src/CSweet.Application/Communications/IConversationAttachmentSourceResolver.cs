using CSweet.Contracts.Communications;

namespace CSweet.Application.Communications;

/// <summary>
/// Resolves opaque conversation attachment references without exposing storage implementation details.
/// Additional connector-backed resolvers can implement this contract without changing chat or agent contracts.
/// </summary>
public interface IConversationAttachmentSourceResolver
{
    string Source { get; }

    Task<ResolvedConversationAttachment?> ResolveAsync(
        Guid organizationId,
        Guid attachmentId,
        CancellationToken cancellationToken = default);
}

public sealed record ResolvedConversationAttachment(
    CommunicationMessageAttachmentResponse Descriptor,
    Guid ConversationId,
    Guid MediaAssetId,
    Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
