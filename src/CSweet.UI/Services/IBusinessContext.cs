using CSweet.Contracts.Core;

namespace CSweet.UI.Services;

public interface IBusinessContext
{
    IReadOnlyList<OrganizationResponse> Businesses { get; }
    OrganizationResponse? SelectedBusiness { get; }
    bool IsLoading { get; }
    string? ErrorMessage { get; }
    event Action? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task SelectAsync(Guid businessId, bool navigate = true, CancellationToken cancellationToken = default);
    Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default);
}
