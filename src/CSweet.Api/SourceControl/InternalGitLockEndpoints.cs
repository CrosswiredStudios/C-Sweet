using System.Globalization;
using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.Infrastructure.SourceControl;

namespace CSweet.Api.SourceControl;

public static partial class InternalGitHttpEndpoints
{
    private sealed record LockBody(string? Path = null, bool Force = false, string? Cursor = null, int Limit = 100);
    private static async Task<IResult> LocksAsync(Guid business, Guid repository, string path, string token, HttpContext http, InternalGitAccessService service, CancellationToken ct)
    {
        const string media = "application/vnd.git-lfs+json";
        await service.AuthorizeAsync(business, repository, token, "git-upload-pack", ct);
        string operation; string? id = null; var body = new LockBody();
        if (path == "info/lfs/locks" && HttpMethods.IsGet(http.Request.Method))
        {
            operation = "list";
            var limit = 100;
            if (http.Request.Query.TryGetValue("limit", out var raw) && !int.TryParse(raw, out limit)) return Results.BadRequest();
            body = new(http.Request.Query["path"], Cursor: http.Request.Query["cursor"], Limit: limit); id = http.Request.Query["id"];
        }
        else if (HttpMethods.IsPost(http.Request.Method))
        {
            if (path == "info/lfs/locks") operation = "create";
            else if (path == "info/lfs/locks/verify") operation = "verify";
            else if (path.StartsWith("info/lfs/locks/", StringComparison.Ordinal) && path.EndsWith("/unlock", StringComparison.Ordinal))
            { operation = "unlock"; id = path[15..^7]; }
            else return Results.NotFound();
            try { body = JsonSerializer.Deserialize<LockBody>(await ReadBodyAsync(http.Request.Body, 16384, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new(); }
            catch (JsonException) { return Results.BadRequest(); }
        }
        else return Results.StatusCode(405);
        (Guid Actor, InternalGitLockResult Result) response;
        try { response = await service.LocksWithTokenAsync(business, repository, token, new(operation, body.Path, id, body.Force, body.Cursor), body.Limit, ct); }
        catch (UnauthorizedAccessException) { return Results.Json(new { message = "Push access is required for this lock operation." }, contentType: media, statusCode: 403); }
        var result = response.Result;
        object Describe(InternalGitFileLock item) => new { id = item.Id, path = item.Path,
            locked_at = item.LockedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), owner = new { name = item.OwnerName } };
        object payload = operation switch
        {
            "list" => new { locks = result.Locks.Select(Describe), next_cursor = result.NextCursor },
            "verify" => new { ours = result.Locks.Where(l => l.OwnerId == response.Actor).Select(Describe),
                theirs = result.Locks.Where(l => l.OwnerId != response.Actor).Select(Describe), next_cursor = result.NextCursor },
            _ => new { @lock = result.Locks.FirstOrDefault() is { } item ? Describe(item) : null, message = result.Message }
        };
        return Results.Json(payload, contentType: media, statusCode: result.StatusCode);
    }
}
