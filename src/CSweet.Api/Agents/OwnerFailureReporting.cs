using System.Diagnostics;
using System.Net;
using CSweet.ConnectionService.Protocol;

namespace CSweet.Api.Agents;

public static class OwnerFailureReporting
{
    public const string StatusHeader = "X-CSweet-Owner-Status";
    public const string SourceHeader = "X-CSweet-Owner-Diagnostic-Source";
    public static Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder WithOwnerFailureHeaders(this Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder policy)
        => policy.WithExposedHeaders(StatusHeader, SourceHeader, OwnerFailureDiagnostic.Header);
    public static void Report(HttpContext context, Exception error, long started, ILogger logger)
    {
        var operation = context.Request.Path.Value switch {
            "/api/plugins/connection-service/owner/prepare" => "prepare", "/api/plugins/connection-service/owner/access" => "access",
            "/api/plugins/connection-service/owner/import" => "import", "/api/plugins/connection-service/owner/approve" => "approve",
            "/api/plugins/connection-service/owner/enroll" => "enroll", "/api/plugins/connection-service/owner/revoke" => "revoke", _ => null
        };
        if (operation is null) return;
        var remoteStatus = error is HttpRequestException { StatusCode: { } code } && (int)code is >= 400 and <= 599 ? (int?)code : null;
        // Without an observed remote status use the existing local HTTP400; do not invent an upstream response.
        var status = (HttpStatusCode)(remoteStatus ?? 400);
        var type = error switch {
            HttpRequestException => "HttpRequestException", OperationCanceledException => "OperationCanceledException", ArgumentException => "ArgumentException",
            InvalidOperationException => "InvalidOperationException", System.Text.Json.JsonException => "JsonException", System.Security.Cryptography.CryptographicException => "CryptographicException",
            IOException => "IOException", Microsoft.EntityFrameworkCore.DbUpdateException => "DbUpdateException", _ => "Exception"
        };
        // The typed exception is public: validate again instead of trusting even its diagnostic property.
        OwnerFailureDiagnostic? remote = null;
        if (error is OwnerOperationException { Diagnostic: { } hint })
        {
            try
            {
                using var check = new HttpResponseMessage(status);
                check.Headers.Add(OwnerFailureDiagnostic.Header, hint.Encode(status));
                remote = OwnerFailureDiagnostic.Read(check.Headers, status);
            }
            catch (ArgumentException) { }
        }
        var elapsed = (int)Math.Min(OwnerFailureDiagnostic.MaximumElapsedMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var diagnostic = remote ?? new(status == HttpStatusCode.Forbidden ? "authorization" : "operation", OwnerFailureDiagnostic.CodeFor(status), status == HttpStatusCode.Forbidden ? "UnauthorizedAccessException" : type, Guid.NewGuid().ToString("N"), elapsed);
        context.Response.Headers[StatusHeader] = ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers[SourceHeader] = remote is null ? "client" : "service";
        context.Response.Headers[OwnerFailureDiagnostic.Header] = diagnostic.Encode(status);
        logger.LogWarning("Owner client failed: operation={Operation} status={Status} remoteStatus={RemoteStatus} stage={Stage} code={Code} exceptionType={ExceptionType} correlationId={CorrelationId} serviceElapsedMs={ServiceElapsedMilliseconds} clientExceptionType={ClientExceptionType} clientElapsedMs={ClientElapsedMilliseconds}",
            operation, (int)status, remoteStatus, diagnostic.Stage, diagnostic.Code, diagnostic.ExceptionType, diagnostic.CorrelationId, remote?.ElapsedMilliseconds, type, elapsed);
    }
}
