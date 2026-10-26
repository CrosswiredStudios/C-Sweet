using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components.Forms;

namespace CSweet.UI.Pages;

public partial class Plugins
{
    private const long MaximumBundleBytes = 512L * 1024 * 1024;
    private int _activeTab;
    private string _installedSearch = string.Empty;
    private string _catalogSearch = string.Empty;
    private string _catalogCategory = "All";
    private bool _uploadBundle;
    private IBrowserFile? _bundle;
    private Guid? _businessId;
    private Guid? _managedId;
    private Guid? _removePending;
    private AgentInstallationResponse? _updateTarget;
    private Guid? _stagedUpdateId;
    private bool UpdateMatches => _updateTarget is null || (_preview is { PluginKind: "Service", Accounts: null, PackageSourceMode: "PluginBundle" } &&
        _preview.AgentId == _updateTarget.AgentId && (_updateTarget.InstallationScope == "System" ||
            _businessId?.ToString("D") == _updateTarget.BusinessId));
    private bool _acknowledgeAllPublic;
    private bool IsToolchainProvider => _preview?.PluginKind == "Service" && _preview.ToolchainAdapters.Provides.Count > 0;
    private IEnumerable<AgentInstallationResponse> FilteredInstallations => _installations.Where(x =>
        $"{x.AgentName} {x.AgentId} {x.PublisherName} {x.PluginKind} {x.Summary}".Contains(_installedSearch, StringComparison.OrdinalIgnoreCase));
    private IEnumerable<PluginCatalogEntry> FilteredCatalog => PluginCatalog.Entries.Where(x =>
        (_catalogCategory == "All" || x.Category == _catalogCategory) &&
        $"{x.Name} {L[x.DescriptionKey]} {L[$"Plugins.Catalog.Category.{x.Category}"]}".Contains(_catalogSearch, StringComparison.OrdinalIgnoreCase));
    private bool CanInstall => !_busy && !_loading && _installationPolicy?.ImportedPluginsEnabled == true && _preview is not null && _approve && !HasMissingConfiguration && UpdateMatches &&
        ((!IsToolchainProvider && (_preview.PluginKind != "Connector" || _preview.Accounts is not null)) || _businessId.HasValue) &&
        (_preview.WebAccess.Mode != PluginWebAccessMode.AllPublic || _acknowledgeAllPublic);
    private string InstallationScope(AgentInstallationResponse installation) => installation.InstallationScope == "System"
        ? L["Plugins.Import.SystemScope"]
        : BusinessContext.Businesses.FirstOrDefault(x => x.Id.ToString("D").Equals(installation.BusinessId, StringComparison.OrdinalIgnoreCase))?.Name ?? installation.BusinessId;
    private string InstallationState(AgentInstallationResponse installation) => installation.RemovalPending
        ? L["Plugins.Installed.RemovalPending"]
        : installation.RevisionStatus == "Staged" ? L["Plugins.Installed.UpdateReview"]
        : installation.IsEnabled ? L["Common.Enabled"] : L["Common.Disabled"];
    private void ChangeTab(int tab)
    {
        if (_busy) return;
        if (tab != 2) { _bundle = null; _updateTarget = null; _stagedUpdateId = null; }
        _activeTab = tab;
    }
    private void SetSource(bool upload) { ClearPreview(); _bundle = null; _uploadBundle = upload; }
    private void BackToSource() { ClearPreview(); _bundle = null; }
    private void Manage(AgentInstallationResponse installation)
    {
        if (installation.Accounts is not null) { Navigation.NavigateTo($"/settings/plugins/{installation.Id:D}"); return; }
        _managedId = _managedId == installation.Id ? null : installation.Id; _removePending = null;
    }
    private void ReviewPublishedUpdate(AgentInstallationResponse installation)
    {
        if (_busy || installation.PluginKind != "Service" || installation.Accounts is not null ||
            installation.RemovalPending || installation.RevisionStatus != "Active") return;
        ClearPreview(); _bundle = null; _stagedUpdateId = null;
        _updateTarget = installation; _uploadBundle = true;
        if (Guid.TryParse(installation.BusinessId, out var business)) _businessId = business;
        ChangeTab(2);
    }
    private void OpenInstalled(AgentInstallationResponse installation) { _installedSearch = string.Empty; _managedId = installation.Id; ChangeTab(0); }
    private void ConfigureFromReview(PluginProviderDeclarations.Requirement requirement) { ChangeTab(0); ConfigureDeclaredProvider(requirement); }
    private async Task ReviewCatalogAsync(PluginCatalogEntry entry)
    {
        if (_busy) return;
        ClearPreview(); _uploadBundle = false; _repositoryUrl = entry.RepositoryUrl; _commitSha = string.Empty;
        ChangeTab(2); await PreviewAsync();
    }
    private void ClearPreview()
    {
        _preview = null; _stagedUpdateId = null; _approve = false; _acknowledgeAllPublic = false; _configurationValues.Clear(); ResetProviderForm();
    }
    private void BundleSelected(InputFileChangeEventArgs args)
    {
        ClearPreview(); _message = null; _bundle = null;
        var file = args.File;
        if (!file.Name.EndsWith(".cspb", StringComparison.OrdinalIgnoreCase))
            ShowError(new InvalidOperationException(L["Plugins.Import.Error.BundleType"]));
        else if (file.Size > MaximumBundleBytes)
            ShowError(new InvalidOperationException(L["Plugins.Import.Error.BundleSize"]));
        else _bundle = file;
    }
    private readonly List<AgentInstallationResponse> _installations = [];
    private readonly List<PluginProviderProfileResponse> _providerProfiles = [];
    private IReadOnlyList<PluginConnectionDeclaration> RequiredConnections => _installations.Where(x => x.PluginKind == "Connector")
        .SelectMany(x => x.ConnectionDeclarations).Concat(_preview?.PluginKind == "Connector" ? _preview.Connections : [])
        .ToArray();
    private HashSet<string> RequiredProviderIds => RequiredConnections
        .Select(x => x.ProviderProfile).ToHashSet(StringComparer.Ordinal);
    private AgentImportPreviewResponse? _preview;
    private PluginInstallationPolicyResponse? _installationPolicy;
    private string _repositoryUrl = string.Empty;
    private string _commitSha = string.Empty;
    private string? _message;
    private bool _loading = true;
    private bool _busy;
    private bool _error;
    private bool _approve;
    private readonly Dictionary<string, string> _configurationValues = new(StringComparer.Ordinal);
    private string _providerId = string.Empty;
    private string _providerDisplayName = string.Empty;
    private string _providerAuthorizationEndpoint = string.Empty;
    private string _providerTokenEndpoint = string.Empty;
    private string _providerRevocationEndpoint = string.Empty;
    private string _providerClientId = string.Empty;
    private string _providerClientSecret = string.Empty;
    private bool _providerEnabled = true;
    private bool _editingManagedProfile;
    private bool _providerProfilesLoaded;
    private bool HasMissingConfiguration =>
        _preview?.ConfigurationFields
            .Where(configField => configField.Required && !configField.Secret && IsConfigurationFieldVisible(configField))
            .Any(configField => string.IsNullOrWhiteSpace(ConfigurationValue(configField.Key))) == true;

