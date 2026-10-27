using System.Security.Cryptography;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>One durable compute operation. Guest pipe reads are internal I/O, never agent work callbacks.</summary>
public static class ComputeEvidenceArchiveTransfer
{
    internal const int BufferBytes = 256 * 1024;
    public static async Task<ComputeEvidenceArtifact> ExecuteAsync(ComputeEvidenceArchive request,
        Func<ComputeGuestCommand, CancellationToken, Task<ComputeGuestWireResult>> execute, CancellationToken token)
    {
        request.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var output = new MemoryStream(request.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < request.Length; offset += BufferBytes)
        {
            var count = Math.Min(BufferBytes, request.Length - offset);
            // Open every component relative to a non-symlink directory descriptor; refuse devices,
            // links and length changes. Literal path is derived only from validated lowercase hashes.
            var script = "import os,stat,sys\np='" + request.GuestPath + "'\nfd=os.open('/',os.O_RDONLY|os.O_DIRECTORY)\n" +
                "for part in p.strip('/').split('/')[:-1]:\n n=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=fd); os.close(fd); fd=n\n" +
                "f=os.open('evidence.zip',os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)\ns=os.fstat(f)\n" +
                "assert stat.S_ISREG(s.st_mode) and s.st_size==" + request.Length + "\n" +
                "os.lseek(f," + offset + ",os.SEEK_SET)\nb=b''\nwhile len(b)<" + count + ":\n c=os.read(f," + count + "-len(b)); assert c; b+=c\n" +
                "sys.stdout.buffer.write(b)\n";
            var command = new ComputeGuestCommand(Guid.NewGuid(), "/usr/bin/python3", "/var/lib/csweet-compute/work", ["-c", script], 5, count);
            var result = await execute(command, deadline.Token);
            if (result.RequestId != command.RequestId || result is not { ExitCode: 0, TimedOut: false, Truncated: false, ErrorCode: null } ||
                result.StandardOutput is not { } bytes || bytes.Length != count || result.StandardError is not { Length: 0 })
                throw new InvalidDataException("Evidence archive read was incomplete.");
            hash.AppendData(bytes);
            await output.WriteAsync(bytes, deadline.Token);
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != request.Sha256)
            throw new InvalidDataException("Evidence archive digest changed during transfer.");
        return new(request.SourceCommitSha, request.BuildSha256, request.Sha256, request.Length, request.ScriptSha256, output.ToArray());
    }
}
