using CSweet.Office.Contracts.ControlPlane;
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

    Task<LocalOfficeSetupActionResponse> CreateLocalSetupSessionAsync(
        CreateLocalOfficeSetupSessionRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<LocalOfficeSetupActionResponse> LaunchLocalSetupSessionAsync(
        Guid sessionId,
        Guid createdByUserId,
        LaunchLocalOfficeSetupRequest request,
        CancellationToken cancellationToken = default);

    Task<LocalOfficeSetupActionResponse> GetLocalSetupSessionAsync(
        Guid sessionId,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<LocalOfficeSetupActionResponse> GetActiveLocalSetupSessionAsync(
        Guid createdByUserId,
        CancellationToken cancellationToken = default);
    Task<LocalOfficeSetupActionResponse> RefreshLocalSetupSessionHandoffAsync(
        Guid sessionId,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<LocalOfficeSetupActionResponse> SelectLocalSetupRecoveryAsync(
        Guid sessionId,
        Guid createdByUserId,
        SelectLocalOfficeRecoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<AssistedOfficePreflightResponse> PreflightLocalSetupSessionAsync(
        AssistedOfficePreflightRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ReportLocalSetupResultAsync(
        ReportAssistedOfficeSetupResultRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteLocalOfficeRemovalAsync(
        CompleteAssistedOfficeRemovalRequest request,
        CancellationToken cancellationToken = default);

    Task<RedeemAssistedOfficeSetupResponse> RedeemLocalSetupSessionAsync(
        RedeemAssistedOfficeSetupRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> RevokeEnrollmentAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default);

    Task<ClaimOfficeResponse> ClaimNodeAsync(
        ClaimOfficeRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> ApproveNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<ExecutionCapacityActionResponse> RejectNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);

    Task<bool> RecordHeartbeatAsync(
        Guid nodeId,
        OfficeHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    Task<OfficeCertificateResponse> GetOperationalCertificateAsync(
        Guid nodeId,
        OfficeCertificateRequest request,
        CancellationToken cancellationToken = default);

    Task<OfficeCertificateResponse> RotateOperationalCertificateAsync(
        Guid nodeId,
        string certificateThumbprint,
        string certificateSerialNumber,
        CancellationToken cancellationToken = default);

    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
