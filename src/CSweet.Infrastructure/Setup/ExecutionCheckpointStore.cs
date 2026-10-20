using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Immutable private blobs with an atomic, optimistic-concurrency head in the existing state table.</summary>
public sealed class ExecutionCheckpointStore(CSweetDbContext db, IOptions<AgentRuntimeManagerOptions> runtime,
    IDataProtectionProvider protection)
{
    public const string StateKind = "execution-checkpoint.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumArchiveBytes = 16 * 1024 * 1024;
    private const long MaximumInstallationBytes = 2L * 1024 * 1024 * 1024;
    private readonly string _root = Path.GetFullPath(runtime.Value.WorkspaceSnapshotStorePath);

    private sealed record Head(string EngineVersion, long EventCursor, string EngineDigest, string? SourceDigest,
        Guid LogicalWorkId, Guid? WorkspaceId, long? AssignmentRevision, string IdempotencyKey, string RequestDigest);

    public Task<ExecutionCheckpointChunkReceipt> StageAsync(Guid organization,Guid installation,Guid runtimeId,long grantRevision,
        ExecutionCheckpointChunkWrite input,CancellationToken token) => RuntimeCheckpointGate.ExecuteAsync(db,installation,
        organization.ToString(),runtimeId,grantRevision,async () =>
        {
            Validate(input.SessionKey,input.EngineVersion);
            await RequireWorkAsync(organization,installation,runtimeId,token);
            await AuthorizeSourceAsync(organization,installation,input.WorkspaceId,input.AssignmentRevision,token);
            if(input.TotalLength is < 1 or > MaximumArchiveBytes || input.Offset<0 || input.Offset%ExecutionCheckpointTransfer.ChunkBytes!=0 ||
                input.Offset>=input.TotalLength || input.Data.Length!=Math.Min(ExecutionCheckpointTransfer.ChunkBytes,input.TotalLength-input.Offset))
                throw new ArgumentException("Invalid checkpoint chunk bounds.");
            await using var admission=await BusinessStorageAdmission.EnterAsync(db,organization,token);
            var path=ChunkPath(organization,installation,input.SessionKey,input.Digest,input.Offset);
            EnsureStorageCapacity(organization,installation,input.Data.Length + 256L);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var protector=protection.CreateProtector(StateKind,organization.ToString("N"),installation.ToString("N"));
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".prepare";
            try
            {
                await File.WriteAllBytesAsync(temporary,protector.Protect(input.Data),token);
                File.Move(temporary,path,overwrite:true);
                Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!,DateTime.UtcNow);
            }
            finally { File.Delete(temporary); }
            return new ExecutionCheckpointChunkReceipt(true);
        },token);

    public async Task<byte[]> AssembleAsync(Guid organization,Guid installation,string key,string digest,CancellationToken token)
    {
        var protector=protection.CreateProtector(StateKind,organization.ToString("N"),installation.ToString("N"));
        using var output=new MemoryStream();
        for(var offset=0;offset<MaximumArchiveBytes;offset+=ExecutionCheckpointTransfer.ChunkBytes)
        {
            var path=ChunkPath(organization,installation,key,digest,offset);
            if(!File.Exists(path)) break;
            var bytes=protector.Unprotect(await File.ReadAllBytesAsync(path,token));
            if(bytes.Length is < 1 or > ExecutionCheckpointTransfer.ChunkBytes) throw new InvalidDataException("Invalid staged chunk.");
            output.Write(bytes);
            if(bytes.Length<ExecutionCheckpointTransfer.ChunkBytes) break;
        }
        var archive=output.ToArray();
        if(Hash(archive)!=digest) throw new InvalidDataException("Staged checkpoint is incomplete or changed.");
        return archive;
    }

    public async Task<ExecutionCheckpoint?> ReadAsync(Guid organization, Guid installation, Guid runtimeId,
        ExecutionCheckpointRead input, CancellationToken token)
    {
        Validate(input.SessionKey, input.EngineVersion);
        var work = await RequireWorkAsync(organization, installation, runtimeId, token);
        var state = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.AgentInstallationId == installation && x.Kind == StateKind && x.ExternalKey == input.SessionKey, token);
        if (state is null) return null;
        var head = JsonSerializer.Deserialize<Head>(state.PayloadJson, Json)!;
        if (head.EngineVersion != input.EngineVersion)
            throw new InvalidOperationException("execution.checkpoint.incompatible: retained engine format requires the matching agent package.");
        await AuthorizeSourceAsync(organization, installation, head.WorkspaceId, head.AssignmentRevision, token, allowPublished:true);
        if (head.WorkspaceId is null && head.LogicalWorkId != work)
            throw new UnauthorizedAccessException("This checkpoint belongs to another logical work item.");
        return await LoadAsync(organization, installation, state, head, token);
    }

    public Task<ExecutionCheckpoint> WriteAsync(Guid organization, Guid installation, Guid runtimeId, long grantRevision,
        ExecutionCheckpointWrite input, CancellationToken token) => RuntimeCheckpointGate.ExecuteAsync(db, installation,
            organization.ToString(), runtimeId, grantRevision, () => WriteCoreAsync(organization, installation, runtimeId, input, token), token);

    private async Task<ExecutionCheckpoint> WriteCoreAsync(Guid organization, Guid installation, Guid runtimeId,
        ExecutionCheckpointWrite input, CancellationToken token)
    {
        Validate(input.SessionKey, input.EngineVersion);
        if (input.ExpectedRevision < 0 || input.EventCursor < 0 || string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 160 ||
            input.WorkspaceId is not null && input.SourceArchive is null || (input.WorkspaceId is null) != (input.AssignmentRevision is null))
            throw new ArgumentException("Checkpoint inputs are inconsistent.");
        var work = await RequireWorkAsync(organization, installation, runtimeId, token);
        await AuthorizeSourceAsync(organization, installation, input.WorkspaceId, input.AssignmentRevision, token);
        await using var admission = await BusinessStorageAdmission.EnterAsync(db, organization, token);
        var engine = await PutAsync(organization, installation, input.EngineArchive, token);
        var source = input.SourceArchive is null ? null : await PutAsync(organization, installation, input.SourceArchive, token);
        var digest = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input with { EngineArchive = [], SourceArchive = null }, Json) + engine + source));
        var state = await db.PluginOperationalStates.SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.AgentInstallationId == installation && x.Kind == StateKind && x.ExternalKey == input.SessionKey, token);
        if (state is not null)
        {
            var prior = JsonSerializer.Deserialize<Head>(state.PayloadJson, Json)!;
            if (prior.IdempotencyKey == input.IdempotencyKey)
            {
                if (prior.RequestDigest != digest) throw new InvalidOperationException("The checkpoint key was reused with different content.");
                return await LoadAsync(organization, installation, state, prior, token);
            }
            if (prior.EngineVersion != input.EngineVersion || prior.WorkspaceId != input.WorkspaceId ||
                prior.AssignmentRevision != input.AssignmentRevision || input.EventCursor < prior.EventCursor ||
                prior.WorkspaceId is null && prior.LogicalWorkId != work)
                throw new InvalidOperationException("Checkpoint lineage changed or the event cursor regressed.");
        }
        if ((state?.Revision ?? 0) != input.ExpectedRevision) throw new DbUpdateConcurrencyException("Checkpoint revision changed.");
        var head = new Head(input.EngineVersion, input.EventCursor, engine, source, work, input.WorkspaceId,
            input.AssignmentRevision, input.IdempotencyKey, digest);
        if (state is null)
        {
            state = new() { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
                Kind = StateKind, ExternalKey = input.SessionKey, CreatedAt = DateTimeOffset.UtcNow };
            db.PluginOperationalStates.Add(state);
        }
        state.PayloadJson = JsonSerializer.Serialize(head, Json);
        state.Revision++;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token); // RuntimeCheckpointGate commits the manifest only after both immutable blobs exist.
        return await LoadAsync(organization, installation, state, head, token);
    }

    private async Task<Guid> RequireWorkAsync(Guid organization, Guid installation, Guid runtimeId, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await db.AgentWorkAttempts.AsNoTracking().Where(x => x.RuntimeInstanceId == runtimeId && x.FinishedAt == null &&
            x.LeaseExpiresAt > now && x.AgentWorkItem!.OrganizationId == organization.ToString() &&
            x.AgentWorkItem.AgentInstallationId == installation && x.AgentWorkItem.Status == AgentWorkStatus.Leased &&
            x.RuntimeInstance!.Status == AgentRuntimeStatus.Running && x.RuntimeInstance.MemoryResetRequestedAt == null &&
            x.RuntimeInstance.AgentInstallation!.IsEnabled).Select(x => x.AgentWorkItemId).Take(2).ToArrayAsync(token);
        return active.Length == 1 ? active[0] : throw new UnauthorizedAccessException("A single current authorized work lease is required.");
    }

    private async Task AuthorizeSourceAsync(Guid organization, Guid installation, Guid? workspaceId, long? revision, CancellationToken token, bool allowPublished = false)
    {
        if (workspaceId is null) return;
        if (revision is not > 0) throw new ArgumentException("A source assignment revision is required.");
        var workspace = await db.SourceControlWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workspaceId &&
            x.OrganizationId == organization && x.AgentInstallationId == installation && x.AssignmentRevision == revision, token)
            ?? throw new UnauthorizedAccessException("Source checkpoint identity changed.");
        if (workspace.Status != SourceControlWorkspaceStatus.Ready && !(allowPublished && workspace.Status == SourceControlWorkspaceStatus.Published)) throw new UnauthorizedAccessException("The source workspace is no longer available.");
        if (!await new CanonicalWorkspaceAuthorization(db).AuthorizeAsync(workspace, allowPublished ? GitWorkspaceCapabilities.Inspect : GitWorkspaceCapabilities.Publish, null, token) &&
            !await db.CoreWorkTasks.AnyAsync(x => x.Id == workspace.WorkItemId && x.OrganizationId == organization &&
                x.AssignedAgentInstallationId == installation && x.AssignmentRevision == revision && x.ArchivedAt == null &&
                x.Status != CSweet.Domain.Core.WorkTaskStatus.Completed && x.Status != CSweet.Domain.Core.WorkTaskStatus.Cancelled, token))
            throw new UnauthorizedAccessException("Current source assignment authority is required.");
    }

    private async Task<string> PutAsync(Guid organization, Guid installation, byte[] archive, CancellationToken token)
    {
        if (archive is not { Length: > 0 and <= MaximumArchiveBytes }) throw new ArgumentException("Checkpoint archive exceeds 16 MiB.");
        var digest = Hash(archive);
        var directory = DirectoryPath(organization, installation);
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".prepare");
        var extracted = staging + ".validation";
        try
        {
            await using var input = new MemoryStream(archive, writable: false);
            await new WorkspaceArtifactValidator(new() { MaximumFiles = 20000, MaximumTotalBytes = 64 * 1024 * 1024, MaximumFileBytes = 16 * 1024 * 1024 })
                .ExtractZipAsync(input, extracted, token);
            var destination = BlobPath(organization, installation, digest);
            if (!File.Exists(destination))
            {
                EnsureStorageCapacity(organization,installation,archive.Length + 256L);
                var protector = protection.CreateProtector(StateKind, organization.ToString("N"), installation.ToString("N"));
                await File.WriteAllBytesAsync(staging, protector.Protect(archive), token);
                File.Move(staging, destination, overwrite: false);
            }
            File.SetLastWriteTimeUtc(destination,DateTime.UtcNow);
            return digest;
        }
        finally
        {
            File.Delete(staging);
            if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        }
    }

    private async Task<ExecutionCheckpoint> LoadAsync(Guid organization, Guid installation, PluginOperationalState state, Head head, CancellationToken token) =>
        new(state.ExternalKey, head.EngineVersion, state.Revision, head.EventCursor,
            await GetAsync(organization, installation, head.EngineDigest, token), head.EngineDigest,
            head.WorkspaceId, head.AssignmentRevision, head.SourceDigest is { } source ? await GetAsync(organization, installation, source, token) : null, head.SourceDigest);

    private async Task<byte[]> GetAsync(Guid organization, Guid installation, string digest, CancellationToken token)
    {
        var protector = protection.CreateProtector(StateKind, organization.ToString("N"), installation.ToString("N"));
        var archive = protector.Unprotect(await File.ReadAllBytesAsync(BlobPath(organization, installation, digest), token));
        if (Hash(archive) != digest) throw new InvalidDataException("Checkpoint digest mismatch.");
        return archive;
    }

    public string DirectoryPath(Guid organization, Guid installation) => Path.Combine(_root, organization.ToString("N"), "execution-checkpoints", installation.ToString("N"));
    private void EnsureStorageCapacity(Guid organization, Guid installation, long additionalBytes)
    {
        var root = DirectoryPath(organization, installation);
        if (!Directory.Exists(root)) return;
        long total = additionalBytes;
        var count = 0;
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > 100000) throw new IOException("execution.checkpoint.storage_limit: checkpoint storage inventory exceeds its bound.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Checkpoint storage contains an unexpected link.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                else total += new FileInfo(path).Length;
                if (total > MaximumInstallationBytes)
                    throw new IOException("execution.checkpoint.storage_limit: this installation reached its 2 GiB private checkpoint limit. Allow cleanup or resolve retained work before retrying.");
            }
        }
    }
    private string ChunkPath(Guid organization,Guid installation,string key,string digest,int offset)
    {
        Validate(key,"transfer");
        if(digest.Length!=64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid checkpoint digest.");
        return Path.Combine(DirectoryPath(organization,installation),"transfers",Hash(Encoding.UTF8.GetBytes(key)),digest,offset.ToString("D8")+".chunk");
    }

    /// <summary>Reclaim abandoned uploads/old generations after a grace period; business deletion removes its whole owned tree.</summary>
    public async Task CollectOrphansAsync(DateTimeOffset now,CancellationToken token)
    {
        if(!Directory.Exists(_root)) return;
        foreach(var organizationDirectory in Directory.EnumerateDirectories(_root))
        {
            if(!Guid.TryParseExact(Path.GetFileName(organizationDirectory),"N",out var organization)) continue;
            var parent=OwnedStorage.Child(_root,organization.ToString("N"),"execution-checkpoints");
            if(!Directory.Exists(parent)) continue;
            foreach(var installationDirectory in Directory.EnumerateDirectories(parent))
            {
                token.ThrowIfCancellationRequested();
                if(!Guid.TryParseExact(Path.GetFileName(installationDirectory),"N",out var installation)) continue;
                var owned=OwnedStorage.Child(parent,installation.ToString("N"));
                var gate=AgentWorkInbox.ClaimLocks.GetOrAdd(installation,_=>new SemaphoreSlim(1,1));
                await gate.WaitAsync(token);
                try
                {
                await using var transaction=await db.Database.BeginTransactionAsync(token);
                if(db.Database.IsNpgsql())
                {
                    var key=$"agent-work-claim:{organization:D}:{installation:D}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))",token);
                }
                if(!await db.AgentInstallations.AnyAsync(x=>x.Id==installation&&x.BusinessId==organization.ToString(),token))
                {
                    if(Directory.GetLastWriteTimeUtc(owned)<now.AddDays(-1).UtcDateTime)
                    {
                        OwnedStorage.DeleteDirectory(parent,owned,token);
                        var abandoned=await db.PluginOperationalStates.Where(x=>x.OrganizationId==organization &&
                            x.AgentInstallationId==installation && x.Kind==StateKind).ToListAsync(token);
                        db.PluginOperationalStates.RemoveRange(abandoned);
                        await db.SaveChangesAsync(token);
                        await transaction.CommitAsync(token);
                    }
                    continue;
                }
                var heads=await db.PluginOperationalStates.AsNoTracking().Where(x=>x.OrganizationId==organization&&
                    x.AgentInstallationId==installation&&x.Kind==StateKind).Select(x=>x.PayloadJson).ToListAsync(token);
                var retained=heads.Select(x=>JsonSerializer.Deserialize<Head>(x,Json)!).SelectMany(x=>new[]{x.EngineDigest,x.SourceDigest})
                    .Where(x=>x is not null).ToHashSet(StringComparer.Ordinal);
                foreach(var file in Directory.EnumerateFiles(owned,"*.blob"))
                    if(File.GetLastWriteTimeUtc(file)<now.AddDays(-1).UtcDateTime && !retained.Contains(Path.GetFileNameWithoutExtension(file)))
                        OwnedStorage.DeleteFile(owned,file);
                var transfers=OwnedStorage.Child(owned,"transfers");
                if(Directory.Exists(transfers))
                    foreach(var session in Directory.EnumerateDirectories(transfers))
                        if(Directory.GetLastWriteTimeUtc(session)<now.AddDays(-1).UtcDateTime)
                            OwnedStorage.DeleteDirectory(transfers,session,token);
                foreach(var file in Directory.EnumerateFiles(owned,"*.prepare"))
                    if(File.GetLastWriteTimeUtc(file)<now.AddDays(-1).UtcDateTime) OwnedStorage.DeleteFile(owned,file);
                foreach(var validation in Directory.EnumerateDirectories(owned,"*.validation"))
                    if(Directory.GetLastWriteTimeUtc(validation)<now.AddDays(-1).UtcDateTime) OwnedStorage.DeleteDirectory(owned,validation,token);
                await transaction.CommitAsync(token);
                }
                finally { gate.Release(); }
            }
        }
    }
    private string BlobPath(Guid organization, Guid installation, string digest)
    {
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid checkpoint digest.");
        return Path.Combine(DirectoryPath(organization, installation), digest + ".blob");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Validate(string key, string version)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || string.IsNullOrWhiteSpace(version) || version.Length > 100)
            throw new ArgumentException("A bounded session key and engine format are required.");
    }
}
