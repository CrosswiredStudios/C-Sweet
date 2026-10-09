using CSweet.Contracts.WorkManagement;

namespace CSweet.Application.WorkManagement;

public interface IWorkInstructionPublicationService
{
    Task<WorkInstructionPreview> PreviewAsync(Guid organization, Guid board, Guid item, Guid user,
        SelectWorkInstructionRequest request, CancellationToken token = default);
    Task<WorkInstructionPublicationResponse> PublishAsync(Guid organization, Guid board, Guid item, Guid user,
        PublishWorkInstructionRequest request, CancellationToken token = default);
    Task<WorkInstructionPublicationPage> ListAsync(Guid organization, Guid board, Guid item, Guid user,
        Guid? cursor = null, CancellationToken token = default);
}
