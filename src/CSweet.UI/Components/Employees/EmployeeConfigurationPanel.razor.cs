using System.Net;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Llm;
using CSweet.UI.Services;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeConfigurationPanel : IDisposable
{
    [Parameter] public Guid OrganizationId { get; set; }
    [Parameter] public Guid EmployeeId { get; set; }
    [Parameter] public EventCallback<AgentConfigurationView> ConfigurationChanged { get; set; }
    [Inject] private IAgentApiClient AgentApi { get; set; } = default!;
    [Inject] private ILlmProviderApiClient LlmProviderApi { get; set; } = default!;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private AgentConfigurationSchemaResponse? _configurationSchema;
    private AgentConfigurationView? _configurationView;
    private IReadOnlyList<LlmProviderProfileResponse> _providerProfiles = [];
    private readonly Dictionary<string, object?> _configurationValues = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, IReadOnlyList<string>> _providerModels = [];
    private readonly HashSet<Guid> _loadingProviderModels = [];
    private readonly Dictionary<Guid, string> _providerModelErrors = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private MudForm? _form;
    private bool _loadingConfiguration = true, _savingConfiguration, _hasChanges, _disposed;
    private string? _configurationError, _configurationMessage, _providerError;
    private (Guid, Guid)? _loaded;
    private string SyncLabel => _configurationView?.SynchronizationStatus switch
    {
        "Current" => "Up to date", "Refreshing" => "Applying changes",
        "PendingNextStart" => "Applies on next start", "Failed" => "Needs attention",
        _ => _configurationView?.SynchronizationStatus ?? "Unknown"
    };

    protected override async Task OnParametersSetAsync()
    {
        if (_loaded == (OrganizationId, EmployeeId)) return;
        _loaded = (OrganizationId, EmployeeId);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loadingConfiguration = true;
        _configurationError = null; _configurationMessage = null; _providerError = null;
        _configurationSchema = null; _configurationView = null; _hasChanges = false;
        _configurationValues.Clear(); _providerModels.Clear(); _providerModelErrors.Clear();
        _providerProfiles = [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var view = await AgentApi.GetEmployeeConfigurationAsync(OrganizationId, EmployeeId, timeout.Token);
            if (_disposed) return;
            Accept(view);
            if (view.Fields.Any(f => f.Type is AgentConfigurationFieldTypes.LlmProvider or AgentConfigurationFieldTypes.LlmModel))
            {
                try
                {
                    _providerProfiles = await LlmProviderApi.ListAsync(timeout.Token);
                    await LoadConfiguredProviderModelsAsync(_configurationSchema!, timeout.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch { _providerError = "Provider choices could not be loaded. Your saved settings are unchanged. Reload to try again."; }
            }
        }
        catch (OperationCanceledException) { if (!_disposed) _configurationError = "Loading took too long. Please try again."; }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Forbidden) { _configurationError = "You don't have permission to configure this employee."; }
        catch (Exception e) { _configurationError = e.Message; }
        finally { _loadingConfiguration = false; }
    }

    private void Accept(AgentConfigurationView view)
    {
        _configurationView = view;
        _configurationSchema = ToSchema(view);
        HydrateConfigurationValues(_configurationSchema);
        _hasChanges = false;
    }

    private async Task SaveConfigurationAsync()
    {
        if (_savingConfiguration || _loadingProviderModels.Count > 0 || !_hasChanges || _configurationView is null || _configurationSchema is null) return;
        if (_form is not null) { await _form.ValidateAsync(); if (!_form.IsValid) return; }
        var settings = _configurationSchema.Fields.ToDictionary(f => f.Key,
            f => JsonSerializer.SerializeToElement(_configurationValues.GetValueOrDefault(f.Key), SerializerOptions), StringComparer.Ordinal);
        await MutateConfigurationAsync(() => AgentApi.UpdateEmployeeConfigurationAsync(OrganizationId, EmployeeId,
            new PutAgentConfigurationOverridesRequest(settings, _configurationView.ExpectedRevision), _disposeCts.Token),
            "Settings saved. Running agents refresh automatically; stopped agents use them on their next start.");
    }

    private Task RestoreConfigurationKeyAsync(string key) => _configurationView is null || _hasChanges
        ? Task.CompletedTask
        : MutateConfigurationAsync(() => AgentApi.RestoreEmployeeConfigurationKeyAsync(OrganizationId, EmployeeId,
            key, _configurationView.ExpectedRevision, _disposeCts.Token), "Default restored for this setting.");

    private Task RestoreAllConfigurationAsync() => _configurationView is null || _hasChanges
        ? Task.CompletedTask
        : MutateConfigurationAsync(() => AgentApi.RestoreAllEmployeeConfigurationAsync(OrganizationId, EmployeeId,
            _configurationView.ExpectedRevision, _disposeCts.Token), "All settings now use the agent defaults.");

    private async Task MutateConfigurationAsync(Func<Task<AgentConfigurationView>> action, string message)
    {
        if (_savingConfiguration) return;
        _savingConfiguration = true; _configurationError = null; _configurationMessage = null;
        try
        {
            var view = await action();
            if (_disposed) return;
            Accept(view);
            await LoadConfiguredProviderModelsAsync(_configurationSchema!, _disposeCts.Token);
            _configurationMessage = message;
            await ConfigurationChanged.InvokeAsync(view);
        }
        catch (ApiClientException e) when (e.StatusCode == HttpStatusCode.Conflict)
        { _configurationError = "These settings changed elsewhere. Your edits are still here. Reload saved values before making another change."; }
        catch (OperationCanceledException) { if (!_disposed) _configurationError = "The request was interrupted. Reload saved values to check its status."; }
        catch (Exception e) { _configurationError = e.Message; }
        finally { _savingConfiguration = false; }
    }

    private void DiscardChanges()
    {
        if (_configurationView is null || _savingConfiguration) return;
        Accept(_configurationView); _configurationError = null; _configurationMessage = null;
    }

    private bool IsConfigurationOverridden(string key) =>
        _configurationView?.OverriddenKeys.Contains(key, StringComparer.Ordinal) == true;

    private static AgentConfigurationSchemaResponse ToSchema(AgentConfigurationView view) => new(
        view.AgentId, view.AgentVersion, view.SchemaVersion, view.Fields, view.EffectiveValues);

    private void HydrateConfigurationValues(AgentConfigurationSchemaResponse schema)
    {
        _configurationValues.Clear();
        foreach (var field in schema.Fields)
        {
            _configurationValues[field.Key] = schema.Settings.TryGetValue(field.Key, out var value)
                ? field.Type switch
                {
                    AgentConfigurationFieldTypes.Boolean => value.ValueKind == JsonValueKind.True,
                    AgentConfigurationFieldTypes.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) => number,
                    _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null
                }
                : null;
        }
    }

    private string ConfigurationString(string key) => _configurationValues.GetValueOrDefault(key)?.ToString() ?? string.Empty;
    private bool ConfigurationBoolean(string key) => _configurationValues.GetValueOrDefault(key) is true;
    private decimal? ConfigurationNumber(string key) => _configurationValues.GetValueOrDefault(key) as decimal?;

    private bool IsConfigurationFieldVisible(AgentConfigurationField field) =>
        AgentConfigurationVisibility.IsVisible(field, _configurationValues);
    private void SetConfigurationValue(string key, object? value)
    {
        _configurationValues[key] = value;
        _hasChanges = true;
        _configurationMessage = null;
    }

    private async Task SetProviderValueAsync(string key, string value)
    {
        SetConfigurationValue(key, value);
        if (_configurationSchema is null)
        {
            return;
        }

        var provider = FindProvider(value);
        if (provider is not null)
        {
            await LoadProviderModelsAsync(provider.Id, _disposeCts.Token);
        }

        if (ConfigurationString(key) != value || _disposed) return;
        foreach (var modelField in _configurationSchema.Fields.Where(field =>
            field.Type == AgentConfigurationFieldTypes.LlmModel &&
            string.Equals(ConfigurationProviderFieldKey(field), key, StringComparison.Ordinal)))
        {
            var models = provider is null ? [] : ModelOptions(modelField);
            _configurationValues[modelField.Key] = provider is null
                ? string.Empty
                : models.Contains(provider.DefaultChatModel, StringComparer.Ordinal)
                    ? provider.DefaultChatModel
                    : models.FirstOrDefault() ?? string.Empty;
        }
    }

    private bool IsModelPickerDisabled(AgentConfigurationField field) =>
        ConfigurationProvider(field) is not { } provider ||
        _loadingProviderModels.Contains(provider.Id);

    private string ModelPlaceholder(AgentConfigurationField field) =>
        ConfigurationProvider(field) is not { } provider
            ? "Select a provider first"
            : _loadingProviderModels.Contains(provider.Id)
                ? "Loading models..."
                : "Select a model";

    private IReadOnlyList<string> ModelOptions(AgentConfigurationField field)
    {
        var provider = ConfigurationProvider(field);
        if (provider is null)
        {
            return [];
        }

        var models = _providerModels.GetValueOrDefault(provider.Id) ?? [];
        return models
            .Append(provider.DefaultChatModel)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private bool SelectedModelMissing(AgentConfigurationField field)
    {
        var selected = ConfigurationString(field.Key);
        return !string.IsNullOrWhiteSpace(selected) &&
            !ModelOptions(field).Contains(selected, StringComparer.Ordinal);
    }

    private LlmProviderProfileResponse? ConfigurationProvider(AgentConfigurationField field) =>
        FindProvider(ConfigurationString(ConfigurationProviderFieldKey(field)));

    private string ConfigurationProviderFieldKey(AgentConfigurationField modelField) =>
        !string.IsNullOrWhiteSpace(modelField.DependsOnFieldKey)
            ? modelField.DependsOnFieldKey
            : _configurationSchema?.Fields.FirstOrDefault(field =>
                field.Type == AgentConfigurationFieldTypes.LlmProvider)?.Key ?? string.Empty;

    private LlmProviderProfileResponse? FindProvider(string providerId) =>
        Guid.TryParse(providerId, out var id)
            ? _providerProfiles.FirstOrDefault(provider => provider.Id == id && provider.IsEnabled)
            : null;

    private async Task LoadConfiguredProviderModelsAsync(
        AgentConfigurationSchemaResponse schema,
        CancellationToken cancellationToken)
    {
        var providerIds = schema.Fields
            .Where(field => field.Type == AgentConfigurationFieldTypes.LlmProvider)
            .Select(field => ConfigurationString(field.Key))
            .Where(value => Guid.TryParse(value, out _))
            .Select(Guid.Parse)
            .Distinct()
            .ToList();

        await Task.WhenAll(providerIds.Select(providerId =>
            LoadProviderModelsAsync(providerId, cancellationToken)));

    }

    private async Task LoadProviderModelsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        if ((_providerModels.ContainsKey(providerId) && !_providerModelErrors.ContainsKey(providerId)) ||
            !_loadingProviderModels.Add(providerId))
        {
            return;
        }

        _providerModelErrors.Remove(providerId);
        try
        {
            var result = await LlmProviderApi.GetModelCatalogAsync(providerId, cancellationToken);
            if (!result.Succeeded)
            {
                _providerModelErrors[providerId] =
                    result.Message ?? "Models could not be loaded from this provider.";
                _providerModels[providerId] = [];
                return;
            }

            _providerModels[providerId] = result.Models
                .Select(model => model.Id)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _providerModelErrors[providerId] = exception.Message;
            _providerModels[providerId] = [];
        }
        finally
        {
            _loadingProviderModels.Remove(providerId);
        }
    }

    private string? ModelCatalogError(AgentConfigurationField providerField)
    {
        var provider = FindProvider(ConfigurationString(providerField.Key));
        return provider is not null &&
               _providerModelErrors.TryGetValue(provider.Id, out var error)
            ? error
            : null;
    }

    public void Dispose() { _disposed = true; _disposeCts.Cancel(); _disposeCts.Dispose(); }
}
