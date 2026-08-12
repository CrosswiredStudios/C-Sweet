using CSweet.AgentRuntime.Protocol;
using Google.Protobuf;
using Grpc.Core;

namespace CSweet.ExecutionGateway;

internal sealed class GrpcWorkloadTunnelStream : Stream
{
    private readonly IAsyncStreamReader<WorkloadTunnelFrame> _requests;
    private readonly IServerStreamWriter<WorkloadTunnelFrame> _responses;
    private readonly Func<WorkloadTunnelFrame, Task<(Guid NodeId, Guid AssignmentId)>> _validate;
    private readonly CancellationToken _cancellationToken;
    private readonly string _nodeId;
    private readonly string _assignmentId;
    private readonly long _fencingEpoch;
    private readonly long _sessionEpoch;
    private WorkloadTunnelFrame? _current;
    private int _currentOffset;
    private long _expectedInputSequence;
    private long _outputSequence;
    private bool _inputComplete;
    private bool _outputComplete;

    public GrpcWorkloadTunnelStream(
        IAsyncStreamReader<WorkloadTunnelFrame> requests,
        IServerStreamWriter<WorkloadTunnelFrame> responses,
        WorkloadTunnelFrame first,
        Func<WorkloadTunnelFrame, Task<(Guid NodeId, Guid AssignmentId)>> validate,
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        long sessionEpoch,
        CancellationToken cancellationToken)
    {
        _requests = requests;
        _responses = responses;
        _current = first;
        _validate = validate;
        _nodeId = nodeId.ToString("D");
        _assignmentId = assignmentId.ToString("D");
        _fencingEpoch = fencingEpoch;
        _sessionEpoch = sessionEpoch;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), _cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (!_inputComplete)
        {
            if (_current is null)
            {
                if (!await _requests.MoveNext(cancellationToken))
                {
                    _inputComplete = true;
                    return 0;
                }
                _current = _requests.Current;
                await _validate(_current);
            }
            ValidateBinding(_current);
            if (_current.Sequence != _expectedInputSequence)
                throw new InvalidDataException("The node guest-channel frame sequence is invalid.");
            var remaining = _current.Content.Length - _currentOffset;
            if (remaining > 0)
            {
                var count = Math.Min(buffer.Length, remaining);
                _current.Content.Span.Slice(_currentOffset, count).CopyTo(buffer.Span);
                _currentOffset += count;
                if (_currentOffset == _current.Content.Length) AdvanceInputFrame();
                return count;
            }
            var completed = _current.Completed;
            AdvanceInputFrame();
            if (completed)
            {
                _inputComplete = true;
                return 0;
            }
        }
        return 0;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), _cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_outputComplete) throw new InvalidOperationException("The guest-channel response is complete.");
        const int maximumFrame = 64 * 1024;
        for (var offset = 0; offset < buffer.Length; offset += maximumFrame)
        {
            var count = Math.Min(maximumFrame, buffer.Length - offset);
            await _responses.WriteAsync(Frame(
                ByteString.CopyFrom(buffer.Span.Slice(offset, count)), completed: false), cancellationToken);
        }
    }

    public async Task CompleteAsync()
    {
        if (_outputComplete) return;
        _outputComplete = true;
        await _responses.WriteAsync(Frame(ByteString.Empty, completed: true), _cancellationToken);
    }

    private WorkloadTunnelFrame Frame(ByteString content, bool completed) => new()
    {
        NodeId = _nodeId,
        AssignmentId = _assignmentId,
        FencingEpoch = _fencingEpoch,
        SessionEpoch = _sessionEpoch,
        Sequence = _outputSequence++,
        Content = content,
        Completed = completed
    };

    private void AdvanceInputFrame()
    {
        _expectedInputSequence++;
        _current = null;
        _currentOffset = 0;
    }

    private void ValidateBinding(WorkloadTunnelFrame frame)
    {
        if (!string.Equals(frame.NodeId, _nodeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(frame.AssignmentId, _assignmentId, StringComparison.OrdinalIgnoreCase) ||
            frame.FencingEpoch != _fencingEpoch || frame.SessionEpoch != _sessionEpoch)
            throw new InvalidDataException("The guest-channel tunnel binding changed.");
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
