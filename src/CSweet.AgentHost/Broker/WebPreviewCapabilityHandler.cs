using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Setup;
using CSweet.WebHost.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.AgentHost.Broker;

public sealed class WebPreviewCapabilityHandler(WebPreviewGrantService service) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability is WebPreviewCapabilities.RequestGrant or WebPreviewCapabilities.Preflight;
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
            object value=request.Capability==WebPreviewCapabilities.RequestGrant
                ? await service.RequestAsync(organizationId,installationId,
                    JsonSerializer.Deserialize<RequestPreviewGrant>(request.Payload.Span,PreviewJson.Options) ?? throw new JsonException("A grant request is required."),cancellationToken)
                : await service.PreflightAsync(organizationId,installationId,
                    JsonSerializer.Deserialize<PreviewRequest>(request.Payload.Span,PreviewJson.Options) ?? throw new JsonException("A preview request is required."),cancellationToken);
            result=new() { RequestId=request.RequestId,Succeeded=true,ContentType="application/json",
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
}
