using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class PersonalTodoCapabilityHandler(
    CSweetDbContext db,
    IPersonalTodoService service) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string capability) => PersonalTodoActions.All.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session, RequestCapability request, CancellationToken token)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                $"The installation is not granted {request.Capability}.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The installation identity is invalid.");
        var actorId = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        if (!actorId.HasValue)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The installation is not assigned to an active employee in this organization.");
        var actor = new PersonalTodoActor(actorId.Value, installationId);
        try
        {
            object result = request.Capability switch
            {
                PersonalTodoActions.Read => await service.ListAsync(organizationId, actor, false, token),
                PersonalTodoActions.Add => await service.AddAsync(organizationId, actor,
                    Read<Wire.AddPersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Reorder => await service.ReorderAsync(organizationId, actor,
                    Read<Wire.ReorderPersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Requeue => await service.RequeueAsync(organizationId, actor,
                    Read<Wire.RequeuePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Activate => await service.ActivateAsync(organizationId, actor,
                    Read<Wire.ActivatePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Update => await service.UpdateAsync(organizationId, actor,
                    Read<Wire.UpdatePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Archive => await service.ArchiveAsync(organizationId, actor,
                    Read<Wire.ArchivePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Restore => await service.RestoreAsync(organizationId, actor,
                    Read<Wire.RestorePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Claim => await service.ClaimAsync(organizationId, actor,
                    Read<Wire.ClaimPersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Complete => await service.CompleteAsync(organizationId, actor,
                    Read<Wire.CompletePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Block => await service.BlockAsync(organizationId, actor,
                    Read<Wire.BlockPersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Release => await service.ReleaseAsync(organizationId, actor,
                    Read<Wire.ReleasePersonalTodoItemRequest>(request), token),
                PersonalTodoActions.Defer => await service.DeferAsync(organizationId, actor,
                    Read<Wire.DeferPersonalTodoItemRequest>(request), token),
                _ => throw new KeyNotFoundException("The personal to-do capability is not implemented.")
            };
            return Success(request.RequestId, result);
        }
        catch (JsonException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message);
        }
    }

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new JsonException("The personal to-do payload is required.");

    private static CapabilityResult Success<T>(string requestId, T value) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    };

    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string message) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = message,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(
            new PlatformCapabilityError(code, message), JsonOptions))
    };
}