    protected override async Task OnInitializedAsync()
    {
        await BusinessContext.InitializeAsync();
        _businessId = BusinessContext.SelectedBusiness?.Id;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _installationPolicy = null;
        _providerProfilesLoaded = false;
        try
        {
            _installationPolicy = await PluginApi.GetInstallationPolicyAsync();
            _installations.Clear();
            _installations.AddRange((await PluginApi.ListAsync()).Where(x =>
                (x.PluginKind is "Service" or "Connector") && x.RevisionStatus != "Retired"));
            _providerProfiles.Clear();
            if (RequiredProviderIds.Count > 0)
                _providerProfiles.AddRange(await PluginApi.ListProviderProfilesAsync());
            _providerProfilesLoaded = true;
            if (!string.IsNullOrEmpty(_providerId) && !CanEditProvider(_providerId)) ResetProviderForm();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { _loading = false; }
    }

    private async Task SaveProviderAsync()
    {
        _busy = true;
        try
        {
            if (_editingManagedProfile || !CanEditProvider(_providerId))
                throw new InvalidOperationException(L["Plugins.Providers.Error.SelectDeclared"]);
            var declared = PluginProviderDeclarations.Collect(RequiredConnections).Single(x => x.Id == _providerId).Provider!;
            if (_providerAuthorizationEndpoint != declared.AuthorizationEndpoint || _providerTokenEndpoint != declared.TokenEndpoint ||
                _providerRevocationEndpoint != declared.RevocationEndpoint)
                throw new InvalidOperationException(L["Plugins.Providers.Error.DeclarationChanged"]);
            await PluginApi.SaveProviderProfileAsync(_providerId.Trim(), new(
                _providerDisplayName.Trim(), _providerAuthorizationEndpoint.Trim(), _providerTokenEndpoint.Trim(),
                string.IsNullOrWhiteSpace(_providerRevocationEndpoint) ? null : _providerRevocationEndpoint.Trim(),
                _providerClientId.Trim(), string.IsNullOrWhiteSpace(_providerClientSecret) ? null : _providerClientSecret,
                _providerEnabled));
            _providerClientSecret = string.Empty;
            ShowSuccess(L["Plugins.Providers.Saved"]);
            await LoadAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { _busy = false; }
    }

    private void EditProvider(PluginProviderProfileResponse profile)
    {
        if (!CanEditProvider(profile.Id)) return;
        _providerId = profile.Id; _providerDisplayName = profile.DisplayName;
        _providerAuthorizationEndpoint = profile.AuthorizationEndpoint; _providerTokenEndpoint = profile.TokenEndpoint;
        _providerRevocationEndpoint = profile.RevocationEndpoint ?? string.Empty; _providerClientId = profile.ClientId;
        _providerClientSecret = string.Empty; _providerEnabled = profile.IsEnabled;
        _editingManagedProfile = profile.ManagedByDeployment;
    }

    private bool CanEditProvider(string id)
    {
        var requirement = PluginProviderDeclarations.Collect(RequiredConnections).SingleOrDefault(x => x.Id == id);
        var profile = _providerProfiles.SingleOrDefault(x => x.Id == id);
        return _providerProfilesLoaded && requirement?.CanConfigure == true && (profile is null || requirement.Matches(profile));
    }

    private void ConfigureDeclaredProvider(PluginProviderDeclarations.Requirement requested)
    {
        // Re-resolve from current declarations; an old UI callback must not configure a removed preview.
        var requirement = PluginProviderDeclarations.Collect(RequiredConnections).SingleOrDefault(x => x.Id == requested.Id);
        if (requirement?.CanConfigure != true || !CanEditProvider(requirement.Id)) return;
        var existing = _providerProfiles.SingleOrDefault(x => x.Id == requirement.Id);
        if (existing is not null) { EditProvider(existing); return; }
        ResetProviderForm();
        _providerId = requirement.Id; _providerDisplayName = requirement.Provider!.DisplayName;
        _providerAuthorizationEndpoint = requirement.Provider.AuthorizationEndpoint;
        _providerTokenEndpoint = requirement.Provider.TokenEndpoint;
        _providerRevocationEndpoint = requirement.Provider.RevocationEndpoint;
    }

    private async Task DeleteProviderAsync(PluginProviderProfileResponse profile)
    {
        _busy = true;
        try { await PluginApi.DeleteProviderProfileAsync(profile.Id); ResetProviderForm(); ShowSuccess(L["Plugins.Providers.Deleted"]); await LoadAsync(); }
        catch (Exception exception) { ShowError(exception); }
        finally { _busy = false; }
    }

    private void ResetProviderForm()
    {
        _providerId = string.Empty; _providerDisplayName = string.Empty;
        _providerAuthorizationEndpoint = string.Empty;
        _providerTokenEndpoint = string.Empty;
        _providerRevocationEndpoint = string.Empty;
        _providerClientId = string.Empty; _providerClientSecret = string.Empty; _providerEnabled = true;
        _editingManagedProfile = false;
    }

    private async Task PreviewAsync()
    {
        if (_busy) return;
        ClearPreview(); _busy = true; _message = null;
        _providerProfilesLoaded = false;
        try
        {
            AgentImportPreviewResponse preview;
            if (_uploadBundle)
            {
                if (_bundle is null) return;
                await using var stream = _bundle.OpenReadStream(MaximumBundleBytes);
                preview = await PluginApi.PreviewBundleAsync(stream, _bundle.Name);
            }
            else preview = await PluginApi.PreviewAsync(new(_repositoryUrl.Trim(), string.IsNullOrWhiteSpace(_commitSha) ? null : _commitSha.Trim()));
            if (preview.PluginKind is not ("Service" or "Connector"))
                throw new InvalidOperationException(L["Plugins.Import.Error.Agent"]);
            _preview = preview;
            _providerProfiles.Clear();
            if (RequiredProviderIds.Count > 0)
                _providerProfiles.AddRange(await PluginApi.ListProviderProfilesAsync());
            _providerProfilesLoaded = true;
            foreach (var configField in _preview.ConfigurationFields.Where(configField => !configField.Secret))
                _configurationValues[configField.Key] = string.Empty;
        }
        catch (Exception exception) { ShowError(exception); }
        finally { _busy = false; }
    }

    private async Task InstallAsync()
    {
        if (!CanInstall || _preview is null) return;
        _busy = true;
        try
        {
            var service = _preview.PluginKind == "Service";
            var brokerOnly = _preview.PluginKind == "Connector";
            var enterprise = service && !IsToolchainProvider || _preview.Accounts is not null;
            var bundle = _preview.PackageSourceMode == "PluginBundle";
            var memoryMb = brokerOnly ? 0 : string.Equals(_preview.PublisherId, "com.csweet", StringComparison.Ordinal) ? 1024 : 512;
            var request = new InstallAgentRequest(
                enterprise ? "enterprise" : _businessId!.Value.ToString("D"),
                brokerOnly ? string.Empty : IsToolchainProvider ? "OnDemand" : service ? "AlwaysOn" : (_preview.DefaultActivationMode ?? "OnDemand"),
                brokerOnly ? 0 : _preview.DefaultTickFrequencySeconds ?? 300,
                brokerOnly ? string.Empty : "Skip",
                _preview.Capabilities,
                _preview.RequestedSubscriptions,
                _preview.RequestedPublications,
                _preview.RequestedPermissions,
                _preview.RequestedNetworkAccess,
                brokerOnly ? 0 : 300, memoryMb, brokerOnly ? 0 : 50)
            {
                PluginScope = enterprise ? "System" : "Organization",
                ExpectedPackageDigest = _updateTarget is null ? null : _preview.BundleDigest,
                AllPublicWebAccessAcknowledged = _acknowledgeAllPublic,
                GrantedRequestedCapabilities = _preview.RequestedCapabilities,
                ConfigurationSettings = _configurationValues
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => JsonSerializer.SerializeToElement(pair.Value.Trim()),
                        StringComparer.Ordinal)
            };
            AgentInstallationResponse installed;
            if (_updateTarget is not null)
            {
                if (!_stagedUpdateId.HasValue)
                    _stagedUpdateId = (await PluginApi.StageUpdateAsync(_updateTarget.Id, _preview.ImportId)).Id;
                installed = await PluginApi.ApproveUpdateAsync(_stagedUpdateId.Value, request);
            }
            else installed = await PluginApi.InstallAsync(_preview.ImportId, request);
            _updateTarget = null; _stagedUpdateId = null;
            if (_preview.Accounts is not null) { Navigation.NavigateTo($"/settings/plugins/{installed.Id:D}"); return; }
            _managedId = installed.Id;
            ClearPreview(); _bundle = null; _repositoryUrl = string.Empty; _commitSha = string.Empty;
            _activeTab = 0;
            ShowSuccess(L[bundle ? "Plugins.Import.BundleInstalled" : "Plugins.Import.Installed"]);
            await LoadAsync();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { _busy = false; }
    }

    private async Task ToggleAsync(AgentInstallationResponse installation)
    {
        _busy = true;
        try { await PluginApi.SetEnabledAsync(installation.Id, !installation.IsEnabled); ShowSuccess(L["Plugins.Installed.StateUpdated"]); await LoadAsync(); }
        catch (Exception exception) { ShowError(exception); }
        finally { _busy = false; }
    }

    private async Task RemoveAsync(AgentInstallationResponse installation)
    {
        _busy = true;
        try { await PluginApi.RemoveAsync(installation.Id); _removePending = null; _managedId = null; ShowSuccess(L["Plugins.Installed.Removed"]); await LoadAsync(); }
        catch (Exception exception) { ShowError(exception); }
        finally { _busy = false; }
    }

    private string Format(IReadOnlyList<string> values) => values.Count == 0 ? L["Common.None"] : string.Join(", ", values.Select(value => CapabilityDisplayNames.For(L, value)));
    private string FieldLabel(PluginConfigurationField configField) =>
        configField.Required ? L.Format("Plugins.Import.RequiredField", new { label = configField.Label }) : configField.Label;
    private string ConfigurationValue(string key) => _configurationValues.GetValueOrDefault(key) ?? string.Empty;
    private void SetConfigurationValue(string key, string value) => _configurationValues[key] = value;
    private bool IsConfigurationFieldVisible(PluginConfigurationField field) =>
        AgentConfigurationVisibility.IsVisible(field, _configurationValues);
    private void ShowSuccess(string message) { _message = message; _error = false; }
    private void ShowError(Exception exception) { _message = exception.Message; _error = true; }
}
