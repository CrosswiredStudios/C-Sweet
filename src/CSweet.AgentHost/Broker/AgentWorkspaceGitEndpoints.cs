using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using CSweet.TrustedServices;

namespace CSweet.AgentHost.Broker;

/// <summary>Session-authenticated smart HTTP for a persisted workspace, without provider credentials.</summary>
public static class AgentWorkspaceGitEndpoints
{
    // The current guest broker is buffered and permits at most 16 MiB per body. Streaming is separate.
    internal const int MaximumBodyBytes = 16 * 1024 * 1024;

    public static IEndpointRouteBuilder MapAgentWorkspaceGitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/git/workspaces/{workspaceId:guid}/{assignmentRevision:long}/{**path}", ["GET", "POST"],
            HandleAsync).RequireRateLimiting("mcp-session").DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaximumBodyBytes));
        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(Guid workspaceId, long assignmentRevision, string path,
        HttpContext http, GitWorkspaceCapabilityHandler handler, ILogger<GitWorkspaceCapabilityHandler> logger, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var session = http.Features.Get<McpIngressPolicy.Authentication>()?.Session;
        if (session is null) return Results.StatusCode(401);
        http.Response.Headers["Mcp-Session-Id"] = session.SessionId;
        var advertise = HttpMethods.IsGet(http.Request.Method) && path == "info/refs";
        var service = advertise ? http.Request.Query["service"].ToString() : path;
        if ((!advertise && !HttpMethods.IsPost(http.Request.Method)) ||
            service is not ("git-upload-pack" or "git-receive-pack")) return Results.NotFound();
        if (http.Request.Query.Keys.Any(key => !advertise || key != "service")) return Results.BadRequest();
        if (!advertise && http.Request.ContentType != $"application/x-{service}-request") return Results.StatusCode(415);
        var encoding = http.Request.Headers.ContentEncoding.ToString();
        if (encoding is not ("" or "gzip") || advertise && encoding != "") return Results.StatusCode(415);
        if (http.Request.ContentLength > MaximumBodyBytes) return Results.StatusCode(413);
        try
        {
            using var gzip = encoding == "gzip" ? new GZipStream(http.Request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
            var input = (Stream?)gzip ?? http.Request.Body;
            using var body = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + count > MaximumBodyBytes) return Results.StatusCode(413);
                body.Write(buffer, 0, count);
            }
            var result = await handler.GitAsync(session, workspaceId, assignmentRevision, service, advertise, body.ToArray(), ct);
            if (result.Body.Length > MaximumBodyBytes) return Results.StatusCode(413);
            return Results.Bytes(result.Body, result.ContentType);
        }
        catch (Exception error) when (WorkspaceOperationErrors.IsExpected(error))
        { return WorkspaceOperationErrors.Result(error, logger); }
    }
}
