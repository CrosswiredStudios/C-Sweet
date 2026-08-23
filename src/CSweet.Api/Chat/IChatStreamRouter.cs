using System.Threading.Channels;

namespace CSweet.Api.Chat;

public sealed record ChatStreamChunk(int Sequence, string Delta, bool IsFinal, string? Error = null, string Kind = "output", IReadOnlyDictionary<string, string>? Metadata = null, int Attempt = 0, string Sensitivity = "Internal");

public interface IChatStreamRouter
{
    ChannelReader<ChatStreamChunk> Subscribe(Guid conversationId);

    void Publish(Guid conversationId, ChatStreamChunk chunk);

    void Complete(Guid conversationId);

    void BindAlias(Guid aliasId, Guid streamId);

    void UnbindAlias(Guid aliasId, Guid streamId);
}
