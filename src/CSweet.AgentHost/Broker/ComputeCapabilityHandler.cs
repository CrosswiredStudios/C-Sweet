using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class ComputeCapabilityHandler(IComputeBroker broker, IComputeDefaults? defaults = null) : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability is InfrastructureActions.Provision or InfrastructureActions.Read or
        InfrastructureActions.List or InfrastructureActions.Start or InfrastructureActions.Stop or InfrastructureActions.Restart or InfrastructureActions.Destroy or InfrastructureActions.Execute or InfrastructureActions.PublishPort;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        CapabilityResult result;
        try
        {
            if (!CanHandle(request.Capability) || !session.Grant.RequiredCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var organizationId) || organizationId == Guid.Empty ||
                !Guid.TryParse(session.InstallationId, out var installationId) || installationId == Guid.Empty)
                throw new UnauthorizedAccessException();
            if (request.ContentType != "application/json" || request.Payload.Length is < 1 or > 32768)
                throw new ArgumentException();
            object response;
            if (request.Capability == InfrastructureActions.Provision)
                response = await broker.RequestAsync(organizationId, installationId, Read<RequestComputeEnvironment>(request), token);
            else if (request.Capability == InfrastructureActions.Read)
            {
                var input = Read<ReadInput>(request);
                if (input.Defaults == true && input.OperationId is null && input.EnvironmentId is null)
                    response = await (defaults ?? throw new InvalidOperationException()).ReadAsync(organizationId, installationId, token);
                else if (input.Defaults is not null) throw new ArgumentException();
                else if (input.OperationId is { } operationId && input.EnvironmentId is null)
                    response = await broker.ReadOperationAsync(organizationId, installationId, operationId, token);
                else if (input.EnvironmentId is { } environmentId && input.OperationId is null)
                    response = await broker.ReadAsync(organizationId, installationId, environmentId, token);
                else throw new ArgumentException("Choose an environment or operation.");
            }
            else if (request.Capability is InfrastructureActions.Execute or InfrastructureActions.PublishPort)
            {
                var input = Read<RequestComputeWorkload>(request);
                if (input.Workload is null || (input.Workload.Command is not null) != (request.Capability == InfrastructureActions.Execute)) throw new ArgumentException();
                response = await broker.SubmitWorkloadAsync(organizationId, installationId, input, token);
            }
            else if (request.Capability == InfrastructureActions.List)
            {
                var input = Read<ListInput>(request);
                response = await broker.ListAsync(organizationId, installationId, input.WorkstreamId, input.AfterId, input.Limit, token);
            }
            else
            {
                var input = Read<LifecycleInput>(request);
                response = await broker.ChangeLifecycleAsync(organizationId, installationId,
                    new(input.EnvironmentId, input.ExpectedGeneration, request.Capability, input.IdempotencyKey), token);
            }
            result = new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.From(response, ComputeProtocol.Json) };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var code = error switch
            {
                UnauthorizedAccessException => "compute_authority_denied",
                JsonException or ArgumentException or InvalidDataException => "compute_request_invalid",
                InvalidOperationException => "compute_conflict",
                _ => "compute_unavailable"
            };
            result = new() { RequestId = request.RequestId, Succeeded = false, Error = code, FailureCode = code,
                Retryable = code == "compute_unavailable", Payload = JsonPayload.From(new { error = code }) };
        }
        yield return result;
    }

    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, ComputeProtocol.Json)
        ?? throw new JsonException("Compute input is missing.");
    private sealed record ReadInput(Guid? EnvironmentId = null, Guid? OperationId = null, bool? Defaults = null);
    private sealed record ListInput(Guid WorkstreamId, Guid? AfterId = null, int Limit = 50);
    private sealed record LifecycleInput(Guid EnvironmentId, long ExpectedGeneration, string IdempotencyKey);
}
