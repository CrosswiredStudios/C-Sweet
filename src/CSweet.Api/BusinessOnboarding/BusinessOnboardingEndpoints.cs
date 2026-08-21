using CSweet.Application.BusinessOnboarding;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Api.Auth;
using System.Security.Claims;

namespace CSweet.Api.BusinessOnboarding;

public static class BusinessOnboardingEndpoints
{
    public static IEndpointRouteBuilder MapBusinessOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/business-onboarding");

        group.MapPost("/complete", async (
            CompleteBusinessOnboardingRequest request,
            ClaimsPrincipal principal,
            IBusinessOnboardingService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.CompleteAsync(request, cancellationToken, principal.GetApplicationUserId());
            return result.Succeeded
                ? Results.Ok(result.Onboarding)
                : Results.BadRequest(result);
        });

        group.MapPost("/operations", async (
            StartBusinessOnboardingRequest request,
            ClaimsPrincipal principal,
            IBusinessOnboardingOperationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var applicationUserId = principal.GetApplicationUserId();
                if (!applicationUserId.HasValue) return Results.Unauthorized();
                var operation = await service.StartAsync(
                    request, applicationUserId.Value, cancellationToken);
                return BusinessOnboardingOperationStatuses.IsActive(operation.Status)
                    ? Results.Accepted($"/api/business-onboarding/operations/{operation.Id:D}", operation)
                    : Results.Ok(operation);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new BusinessOnboardingActionResponse(false, "validation_error", exception.Message));
            }
        });

        group.MapGet("/operations", async (
            ClaimsPrincipal principal,
            IBusinessOnboardingOperationService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = principal.GetApplicationUserId();
            return !applicationUserId.HasValue
                ? Results.Unauthorized()
                : Results.Ok(await service.ListForUserAsync(applicationUserId.Value, cancellationToken));
        });

        group.MapGet("/operations/{operationId:guid}", async (
            Guid operationId,
            ClaimsPrincipal principal,
            IBusinessOnboardingOperationService service,
            CancellationToken cancellationToken) =>
        {
            var applicationUserId = principal.GetApplicationUserId();
            if (!applicationUserId.HasValue) return Results.Unauthorized();
            return await service.GetForUserAsync(operationId, applicationUserId.Value, cancellationToken) is { } operation
                ? Results.Ok(operation)
                : Results.NotFound();
        });

        group.MapPost("/operations/{operationId:guid}/retry", async (
            Guid operationId,
            ClaimsPrincipal principal,
            IBusinessOnboardingOperationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var applicationUserId = principal.GetApplicationUserId();
                if (!applicationUserId.HasValue) return Results.Unauthorized();
                return await service.RetryAsync(operationId, applicationUserId.Value, cancellationToken) is { } operation
                    ? Results.Accepted($"/api/business-onboarding/operations/{operation.Id:D}", operation)
                    : Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new BusinessOnboardingActionResponse(false, "invalid_state", exception.Message));
            }
        });

        group.MapPost("/operations/{operationId:guid}/dismiss", async (
            Guid operationId,
            ClaimsPrincipal principal,
            IBusinessOnboardingOperationService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var applicationUserId = principal.GetApplicationUserId();
                if (!applicationUserId.HasValue) return Results.Unauthorized();
                return await service.DismissAsync(operationId, applicationUserId.Value, cancellationToken) is { } operation
                    ? Results.Ok(operation)
                    : Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new BusinessOnboardingActionResponse(false, "invalid_state", exception.Message));
            }
        });

        group.MapPost("/{organizationId:guid}/chief", async (
            Guid organizationId,
            CompleteChiefSetupRequest request,
            IBusinessOnboardingService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.AssignChiefAsync(organizationId, request, cancellationToken);
            return result.Succeeded ? Results.Ok(result.Setup) : Results.BadRequest(result);
        });

        return endpoints;
    }
}
