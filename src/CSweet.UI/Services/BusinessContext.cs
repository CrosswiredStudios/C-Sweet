using CSweet.Contracts.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace CSweet.UI.Services;

public sealed class BusinessContext : IBusinessContext, IDisposable
{
    private const string StorageKey = "csweet.focusedBusinessId";
    private readonly IOrganizationApiClient _organizations;
    private readonly NavigationManager _navigation;
    private readonly IJSRuntime _js;
    private bool _initialized;

    public BusinessContext(IOrganizationApiClient organizations, NavigationManager navigation, IJSRuntime js)
    {
        _organizations = organizations;
        _navigation = navigation;
        _js = js;
        _navigation.LocationChanged += OnLocationChanged;
    }

    public IReadOnlyList<OrganizationResponse> Businesses { get; private set; } = [];
    public OrganizationResponse? SelectedBusiness { get; private set; }
    public bool IsLoading { get; private set; }
    public string? ErrorMessage { get; private set; }
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        NotifyChanged();

        try
        {
            Businesses = await _organizations.ListAsync(cancellationToken);
            var persistedId = await ReadPersistedIdAsync(cancellationToken);
            var selectedId = BusinessNavigation.ResolveSelection(CurrentPath(), persistedId, Businesses);
            SelectedBusiness = Businesses.FirstOrDefault(x => x.Id == selectedId);
            await PersistSelectionAsync(cancellationToken);
        }
        catch
        {
            Businesses = [];
            SelectedBusiness = null;
            ErrorMessage = "Businesses could not be loaded.";
        }
        finally
        {
            IsLoading = false;
            NotifyChanged();
        }
    }

    public async Task SelectAsync(Guid businessId, bool navigate = true, CancellationToken cancellationToken = default)
    {
        var business = Businesses.FirstOrDefault(x => x.Id == businessId);
        if (business is null)
        {
            return;
        }

        SelectedBusiness = business;
        await PersistSelectionAsync(cancellationToken);
        NotifyChanged();

        if (navigate)
        {
            _navigation.NavigateTo(BusinessNavigation.SwitchDestination(CurrentPath(), businessId));
        }
    }

    public async Task RemoveDeletedBusinessAsync(Guid businessId, CancellationToken cancellationToken = default)
    {
        Businesses = Businesses.Where(x => x.Id != businessId).ToList();
        if (SelectedBusiness?.Id == businessId)
        {
            SelectedBusiness = Businesses.FirstOrDefault();
        }

        await PersistSelectionAsync(cancellationToken);
        NotifyChanged();
    }

    private async void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        var routeId = BusinessNavigation.OrganizationIdFromPath(_navigation.ToBaseRelativePath(args.Location));
        var business = routeId is null ? null : Businesses.FirstOrDefault(x => x.Id == routeId);
        if (business is null || business.Id == SelectedBusiness?.Id)
        {
            return;
        }

        SelectedBusiness = business;
        await PersistSelectionAsync(CancellationToken.None);
        NotifyChanged();
    }

    private string CurrentPath() => _navigation.ToBaseRelativePath(_navigation.Uri);

    private async Task<Guid?> ReadPersistedIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var value = await _js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, StorageKey);
            return Guid.TryParse(value, out var id) ? id : null;
        }
        catch (JSException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task PersistSelectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (SelectedBusiness is null)
            {
                await _js.InvokeVoidAsync("localStorage.removeItem", cancellationToken, StorageKey);
            }
            else
            {
                await _js.InvokeVoidAsync("localStorage.setItem", cancellationToken, StorageKey, SelectedBusiness.Id.ToString());
            }
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    private void NotifyChanged() => Changed?.Invoke();

    public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;
}
