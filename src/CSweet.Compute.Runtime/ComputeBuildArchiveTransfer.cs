using System.Security.Cryptography;
using System.Text;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>Mechanical, resumable file transfer. Never executes application code or extracts the archive.</summary>
public static class ComputeBuildArchiveTransfer
{
    public static async Task<ComputeBuildArchiveReceipt> ExecuteAsync(ComputeBuildArchive archive,
        Func<int, CancellationToken, Task<byte[]>> readPart,
        Func<ComputeGuestCommand, CancellationToken, Task<ComputeGuestWireResult>> execute, CancellationToken token)
    {
        archive = archive.Validate();
        using var bytes = new MemoryStream(archive.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < archive.Length; offset += ComputeBuildArchive.PartBytes)
        {
            var part = await readPart(offset, token);
            if (part.Length != Math.Min(ComputeBuildArchive.PartBytes, archive.Length - offset) ||
                Convert.ToHexStringLower(SHA256.HashData(part)) != archive.PartSha256[offset / ComputeBuildArchive.PartBytes])
                throw new InvalidDataException("Build input page differs from the pinned request.");
            hash.AppendData(part); await bytes.WriteAsync(part, token);
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != archive.BuildSha256)
            throw new InvalidDataException("Build archive digest mismatch.");
        // No guest writes occur before the complete immutable input has been verified.
        var common = "import os,stat,hashlib,base64\n" +
            "d=os.open('/',os.O_RDONLY|os.O_DIRECTORY)\n" +
            "for part in '" + archive.GuestRoot.TrimStart('/') + "'.split('/'):\n" +
            " try: os.mkdir(part,0o700,dir_fd=d)\n except FileExistsError: pass\n" +
            " n=os.open(part,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=d); os.close(d); d=n\n" +
            "try: os.mkdir('evidence',0o700,dir_fd=d)\nexcept FileExistsError: pass\n" +
            "e=os.open('evidence',os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=d); os.close(e)\n";
        var check = common + "try:\n f=os.open('build.zip',os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=d)\n" +
            "except FileNotFoundError: print('missing')\nelse:\n s=os.fstat(f); assert stat.S_ISREG(s.st_mode) and s.st_size==" + archive.Length +
            "; h=hashlib.sha256()\n while True:\n  b=os.read(f,65536)\n  if not b: break\n  h.update(b)\n" +
            " assert h.hexdigest()=='" + archive.BuildSha256 + "'; os.close(f); print('complete')\n";
        if ((await RunAsync(check)).Trim() == "complete") return Receipt();
        var all = bytes.ToArray();
        for (var offset = 0; offset < all.Length; offset += 18000)
        {
            var count = Math.Min(18000, all.Length - offset);
            var script = common + "b=base64.b64decode('" + Convert.ToBase64String(all, offset, count) + "')\n" +
                "f=os.open('.build-upload',os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW|os.O_NONBLOCK,0o600,dir_fd=d)\n" +
                "s=os.fstat(f); assert stat.S_ISREG(s.st_mode) and s.st_nlink==1 and " + offset + "<=s.st_size<=" + archive.Length + "\n" +
                "os.lseek(f," + offset + ",os.SEEK_SET); prior=os.read(f,len(b)); assert prior==b[:len(prior)], 'upload prefix changed'\n" +
                "os.lseek(f," + offset + ",os.SEEK_SET)\nwhile b:\n n=os.write(f,b); assert n>0; b=b[n:]\nos.fsync(f); os.close(f)\n";
            await RunAsync(script);
        }
        await RunAsync(common + "f=os.open('.build-upload',os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=d)\n" +
            "s=os.fstat(f); assert stat.S_ISREG(s.st_mode) and s.st_nlink==1 and s.st_size==" + archive.Length + "\n" +
            "h=hashlib.sha256()\nwhile True:\n b=os.read(f,65536)\n if not b: break\n h.update(b)\n" +
            "assert h.hexdigest()=='" + archive.BuildSha256 + "'; os.close(f)\n" +
            "try: os.mkdir('evidence',0o700,dir_fd=d)\nexcept FileExistsError: pass\n" +
            "e=os.open('evidence',os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=d); os.close(e)\n" +
            "os.replace('.build-upload','build.zip',src_dir_fd=d,dst_dir_fd=d); os.fsync(d)\n");
        return Receipt();

        ComputeBuildArchiveReceipt Receipt() => new(archive.SourceCommitSha, archive.BuildSha256, archive.Length);
        async Task<string> RunAsync(string script)
        {
            var command = new ComputeGuestCommand(Guid.NewGuid(), "/usr/bin/python3", "/var/lib/csweet-compute/work", ["-c", script], 10, 1024);
            command.ValidateAndSnapshot("linux");
            var result = await execute(command, token);
            if (result.RequestId != command.RequestId || result is not { ExitCode: 0, TimedOut: false, Truncated: false, ErrorCode: null } ||
                result.StandardError is not { Length: 0 } || result.StandardOutput is not { } output || output.Length > 1024)
                throw new InvalidDataException("Build archive transfer did not complete safely.");
            return Encoding.UTF8.GetString(output);
        }
    }
}
