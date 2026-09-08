using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CSweet.Contracts.GenAi;

namespace CSweet.UI.Services;

public sealed record MediaUploadFile(string Name, string ContentType, long Size,
    Func<long, int, CancellationToken, Task<byte[]>> Read);
public sealed record MediaUploadTicket(Guid OrganizationId, Guid ConversationId, string Name, string ContentType,
    long Size, string IdempotencyKey, string? Sha256 = null, Guid? SessionId = null);
public sealed record MediaUploadProgress(string Stage, long Bytes, long Total);
public sealed record ConversationMediaUploaded(Guid OrganizationId, Guid ConversationId, MediaAssetResponse Asset);
public sealed record ConversationUploadState(Guid OrganizationId, Guid ConversationId, bool Pending);

/// <summary>One bounded upload attempt. A retry always reads authoritative progress before sending bytes.</summary>
public sealed class ResumableAttachmentUpload(HttpClient http)
{
    public const int MaximumBufferBytes = 8 * 1024 * 1024;

    public async Task<MediaUploadPolicyResponse> ReadPolicyAsync(Guid organization, CancellationToken ct) =>
        await ReadAsync<MediaUploadPolicyResponse>(await http.GetAsync(
            $"api/media-assets/uploads/policy/organization/{organization:D}", ct), ct);

    public async Task<MediaAssetResponse> UploadAsync(Guid organization, Guid conversation, MediaUploadFile file,
        MediaUploadTicket? ticket, Func<MediaUploadTicket, Task> save, Action<MediaUploadProgress> progress,
        CancellationToken ct)
    {
        if (organization == Guid.Empty || conversation == Guid.Empty) throw new InvalidOperationException("Choose a conversation first.");
        var policy = await ReadPolicyAsync(organization, ct);
        MediaAttachmentPolicy.Validate([(file.ContentType, file.Size)], policy.MaximumFileSizeBytes);
        ticket ??= new(organization, conversation, file.Name, file.ContentType, file.Size, Guid.NewGuid().ToString("N"));
        if (ticket.OrganizationId != organization || ticket.ConversationId != conversation || ticket.Name != file.Name ||
            ticket.ContentType != file.ContentType || ticket.Size != file.Size || !Guid.TryParseExact(ticket.IdempotencyKey, "N", out _))
            throw new InvalidOperationException("Select the original file to resume this conversation's upload.");
        await save(ticket); // Retain the retry identity before creating any server-side session.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < file.Size;)
        {
            var count = (int)Math.Min(MaximumBufferBytes, file.Size - offset);
            var bytes = await ReadFileAsync(file, offset, count, ct);
            hash.AppendData(bytes); offset += count;
            progress(new("Checking file", offset, file.Size));
        }
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (ticket.Sha256 is not null && ticket.Sha256 != sha)
            throw new InvalidOperationException("This is not the original file. The saved upload was not changed.");
        ticket = ticket with { Sha256 = sha };
        await save(ticket);
        MediaUploadSessionResponse session;
        if (ticket.SessionId is { } sessionId)
            session = await ReadAsync<MediaUploadSessionResponse>(await http.GetAsync(SessionPath(organization, sessionId), ct), ct);
        else
            session = await ReadAsync<MediaUploadSessionResponse>(await http.PostAsJsonAsync(
                $"api/media-assets/uploads/organization/{organization:D}",
                new CreateMediaUploadSessionRequest(file.Name, file.ContentType, file.Size, sha, ticket.IdempotencyKey), ct), ct);
        ValidateSession(session, ticket);
        ticket = ticket with { SessionId = session.Id };
        await save(ticket);
        if (session.Status == "Completed") return Completed(session, ticket);
        RequireActive(session);
        progress(new("Uploading", session.ReceivedBytes, file.Size));
        while (session.ReceivedBytes < file.Size)
        {
            ct.ThrowIfCancellationRequested();
            var offset = session.ReceivedBytes;
            var count = (int)Math.Min(Math.Min(session.ChunkSizeBytes, MaximumBufferBytes), file.Size - offset);
            using var body = new ByteArrayContent(await ReadFileAsync(file, offset, count, ct));
            body.Headers.ContentType = new("application/octet-stream");
            session = await ReadAsync<MediaUploadSessionResponse>(await http.PutAsync(
                $"api/media-assets/uploads/{session.Id:D}/chunks/{offset}?organizationId={organization:D}", body, ct), ct);
            ValidateSession(session, ticket);
            RequireActive(session);
            if (session.ReceivedBytes != offset + count)
                throw new InvalidOperationException("Upload progress changed unexpectedly. Resume to check the stored position.");
            progress(new("Uploading", session.ReceivedBytes, file.Size));
        }
        progress(new("Validating stored file", file.Size, file.Size));
        session = await ReadAsync<MediaUploadSessionResponse>(await http.PostAsync(
            $"api/media-assets/uploads/{session.Id:D}/complete?organizationId={organization:D}", null, ct), ct);
        ValidateSession(session, ticket);
        ct.ThrowIfCancellationRequested();
        return Completed(session, ticket);
    }

