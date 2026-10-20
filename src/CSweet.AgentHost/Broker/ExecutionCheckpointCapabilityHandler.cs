using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ExecutionCheckpointCapabilityHandler(ExecutionCheckpointStore store) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => capability is ExecutionCheckpointCapabilities.Read or ExecutionCheckpointCapabilities.Write;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        CapabilityResult result;
        try
        {
            if (!CanHandle(request.Capability) || !session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var organization) || !Guid.TryParse(session.InstallationId, out var installation) ||
                !Guid.TryParse(session.RuntimeInstanceId, out var runtime)) throw new UnauthorizedAccessException();
            if (request.Payload.Length > 6 * 1024 * 1024) throw new ArgumentException("Checkpoint request exceeds its bound.");
            var payload=request.Payload.ToElement();
            var mode=payload.TryGetProperty("mode",out var operation)? operation.GetString():null;
            object data;
            if(request.Capability==ExecutionCheckpointCapabilities.Read)
            {
                if(mode=="chunk")
                {
                    var input=payload.Deserialize<ExecutionCheckpointChunkRead>(Json)!;
                    var saved=await store.ReadAsync(organization,installation,runtime,new(input.SessionKey,input.EngineVersion),token)
                        ?? throw new InvalidOperationException("Checkpoint disappeared.");
                    if(saved.Revision!=input.Revision || input.Blob is not ("engine" or "source")) throw new InvalidOperationException("Checkpoint revision changed.");
                    var archive=input.Blob=="engine"?saved.EngineArchive:saved.SourceArchive;
                    if(archive is null || input.Offset<0 || input.Offset>=archive.Length || input.Offset%ExecutionCheckpointTransfer.ChunkBytes!=0)
                        throw new ArgumentException("Invalid checkpoint chunk offset.");
                    data=new ExecutionCheckpointChunk(archive.AsSpan(input.Offset,Math.Min(ExecutionCheckpointTransfer.ChunkBytes,archive.Length-input.Offset)).ToArray());
                }
                else if(mode is null)
                {
                    var saved=await store.ReadAsync(organization,installation,runtime,payload.Deserialize<ExecutionCheckpointRead>(Json)!,token);
                    data=saved is null || saved.EngineArchive.Length+(saved.SourceArchive?.Length??0)<=ExecutionCheckpointTransfer.InlineBytes
                        ? new ExecutionCheckpointReadResult(saved)
                        : new ExecutionCheckpointReadResult(null,new(saved.Revision,saved.EventCursor,saved.EngineDigest,saved.EngineArchive.Length,
                            saved.SourceDigest,saved.SourceArchive?.Length??0,saved.WorkspaceId,saved.AssignmentRevision));
                }
                else throw new ArgumentException("Unsupported checkpoint read mode.");
            }
            else if(mode=="stage")
                data=await store.StageAsync(organization,installation,runtime,session.Grant.Revision,payload.Deserialize<ExecutionCheckpointChunkWrite>(Json)!,token);
            else if(mode=="commit")
            {
                var input=payload.Deserialize<ExecutionCheckpointCommit>(Json)!;
                var engine=await store.AssembleAsync(organization,installation,input.Request.SessionKey,input.EngineDigest,token);
                var source=input.SourceDigest is { } digest?await store.AssembleAsync(organization,installation,input.Request.SessionKey,digest,token):null;
                var saved=await store.WriteAsync(organization,installation,runtime,session.Grant.Revision,
                    input.Request with { EngineArchive=engine,SourceArchive=source },token);
                data=new ExecutionCheckpointCommitReceipt(saved.Revision);
            }
            else if(mode is null)
                data=await store.WriteAsync(organization,installation,runtime,session.Grant.Revision,payload.Deserialize<ExecutionCheckpointWrite>(Json)!,token);
            else throw new ArgumentException("Unsupported checkpoint write mode.");
            result = new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.From(data, Json) };
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or IOException or
            UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or DbUpdateConcurrencyException)
        {
            result = new() { RequestId = request.RequestId, FailureCode = error is UnauthorizedAccessException
                ? "denied" : "validation_failed",
                Error = error.Message.StartsWith("execution.checkpoint.incompatible:", StringComparison.Ordinal) ||
                    error.Message.StartsWith("execution.checkpoint.storage_limit:", StringComparison.Ordinal) ? error.Message :
                    "The execution checkpoint is unavailable, stale, unauthorized or invalid. Retained state was not discarded." };
        }
        yield return result;
    }
}
