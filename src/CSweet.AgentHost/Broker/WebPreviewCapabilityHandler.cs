using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.AgentHost.Broker;

public sealed class WebPreviewCapabilityHandler(WebPreviewGrantService service, WebPreviewExecutionService execution, DeliveryEvidenceCapabilityHandler? builds = null) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability is WebPreviewCapabilities.List or WebPreviewCapabilities.RequestGrant or WebPreviewCapabilities.Preflight or WebPreviewCapabilities.Start or WebPreviewCapabilities.Read or WebPreviewCapabilities.Stop or WebPreviewCapabilities.Diagnostics or WebPreviewCapabilities.Renew or WebPreviewCapabilities.Test or WebPreviewCapabilities.Build;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session,RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        CapabilityResult result;
        try
        {
            if(!CanHandle(request.Capability) || !session.Grant.RequiredCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId,out var organizationId) || !Guid.TryParse(session.InstallationId,out var installationId))
                throw new UnauthorizedAccessException("The session does not have this preview capability.");
            if(request.Payload.Length>1024*1024) throw new InvalidDataException("The preview request exceeds its input limit.");
            object value = request.Capability switch
            {
                WebPreviewCapabilities.List => await execution.ListAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<ListPreviewsRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.Build => await BuildAsync(organizationId, installationId, session,
                    JsonSerializer.Deserialize<PreviewBuildRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.Renew => await execution.RenewAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<RenewPreviewRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.Test => await execution.TestAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<RunPreviewTestsRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.RequestGrant => await service.RequestAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<RequestPreviewGrant>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.Preflight => await service.PreflightAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<PreviewRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.Start => await execution.StartAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<PreviewRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                WebPreviewCapabilities.Read => await execution.ReadAsync(organizationId, installationId,
                    (JsonSerializer.Deserialize<PreviewIdentityRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException()).PreviewId, cancellationToken),
                WebPreviewCapabilities.Stop => await execution.StopAsync(organizationId, installationId,
                    (JsonSerializer.Deserialize<PreviewIdentityRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException()).PreviewId, cancellationToken),
                WebPreviewCapabilities.Diagnostics => await execution.DiagnosticsAsync(organizationId, installationId,
                    JsonSerializer.Deserialize<PreviewDiagnosticRequest>(request.Payload.Span, PreviewJson.Options) ?? throw new JsonException(), cancellationToken),
                _ => throw new UnauthorizedAccessException()
            };            result=new() { RequestId=request.RequestId,Succeeded=true,ContentType="application/json",
                Payload=JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value,PreviewJson.Options)) };
        }
        catch(Exception error) when(error is UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidOperationException or IOException or DbUpdateException)
        {
            var code=error is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied :
                error is DbUpdateException ? PlatformCapabilityErrorCode.Conflict : PlatformCapabilityErrorCode.ValidationFailed;
            var message=error is DbUpdateException ? "The preview request changed concurrently. Retry with the same request key." : error.Message;
            result=new() { RequestId=request.RequestId,Succeeded=false,ContentType="application/json",Error=message,
                Payload=JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code,message),PreviewJson.Options)) };
        }
        yield return result;
    }
    private async Task<PreviewBuildOperation> BuildAsync(Guid organization, Guid installation, AgentSession session, PreviewBuildRequest input, CancellationToken token)
    {
        if (builds is null || !session.Grant.RequiredCapabilities.Contains(CSweet.WorkManagement.Contracts.DeliveryEvidenceCapabilityNames.BuildRequestV2))
            throw new UnauthorizedAccessException("The normal certified-toolchain build capability is required.");
        var actorId = await execution.AuthorizeBuildAsync(organization, installation, input, token);
        var request = input.Preview;
        var build = await builds.RequestBuildAsync(organization, installation, actorId,
            new(request.ProjectId, null, input.ToolchainDefinitionId, input.ToolchainProviderInstallationId, request.RepositoryId,
                request.Manifest.SourceRevision, input.RecipeKey, input.TargetKey, input.Configuration, input.MaximumAttempts, request.IdempotencyKey), token);
        return new(build.Id, build.Status);
    }}
