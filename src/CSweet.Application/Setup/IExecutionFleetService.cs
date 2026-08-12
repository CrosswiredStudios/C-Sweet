using CSweet.SatelliteOffice.Contracts.ControlPlane;
using CSweet.Contracts.Setup;

namespace CSweet.Application.Setup;

public interface IExecutionFleetService
{
    Task EnsureDefaultPoolAsync(CancellationToken cancellationToken = default);

    Task<ExecutionCapacityOnboardingResponse> GetOnboardingStatusAsync(
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> SelectOnboardingModeAsync(
        SelectExecutionOnboardingModeRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> CreateEnrollmentAsync(
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> RevokeEnrollmentAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default);

    Task<ClaimSatelliteOfficeResponse> ClaimNodeAsync(
        ClaimSatelliteOfficeRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> ApproveNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> RejectNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<bool> RecordHeartbeatAsync(
        Guid nodeId,
        SatelliteOfficeHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    Task<SatelliteOfficeCertificateResponse> GetOperationalCertificateAsync(
        Guid nodeId,
        SatelliteOfficeCertificateRequest request,
        CancellationToken cancellationToken = default);

    Task<SatelliteOfficeCertificateResponse> RotateOperationalCertificateAsync(
        Guid nodeId,
        string certificateThumbprint,
        string certificateSerialNumber,
        CancellationToken cancellationToken = default);

    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
