using System.Diagnostics;
using System.Net;
using CSweet.Api.Agents;
using CSweet.ConnectionService.Protocol;
using CSweet.UI.Setup;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CSweet.UnitTests;

public sealed class OwnerFailureReportingTests
{
    [Theory]
    [InlineData(400, "name", "request_rejected", "ArgumentException")]
    [InlineData(403, "authorization", "not_authorized", "UnauthorizedAccessException")]
    public void SafeRemoteHintReachesAdministratorWithoutRawBodyOrException(int status, string stage, string code, string type)
    {
        var hint = new OwnerFailureDiagnostic(stage, code, type, new string('a', 32), 94);
        var error = new OwnerOperationException((HttpStatusCode)status, hint);
        var context = new DefaultHttpContext(); context.Request.Path = "/api/plugins/connection-service/owner/enroll";
        var logger = new CapturedLogger();
        OwnerFailureReporting.Report(context, error, Stopwatch.GetTimestamp(), logger);
        using var response = LocalResponse(context);
        var shown = Assert.Throws<OwnerPanelOperationException>(() => OwnerFailureDisplay.EnsureSuccess(response));
        Assert.Contains($"HTTP {status}", shown.Message); Assert.Contains($"stage {stage}", shown.Message);
        Assert.Contains(hint.CorrelationId, shown.Message + string.Join("\n", logger.Messages));
        Assert.Contains("check service state before retrying", shown.Message);
        Assert.Contains("clientExceptionType=HttpRequestException", string.Join("\n", logger.Messages));
    }

    [Fact]
    public void LegacyFailureUsesLocalHintsWithoutLeakingRawExceptionOrClaimingRemoteStage()
    {
        var context = new DefaultHttpContext(); context.Request.Path = "/api/plugins/connection-service/owner/enroll";
        var logger = new CapturedLogger();
        OwnerFailureReporting.Report(context, new HttpRequestException("SENSITIVE_RESPONSE_BODY_AND_PROOF", null, HttpStatusCode.BadRequest), Stopwatch.GetTimestamp(), logger);
        using var response = LocalResponse(context);
        var shown = Assert.Throws<OwnerPanelOperationException>(() => OwnerFailureDisplay.EnsureSuccess(response));
        Assert.Contains("stage operation", shown.Message);
        Assert.DoesNotContain("SENSITIVE", shown.Message + string.Join("\n", logger.Messages));
    }

    [Theory]
    [InlineData("bad-stage", "ArgumentException", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("name", "SENSITIVE_EXCEPTION", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("name", "ArgumentException", "SENSITIVE_CORRELATION")]
    public void PublicTypedExceptionsCannotInjectUnvalidatedHints(string stage, string type, string correlation)
    {
        var context = new DefaultHttpContext(); context.Request.Path = "/api/plugins/connection-service/owner/enroll";
        var logger = new CapturedLogger();
        OwnerFailureReporting.Report(context, new OwnerOperationException(HttpStatusCode.BadRequest, new(stage, "request_rejected", type, correlation, 1)), Stopwatch.GetTimestamp(), logger);
        using var response = LocalResponse(context);
        var shown = Assert.Throws<OwnerPanelOperationException>(() => OwnerFailureDisplay.EnsureSuccess(response));
        Assert.Contains("stage operation", shown.Message);
        Assert.DoesNotContain("SENSITIVE", shown.Message + string.Join("\n", logger.Messages));
        Assert.DoesNotContain("bad-stage", shown.Message + string.Join("\n", logger.Messages));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("untrusted-body-header")]
    [InlineData("v1.name.request_rejected.ArgumentException.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.0001")]
    [InlineData("v1.name.request_rejected.ArgumentException.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.3600001")]
    [InlineData("v1.name.request_rejected.ArgumentException.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.1,extra")]
    public void UiSupportsLegacyAndIgnoresMalformedMetadata(string? metadata)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("SENSITIVE_RESPONSE_BODY") };
        response.Headers.Add(OwnerFailureDisplay.StatusHeader, "400");
        if (metadata is not null) response.Headers.TryAddWithoutValidation(OwnerFailureDiagnostic.Header, metadata);
        var shown = Assert.Throws<OwnerPanelOperationException>(() => OwnerFailureDisplay.EnsureSuccess(response));
        Assert.DoesNotContain("Diagnostic:", shown.Message); Assert.DoesNotContain("SENSITIVE", shown.Message);
        Assert.DoesNotContain("untrusted", shown.Message);
    }

    [Theory]
    [InlineData("0400")]
    [InlineData("400,403")]
    [InlineData(" 400")]
    [InlineData("200")]
    [InlineData("SENSITIVE")]
    public void UiRejectsNoncanonicalCrossBoundaryStatus(string status)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        response.Headers.TryAddWithoutValidation(OwnerFailureDisplay.StatusHeader, status);
        response.Headers.Add(OwnerFailureDiagnostic.Header, new OwnerFailureDiagnostic("name", "request_rejected", "ArgumentException", new string('a', 32), 1).Encode(HttpStatusCode.BadRequest));
        Assert.DoesNotContain("Diagnostic:", Assert.Throws<OwnerPanelOperationException>(() => OwnerFailureDisplay.EnsureSuccess(response)).Message);
    }

    [Fact]
    public void CancellationHasSafeTimeoutHintsAndUnknownRoutesDoNotReflectInput()
    {
        var context = new DefaultHttpContext(); context.Request.Path = "/api/plugins/connection-service/owner/enroll";
        var logger = new CapturedLogger();
        OwnerFailureReporting.Report(context, new OperationCanceledException("SENSITIVE"), Stopwatch.GetTimestamp(), logger);
        Assert.Equal("504", context.Response.Headers[OwnerFailureReporting.StatusHeader].ToString());
        Assert.DoesNotContain("SENSITIVE", string.Join("\n", logger.Messages));
        var unknown = new DefaultHttpContext(); unknown.Request.Path = "/SENSITIVE";
        OwnerFailureReporting.Report(unknown, new InvalidOperationException("SENSITIVE"), Stopwatch.GetTimestamp(), logger);
        Assert.Empty(unknown.Response.Headers); Assert.Single(logger.Messages);
    }
    private static HttpResponseMessage LocalResponse(HttpContext context)
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        foreach (var header in context.Response.Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        return response;
    }
    private sealed class CapturedLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
}