    public async Task CancelAsync(MediaUploadTicket ticket, CancellationToken ct)
    {
        // If the create response was lost, replaying its exact identity retrieves the same session.
        var id = ticket.SessionId;
        if (id is null && ticket.Sha256 is not null)
        {
            var session = await ReadAsync<MediaUploadSessionResponse>(await http.PostAsJsonAsync(
                $"api/media-assets/uploads/organization/{ticket.OrganizationId:D}",
                new CreateMediaUploadSessionRequest(ticket.Name, ticket.ContentType, ticket.Size, ticket.Sha256, ticket.IdempotencyKey), ct), ct);
            ValidateSession(session, ticket); id = session.Id;
        }
        if (id is null) return;
        using var response = await http.DeleteAsync(SessionPath(ticket.OrganizationId, id.Value), ct);
        EnsureSuccess(response);
    }

    private static async Task<byte[]> ReadFileAsync(MediaUploadFile file, long offset, int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = await file.Read(offset, count, ct);
        if (bytes.Length != count) throw new InvalidOperationException("The selected file could not be read completely.");
        return bytes;
    }

    private static void ValidateSession(MediaUploadSessionResponse session, MediaUploadTicket ticket)
    {
        if (session.Id == Guid.Empty || ticket.SessionId is { } id && id != session.Id ||
            session.OrganizationId != ticket.OrganizationId || session.FileName != ticket.Name ||
            session.ContentType != ticket.ContentType || session.TotalBytes != ticket.Size ||
            session.ReceivedBytes < 0 || session.ReceivedBytes > session.TotalBytes ||
            session.ChunkSizeBytes is < 65536 or > 64 * 1024 * 1024)
            throw new InvalidOperationException("The upload response did not match the selected file.");
    }
    private static void RequireActive(MediaUploadSessionResponse session)
    {
        if (session.Status != "Active" || session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("This upload has ended or expired. Start a new upload.");
    }
    private static MediaAssetResponse Completed(MediaUploadSessionResponse session, MediaUploadTicket ticket)
    {
        var asset = session.Asset;
        if (session.Status != "Completed" || asset is null || asset.Id == Guid.Empty || asset.FileName != ticket.Name ||
            asset.ContentType != ticket.ContentType || asset.SizeBytes != ticket.Size || asset.Sha256 != ticket.Sha256)
            throw new InvalidOperationException("The stored file did not pass checksum verification. It was not attached.");
        return asset;
    }
    private static string SessionPath(Guid org, Guid session) => $"api/media-assets/uploads/{session:D}?organizationId={org:D}";
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            EnsureSuccess(response);
            return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new InvalidOperationException("The upload returned no progress.");
        }
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Sign in with access to this organization before resuming.",
            HttpStatusCode.NotFound => "The saved upload is unavailable. Start a new upload.",
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => "The file did not pass validation or exceeds the deployment's upload or storage limit.",
            HttpStatusCode.Conflict => "Stored upload progress changed. Resume to check its current position.",
            _ => "The upload was interrupted. Resume after the connection is available."
        });
    }
}
