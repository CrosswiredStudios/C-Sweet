using CSweet.Agent.SDK;

namespace CSweet.Application.Core;

public interface IChiefLeadershipService
{
    Task<ChiefLeadershipSnapshot> ReadAsync(Guid organization, Guid installation, ReadChiefLeadershipRequest request, CancellationToken token = default);
    Task<PresentedChiefFocus> PresentAsync(Guid organization, Guid installation, PresentChiefFocusRequest request, CancellationToken token = default);
    Task<ChiefLeadershipSnapshot> CaptureAsync(Guid organization, Guid installation, CaptureChiefFocusRequest request, CancellationToken token = default);
    Task<ChiefCoverageAssessment> AssessAsync(Guid organization, Guid installation, RecordChiefAssessmentRequest request, CancellationToken token = default);
    Task<ChiefLeadershipSnapshot> ControlAsync(Guid organization, Guid installation, ControlChiefLeadershipRequest request, CancellationToken token = default);
}
