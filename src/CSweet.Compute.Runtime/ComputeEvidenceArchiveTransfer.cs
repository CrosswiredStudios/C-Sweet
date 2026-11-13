using System.Security.Cryptography;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>
/// One durable compute operation. Guest pipe reads are internal I/O, never agent work callbacks. The archive is streamed
/// to Core in <see cref="ComputeEvidenceArchive.PartBytes"/> parts as it is read, so neither the provider nor Core
/// holds the whole archive in memory and no size cap applies. Core verifies the complete archive when the result arrives.
/// </summary>
public static class ComputeEvidenceArchiveTransfer
{
    internal const int BufferBytes = 256 * 1024;

    /// <summary>
    /// Uploads one part. Returns false when Core does not accept staged parts (an older Core): archives up to
    /// <see cref="ComputeEvidenceArchive.LegacyInlineMaximumBytes"/> then travel inline in the signed result.
    /// </summary>
    public delegate Task<bool> UploadPart(int offset, byte[] content, CancellationToken token);

    public static Task<ComputeEvidenceArtifact> ExecuteAsync(ComputeEvidenceArchive request,
        Func<ComputeGuestCommand, CancellationToken, Task<ComputeGuestWireResult>> execute, CancellationToken token) =>
        ExecuteAsync(request, execute, null, token);

    public static async Task<ComputeEvidenceArtifact> ExecuteAsync(ComputeEvidenceArchive request,
        Func<ComputeGuestCommand, CancellationToken, Task<ComputeGuestWireResult>> execute, UploadPart? upload, CancellationToken token)
    {
        request.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Time scales with size instead of capping it: 30 seconds plus two seconds per MiB read and uploaded.
        deadline.CancelAfter(TimeSpan.FromSeconds(30 + 2.0 * request.Length / ComputeEvidenceArchive.PartBytes));
        MemoryStream? inline = upload is null ? new MemoryStream(InlineCapacity(request)) : null;
        using var part = new MemoryStream(Math.Min(ComputeEvidenceArchive.PartBytes, request.Length));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var partOffset = 0;
            for (var offset = 0; offset < request.Length;)
            {
                // Reads never cross a part boundary, so each part is uploaded as soon as it is complete.
                var count = Math.Min(BufferBytes, Math.Min(request.Length - offset, ComputeEvidenceArchive.PartBytes - (int)part.Length));
                var bytes = await ReadGuestAsync(request, offset, count, execute, deadline.Token);
                hash.AppendData(bytes);
                offset += count;
                if (inline is not null) { await inline.WriteAsync(bytes, deadline.Token); continue; }
                await part.WriteAsync(bytes, deadline.Token);
                if (part.Length < ComputeEvidenceArchive.PartBytes && offset < request.Length) continue;
                var content = part.ToArray();
                part.SetLength(0);
                if (!await upload!(partOffset, content, deadline.Token))
                {
                    // Core predates staged evidence. Carry small archives inline as before; larger ones need a Core update.
                    if (partOffset != 0) throw new IOException("Core stopped accepting staged evidence during the transfer.");
                    inline = new MemoryStream(InlineCapacity(request));
                    await inline.WriteAsync(content, deadline.Token);
                }
                partOffset += content.Length;
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != request.Sha256)
                throw new InvalidDataException("Evidence archive digest changed during transfer.");
            return new(request.SourceCommitSha, request.BuildSha256, request.Sha256, request.Length, request.ScriptSha256, inline?.ToArray());
        }
        finally { inline?.Dispose(); }
    }

    private static int InlineCapacity(ComputeEvidenceArchive request) =>
        request.Length <= ComputeEvidenceArchive.LegacyInlineMaximumBytes ? request.Length :
            throw new IOException("Archives over 4 MiB are transferred as staged parts; update headquarters and this provider.");

    private static async Task<byte[]> ReadGuestAsync(ComputeEvidenceArchive request, int offset, int count,
        Func<ComputeGuestCommand, CancellationToken, Task<ComputeGuestWireResult>> execute, CancellationToken token)
    {
        // Open every component relative to a non-symlink directory descriptor; refuse devices,
        // links and length changes. Literal path is derived only from validated lowercase hashes.
        var script = "import os,stat,sys\np='" + request.GuestPath + "'\nfd=os.open('/',os.O_RDONLY|os.O_DIRECTORY)\n" +
            "for part in p.strip('/').split('/')[:-1]:\n n=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=fd); os.close(fd); fd=n\n" +
            "f=os.open('evidence.zip',os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)\ns=os.fstat(f)\n" +
            "assert stat.S_ISREG(s.st_mode) and s.st_size==" + request.Length + "\n" +
            "os.lseek(f," + offset + ",os.SEEK_SET)\nb=b''\nwhile len(b)<" + count + ":\n c=os.read(f," + count + "-len(b)); assert c; b+=c\n" +
            "sys.stdout.buffer.write(b)\n";
        var command = new ComputeGuestCommand(Guid.NewGuid(), "/usr/bin/python3", "/var/lib/csweet-compute/work", ["-c", script], 5, count);
        var result = await execute(command, token);
        if (result.RequestId != command.RequestId || result is not { ExitCode: 0, TimedOut: false, Truncated: false, ErrorCode: null } ||
            result.StandardOutput is not { } bytes || bytes.Length != count || result.StandardError is not { Length: 0 })
            throw new InvalidDataException("Evidence archive read was incomplete.");
        return bytes;
    }
}
