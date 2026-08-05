using System.Collections.Concurrent;
using System.Security.Cryptography;
using CSweet.Application.GenAi;
using CSweet.Application.Setup;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.GenAi;

public interface IResumableMediaUploadStore
{
    Task CreateAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task AppendAsync(Guid sessionId, long committedLength, long contentLength, Stream content,
        CancellationToken cancellationToken = default);
    Task<long> GetLengthAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed class FileResumableMediaUploadStore : IResumableMediaUploadStore
{
    private readonly string _root;

    public FileResumableMediaUploadStore(IConfiguration configuration)
    {
        var mediaRoot = Path.GetFullPath(configuration["CSweet:GenAi:MediaRoot"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "media"));
        _root = Path.Combine(mediaRoot, ".uploads");
        Directory.CreateDirectory(_root);
    }

    public async Task CreateAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Resolve(sessionId), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task AppendAsync(Guid sessionId, long committedLength, long contentLength, Stream content,
        CancellationToken cancellationToken = default)
    {
        await using var output = new FileStream(Resolve(sessionId), FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (output.Length < committedLength)
            throw new InvalidOperationException("The temporary upload is incomplete and cannot be resumed.");
        if (output.Length != committedLength) output.SetLength(committedLength);
        output.Position = committedLength;
        var buffer = new byte[Math.Min(1024 * 1024, (int)contentLength)];
        long remaining = contentLength;
        while (remaining > 0)
        {
            var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0) throw new InvalidOperationException("The upload chunk ended before Content-Length bytes were received.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
        await output.FlushAsync(cancellationToken);
    }

    public Task<long> GetLengthAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FileInfo(Resolve(sessionId)).Length);

    public Task<Stream> OpenReadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(Resolve(sessionId), FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));

    public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var path = Resolve(sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(Guid sessionId) => Path.Combine(_root, $"{sessionId:N}.upload");
}

public sealed class ResumableMediaUploadService(
    CSweetDbContext db,
    IResumableMediaUploadStore store,
    IMediaAssetService mediaAssets,
    IOptions<MediaAssetStorageOptions> configuredOptions,
    IAuditEventWriter audit) : IResumableMediaUploadService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SessionLocks = new();
    private readonly long _maximumFileSize = Math.Clamp(configuredOptions.Value.MaximumFileSizeBytes, 1,
        MediaAssetStorageOptions.AbsoluteMaximumFileSizeBytes);
    private readonly long _organizationQuota = Math.Max(1, configuredOptions.Value.MaximumOrganizationStorageBytes);
    private readonly int _chunkSize = Math.Clamp(configuredOptions.Value.ResumableChunkSizeBytes, 64 * 1024, 64 * 1024 * 1024);
    private readonly TimeSpan _lifetime = TimeSpan.FromHours(Math.Clamp(configuredOptions.Value.UploadSessionLifetimeHours, 1, 168));

    public async Task<MediaUploadSessionResponse> CreateAsync(Guid organizationId,
        CreateMediaUploadSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (!await db.CoreOrganizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, cancellationToken))
            throw new InvalidOperationException("Organization was not found.");
        var fileName = Path.GetFileName(request.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
            throw new InvalidOperationException("A bounded file name is required.");
        if (request.TotalBytes is < 1 || request.TotalBytes > _maximumFileSize)
            throw new InvalidOperationException($"The upload must be between 1 and {_maximumFileSize} bytes.");
        var expectedHash = NormalizeHash(request.Sha256);
        var contentType = MediaAssetService.NormalizeContentType(request.ContentType);
        var storedBytes = await db.MediaAssets.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .SumAsync(x => (long?)x.SizeBytes, cancellationToken) ?? 0;
        var reservedBytes = await db.MediaUploadSessions.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.Status == MediaUploadSessionStatus.Active)
            .SumAsync(x => (long?)x.TotalBytes, cancellationToken) ?? 0;
        if (storedBytes > _organizationQuota - reservedBytes ||
            request.TotalBytes > _organizationQuota - storedBytes - reservedBytes)
            throw new InvalidOperationException("The organization's media storage quota would be exceeded.");
        var now = DateTimeOffset.UtcNow;
        var session = new MediaUploadSession
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, FileName = fileName, ContentType = contentType,
            TotalBytes = request.TotalBytes, ChunkSizeBytes = _chunkSize, ExpectedSha256 = expectedHash,
            Status = MediaUploadSessionStatus.Active, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.Add(_lifetime)
        };
        await store.CreateAsync(session.Id, cancellationToken);
        try
        {
            db.MediaUploadSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await store.DeleteAsync(session.Id, CancellationToken.None);
            throw;
        }
        await audit.WriteAsync("media-upload.created", nameof(MediaUploadSession), session.Id,
            $"Created a resumable upload for {session.TotalBytes} bytes.", null, cancellationToken);
        return Map(session);
    }

    public async Task<MediaUploadSessionResponse?> GetAsync(Guid organizationId, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await db.MediaUploadSessions.AsNoTracking().Include(x => x.MediaAsset)
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.OrganizationId == organizationId, cancellationToken);
        return session is null ? null : Map(session);
    }

    public async Task<MediaUploadSessionResponse> AppendAsync(Guid organizationId, Guid sessionId, long offset,
        long contentLength, Stream content, CancellationToken cancellationToken = default)
    {
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var session = await RequireActiveAsync(organizationId, sessionId, cancellationToken);
            if (offset != session.ReceivedBytes)
                throw new InvalidOperationException($"Resume from byte offset {session.ReceivedBytes}.");
            if (contentLength is < 1 || contentLength > session.ChunkSizeBytes ||
                contentLength > session.TotalBytes - session.ReceivedBytes)
                throw new InvalidOperationException("The chunk length is invalid for this upload session.");
            await store.AppendAsync(sessionId, session.ReceivedBytes, contentLength, content, cancellationToken);
            session.ReceivedBytes += contentLength;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.ExpiresAt = session.UpdatedAt.Add(_lifetime);
            await db.SaveChangesAsync(cancellationToken);
            return Map(session);
        }
        finally { gate.Release(); }
    }

    public async Task<MediaUploadSessionResponse> CompleteAsync(Guid organizationId, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var session = await db.MediaUploadSessions.Include(x => x.MediaAsset)
                .SingleOrDefaultAsync(x => x.Id == sessionId && x.OrganizationId == organizationId, cancellationToken)
                ?? throw new KeyNotFoundException("The upload session was not found.");
            if (session.Status == MediaUploadSessionStatus.Completed) return Map(session);
            EnsureActive(session);
            if (session.ReceivedBytes != session.TotalBytes ||
                await store.GetLengthAsync(sessionId, cancellationToken) != session.TotalBytes)
                throw new InvalidOperationException($"Upload is incomplete. Resume from byte offset {session.ReceivedBytes}.");
            await using var content = await store.OpenReadAsync(sessionId, cancellationToken);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken)).ToLowerInvariant();
            if (session.ExpectedSha256 is not null && !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(session.ExpectedSha256)))
            {
                await FailAsync(session, "The completed upload checksum does not match.", cancellationToken);
                throw new InvalidOperationException("The completed upload checksum does not match.");
            }
            content.Position = 0;
            MediaAssetResponse asset;
            try
            {
                asset = await mediaAssets.SaveUploadAsync(organizationId, session.FileName, session.ContentType,
                    content, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                await FailAsync(session, "The uploaded file failed media validation.", cancellationToken);
                throw;
            }
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The persisted media checksum changed unexpectedly.");
            session.MediaAssetId = asset.Id;
            session.MediaAsset = await db.MediaAssets.SingleAsync(x => x.Id == asset.Id, cancellationToken);
            session.Status = MediaUploadSessionStatus.Completed;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await store.DeleteAsync(sessionId, cancellationToken);
            await audit.WriteAsync("media-upload.completed", nameof(MediaUploadSession), session.Id,
                $"Completed and validated media asset {asset.Id:D}.", null, cancellationToken);
            return Map(session);
        }
        finally
        {
            gate.Release();
            SessionLocks.TryRemove(sessionId, out _);
        }
    }

    public async Task CancelAsync(Guid organizationId, Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var session = await db.MediaUploadSessions.SingleOrDefaultAsync(x =>
                x.Id == sessionId && x.OrganizationId == organizationId, cancellationToken);
            if (session is null || session.Status != MediaUploadSessionStatus.Active) return;
            session.Status = MediaUploadSessionStatus.Cancelled;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await store.DeleteAsync(sessionId, cancellationToken);
            await audit.WriteAsync("media-upload.cancelled", nameof(MediaUploadSession), session.Id,
                "Cancelled a resumable media upload and removed its temporary data.", null, cancellationToken);
        }
        finally
        {
            gate.Release();
            SessionLocks.TryRemove(sessionId, out _);
        }
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.MediaUploadSessions.Where(x =>
            x.Status == MediaUploadSessionStatus.Active && x.ExpiresAt <= now).ToListAsync(cancellationToken);
        foreach (var session in expired)
        {
            session.Status = MediaUploadSessionStatus.Expired;
            session.UpdatedAt = now;
            await store.DeleteAsync(session.Id, cancellationToken);
        }
        if (expired.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    private async Task<MediaUploadSession> RequireActiveAsync(Guid organizationId, Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await db.MediaUploadSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("The upload session was not found.");
        EnsureActive(session);
        return session;
    }

    private static void EnsureActive(MediaUploadSession session)
    {
        if (session.Status != MediaUploadSessionStatus.Active)
            throw new InvalidOperationException("The upload session is not active.");
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The upload session expired.");
    }

    private async Task FailAsync(MediaUploadSession session, string summary, CancellationToken cancellationToken)
    {
        session.Status = MediaUploadSessionStatus.Failed;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await store.DeleteAsync(session.Id, cancellationToken);
        await audit.WriteAsync("media-upload.failed", nameof(MediaUploadSession), session.Id, summary, null,
            cancellationToken);
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hash = value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidOperationException("SHA-256 must be exactly 64 hexadecimal characters.");
        return hash;
    }

    private static MediaUploadSessionResponse Map(MediaUploadSession value) => new(
        value.Id, value.OrganizationId, value.FileName, value.ContentType, value.TotalBytes, value.ReceivedBytes,
        value.ChunkSizeBytes, value.Status.ToString(), value.ExpiresAt,
        value.MediaAsset is null ? null : MediaAssetService.ToResponse(value.MediaAsset));
}
